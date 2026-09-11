using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The house's standing moving crate, and reading a house key without a prefix
/// (port plan İŞ-32 / PLAN-501).
///
/// Source-X keeps the crate on the multi (_uidMovingCrate): GetMovingCrate(fCreate)
/// mints an ITEMID_CRATE1 at the house's spot but 20 below it and links it back
/// (CItemMulti.cpp:1329), SetMovingCrate moves a non-empty old crate's contents into
/// the new one before deleting it (:1306), and the uid is written to the save when the
/// house has one (:2666). None of that existed here, so the housing pack's
/// &lt;MovingCrate&gt; reads (house_dialogs.scp:238/277, house_typedefs.scp:632) answered
/// nothing.
///
/// The reference also keeps the housing keys BARE on the multi (CItemMulti::r_WriteVal).
/// Here only the SETTER took the bare form: BASESTORAGE=4688 reached a live house and
/// &lt;BASESTORAGE&gt; then read back nothing at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HouseMovingCrateParityTests
{
    private static (GameWorld World, Item Multi, House House) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        world.PlaceItem(multi, new Point3D(100, 100, 10, 0));

        var house = new House(multi);
        Item.ResolveHouse = uid => uid == multi.Uid ? house : null;
        return (world, multi, house);
    }

    // ---- bare house keys ------------------------------------------------

    [Fact]
    public void AHouseKeyReadsBackWithoutTheHousePrefix()
    {
        var (_, multi, house) = Setup();
        house.BaseStorage = 425;

        Assert.True(multi.TryGetProperty("BASESTORAGE", out string bare));
        Assert.Equal("425", bare);
        Assert.True(multi.TryGetProperty("HOUSE.BASESTORAGE", out string prefixed));
        Assert.Equal(bare, prefixed);
    }

    [Fact]
    public void WhatAScriptWritesIsWhatItReadsBack()
    {
        var (_, multi, house) = Setup();

        Assert.True(multi.TrySetProperty("BASESTORAGE", "999"));
        Assert.Equal(999, house.BaseStorage);
        Assert.True(multi.TryGetProperty("BASESTORAGE", out string v));
        Assert.Equal("999", v);
    }

    [Fact]
    public void AKeyTheItemOwnsStillMeansTheItems()
    {
        // TYPE is both an item key and a house key; the item's must win, which is
        // why the bare housing lookup runs last.
        var (_, multi, house) = Setup();
        house.Type = HouseType.Public;

        Assert.True(multi.TryGetProperty("TYPE", out string type));
        Assert.Equal("t_multi", type);                       // the ITEM type
        Assert.True(multi.TryGetProperty("HOUSE.TYPE", out string houseType));
        Assert.Equal(((byte)HouseType.Public).ToString(), houseType);
    }

    [Fact]
    public void APlainItemAnswersNoHousingKey()
    {
        // The bare path must not turn every unknown key on every item into "0".
        var (world, _, _) = Setup();
        var rock = world.CreateItem();
        rock.BaseId = 0x1363;

        Assert.False(rock.TryGetProperty("BASESTORAGE", out _));
        Assert.False(rock.TryGetProperty("MOVINGCRATE", out _));
    }

    // ---- the crate -------------------------------------------------------

    [Fact]
    public void AHouseWithNoCrateReadsZeroAndMakesNone()
    {
        var (_, multi, house) = Setup();

        Assert.True(multi.TryGetProperty("MOVINGCRATE", out string v));
        Assert.Equal("0", v);
        Assert.Null(house.ResolveMovingCrate());
    }

    [Fact]
    public void AskingWithOneMakesTheCrateUnderTheHouse()
    {
        var (_, multi, house) = Setup();

        Assert.True(multi.TryGetProperty("MOVINGCRATE 1", out string v));
        var crate = house.ResolveMovingCrate();
        Assert.NotNull(crate);
        Assert.Equal($"0{crate!.Uid.Value:X}", v);
        Assert.Equal(House.MovingCrateId, crate.BaseId);
        Assert.Equal(multi.Uid, crate.Link);
        // Source-X puts it at the house's own spot, 20 below (CItemMulti.cpp:1344).
        Assert.Equal(multi.X, crate.X);
        Assert.Equal(multi.Y, crate.Y);
        Assert.Equal(multi.Z - 20, crate.Z);

        // Asking again does not mint a second one.
        Assert.True(multi.TryGetProperty("MOVINGCRATE 1", out string again));
        Assert.Equal(v, again);
    }

    [Fact]
    public void ASubkeyReadsThroughToTheCrate()
    {
        // The pack reads <uid.<movingcrate>.count>; the reference also forwards a
        // subkey straight through (CItemMulti.cpp:2866).
        var (world, multi, house) = Setup();
        var crate = house.GetMovingCrate(create: true);
        Assert.NotNull(crate);

        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(crate!.TryAddItem(goods));

        Assert.True(multi.TryGetProperty("MOVINGCRATE.COUNT", out string count));
        Assert.Equal("1", count);
    }

    [Fact]
    public void ReplacingACrateCarriesTheGoodsAcross()
    {
        // Source-X SetMovingCrate: a non-empty old crate is emptied into the new one
        // and deleted, so swapping crates never strands anything (CItemMulti.cpp:1317).
        var (world, _, house) = Setup();
        var first = house.GetMovingCrate(create: true)!;
        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(first.TryAddItem(goods));

        var second = world.CreateItem();
        second.ItemType = ItemType.Container;
        second.BaseId = House.MovingCrateId;
        house.AssignMovingCrate(second);

        Assert.Same(second, house.ResolveMovingCrate());
        Assert.Contains(goods, second.Contents);
        Assert.True(first.IsDeleted);
    }

    [Fact]
    public void ACrateThatWentAwayIsForgotten()
    {
        var (world, multi, house) = Setup();
        var crate = house.GetMovingCrate(create: true)!;

        world.RemoveItem(crate);
        crate.Delete();

        Assert.Null(house.ResolveMovingCrate());
        Assert.True(multi.TryGetProperty("MOVINGCRATE", out string v));
        Assert.Equal("0", v);
    }

    [Fact]
    public void SettingItToOneIsHowASaveAsksForOne()
    {
        // Source-X SHL_MOVINGCRATE load treats the uid 1 as "make one"
        // (CItemMulti.cpp:3034) - that is what "movingcrate 1" in a save means.
        var (_, multi, house) = Setup();

        Assert.True(multi.TrySetProperty("MOVINGCRATE", "1"));

        Assert.NotNull(house.ResolveMovingCrate());
    }

    [Fact]
    public void SettingItToAUidAdoptsThatCrate()
    {
        var (world, multi, house) = Setup();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = House.MovingCrateId;

        Assert.True(multi.TrySetProperty("MOVINGCRATE", $"0{chest.Uid.Value:X}"));

        Assert.Same(chest, house.ResolveMovingCrate());
        Assert.Equal(multi.Uid, chest.Link);
    }

    [Fact]
    public void SettingItToZeroForgetsTheCrateWithoutDeletingIt()
    {
        var (_, multi, house) = Setup();
        var crate = house.GetMovingCrate(create: true)!;

        Assert.True(multi.TrySetProperty("MOVINGCRATE", "0"));

        Assert.Null(house.ResolveMovingCrate());
        Assert.False(crate.IsDeleted);
    }
}
