using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
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
/// A component property set on an INSTANCE reaches the engine that reads it.
///
/// Upstream keeps these on entity components (the ADDPROP tables under src/tables), so
/// every one of them is settable on an item or a character, not only on a definition.
/// Here the definition side already worked - an ITEMDEF key the parser does not name
/// lands in the def's tags, and the combat aggregations read those - while
/// TrySetProperty had no such catch-all and refused the instance write outright.
///
/// That is exactly what an item's @Create block does. The shipped packs write
/// RESPHYSICAL, RESFIRE, RESCOLD, RESPOISON and RESENERGY inside @Create about 1,100
/// times between them, and every one was dropped on the floor: the armour came out
/// with no resists at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ComponentPropertyInstanceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_cp_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Build an armour whose ITEMDEF sets the given lines in @Create, equip it,
    /// and hand back the wearer and the worn item.</summary>
    private (SphereNet.Game.Objects.Characters.Character Ch, Item Worn) Wear(params string[] createLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 013bb]", "DEFNAME=i_probe_mail", "TYPE=t_armor", "LAYER=13",
            "ON=@Create",
        };
        lines.AddRange(createLines);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
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
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it, ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });

        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var worn = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(worn, 0x13BB);
        Assert.True(ch.Equip(worn, Layer.Ring));
        return (ch, worn);
    }

    /// <summary>The 1,100-line case, end to end: @Create writes a resist and the
    /// wearer has it.</summary>
    [Fact]
    public void AResistSetInCreateReachesTheWearer()
    {
        var (ch, worn) = Wear("RESPHYSICAL=2", "RESFIRE=3");

        Assert.True(worn.TryGetProperty("RESPHYSICAL", out string phys));
        Assert.Equal("2", phys);
        Assert.Equal(2, CombatEngine.EffectiveResist(ch, DamageType.Physical));
        Assert.Equal(3, CombatEngine.EffectiveResist(ch, DamageType.Fire));
    }

    /// <summary>An instance value wins over the definition's, which is what an
    /// @Create line rolling a random value depends on.</summary>
    [Fact]
    public void TheInstanceValueWinsOverTheDefinition()
    {
        var (_, worn) = Wear("RESFIRE=9");
        Assert.True(worn.TrySetProperty("RESFIRE", "4"));
        Assert.True(worn.TryGetProperty("RESFIRE", out string v));
        Assert.Equal("4", v);
    }

    /// <summary>Unset reads 0, the default upstream answers - not an empty string,
    /// which a script comparing it would read as a missing key.</summary>
    [Fact]
    public void AnUnsetOneReadsZero()
    {
        var (_, worn) = Wear("RESFIRE=3");
        Assert.True(worn.TryGetProperty("RESCOLD", out string cold));
        Assert.Equal("0", cold);
    }

    /// <summary>The names that are NOT in these tables keep the code that owns them.
    /// A character's base resists and LUCK are real fields here, and shadowing them
    /// with a tag broke eight tests when the first cut of this list was too wide.</summary>
    [Fact]
    public void TheNamesOwnedByRealFieldsAreLeftAlone()
    {
        Assert.False(ComponentProperties.IsCharProperty("RESFIRE"));
        Assert.False(ComponentProperties.IsCharProperty("LUCK"));
        Assert.False(ComponentProperties.IsCharProperty("RESFIREMAX"));
        Assert.False(ComponentProperties.IsItemProperty("RANGEH"));
        Assert.False(ComponentProperties.IsItemProperty("INCREASEDAM"));   // AosEquipProperties
        Assert.False(ComponentProperties.IsItemProperty("FASTERCASTING")); // SpellCastingProperties

        // And the ones it does own.
        Assert.True(ComponentProperties.IsItemProperty("RESPHYSICAL"));
        Assert.True(ComponentProperties.IsItemProperty("NIGHTSIGHT"));
        Assert.True(ComponentProperties.IsCharProperty("SOULCHARGE"));
    }

    /// <summary>They are tags, so they survive a save like every other tag. An item
    /// that rolled its resists at creation must still have them after a restart.</summary>
    [Fact]
    public void TheyOutliveASave()
    {
        var (_, worn) = Wear("RESPHYSICAL=2", "RESFIRE=3", "NIGHTSIGHT=1");
        string dir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(dir);

        var live = ObjBase.ResolveWorld!.Invoke()!;
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(live, dir);

        var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, dir);

        var back = reloaded.FindItem(worn.Uid);
        Assert.NotNull(back);
        Assert.True(back!.TryGetProperty("RESPHYSICAL", out string phys));
        Assert.Equal("2", phys);
        Assert.True(back.TryGetProperty("NIGHTSIGHT", out string ns));
        Assert.Equal("1", ns);
    }
}
