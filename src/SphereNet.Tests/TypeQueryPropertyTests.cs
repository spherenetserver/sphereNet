using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// ISARMOR / ISWEAPON / DISTANCE, as upstream answers them (CObjBase.cpp:1470,
/// :1509, :1252). All three are asked of ANY object, and the first two take an
/// optional argument - a uid or an itemdef name - so a script can classify a thing
/// it does not hold. The shipped pin system asks ISWEAPON and ISARMOR about a
/// layer it just looked up; with nothing answering, both read as 0 and every
/// branch behind them took the wrong side silently.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TypeQueryPropertyTests
{
    private static (GameWorld World, SphereNet.Game.Objects.Characters.Character Ch) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item Make(GameWorld world, ItemType type, short x = 101)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        it.ItemType = type;
        world.PlaceItem(it, new Point3D(x, 100, 0, 0));
        return it;
    }

    [Fact]
    public void AnItemClassifiesItself()
    {
        var (world, _) = Setup();
        Assert.True(Make(world, ItemType.Shield).TryGetProperty("ISARMOR", out string shield));
        Assert.Equal("1", shield);
        Assert.True(Make(world, ItemType.WeaponSword).TryGetProperty("ISWEAPON", out string sword));
        Assert.Equal("1", sword);
        Assert.True(Make(world, ItemType.Food).TryGetProperty("ISARMOR", out string food));
        Assert.Equal("0", food);
    }

    [Fact]
    public void AWandCountsAsAWeapon()
    {
        // Upstream says so in a comment and its own IsWeaponType deliberately does
        // not - the two answer different questions and must not be shared.
        var (world, _) = Setup();
        var wand = Make(world, ItemType.Wand);
        Assert.True(wand.TryGetProperty("ISWEAPON", out string v));
        Assert.Equal("1", v);
        Assert.False(wand.IsWeaponType);
    }

    [Fact]
    public void AUidArgumentClassifiesThatObjectInstead()
    {
        var (world, ch) = Setup();
        var shield = Make(world, ItemType.Shield);
        Assert.True(ch.TryGetProperty($"ISARMOR 0{shield.Uid.Value:X}", out string v));
        Assert.Equal("1", v);
    }

    [Fact]
    public void ACharacterWithNoArgumentIsNeither()
    {
        var (_, ch) = Setup();
        Assert.True(ch.TryGetProperty("ISWEAPON", out string w));
        Assert.Equal("0", w);
        Assert.True(ch.TryGetProperty("ISARMOR", out string a));
        Assert.Equal("0", a);
    }

    [Fact]
    public void DistanceIsMeasuredBetweenTopLevelPositions()
    {
        // A thing in a backpack answers for its carrier, which is the whole point
        // of measuring from the top-level object.
        var (world, ch) = Setup();
        var far = Make(world, ItemType.Normal, x: 105);
        Assert.True(ch.TryGetProperty($"DISTANCE 0{far.Uid.Value:X}", out string d));
        Assert.Equal("5", d);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        var inside = world.CreateItem();
        Assert.True(pack.TryAddItem(inside));
        Assert.True(far.TryGetProperty($"DISTANCE 0{inside.Uid.Value:X}", out string carried));
        Assert.Equal("5", carried);
    }

    [Fact]
    public void DistanceWithNoTargetDoesNotAnswer()
    {
        // The bare word is the caller's business - this getter has nothing to
        // measure against and must not invent a zero.
        var (_, ch) = Setup();
        Assert.False(ch.TryGetProperty("DISTANCE", out _));
    }
}
