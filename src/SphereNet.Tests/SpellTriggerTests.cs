using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired spell triggers @SpellSelect and @SpellBook. @SpellSelect
// fires at the start of a cast request — before @SpellCast and the mana/skill
// checks — so a script can cancel early (RETURN 1). @SpellBook fires when a
// spellbook is opened (RETURN 1 keeps it shut).
public class SpellTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient client, Character player) NewClient(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1801);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.MaxMana = player.Mana = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (client, player);
    }

    private static SpellEngine MakeSpellEngine(GameWorld world)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef { Id = SpellType.Polymorph, ManaCost = 0, CastTimeBase = 1 });
        return new SpellEngine(world, registry);
    }

    [Fact]
    public void SpellSelect_OnCast_FiresBeforeSpellCast()
    {
        var world = CreateWorld();
        var (client, _) = NewClient(world);
        var d = new TriggerDispatcher();
        var order = new List<string>();
        d.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, a) => { order.Add($"select:{a.N1}"); return TriggerResult.Default; });
        d.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, a) => { order.Add($"cast:{a.N1}"); return TriggerResult.Default; });
        client.SetEngines(spellEngine: MakeSpellEngine(world), triggerDispatcher: d);

        client.HandleCastSpell(SpellType.Polymorph, 0);

        Assert.True(order.Count >= 2);
        Assert.Equal($"select:{(int)SpellType.Polymorph}", order[0]);
        Assert.Equal($"cast:{(int)SpellType.Polymorph}", order[1]);
    }

    [Fact]
    public void SpellSelect_ReturnTrue_CancelsCastBeforeSpellCast()
    {
        var world = CreateWorld();
        var (client, _) = NewClient(world);
        var d = new TriggerDispatcher();
        bool castFired = false;
        d.RegisterCharEvent("EVENTSPLAYER", "SpellSelect", (_, _) => TriggerResult.True);
        d.RegisterCharEvent("EVENTSPLAYER", "SpellCast", (_, _) => { castFired = true; return TriggerResult.Default; });
        client.SetEngines(spellEngine: MakeSpellEngine(world), triggerDispatcher: d);

        client.HandleCastSpell(SpellType.Polymorph, 0);

        Assert.False(castFired); // @SpellSelect cancelled before @SpellCast
    }

    [Fact]
    public void SpellBook_OnDoubleClick_FiresSpellBookTrigger()
    {
        var world = CreateWorld();
        var (client, player) = NewClient(world);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.BaseId = 0x0EFA;
        pack.AddItem(book);

        var d = new TriggerDispatcher();
        bool fired = false;
        d.RegisterCharEvent("EVENTSPLAYER", "SpellBook", (_, _) => { fired = true; return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: d);

        client.HandleDoubleClick(book.Uid.Value);

        Assert.True(fired);
    }
}
