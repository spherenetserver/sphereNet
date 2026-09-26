using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// CItem::OnTakeDamage (CItem.cpp:5792-5987) by item type, ATTR_CANNOTREPAIR on the
/// 64-bit attribute word (CItem.h:151, CItem.cpp:4851), and Use_Drink's TRIGRET and
/// ENHANCEPOTIONS readings (CCharUse.cpp:1017-1022, 1060-1063).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemTakeDamageDrinkParityTests
{
    private sealed class Console : ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }

    private static (GameWorld World, Item Item) Ground(ItemType type)
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.ItemType = type;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        return (world, item);
    }

    // --- OnTakeDamage ----------------------------------------------------

    [Fact]
    public void AnItemOutsideTheArmourWeaponFamilyTakesNoWear()
    {
        var (_, item) = Ground(ItemType.Normal);
        item.HitsMax = 10;
        item.HitsCur = 10;

        Assert.Equal(0, ItemDamageEngine.OnTakeDamage(item, 5, null, DamageType.HitBlunt));
        Assert.Equal(10, item.HitsCur);
    }

    [Fact]
    public void ArmourLosesOneHitPointPerBlowAndBreaksAtItsLast()
    {
        var (_, armor) = Ground(ItemType.Armor);
        armor.HitsMax = 2;
        armor.HitsCur = 2;

        Assert.Equal(2, ItemDamageEngine.OnTakeDamage(armor, 40, null, DamageType.HitSlash));
        Assert.Equal(1, armor.HitsCur);
        Assert.Equal(ItemDamageEngine.Destroyed,
            ItemDamageEngine.OnTakeDamage(armor, 1, null, DamageType.HitSlash));
        Assert.True(armor.IsDeleted);
    }

    [Fact]
    public void SelfRepairMendsTwoInsteadOfTakingTheBlow()
    {
        var (_, sword) = Ground(ItemType.WeaponSword);
        sword.HitsMax = 20;
        sword.HitsCur = 10;
        sword.SetTag("SELFREPAIR", "5");
        ItemDamageEngine.RandVal = _ => 0;   // 5 > 0

        Assert.Equal(0, ItemDamageEngine.OnTakeDamage(sword, 3, null, DamageType.HitBlunt));
        Assert.Equal(12, sword.HitsCur);
    }

    [Fact]
    public void ClothingBurnsInFire()
    {
        var (_, shirt) = Ground(ItemType.Clothing);
        shirt.HitsMax = 10;
        shirt.HitsCur = 10;

        Assert.Equal(2, ItemDamageEngine.OnTakeDamage(shirt, 7, null, DamageType.Fire));
        Assert.Equal(9, shirt.HitsCur);
    }

    [Fact]
    public void AMissedArrowUsuallySurvivesAndAHitOneUsuallyBreaks()
    {
        var (_, arrow) = Ground(ItemType.WeaponArrow);

        ItemDamageEngine.RandVal = _ => 1;
        Assert.Equal(0, ItemDamageEngine.OnTakeDamage(arrow, 1, null, DamageType.HitPierce));
        Assert.False(arrow.IsDeleted);

        ItemDamageEngine.RandVal = _ => 0;
        Assert.Equal(1, ItemDamageEngine.OnTakeDamage(arrow, 12, null, DamageType.HitPierce));
        Assert.False(arrow.IsDeleted);

        ItemDamageEngine.RandVal = _ => 2;
        Assert.Equal(ItemDamageEngine.Destroyed,
            ItemDamageEngine.OnTakeDamage(arrow, 12, null, DamageType.HitPierce));
        Assert.True(arrow.IsDeleted);
    }

    [Fact]
    public void AWebIsWeakenedByBlowsAndDestroyedByFire()
    {
        var (world, web) = Ground(ItemType.Web);
        web.More1 = 10;

        Assert.Equal(0, ItemDamageEngine.OnTakeDamage(web, 5, null, DamageType.Poison));
        Assert.Equal(10u, web.More1);

        Assert.Equal(1, ItemDamageEngine.OnTakeDamage(web, 4, null, DamageType.HitSlash));
        Assert.Equal(6u, web.More1);

        Assert.Equal(ItemDamageEngine.Destroyed,
            ItemDamageEngine.OnTakeDamage(web, 1, null, DamageType.Fire));
        Assert.True(web.IsDeleted);

        var tough = world.CreateItem();
        tough.ItemType = ItemType.Web;
        tough.More1 = 3;
        world.PlaceItem(tough, new Point3D(100, 101, 0, 0));
        Assert.Equal(ItemDamageEngine.Destroyed,
            ItemDamageEngine.OnTakeDamage(tough, 4, null, DamageType.HitBlunt));
        Assert.True(tough.IsDeleted);
    }

    [Fact]
    public void AnExplosionPotionThatIsDamagedExplodesOnThoseAround()
    {
        var (world, potion) = Ground(ItemType.Potion);
        potion.More1 = (uint)SpellType.Explosion;
        potion.More2 = 500;
        potion.Amount = 2;
        Character.ResolveSpellDef = s => s == SpellType.Explosion
            ? new SpellDef { Id = SpellType.Explosion, EffectBase = 12, EffectScale = 12 }
            : null;

        var near = world.CreateCharacter();
        near.MaxHits = 100; near.Hits = 100;
        world.PlaceCharacter(near, new Point3D(101, 100, 0, 0));
        var far = world.CreateCharacter();
        far.MaxHits = 100; far.Hits = 100;
        world.PlaceCharacter(far, new Point3D(110, 100, 0, 0));

        Assert.True(potion.TryExecuteCommand("DAMAGE", "1,0x8", new Console()));

        Assert.Equal((short)88, near.Hits);
        Assert.Equal((short)100, far.Hits);
        Assert.Equal(1, potion.Amount);   // ConsumeAmount: one of the stack
    }

    [Fact]
    public void AnOrdinaryPotionTakesTheBlowUnharmed()
    {
        var (_, potion) = Ground(ItemType.Potion);
        potion.More1 = (uint)SpellType.Heal;
        Assert.Equal(1, ItemDamageEngine.OnTakeDamage(potion, 5, null, DamageType.Fire));
        Assert.False(potion.IsDeleted);
    }

    [Fact]
    public void TheDamageTriggerStillVetoesEveryBranch()
    {
        var (_, arrow) = Ground(ItemType.WeaponArrow);
        CombatEngine.OnItemDamaged = (_, _, _, _) => true;
        ItemDamageEngine.RandVal = _ => 0;
        Assert.Equal(0, ItemDamageEngine.OnTakeDamage(arrow, 1, null, DamageType.HitPierce));
        Assert.False(arrow.IsDeleted);
    }

    // --- ATTR_CANNOTREPAIR ------------------------------------------------

    [Fact]
    public void TheHighAttributeBitsSurviveTheAttrPropertyAndRefuseRepair()
    {
        var (_, plate) = Ground(ItemType.Armor);
        Assert.True(ClientItemUseHandler.IsArmorRepairable(plate));

        // ATTR_CANNOTREPAIR|ATTR_NEWBIE as a save or script writes it.
        Assert.True(plate.TrySetProperty("ATTR", "0400000000004"));
        Assert.True(plate.IsAttr(ObjAttributes.CannotRepair));
        Assert.True(plate.IsAttr(ObjAttributes.Newbie));
        Assert.True(plate.TryGetProperty("ATTR", out string attr));
        Assert.Equal((0x400000000000UL | 0x4UL).ToString(), attr);
        Assert.False(ClientItemUseHandler.IsArmorRepairable(plate));

        // The decimal read-back feeds a later write unchanged.
        Assert.True(plate.TrySetProperty("ATTR", attr));
        Assert.True(plate.IsAttr(ObjAttributes.CannotRepair));
    }

    // --- Use_Drink --------------------------------------------------------

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher triggers)
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8411);
        client.SetEngines(triggerDispatcher: triggers,
            skillHandlers: new SphereNet.Game.Skills.SkillHandlers(world));
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    private const ushort EmptyBottle = 0x0F0E;

    private static int BottlesAfterDrinking(long? returnNumber, TriggerResult result)
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "Drink", (_, args) =>
        {
            args.Locals!.SetInt("BottleId", EmptyBottle);
            args.N2 = 2;                        // two units consumed
            args.ReturnNumber = returnNumber;
            return result;
        });
        var bench = Setup(triggers);
        var ale = bench.World.CreateItem();
        ale.BaseId = 0x099F;
        ale.ItemType = ItemType.Booze;
        ale.Amount = 3;
        Assert.True(bench.Pack.TryAddItem(ale));

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        if (returnNumber == 1)
            Assert.Equal(3, ale.Amount);        // RETURN 1: nothing happens
        else
            Assert.Equal(1, ale.Amount);        // every other return still consumes ARGN2
        return bench.Pack.Contents.Where(i => i.BaseId == EmptyBottle).Sum(i => i.Amount);
    }

    [Fact]
    public void DrinkReturnsDecideTheEmptyBottles()
    {
        Assert.Equal(2, BottlesAfterDrinking(null, TriggerResult.Default));  // ARGN2 bottles
        Assert.Equal(0, BottlesAfterDrinking(1, TriggerResult.True));        // RETURN 1 stops
        Assert.Equal(1, BottlesAfterDrinking(5, TriggerResult.True));        // TRIGRET_ELSEIF
        Assert.Equal(0, BottlesAfterDrinking(6, TriggerResult.True));        // TRIGRET_RET_HALFBAKED
    }

    [Fact]
    public void EnhancePotionsRaisesThePotionStrength()
    {
        var world = TestHarness.CreateWorld();
        var me = world.CreateCharacter();
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        var potion = world.CreateItem();
        potion.ItemType = ItemType.Potion;
        potion.More2 = 400;

        Assert.Equal(400, ClientWorldFeaturesHandler.PotionStrength(me, potion));

        me.SetTag("ENHANCEPOTIONS", "15");
        var ring = world.CreateItem();
        ring.ItemType = ItemType.Jewelry;
        ring.SetTag("ENHANCEPOTIONS", "10");
        Assert.True(me.Equip(ring, Layer.Ring));

        // iSkillQuality += IMulDiv(iSkillQuality, 25, 100) = 400 + 100.
        Assert.Equal(500, ClientWorldFeaturesHandler.PotionStrength(me, potion));
    }
}
