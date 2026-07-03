using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Ships;
using Xunit;

namespace SphereNet.Tests;

// Wave H1 (wiki/housing-movement-scriptpack.txt): ship redeed moving crate,
// ship placement overlap, REDEED-verb full-teardown routing.
public class HousingShipWaveH1Tests
{
    private static MultiRegistry MakeShipRegistry(ushort id = 0x4000)
    {
        var def = new MultiDef { Id = id, Name = "small boat" };
        def.Components.Add(new MultiComponent { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        var reg = new MultiRegistry();
        reg.Register(def);
        return reg;
    }

    [Fact]
    public void ShipRedeed_MovesHoldCargoToACrate_InsteadOfDeletingIt()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);

        // Put cargo into a component container (the hold stand-in).
        var holdUid = ship!.Components[0];
        var hold = world.FindItem(holdUid)!;
        var cargo = world.CreateItem();
        cargo.Name = "treasure";
        hold.AddItem(cargo);

        Assert.NotNull(engine.RemoveShip(ship.MultiItem.Uid, owner));

        // No bank box on the owner: the crate drops at the ship's spot with
        // the cargo inside (previously the cargo was deleted outright).
        Assert.False(cargo.IsDeleted);
        var crate = world.FindItem(cargo.ContainedIn);
        Assert.NotNull(crate);
        Assert.Equal("a moving crate", crate!.Name);
    }

    [Fact]
    public void ShipPlacement_RefusesOverlappingAnotherHull()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        Assert.NotNull(engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North));
        // Same tile: CanPlaceShip must refuse the second hull.
        Assert.False(engine.CanPlaceShip(new Point3D(200, 200, 0, 0),
            MakeShipRegistry().Get(0x4000)!));
    }

    // ---- H1b: house placement footprint checks ----

    [Fact]
    public void HousePlacement_BlockedByLivingChar_ButGmPassesThrough()
    {
        var world = TestHarness.CreateWorld();
        var multiRegistry = MakeShipRegistry(0x407C); // 1x1 footprint def
        var housing = new HousingEngine(world, multiRegistry);
        var def = multiRegistry.Get(0x407C)!;

        var bystander = world.CreateCharacter();
        bystander.MaxHits = 10; bystander.Hits = 10;
        world.PlaceCharacter(bystander, new Point3D(300, 300, 0, 0));

        // A living char in the footprint blocks placement — the old range-0
        // scan returned nothing, so this check was silently a no-op.
        Assert.False(housing.CanPlaceHouse(new Point3D(300, 300, 0, 0), def));

        // GM staff pass through (Source-X CItemMulti.cpp:3330).
        bystander.PrivLevel = Core.Enums.PrivLevel.GM;
        Assert.True(housing.CanPlaceHouse(new Point3D(300, 300, 0, 0), def));
    }

    [Fact]
    public void HousePlacement_BlockedByNoBuildMargin_AndByShipHull()
    {
        var world = TestHarness.CreateWorld();
        var multiRegistry = MakeShipRegistry(0x407C);
        var housing = new HousingEngine(world, multiRegistry);
        var def = multiRegistry.Get(0x407C)!;

        // NoBuild region 3 tiles away — inside the Source-X +5 margin.
        var noBuild = new SphereNet.Game.World.Regions.Region
        { Name = "nobuild_test", Flags = Core.Enums.RegionFlag.NoBuild, MapIndex = 0 };
        noBuild.AddRect(303, 300, 305, 302);
        world.AddRegion(noBuild);
        Assert.False(housing.CanPlaceHouse(new Point3D(300, 300, 0, 0), def));

        // A docked hull under the footprint blocks placement too.
        housing.IsShipAt = pt => pt.X == 500 && pt.Y == 500;
        Assert.False(housing.CanPlaceHouse(new Point3D(500, 500, 0, 0), def));
        Assert.True(housing.CanPlaceHouse(new Point3D(600, 600, 0, 0), def));
    }

    // ---- P3 tail: ship boundary, ship script-redeed ----

    [Fact]
    public void ShipCannotSailOffTheMapEdge()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(0, 200, 0, 0), Direction.West);
        Assert.NotNull(ship);
        // Leading west edge at x=0: the move must be refused, not wrap to -1.
        Assert.False(engine.Move(ship!, Direction.West));
        Assert.Equal(0, ship!.MultiItem.X);
    }

    [Fact]
    public void ShipRedeedFromScript_DeliversDeedAndTearsDown()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Equip(pack, Layer.Pack);
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        var deed = engine.RedeedFromScript(ship!.MultiItem.Uid);

        Assert.NotNull(deed);
        Assert.Equal(pack.Uid, deed!.ContainedIn); // delivered to the owner
        Assert.Null(engine.GetShip(ship.MultiItem.Uid));
    }

    [Fact]
    public void ShipRedeed_ThroughTheItemVerb_RoutesToTheEngine()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Equip(pack, Layer.Pack);
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);

        // Production wiring: the verb goes through the static hook.
        SphereNet.Game.Objects.Items.Item.RedeedShip = uid => engine.RedeedFromScript(uid);

        // The multi item carries ItemType.Ship — this integration path caught
        // that the verb gate only admitted Multi/MultiCustom.
        Assert.True(ship!.MultiItem.TryExecuteCommand("REDEED", "", null!));
        Assert.Null(engine.GetShip(ship.MultiItem.Uid));
        Assert.Contains(pack.Contents, i => i.Name.EndsWith("deed"));
    }

    [Fact]
    public void LockdownAndSecure_RefuseItemsOutsideTheHouseRegion()
    {
        var world = TestHarness.CreateWorld();
        var multiRegistry = MakeShipRegistry(0x407C);
        var housing = new HousingEngine(world, multiRegistry);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var house = housing.PlaceHouse(owner, 0x407C, new Point3D(300, 300, 0, 0));
        Assert.NotNull(house);

        var outside = world.CreateItem();
        world.PlaceItem(outside, new Point3D(350, 350, 0, 0));
        Assert.False(house!.Lockdown(outside.Uid, owner.Uid));
        outside.ItemType = ItemType.Container;
        Assert.False(house.SecureContainer(outside.Uid, owner.Uid));

        var inside = world.CreateItem();
        world.PlaceItem(inside, new Point3D(300, 300, 0, 0));
        Assert.True(house.Lockdown(inside.Uid, owner.Uid));
    }

    [Fact]
    public void RedeedFromScript_TearsDownRegistryAndRegion()
    {
        var world = TestHarness.CreateWorld();
        var multiRegistry = new MultiRegistry();
        var def = new MultiDef { Id = 0x407C, Name = "small house" };
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        multiRegistry.Register(def);
        var housing = new HousingEngine(world, multiRegistry);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var house = housing.PlaceHouse(owner, 0x407C, new Point3D(300, 300, 0, 0));
        Assert.NotNull(house);

        var deed = housing.RedeedFromScript(house!.MultiItem.Uid);
        Assert.NotNull(deed);
        // Registry slot freed and the dynamic house region torn down —
        // the raw house.Redeed path leaked both.
        Assert.Null(housing.GetHouse(house.MultiItem.Uid));
        Assert.Null(housing.FindHouseAt(new Point3D(300, 300, 0, 0)));
    }
}
