using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("VendorStateSerial")]
public sealed class HousingShipIntegrityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character CreatePlayer(GameWorld world, short x = 50, short y = 50)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(x, y, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        var bank = world.CreateItem();
        bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);
        return player;
    }

    private static MultiRegistry CreateHouseRegistry(ushort id = 0x0064, short min = 0, short max = 0)
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = id, Name = "test house" };
        def.Components.Add(new MultiComponent
        {
            TileId = 0x0064, DeltaX = min, DeltaY = min, DeltaZ = 0, Visible = true,
        });
        if (max != min)
        {
            def.Components.Add(new MultiComponent
            {
                TileId = 0x0065, DeltaX = max, DeltaY = max, DeltaZ = 0, Visible = true,
            });
        }
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    private static MultiRegistry CreateDirectionalShipRegistry()
    {
        var registry = new MultiRegistry();
        var north = new MultiDef { Id = 0x4000, Name = "test ship" };
        north.Components.Add(new MultiComponent
            { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        north.Components.Add(new MultiComponent
            { TileId = 0x3E41, DeltaX = 0, DeltaY = -1, DeltaZ = 0, Visible = true });
        north.RecalcBounds();
        registry.Register(north);

        var east = new MultiDef { Id = 0x4001, Name = "test ship" };
        east.Components.Add(new MultiComponent
            { TileId = 0x3E50, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        east.Components.Add(new MultiComponent
            { TileId = 0x3E51, DeltaX = 1, DeltaY = 0, DeltaZ = 0, Visible = true });
        east.RecalcBounds();
        registry.Register(east);
        return registry;
    }

    [Fact]
    public void HouseRoles_AreExclusive_AndBanOverridesPublicAccess()
    {
        var world = CreateWorld();
        var owner = CreatePlayer(world);
        var target = CreatePlayer(world, 60, 60);
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        var house = new House(multi) { Owner = owner.Uid, Type = HouseType.Public };

        Assert.True(house.AddFriend(target.Uid));
        Assert.Equal(HousePriv.Friend, house.GetPriv(target.Uid));
        Assert.True(house.AddBan(target.Uid));
        Assert.Equal(HousePriv.Ban, house.GetPriv(target.Uid));
        Assert.DoesNotContain(target.Uid, house.Friends);
        Assert.False(house.CanAccess(target.Uid));
        Assert.True(house.CanAccess(new Serial(0x4000ABCD)));
        Assert.False(house.AddBan(owner.Uid));
    }

    [Fact]
    public void LockdownAndSecure_RejectInvalidTargets_AndReleaseClearsStructureLink()
    {
        var world = CreateWorld();
        var owner = CreatePlayer(world);
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        var house = new House(multi) { Owner = owner.Uid };

        Assert.False(house.Lockdown(new Serial(0x40009999), owner.Uid));
        var normal = world.CreateItem();
        Assert.False(house.SecureContainer(normal.Uid, owner.Uid));

        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        Assert.True(house.SecureContainer(container.Uid, owner.Uid));
        Assert.True(container.IsAttr(ObjAttributes.Secure));
        Assert.Equal(multi.Uid, container.Link);
        Assert.True(house.ReleaseSecure(container.Uid, owner.Uid));
        Assert.False(container.IsAttr(ObjAttributes.Secure));
        Assert.False(container.Link.IsValid);

        Assert.True(house.Lockdown(normal.Uid, owner.Uid));
        Assert.True(house.ReleaseLockdown(normal.Uid, owner.Uid));
        Assert.False(normal.IsAttr(ObjAttributes.LockedDown));
        Assert.False(normal.Link.IsValid);
    }

    [Fact]
    public void HousePersistence_RestoresAccessVendorGuildAndProtectedItemState()
    {
        var world = CreateWorld();
        var registry = CreateHouseRegistry();
        var owner = CreatePlayer(world);
        var access = CreatePlayer(world, 60, 60);
        var vendor = CreatePlayer(world, 70, 70);
        var engine = new HousingEngine(world, registry);
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
        house.AddAccess(access.Uid);
        house.AddVendor(vendor.Uid);
        house.GuildStone = new Serial(0x40001234);
        var locked = world.CreateItem();
        world.PlaceItem(locked, new Point3D(200, 200, 0, 0));
        var secure = world.CreateItem();
        secure.ItemType = ItemType.Container;
        world.PlaceItem(secure, new Point3D(200, 200, 0, 0));
        Assert.True(house.Lockdown(locked.Uid, owner.Uid));
        Assert.True(house.SecureContainer(secure.Uid, owner.Uid));
        engine.SerializeAllToTags();

        locked.ClearAttr(ObjAttributes.LockedDown);
        locked.Link = Serial.Invalid;
        secure.ClearAttr(ObjAttributes.Secure);
        secure.Link = Serial.Invalid;
        engine.DeserializeFromWorld();
        var loaded = engine.GetHouse(house.MultiItem.Uid)!;

        Assert.Contains(access.Uid, loaded.AccessList);
        Assert.Contains(vendor.Uid, loaded.Vendors);
        Assert.Equal(new Serial(0x40001234), loaded.GuildStone);
        Assert.True(locked.IsAttr(ObjAttributes.LockedDown));
        Assert.True(secure.IsAttr(ObjAttributes.Secure));
        Assert.Equal(house.MultiItem.Uid, locked.Link);
        Assert.Equal(house.MultiItem.Uid, secure.Link);

        int regionCount = world.Regions.Count(r => r.IsFlag(RegionFlag.House));
        engine.DeserializeFromWorld();
        Assert.Equal(regionCount, world.Regions.Count(r => r.IsFlag(RegionFlag.House)));
    }

    [Fact]
    public void TransferHouse_RotatesKeys_AndCoOwnerCannotForgeTransferResponse()
    {
        var world = CreateWorld();
        var registry = CreateHouseRegistry();
        var engine = new HousingEngine(world, registry);
        var owner = CreatePlayer(world);
        var newOwner = CreatePlayer(world, 60, 60);
        var coOwner = CreatePlayer(world, 70, 70);
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
        house.AddCoOwner(coOwner.Uid);

        Assert.True(engine.TransferHouse(house, owner, newOwner));
        Assert.Equal(newOwner.Uid, house.Owner);
        Assert.DoesNotContain(owner.Backpack!.Contents, i => i.ItemType == ItemType.Key && i.Link == house.MultiItem.Uid);
        Assert.Contains(newOwner.Backpack!.Contents, i => i.ItemType == ItemType.Key && i.Link == house.MultiItem.Uid);

        house.AddCoOwner(coOwner.Uid);
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2601);
        client.SetEngines(housingEngine: engine);
        TestHarness.AttachCharacter(client, coOwner);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("OpenHouseSignGump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [house.MultiItem]);
        uint gump = Assert.Single(client.Gumps.ActiveGumps);
        client.HandleGumpResponse(house.MultiItem.Uid.Value, gump, 1, [], []);
        client.HandleTargetResponse(0, 0, owner.Uid.Value, owner.X, owner.Y, owner.Z, 0);
        Assert.Equal(newOwner.Uid, house.Owner);
    }

    [Fact]
    public void HouseRedeed_CanBePlacedAgain_AndRestoresUuid()
    {
        var world = CreateWorld();
        var registry = CreateHouseRegistry();
        var engine = new HousingEngine(world, registry);
        var owner = CreatePlayer(world);
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
        Guid uuid = house.MultiItem.Uuid;
        var deed = engine.RemoveHouse(house.MultiItem.Uid, owner)!;
        Assert.Equal(owner.Backpack!.Uid, deed.ContainedIn);
        Assert.Equal((uint)0x0064, deed.More1);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2602);
        client.SetEngines(housingEngine: engine);
        TestHarness.AttachCharacter(client, owner);
        client.HandleDoubleClick(deed.Uid.Value);
        client.HandleTargetResponse(0, 0, 0, 300, 300, 0, 0);

        var rebuilt = engine.FindHouseAt(new Point3D(300, 300, 0, 0));
        Assert.NotNull(rebuilt);
        Assert.Equal(uuid, rebuilt!.MultiItem.Uuid);
        Assert.True(deed.IsDeleted);
    }

    [Fact]
    public void CustomHouseSession_EnforcesBoundsSingleArchitectAndCurrentOwner()
    {
        var world = CreateWorld();
        var registry = CreateHouseRegistry(0x1404, -2, 2);
        var housing = new HousingEngine(world, registry);
        var owner = CreatePlayer(world);
        var replacement = CreatePlayer(world, 60, 60);
        var gm = CreatePlayer(world, 70, 70);
        gm.PrivLevel = PrivLevel.GM;
        var house = housing.PlaceHouse(owner, 0x1404, new Point3D(200, 200, 0, 0), customFoundation: true)!;
        var custom = new CustomHousingEngine(world, housing);

        custom.Begin(owner, house.MultiItem);
        Assert.True(custom.Build(owner, 0x0064, 2, 2));
        Assert.False(custom.Build(owner, 0x0064, 3, 2));
        custom.Begin(gm, house.MultiItem);
        Assert.Null(custom.GetSession(owner.Uid));

        custom.End(gm);
        custom.Begin(owner, house.MultiItem);
        house.TransferOwnership(replacement.Uid);
        Assert.Null(custom.Commit(owner));
        Assert.Null(custom.GetSession(owner.Uid));
    }

    [Fact]
    public void DirectionalShipPlacementAndTurn_UseFacingDefinitionAndRotateRiders()
    {
        var world = CreateWorld();
        var registry = CreateDirectionalShipRegistry();
        var owner = CreatePlayer(world);
        var engine = new ShipEngine(world, registry, null)
        {
            MaxShipsPerPlayer = -1,
            MaxShipsPerAccount = -1,
        };

        var eastShip = engine.PlaceShip(owner, 0x4000, new Point3D(300, 300, 0, 0), Direction.East)!;
        Assert.Equal((ushort)0x4001, eastShip.MultiItem.BaseId);
        var eastComponent = world.FindItem(eastShip.Components[1])!;
        Assert.Equal((ushort)0x3E51, eastComponent.BaseId);
        Assert.Equal(301, eastComponent.X);
        Assert.Equal(eastShip.MultiItem.Uid, eastComponent.Link);
        Assert.True(eastComponent.IsAttr(ObjAttributes.Move_Never));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(400, 400, 0, 0), Direction.North)!;
        ship.Anchored = false;
        var rider = CreatePlayer(world, 400, 400);
        rider.Direction = Direction.North;
        Assert.True(engine.Face(ship, Direction.East));
        Assert.Equal((ushort)0x4001, ship.MultiItem.BaseId);
        Assert.Equal(Direction.East, rider.Direction);
        var turnedComponent = world.FindItem(ship.Components[1])!;
        Assert.Equal((ushort)0x3E51, turnedComponent.BaseId);
        Assert.Equal(new Point3D(401, 400, 0, 0), turnedComponent.Position);
    }

    [Fact]
    public void ShipPilot_RequiresUnanchoredAboardUnmountedCharacter()
    {
        var world = CreateWorld();
        var registry = CreateDirectionalShipRegistry();
        var owner = CreatePlayer(world);
        var pilot = CreatePlayer(world, 50, 60);
        var engine = new ShipEngine(world, registry, null);
        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North)!;

        Assert.False(engine.SetPilot(ship, pilot));
        world.MoveCharacter(pilot, new Point3D(200, 200, 0, 0));
        Assert.False(engine.SetPilot(ship, pilot));
        ship.Anchored = false;
        pilot.SetStatFlag(StatFlag.OnHorse);
        Assert.False(engine.SetPilot(ship, pilot));
        pilot.ClearStatFlag(StatFlag.OnHorse);
        Assert.True(engine.SetPilot(ship, pilot));
        Assert.Equal(pilot.Uid, ship.Pilot);
        Assert.True(engine.SetPilot(ship, null));
        Assert.False(ship.Pilot.IsValid);
    }

    [Fact]
    public void ShipPlacement_EnforcesPerPlayerOwnershipLimit()
    {
        var world = CreateWorld();
        var owner = CreatePlayer(world);
        var engine = new ShipEngine(world, CreateDirectionalShipRegistry(), null)
        {
            MaxShipsPerPlayer = 1,
            MaxShipsPerAccount = 1,
        };
        Assert.NotNull(engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North));
        Assert.Null(engine.PlaceShip(owner, 0x4000, new Point3D(300, 300, 0, 0), Direction.North));
    }

    [Fact]
    public void ShipDryDock_PreservesLooseDeckItems_AndPlanRebuildsUuid()
    {
        var oldShipResolver = Item.ResolveShipEngine;
        try
        {
            var world = CreateWorld();
            var registry = CreateDirectionalShipRegistry();
            var housing = new HousingEngine(world, registry);
            var owner = CreatePlayer(world);
            var engine = new ShipEngine(world, registry, null);
            Item.ResolveShipEngine = () => engine;
            var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North)!;
            Guid uuid = ship.MultiItem.Uuid;
            var loose = world.CreateItem();
            world.PlaceItem(loose, new Point3D(200, 200, 0, 0));

            var plan = engine.RemoveShip(ship.MultiItem.Uid, owner)!;
            Assert.Equal((ushort)0x14F1, plan.BaseId);
            var crate = world.FindItem(loose.ContainedIn);
            Assert.NotNull(crate);
            Assert.Equal("a moving crate", crate!.Name);
            Assert.Equal(owner.GetEquippedItem(Layer.BankBox)!.Uid, crate.ContainedIn);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(loggerFactory, world,
                new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2603);
            client.SetEngines(housingEngine: housing);
            TestHarness.AttachCharacter(client, owner);
            client.HandleDoubleClick(plan.Uid.Value);
            client.HandleTargetResponse(0, 0, 0, 300, 300, 0, 0);

            var rebuilt = engine.FindShipAt(new Point3D(300, 300, 0, 0));
            Assert.NotNull(rebuilt);
            Assert.Equal(uuid, rebuilt!.MultiItem.Uuid);
            Assert.True(plan.IsDeleted);
        }
        finally
        {
            Item.ResolveShipEngine = oldShipResolver;
        }
    }

    [Fact]
    public void ShipPersistence_CleansStalePilotAndRestoresMoveAndComponentLinks()
    {
        var world = CreateWorld();
        var registry = CreateDirectionalShipRegistry();
        var owner = CreatePlayer(world);
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        multi.ItemType = ItemType.Ship;
        world.PlaceItem(multi, new Point3D(200, 200, 0, 0));
        var component = world.CreateItem();
        component.BaseId = 0x3E40;
        world.PlaceItem(component, new Point3D(200, 200, 0, 0));
        multi.SetTag("SHIP.OWNER", $"0{owner.Uid.Value:X}");
        multi.SetTag("SHIP.ANCHORED", "1");
        multi.SetTag("SHIP.DIRFACE", "0");
        multi.SetTag("SHIP.DIRMOVE", ((byte)Direction.SouthWest).ToString());
        multi.SetTag("SHIP.SPEEDPERIOD", "0");
        multi.SetTag("SHIP.SPEEDTILES", "255");
        multi.SetTag("SHIP.SPEEDMODE", "99");
        multi.SetTag("SHIP.PILOT", $"0{owner.Uid.Value:X}");
        multi.SetTag("SHIP.COMPONENTS", $"0{component.Uid.Value:X}");

        var engine = new ShipEngine(world, registry, null);
        engine.DeserializeFromWorld();
        var ship = engine.GetShip(multi.Uid)!;
        Assert.Equal(Direction.SouthWest, ship.DirMove);
        Assert.Equal(1, ship.SpeedPeriod);
        Assert.Equal(16, ship.SpeedTiles);
        Assert.False(ship.Pilot.IsValid);
        Assert.Equal(multi.Uid, component.Link);
        Assert.True(component.IsAttr(ObjAttributes.Move_Never));

        multi.SetTag("SHIP.PILOT", "040001234");
        ship.Pilot = Serial.Invalid;
        engine.SerializeAllToTags();
        Assert.False(multi.TryGetTag("SHIP.PILOT", out _));
    }
}
