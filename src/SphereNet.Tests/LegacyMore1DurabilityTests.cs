using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A Source-X save keeps an armor's or weapon's durability in MORE1 (low word =
/// current hits, high word = maximum; CItem.h m_itArmor / m_itWeapon). The loader
/// moves it into the hits fields once the item's type is known.
/// </summary>
public sealed class LegacyMore1DurabilityTests
{
    [Fact]
    public void Weapon_More1_BecomesHits_AndMore1IsCleared()
    {
        var sword = new Item { ItemType = ItemType.WeaponSword, More1 = 0x0032_0028 }; // max 50, cur 40
        Assert.True(sword.MigrateLegacyMore1Hits());
        Assert.Equal(40, sword.HitsCur);
        Assert.Equal(50, sword.HitsMax);
        Assert.Equal(0u, sword.More1);
    }

    [Theory]
    [InlineData(ItemType.Armor)]
    [InlineData(ItemType.Shield)]
    [InlineData(ItemType.Clothing)]
    [InlineData(ItemType.Wand)]
    public void ArmorClothingAndWands_AreMigrated(ItemType type)
    {
        var item = new Item { ItemType = type, More1 = 0x0019_0019 };
        Assert.True(item.MigrateLegacyMore1Hits());
        Assert.Equal(25, item.HitsCur);
        Assert.Equal(25, item.HitsMax);
    }

    [Fact]
    public void MissingMaximum_FollowsCurrentHits()
    {
        var armor = new Item { ItemType = ItemType.ArmorChain, More1 = 30 };
        Assert.True(armor.MigrateLegacyMore1Hits());
        Assert.Equal(30, armor.HitsCur);
        Assert.Equal(30, armor.HitsMax);
    }

    [Fact]
    public void ItemWithOwnHits_IsLeftAlone()
    {
        var sword = new Item { ItemType = ItemType.WeaponSword, More1 = 0x0032_0028, HitsCur = 10, HitsMax = 20 };
        Assert.False(sword.MigrateLegacyMore1Hits());
        Assert.Equal(10, sword.HitsCur);
        Assert.Equal(0x0032_0028u, sword.More1);
    }

    [Fact]
    public void OtherTypes_KeepTheirMore1()
    {
        var box = new Item { ItemType = ItemType.Container, More1 = 0x0032_0028 };
        Assert.False(box.MigrateLegacyMore1Hits());
        Assert.Equal(0x0032_0028u, box.More1);
        Assert.Equal(0, box.HitsCur);
    }

    [Fact]
    public void IniProduct_EvaluatesReferenceForm()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "[SPHERE]\nMinCharDeleteTime=7*24*60*60\nCriminalTimer=3\nCombatParryingEra=01|010\nRegen3=60*60*24\n");
            var parser = new IniParser();
            parser.Load(path);
            var cfg = new SphereConfig();
            cfg.LoadFromIni(parser);
            Assert.Equal(604800, cfg.MinCharDeleteTime);
            Assert.Equal(3, cfg.CriminalTimer);
            Assert.Equal(0x11, cfg.CombatParryingEra);
            Assert.Equal(86400, cfg.RegenFood);
        }
        finally { System.IO.File.Delete(path); }
    }
}
