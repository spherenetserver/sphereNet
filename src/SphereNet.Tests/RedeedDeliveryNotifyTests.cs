using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A redeed puts the deed in the owner's backpack. Their backpack may be open on
/// their screen while it happens.
///
/// Upstream announces new container content to every client that has that container
/// open - CItemContainer::ContentAdd calls CClient::addContents, which is what puts
/// the item into the gump the player is looking at. Both engines here added the deed
/// server-side and sent nothing, so from the deck it read as "I dry-docked the ship,
/// the deed was not in my bag, I closed the bag and reopened it and there it was".
///
/// These pin the notification itself rather than the packet: the engines have no
/// client, so what they owe is telling the server which character just received
/// what. The server-side wiring turns that into the container packet.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class RedeedDeliveryNotifyTests
{
    private readonly ITestOutputHelper _out;
    public RedeedDeliveryNotifyTests(ITestOutputHelper output) => _out = output;

    private static MultiRegistry ShipRegistry(ushort id = 0x4000)
    {
        var def = new MultiDef { Id = id, Name = "small boat" };
        def.Components.Add(new MultiComponent { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = false });
        def.RecalcBounds();
        var reg = new MultiRegistry();
        reg.Register(def);
        return reg;
    }

    private static Item Backpack(SphereNet.Game.World.GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        Assert.True(ch.Equip(pack, Layer.Pack));
        return pack;
    }

    [Fact]
    public void DryDockingAShipTellsTheOwnerWhereTheDeedWent()
    {
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, ShipRegistry(), null);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Str = 100;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var pack = Backpack(world, owner);

        var delivered = new List<(Character To, Item What)>();
        engine.OnDeedDelivered = (to, what) => delivered.Add((to, what));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        var deed = engine.RemoveShip(ship!.MultiItem.Uid, owner);

        _out.WriteLine($"deed {deed!.Uid.Value:X} in {deed.ContainedIn.Value:X}, " +
                       $"{delivered.Count} notification(s)");
        Assert.Equal(pack.Uid, deed.ContainedIn);
        var (to, what) = Assert.Single(delivered);
        Assert.Same(owner, to);
        Assert.Same(deed, what);
    }

    [Fact]
    public void ADeedThatFallsOnTheGroundIsNotAnnouncedAsPackContent()
    {
        // No backpack to put it in: the deed is dropped at the ship's spot, where the
        // ordinary ground view is what shows it. A container notification there would
        // point the client at a container the item is not in.
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, ShipRegistry(), null);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var delivered = new List<Item>();
        engine.OnDeedDelivered = (_, what) => delivered.Add(what);

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        var deed = engine.RemoveShip(ship!.MultiItem.Uid, owner);

        Assert.NotNull(deed);
        Assert.False(deed!.ContainedIn.IsValid);
        Assert.Empty(delivered);
    }

    [Fact]
    public void TheScriptDrivenRedeedAnnouncesItToo()
    {
        // REDEED from a script reaches the owner the same way, and a pack open at that
        // moment has the same problem.
        var world = TestHarness.CreateWorld();
        var engine = new ShipEngine(world, ShipRegistry(), null);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Str = 100;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var pack = Backpack(world, owner);

        var delivered = new List<Item>();
        engine.OnDeedDelivered = (_, what) => delivered.Add(what);

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        var deed = engine.RedeedFromScript(ship!.MultiItem.Uid);

        Assert.Equal(pack.Uid, deed!.ContainedIn);
        Assert.Same(deed, Assert.Single(delivered));
    }
}
