using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X ship/housing (multi) parity (wiki/9.txt audit): diagonal ship movement
// is preserved, custom-foundation houses are restored on load, and persisted
// lockdown/secure entries are loaded without re-running capacity checks.
public class ShipHousingParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ---- #9: ship movement keeps all 8 directions ----

    [Fact]
    public void SetMoveDir_PreservesDiagonalMovement()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(200, 200, 0, 0));
        var engine = new ShipEngine(world, new MultiRegistry(), null);
        var ship = new Ship(multi);

        Assert.True(engine.SetMoveDir(ship, Direction.NorthEast, ShipMovementType.Normal));
        Assert.Equal(Direction.NorthEast, ship.DirMove); // not collapsed to East

        Assert.True(engine.SetMoveDir(ship, Direction.SouthWest, ShipMovementType.Normal));
        Assert.Equal(Direction.SouthWest, ship.DirMove);
    }

    // ---- #2: custom-foundation houses are rebuilt on load ----

    [Fact]
    public void DeserializeFromWorld_RestoresCustomFoundationHouse()
    {
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());

        var custom = world.CreateItem();
        custom.BaseId = 0x1BC0;
        custom.ItemType = ItemType.MultiCustom; // PlaceHouse stamps custom foundations this way
        world.PlaceItem(custom, new Point3D(300, 300, 0, 0));
        custom.SetTag("HOUSE.OWNER", $"0{new Serial(0x401).Value:x8}");

        engine.DeserializeFromWorld();

        Assert.NotNull(engine.GetHouse(custom.Uid)); // was dropped (only Multi was read) before
    }

    // ---- #7: persisted lockdown/secure load past the current limit ----

    [Fact]
    public void LockdownForLoad_BypassesCapacityLimit()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x1BC0;
        world.PlaceItem(multi, new Point3D(400, 400, 0, 0));

        var house = new House(multi) { Owner = new Serial(0x401), BaseStorage = 4 };
        // MaxLockdowns = 4 * 50% = 2, MaxSecure = 4 - 2 = 2.
        Assert.Equal(2, house.MaxLockdowns);

        // A saved house with 4 lockdowns must load all 4 even though the live cap is 2.
        house.LockdownForLoad(new Serial(0x1001));
        house.LockdownForLoad(new Serial(0x1002));
        house.LockdownForLoad(new Serial(0x1003));
        house.LockdownForLoad(new Serial(0x1004));
        Assert.Equal(4, house.Lockdowns.Count);

        house.SecureForLoad(new Serial(0x2001));
        house.SecureForLoad(new Serial(0x2002));
        house.SecureForLoad(new Serial(0x2003));
        Assert.Equal(3, house.SecureContainers.Count);
    }
}
