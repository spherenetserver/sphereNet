using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a load costs while walking — the first packet of ini keys PLAN-302 asks for
/// (port plan İŞ-69).
///
/// PLAN-302's acceptance criterion is blunt: *changing a config value changes the
/// matching behaviour measurably*. So each key here is asserted by MOVING it and
/// watching the step, not by reading it back.
///
/// The behaviour was missing outright. This engine shipped BACKPACKOVERLOAD=40 —
/// permission to carry 40 stones more than you can carry — with nothing to pay for it,
/// because the per-step cost is not in Event_Walk (where a comment here said it was
/// absent) but one level down, in CanMoveWalkTo's committed branch
/// (CCharAct.cpp:4787-4829).
///
/// Two branches, and they are not variations of one another: under the carry weight a
/// step MIGHT cost one point, over it a step ALWAYS costs several.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WeightMovementConfigTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public WeightMovementConfigTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => MovementEngine.ResetWeightLossRoll();

    private static (GameWorld World, MovementEngine Engine, Character Walker, Item Pack) Walker()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BaseId = 0x0190;
        ch.Str = 40;                      // carry weight = 40 + 40*3.5 = 180 stones
        ch.MaxStam = 100; ch.Stam = 100;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);

        return (world, new MovementEngine(world), ch, pack);
    }

    /// <summary>Put <paramref name="stones"/> of dead weight in the pack.</summary>
    private static Item Load(GameWorld world, Item pack, int stones)
    {
        var rock = world.CreateItem();
        rock.BaseId = 0x1BF2;
        rock.TrySetProperty("BASEWEIGHT", (stones * Item.WeightUnits).ToString());
        pack.AddItem(rock);
        return rock;
    }

    // ---- under the carry weight: a chance --------------------------------

    [Fact]
    public void AnUnburdenedStepCostsNothing()
    {
        var (_, engine, ch, _) = Walker();
        MovementEngine.WeightLossRoll = _ => 0;    // the kindest possible die

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"weight {ch.GetTotalWeight()}/{ch.MaxWeight} stam {ch.Stam}");

        // Even with the die rigged to its lowest value, an empty-handed walker pays
        // nothing: the S-curve's chance at zero load is zero, not small.
        Assert.Equal(100, ch.Stam);
    }

    [Fact]
    public void LoweringTheThresholdMakesAnOrdinaryLoadExpensive()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 90);                     // half of what this walker can carry
        MovementEngine.WeightLossRoll = _ => 0;

        MovementEngine.StaminaLossAtWeight = 150;  // the default
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        short atDefault = ch.Stam;

        MovementEngine.StaminaLossAtWeight = 40;   // now the load is well past the midpoint
        Assert.True(engine.TryMove(ch, Direction.South, running: false, sequence: 0));
        short atLowered = ch.Stam;

        _out.WriteLine($"load {ch.GetWeightLoadPercent()}% -> default {atDefault}, lowered {atLowered}");

        // PLAN-302's acceptance test, in one assertion: the same load, the same step,
        // a different setting, a different outcome. At 150 a half-full pack is nowhere
        // near paying; at 40 it pays every time.
        Assert.Equal(100, atDefault);
        Assert.Equal(99, atLowered);
    }

    [Fact]
    public void TheThresholdIsAMidpointNotACliff()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 90);                     // 50% load
        MovementEngine.StaminaLossAtWeight = 50;   // exactly at the midpoint

        // At the midpoint the S-curve is 500 in 1000: a die below it pays, one above
        // it does not. A threshold would answer the same either way, which is how a
        // chance quietly becomes a rule.
        MovementEngine.WeightLossRoll = _ => 499;
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        short unlucky = ch.Stam;

        ch.Stam = 100;
        MovementEngine.WeightLossRoll = _ => 501;
        Assert.True(engine.TryMove(ch, Direction.South, running: false, sequence: 0));
        short lucky = ch.Stam;

        _out.WriteLine($"at the midpoint: unlucky {unlucky}, lucky {lucky}");
        Assert.Equal(99, unlucky);
        Assert.Equal(100, lucky);
    }

    [Fact]
    public void FlyingAddsTheRunningPenaltyToTheLoad()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 90);                     // 50% load
        MovementEngine.StaminaLossAtWeight = 90;
        MovementEngine.RunningPenalty = 50;        // 50% + 50 = 100, past the midpoint
        MovementEngine.WeightLossRoll = _ => 500;

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        short onFoot = ch.Stam;

        ch.Stam = 100;
        ch.SetStatFlag(StatFlag.Fly);
        Assert.True(engine.TryMove(ch, Direction.South, running: false, sequence: 0));
        short flying = ch.Stam;

        _out.WriteLine($"on foot {onFoot}, flying {flying}");

        // The key is called RUNNINGPENALTY and the reference checks the FLY/HOVERING
        // flags, not running (CCharAct.cpp:4799). The behaviour is matched, not the
        // name - which is also why running on foot costs nothing extra.
        Assert.Equal(100, onFoot);
        Assert.Equal(99, flying);
    }

    // ---- over the carry weight: a certainty ------------------------------

    [Fact]
    public void BeingOverweightCostsEveryStepWithNoDiceAtAll()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 185);                    // 5 stones past 180
        MovementEngine.WeightLossRoll = _ => 999;  // the luckiest possible die

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"weight {ch.GetTotalWeight()}/{ch.MaxWeight} stam {ch.Stam}");

        // 5 base + one per 5 stones over = 6, charged outright. The die is not
        // consulted on this branch, which is the whole difference between it and the
        // one above.
        Assert.Equal(94, ch.Stam);
    }

    [Fact]
    public void TheOverweightCostGrowsWithTheExcess()
    {
        var (world, engine, ch, pack) = Walker();
        var rock = Load(world, pack, 185);

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        int near = 100 - ch.Stam;

        ch.Stam = 100;
        rock.TrySetProperty("BASEWEIGHT", (230 * Item.WeightUnits).ToString());  // 50 stones over
        Assert.True(engine.TryMove(ch, Direction.South, running: false, sequence: 0));
        int far = 100 - ch.Stam;

        _out.WriteLine($"5 stones over: {near}; 50 stones over: {far}");

        // One more point for every five stones past the limit (CCharAct.cpp:4819).
        // A flat cost would make 50 stones over as cheap as 5 and turn
        // BACKPACKOVERLOAD into a free pass.
        Assert.Equal(6, near);
        Assert.Equal(15, far);
    }

    [Fact]
    public void AMountCarriesTwoThirdsOfTheOverweightCost()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 230);
        ch.SetStatFlag(StatFlag.OnHorse);

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"mounted, 50 over: cost {100 - ch.Stam}");

        // 15 / 3. The horse does the carrying, the rider still pays something.
        Assert.Equal(5, 100 - ch.Stam);
    }

    [Fact]
    public void TheOverweightUpliftUsesItsOwnKeyRatherThanItsNeighbours()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 185);
        ch.SetStatFlag(StatFlag.Hovering);

        MovementEngine.RunningPenaltyOverweight = 100;
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        int doubled = 100 - ch.Stam;

        ch.Stam = 100;
        MovementEngine.RunningPenaltyOverweight = 0;
        Assert.True(engine.TryMove(ch, Direction.South, running: false, sequence: 0));
        int plain = 100 - ch.Stam;

        _out.WriteLine($"uplift 100% -> {doubled}, uplift 0% -> {plain}");

        // DELIBERATE DIVERGENCE. Upstream's key table points RUNNINGPENALTYOVERWEIGHT
        // at the field RUNNINGPENALTY already owns (CServerConfig.cpp:981-982), so
        // setting it there moves the wrong number and this uplift never leaves its
        // default. The consuming site reads the overweight field by name
        // (CCharAct.cpp:4823); the key is wired to the field it names.
        Assert.Equal(12, doubled);
        Assert.Equal(6, plain);
    }

    // ---- who does not pay ------------------------------------------------

    [Fact]
    public void AGmPaysNothingHoweverLoadedTheyAre()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 500);
        ch.PrivLevel = PrivLevel.GM;

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"GM load {ch.GetWeightLoadPercent()}% stam {ch.Stam}");

        // Upstream returns from CanMoveWalkTo at the GM check, before the penalty
        // block - and GetWeightLoadPercent answers 1 for staff whatever they hold.
        Assert.Equal(100, ch.Stam);
        Assert.Equal(1, ch.GetWeightLoadPercent());
    }

    [Fact]
    public void MovementStillSucceedsWhenTheCostExceedsWhatIsLeft()
    {
        var (world, engine, ch, pack) = Walker();
        Load(world, pack, 400);
        ch.Stam = 2;

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"stam after an unaffordable step: {ch.Stam}");

        // The cost is charged after the step is committed, so it drains rather than
        // refuses. Stamina floors at zero instead of going negative, which would read
        // as an enormous positive through the status packet's unsigned fields.
        Assert.Equal(0, ch.Stam);
    }
}
