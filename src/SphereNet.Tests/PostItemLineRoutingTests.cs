using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A line that follows an ITEM= belongs to the item that ITEM= made.
///
/// Upstream reads a creature's script holding the last-created item in pItem and sends
/// every following line to it, falling back to the creature only when there is none -
/// the comment says it outright: "I'm setting an attribute to myself, not the item
/// (e.g. @Create trigger)" (CChar.cpp:1441-1449).
///
/// Only COLOR was routed that way here. The live pack writes 1,051 lines after an
/// ITEM= inside a CHARDEF and every one of them is an item property; 109 are ADDSPELL,
/// filling a monster's spellbook right after the ITEM=i_spellbook that made it. Those
/// went to the creature, which does not answer ADDSPELL, and vanished - every lich and
/// mage in the pack carried an empty book.
///
/// ADDSPELL itself only took a number, while the packs write a defname, so it would
/// have added nothing even once it arrived.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PostItemLineRoutingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_pil_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Spawn a creature whose @Create runs the given lines, and hand it back
    /// with its pack.</summary>
    private SphereNet.Game.Objects.Characters.Character Spawn(params string[] createLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "p.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 0efa]", "DEFNAME=i_pil_book", "NAME=spellbook", "TYPE=t_spellbook",
            "[CHARDEF 0013]", "DEFNAME=c_pil_mage", "NAME=mage",
            "ON=@Create",
        };
        lines.AddRange(createLines);
        File.WriteAllLines(file, lines);

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            dispatcher.FireCharTrigger(npc, CharTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var npc = spawn.SpawnSpecific(0x13);
        Assert.NotNull(npc);
        return npc!;
    }

    /// <summary>Whether the book holds this spell. Upstream splits the bits across two
    /// words - 1..31 in MORE1, 32..63 in MORE2 (CItem::IsSpellInBook).</summary>
    private static bool BookHas(Item book, SpellType spell)
    {
        // Spell n sits at bit n-1 of the magery book (AddSpellbookSpell, CItem.cpp:4485).
        int bit = (int)spell - 1;
        string key = bit < 32 ? "MORE1" : "MORE2";
        Assert.True(book.TryGetProperty(key, out string raw));
        uint bits = Convert.ToUInt32(raw, 16);
        return (bits & (1u << (bit < 32 ? bit : bit - 32))) != 0;
    }

    private static Item BookOf(SphereNet.Game.Objects.Characters.Character ch)
    {
        // Wherever the creature ended up putting it - equipped or in the pack.
        var book = ch.Backpack?.Contents.FirstOrDefault(i => i.BaseId == 0x0EFA);
        if (book == null)
            for (int layer = 0; layer < 32 && book == null; layer++)
            {
                var worn = ch.GetEquippedItem((Layer)layer);
                if (worn?.BaseId == 0x0EFA) book = worn;
            }
        Assert.NotNull(book);
        return book!;
    }

    /// <summary>The 109-line case, end to end: the book the creature was given holds
    /// the spells the lines after it named.</summary>
    [Fact]
    public void ASpellWrittenAfterAnItemGoesIntoThatItem()
    {
        var mage = Spawn("ITEM=i_pil_book", "ADDSPELL=s_paralyze", "ADDSPELL=s_fireball");
        var book = BookOf(mage);

        Assert.True(BookHas(book, SpellType.Paralyze));
        Assert.True(BookHas(book, SpellType.Fireball));
    }

    /// <summary>A defname is what the packs write; a number still works.</summary>
    [Fact]
    public void ASpellCanBeNamedOrNumbered()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        world.PlaceItem(book, new Point3D(100, 100, 0, 0));

        Assert.True(book.TrySetProperty("ADDSPELL", ((int)SpellType.Paralyze).ToString()));
        Assert.True(BookHas(book, SpellType.Paralyze));

        Assert.True(book.TrySetProperty("ADDSPELL", "s_fireball"));
        Assert.True(BookHas(book, SpellType.Fireball));
    }

    /// <summary>A key the creature answers still reaches the creature - the item gets
    /// only what the creature refuses, so nothing that worked is taken away.</summary>
    [Fact]
    public void ACreatureKeepsItsOwnKeys()
    {
        var mage = Spawn("ITEM=i_pil_book", "STR=77", "ADDSPELL=s_paralyze");

        Assert.Equal(77, mage.Str);
        Assert.True(BookHas(BookOf(mage), SpellType.Paralyze));
    }

    /// <summary>COLOR is the one the creature would otherwise swallow: it tinted the
    /// creature's body instead of the hair or book the line was written for.</summary>
    [Fact]
    public void ColourStillTintsTheItemItFollows()
    {
        var mage = Spawn("ITEM=i_pil_book", "COLOR=0481");
        Assert.Equal(0x481, BookOf(mage).Hue.Value);
    }
}
