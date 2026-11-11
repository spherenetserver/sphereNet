using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Chat;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Requests a client can send that the handlers took at face value. Each fix has the
/// request that abused it, refused, and the ordinary request next to it, still served.
///
/// The reference for what a handler may trust is Source-X: a resync refreshes the
/// requester only (PacketResynchronize, receive.cpp:590-606); an equip and a drop
/// answer for the item on LAYER_DRAGGING and nothing else (receive.cpp:541-547,
/// CClientEvent.cpp:283-291); "play character" plays one that exists (Setup_Play,
/// CClientMsg.cpp:2924-2926); EquipLastWeapon goes through ItemPickup
/// (receive.cpp:4139-4145); a conference needs a valid name (CChat.cpp:29-33, 437);
/// no layer but the visible ones is lifted (CCharStatus.cpp:1669-1686); and a view
/// range request is answered (receive.cpp:3498-3509).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PacketAbuseGuardTests
{
    private const ushort SwordId = 0x0F5E;
    private const ushort IngotId = 0x1BF2;

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack,
        TriggerDispatcher Triggers);

    private static Character MakePlayer(GameWorld world, string name, short x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.BodyId = 0x0190;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        ch.Dex = 100; ch.Stam = 100; ch.Int = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static Bench Setup(int id)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        VendorEngine.World = world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var me = MakePlayer(world, "Abuser", 100);
        TestHarness.AttachCharacter(client, me);
        var triggers = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: triggers);
        TestHarness.SeedWearable(SwordId, Layer.OneHanded);
        return new Bench(world, client, me, me.Backpack!, triggers);
    }

    private static Item Sword(GameWorld world)
    {
        var sword = world.CreateItem();
        sword.BaseId = SwordId;
        sword.ItemType = ItemType.WeaponSword;
        return sword;
    }

    private static Item Pile(GameWorld world, ushort amount)
    {
        var pile = world.CreateItem();
        pile.BaseId = IngotId;
        pile.Amount = amount;
        return pile;
    }

    private static List<byte[]> Sent(GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Where(p => p.Span.Length > 0).Select(p => p.Span.ToArray()).ToList();

    private static int Count(GameClient client, byte opcode) => Sent(client).Count(p => p[0] == opcode);

    private static bool IsDragging(Character ch, Item item) =>
        ch.TryGetTag("DRAGGING", out string? held) && held == item.Uid.Value.ToString();

    // ---- 1. 0x22 resync ----------------------------------------------------

    [Fact]
    public void AResyncRequestRefreshesTheRequesterAndAnnouncesNothing()
    {
        var b = Setup(9901);
        int announced = 0;
        b.Client.BroadcastCharacterAppear = _ => announced++;
        TestHarness.ClearQueuedPackets(b.Client.NetState);

        Assert.True(b.Client.HandleResyncRequest());

        Assert.Equal(0, announced);                 // no 0x78 for every viewer
        Assert.True(Count(b.Client, 0x20) >= 1);    // the requester is put where it stands
        Assert.Equal(0, b.Client.NetState.WalkSequence);
    }

    [Fact]
    public void AServerInitiatedResyncStillAnnouncesTheCharacter()
    {
        var b = Setup(9902);
        int announced = 0;
        b.Client.BroadcastCharacterAppear = _ => announced++;

        b.Client.Resync();

        Assert.Equal(1, announced);
    }

    [Fact]
    public void RepeatedResyncRequestsGetOneFullRefresh()
    {
        var b = Setup(9903);
        GameClient.ResyncRequestMinIntervalMs = 60_000;
        int announced = 0;
        b.Client.BroadcastCharacterAppear = _ => announced++;

        Assert.True(b.Client.HandleResyncRequest());
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.NetState.WalkSequence = 7;

        for (int i = 0; i < 50; i++)
            Assert.False(b.Client.HandleResyncRequest());

        // Each repeat is answered with the requester's own position alone, which is
        // what lets a client that is waiting for it walk again - nothing is rebuilt.
        var sent = Sent(b.Client);
        Assert.Equal(50, sent.Count);
        Assert.All(sent, p => Assert.Equal(0x20, p[0]));
        Assert.Equal(0, b.Client.NetState.WalkSequence);
        Assert.Equal(0, announced);
    }

    // ---- 2. 0x13 equip -----------------------------------------------------

    [Fact]
    public void ADraggedItemIsEquippedOnce_AndTheRequestCannotBeReplayed()
    {
        var b = Setup(9911);
        int equips = 0;
        b.Triggers.RegisterItemEvent("EVENTSITEM", "Equip", (_, _) => { equips++; return TriggerResult.Default; });
        var sword = Sword(b.World);
        Assert.True(b.Pack.TryAddItem(sword));

        // The ordinary flow: lift, then wear.
        b.Client.HandleItemPickup(sword.Uid.Value, 1);
        b.Client.HandleItemEquip(sword.Uid.Value, (byte)Layer.OneHanded, b.Me.Uid.Value);
        Assert.Same(sword, b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(1, equips);
        Assert.False(IsDragging(b.Me, sword));

        // The same 0x13 again, for the item that is now worn.
        long swingAt = b.Me.NextAttackTime;
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        for (int i = 0; i < 5; i++)
            b.Client.HandleItemEquip(sword.Uid.Value, (byte)Layer.OneHanded, b.Me.Uid.Value);

        Assert.Equal(1, equips);                                // @Equip did not run again
        Assert.Same(sword, b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(swingAt, b.Me.NextAttackTime);             // nor was the swing restarted
        Assert.Equal(0, Count(b.Client, 0x2E));                 // no worn-item packet
        Assert.Equal(5, Count(b.Client, 0x27));                 // the drag is cancelled instead
    }

    [Fact]
    public void AnItemParentedToTheCharacterButNotDraggedIsNotEquipped()
    {
        var b = Setup(9912);
        var sword = Sword(b.World);
        sword.ContainedIn = b.Me.Uid;       // held, on no layer, and no drag recorded

        b.Client.HandleItemEquip(sword.Uid.Value, (byte)Layer.OneHanded, b.Me.Uid.Value);

        Assert.Null(b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.False(sword.IsEquipped);
    }

    [Fact]
    public void AGhostsEquipRequestSettlesTheDrag()
    {
        var b = Setup(9913);
        var sword = Sword(b.World);
        Assert.True(b.Pack.TryAddItem(sword));
        b.Client.HandleItemPickup(sword.Uid.Value, 1);
        Assert.True(IsDragging(b.Me, sword));

        b.Me.SetStatFlag(StatFlag.Dead);
        b.Client.HandleItemEquip(sword.Uid.Value, (byte)Layer.OneHanded, b.Me.Uid.Value);

        Assert.False(sword.IsEquipped);
        Assert.False(IsDragging(b.Me, sword));
        Assert.Equal(b.Pack.Uid, sword.ContainedIn);    // back where it was lifted from
    }

    // ---- 3. vendor sell, amount 0 -------------------------------------------

    private static (Character Vendor, Item Extra) MakePlayerVendor(GameWorld world, Character owner, int gold)
    {
        var vendor = world.CreateCharacter();
        vendor.Name = "the shopkeeper";
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        Assert.True(vendor.TryAssignOwnership(owner, owner));

        var extra = world.CreateItem();
        extra.ItemType = ItemType.EqVendorBox;
        extra.BaseId = 0x09AB;
        vendor.Equip(extra, Layer.VendorExtra);

        var sample = TestHarness.GiveVendorBuySample(world, vendor, IngotId);
        sample.SetTag("OVERRIDE.VALUE", "10");
        VendorEngine.SetVendorGold(vendor, gold);
        return (vendor, extra);
    }

    [Fact]
    public void ASellLineOfNothingLeavesNothingInThePlayerVendorsBox()
    {
        var b = Setup(9921);
        var owner = MakePlayer(b.World, "Owner", 102);
        var (vendor, extra) = MakePlayerVendor(b.World, owner, 1000);
        var pile = Pile(b.World, 10);
        Assert.True(b.Pack.TryAddItem(pile));

        // Straight at the engine, as many lines as the packet cares to carry.
        var lines = Enumerable.Range(0, 20)
            .Select(_ => new TradeEntry { ItemUid = pile.Uid, ItemId = pile.BaseId, Amount = 0 }).ToList();
        Assert.Equal(0, VendorEngine.ProcessSell(b.Me, vendor, lines));
        Assert.Empty(extra.Contents);
        Assert.Equal(10, pile.Amount);
        Assert.Equal(1000, VendorEngine.GetVendorGold(vendor));

        // And through the packet handler.
        b.Client.HandleVendorSell(vendor.Uid.Value,
            [new VendorSellEntry { ItemSerial = pile.Uid.Value, Amount = 0 }]);
        Assert.Empty(extra.Contents);
        Assert.Equal(10, pile.Amount);
    }

    [Fact]
    public void APartOfAPileIsStillSoldToAPlayerVendor()
    {
        var b = Setup(9922);
        var owner = MakePlayer(b.World, "Owner", 102);
        var (vendor, extra) = MakePlayerVendor(b.World, owner, 1000);
        var pile = Pile(b.World, 10);
        Assert.True(b.Pack.TryAddItem(pile));

        int paid = VendorEngine.ProcessSell(b.Me, vendor,
            [new TradeEntry { ItemUid = pile.Uid, ItemId = pile.BaseId, Amount = 3 }]);

        Assert.True(paid > 0);
        Assert.Equal(7, pile.Amount);
        Assert.Equal(3, Assert.Single(extra.Contents).Amount);
        Assert.Equal(1000 - paid, VendorEngine.GetVendorGold(vendor));
    }

    // ---- 4. 0x5D on an empty slot -------------------------------------------

    private static (GameClient Client, Account Account, GameWorld World) LoggedIn(int id, string name)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = TestHarness.CreateClient(lf, world, accounts, id);
        var account = accounts.CreateAccount(name, "pw")!;
        client.HandleLoginRequest(account.Name, "pw");
        client.HandleGameLogin(account.Name, "pw", 1);
        return (client, account, world);
    }

    [Fact]
    public void PlayingAnEmptySlotMakesNoCharacter()
    {
        var (client, account, world) = LoggedIn(9931, "emptyslot");
        int before = world.GetAllObjects().Count();
        TestHarness.ClearQueuedPackets(client.NetState);

        for (int slot = 0; slot < 3; slot++)
            client.HandleCharSelect(slot, "Freebie");

        Assert.Null(client.Character);
        Assert.Equal(0, account.CharCount);
        Assert.Equal(before, world.GetAllObjects().Count());
        Assert.Equal(3, Count(client, 0xA9));           // shown the list again each time
        Assert.Equal(0, Count(client, 0x1B));           // and never let into the world
    }

    [Fact]
    public void ACreationRequestStillMakesTheCharacter_WithinItsBudget()
    {
        var (client, account, _) = LoggedIn(9932, "creator");

        client.PendingCharCreate = new CharCreateInfo
        {
            Name = "Maker", Race = 1, Str = 60, Dex = 60, Int = 60, Skills = [],
        };
        client.HandleCharSelect(-1, "Maker");

        Assert.NotNull(client.Character);
        Assert.True(client.Character!.IsOnline);
        Assert.Equal(1, account.CharCount);
        // The budget the creation path enforces and the empty slot walked around.
        Assert.True(client.Character.Str + client.Character.Dex + client.Character.Int <= 90);
        Assert.Null(client.PendingCharCreate);
    }

    [Fact]
    public void PlayingASlotThatHoldsACharacterEntersTheWorld()
    {
        var (client, account, world) = LoggedIn(9933, "veteran");
        var existing = MakePlayer(world, "Veteran", 100);
        existing.IsOnline = false;
        Assert.True(account.SetCharSlot(0, existing.Uid));

        client.HandleCharSelect(0, "Veteran");

        Assert.Same(existing, client.Character);
        Assert.True(existing.IsOnline);
        Assert.True(Count(client, 0x1B) >= 1);
    }

    [Fact]
    public void ACreationRequestThatWasNotActedOnDoesNotOutliveItsPacket()
    {
        var (client, account, world) = LoggedIn(9934, "stale");
        var existing = MakePlayer(world, "Online", 100);
        Assert.True(account.SetCharSlot(0, existing.Uid));
        client.HandleCharSelect(0, "Online");
        Assert.Same(existing, client.Character);

        // A creation packet while a character is being played is ignored...
        client.PendingCharCreate = new CharCreateInfo { Name = "Second", Race = 1, Skills = [] };
        client.HandleCharSelect(-1, "Second");

        // ...and leaves nothing behind for a later play request to pick up.
        Assert.Null(client.PendingCharCreate);
        Assert.Equal(1, account.CharCount);
    }

    [Fact]
    public void TheBotsCreationPacketIsReadAsOne()
    {
        // The socket bots ask for a character the way a client does when the list
        // shows the slot empty.
        byte[] list = new PacketCharList(["", ""], 2).Build().Span.ToArray();
        Assert.True(SphereNet.Game.Diagnostics.BotPacketBuilder.IsCharListSlotEmpty(list, 0));
        byte[] taken = new PacketCharList(["SphereBot1", ""], 2).Build().Span.ToArray();
        Assert.False(SphereNet.Game.Diagnostics.BotPacketBuilder.IsCharListSlotEmpty(taken, 0));
        Assert.True(SphereNet.Game.Diagnostics.BotPacketBuilder.IsCharListSlotEmpty(taken, 1));

        byte[] create = SphereNet.Game.Diagnostics.BotPacketBuilder.BuildCharCreate("SphereBot1");
        Assert.Equal(104, create.Length);

        var lf = TestHarness.CreateLoggerFactory();
        var state = TestHarness.CreateActiveNetState(lf, 9935);
        CharCreateInfo? read = null;
        state.CharCreateHandler = (_, info) => read = info;
        new PacketCreateCharacter().OnReceive(new PacketBuffer(create.AsSpan(1).ToArray()), state);

        Assert.NotNull(read);
        Assert.Equal("SphereBot1", read!.Name);
        Assert.True(read.Str + read.Dex + read.Int <= 80);
        Assert.Equal(3, read.Skills.Length);
        Assert.True(read.Skills.Sum(s => s.Value) <= 100);
    }

    // ---- 5. EquipLastWeapon (0xD7 0x1E) -------------------------------------

    private static void SendEquipLastWeapon(Bench b) =>
        b.Client.HandleEncodedCommand(EncodedCommandRegistry.EquipLastWeapon,
            b.Me.Uid.Value, new PacketBuffer(Array.Empty<byte>()));

    private static Item WieldThenDisarmInto(Bench b, Item container)
    {
        var sword = Sword(b.World);
        b.Me.Equip(sword, Layer.OneHanded);
        Assert.Equal(sword.Uid, b.Me.LastWeaponUid);
        b.Me.Unequip(Layer.OneHanded);
        Assert.True(container.TryAddItem(sword));
        return sword;
    }

    [Fact]
    public void AGhostsEquipLastWeaponLeavesTheWeaponInThePack()
    {
        var b = Setup(9941);
        var sword = WieldThenDisarmInto(b, b.Pack);
        b.Me.SetStatFlag(StatFlag.Dead);

        SendEquipLastWeapon(b);

        Assert.False(sword.IsEquipped);
        Assert.Equal(b.Pack.Uid, sword.ContainedIn);    // not parented to the ghost
        Assert.Contains(sword, b.Pack.Contents);
        Assert.False(IsDragging(b.Me, sword));
    }

    [Fact]
    public void EquipLastWeaponDoesNotReachIntoABankBoxThatIsNotOpen()
    {
        var b = Setup(9942);
        var bank = b.World.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        b.Me.Equip(bank, Layer.BankBox);
        bank.MoreP = new Point3D(500, 500, 0, 0);       // opened somewhere else
        var sword = WieldThenDisarmInto(b, bank);

        SendEquipLastWeapon(b);

        Assert.Null(b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(bank.Uid, sword.ContainedIn);
    }

    [Fact]
    public void EquipLastWeaponObeysThePickupTrigger()
    {
        var b = Setup(9943);
        b.Triggers.RegisterItemEvent("EVENTSITEM", "Pickup_Pack", (_, _) => TriggerResult.True);
        var sword = WieldThenDisarmInto(b, b.Pack);

        SendEquipLastWeapon(b);

        Assert.Null(b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(b.Pack.Uid, sword.ContainedIn);
    }

    [Fact]
    public void EquipLastWeaponStillWieldsTheWeaponFromThePack()
    {
        var b = Setup(9944);
        var sword = WieldThenDisarmInto(b, b.Pack);

        SendEquipLastWeapon(b);

        Assert.Same(sword, b.Me.GetEquippedItem(Layer.OneHanded));
        Assert.False(IsDragging(b.Me, sword));
    }

    // ---- 6. secure trade and a vendor sale -----------------------------------

    private static (Bench B, Character Partner, SecureTrade Trade, TradeManager Manager) OpenTrade(int id)
    {
        var b = Setup(id);
        var partner = MakePlayer(b.World, "Partner", 101);
        partner.IsOnline = true;
        var manager = new TradeManager();
        TestHarness.SetPrivateField(b.Client, "_tradeManager", manager);
        // The wiring the server installs (Program.EngineWiring).
        Item.OnTradeWindowChanged = w => manager.FindByContainer(w.Uid.Value)?.ResetAcceptance();
        Assert.True(b.Client.InitiateTrade(partner));
        return (b, partner, manager.FindTradeFor(b.Me)!, manager);
    }

    [Fact]
    public void WhatIsOfferedInATradeIsNotSoldToAVendor()
    {
        var (b, partner, trade, _) = OpenTrade(9951);
        var owner = MakePlayer(b.World, "Owner", 102);
        var (vendor, extra) = MakePlayerVendor(b.World, owner, 1000);
        var offered = Pile(b.World, 10);
        Assert.True(trade.InitiatorContainer.TryAddItem(offered));
        trade.SetAccept(partner, true);
        trade.SetAccept(b.Me, false);
        trade.SetAccept(partner, true);
        Assert.True(trade.PartnerAccepted);

        // Selling nine of the ten out from under the partner's check mark.
        Assert.Equal(0, VendorEngine.ProcessSell(b.Me, vendor,
            [new TradeEntry { ItemUid = offered.Uid, ItemId = offered.BaseId, Amount = 9 }]));
        b.Client.HandleVendorSell(vendor.Uid.Value,
            [new VendorSellEntry { ItemSerial = offered.Uid.Value, Amount = 9 }]);

        Assert.Equal(10, offered.Amount);
        Assert.Empty(extra.Contents);
        Assert.Equal(1000, VendorEngine.GetVendorGold(vendor));
        Assert.Contains(offered, trade.InitiatorContainer.Contents);
    }

    [Fact]
    public void APileThatChangesInsideATradeWindowClearsTheAcceptance()
    {
        var (b, partner, trade, _) = OpenTrade(9952);
        var offered = Pile(b.World, 10);
        Assert.True(trade.InitiatorContainer.TryAddItem(offered));
        trade.SetAccept(partner, true);
        Assert.True(trade.PartnerAccepted);

        offered.Amount = 1;     // any path: a script, a consume, a sale

        Assert.False(trade.PartnerAccepted);
        Assert.False(trade.InitiatorAccepted);
    }

    [Fact]
    public void TakingSomethingOutOfABagInTheTradeWindowClearsTheAcceptance()
    {
        var (b, partner, trade, _) = OpenTrade(9953);
        var bag = b.World.CreateItem();
        bag.ItemType = ItemType.Container; bag.BaseId = 0x0E76;
        var prize = Pile(b.World, 1);
        Assert.True(bag.TryAddItem(prize));
        Assert.True(trade.InitiatorContainer.TryAddItem(bag));
        trade.SetAccept(partner, true);
        Assert.True(trade.PartnerAccepted);

        // The offer's owner lifts the prize out of the bag; the bag stays offered.
        b.Client.HandleItemPickup(prize.Uid.Value, 1);

        Assert.True(IsDragging(b.Me, prize));
        Assert.False(trade.PartnerAccepted);
    }

    [Fact]
    public void APileOutsideAnyTradeChangesWithoutTouchingOne()
    {
        var (b, partner, trade, _) = OpenTrade(9954);
        var mine = Pile(b.World, 10);
        Assert.True(b.Pack.TryAddItem(mine));
        trade.SetAccept(partner, true);

        mine.Amount = 4;

        Assert.True(trade.PartnerAccepted);
    }

    // ---- 7. chat conference names --------------------------------------------

    private sealed record ChatBench(ChatEngine Chat, GameClient Alice, Character AliceChar,
        List<(Serial To, PacketWriter Packet)> Announced);

    private static ChatBench ChatSetup(int id)
    {
        var world = TestHarness.CreateWorld();
        var chat = new ChatEngine();
        var announced = new List<(Serial, PacketWriter)>();
        var lf = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        client.SetEngines(chatEngine: chat);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Alice";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        client.SendToChar = (to, packet) => announced.Add((to, packet));
        client.HandleChatOpen();
        announced.Clear();
        TestHarness.ClearQueuedPackets(client.NetState);
        return new ChatBench(chat, client, ch, announced);
    }

    private static ushort ChatCommand(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(3));

    public static IEnumerable<object[]> BadConferenceNames() =>
    [
        ["SYSTEM"],
        ["system"],
        [new string('A', 4000)],
        ["Bad\u0007Name"],
        ["tab\there"],
        ["<b>shout</b>"],
    ];

    [Theory]
    [MemberData(nameof(BadConferenceNames))]
    public void AConferenceIsNotCreatedUnderAnInvalidName(string name)
    {
        var c = ChatSetup(9961);

        c.Alice.HandleChatAction(0x63, name);

        Assert.Null(c.Chat.GetMemberChannel(c.AliceChar.Uid));
        Assert.Null(c.Chat.GetChannel(name));
        Assert.Empty(c.Announced);      // nothing went out to the other chat windows
        var said = Assert.Single(Sent(c.Alice), p => p[0] == 0xB2);
        Assert.Equal(PacketChatSystem.InvalidConferenceName, ChatCommand(said));
    }

    [Fact]
    public void AConferenceIsStillCreatedUnderAnOrdinaryName()
    {
        var c = ChatSetup(9962);

        c.Alice.HandleChatAction(0x63, "Trade Hall 2");

        Assert.Equal("Trade Hall 2", c.Chat.GetMemberChannel(c.AliceChar.Uid)?.Name);
        Assert.Contains(c.Announced, a => ChatCommand(a.Packet.Build().Span.ToArray()) == PacketChatSystem.CreateChannel);
    }

    [Fact]
    public void AConferenceIsNotRenamedToAnInvalidName_AndIsToAValidOne()
    {
        var c = ChatSetup(9963);
        c.Alice.HandleChatAction(0x63, "Bazaar");
        c.Announced.Clear();
        TestHarness.ClearQueuedPackets(c.Alice.NetState);

        c.Alice.HandleChatAction(0x64, "SYSTEM");
        c.Alice.HandleChatAction(0x64, new string('B', 500));

        Assert.Equal("Bazaar", c.Chat.GetMemberChannel(c.AliceChar.Uid)?.Name);
        Assert.Empty(c.Announced);
        Assert.Equal(2, Sent(c.Alice).Count(p => p[0] == 0xB2 &&
            ChatCommand(p) == PacketChatSystem.InvalidConferenceName));

        c.Alice.HandleChatAction(0x64, "Market");
        Assert.Equal("Market", c.Chat.GetMemberChannel(c.AliceChar.Uid)?.Name);

        // The engine holds the rule as well, whoever calls it.
        Assert.Null(c.Chat.Rename(c.AliceChar.Uid, "SYSTEM"));
        Assert.Null(c.Chat.Join(new Serial(0x7777), "no\u0001good", create: true));
    }

    // ---- 8. crafting gump buttons ---------------------------------------------

    private static CraftRecipe? PendingCraft(GameClient client) =>
        (CraftRecipe?)typeof(ClientWorldFeaturesHandler)
            .GetField("_pendingCraftRecipe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client.WorldFeatures);

    private static (Bench B, List<CraftRecipe> Recipes) CraftSetup(int id)
    {
        var b = Setup(id);
        var engine = new CraftingEngine(b.World);
        for (int i = 0; i < 20; i++)
            engine.RegisterRecipe(new CraftRecipe
            {
                ResultDefId = 0x4000 + i,
                ResultItemId = (ushort)(0x4000 + i),
                ResultName = "widget " + i,
                PrimarySkill = SkillType.Tinkering,
            });
        b.Client.SetEngines(skillHandlers: new SkillHandlers(b.World), craftingEngine: engine,
            triggerDispatcher: b.Triggers);
        // No [SKILLMENU] in this pack-less bench: the engine's own recipe list opens.
        b.Client.OpenCraftingGump(SkillType.Tinkering);
        Assert.True(b.Client.Gumps.Callbacks.ContainsKey(0));
        return (b, engine.GetRecipesBySkill(SkillType.Tinkering));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]       // wrapped to a negative index and threw
    [InlineData(0x80000064u)]
    [InlineData(100u + 15u)]        // a row the first page does not show
    [InlineData(100u + 19u)]
    [InlineData(5000u)]
    public void ACraftButtonOutsideTheShownPageStartsNothing(uint button)
    {
        var (b, _) = CraftSetup(9971);

        var thrown = Record.Exception(() =>
            b.Client.HandleGumpResponse(b.Me.Uid.Value, 0, button, [], []));

        Assert.Null(thrown);
        Assert.Null(PendingCraft(b.Client));
    }

    [Fact]
    public void ACraftButtonOnTheShownPageStartsThatRecipe()
    {
        var (b, recipes) = CraftSetup(9972);

        b.Client.HandleGumpResponse(b.Me.Uid.Value, 0, 100u + 2u, [], []);

        Assert.Same(recipes[2], PendingCraft(b.Client));
    }

    // ---- 9. the trade container itself ----------------------------------------

    [Fact]
    public void TheTradeContainerItselfCannotBeLifted()
    {
        var (b, _, trade, _) = OpenTrade(9981);
        var window = trade.InitiatorContainer;
        var offered = Pile(b.World, 3);
        Assert.True(window.TryAddItem(offered));

        Assert.False(ItemMoveRules.CanMove(b.Me, window, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.ItemLayerLocked, denial);

        b.Client.HandleItemPickup(window.Uid.Value, 1);

        Assert.False(IsDragging(b.Me, window));
        Assert.True(window.IsEquipped);
        Assert.Equal(Layer.Special, window.EquipLayer);
        Assert.Contains(offered, window.Contents);
    }

    [Fact]
    public void TheOwnerStillTakesAnItemOutOfTheirOwnTradeWindow()
    {
        var (b, _, trade, _) = OpenTrade(9982);
        var offered = Pile(b.World, 3);
        Assert.True(trade.InitiatorContainer.TryAddItem(offered));

        Assert.True(ItemMoveRules.CanMove(b.Me, offered, out _));
        b.Client.HandleItemPickup(offered.Uid.Value, 3);

        Assert.True(IsDragging(b.Me, offered));
        Assert.DoesNotContain(offered, trade.InitiatorContainer.Contents);
    }

    // ---- 10. 0xC8 view range ---------------------------------------------------

    [Theory]
    [InlineData(10, 10)]
    [InlineData(18, 18)]
    [InlineData(200, 24)]       // clamped, and the client is told the clamped value
    [InlineData(0, 4)]
    public void AViewRangeRequestIsAnsweredWithTheRangeInForce(byte asked, byte expected)
    {
        var b = Setup(9991);
        TestHarness.ClearQueuedPackets(b.Client.NetState);

        b.Client.HandleViewRange(asked);

        Assert.Equal(expected, b.Client.NetState.ViewRange);
        var answer = Assert.Single(Sent(b.Client), p => p[0] == 0xC8);
        Assert.Equal(new byte[] { 0xC8, expected }, answer);
    }

    // ---- 11. smaller ones --------------------------------------------------------

    [Fact]
    public void CrashReportsAreActedOnOncePerInterval()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var state = TestHarness.CreateActiveNetState(lf, 9995);
        int acted = 0;
        state.CrashReportHandler = _ => acted++;
        NetState.CrashReportMinIntervalMs = 60_000;

        for (int i = 0; i < 200; i++)
            state.OnCrashReport();
        Assert.Equal(1, acted);         // the first one is still heard

        NetState.CrashReportMinIntervalMs = 0;
        state.OnCrashReport();
        state.OnCrashReport();
        Assert.Equal(3, acted);
    }

    [Fact]
    public void PartyTextIsCutToTheSpeechBound()
    {
        var world = TestHarness.CreateWorld();
        var parties = new PartyManager();
        var lf = TestHarness.CreateLoggerFactory();
        var accounts = new AccountManager(lf);
        var sent = new List<(Serial To, PacketWriter Packet)>();

        GameClient Player(int port, string name, short x, out Character ch)
        {
            var client = TestHarness.CreateClient(lf, world, accounts, port);
            client.SetEngines(partyManager: parties);
            ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = name;
            ch.IsOnline = true;
            world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch);
            client.SendToChar = (to, packet) => sent.Add((to, packet));
            return client;
        }

        var alice = Player(9996, "Alice", 100, out var aliceChar);
        var bob = Player(9997, "Bob", 101, out var bobChar);
        world.SetGameClockMs(world.GameClockMs + 6000);
        alice.HandlePartyInvite(bobChar.Uid.Value);
        bob.HandleExtendedCommand(0x0006,
        [
            8, (byte)(aliceChar.Uid.Value >> 24), (byte)(aliceChar.Uid.Value >> 16),
            (byte)(aliceChar.Uid.Value >> 8), (byte)aliceChar.Uid.Value,
        ]);
        Assert.NotNull(parties.FindParty(bobChar.Uid));
        sent.Clear();

        byte[] text = Encoding.BigEndianUnicode.GetBytes(new string('x', 2000));

        // Public party text: [4][text].
        alice.HandleExtendedCommand(0x0006, [4, .. text]);
        // A tell: [3][target uid][text].
        alice.HandleExtendedCommand(0x0006,
        [
            3, (byte)(bobChar.Uid.Value >> 24), (byte)(bobChar.Uid.Value >> 16),
            (byte)(bobChar.Uid.Value >> 8), (byte)bobChar.Uid.Value, .. text,
        ]);

        var messages = sent.Where(s => s.Packet is PacketPartyMessage).ToList();
        Assert.Equal(3, messages.Count);    // both members, then the tell
        // 0xBF header (3) + sub (2) + kind (1) + serial (4) + 256 chars + terminator.
        Assert.All(messages, m => Assert.Equal(3 + 2 + 1 + 4 + 256 * 2 + 2, m.Packet.Build().Span.Length));

        // An ordinary line arrives whole.
        sent.Clear();
        alice.HandleExtendedCommand(0x0006, [4, .. Encoding.BigEndianUnicode.GetBytes("hello")]);
        Assert.All(sent.Where(s => s.Packet is PacketPartyMessage),
            m => Assert.Equal(3 + 2 + 1 + 4 + 5 * 2 + 2, m.Packet.Build().Span.Length));
        Assert.Equal(2, sent.Count(s => s.Packet is PacketPartyMessage));
    }

    [Fact]
    public void DroppingADraggedItemThatWasSoldWholeClearsTheCursor()
    {
        var b = Setup(9998);
        var vendor = b.World.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        b.World.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        TestHarness.GiveVendorBuySample(b.World, vendor, IngotId).SetTag("OVERRIDE.VALUE", "10");
        VendorEngine.SetVendorGold(vendor, 1000);
        var pile = Pile(b.World, 5);
        Assert.True(b.Pack.TryAddItem(pile));
        uint serial = pile.Uid.Value;

        b.Client.HandleItemPickup(serial, 5);
        Assert.True(IsDragging(b.Me, pile));
        Assert.True(VendorEngine.ProcessSell(b.Me, vendor,
            [new TradeEntry { ItemUid = pile.Uid, ItemId = pile.BaseId, Amount = 5 }]) > 0);
        Assert.Null(b.World.FindItem(new Serial(serial)));

        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.HandleItemDrop(serial, 50, 50, 0, b.Pack.Uid.Value);

        Assert.False(b.Me.TryGetTag("DRAGGING", out _));
        Assert.Equal(1, Count(b.Client, 0x27));     // the client's cursor is cancelled
        Assert.Contains(Sent(b.Client), p => p[0] == 0x1D &&
            BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)) == serial);
    }

    [Fact]
    public void ADraggedItemSoldToAPlayerVendorCannotBeDroppedBackOut()
    {
        var b = Setup(9999);
        var owner = MakePlayer(b.World, "Owner", 102);
        var (vendor, extra) = MakePlayerVendor(b.World, owner, 1000);
        var pile = Pile(b.World, 5);
        Assert.True(b.Pack.TryAddItem(pile));

        b.Client.HandleItemPickup(pile.Uid.Value, 5);
        int paid = VendorEngine.ProcessSell(b.Me, vendor,
            [new TradeEntry { ItemUid = pile.Uid, ItemId = pile.BaseId, Amount = 5 }]);
        Assert.True(paid > 0);
        Assert.Contains(pile, extra.Contents);      // a player's vendor keeps what it buys

        // The drag is still recorded, and the item is no longer on the cursor: onto
        // the ground at the seller's feet, or into the seller's pack.
        b.Client.HandleItemDrop(pile.Uid.Value, 100, 101, 0, 0xFFFFFFFF);
        b.Client.HandleItemDrop(pile.Uid.Value, 50, 50, 0, b.Pack.Uid.Value);

        Assert.Contains(pile, extra.Contents);      // paid for, and it stays sold
        Assert.Equal(extra.Uid, pile.ContainedIn);
        Assert.DoesNotContain(pile, b.Pack.Contents);
        Assert.False(b.Me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void AnOrdinaryPickupAndDropStillMovesTheItem()
    {
        var b = Setup(10000);
        var bag = b.World.CreateItem();
        bag.ItemType = ItemType.Container; bag.BaseId = 0x0E76;
        Assert.True(b.Pack.TryAddItem(bag));
        var pile = Pile(b.World, 5);
        Assert.True(b.Pack.TryAddItem(pile));

        b.Client.HandleItemPickup(pile.Uid.Value, 5);
        b.Client.HandleItemDrop(pile.Uid.Value, 50, 50, 0, bag.Uid.Value);

        Assert.Contains(pile, bag.Contents);
        Assert.False(b.Me.TryGetTag("DRAGGING", out _));
    }
}
