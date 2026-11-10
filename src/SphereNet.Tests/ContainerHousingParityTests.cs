using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Server.Admin;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Container, pickup and housing rules taken from Source-X: the quest-item drop
/// restriction, CanMoveItem's mover and layer gates, container limits asked of every
/// container on the way up, the container-volume rule, the pickup rate limit, the
/// pack's own placement function, house-key lockdown, key removal with a co-owner,
/// target-cursor staff verbs, ACCOUNT arguments and PRIVSET.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ContainerHousingParityTests
{
    private static (GameWorld World, GameClient Client, Character Player) Setup(int id = 7300,
        PrivLevel level = PrivLevel.Player)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = level;
        player.Str = 100; player.MaxHits = player.Hits = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AddPack(world, player);

        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
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

    private static Item NewItem(GameWorld world, ushort baseId = 0x0F51)
    {
        var item = world.CreateItem();
        item.BaseId = baseId;
        return item;
    }

    // --- 1: ATTR_QUESTITEM drops (CClientEvent.cpp:295-310) -----------------

    [Fact]
    public void AQuestItemCannotBeDroppedOnTheGround()
    {
        var (world, client, player) = Setup();
        var quest = NewItem(world);
        quest.SetAttr(ObjAttributes.QuestItem);
        Assert.True(player.Backpack!.TryAddItem(quest));

        client.Inventory.HandleItemPickup(quest.Uid.Value, 0);
        client.Inventory.HandleItemDrop(quest.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        Assert.Contains(quest, player.Backpack.Contents);
        Assert.False(quest.IsOnGround);
    }

    [Fact]
    public void AQuestItemMayGoIntoTheOwnPackAndIntoATrashCanWhichDeletesIt()
    {
        var (world, client, player) = Setup(7301);
        var quest = NewItem(world);
        quest.SetAttr(ObjAttributes.QuestItem);
        Assert.True(player.Backpack!.TryAddItem(quest));

        client.Inventory.HandleItemPickup(quest.Uid.Value, 0);
        client.Inventory.HandleItemDrop(quest.Uid.Value, 20, 20, 0, player.Backpack.Uid.Value);
        Assert.Contains(quest, player.Backpack.Contents);

        var trash = NewItem(world, 0x0E77);
        trash.ItemType = ItemType.TrashCan;
        world.PlaceItem(trash, new Point3D(101, 100, 0, 0));
        client.Inventory.HandleItemPickup(quest.Uid.Value, 0);
        client.Inventory.HandleItemDrop(quest.Uid.Value, 0, 0, 0, trash.Uid.Value);
        Assert.True(quest.IsDeleted);
    }

    [Fact]
    public void AGameMasterMayDropAQuestItemAnywhere()
    {
        var (world, client, player) = Setup(7302, PrivLevel.GM);
        var quest = NewItem(world);
        quest.SetAttr(ObjAttributes.QuestItem);
        Assert.True(player.Backpack!.TryAddItem(quest));

        client.Inventory.HandleItemPickup(quest.Uid.Value, 0);
        client.Inventory.HandleItemDrop(quest.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        Assert.True(quest.IsOnGround);
    }

    // --- 2: CanMoveItem (CCharStatus.cpp:1588-1687) --------------------------

    [Theory]
    [InlineData(StatFlag.Stone)]
    [InlineData(StatFlag.Insubstantial)]
    [InlineData(StatFlag.Sleeping)]
    public void AMoverWhoIsStoneInsubstantialOrAsleepCannotLiftAnything(StatFlag flag)
    {
        var (world, _, player) = Setup(7303);
        var item = NewItem(world);
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        Assert.True(ItemMoveRules.CanMove(player, item, out _));

        player.SetStatFlag(flag);

        Assert.False(ItemMoveRules.CanMove(player, item, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.MoverIncapacitated, denial);
    }

    [Fact]
    public void WornHairBeardAndPackAreNeverLiftedButClothesAre()
    {
        var (world, _, player) = Setup(7304);
        var hair = NewItem(world, 0x203B);
        var beard = NewItem(world, 0x203E);
        var shirt = NewItem(world, 0x1517);
        player.Equip(hair, Layer.Hair);
        player.Equip(beard, Layer.FacialHair);
        player.Equip(shirt, Layer.Shirt);

        Assert.False(ItemMoveRules.CanMove(player, hair, out var d1));
        Assert.Equal(ItemMoveRules.MoveDenial.ItemLayerLocked, d1);
        Assert.False(ItemMoveRules.CanMove(player, beard, out _));
        Assert.False(ItemMoveRules.CanMove(player, player.Backpack!, out _));
        Assert.True(ItemMoveRules.CanMove(player, shirt, out _));

        player.PrivLevel = PrivLevel.GM;
        Assert.True(ItemMoveRules.CanMove(player, hair, out _));
    }

    [Fact]
    public void NothingIsLiftedFromAnotherPartysTradeWindowButOwnOfferCanBeWithdrawn()
    {
        var (world, _, player) = Setup(7305);
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        Item Window(Character side)
        {
            var window = world.CreateItem();
            window.ItemType = ItemType.EqTradeWindow;
            window.IsEquipped = true;
            window.EquipLayer = Layer.Special;
            window.ContainedIn = side.Uid;
            return window;
        }

        var theirs = Window(partner);
        var offered = NewItem(world);
        theirs.AddItem(offered);
        Assert.False(ItemMoveRules.CanMove(player, offered, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.ItemInTrade, denial);

        // A bag inside their window is still inside the window.
        var bag = NewItem(world, 0x0E76);
        bag.ItemType = ItemType.Container;
        theirs.AddItem(bag);
        var inBag = NewItem(world);
        bag.AddItem(inBag);
        Assert.False(ItemMoveRules.CanMove(player, inBag, out _));

        // Own side: the owner may still take the offer back.
        var mine = Window(player);
        var own = NewItem(world);
        mine.AddItem(own);
        Assert.True(ItemMoveRules.CanMove(player, own, out _));
    }

    // --- 3: limits asked of every container on the way up --------------------

    [Fact]
    public void ABagInsideAFullWeightPackRefusesWhatThePackCouldNotHold()
    {
        var (world, client, player) = Setup(7306);
        int oldOverload = Item.BackpackOverload;
        try
        {
            Item.BackpackOverload = -1;                 // the owner's strength sets no limit
            player.Backpack!.ModMaxWeight = 1;          // so the pack's own MODMAXWEIGHT is the limit
            for (int i = 0; i < 10; i++)
                Assert.True(player.Backpack.TryAddItem(NewItem(world)));
            var bag = NewItem(world, 0x0E76);
            bag.ItemType = ItemType.Container;
            Assert.True(player.Backpack.TryAddItem(bag));

            var stone = NewItem(world, 0x0F52);
            world.PlaceItem(stone, new Point3D(101, 100, 0, 0));
            client.Inventory.HandleItemPickup(stone.Uid.Value, 0);
            client.Inventory.HandleItemDrop(stone.Uid.Value, 5, 5, 0, bag.Uid.Value);

            Assert.DoesNotContain(stone, bag.Contents);

            // With room in the pack the same drop lands.
            player.Backpack.ModMaxWeight = 100;
            client.Inventory.HandleItemPickup(stone.Uid.Value, 0);
            client.Inventory.HandleItemDrop(stone.Uid.Value, 5, 5, 0, bag.Uid.Value);
            Assert.Contains(stone, bag.Contents);
        }
        finally
        {
            Item.BackpackOverload = oldOverload;
        }
    }

    [Fact]
    public void ABagInsideAPackThatIsOutOfSlotsRefusesToo()
    {
        var (world, client, player) = Setup(7307);
        var bag = NewItem(world, 0x0E76);
        bag.ItemType = ItemType.Container;
        Assert.True(player.Backpack!.TryAddItem(bag));
        Assert.True(player.Backpack.TryAddItem(NewItem(world)));
        player.Backpack.SetTag("OVERRIDE.MAXITEMS", "2");   // bag + one item: full

        var coin = NewItem(world, 0x0F52);
        world.PlaceItem(coin, new Point3D(101, 100, 0, 0));
        client.Inventory.HandleItemPickup(coin.Uid.Value, 0);
        client.Inventory.HandleItemDrop(coin.Uid.Value, 5, 5, 0, bag.Uid.Value);

        Assert.DoesNotContain(coin, bag.Contents);
    }

    // --- 4: container volume (CItemContainer.cpp:922-929) ---------------------

    [Fact]
    public void ABiggerContainerDoesNotFitInASmallerOneUnlessTheShardAllowsIt()
    {
        var (world, client, player) = Setup(7308);
        // The volume is the DEFINITION's weight in stones: 2 for the small one, 5 for the big.
        SphereNet.Game.Definitions.DefinitionLoader.SetItemDef(0x0E76,
            new SphereNet.Scripting.Definitions.ItemDef(ResourceId.Invalid) { DispIndex = 0x0E76, Weight = 20, HasWeight = true });
        SphereNet.Game.Definitions.DefinitionLoader.SetItemDef(0x0E77,
            new SphereNet.Scripting.Definitions.ItemDef(ResourceId.Invalid) { DispIndex = 0x0E77, Weight = 50, HasWeight = true });
        var small = NewItem(world, 0x0E76);
        small.ItemType = ItemType.Container;
        var big = NewItem(world, 0x0E77);
        big.ItemType = ItemType.Container;
        Assert.True(player.Backpack!.TryAddItem(small));
        Assert.True(player.Backpack.TryAddItem(big));

        client.Inventory.HandleItemPickup(big.Uid.Value, 0);
        client.Inventory.HandleItemDrop(big.Uid.Value, 5, 5, 0, small.Uid.Value);
        Assert.DoesNotContain(big, small.Contents);
        Assert.Contains(big, player.Backpack.Contents);

        // A smaller one goes into a bigger one.
        client.Inventory.HandleItemPickup(small.Uid.Value, 0);
        client.Inventory.HandleItemDrop(small.Uid.Value, 5, 5, 0, big.Uid.Value);
        Assert.Contains(small, big.Contents);
        client.Inventory.HandleItemPickup(small.Uid.Value, 0);
        client.Inventory.HandleItemDrop(small.Uid.Value, 5, 5, 0, player.Backpack.Uid.Value);

        // OF_AllowContainerInsideContainer lifts the rule.
        GameClient.ServerOptionFlags |= OptionFlags.AllowContainerInsideContainer;
        client.Inventory.HandleItemPickup(big.Uid.Value, 0);
        client.Inventory.HandleItemDrop(big.Uid.Value, 5, 5, 0, small.Uid.Value);
        Assert.Contains(big, small.Contents);
    }

    // --- 5: pickup rate limit (CClientEvent.cpp:201-209) ----------------------

    [Fact]
    public void ASecondPickupInsideTheFastlootWindowIsCancelled()
    {
        var (world, client, player) = Setup(7309);
        var a = NewItem(world);
        var b = NewItem(world, 0x0F52);
        Assert.True(player.Backpack!.TryAddItem(a));
        Assert.True(player.Backpack.TryAddItem(b));

        ClientInventoryHandler.PickupMinIntervalMs = 60_000;
        client.Inventory.HandleItemPickup(a.Uid.Value, 0);
        Assert.True(player.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(a.Uid.Value.ToString(), held);

        client.Inventory.HandleItemPickup(b.Uid.Value, 0);

        // Cancelled: B stays where it was and A is still the one on the cursor.
        Assert.Contains(b, player.Backpack.Contents);
        Assert.True(player.TryGetTag("DRAGGING", out held));
        Assert.Equal(a.Uid.Value.ToString(), held);
    }

    // --- 6: f_multi_onplacement_check and the staff bypass ---------------------

    private static (HousingEngine Engine, GameWorld World, Character Owner, MultiDef Def) HouseSetup()
    {
        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x0064, Name = "probe house" };
        def.Components.Add(new MultiComponent { TileId = 0x0001, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        registry.Register(def);
        var engine = new HousingEngine(world, registry) { MaxHousesPerPlayer = -1, MaxHousesPerAccount = -1 };
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        AddPack(world, owner);
        return (engine, world, owner, def);
    }

    private static Character Bystander(GameWorld world, Point3D at)
    {
        var bystander = world.CreateCharacter();
        bystander.MaxHits = 10; bystander.Hits = 10;
        world.PlaceCharacter(bystander, at);
        return bystander;
    }

    [Fact]
    public void ThePlacementFunctionRunsFirstAndVetoesWithReturnOne()
    {
        var (engine, _, owner, _) = HouseSetup();
        try
        {
            ushort seenId = 0;
            Point3D seenPoint = default;
            HousingEngine.OnPlacementCheck = (_, id, pt) =>
            {
                seenId = id; seenPoint = pt;
                return new HousingEngine.PlacementCheck(Veto: true, SkipGeometry: false, null, null);
            };

            var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0), out var failure);

            Assert.Null(house);
            Assert.Equal(PlacementFailure.ScriptVeto, failure);
            Assert.Equal(0x0064, seenId);
            Assert.Equal(200, seenPoint.X);
        }
        finally { HousingEngine.OnPlacementCheck = null; }
    }

    [Fact]
    public void HalfbakedSkipsTheGeometryChecksAndStaffNeverRunsThem()
    {
        var (engine, world, owner, _) = HouseSetup();
        try
        {
            Bystander(world, new Point3D(200, 200, 0, 0));      // stands where the house goes

            Assert.Null(engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0), out var failure));
            Assert.Equal(PlacementFailure.LocationBlocked, failure);

            HousingEngine.OnPlacementCheck = (_, _, _) =>
                new HousingEngine.PlacementCheck(false, SkipGeometry: true, null, null);
            Assert.NotNull(engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0)));
        }
        finally { HousingEngine.OnPlacementCheck = null; }

        var (engine2, world2, gm, _) = HouseSetup();
        Bystander(world2, new Point3D(300, 300, 0, 0));
        gm.PrivLevel = PrivLevel.GM;
        Assert.NotNull(engine2.PlaceHouse(gm, 0x0064, new Point3D(300, 300, 0, 0)));
    }

    [Fact]
    public void TheMarginsComeFromTheFunctionNotFromConstants()
    {
        var (engine, world, owner, def) = HouseSetup();
        var noBuild = new SphereNet.Game.World.Regions.Region
        { Name = "nobuild_probe", Flags = RegionFlag.NoBuild, MapIndex = 0 };
        noBuild.AddRect(297, 300, 298, 300);                    // three tiles west of the site
        world.AddRegion(noBuild);
        var site = new Point3D(300, 300, 0, 0);

        // The default scan is north and south only (0,-5,0,5).
        Assert.True(engine.CanPlaceHouse(site, def));
        // A function that widens the west margin finds the region.
        Assert.False(engine.CanPlaceHouse(site, def, HousingEngine.DefaultBlockRadius, (-5, -5, 0, 5)));

        try
        {
            HousingEngine.OnPlacementCheck = (_, _, _) =>
                new HousingEngine.PlacementCheck(false, false, null, "-5, -5, 0, 5");
            Assert.Null(engine.PlaceHouse(owner, 0x0064, site, out var failure));
            Assert.Equal(PlacementFailure.LocationBlocked, failure);
        }
        finally { HousingEngine.OnPlacementCheck = null; }

        Assert.Equal((-5, -5, 0, 5), HousingEngine.ParseRadius("-5, -5, 0, 5", HousingEngine.DefaultMultiRadius));
        Assert.Equal(HousingEngine.DefaultBlockRadius, HousingEngine.ParseRadius("nonsense", HousingEngine.DefaultBlockRadius));
    }

    // --- 7: a house key locks a loose item down (CCharUse.cpp:23-47, :1399) -----

    private static string UseKey(GameClient client, Item key, Item target)
    {
        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleDoubleClick(key.Uid.Value);
        Assert.NotEqual(0u, client.ActiveTargetCursorId);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, target.Uid.Value, target.X, target.Y, target.Z, target.BaseId);
        var sb = new StringBuilder();
        foreach (var pkt in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = pkt.Span;
            if (span.Length > 48 && span[0] == 0xAE)
                sb.Append(Encoding.BigEndianUnicode.GetString(span[48..])).Append('\n');
            else if (span.Length > 44 && span[0] == 0x1C)
                sb.Append(Encoding.ASCII.GetString(span[44..])).Append('\n');
        }
        return sb.ToString().Replace("\0", "");
    }

    private static MultiDef FourTileHouse()
    {
        var def = new MultiDef { Id = 0x0064, Name = "probe house" };
        foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            def.Components.Add(new MultiComponent { TileId = 0x0001, DeltaX = (short)dx, DeltaY = (short)dy, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        return def;
    }

    [Fact]
    public void AHouseKeyLocksALooseItemDownAndASecondUseReleasesIt()
    {
        var (world, client, player) = Setup(7310);
        var registry = new MultiRegistry();
        registry.Register(FourTileHouse());
        var engine = new HousingEngine(world, registry) { MaxHousesPerPlayer = -1, MaxHousesPerAccount = -1 };
        client.SetEngines(housingEngine: engine);
        var house = engine.PlaceHouse(player, 0x0064, new Point3D(100, 100, 0, 0), magic: true)!;
        Assert.NotNull(house);

        var key = NewItem(world, 0x100E);
        key.ItemType = ItemType.Key;
        key.Link = house.MultiItem.Uid;
        Assert.True(player.Backpack!.TryAddItem(key));

        var loose = NewItem(world);
        world.PlaceItem(loose, new Point3D(101, 100, 0, 0));

        string said = UseKey(client, key, loose);
        Assert.Contains(ServerMessages.Get(Msg.MultiLockdown), said);
        Assert.Contains(loose.Uid, house.Lockdowns);
        Assert.Equal(house.MultiItem.Uid, loose.Link);

        said = UseKey(client, key, loose);
        Assert.Contains(ServerMessages.Get(Msg.MultiLockup), said);
        Assert.DoesNotContain(loose.Uid, house.Lockdowns);
        Assert.False(loose.Link.IsValid);
    }

    // --- 8: keys leave with a co-owner / friend (CItemMulti.cpp:776-880, :1159) -----

    private static Item GiveKey(GameWorld world, Character holder, House house)
    {
        var key = NewItem(world, 0x100F);
        key.ItemType = ItemType.Key;
        key.Link = house.MultiItem.Uid;
        Assert.True(holder.Backpack!.TryAddItem(key));
        return key;
    }

    [Fact]
    public void RemovingACoOwnerOrFriendTakesTheirKeys()
    {
        var (engine, world, owner, _) = HouseSetup();
        var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
        var coOwner = world.CreateCharacter(); coOwner.IsPlayer = true; AddPack(world, coOwner);
        var friend = world.CreateCharacter(); friend.IsPlayer = true; AddPack(world, friend);
        house.AddCoOwner(coOwner.Uid);
        house.AddFriend(friend.Uid);
        var coKey = GiveKey(world, coOwner, house);
        var friendKey = GiveKey(world, friend, house);

        Assert.True(house.RemoveCoOwner(coOwner.Uid));
        Assert.DoesNotContain(coKey, coOwner.Backpack!.Contents);

        Assert.True(house.RemoveFriend(friend.Uid));
        Assert.DoesNotContain(friendKey, friend.Backpack!.Contents);
    }

    [Fact]
    public void TearingTheHouseDownTakesEveryListedKeyOnlyWithAutoHouseKeys()
    {
        foreach (bool auto in new[] { true, false })
        {
            var (engine, world, owner, _) = HouseSetup();
            engine.AutoHouseKeys = true;       // so the placement itself mints keys
            var house = engine.PlaceHouse(owner, 0x0064, new Point3D(200, 200, 0, 0))!;
            var coOwner = world.CreateCharacter(); coOwner.IsPlayer = true; AddPack(world, coOwner);
            var access = world.CreateCharacter(); access.IsPlayer = true; AddPack(world, access);
            house.AddCoOwner(coOwner.Uid);
            house.AddAccess(access.Uid);
            var coKey = GiveKey(world, coOwner, house);
            var accessKey = GiveKey(world, access, house);

            engine.AutoHouseKeys = auto;
            world.DeleteObject(house.MultiItem);

            Assert.Equal(auto ? 0 : 1, coOwner.Backpack!.Contents.Count(i => i.Uid == coKey.Uid));
            Assert.Equal(auto ? 0 : 1, access.Backpack!.Contents.Count(i => i.Uid == accessKey.Uid));
        }
    }

    // --- 9: redirect verbs ask for a target (CClientEvent.cpp:28-42, 1117-1124) ------

    private static (CommandHandler Cmds, Character Gm, List<(string Verb, string Args)> Asked) StaffSetup(PrivLevel level)
    {
        var world = TestHarness.CreateWorld();
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = level;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var cmds = new CommandHandler();
        cmds.RegisterDefaults(world);
        var asked = new List<(string, string)>();
        cmds.OnAddVerbTargetRequested += (_, verb, args) => asked.Add((verb, args));
        return (cmds, gm, asked);
    }

    [Theory]
    [InlineData("KICK", "KICK", "")]
    [InlineData("PRIVSET Admin", "PRIVSET", "Admin")]
    [InlineData("SHRINK", "SHRINK", "")]
    [InlineData("DESTROY", "DESTROY", "")]
    [InlineData("NUDGEUP 2", "NUDGEUP", "2")]
    [InlineData("NUDGEDOWN", "NUDGEDOWN", "")]
    [InlineData("JAIL 3", "JAIL", "3")]
    [InlineData("FORGIVE", "FORGIVE", "")]
    [InlineData("KILL", "KILL", "")]
    public void ARedirectVerbTypedWithoutATargetRaisesACursor(string line, string verb, string args)
    {
        var (cmds, gm, asked) = StaffSetup(PrivLevel.Owner);

        Assert.Equal(CommandResult.Executed, cmds.TryExecute(gm, line));

        Assert.Equal([(verb, args)], asked);
    }

    [Fact]
    public void AJailWithAnObjectIdStillNamesItsTarget()
    {
        var world = TestHarness.CreateWorld();
        var gm = world.CreateCharacter();
        gm.IsPlayer = true; gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var cmds = new CommandHandler();
        cmds.RegisterDefaults(world);
        var asked = new List<string>();
        cmds.OnAddVerbTargetRequested += (_, verb, _) => asked.Add(verb);

        cmds.TryExecute(gm, $"JAIL 0x{target.Uid.Value:X}");

        Assert.Empty(asked);
    }

    // --- 10: ACCOUNT arguments --------------------------------------------------

    private static (AdminCommandProcessor Processor, AccountManager Accounts) AccountSetup()
    {
        var accounts = new AccountManager(NullLoggerFactory.Instance);
        var processor = new AdminCommandProcessor(new GameWorld(NullLoggerFactory.Instance),
            accounts, new SphereConfig(), () => 0, NullLoggerFactory.Instance);
        return (processor, accounts);
    }

    [Fact]
    public void AccountBlockTakesItsZeroOrOneArgument()
    {
        var (processor, accounts) = AccountSetup();
        var lines = new List<string>();
        Assert.NotNull(accounts.CreateAccount("probe", "secret"));
        var account = accounts.FindAccount("probe")!;

        processor.ProcessCommand("ACCOUNT probe BLOCK", lines.Add);
        Assert.True(account.IsBanned);
        processor.ProcessCommand("ACCOUNT probe BLOCK 0", lines.Add);
        Assert.False(account.IsBanned);
        processor.ProcessCommand("ACCOUNT probe BLOCK 1", lines.Add);
        Assert.True(account.IsBanned);
        processor.ProcessCommand("ACCOUNT probe BAN 0", lines.Add);
        Assert.False(account.IsBanned);
    }

    [Fact]
    public void AccountJailGuestMaxCharsAndKickAreSubcommands()
    {
        var (processor, accounts) = AccountSetup();
        var lines = new List<string>();
        Assert.NotNull(accounts.CreateAccount("probe", "secret"));
        var account = accounts.FindAccount("probe")!;
        string? kicked = null;
        processor.KickAccountClients = name => kicked = name;

        processor.ProcessCommand("ACCOUNT probe JAIL", lines.Add);
        Assert.True(account.Jail);
        processor.ProcessCommand("ACCOUNT probe JAIL 0", lines.Add);
        Assert.False(account.Jail);

        processor.ProcessCommand("ACCOUNT probe GUEST", lines.Add);
        Assert.Equal(PrivLevel.Guest, account.PrivLevel);
        processor.ProcessCommand("ACCOUNT probe GUEST 0", lines.Add);
        Assert.Equal(PrivLevel.Player, account.PrivLevel);

        processor.ProcessCommand("ACCOUNT probe MAXCHARS 3", lines.Add);
        Assert.Equal(3, account.MaxChars);

        processor.ProcessCommand("ACCOUNT probe KICK", lines.Add);
        Assert.Equal("probe", kicked);
        Assert.False(account.IsBanned);
    }

    // --- 11: PRIVSET / SETPRIV (CCharAct.cpp:5473-5511) -------------------------------

    private sealed class LevelConsole(PrivLevel level) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => level;
        public void SysMessage(string text) { }
        public string GetName() => "console";
    }

    private static (Character Target, Account Account, GameWorld World) PrivTarget()
    {
        var world = TestHarness.CreateWorld();
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(100, 100, 0, 0));
        AddPack(world, target);
        var account = new Account { Name = "probe", PrivLevel = PrivLevel.Player };
        Character.ResolveAccountForChar = uid => uid == target.Uid ? account : null;
        return (target, account, world);
    }

    [Fact]
    public void PrivSetPromotesWithRobeAndInvulAndDemotionTakesThemBack()
    {
        var (target, account, world) = PrivTarget();
        var shirt = NewItem(world, 0x1517);
        target.Equip(shirt, Layer.Shirt);

        Assert.True(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.Admin), "GM"));

        Assert.Equal(PrivLevel.GM, account.PrivLevel);
        Assert.Equal(0x000Au, account.Priv & 0x000Au);          // PRIV_GM | PRIV_GM_PAGE
        Assert.True(target.IsStatFlag(StatFlag.Invul));
        var robe = target.GetEquippedItem(Layer.Robe);
        Assert.NotNull(robe);
        Assert.Equal(0x204F, robe!.BaseId);
        Assert.Equal((ushort)0x0022, robe.Hue.Value);
        Assert.Contains(shirt, target.Backpack!.Contents);       // everything else went to the pack

        // Down again: level, flags, invulnerability and robe all go.
        Assert.True(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.Admin), "1"));

        Assert.Equal(PrivLevel.Player, account.PrivLevel);
        Assert.Equal(0u, account.Priv & 0x000Au);
        Assert.False(target.IsStatFlag(StatFlag.Invul));
        Assert.Null(target.GetEquippedItem(Layer.Robe));
    }

    [Fact]
    public void PrivSetNeedsAnAdminNoLowerThanTheTargetAndAPlayerTarget()
    {
        var (target, account, _) = PrivTarget();

        Assert.False(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.GM), "Counsel"));
        Assert.Equal(PrivLevel.Player, account.PrivLevel);

        account.PrivLevel = PrivLevel.Owner;
        Assert.False(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.Admin), "Player"));
        Assert.Equal(PrivLevel.Owner, account.PrivLevel);

        account.PrivLevel = PrivLevel.Player;
        Assert.False(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.Admin), ""));
        target.IsPlayer = false;
        Assert.False(CommandHandler.SetPrivLevel(target, new LevelConsole(PrivLevel.Admin), "Counsel"));
    }

    [Fact]
    public void ThePrivSetVerbOnACharacterGoesThroughTheSameOperation()
    {
        var (target, account, _) = PrivTarget();

        Assert.False(target.TryExecuteCommand("PRIVSET", "Counsel", new LevelConsole(PrivLevel.GM)));
        Assert.Equal(PrivLevel.Player, account.PrivLevel);

        Assert.True(target.TryExecuteCommand("PRIVSET", "Counsel", new LevelConsole(PrivLevel.Admin)));
        Assert.Equal(PrivLevel.Counsel, account.PrivLevel);
        Assert.Equal((ushort)0x0003, target.GetEquippedItem(Layer.Robe)!.Hue.Value);
    }
}
