using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The vendor's BUY side and its money, against Source-X:
/// NPC_FindVendableItem (CCharNPCStatus.cpp:603) matches an offered item to a BUY
/// sample by full definition and TYPE; the sample's OVERRIDE.VALUE prices the sale
/// (CClientEvent.cpp:1488); IsValidSaleItem refuses NEWBIE / MOVE_NEVER goods
/// (CItemVendable.cpp:237); the sell list walks searchable sub-containers
/// (send.cpp:3036) and OF_VendorStockLimit caps it and consumes the sample; the
/// make value is read linearly past quality 100 (CItemBase.cpp:1026); a deed is
/// worth what it deeds (CItemVendable.cpp:205); a hairdresser equips and a
/// figurine makes a pet (CClientEvent.cpp:1304); the purse is the bank box's MORE1
/// (CCharNPC.cpp:198); virtual gold widens the cost ceiling (CClientEvent.cpp:1164).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorEconomyParityTests
{
    private const string Defs = """
        [ITEMDEF 01bf2]
        DEFNAME=i_audit_ingot_base
        TYPE=t_ingot
        VALUE=5

        [ITEMDEF i_audit_iron]
        ID=01bf2
        TYPE=t_ingot
        VALUE=10

        [ITEMDEF i_audit_rare]
        ID=01bf2
        TYPE=t_ingot
        VALUE=10

        [ITEMDEF i_audit_ranged]
        ID=01bf2
        TYPE=t_ingot
        VALUE=10,20

        [ITEMDEF i_audit_falling]
        ID=01bf2
        TYPE=t_ingot
        VALUE=20,10

        [ITEMDEF 014ef]
        DEFNAME=i_audit_deed
        TYPE=t_deed
        VALUE=10

        [ITEMDEF i_audit_prize]
        ID=0f0e
        VALUE=500

        [MULTIDEF 068]
        DEFNAME=m_audit_house
        TYPE=t_multi
        VALUE=36750

        [TEMPLATE vendor_b_audit]
        BUY=i_audit_rare,{2 2}

        [TEMPLATE vendor_b_audit_priced]
        BUY=i_audit_rare
        TAG.OVERRIDE.VALUE=90
        BUY=i_audit_iron
        """;

    private sealed class Env
    {
        public required GameWorld World { get; init; }
        public int Index(string defname) => DefinitionLoader.StaticResources!.ResolveDefName(defname).Index;

        /// <summary>An item made from its definition the way the engine makes one.</summary>
        public Item Make(string defname, ushort amount = 1)
        {
            var item = World.CreateItem();
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, Index(defname)));
            item.Amount = amount;
            return item;
        }
    }

    private static Env Setup()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_vendecon_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, Defs);
        try
        {
            stack.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        }
        finally { File.Delete(path); }
        var world = TestHarness.CreateWorld();
        VendorEngine.World = world;
        return new Env { World = world };
    }

    private static Character MakeVendor(GameWorld world, int markup = 0)
    {
        var vendor = world.CreateCharacter();
        vendor.Name = "vendor";
        vendor.NpcBrain = NpcBrainType.Vendor;
        vendor.SetTag("VENDORMARKUP", markup.ToString());
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));
        return vendor;
    }

    private static Character MakePlayer(GameWorld world, int gold = 0, ushort body = 0x0190)
    {
        var player = world.CreateCharacter();
        player.Name = "player";
        player.IsPlayer = true;
        player.BodyId = body;
        player.Str = short.MaxValue;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;
        if (gold > 0)
        {
            var coins = world.CreateItem();
            coins.BaseId = 0x0EED;
            coins.ItemType = ItemType.Gold;
            coins.Amount = (ushort)gold;
            pack.AddItem(coins);
        }
        return player;
    }

    /// <summary>A BUY sample in the vendor's BUYS box.</summary>
    private static Item AddSample(Env env, Character vendor, string defname, ushort amount = 1)
    {
        var box = VendorEngine.GetVendorBox(vendor, Layer.VendorBuy)!;
        var sample = env.Make(defname, amount);
        Assert.True(box.TryAddItem(sample));
        return sample;
    }

    private static (GameClient Client, NetState State) Connect(GameWorld world, Character player)
    {
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 77);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, player);
        // The confirmation follows the list at once here; BUYSELLTIME would read that
        // as an agent (receive.cpp:1906).
        GameClient.AllowBuySellAgent = true;
        return (client, state);
    }

    /// <summary>The rows of the 0x9E sell list sent to the client, or null when no
    /// list went out.</summary>
    private static List<(uint Serial, ushort Amount, ushort Price)>? SentSellList(NetState state)
    {
        var packet = TestHarness.GetQueuedPackets(state).LastOrDefault(p => p.Span[0] == 0x9E);
        if (packet == null)
            return null;
        var s = packet.Span;
        int count = (s[7] << 8) | s[8];
        int pos = 9;
        var rows = new List<(uint, ushort, ushort)>();
        for (int i = 0; i < count; i++)
        {
            uint serial = (uint)((s[pos] << 24) | (s[pos + 1] << 16) | (s[pos + 2] << 8) | s[pos + 3]);
            ushort amount = (ushort)((s[pos + 8] << 8) | s[pos + 9]);
            ushort price = (ushort)((s[pos + 10] << 8) | s[pos + 11]);
            int nameLen = (s[pos + 12] << 8) | s[pos + 13];
            rows.Add((serial, amount, price));
            pos += 14 + nameLen;
        }
        return rows;
    }

    private static int Sell(Character player, Character vendor, Item item, int amount) =>
        VendorEngine.ProcessSell(player, vendor,
            [new TradeEntry { ItemUid = item.Uid, ItemId = item.BaseId, Amount = amount }]);

    // ---- E05: full itemdef identity, not the shared graphic ----

    [Fact]
    public void E05_TwoDefinitionsSharingOneGraphic_AreDifferentGoods()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var iron = env.Make("i_audit_iron", 5);
        player.Backpack!.AddItem(iron);
        Assert.Equal(iron.BaseId, env.Make("i_audit_rare").BaseId);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Null(SentSellList(state));                 // nothing the vendor wants

        Assert.Equal(0, Sell(player, vendor, iron, 1));   // nor through a crafted packet
        Assert.Equal(5, iron.Amount);
        Assert.Equal(1000, VendorEngine.GetVendorGold(vendor));

        var rare = env.Make("i_audit_rare", 5);
        player.Backpack.AddItem(rare);
        TestHarness.ClearQueuedPackets(state);
        client.OpenVendorSell(vendor);
        var rows = SentSellList(state);
        Assert.NotNull(rows);
        Assert.Equal(rare.Uid.Value, Assert.Single(rows!).Serial);
        Assert.Equal(10, Sell(player, vendor, rare, 1));
    }

    // ---- E10: the sample must have the same TYPE ----

    [Fact]
    public void E10_SameDefinitionButDifferentType_IsNotBought()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_iron");
        var player = MakePlayer(env.World);
        var odd = env.Make("i_audit_iron", 5);
        odd.ItemType = ItemType.Gem;
        player.Backpack!.AddItem(odd);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Null(SentSellList(state));
        Assert.Equal(0, Sell(player, vendor, odd, 1));
        Assert.Equal(5, odd.Amount);
    }

    // ---- E09: the matched sample's OVERRIDE.VALUE prices list, @Sell and payment ----

    [Fact]
    public void E09_SampleOverrideValue_PricesListTriggerAndPayment()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        var sample = AddSample(env, vendor, "i_audit_rare");
        sample.SetTag("OVERRIDE.VALUE", "90");
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 3);
        rare.SetTag("OVERRIDE.VALUE", "10");
        player.Backpack!.AddItem(rare);

        var (client, state) = Connect(env.World, player);
        long sellArg = 0;
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "Sell", (_, args) =>
        {
            sellArg = args.N2;
            return TriggerResult.Default;
        });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.OpenVendorSell(vendor);
        Assert.Equal((ushort)90, Assert.Single(SentSellList(state)!).Price);

        client.HandleVendorSell(vendor.Uid.Value, [new VendorSellEntry { ItemSerial = rare.Uid.Value, Amount = 1 }]);
        Assert.Equal(90, sellArg);                         // ARGN2 = line total at the sample's price
        Assert.Equal(2, rare.Amount);
        Assert.Equal(910, VendorEngine.GetVendorGold(vendor));
        Assert.Equal(90, VendorEngine.CountGold(player));
    }

    [Fact]
    public void E09_SampleWithoutOverride_PaysThePlayersItemValue()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 1);
        rare.SetTag("OVERRIDE.VALUE", "40");
        player.Backpack!.AddItem(rare);

        Assert.Equal(40, Sell(player, vendor, rare, 1));
    }

    // ---- E08: quality value, linear past 100, Source-X rounding ----

    [Theory]
    [InlineData("i_audit_ranged", 0, 10)]
    [InlineData("i_audit_ranged", 5, 11)]
    [InlineData("i_audit_ranged", 100, 20)]
    [InlineData("i_audit_ranged", 175, 28)]
    [InlineData("i_audit_ranged", 200, 30)]
    [InlineData("i_audit_falling", 0, 20)]
    [InlineData("i_audit_falling", 5, 19)]
    [InlineData("i_audit_falling", 100, 10)]
    [InlineData("i_audit_falling", 175, 2)]
    [InlineData("i_audit_falling", 200, 0)]
    public void E08_MakeValue_IsGetLinearAtQualityTimesTen(string defname, int quality, int expected)
    {
        var env = Setup();
        var item = env.Make(defname);
        item.Quality = (ushort)quality;
        Assert.Equal(expected, VendorEngine.GetMakeValue(item));

        // And that is what a vendor with no markup pays for it.
        var vendor = MakeVendor(env.World);
        Assert.Equal(expected, VendorEngine.GetServerSellPrice(vendor, item));
    }

    // ---- E12: NEWBIE and MOVE_NEVER goods are not sold to a vendor ----

    [Fact]
    public void E12_NewbieItem_NotListed_AndCraftedLineSkipped()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var blessed = env.Make("i_audit_rare", 5);
        blessed.SetAttr(ObjAttributes.Newbie);
        player.Backpack!.AddItem(blessed);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Null(SentSellList(state));
        Assert.False(VendorEngine.IsValidSaleItem(blessed, buyFromVendor: false));

        client.HandleVendorSell(vendor.Uid.Value, [new VendorSellEntry { ItemSerial = blessed.Uid.Value, Amount = 1 }]);
        Assert.Equal(5, blessed.Amount);
        Assert.Equal(0, VendorEngine.CountGold(player));
        Assert.Equal(0, Sell(player, vendor, blessed, 1));
        Assert.Equal(5, blessed.Amount);
    }

    [Fact]
    public void E12_MoveNeverItemInCraftedPacket_EndsTheWholeSale()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var good = env.Make("i_audit_rare", 2);
        var fixedItem = env.Make("i_audit_rare", 2);
        fixedItem.SetAttr(ObjAttributes.Move_Never);
        player.Backpack!.AddItem(good);
        player.Backpack.AddItem(fixedItem);

        int paid = VendorEngine.ProcessSell(player, vendor,
        [
            new TradeEntry { ItemUid = good.Uid, Amount = 1 },
            new TradeEntry { ItemUid = fixedItem.Uid, Amount = 1 },
        ]);
        // IsValidSaleItem(true) fails on line 2: a cheat, and the sale ends THERE.
        // Line 1 has already been taken and the purse debited, and the seller is
        // never paid for it - upstream hands the gold over after the loop
        // (CClientEvent.cpp:1463/:1556).
        Assert.Equal(0, paid);
        Assert.Equal(1, good.Amount);
        Assert.Equal(2, fixedItem.Amount);
        Assert.Equal(990, VendorEngine.GetVendorGold(vendor));
        Assert.Equal(0, VendorEngine.CountGold(player));
    }

    [Fact]
    public void Sell_UnknownSerialMidPacket_EndsTheSaleAfterTheEarlierLines()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var first = env.Make("i_audit_rare", 1);
        var after = env.Make("i_audit_rare", 1);
        player.Backpack!.AddItem(first);
        player.Backpack.AddItem(after);

        int paid = VendorEngine.ProcessSell(player, vendor,
        [
            new TradeEntry { ItemUid = first.Uid, Amount = 1 },
            new TradeEntry { ItemUid = new Serial(0x7FFFFFF0), Amount = 1 },
            new TradeEntry { ItemUid = after.Uid, Amount = 1 },
        ]);
        Assert.Equal(0, paid);
        Assert.True(first.IsDeleted);           // taken before the bad line
        Assert.False(after.IsDeleted);          // never reached
        Assert.Equal(990, VendorEngine.GetVendorGold(vendor));
        Assert.Equal(0, VendorEngine.CountGold(player));
    }

    [Fact]
    public void Sell_AnythingTheSellerHolds_IncludingTheBank_IsAccepted()
    {
        // "Do we still have it?" is GetTopLevelObj() == seller (CClientEvent.cpp:1468).
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var bank = env.World.CreateItem();
        bank.BaseId = 0x09AB;
        bank.ItemType = ItemType.EqBankBox;
        player.Equip(bank, Layer.BankBox);
        var banked = env.Make("i_audit_rare", 2);
        bank.AddItem(banked);

        var stranger = MakePlayer(env.World);
        var notMine = env.Make("i_audit_rare", 2);
        stranger.Backpack!.AddItem(notMine);

        Assert.Equal(10, Sell(player, vendor, banked, 1));
        Assert.Equal(1, banked.Amount);
        Assert.Equal(0, Sell(player, vendor, notMine, 1));   // someone else's: skipped
        Assert.Equal(2, notMine.Amount);
    }

    [Fact]
    public void Sell_MoreThanHeld_SellsWhatIsHeld()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 3);
        player.Backpack!.AddItem(rare);

        Assert.Equal(30, Sell(player, vendor, rare, 5));
        Assert.True(rare.IsDeleted);
    }

    [Fact]
    public void Sell_NonEmptyContainer_TheVendorBuys_GoesWithWhatItHolds()
    {
        // Event_VendorSell does not look inside: a bag the vendor buys is sold whole,
        // contents and all (the sell list never offers one, send.cpp:3057).
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        var sample = env.World.CreateItem();
        sample.BaseId = 0x0E76;
        sample.ItemType = ItemType.Container;
        VendorEngine.GetVendorBox(vendor, Layer.VendorBuy)!.AddItem(sample);
        var player = MakePlayer(env.World);
        var bag = env.World.CreateItem();
        bag.BaseId = 0x0E76;
        bag.ItemType = ItemType.Container;
        bag.SetTag("OVERRIDE.VALUE", "7");
        player.Backpack!.AddItem(bag);
        var inside = env.Make("i_audit_rare", 1);
        bag.AddItem(inside);

        Assert.Equal(7, Sell(player, vendor, bag, 1));
        Assert.True(bag.IsDeleted);
    }

    [Fact]
    public void Buy_LinesNamingTheSameStock_AreCombined()
    {
        // receive.cpp:790-804 adds the amounts of repeated serials together.
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.Amount = 5;
        row.SetTag("PRICE", "10");
        stock.AddItem(row);
        var player = MakePlayer(env.World, gold: 100);

        Assert.Equal(40, VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 2 }, new TradeEntry { ItemUid = row.Uid, Amount = 2 }]));
        Assert.Equal(1, row.Amount);
        Assert.Equal(60, VendorEngine.CountGold(player));

        Assert.Equal(-1, VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }, new TradeEntry { ItemUid = row.Uid, Amount = 1 }],
            out var refusal, null));
        Assert.Equal(VendorEngine.VendorBuyRefusal.CantFulfill, refusal);
    }

    [Fact]
    public void Buy_OwnerAndGmModeTakeWithoutPaying_OtherStaffPay()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.Amount = 10;
        row.SetTag("PRICE", "10");
        stock.AddItem(row);

        var owner = MakePlayer(env.World, gold: 100);
        Assert.True(vendor.TryAssignOwnership(owner, owner));
        Assert.True(VendorEngine.IsVendorBoss(vendor, owner));
        // The cost is still worked out ("That is N gold coins worth of goods").
        Assert.Equal(10, VendorEngine.ProcessBuy(owner, vendor, [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(100, VendorEngine.CountGold(owner));

        var counselor = MakePlayer(env.World, gold: 100);
        counselor.PrivLevel = PrivLevel.Counsel;
        Assert.False(VendorEngine.IsVendorBoss(vendor, counselor));
        Assert.Equal(10, VendorEngine.ProcessBuy(counselor, vendor, [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(90, VendorEngine.CountGold(counselor));
    }

    [Fact]
    public void BuyTemplate_PropertyLinesAfterABuyRow_ApplyToTheSample()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        typeof(Character).GetMethod("PopulateVendorStock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vendor, ["vendor_b_audit_priced", true]);
        var samples = vendor.GetEquippedItem(Layer.VendorBuy)!.Contents.ToList();
        Assert.Equal(2, samples.Count);
        Assert.True(samples[0].TryGetTag("OVERRIDE.VALUE", out string? ov));
        Assert.Equal("90", ov);
        Assert.False(samples[1].TryGetTag("OVERRIDE.VALUE", out _));   // the line belongs to the first only

        VendorEngine.SetVendorGold(vendor, 1000);
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 1);
        player.Backpack!.AddItem(rare);
        Assert.Equal(90, Sell(player, vendor, rare, 1));
    }

    [Fact]
    public void HairPurchase_CallsTheHaircutService_OncePerLine()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var hair = env.World.CreateItem();
        hair.BaseId = 0x203B;
        hair.ItemType = ItemType.Hair;
        hair.SetTag("PRICE", "10");
        hair.Amount = 3;
        stock.AddItem(hair);
        var player = MakePlayer(env.World, gold: 100);

        int cuts = 0;
        Assert.Equal(10, VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = hair.Uid, Amount = 1 }], out _, null, () => cuts++));
        Assert.Equal(1, cuts);
    }

    // ---- E13: OF_VendorStockLimit caps the list and consumes the sample ----

    [Fact]
    public void E13_StockLimitOn_ListCappedBySample_AndSaleConsumesIt()
    {
        var env = Setup();
        GameClient.ServerOptionFlags |= OptionFlags.VendorStockLimit;
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        var sample = AddSample(env, vendor, "i_audit_rare", amount: 1);
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 3);
        player.Backpack!.AddItem(rare);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Equal((ushort)1, Assert.Single(SentSellList(state)!).Amount);

        Assert.Equal(10, Sell(player, vendor, rare, 1));
        Assert.True(sample.IsDeleted);                     // the demand is used up
        Assert.Equal(2, rare.Amount);

        TestHarness.ClearQueuedPackets(state);
        client.OpenVendorSell(vendor);
        Assert.Null(SentSellList(state));                  // and the vendor buys no more
        Assert.Equal(0, Sell(player, vendor, rare, 1));
    }

    [Fact]
    public void E13_StockLimitOn_PartialAndFullSalesCountTheDemandDown()
    {
        var env = Setup();
        GameClient.ServerOptionFlags |= OptionFlags.VendorStockLimit;
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        var sample = AddSample(env, vendor, "i_audit_rare", amount: 5);
        var player = MakePlayer(env.World);
        var pile = env.Make("i_audit_rare", 4);
        var single = env.Make("i_audit_rare", 1);
        player.Backpack!.AddItem(pile);
        player.Backpack.AddItem(single);

        Assert.Equal(20, Sell(player, vendor, pile, 2));   // partial sale of a pile
        Assert.Equal(3, sample.Amount);
        Assert.Equal(10, Sell(player, vendor, single, 1)); // a whole item
        Assert.Equal(2, sample.Amount);
    }

    [Fact]
    public void E13_StockLimitOff_ListShowsTheWholePile_AndSampleStays()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        var sample = AddSample(env, vendor, "i_audit_rare", amount: 1);
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 3);
        player.Backpack!.AddItem(rare);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Equal((ushort)3, Assert.Single(SentSellList(state)!).Amount);
        Assert.Equal(30, Sell(player, vendor, rare, 3));
        Assert.False(sample.IsDeleted);
        Assert.Equal(1, sample.Amount);
    }

    [Fact]
    public void E13_BuyTemplate_LaysOutSamplesWithTheTemplateAmount()
    {
        var env = Setup();
        GameClient.ServerOptionFlags |= OptionFlags.VendorStockLimit;
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        typeof(Character).GetMethod("PopulateVendorStock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vendor, ["vendor_b_audit", true]);
        var sample = Assert.Single(vendor.GetEquippedItem(Layer.VendorBuy)!.Contents);
        Assert.Equal(env.Index("i_audit_rare"), ItemDefHelper.ResolveInstanceDefIndex(sample));
        Assert.Equal(2, sample.Amount);

        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 5);
        player.Backpack!.AddItem(rare);
        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        Assert.Equal((ushort)2, Assert.Single(SentSellList(state)!).Amount);
    }

    [Fact]
    public void E13_VendorSavedWithOnlyTheBuyTemplateName_LaysItsSamplesOutOnce()
    {
        var env = Setup();
        GameClient.ServerOptionFlags |= OptionFlags.VendorStockLimit;
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        vendor.SetTag("VENDOR_BUY_LIST", "vendor_b_audit");   // an earlier save: the name only
        var player = MakePlayer(env.World);
        var rare = env.Make("i_audit_rare", 5);
        player.Backpack!.AddItem(rare);

        Assert.Equal(20, Sell(player, vendor, rare, 2));    // the template's demand is 2
        Assert.Empty(vendor.GetEquippedItem(Layer.VendorBuy)!.Contents);
        Assert.Equal(0, Sell(player, vendor, rare, 1));     // used up; not laid out again
        Assert.Equal(3, rare.Amount);
    }

    // ---- E14: the sell list walks searchable sub-containers ----

    [Fact]
    public void E14_ItemInOpenSubBag_IsListedAndSold_LockedBoxIsNotListed()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        VendorEngine.SetVendorGold(vendor, 1000);
        AddSample(env, vendor, "i_audit_rare");
        var player = MakePlayer(env.World);

        var bag = env.World.CreateItem();
        bag.BaseId = 0x0E76;
        bag.ItemType = ItemType.Container;
        player.Backpack!.AddItem(bag);
        var inBag = env.Make("i_audit_rare", 2);
        bag.AddItem(inBag);

        var chest = env.World.CreateItem();
        chest.BaseId = 0x0E40;
        chest.ItemType = ItemType.ContainerLocked;
        player.Backpack.AddItem(chest);
        var locked = env.Make("i_audit_rare", 2);
        chest.AddItem(locked);

        var (client, state) = Connect(env.World, player);
        client.OpenVendorSell(vendor);
        var row = Assert.Single(SentSellList(state)!);
        Assert.Equal(inBag.Uid.Value, row.Serial);

        Assert.Equal(10, Sell(player, vendor, inBag, 1));
        Assert.Equal(1, inBag.Amount);
        // The list never offers the locked one, but a sale only asks whether the
        // seller holds it (CClientEvent.cpp:1468) - a crafted line for it is sold.
        Assert.Equal(10, Sell(player, vendor, locked, 1));
        Assert.Equal(1, locked.Amount);
    }

    // ---- E15: hairdresser and figurine are services, not parcels ----

    [Fact]
    public void E15_BoughtHair_IsWornWithGrowingTimer_AndTheOldHairIsGone()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var hairStock = env.World.CreateItem();
        hairStock.BaseId = 0x203B;
        hairStock.ItemType = ItemType.Hair;
        hairStock.Amount = 5;
        hairStock.SetTag("PRICE", "10");
        stock.AddItem(hairStock);

        var player = MakePlayer(env.World, gold: 100);
        var oldHair = env.World.CreateItem();
        oldHair.BaseId = 0x203C;
        oldHair.ItemType = ItemType.Hair;
        player.Equip(oldHair, Layer.Hair);

        var (client, _) = Connect(env.World, player);
        client.HandleVendorBuy(vendor.Uid.Value, 1,
            [new VendorBuyEntry { ItemSerial = hairStock.Uid.Value, Amount = 1, Layer = (byte)Layer.VendorStock }]);

        var worn = player.GetEquippedItem(Layer.Hair);
        Assert.NotNull(worn);
        Assert.NotSame(oldHair, worn);
        Assert.Equal(0x203B, worn!.BaseId);
        Assert.True(oldHair.IsDeleted);
        Assert.True(worn.Timeout > Environment.TickCount64 + 54_000L * 1000);
        Assert.Equal(90, VendorEngine.CountGold(player));
        Assert.Equal(4, hairStock.Amount);
        // Upstream breaks out of the service switch into the ordinary delivery, so a
        // copy also lands in the pack (CClientEvent.cpp:1311-1340).
        Assert.Contains(player.Backpack!.Contents, i => i.BaseId == 0x203B && !i.IsDeleted);
    }

    [Fact]
    public void E15_BeardForABodyThatGrowsNone_IsRefusedBeforePayment()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var beard = env.World.CreateItem();
        beard.BaseId = 0x203E;
        beard.ItemType = ItemType.Beard;
        beard.SetTag("PRICE", "10");
        stock.AddItem(beard);
        var player = MakePlayer(env.World, gold: 100, body: 0x0191);

        long cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = beard.Uid, Amount = 1 }], out var refusal, null);
        Assert.Equal(-1, cost);
        Assert.Equal(VendorEngine.VendorBuyRefusal.CantBuy, refusal);
        Assert.Equal(100, VendorEngine.CountGold(player));
        Assert.Null(player.GetEquippedItem(Layer.FacialHair));
    }

    [Fact]
    public void E15_BoughtFigurine_MakesAnOwnedPet_NotAParcel()
    {
        var env = Setup();
        TestHarness.SeedCharDef(0x00CC);
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var figurine = env.World.CreateItem();
        figurine.BaseId = 0x2121;
        figurine.ItemType = ItemType.Figurine;
        figurine.More1 = 0x00CC;
        figurine.Name = "a horse";
        figurine.Amount = 3;
        figurine.SetTag("PRICE", "10");
        stock.AddItem(figurine);
        var player = MakePlayer(env.World, gold: 100);

        var (client, _) = Connect(env.World, player);
        client.HandleVendorBuy(vendor.Uid.Value, 1,
            [new VendorBuyEntry { ItemSerial = figurine.Uid.Value, Amount = 1, Layer = (byte)Layer.VendorStock }]);

        var pets = env.World.GetAllObjects().OfType<Character>()
            .Where(c => !c.IsDeleted && c.NpcMaster == player.Uid).ToList();
        var pet = Assert.Single(pets);
        Assert.Equal("a horse", pet.Name);
        Assert.Equal(90, VendorEngine.CountGold(player));
        Assert.Equal(2, figurine.Amount);
        Assert.DoesNotContain(player.Backpack!.Contents, i => i.ItemType == ItemType.Figurine);
    }

    [Fact]
    public void E15_FigurinePastTheFollowerSlots_IsRefusedBeforePayment()
    {
        var env = Setup();
        TestHarness.SeedCharDef(0x00CC);
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var figurine = env.World.CreateItem();
        figurine.BaseId = 0x2121;
        figurine.ItemType = ItemType.Figurine;
        figurine.More1 = 0x00CC;
        figurine.SetTag("FOLLOWERSLOTS", "1");
        figurine.SetTag("PRICE", "10");
        stock.AddItem(figurine);
        var player = MakePlayer(env.World, gold: 100);
        player.MaxFollower = 0;

        long cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = figurine.Uid, Amount = 1 }], out var refusal, (_, _) => null);
        Assert.Equal(-1, cost);
        Assert.Equal(VendorEngine.VendorBuyRefusal.PetSlots, refusal);
        Assert.Equal(100, VendorEngine.CountGold(player));
    }

    // ---- E16: the purse is the bank box's MORE1 ----

    private static Item GiveBank(GameWorld world, Character ch, uint more1)
    {
        var bank = world.CreateItem();
        bank.BaseId = 0x09AB;
        bank.ItemType = ItemType.EqBankBox;
        bank.More1 = more1;
        ch.Equip(bank, Layer.BankBox);
        return bank;
    }

    [Fact]
    public void E16_PurseIsTheBankBoxMore1_ReadByVendGold_CreditedByASale()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var bank = GiveBank(env.World, vendor, 777);
        Assert.Equal(777, VendorEngine.GetVendorGold(vendor));
        Assert.True(vendor.TryGetProperty("VENDGOLD", out string vendGold));
        Assert.Equal("777", vendGold);

        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.Amount = 5;
        row.SetTag("PRICE", "20");
        stock.AddItem(row);
        var player = MakePlayer(env.World, gold: 100);

        Assert.Equal(20, VendorEngine.ProcessBuy(player, vendor, [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(797u, bank.More1);
        Assert.Equal(80, VendorEngine.CountGold(player));
        Assert.False(vendor.TryGetTag(VendorEngine.LegacyVendorGoldTag, out _));

        Assert.True(vendor.TrySetProperty("VENDGOLD", "1234"));
        Assert.Equal(1234u, bank.More1);
    }

    [Fact]
    public void E16_VirtualGoldPurchase_DoesNotReachThePurse()
    {
        // Upstream credits m_Check_Amount only on the coin path (CClientEvent.cpp:1398-1407).
        var env = Setup();
        VirtualGold.Enabled = true;
        var vendor = MakeVendor(env.World);
        var bank = GiveBank(env.World, vendor, 777);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.SetTag("PRICE", "20");
        stock.AddItem(row);
        var player = MakePlayer(env.World);
        VirtualGold.Set(player, 100);

        Assert.Equal(20, VendorEngine.ProcessBuy(player, vendor, [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(80, VirtualGold.Get(player));
        Assert.Equal(777u, bank.More1);
    }

    [Fact]
    public void E16_LegacyTagMigration_OwnedAdds_OwnerlessKeepsTheLarger_Once()
    {
        var env = Setup();
        var owner = MakePlayer(env.World);

        var owned = MakeVendor(env.World);
        Assert.True(owned.TryAssignOwnership(owner, owner));
        GiveBank(env.World, owned, 500);
        owned.SetTag(VendorEngine.LegacyVendorGoldTag, "300");

        var shop = MakeVendor(env.World);
        GiveBank(env.World, shop, 500);
        shop.SetTag(VendorEngine.LegacyVendorGoldTag, "300");

        Assert.Equal(800, VendorEngine.GetVendorGold(owned));
        Assert.Equal(500, VendorEngine.GetVendorGold(shop));
        Assert.False(owned.TryGetTag(VendorEngine.LegacyVendorGoldTag, out _));
        Assert.False(VendorEngine.MigrateLegacyVendorGold(owned));
        Assert.Equal(800, VendorEngine.GetVendorGold(owned));   // not counted twice
    }

    [Fact]
    public void E16_CashKeepsOneDaysWage_InThePurse()
    {
        var env = Setup();
        var def = new SphereNet.Scripting.Definitions.CharDef(ResourceId.Invalid) { HireDayWage = 100 };
        DefinitionLoader.SetCharDef(0x0190, def);
        var owner = MakePlayer(env.World);
        var vendor = MakeVendor(env.World);
        vendor.CharDefIndex = 0x0190;
        Assert.True(vendor.TryAssignOwnership(owner, owner));
        VendorEngine.SetVendorGold(vendor, 500);

        Assert.Equal(400, VendorEngine.DispenseVendorGold(vendor, owner));
        Assert.Equal(100, VendorEngine.GetVendorGold(vendor));
        Assert.Equal(400, VendorEngine.CountGold(owner));
        Assert.Equal(0, VendorEngine.DispenseVendorGold(vendor, owner));
    }

    [Fact]
    public void E16_SaveLoadRoundtrip_MigratesTheLegacyTagOnce_AndKeepsMore1()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_vendgold_{Guid.NewGuid():N}");
        string dir2 = dir + "_2";
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(dir2);
        try
        {
            var env = Setup();
            var owner = MakePlayer(env.World);
            var vendor = MakeVendor(env.World);
            Assert.True(vendor.TryAssignOwnership(owner, owner));
            // A save from an earlier SphereNet: MORE1 brought from Source-X, the tag
            // holding what was earned since.
            GiveBank(env.World, vendor, 500);
            vendor.SetTag(VendorEngine.LegacyVendorGoldTag, "300");

            var lf = LoggerFactory.Create(_ => { });
            Assert.True(new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 }.Save(env.World, dir));

            var loaded = TestHarness.CreateWorld();
            VendorEngine.World = loaded;
            new WorldLoader(lf).Load(loaded, dir);
            var back = loaded.FindChar(vendor.Uid)!;
            Assert.False(back.TryGetTag(VendorEngine.LegacyVendorGoldTag, out _));
            Assert.Equal(800u, back.GetEquippedItem(Layer.BankBox)!.More1);

            // Saved again and reloaded: still 800, never 1100.
            Assert.True(new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 }.Save(loaded, dir2));
            var again = TestHarness.CreateWorld();
            VendorEngine.World = again;
            new WorldLoader(lf).Load(again, dir2);
            Assert.Equal(800, VendorEngine.GetVendorGold(again.FindChar(vendor.Uid)!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { Directory.Delete(dir2, recursive: true); } catch { }
        }
    }

    // ---- E17: a deed is priced as what it deeds ----

    [Fact]
    public void E17_DeedPrice_IsTheValueOfTheDefinitionInMore1()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);

        var deed = env.Make("i_audit_deed");
        Assert.Equal(ItemType.Deed, deed.ItemType);
        deed.More1 = (uint)env.Index("i_audit_prize");
        Assert.Equal(500, VendorEngine.GetVendorSellToPlayerPrice(vendor, deed));
        Assert.Equal(500, VendorEngine.GetServerSellPrice(vendor, deed));

        // A house deed names a MULTIDEF by name; that definition's VALUE.
        var house = env.Make("i_audit_deed");
        house.SetTag("MORE1_DEFNAME", "m_audit_house");
        Assert.Equal(36750, VendorEngine.GetVendorSellToPlayerPrice(vendor, house));

        // Naming nothing: 1, not the deed's own VALUE.
        var blank = env.Make("i_audit_deed");
        Assert.Equal(1, VendorEngine.GetVendorSellToPlayerPrice(vendor, blank));

        // OVERRIDE.VALUE still comes first, and a vendor's PRICE when selling.
        deed.SetTag("OVERRIDE.VALUE", "77");
        Assert.Equal(77, VendorEngine.GetVendorSellToPlayerPrice(vendor, deed));
        blank.Price = 33;
        Assert.Equal(33, VendorEngine.GetVendorSellToPlayerPrice(vendor, blank));
        Assert.Equal(1, VendorEngine.GetServerSellPrice(vendor, blank));
    }

    // ---- E18: virtual gold is 64-bit end to end ----

    [Fact]
    public void E18_VirtualGold_PurchasePastInt32_Succeeds()
    {
        var env = Setup();
        VirtualGold.Enabled = true;
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.Amount = 5;
        row.SetTag("PRICE", "1000000000");
        stock.AddItem(row);
        var player = MakePlayer(env.World);
        VirtualGold.Set(player, 4_000_000_000);

        long cost = VendorEngine.ProcessBuy(player, vendor, [new TradeEntry { ItemUid = row.Uid, Amount = 3 }]);
        Assert.Equal(3_000_000_000L, cost);
        Assert.Equal(1_000_000_000L, VirtualGold.Get(player));
        Assert.Equal(2, row.Amount);
    }

    [Fact]
    public void E18_CoinPurchase_KeepsTheHalfInt32Ceiling()
    {
        var env = Setup();
        var vendor = MakeVendor(env.World);
        var stock = env.World.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var row = env.World.CreateItem();
        row.BaseId = 0x0F0E;
        row.Amount = 5;
        row.SetTag("PRICE", "600000000");
        stock.AddItem(row);
        var player = MakePlayer(env.World, gold: 100);

        long cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 2 }], out var refusal, null);
        Assert.Equal(-1, cost);
        Assert.Equal(VendorEngine.VendorBuyRefusal.CostTooHigh, refusal);
        Assert.Equal(5, row.Amount);
        Assert.Equal(int.MaxValue / 2, VendorEngine.MaxBuyCost);
    }
}
