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

// Source-X NPC_OnItemGive contract (CCharNPCAct.cpp:2060) for the
// drop-on-NPC flow: @ReceiveItem fires first; then the NPC_WantThisItem
// gate REFUSES an unwanted gift by default — @NPCRefuseItem RETURN 1
// overrides the refusal (opens the accept path), and @NPCAcceptItem
// RETURN 1 cancels the native accept. The previous SphereNet contract
// (default-accept, RefuseItem RETURN 1 = refuse) was inverted.
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
    public void DropOnNpc_UnwantedItem_IsRefusedByDefault()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);

        var dispatcher = new TriggerDispatcher();
        bool acceptFired = false;
        dispatcher.RegisterCharEvent("EVENTSPET", "NPCAcceptItem",
            (_, _) => { acceptFired = true; return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        // No want, no refuse-override: the gift bounces back to the giver.
        Assert.Contains(giver.Backpack!.Contents, i => i.Uid == item.Uid);
        Assert.DoesNotContain(npc.Backpack!.Contents, i => i.Uid == item.Uid);
        Assert.False(acceptFired); // the accept path was never reached
    }

    [Fact]
    public void DropOnNpc_NpcRefuseItemReturn1_OverridesRefusal_NpcAccepts()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPET", "NPCRefuseItem", (_, _) => TriggerResult.True);
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        // Source-X: RETURN 1 in @NPCRefuseItem SKIPS the native refusal.
        Assert.Contains(npc.Backpack!.Contents, i => i.Uid == item.Uid);
        Assert.DoesNotContain(giver.Backpack!.Contents, i => i.Uid == item.Uid);
    }

    [Fact]
    public void DropOnNpc_WantedItem_Accepted_AndAcceptReturn1_Cancels()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);
        Character.NpcWantThisItem = (_, _) => 100; // the NPC wants it

        var dispatcher = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);
        Assert.Contains(npc.Backpack!.Contents, i => i.Uid == item.Uid); // wanted → pocketed

        // Second gift: @NPCAcceptItem RETURN 1 cancels the native accept.
        var item2 = world.CreateItem();
        item2.BaseId = 0x1234;
        giver.SetTag("DRAGGING", item2.Uid.Value.ToString());
        dispatcher.RegisterCharEvent("EVENTSPET", "NPCAcceptItem", (_, _) => TriggerResult.True);

        client.HandleItemDrop(item2.Uid.Value, 0, 0, 0, npc.Uid.Value);
        Assert.Contains(giver.Backpack!.Contents, i => i.Uid == item2.Uid);
        Assert.DoesNotContain(npc.Backpack!.Contents, i => i.Uid == item2.Uid);
    }

    [Fact]
    public void DropOnOwnPet_Food_IsEaten()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);
        npc.SetTag("OWNER_UID", giver.Uid.Value.ToString());
        npc.NpcFood = 10;
        item.ItemType = ItemType.Food;
        item.Amount = 1;

        client.SetEngines(triggerDispatcher: new TriggerDispatcher());
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        Assert.True(item.IsDeleted, "the pet did not eat the offered food");
        Assert.True(npc.NpcFood > 10, "the meal did not feed the pet");
    }

    [Fact]
    public void GoldGivenToBanker_LandsInTheGiversBankBox()
    {
        var world = CreateWorld();
        var (client, giver, npc, item) = Setup(world);
        npc.NpcBrain = NpcBrainType.Banker;
        item.ItemType = ItemType.Gold;
        item.BaseId = 0x0EED;
        item.Amount = 500;

        client.SetEngines(triggerDispatcher: new TriggerDispatcher());
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, npc.Uid.Value);

        var bank = giver.GetEquippedItem(Layer.BankBox);
        Assert.NotNull(bank);
        Assert.Contains(bank!.Contents, i => i.Uid == item.Uid); // deposited
        Assert.DoesNotContain(npc.Backpack!.Contents, i => i.Uid == item.Uid);
    }
}
