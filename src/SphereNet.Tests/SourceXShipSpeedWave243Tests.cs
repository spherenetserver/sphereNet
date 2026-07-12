using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 243 — Source-X CCMultiMovable::SetNextMove (CCMultiMovable.cpp:119/124):
/// one-tile steering (SMT_SLOW) runs at the full period, while continuous
/// ("normal") sailing runs in the fast speed mode and halves the tick interval,
/// so a ship sailing forward advances twice as fast as click-by-click steering.
/// </summary>
public sealed class SourceXShipSpeedWave243Tests
{
    private static (ShipEngine engine, Ship ship) MakeShip()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(200, 200, 0, 0));
        var engine = new ShipEngine(world, new MultiRegistry(), null);
        var ship = new Ship(multi) { SpeedPeriod = 1000 };
        return (engine, ship);
    }

    [Fact]
    public void SetMoveDir_ContinuousSailing_HalvesTheInterval()
    {
        var (engine, ship) = MakeShip();

        long before = Environment.TickCount64;
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal));

        Assert.Equal(ShipSpeedMode.Fast, ship.SpeedMode);
        long delay = ship.NextMoveTick - before;
        Assert.InRange(delay, 500, 700); // ~SpeedPeriod / 2 (+ scheduling slack)
    }

    [Fact]
    public void SetMoveDir_OneTileSteering_UsesFullPeriod()
    {
        var (engine, ship) = MakeShip();

        long before = Environment.TickCount64;
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.OneTile));

        Assert.Equal(ShipSpeedMode.Slow, ship.SpeedMode);
        long delay = ship.NextMoveTick - before;
        Assert.InRange(delay, 1000, 1200); // full SpeedPeriod (+ scheduling slack)
    }
}
