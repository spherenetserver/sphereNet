using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DestroyLifecycleParityTests
{
    [Fact]
    public void ClientDeletionUsesNoPlayerSourceAndDispatchesOnlyOnce()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); var item = world.CreateItem();
        var client = TestHarness.CreateClient(logs, world, new SphereNet.Game.Accounts.AccountManager(logs), 999);
        var triggers = new SphereNet.Game.Scripting.TriggerDispatcher();
        int calls = 0;
        triggers.RegisterItemEvent("EVENTSITEM", "Destroy", (_, args) =>
        {
            calls++; Assert.Null(args.CharSrc); Assert.Null(args.ItemSrc);
            return TriggerResult.True;
        });
        client.SetEngines(triggerDispatcher: triggers);
        world.ItemDeleteAllowed = _ => throw new InvalidOperationException("Duplicate notification");
        Assert.False(client.TryDeleteItemFromClient(item));
        Assert.False(((SphereNet.Game.Clients.IClientContext)client).TryDeleteItemFromClient(item));
        Assert.Equal(2, calls); Assert.False(item.IsDeleted);
    }

    [Fact]
    public void ContainerNotifiesBeforeContentsAndAgainWhenDeletingAcceptedChild()
    {
        var world = TestHarness.CreateWorld(); var bag = world.CreateItem(); bag.ItemType = ItemType.Container;
        var child = world.CreateItem(); bag.AddItem(child);
        var order = new List<Item>();
        world.ItemDeleteAllowed = item => { order.Add(item); return true; };
        world.DeleteObject(bag);
        Assert.Equal(new[] { bag, child, child }, order);
        Assert.True(bag.IsDeleted); Assert.True(child.IsDeleted);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("direct")]
    [InlineData("script")]
    [InlineData("consume")]
    public void VetoPreservesRegistrationEquipmentAndContents(string route)
    {
        var world = TestHarness.CreateWorld();
        var wearer = world.CreateCharacter(); wearer.PrivLevel = PrivLevel.GM;
        var bag = world.CreateItem(); bag.ItemType = ItemType.Container;
        wearer.Equip(bag, Layer.Pack);
        var child = world.CreateItem(); bag.AddItem(child);
        int calls = 0, unequips = 0;
        Item.OnItemUnequipped = (_, _) => unequips++;
        world.ItemDeleteAllowed = item => { calls++; Assert.Same(bag, item); return false; };
        if (route == "world") world.DeleteObject(bag);
        else if (route == "direct") bag.Delete();
        else bag.TryExecuteCommand(route == "script" ? "REMOVE" : "CONSUME", "", null!);
        Assert.Equal(1, calls); Assert.Equal(0, unequips);
        Assert.False(bag.IsDeleted); Assert.Same(bag, world.FindItem(bag.Uid));
        Assert.Same(bag, wearer.GetEquippedItem(Layer.Pack));
        Assert.Same(child, Assert.Single(bag.Contents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerVetoPreservesChildOnGroundOrAtScriptDestination(bool relocate)
    {
        var world = TestHarness.CreateWorld(); var bag = world.CreateItem(); bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(100, 100));
        var child = world.CreateItem(); bag.AddItem(child);
        var destination = world.CreateItem(); destination.ItemType = ItemType.Container;
        world.PlaceItem(destination, new Point3D(110, 110));
        world.ItemDeleteAllowed = item =>
        {
            if (item != child) return true;
            if (relocate) destination.AddItem(child);
            return false;
        };
        world.DeleteObject(bag);
        Assert.True(bag.IsDeleted); Assert.False(child.IsDeleted);
        Assert.Same(child, world.FindItem(child.Uid));
        if (relocate) Assert.Same(child, Assert.Single(destination.Contents));
        else { Assert.False(child.ContainedIn.IsValid); Assert.Equal(new Point3D(100, 100), child.Position); }
    }

    [Fact]
    public void ForcedRemovalStillNotifiesAndIgnoresVeto()
    {
        var world = TestHarness.CreateWorld(); var item = world.CreateItem(); int calls = 0;
        world.ItemDeleteAllowed = target => { calls++; return false; };
        Assert.True(world.TryDeleteObject(item, force: true));
        Assert.Equal(1, calls); Assert.True(item.IsDeleted); Assert.Null(world.FindItem(item.Uid));
    }

    [Fact]
    public void RecursiveRemoveDoesNotReenterOrOverrideVeto()
    {
        var world = TestHarness.CreateWorld(); var item = world.CreateItem(); int calls = 0;
        world.ItemDeleteAllowed = target => { calls++; target.Delete(); return false; };
        world.DeleteObject(item);
        Assert.Equal(1, calls); Assert.False(item.IsDeleted); Assert.Same(item, world.FindItem(item.Uid));
    }

    [Fact]
    public void DestroyPrecedesUnequipAndSeesRegisteredItem()
    {
        var world = TestHarness.CreateWorld(); var wearer = world.CreateCharacter(); wearer.PrivLevel = PrivLevel.GM;
        var item = world.CreateItem(); wearer.Equip(item, Layer.Shirt);
        var order = new List<string>();
        world.ItemDeleteAllowed = target =>
        {
            Assert.Same(item, world.FindItem(item.Uid)); Assert.True(item.IsEquipped);
            order.Add("destroy"); return true;
        };
        Item.OnItemUnequipped = (_, _) => order.Add("unequip");
        item.Delete();
        Assert.Equal(new[] { "destroy", "unequip" }, order);
        Assert.True(item.IsDeleted); Assert.Null(wearer.GetEquippedItem(Layer.Shirt));
    }
}
