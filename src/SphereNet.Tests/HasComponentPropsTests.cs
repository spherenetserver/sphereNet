using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// HASCOMPONENTPROPS answers which component block an object carries.
///
/// Upstream's OBC_HASCOMPONENTPROPS (CBase.cpp:189) returns 1 when the object has
/// subscribed that component, and the subscription predicates are plain type tests -
/// CCPropsItemEquippable::CanSubscribe is CItemBase::IsTypeEquippable, the weapon one
/// is a list of t_weapon_* plus the fishing pole and instruments, the ranged one is
/// bow and crossbow. The reference pack names the ids in core/defs_component_props.scp
/// exactly as the COMPPROPS_TYPE enum orders them.
///
/// The read had no case here and came back empty, which reads as 0. The reference
/// pack's equipment tooltip opens with
///
///     IF !(&lt;HasComponentProps &lt;DEF.CompProps_ItemEquippable&gt;&gt;)
///        RETURN 1
///
/// so every AOS property line on every piece of gear was abandoned before it was
/// written - and only once the TEVENTS-to-typedef resolution and the RETURN-inside-IF
/// repair had made that block reachable and that RETURN real.
/// </summary>
public sealed class HasComponentPropsTests
{
    private const int ItemChar = 0, Char = 1, ItemComp = 2, Equippable = 3, Weapon = 4, Ranged = 5;

    private static Item ItemOf(ItemType type)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var it = world.CreateItem();
        it.ItemType = type;
        return it;
    }

    private static int Ask(ObjBase obj, int id) =>
        obj.TryGetProperty($"HASCOMPONENTPROPS {id}", out string v) && int.TryParse(v, out int n) ? n : -1;

    [Fact]
    public void AWeaponCarriesTheWeaponAndEquippableBlocks()
    {
        var sword = ItemOf(ItemType.WeaponSword);
        Assert.Equal(1, Ask(sword, Weapon));
        Assert.Equal(1, Ask(sword, Equippable));
        Assert.Equal(0, Ask(sword, Ranged));
        Assert.Equal(0, Ask(sword, Char));
    }

    [Fact]
    public void ABowIsAlsoRanged()
    {
        var bow = ItemOf(ItemType.WeaponBow);
        Assert.Equal(1, Ask(bow, Ranged));
        Assert.Equal(1, Ask(bow, Weapon));
        Assert.Equal(1, Ask(bow, Equippable));
    }

    /// <summary>The tooltip gate: a piece of armour has to answer 1 here or the whole
    /// property block is abandoned.</summary>
    [Theory]
    [InlineData(ItemType.Armor)]
    [InlineData(ItemType.ArmorLeather)]
    [InlineData(ItemType.ArmorChain)]
    [InlineData(ItemType.Shield)]
    [InlineData(ItemType.Jewelry)]
    [InlineData(ItemType.Spellbook)]
    [InlineData(ItemType.Talisman)]
    public void EquippableGearAnswersForTheEquippableBlock(ItemType type)
    {
        Assert.Equal(1, Ask(ItemOf(type), Equippable));
    }

    /// <summary>And something that is not worn does not.</summary>
    [Theory]
    [InlineData(ItemType.Normal)]
    [InlineData(ItemType.Container)]
    [InlineData(ItemType.Food)]
    public void PlainGoodsDoNotCarryTheEquippableBlock(ItemType type)
    {
        var it = ItemOf(type);
        Assert.Equal(0, Ask(it, Equippable));
        Assert.Equal(0, Ask(it, Weapon));
        // but every item carries the item blocks
        Assert.Equal(1, Ask(it, ItemComp));
        Assert.Equal(1, Ask(it, ItemChar));
    }

    /// <summary>A character carries CHAR and ITEMCHAR and none of the item ones
    /// (CChar.cpp:334).</summary>
    [Fact]
    public void ACharacterCarriesTheCharBlocks()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.Equal(1, Ask(ch, Char));
        Assert.Equal(1, Ask(ch, ItemChar));
        Assert.Equal(0, Ask(ch, ItemComp));
        Assert.Equal(0, Ask(ch, Equippable));
    }

    /// <summary>An id past the enum is not a component, and neither is a missing
    /// one - a script asking for something that does not exist gets 0, not a
    /// crash.</summary>
    [Fact]
    public void AnUnknownIdAnswersZero()
    {
        var it = ItemOf(ItemType.WeaponSword);
        Assert.Equal(0, Ask(it, 6));
        Assert.Equal(0, Ask(it, 99));
        Assert.True(it.TryGetProperty("HASCOMPONENTPROPS", out string bare));
        Assert.Equal("0", bare);
    }
}
