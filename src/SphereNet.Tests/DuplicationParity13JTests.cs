using System;
using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a copy carries, and what it must NOT (review 13J).
///
/// Source-X DupeFrom copies the stat block and then duplicates every equipped layer
/// onto the new character (CChar.cpp:1092/1194). Two things follow that SphereNet has
/// to say explicitly: the pools are BASE values here - the effective maximum is derived
/// from the suit on read, so copying the derived total writes the suit's contribution
/// into the copy's base and it compounds - and an equipped item pointing at the
/// character it was worn by is re-pointed at the new one (:1227). The character's own
/// DUPE verb is that same DupeFrom (CChar.cpp:4541), with its argument choosing the
/// newbie contract. A container copy keeps its children where they were
/// (CItemContainer::DupeCopy, CItemContainer.cpp:830).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DuplicationParity13JTests
{
    private static GameWorld NewWorld() => TestHarness.CreateWorld();

    private static Character MakeSource(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.Name = "Source";
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = 100; ch.MaxMana = 100; ch.MaxStam = 100;
        ch.Hits = 100; ch.Mana = 100; ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(150, 150, 0, 0));
        return ch;
    }

    private static Item MakeBonusSuit(GameWorld world)
    {
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.SetTag("BONUSHITSMAX", "20");
        shirt.SetTag("BONUSMANAMAX", "30");
        shirt.SetTag("BONUSSTAMMAX", "40");
        return shirt;
    }

    // ================================================================ 13J-1

    [Fact]
    public void ACopyKeepsTheSourcesBasePoolsRatherThanItsEffectiveOnes()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));

        Assert.Equal(100, source.BaseMaxHits);
        Assert.Equal(120, source.MaxHits);

        var copy = source.CreateDupe(world);

        // The suit's contribution is derived on both, never written into the base.
        Assert.Equal(100, copy.BaseMaxHits);
        Assert.Equal(100, copy.BaseMaxMana);
        Assert.Equal(100, copy.BaseMaxStam);
        Assert.Equal(120, copy.MaxHits);
        Assert.Equal(130, copy.MaxMana);
        Assert.Equal(140, copy.MaxStam);
    }

    [Fact]
    public void CopyingACopyDoesNotKeepStrengtheningIt()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));

        var second = source.CreateDupe(world).CreateDupe(world);

        Assert.Equal(100, second.BaseMaxHits);
        Assert.Equal(120, second.MaxHits);
    }

    [Fact]
    public void TakingTheSuitOffACopyGivesBackTheBasePool()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));

        var copy = source.CreateDupe(world);
        copy.Unequip(Layer.Shirt);

        Assert.Equal(100, copy.MaxHits);
    }

    [Fact]
    public void ACopyStandsAtTheSameFilledPoolsAsItsSource()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));
        source.Hits = source.MaxHits;   // 120, its effective maximum

        var copy = source.CreateDupe(world);

        // The pools are applied after the suit is on, so the copy is not trimmed to
        // the base maximum on its way across.
        Assert.Equal(120, copy.Hits);
    }

    // ================================================================ 13J-2

    [Fact]
    public void AWornItemPointingAtItsWearerIsRePointedAtTheCopy()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        var worn = world.CreateItem();
        worn.BaseId = 0x1517;
        worn.More1 = source.Uid.Value;
        worn.More2 = source.Uid.Value;
        worn.Link = source.Uid;
        Assert.True(source.Equip(worn, Layer.Shirt));

        var copy = source.CreateDupe(world);
        var wornCopy = copy.GetEquippedItem(Layer.Shirt)!;

        Assert.NotEqual(worn.Uid, wornCopy.Uid);
        Assert.Equal(copy.Uid.Value, wornCopy.More1);
        Assert.Equal(copy.Uid.Value, wornCopy.More2);
        Assert.Equal(copy.Uid, wornCopy.Link);
        // ...and the source's own item is untouched.
        Assert.Equal(source.Uid.Value, worn.More1);
    }

    [Fact]
    public void ALinkToSomeoneElseIsLeftAlone()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        var other = world.CreateCharacter();
        var worn = world.CreateItem();
        worn.BaseId = 0x1517;
        worn.More1 = other.Uid.Value;
        worn.Link = other.Uid;
        Assert.True(source.Equip(worn, Layer.Shirt));

        var copy = source.CreateDupe(world);
        var wornCopy = copy.GetEquippedItem(Layer.Shirt)!;

        Assert.Equal(other.Uid.Value, wornCopy.More1);
        Assert.Equal(other.Uid, wornCopy.Link);
    }

    // ================================================================ 13J-3

    [Fact]
    public void TheDupeVerbMakesTheSameCopyTheOtherPathDoes()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        source.Fame = 1000;
        source.ResFire = 25;
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        Assert.True(source.Equip(shirt, Layer.Shirt));

        Assert.True(source.TryExecuteCommand("DUPE", "", new TestConsole()));

        var clone = FindOtherCharacter(world, source);
        Assert.NotNull(clone);
        Assert.Equal("Source", clone!.Name);
        Assert.Equal(1000, clone.Fame);
        Assert.Equal(25, clone.ResFire);
        Assert.NotNull(clone.GetEquippedItem(Layer.Shirt));
    }

    [Fact]
    public void TheDupeArgumentChoosesTheNewbieContract()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        Assert.True(source.Equip(shirt, Layer.Shirt));

        // No argument - and anything below one - is fNewbieItems (CChar.cpp:4541).
        Assert.True(source.TryExecuteCommand("DUPE", "", new TestConsole()));
        var newbieClone = FindOtherCharacter(world, source)!;
        Assert.True(newbieClone.GetEquippedItem(Layer.Shirt)!.IsAttr(ObjAttributes.Newbie));

        var world2 = NewWorld();
        var source2 = MakeSource(world2);
        var shirt2 = world2.CreateItem();
        shirt2.BaseId = 0x1517;
        Assert.True(source2.Equip(shirt2, Layer.Shirt));

        Assert.True(source2.TryExecuteCommand("DUPE", "1", new TestConsole()));
        var plainClone = FindOtherCharacter(world2, source2)!;
        Assert.False(plainClone.GetEquippedItem(Layer.Shirt)!.IsAttr(ObjAttributes.Newbie));
    }

    // ================================================================ 13J-4

    [Fact]
    public void AContainerCopyKeepsItsContentsWhereTheyWere()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;

        var loose = world.CreateItem();
        loose.BaseId = 0x1F03;
        box.AddItem(loose);
        loose.Position = new Point3D(21, 35, 0, 0);

        var innerBox = world.CreateItem();
        innerBox.BaseId = 0x0E75;
        innerBox.ItemType = ItemType.Container;
        box.AddItem(innerBox);
        innerBox.Position = new Point3D(40, 50, 0, 0);

        var deep = world.CreateItem();
        deep.BaseId = 0x1F03;
        innerBox.AddItem(deep);
        deep.Position = new Point3D(60, 70, 0, 0);

        var copy = box.CreateDupe(world);

        Assert.Equal(2, copy.ContentCount);
        Assert.Equal(21, copy.Contents[0].X);
        Assert.Equal(35, copy.Contents[0].Y);
        Assert.Equal(40, copy.Contents[1].X);
        Assert.Equal(50, copy.Contents[1].Y);

        var deepCopy = Assert.Single(copy.Contents[1].Contents);
        Assert.Equal(60, deepCopy.X);
        Assert.Equal(70, deepCopy.Y);

        // The copies are their own objects, and the source is untouched.
        Assert.NotEqual(loose.Uid, copy.Contents[0].Uid);
        Assert.Equal(21, loose.X);
    }

    private static Character? FindOtherCharacter(GameWorld world, Character source)
    {
        foreach (var ch in world.GetAllCharactersSnapshot())
        {
            if (!ReferenceEquals(ch, source) && ch.Name == source.Name)
                return ch;
        }
        return null;
    }

    private sealed class TestConsole : Core.Interfaces.ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }

    // ================================================================ İŞ-4 / PLAN-101
    // The creation entry points are meant to report their result the same way. DUPE on
    // an item hands its copy to CreateDupeItem with the caller and fSetNew set, so both
    // NEW and the caller's ACT name the copy - the last one when the line made several
    // (CItem.cpp:3641 -> :385). DUPE on a CHARACTER does not: upstream's CHV_DUPE calls
    // DupeFrom directly and touches neither (CChar.cpp:4541).

    [Fact]
    public void DuplicatingAnItemPointsBothNewAndTheCallersActAtTheCopy()
    {
        var world = NewWorld();
        var caller = world.CreateCharacter();
        world.PlaceCharacter(caller, new Point3D(150, 150, 0, 0));

        var source = world.CreateItem();
        source.BaseId = 0x1F03;
        world.PlaceItem(source, new Point3D(151, 150, 0, 0));

        Assert.True(source.TryExecuteCommand("DUPE", "", new ActConsole(caller)));

        var copy = world.GetAllObjects().OfType<Item>()
            .Single(i => i != source && i.BaseId == 0x1F03);
        Assert.Equal(copy.Uid, world.LastNewObject);
        Assert.Equal(copy.Uid, caller.Act);
    }

    [Fact]
    public void SeveralCopiesLeaveTheLastOneNamed()
    {
        var world = NewWorld();
        var caller = world.CreateCharacter();
        world.PlaceCharacter(caller, new Point3D(150, 150, 0, 0));
        var source = world.CreateItem();
        source.BaseId = 0x1F03;
        world.PlaceItem(source, new Point3D(151, 150, 0, 0));

        Assert.True(source.TryExecuteCommand("DUPE", "3", new ActConsole(caller)));

        var copies = world.GetAllObjects().OfType<Item>()
            .Where(i => i != source && i.BaseId == 0x1F03).ToList();
        Assert.Equal(3, copies.Count);
        Assert.Contains(copies, c => c.Uid == world.LastNewObject);
        Assert.Equal(world.LastNewObject, caller.Act);
    }

    [Fact]
    public void ATopLevelPileIsBoundedByTheConfiguredComplexity()
    {
        var world = NewWorld();
        var source = world.CreateItem();
        source.BaseId = 0x1F03;
        world.PlaceItem(source, new Point3D(151, 150, 0, 0));

        int previous = Item.MaxItemComplexity;
        Item.MaxItemComplexity = 4;
        try
        {
            Assert.True(source.TryExecuteCommand("DUPE", "50", new ActConsole(null)));
            int copies = world.GetAllObjects().OfType<Item>().Count(i => i != source && i.BaseId == 0x1F03);
            Assert.Equal(4, copies);
        }
        finally { Item.MaxItemComplexity = previous; }
    }

    [Fact]
    public void ACopyInsideAContainerIsNotBoundedByIt()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(152, 150, 0, 0));

        var source = world.CreateItem();
        source.BaseId = 0x1F03;
        Assert.True(box.TryAddItem(source));

        int previous = Item.MaxItemComplexity;
        Item.MaxItemComplexity = 2;
        try
        {
            Assert.True(source.TryExecuteCommand("DUPE", "5", new ActConsole(null)));
            int copies = world.GetAllObjects().OfType<Item>().Count(i => i != source && i.BaseId == 0x1F03);
            Assert.Equal(5, copies);
        }
        finally { Item.MaxItemComplexity = previous; }
    }

    [Fact]
    public void ACopyDoesNotRunTheDefinitionsCreateScriptAgain()
    {
        // A duplicate is a COPY, not a new creation: upstream builds it with CreateBase
        // plus DupeCopy and never reaches the script that fires @Create
        // (CreateDupeItem, CItem.cpp:385). Re-running it would re-roll a magic weapon's
        // properties and the copy would not be a copy.
        var world = NewWorld();
        int fired = 0;
        Item.CreateTriggerHook = _ => fired++;
        try
        {
            var source = world.CreateItem();
            source.BaseId = 0x1F03;
            source.FireCreateTrigger();
            Assert.Equal(1, fired);

            source.CreateDupe(world);
            Assert.Equal(1, fired);
        }
        finally { Item.CreateTriggerHook = null; }
    }

    private sealed class ActConsole(Character? actor) : Core.Interfaces.ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
        public Core.Interfaces.IScriptObj? GetSourceChar() => actor;
    }
}
