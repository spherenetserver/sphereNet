using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which references a copy re-points and which it leaves alone (port plan İŞ-54 /
/// PLAN-103).
///
/// The two sides of duplication treat references DIFFERENTLY, and the difference is
/// upstream's, not an oversight:
///
///   CChar::DupeFrom walks the equipment of the character it copied and rewrites
///   every MORE1/MORE2/LINK that pointed at the OLD character to point at the new
///   one (CChar.cpp:1222-1229). A worn memory, a pet link or a quest marker
///   otherwise keeps working on the original.
///
///   CItem::DupeCopy copies m_uidLink raw (CItem.cpp:4117). An item linked to
///   itself produces a copy still linked to the SOURCE, and upstream does not
///   correct it.
///
/// Written side by side those look inconsistent, which is exactly why they are
/// pinned here: harmonising them would be a one-line change that silently diverges
/// from the reference in whichever direction the person preferred.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CopyReferenceContractTests
{
    private readonly ITestOutputHelper _out;
    public CopyReferenceContractTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, string name, short x)
    {
        var ch = world.CreateCharacter();
        ch.Name = name;
        ch.BaseId = 0x0190;
        ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D(x, 150, 0, 0));
        return ch;
    }

    // ---- 1. self references on a character -------------------------------

    [Fact]
    public void AWornMemoryPointingAtItsOwnWearerFollowsTheCopy()
    {
        var world = NewWorld();
        var source = MakeChar(world, "Source", 150);

        var memory = world.CreateItem();
        memory.BaseId = 0x14F0;
        memory.More1 = source.Uid.Value;
        memory.More2 = source.Uid.Value;
        memory.Link = source.Uid;
        Assert.True(source.Equip(memory, Layer.Special));

        var copy = source.CreateDupe(world);
        var copied = copy.GetEquippedItem(Layer.Special);
        Assert.NotNull(copied);
        _out.WriteLine($"source {source.Uid}, copy {copy.Uid}, " +
                       $"copied memory more1 {copied!.More1:X} more2 {copied.More2:X} link {copied.Link}");

        // CChar.cpp:1222-1229: every reference that named the character being copied
        // names the copy instead. Leaving them would mean the copy's own memory kept
        // working on the original.
        Assert.Equal(copy.Uid.Value, copied.More1);
        Assert.Equal(copy.Uid.Value, copied.More2);
        Assert.Equal(copy.Uid, copied.Link);
    }

    // ---- 2. external references ------------------------------------------

    [Fact]
    public void AReferenceToSomebodyElseIsLeftExactlyWhereItPointed()
    {
        var world = NewWorld();
        var source = MakeChar(world, "Source", 150);
        var stranger = MakeChar(world, "Stranger", 160);

        var marker = world.CreateItem();
        marker.BaseId = 0x14F0;
        marker.More1 = stranger.Uid.Value;
        marker.Link = stranger.Uid;
        Assert.True(source.Equip(marker, Layer.Special));

        var copy = source.CreateDupe(world);
        var copied = copy.GetEquippedItem(Layer.Special);
        Assert.NotNull(copied);

        // Only references to the copied character are rewritten. A quest marker or a
        // guild link naming a third party has to keep naming that third party, or
        // duplicating an NPC would quietly re-target whatever it remembered.
        Assert.Equal(stranger.Uid.Value, copied!.More1);
        Assert.Equal(stranger.Uid, copied.Link);
    }

    // ---- 3. the item side does NOT re-point ------------------------------

    [Fact]
    public void AnItemLinkedToItselfCopiesTheLinkRawAsUpstreamDoes()
    {
        var world = NewWorld();
        var src = world.CreateItem();
        src.BaseId = 0x0EED;
        world.PlaceItem(src, new Point3D(50, 50, 0, 0));
        src.Link = src.Uid;

        var copy = src.CreateDupe(world);
        _out.WriteLine($"source {src.Uid}, copy {copy.Uid}, copy link {copy.Link}");

        // CItem::DupeCopy assigns m_uidLink = pItem->m_uidLink and stops
        // (CItem.cpp:4117). There is no self-reference pass on the item side, so the
        // copy's link still names the source. This is pinned rather than "fixed":
        // making it point at the copy would be a deliberate divergence, and the
        // asymmetry with the character path above is upstream's own.
        Assert.Equal(src.Uid, copy.Link);
        Assert.NotEqual(copy.Uid, copy.Link);
    }

    // ---- 4. independent identity ----------------------------------------

    [Fact]
    public void EveryObjectInACopiedTreeGetsItsOwnUid()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(50, 50, 0, 0));

        var inner = world.CreateItem();
        inner.BaseId = 0x0E75;
        inner.ItemType = ItemType.Container;
        box.AddItem(inner);

        var deep = world.CreateItem();
        deep.BaseId = 0x1F03;
        inner.AddItem(deep);

        var copy = box.CreateDupe(world);
        var copyInner = copy.Contents[0];
        var copyDeep = copyInner.Contents[0];

        var seen = new System.Collections.Generic.HashSet<uint>
        {
            box.Uid.Value, inner.Uid.Value, deep.Uid.Value,
            copy.Uid.Value, copyInner.Uid.Value, copyDeep.Uid.Value,
        };
        _out.WriteLine($"six objects, {seen.Count} distinct uids");

        // A shared uid anywhere in the tree means the two trees are the same objects
        // wearing two names, and deleting one would take the other's children.
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void ACopiedChildIsHeldByTheCopyAndNotByTheSource()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(50, 50, 0, 0));

        var child = world.CreateItem();
        child.BaseId = 0x1F03;
        box.AddItem(child);

        var copy = box.CreateDupe(world);

        // The containment link is the one reference that MUST be re-pointed on the
        // item side, because it is what makes the copy a tree of its own rather than
        // a second view of the source's.
        Assert.Single(box.Contents);
        Assert.Single(copy.Contents);
        Assert.Equal(copy.Uid, copy.Contents[0].ContainedIn);
        Assert.Equal(box.Uid, box.Contents[0].ContainedIn);
    }

    // ---- 5. the copied tree itself --------------------------------------

    [Fact]
    public void ACopiedTreeKeepsEveryNameAndPositionItHad()
    {
        var world = NewWorld();
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        box.Name = "a named box";
        world.PlaceItem(box, new Point3D(50, 50, 0, 0));

        var child = world.CreateItem();
        child.BaseId = 0x1F03;
        child.Name = "a child";
        box.AddItem(child);
        child.Position = new Point3D(33, 44, 0, 0);

        var direct = box.CreateDupe(world);

        // The scripted DUPE verb and this direct call are the same code path, and
        // DuplicationParity13JTests.TheDupeVerbMakesTheSameCopyTheOtherPathDoes
        // pins that. What is measured here is the tree the path produces: names and
        // in-container positions survive, so a copied backpack is not an unsorted
        // heap of nameless things.
        _out.WriteLine($"direct copy holds {direct.Contents.Count}");
        Assert.Single(direct.Contents);
        Assert.Equal("a child", direct.Contents[0].Name);
        Assert.Equal(33, direct.Contents[0].X);
        Assert.Equal(44, direct.Contents[0].Y);
        Assert.Equal("a named box", direct.Name);
    }
}
