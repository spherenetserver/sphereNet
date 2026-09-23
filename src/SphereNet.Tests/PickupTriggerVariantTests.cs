using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Network.State;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// HandleItemPickup runs the lift triggers the way Source-X CChar::ItemPickup does
// (CCharAct.cpp:2967-3020):
//   Ground - loose on the ground: @PickUp_Ground on the item, ARGN1 = amount;
//   Pack   - out of a container: @PickUp_Pack on the item, ARGN1 = amount, then
//            @PickUp_Self on the CONTAINER with ARGO = the item;
//   worn   - no pickup trigger, the item's @Unequip runs instead;
//   Stack  - a partial lift ALSO runs @PickUp_Stack on the lifted pile, ARGO = the
//            pile left behind; RETURN 1 keeps the pile whole.
public class PickupTriggerVariantTests
{
    private static GameWorld CreateWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static SphereNet.Game.Clients.GameClient MakeClient(GameWorld world, Character ch,
        TriggerDispatcher dispatcher)
    {
        var lf = LoggerFactory.Create(_ => { });
        var netState = new NetState(lf.CreateLogger<NetState>()) { Id = 7001 };
        typeof(NetState).GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world,
            new AccountManager(lf), lf.CreateLogger<SphereNet.Game.Clients.GameClient>());
        client.SetEngines(triggerDispatcher: dispatcher);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        return client;
    }

    private sealed record Fire(string Name, SphereNet.Core.Interfaces.IScriptObj Obj, TriggerArgs Args);

    // Records every Pickup_*/Unequip fire by registering the names globally.
    private static (SphereNet.Game.Clients.GameClient client, Character ch, GameWorld world,
        List<Fire> fired, TriggerDispatcher dispatcher) Setup()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM; // bypass distance/access gates
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        var fired = new List<Fire>();
        foreach (var name in new[] { "Pickup_Ground", "Pickup_Pack", "Pickup_Self", "Pickup_Stack", "Unequip" })
            dispatcher.RegisterItemEvent("EVENTSITEM", name, (obj, args) =>
            {
                fired.Add(new Fire(name, obj, args));
                return TriggerResult.Default;
            });

        var client = MakeClient(world, ch, dispatcher);
        return (client, ch, world, fired, dispatcher);
    }

    [Fact]
    public void LooseGroundItem_FiresPickupGround_WithTheAmount()
    {
        var (client, ch, world, fired, _) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, ch.Position);

        client.HandleItemPickup(item.Uid.Value, 1);

        var f = Assert.Single(fired);
        Assert.Equal("Pickup_Ground", f.Name);
        Assert.Same(item, f.Obj);
        Assert.Equal(1, f.Args.N1);
    }

    [Fact]
    public void ItemInsideContainer_FiresPickupPackOnItem_ThenPickupSelfOnContainer()
    {
        var (client, ch, world, fired, _) = Setup();
        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        world.PlaceItem(container, ch.Position);
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        container.AddItem(item);

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.Equal(["Pickup_Pack", "Pickup_Self"], fired.Select(f => f.Name));
        Assert.Same(item, fired[0].Obj);
        Assert.Same(container, fired[1].Obj);          // the CONTAINER hears Pickup_Self
        Assert.Same(item, fired[1].Args.O1);           // ...with the item as ARGO
    }

    [Fact]
    public void PickupSelfReturn1_KeepsTheItemInItsContainer()
    {
        var (client, ch, world, _, dispatcher) = Setup();
        dispatcher.RegisterItemEvent("EVENTSITEM", "Pickup_Self", (_, _) => TriggerResult.True);
        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        world.PlaceItem(container, ch.Position);
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        container.AddItem(item);

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.Equal(container.Uid, item.ContainedIn);
        Assert.False(ch.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void EquippedItem_FiresUnequip_AndNoPickupTrigger()
    {
        var (client, ch, world, fired, _) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x1F03;
        ch.Equip(item, Layer.Shirt);

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.DoesNotContain(fired, f => f.Name.StartsWith("Pickup_"));
        Assert.Contains(fired, f => f.Name == "Unequip" && ReferenceEquals(f.Obj, item));
    }

    [Fact]
    public void PartialStackSplit_FiresPickupGroundAndPickupStack_WithTheLeftoverAsArgo()
    {
        var (client, ch, world, fired, _) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x0EED; // gold-like stackable
        item.Amount = 10;
        world.PlaceItem(item, ch.Position);

        client.HandleItemPickup(item.Uid.Value, 3); // partial → split

        Assert.Equal(["Pickup_Ground", "Pickup_Stack"], fired.Select(f => f.Name));
        Assert.Equal(3, fired[0].Args.N1);
        Assert.True(ch.TryGetTag("DRAGGING", out var dragging));
        Assert.Equal(item.Uid.Value.ToString(), dragging);
        Assert.Equal(3, item.Amount); // clicked serial remains the dragged stack

        var remainder = world.GetSector(ch.Position)!.Items.Single(i => i.Uid != item.Uid);
        Assert.Equal(7, remainder.Amount); // newly created leftover stays behind
        Assert.Same(item, fired[1].Obj);
        Assert.Same(remainder, fired[1].Args.O1);
    }

    [Fact]
    public void PickupStackReturn1_LeavesThePileWhole()
    {
        var (client, ch, world, _, dispatcher) = Setup();
        dispatcher.RegisterItemEvent("EVENTSITEM", "Pickup_Stack", (_, _) => TriggerResult.True);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.Amount = 10;
        world.PlaceItem(item, ch.Position);

        client.HandleItemPickup(item.Uid.Value, 3);

        Assert.Equal(10, item.Amount);
        Assert.False(item.ContainedIn.IsValid);
        Assert.False(ch.TryGetTag("DRAGGING", out _));
        Assert.Single(world.GetSector(ch.Position)!.Items, i => !i.IsDeleted && i.BaseId == 0x0EED);
    }
}
