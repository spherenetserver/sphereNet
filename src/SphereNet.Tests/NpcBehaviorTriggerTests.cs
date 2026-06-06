using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the newly-wired @NPCRefuseItem accept-gate in the drop-on-NPC flow.
// When a player gives an item to an NPC, @ReceiveItem fires first; if it does not
// handle the gift, @NPCRefuseItem fires as the engine accept gate — RETURN 1
// bounces the item back to the giver instead of the NPC pocketing it. Default
// (no handler) accepts, preserving prior behaviour. The NPC-side trigger fires
// through the EVENTSPET global event set (TriggerDispatcher uses EVENTSPET for
// non-players), so handlers are registered there.
public class NpcBehaviorTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item AddPack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    private static (GameClient client, Character giver, Character npc, Item item) Setup(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1501);

        var giver = world.CreateCharacter();
        giver.IsPlayer = true;
        world.PlaceCharacter(giver, new Point3D(100, 100, 0, 0));
        AddPack(world, giver);
        TestHarness.AttachCharacter(client, giver);

        var npc = world.CreateCharacter(); // NPC (not a player)
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        AddPack(world, npc);

        var item = world.CreateItem();
        item.BaseId = 0x1234;
        giver.SetTag("DRAGGING", item.Uid.Value.ToString()); // HandleItemDrop precondition
        return (client, giver, npc, item);
    }

    [Fact]
    public void DropOnNpc_NpcRefuseItemReturnsTrue_BouncesItemToGiver()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPET", "NPCRefuseItem", (_, _) => TriggerResult.True);
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        Assert.Contains(giver.Backpack!.Contents, i => i.Uid == item.Uid);     // bounced back
        Assert.DoesNotContain(npc.Backpack!.Contents, i => i.Uid == item.Uid); // not pocketed
    }

    [Fact]
    public void DropOnNpc_NoRefuse_NpcAcceptsItem_AndFiresAcceptTrigger()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);

        var dispatcher = new TriggerDispatcher();
        bool accepted = false;
        dispatcher.RegisterCharEvent("EVENTSPET", "NPCAcceptItem", (_, _) => { accepted = true; return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        Assert.Contains(npc.Backpack!.Contents, i => i.Uid == item.Uid);        // accepted into pack
        Assert.DoesNotContain(giver.Backpack!.Contents, i => i.Uid == item.Uid);
        Assert.True(accepted);                                                  // @NPCAcceptItem fired
    }
}
