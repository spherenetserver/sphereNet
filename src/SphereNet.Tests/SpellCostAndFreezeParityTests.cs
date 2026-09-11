using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a cast costs, and what walking does to it (port plan İŞ-23 / PLAN-403).
///
/// Three things the reference does that this engine did not:
///
/// 1. LOWERMANACOST is a percent off the bill, and may be NEGATIVE to raise it
///    (Calc_SpellManaCost, CResourceCalc.cpp:545). Nothing read it.
///
/// 2. LOWERREAGENTCOST is a percent CHANCE the cast spends no reagents at all
///    (Calc_SpellReagentsConsume, :570) - not a discount on how many. Nothing read it
///    either, although the reference script pack sets both on its artifacts.
///
/// 3. Walking does NOT interrupt a spell. The reference gives one of two answers
///    (OnFreezeCheck, CCharAct.cpp:4539): the cast roots you (MAGICF_FREEZEONCAST, or
///    SPELLFLAG_FREEZEONCAST while the global flag is off), or you walk and keep
///    casting. This engine cancelled the spell on any step, which is neither - and on
///    the live shard, whose MAGICFLAGS is 0, that is the difference between walking
///    while casting and losing the spell to one footfall.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellCostAndFreezeParityTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_cost_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static void PinStatics()
    {
        // ResetEngineStatics runs between the constructor and the test body, so the
        // pinning has to happen here.
        Character.MagicFlags = 0;
        Character.ReagentsRequiredEnabled = true;
        Character.SpellbookRequiredEnabled = false;
        Character.EquippedCastEnabled = true;
    }

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(
        ushort manaCost = 40, SpellFlag extraFlags = SpellFlag.None,
        (ushort Id, int Count)[]? reagents = null)
    {
        PinStatics();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        var def = new SpellDef
        {
            Id = SpellType.Heal,
            Flags = SpellFlag.TargChar | SpellFlag.Heal | extraFlags,
            ManaCost = manaCost,
            CastTimeBase = 20,
            EffectBase = 20,
            EffectScale = 20,
        };
        foreach (var (id, count) in reagents ?? [])
            def.Reagents[id] = count;
        registry.Register(def);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        // Max first: the pool setter clamps to the maximum, so the chained
        // assignment would have left the mana at zero.
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxHits = 100; caster.Hits = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);

        return (world, new SpellEngine(world, registry), caster);
    }

    private static int ManaSpent(SpellEngine engine, Character caster)
    {
        int before = caster.Mana;
        string? why = null;
        engine.OnSysMessage = (_, m) => why = m;
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0,
            $"cast refused: {why ?? "(no message)"}");
        caster.SetCastTimerEnd(Environment.TickCount64 - 1);
        engine.TickCastTimer(caster);
        return before - caster.Mana;
    }

    // ---- LOWERMANACOST -----------------------------------------------------

    [Fact]
    public void LowerManaCostTakesAPercentOffTheBill()
    {
        var (_, engine, caster) = Setup(manaCost: 40);
        Assert.Equal(40, ManaSpent(engine, caster));

        caster.Mana = caster.MaxMana;
        caster.SetTag(SpellCastingProperties.LowerManaCost, "25");
        Assert.Equal(30, ManaSpent(engine, caster));       // 40 - 25%
    }

    [Fact]
    public void ANegativeLowerManaCostRaisesTheBill()
    {
        // The reference says so in its own comment: "LowerManaCost can be negative,
        // and thus increasing the mana cost" (CResourceCalc.cpp:547).
        var (_, engine, caster) = Setup(manaCost: 40);
        caster.SetTag(SpellCastingProperties.LowerManaCost, "-50");

        Assert.Equal(60, ManaSpent(engine, caster));
    }

    [Fact]
    public void LowerManaCostIsSummedOffWornItemsToo()
    {
        var (world, engine, caster) = Setup(manaCost: 40);
        var ring = world.CreateItem();
        ring.BaseId = 0x108A;
        ring.SetTag(SpellCastingProperties.LowerManaCost, "10");
        caster.Equip(ring, Layer.Ring);
        caster.SetTag(SpellCastingProperties.LowerManaCost, "15");

        Assert.Equal(30, ManaSpent(engine, caster));       // 40 - 25%
    }

    // ---- LOWERREAGENTCOST ---------------------------------------------------

    [Fact]
    public void LowerReagentCostAtAHundredNeverSpendsReagents()
    {
        const ushort reagentId = 0x0F7A;
        var (world, engine, caster) = Setup(manaCost: 0, reagents: [(reagentId, 1)]);

        var reg = world.CreateItem();
        reg.BaseId = reagentId;
        reg.Amount = 20;
        Assert.True(caster.Backpack!.TryAddItem(reg));

        caster.SetTag(SpellCastingProperties.LowerReagentCost, "100");

        for (int i = 0; i < 10; i++)
        {
            caster.Mana = caster.MaxMana;
            Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
            caster.SetCastTimerEnd(Environment.TickCount64 - 1);
            engine.TickCastTimer(caster);
        }

        Assert.Equal(20, reg.Amount);
    }

    [Fact]
    public void WithoutTheChanceTheReagentsAreSpentAsBefore()
    {
        const ushort reagentId = 0x0F7A;
        var (world, engine, caster) = Setup(manaCost: 0, reagents: [(reagentId, 1)]);

        var reg = world.CreateItem();
        reg.BaseId = reagentId;
        reg.Amount = 20;
        Assert.True(caster.Backpack!.TryAddItem(reg));

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        caster.SetCastTimerEnd(Environment.TickCount64 - 1);
        engine.TickCastTimer(caster);

        Assert.Equal(19, reg.Amount);
    }

    [Fact]
    public void AFreeCastIsNotRefusedForLackingReagents()
    {
        // The reference's roll wraps the availability check as well as the spend, so
        // a cast that pays nothing cannot be turned away for having nothing.
        const ushort reagentId = 0x0F7A;
        var (_, engine, caster) = Setup(manaCost: 0, reagents: [(reagentId, 1)]);
        caster.SetTag(SpellCastingProperties.LowerReagentCost, "100");

        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
    }

    // ---- walking while casting ---------------------------------------------

    [Fact]
    public void WalkingDoesNotInterruptACast()
    {
        var (world, engine, caster) = Setup();
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Assert.True(caster.IsCasting);

        var movement = new MovementEngine(world) { SpellEngine = engine };
        Assert.True(movement.TryMove(caster, Direction.East, false, 0));

        Assert.True(caster.IsCasting);
    }

    [Fact]
    public void TheGlobalFreezeFlagRefusesTheStepInstead()
    {
        var (world, engine, caster) = Setup();
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Character.MagicFlags = (int)MagicConfigFlags.FreezeOnCast;

        var movement = new MovementEngine(world) { SpellEngine = engine };
        Assert.False(movement.TryMove(caster, Direction.East, false, 0));

        // The spell is still going: the step was refused, not the cast.
        Assert.True(caster.IsCasting);
    }

    [Fact]
    public void ASpellCanRootTheCasterOnItsOwn()
    {
        // SPELLFLAG_FREEZEONCAST with the global flag off (CCharAct.cpp:4547).
        var (world, engine, caster) = Setup(extraFlags: SpellFlag.FreezeOnCast);
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);

        var movement = new MovementEngine(world) { SpellEngine = engine };
        Assert.False(movement.TryMove(caster, Direction.East, false, 0));
        Assert.True(caster.IsCasting);
    }

    [Fact]
    public void ASpellCanExemptItselfFromTheGlobalFlag()
    {
        // SPELLFLAG_NOFREEZEONCAST with the global flag on (CCharAct.cpp:4544).
        var (world, engine, caster) = Setup(extraFlags: SpellFlag.NoFreezeOnCast);
        Assert.True(engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position) > 0);
        Character.MagicFlags = (int)MagicConfigFlags.FreezeOnCast;

        var movement = new MovementEngine(world) { SpellEngine = engine };
        Assert.True(movement.TryMove(caster, Direction.East, false, 0));
        Assert.True(caster.IsCasting);
    }
}
