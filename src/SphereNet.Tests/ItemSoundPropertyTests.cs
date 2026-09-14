using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The item sound properties, and the difference between having one and playing it
/// (port plan İŞ-67 / PLAN-305).
///
/// PLAN-305 asks for exactly that separation, and the door is why. A door already
/// played the right sounds - 0x00EA opening, 0x00F1 closing - but it reached them
/// by hard-coding the numbers, so DOOROPENSOUND and DOORCLOSESOUND did nothing. A
/// shard could set them, hear the default, and have no way to tell that the key was
/// ignored rather than misconfigured.
///
/// Upstream resolves all of these the same way: GetDefKey(name, true) reads the
/// INSTANCE first and falls back to the ITEMDEF (CItem.cpp:2690), a zero means "not
/// set", and only then does the type default apply. Doors take their defaults from
/// the definition's own door block before the override
/// (CItem.cpp:4655-4665); pickup falls back to SOUND_USE_CLOTH = 0x57
/// (CClientEvent.cpp:239, uofiles_enums.h:96).
/// </summary>
public sealed class ItemSoundPropertyTests
{
    private readonly ITestOutputHelper _out;
    public ItemSoundPropertyTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Thing(GameWorld world, ItemType type = ItemType.Normal)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        it.ItemType = type;
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return it;
    }

    // ---- pickup ----------------------------------------------------------

    [Fact]
    public void PickingSomethingUpFallsBackToTheClothSound()
    {
        var world = NewWorld();
        var it = Thing(world);

        _out.WriteLine($"pickup sound: 0x{it.GetPickupSound():X3}");

        // SOUND_USE_CLOTH (uofiles_enums.h:96), the fallback upstream uses when the
        // item names no sound of its own.
        Assert.Equal(0x057, it.GetPickupSound());
    }

    [Fact]
    public void PickupSoundOnTheItemWins()
    {
        var world = NewWorld();
        var it = Thing(world);
        it.SetTag("PICKUPSOUND", "0x123");

        _out.WriteLine($"pickup sound: 0x{it.GetPickupSound():X3}");
        Assert.Equal(0x123, it.GetPickupSound());
    }

    [Fact]
    public void AZeroIsNotAnOverride()
    {
        var world = NewWorld();
        var it = Thing(world);
        it.SetTag("PICKUPSOUND", "0");

        // "Not set" and "set to nothing" are the same thing upstream - the override
        // only applies when the value is non-zero. Treating 0 as a choice would mean
        // a script could silence an item by accident and never find out why.
        Assert.Equal(0x057, it.GetPickupSound());
    }

    // ---- doors -----------------------------------------------------------

    [Fact]
    public void ADoorWithoutOverridesUsesTheClassicPair()
    {
        var world = NewWorld();
        var door = Thing(world, ItemType.Door);

        _out.WriteLine($"door open 0x{door.GetDoorSound(opening: true):X3}, " +
                       $"close 0x{door.GetDoorSound(opening: false):X3}");

        Assert.Equal(0x0EA, door.GetDoorSound(opening: true));
        Assert.Equal(0x0F1, door.GetDoorSound(opening: false));
    }

    [Fact]
    public void ADoorOverrideIsHonouredForEachDirectionSeparately()
    {
        var world = NewWorld();
        var door = Thing(world, ItemType.Door);
        door.SetTag("DOOROPENSOUND", "0x200");

        _out.WriteLine($"door open 0x{door.GetDoorSound(opening: true):X3}, " +
                       $"close 0x{door.GetDoorSound(opening: false):X3}");

        // Two keys, two directions. Overriding one must not disturb the other - a
        // creaking gate that slams shut is a normal thing for a shard to want.
        Assert.Equal(0x200, door.GetDoorSound(opening: true));
        Assert.Equal(0x0F1, door.GetDoorSound(opening: false));
    }

    [Fact]
    public void ADoorCloseOverrideIsHonouredToo()
    {
        var world = NewWorld();
        var door = Thing(world, ItemType.Door);
        door.SetTag("DOORCLOSESOUND", "0x201");

        Assert.Equal(0x0EA, door.GetDoorSound(opening: true));
        Assert.Equal(0x201, door.GetDoorSound(opening: false));
    }

    // ---- the resolution order shared by all of them ----------------------

    [Fact]
    public void EveryOneOfThemReadsAnInstanceKeyTheSameWay()
    {
        var world = NewWorld();
        var it = Thing(world);

        it.SetTag("DROPSOUND", "0x301");
        it.SetTag("EQUIPSOUND", "0x302");
        it.SetTag("PICKUPSOUND", "0x303");

        _out.WriteLine($"drop 0x{it.GetDropSound(false):X3} equip 0x{it.GetEquipSound():X3} " +
                       $"pickup 0x{it.GetPickupSound():X3}");

        // One resolution rule for the family, not three near-copies: instance first,
        // then the definition, zero meaning unset.
        Assert.Equal(0x301, it.GetDropSound(ontoSomething: false));
        Assert.Equal(0x302, it.GetEquipSound());
        Assert.Equal(0x303, it.GetPickupSound());
    }

    [Fact]
    public void AnOverrideBeatsTheTypeDefaultRatherThanTheOtherWayRound()
    {
        var world = NewWorld();
        var gold = Thing(world, ItemType.Gold);
        gold.Amount = 1;

        ushort withoutOverride = gold.GetDropSound(ontoSomething: false);
        gold.SetTag("DROPSOUND", "0x400");
        ushort withOverride = gold.GetDropSound(ontoSomething: false);
        _out.WriteLine($"gold drop: default 0x{withoutOverride:X3}, overridden 0x{withOverride:X3}");

        // Gold picks its sound from the amount, and the script still wins: upstream
        // applies the type rule first and lets the key replace the result
        // (CItem.cpp:1541). A default that beat the override would make the key look
        // supported and behave as though it were not - the exact failure this wave is
        // about.
        Assert.Equal(0x035, withoutOverride);
        Assert.Equal(0x400, withOverride);
    }
}
