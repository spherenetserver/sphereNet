using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NUKE / NUKECHAR / NUDGE keep their raw argument and pick a two-corner
/// rectangle (Source-X CClient CV_NUKE.. and OnTarg_Tile); spell memories sit on
/// the Source-X spell layers with a real UID, replace each other per layer
/// under MAGICF_STACKSTATS, and a SPELL LAYER with no native case still equips
/// a timed memory (Spell_Effect_Create / OnSpellEffect default).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AreaVerbAndSpellLayerParityTests
{
    private static (GameWorld World, Character Gm) CreateWorld()
    {
        var w = TestHarness.CreateWorld();
        var gm = w.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        gm.BodyId = 0x190;
        gm.Str = gm.Dex = gm.Int = 100;
        w.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        return (w, gm);
    }

    private static (GameClient Client, CommandHandler Commands) CreateClient(GameWorld w, Character gm)
    {
        var c = new GameClient(new NetState(NullLogger.Instance), w,
            new AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        TestHarness.AttachCharacter(c, gm);
        var commands = new CommandHandler();
        commands.RegisterDefaults(w);
        commands.ScriptFallbackExecutor = (_, _) => false;
        // Program.EngineWiring forwards the raw argument the same way.
        commands.OnAreaTargetRequested += (_, verb, args) => c.BeginAreaTarget(verb, args);
        return (c, commands);
    }

    private static Item PlaceItem(GameWorld w, short x, short y)
    {
        var item = w.CreateItem();
        item.BaseId = 0x0EED;
        w.PlaceItem(item, new Point3D(x, y, 0, 0));
        return item;
    }

    private static void Pick(GameClient c, short x, short y) =>
        c.HandleTargetResponse(1, c.ActiveTargetCursorId, 0, x, y, 0, 0);

    [Fact]
    public void Nudge_ShiftsByTheCommandArgument_OverATwoCornerArea()
    {
        var (w, gm) = CreateWorld();
        var (c, commands) = CreateClient(w, gm);
        var item = PlaceItem(w, 110, 110);

        commands.TryExecute(gm, "NUDGE 1 2 3");
        Pick(c, 108, 108);
        // The first corner only arms the second pick.
        Assert.Equal(new Point3D(110, 110, 0, 0), item.Position);
        Pick(c, 112, 112);

        Assert.Equal(111, item.X);
        Assert.Equal(112, item.Y);
        Assert.Equal(3, item.Z);
    }

    [Fact]
    public void Nuke_WithVerbLine_AppliesTheVerbInsteadOfDeleting()
    {
        var (w, gm) = CreateWorld();
        var (c, commands) = CreateClient(w, gm);
        var item = PlaceItem(w, 110, 110);

        commands.TryExecute(gm, "NUKE COLOR 0455");
        Pick(c, 108, 108);
        Pick(c, 112, 112);

        Assert.False(item.IsDeleted);
        Assert.Equal(0x455, (ushort)item.Hue);
    }

    [Fact]
    public void Nuke_RectangleExcludesItsRightAndBottomEdge()
    {
        var (w, gm) = CreateWorld();
        var (c, commands) = CreateClient(w, gm);
        var inside = PlaceItem(w, 108, 108);
        var edge = PlaceItem(w, 112, 110);

        commands.TryExecute(gm, "NUKE");
        Pick(c, 108, 108);
        Pick(c, 112, 112);

        Assert.True(inside.IsDeleted);   // CRect::IsInside2d: left/top inclusive
        Assert.False(edge.IsDeleted);    // right/bottom exclusive
    }

    [Fact]
    public void AreaVerb_SamePointTwice_DoesNotApply()
    {
        var (w, gm) = CreateWorld();
        var (c, commands) = CreateClient(w, gm);
        var item = PlaceItem(w, 110, 110);

        commands.TryExecute(gm, "NUKE");
        Pick(c, 110, 110);
        Pick(c, 110, 110);

        Assert.False(item.IsDeleted);
        Assert.True(c.ActiveTargetCursorId != 0); // still waiting for a real second corner
    }

    [Fact]
    public void Nudge_WithoutArguments_OpensNoCursor()
    {
        var (w, gm) = CreateWorld();
        var (c, commands) = CreateClient(w, gm);

        commands.TryExecute(gm, "NUDGE");

        Assert.Equal(0u, c.ActiveTargetCursorId);
    }

    private static SpellEngine Engine(GameWorld w, params SpellDef[] defs)
    {
        var registry = new SpellRegistry();
        foreach (var d in defs)
            registry.Register(d);
        return new SpellEngine(w, registry);
    }

    private static SpellDef StatDef(SpellType id) => new()
    {
        Id = id, Name = id.ToString(), Flags = SpellFlag.Bless | SpellFlag.TargChar,
        DurationBase = 600, EffectBase = 10,
    };

    [Fact]
    public void StatSpellMemory_IsOnLayerSpellStats_WithAResolvableUid()
    {
        Character.MagicFlags = 0;
        var (w, gm) = CreateWorld();
        var engine = Engine(w, StatDef(SpellType.Bless));

        engine.ApplyDirectEffect(gm, gm, SpellType.Bless, 500);

        var mem = gm.Memories.Single(m => m.ItemType == ItemType.Spell);
        Assert.Equal(SpellLayers.Stats, mem.EquipLayer);
        Assert.True(mem.Uid.IsValid);
        Assert.Same(mem, w.FindObject(mem.Uid));
        Assert.True(gm.TryGetProperty("FINDLAYER(32)", out var value));
        Assert.Equal($"0{mem.Uid.Value:X8}", value);
    }

    [Fact]
    public void RemovingTheMemoryByUid_EndsTheEffect()
    {
        Character.MagicFlags = 0;
        var (w, gm) = CreateWorld();
        var engine = Engine(w, StatDef(SpellType.Strength));

        engine.ApplyDirectEffect(gm, gm, SpellType.Strength, 500);
        Assert.Equal(110, gm.Str);
        var mem = gm.FindLayer(SpellLayers.Stats)!;

        w.DeleteObject(mem);

        Assert.Equal(100, gm.Str);
        Assert.DoesNotContain(gm.Memories, m => m.ItemType == ItemType.Spell);
        Assert.Null(w.FindObject(mem.Uid));
    }

    [Fact]
    public void StackStatsOff_ADifferentStatSpellReplacesThePreviousOne()
    {
        Character.MagicFlags = 0;
        var (w, gm) = CreateWorld();
        var engine = Engine(w, StatDef(SpellType.Strength), StatDef(SpellType.Agility));

        engine.ApplyDirectEffect(gm, gm, SpellType.Strength, 500);
        engine.ApplyDirectEffect(gm, gm, SpellType.Agility, 500);

        Assert.Equal(100, gm.Str);
        Assert.Equal(110, gm.Dex);
        Assert.Single(gm.Memories, m => m.ItemType == ItemType.Spell);
    }

    [Fact]
    public void StackStatsOn_DifferentStatSpellsStack()
    {
        Character.MagicFlags = (int)MagicConfigFlags.StackStats;
        var (w, gm) = CreateWorld();
        var engine = Engine(w, StatDef(SpellType.Strength), StatDef(SpellType.Agility));

        engine.ApplyDirectEffect(gm, gm, SpellType.Strength, 500);
        engine.ApplyDirectEffect(gm, gm, SpellType.Agility, 500);
        // Re-casting the same spell still refreshes rather than stacking.
        engine.ApplyDirectEffect(gm, gm, SpellType.Strength, 500);

        Assert.Equal(110, gm.Str);
        Assert.Equal(110, gm.Dex);
        Assert.Equal(2, gm.Memories.Count(m => m.ItemType == ItemType.Spell));
    }

    [Fact]
    public void SpellWithOnlyALayer_EquipsATimedMemoryOnThatLayer()
    {
        var (w, gm) = CreateWorld();
        var def = new SpellDef
        {
            Id = SpellType.Attunement, Name = "Attunement",
            Flags = SpellFlag.Bless | SpellFlag.TargChar,
            Layer = (Layer)65, DurationBase = 600, EffectBase = 10,
        };
        var engine = Engine(w, def);

        engine.ApplyDirectEffect(gm, gm, SpellType.Attunement, 500);

        var mem = gm.Memories.Single(m => m.ItemType == ItemType.Spell);
        Assert.Equal((Layer)65, mem.EquipLayer);
        Assert.Same(mem, gm.FindLayer((Layer)65));

        engine.ProcessExpirations(System.Environment.TickCount64 + 10_000_000);
        Assert.DoesNotContain(gm.Memories, m => m.ItemType == ItemType.Spell);
        Assert.True(mem.IsDeleted);
    }
}
