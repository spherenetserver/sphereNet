using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class UnequipLifecycleParityTests
{
    private sealed class Console(SphereNet.Game.Objects.Characters.Character wearer) : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.GM;
        public string GetName() => "test";
        public void SysMessage(string text) { }
        public SphereNet.Core.Interfaces.IScriptObj GetSourceChar() => wearer;
    }
    [Theory]
    [InlineData("direct")]
    [InlineData("container")]
    [InlineData("delete")]
    [InlineData("script")]
    public void EveryRemovalNotifiesOnceWhileItemStillBelongsToWearer(string route)
    {
        var world = TestHarness.CreateWorld();
        var wearer = world.CreateCharacter(); wearer.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(wearer, new Point3D(100, 100));
        var pack = world.CreateItem(); pack.ItemType = ItemType.Container; wearer.Equip(pack, Layer.Pack);
        var item = world.CreateItem(); wearer.Equip(item, Layer.Shirt);
        int calls = 0; bool equipped = false, registered = false, correctSource = false;
        Item.OnItemUnequipped = (removed, source) =>
        {
            if (removed != item) return;
            calls++; equipped = removed.IsEquipped && wearer.GetEquippedItem(Layer.Shirt) == item;
            registered = world.FindItem(item.Uid) == item;
            correctSource = source == wearer && item.ContainedIn == wearer.Uid;
        };
        if (route == "direct") wearer.Unequip(Layer.Shirt);
        else if (route == "container") pack.AddItem(item);
        else if (route == "delete") world.RemoveItem(item);
        else Assert.True(item.TryExecuteCommand("UNEQUIP", "", new Console(wearer)));
        Assert.Equal(1, calls); Assert.True(equipped); Assert.True(registered); Assert.True(correctSource);
        Assert.Null(wearer.GetEquippedItem(Layer.Shirt));
    }

    [Fact]
    public void RecursiveRemovalDoesNotRepeatTheTriggerOrEraseReplacement()
    {
        var world = TestHarness.CreateWorld(); var wearer = world.CreateCharacter();
        wearer.PrivLevel = PrivLevel.GM;
        var item = world.CreateItem(); wearer.Equip(item, Layer.Shirt);
        var replacement = world.CreateItem(); int calls = 0;
        Item.OnItemUnequipped = (removed, owner) =>
        {
            if (removed != item) return;
            calls++;
            if (calls > 1) return;
            owner.Unequip(Layer.Shirt);
            owner.Equip(replacement, Layer.Shirt);
        };
        wearer.Unequip(Layer.Shirt);
        Assert.Equal(1, calls);
        Assert.Same(replacement, wearer.GetEquippedItem(Layer.Shirt));
    }
    [Theory]
    [InlineData("container")]
    [InlineData("equip")]
    [InlineData("ground")]
    [InlineData("delete")]
    public void TriggerDeletingItsOwnItemDoesNotReinsertItOrRunTwice(string route)
    {
        var world = TestHarness.CreateWorld(); var wearer = world.CreateCharacter();
        wearer.PrivLevel = PrivLevel.GM; world.PlaceCharacter(wearer, new Point3D(100, 100));
        var item = world.CreateItem(); wearer.Equip(item, Layer.Shirt);
        var bag = world.CreateItem(); bag.ItemType = ItemType.Container;
        int calls = 0;
        Item.OnItemUnequipped = (removed, _) => { if (removed == item) { calls++; item.RemoveFromWorld(); } };
        if (route == "container") Assert.False(bag.TryAddItem(item));
        else if (route == "equip") Assert.False(wearer.Equip(item, Layer.Robe));
        else if (route == "ground") Assert.False(world.PlaceItem(item, new Point3D(101, 100)));
        else world.RemoveItem(item);
        Assert.Equal(1, calls); Assert.True(item.IsDeleted);
        Assert.Null(world.FindItem(item.Uid));
        Assert.Null(wearer.GetEquippedItem(Layer.Shirt));
        Assert.Null(wearer.GetEquippedItem(Layer.Robe));
        Assert.Empty(bag.Contents);
    }

    [Fact]
    public void DraggingLayerDoesNotFireUnequip()
    {
        var world = TestHarness.CreateWorld(); var wearer = world.CreateCharacter(); wearer.PrivLevel = PrivLevel.GM;
        var item = world.CreateItem(); wearer.Equip(item, Layer.Dragging);
        int calls = 0; Item.OnItemUnequipped = (_, _) => calls++;
        wearer.Unequip(Layer.Dragging);
        Assert.Equal(0, calls);
    }
    [Fact]
    public void EquippingTheSameItemOnItsCurrentLayerDoesNotRemoveIt()
    {
        var world = TestHarness.CreateWorld(); var wearer = world.CreateCharacter(); wearer.PrivLevel = PrivLevel.GM;
        var item = world.CreateItem(); wearer.Equip(item, Layer.Shirt);
        int calls = 0; Item.OnItemUnequipped = (_, _) => calls++;
        Assert.True(wearer.Equip(item, Layer.Shirt));
        Assert.Equal(0, calls);
        Assert.Same(item, wearer.GetEquippedItem(Layer.Shirt));
    }
}
