using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Components;
using SphereNet.Game.Housing;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Item and economy behaviour that had drifted from Source-X: eating and drinking
/// (Use_EatQty / Use_Drink, CCharUse.cpp), the potion cooldown marker, blades on
/// items, crafting stations, bedrolls, pitchers, the item stone, tiledata weights,
/// the vendor purse restock and the house storage defaults.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemsEconomySourceXParityTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8401);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 50; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    private static Item InPack(Bench bench, ItemType type, ushort id = 0x0F0E, ushort amount = 1)
    {
        var item = bench.World.CreateItem();
        item.BaseId = id;
        item.ItemType = type;
        item.Amount = amount;
        Assert.True(bench.Pack.TryAddItem(item));
        return item;
    }

    // --- food value (Use_EatQty, CCharUse.cpp:880-887) ------------------

    [Fact]
    public void FoodValue_IsMoreM_ElseVolume_AtLeastOne()
    {
        var world = TestHarness.CreateWorld();
        Item.ResolveWorld = () => world;
        var map = new MapDataManager("");
        map.SetSyntheticItemTile(0x3010, new ItemTileData { Weight = 3, Name = "ham" });
        map.SetSyntheticItemTile(0x3011, new ItemTileData { Weight = 0, Name = "crumb" });
        world.MapData = map;

        var food = world.CreateItem();
        food.BaseId = 0x3010;
        food.ItemType = ItemType.Food;
        food.SetTag("FOODVAL", "40"); // not the reference's field: ignored

        food.MoreP = new Point3D(0, 0, 0, 7); // MOREM
        Assert.Equal(7, EatEngine.RestorePerUnit(food));

        food.MoreP = new Point3D(0, 0, 0, 0);  // no MOREM: 3 stones -> volume 3
        Assert.Equal(3, EatEngine.RestorePerUnit(food));

        food.BaseId = 0x3011;                  // weightless -> 0 -> floor 1
        Assert.Equal(1, EatEngine.RestorePerUnit(food));
    }

    // --- drinks (Use_Drink, CCharUse.cpp:983-1112) ----------------------

    [Fact]
    public void Booze_DoesNotFeed()
    {
        var bench = Setup();
        bench.Me.Food = 0;
        int foodBefore = bench.Me.Food;
        var ale = InPack(bench, ItemType.Booze, amount: 2);

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        Assert.Equal(foodBefore, bench.Me.Food);
        Assert.Equal(1, ale.Amount);
    }

    [Theory]
    [InlineData(ItemType.Drink)]
    [InlineData(ItemType.WaterWash)]
    public void Drink_WithoutDrinkIsFood_FeedsNothingAndGivesNoStamina(ItemType type)
    {
        var bench = Setup();
        GameClient.ServerOptionFlags &= ~OptionFlags.DrinkIsFood;
        bench.Me.Food = 0;
        short stamBefore = bench.Me.Stam;
        var drink = InPack(bench, type, amount: 2);
        drink.MoreP = new Point3D(0, 0, 0, 10);

        bench.Client.HandleDoubleClick(drink.Uid.Value);

        Assert.Equal(0, bench.Me.Food);
        Assert.Equal(stamBefore, bench.Me.Stam);
        Assert.Equal(1, drink.Amount); // one unit drunk
    }

    [Fact]
    public void Drink_WithDrinkIsFood_FeedsMoreM()
    {
        var bench = Setup();
        GameClient.ServerOptionFlags |= OptionFlags.DrinkIsFood;
        bench.Me.Food = 0;
        if (bench.Me.MaxFood < 20)
            return; // a body with no hunger cannot be fed; nothing to measure
        var drink = InPack(bench, ItemType.Drink, amount: 2);
        drink.MoreP = new Point3D(0, 0, 0, 6);

        bench.Client.HandleDoubleClick(drink.Uid.Value);

        Assert.Equal(6, bench.Me.Food);
    }

    [Fact]
    public void Potion_LeavesTheCooldownMarker_AndASecondIsRefused()
    {
        var bench = Setup();
        var potions = InPack(bench, ItemType.Potion, amount: 3);

        bench.Client.HandleDoubleClick(potions.Uid.Value);
        Assert.Equal(2, potions.Amount);
        var marker = bench.Me.GetEquippedItem(Layer.FlagPotionUsed);
        Assert.NotNull(marker);
        // (TDATA2 ?: 15) * 10 tenths = 15 s (CCharUse.cpp:996).
        long left = marker!.Timeout - Environment.TickCount64;
        Assert.InRange(left, 10_000, 15_000);

        bench.Client.HandleDoubleClick(potions.Uid.Value);
        Assert.Equal(2, potions.Amount); // refused: drink_potion_delay

        marker.SetTimeout(1);
        marker.OnTick();
        Assert.True(marker.IsDeleted);
        bench.Client.HandleDoubleClick(potions.Uid.Value);
        Assert.Equal(1, potions.Amount);
    }

    // --- blades on items (CClientTarg.cpp:1823-1983) --------------------

    private static void UseOn(Bench bench, Item tool, uint target)
    {
        bench.Client.HandleDoubleClick(tool.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId, target, 0, 0, 0, 0);
    }

    [Fact]
    public void BladeOnAWeapon_SmashesItInsteadOfPoisoning()
    {
        var bench = Setup();
        var blade = InPack(bench, ItemType.WeaponSword);
        var victim = InPack(bench, ItemType.WeaponFence);
        victim.HitsMax = 20;
        victim.HitsCur = 20;

        UseOn(bench, blade, victim.Uid.Value);

        Assert.Equal(19, victim.HitsCur);        // OnTakeDamage(1)
        Assert.False(bench.Client.HasPendingTarget); // no Poisoning target prompt
    }

    [Fact]
    public void BladeOnAnItemAtItsLastHitPoint_DestroysIt()
    {
        var bench = Setup();
        var blade = InPack(bench, ItemType.WeaponSword);
        // Only the armour/weapon family wears (IsTypeArmorWeapon, CItem.cpp:5807/5910);
        // a plain item has no hits to lose and OnTakeDamage does nothing to it (:5985).
        // This test used a plain chair and expected it destroyed.
        var shield = InPack(bench, ItemType.Shield);
        shield.HitsMax = 5;
        shield.HitsCur = 1;
        var chair = InPack(bench, ItemType.Normal);
        chair.HitsMax = 5;
        chair.HitsCur = 1;

        UseOn(bench, blade, shield.Uid.Value);
        Assert.True(shield.IsDeleted);

        UseOn(bench, blade, chair.Uid.Value);
        Assert.False(chair.IsDeleted);
        Assert.Equal(1, chair.HitsCur);
    }

    [Fact]
    public void BladeOnAFixedItem_IsImmune()
    {
        var bench = Setup();
        var blade = InPack(bench, ItemType.WeaponSword);
        var post = bench.World.CreateItem();
        post.ItemType = ItemType.Normal;
        post.HitsMax = 5;
        post.HitsCur = 5;
        post.SetAttr(ObjAttributes.Move_Never);
        bench.World.PlaceItem(post, new Point3D(101, 100, 0, 0));

        UseOn(bench, blade, post.Uid.Value);

        Assert.Equal(5, post.HitsCur);
    }

    [Fact]
    public void SmithHammerOnClothing_DoesNotRepair()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.ArmsLore, 1000);
        var hammer = InPack(bench, ItemType.WeaponMaceSmith);
        var shirt = InPack(bench, ItemType.Clothing);
        shirt.HitsMax = 20;
        shirt.HitsCur = 5;
        var anvil = bench.World.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        bench.World.PlaceItem(anvil, bench.Me.Position);

        UseOn(bench, hammer, shirt.Uid.Value);

        Assert.Equal(5, shirt.HitsCur); // Armor_IsRepairable: clothing is not
    }

    [Fact]
    public void Armor_IsRepairable_FollowsTheReferenceTypeTable()
    {
        var world = TestHarness.CreateWorld();
        Item Of(ItemType t) { var i = world.CreateItem(); i.ItemType = t; return i; }
        Assert.True(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.Armor)));
        Assert.True(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.Shield)));
        Assert.True(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.WeaponSword)));
        Assert.True(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.WeaponXBow)));
        Assert.False(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.WeaponBow)));
        Assert.False(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.ArmorLeather)));
        Assert.False(ClientItemUseHandler.IsArmorRepairable(Of(ItemType.Clothing)));
    }

    // --- crafting stations / bedroll / pitcher / stone ------------------

    [Fact]
    public void Forge_AsksForOre_InsteadOfOpeningAMenu()
    {
        var bench = Setup();
        var forge = bench.World.CreateItem();
        forge.ItemType = ItemType.Forge;
        bench.World.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(forge.Uid.Value);

        Assert.True(bench.Client.HasPendingTarget);
    }

    [Fact]
    public void SpinningWheel_JustSpins()
    {
        var bench = Setup();
        var wheel = bench.World.CreateItem();
        wheel.BaseId = 0x1015;
        wheel.ItemType = ItemType.SpinWheel;
        bench.World.PlaceItem(wheel, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(wheel.Uid.Value);

        Assert.Equal(ItemType.AnimActive, wheel.ItemType);
        Assert.Equal(0x1016, wheel.DispIdFull);
    }

    [Fact]
    public void UnknownBedrollGraphic_DoesNotStartCamping()
    {
        var bench = Setup();
        var roll = bench.World.CreateItem();
        roll.BaseId = 0x1234;
        roll.ItemType = ItemType.Bedroll;
        bench.World.PlaceItem(roll, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(roll.Uid.Value);

        Assert.False(bench.Me.HasActiveSkillPending());
        Assert.Equal((ushort)0x1234, roll.BaseId);
    }

    [Fact]
    public void FilledPitcher_IsTheWaterPitcher()
    {
        var bench = Setup();
        var trough = bench.World.CreateItem();
        trough.ItemType = ItemType.WaterWash;
        bench.World.PlaceItem(trough, new Point3D(101, 100, 0, 0));
        var pitcher = InPack(bench, ItemType.PitcherEmpty, 0x0FF6);

        UseOn(bench, pitcher, trough.Uid.Value);

        Assert.Equal((ushort)0x0FF8, pitcher.BaseId); // ITEMID_PITCHER_WATER
    }

    [Fact]
    public void ItemStone_RegenTimerRefusesUntilItRuns()
    {
        var bench = Setup();
        var stone = bench.World.CreateItem();
        stone.ItemType = ItemType.ItemStone;
        stone.More1 = 0x0F3F;
        stone.MoreP = new Point3D(60, 0, 0, 0); // MOREX 60 s regen, MOREY 0 = infinite
        bench.World.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        bench.Client.HandleDoubleClick(stone.Uid.Value);
        bench.Client.HandleDoubleClick(stone.Uid.Value);

        Assert.Equal(1, bench.Pack.Contents.Where(i => i.BaseId == 0x0F3F).Sum(i => i.Amount));
        Assert.True(stone.Timeout > Environment.TickCount64);
        Assert.Equal(0, stone.MoreP.Y); // infinite stays infinite
    }

    // --- tiledata weight (CItemBase.cpp:103-111, :997-1003) -------------

    [Fact]
    public void TiledataWeight_ZeroIsWeightless_0xFFIsOneStone()
    {
        var world = TestHarness.CreateWorld();
        Item.ResolveWorld = () => world;
        var map = new MapDataManager("");
        map.SetSyntheticItemTile(0x3001, new ItemTileData { Weight = 0, Name = "feather" });
        map.SetSyntheticItemTile(0x3002, new ItemTileData { Weight = 0xFF, Name = "boulder" });
        map.SetSyntheticItemTile(0x3003, new ItemTileData { Weight = 4, Name = "brick" });
        world.MapData = map;

        var feather = world.CreateItem(); feather.BaseId = 0x3001;
        var boulder = world.CreateItem(); boulder.BaseId = 0x3002;
        var brick = world.CreateItem(); brick.BaseId = 0x3003;

        Assert.Equal(0, feather.Weight);
        Assert.Equal(Item.WeightUnits, boulder.Weight);
        Assert.Equal(ushort.MaxValue, boulder.DefinitionWeightRaw);
        Assert.Equal(40, brick.Weight);
    }

    // --- vendor purse / house defaults / item spawner -------------------

    [Fact]
    public void VendorPurse_RestocksTo10000_OrTheBankBoxMore2()
    {
        Assert.Equal(10000, VendorEngine.RestockGold);

        var world = TestHarness.CreateWorld();
        var vendor = world.CreateCharacter();
        Assert.Equal(10000, VendorEngine.RestockGoldFor(vendor));

        var bank = world.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        bank.More2 = 2500; // m_itEqBankBox.m_Check_Restock
        vendor.Equip(bank, Layer.BankBox);
        Assert.Equal(2500, VendorEngine.RestockGoldFor(vendor));
    }

    [Fact]
    public void House_StorageAndVendorDefaults_AreTheSmallestHouse()
    {
        var world = TestHarness.CreateWorld();
        var multi = world.CreateItem();
        var house = new House(multi);
        Assert.Equal(489, house.BaseStorage);
        Assert.Equal(10, house.BaseVendors);
    }

    [Fact]
    public void ItemSpawner_FirstArm_UsesItsOwnDelayWindow()
    {
        var world = TestHarness.CreateWorld();
        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnItem;
        world.PlaceItem(spawner, new Point3D(100, 100, 0, 0));
        var comp = new ItemSpawnComponent(spawner, world);
        comp.SetDelay(2, 2);

        comp.ResetTimer();

        long delay = spawner.Timeout - Environment.TickCount64;
        Assert.InRange(delay, 110_000, 120_000); // 2 minutes, not 5-30 s
    }
}
