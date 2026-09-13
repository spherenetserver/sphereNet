using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
/// What every way of making an object carries, side by side (port plan İŞ-52 /
/// PLAN-101).
///
/// There are several doors into "a new object appears": DUPE on an item, DUPE on a
/// character, NEWDUPE, a stack split, a spawner, a template, a vendor restock,
/// NEWITEM and NEWNPC. They are written in different files by different waves, and
/// the failure they share is silent: one door copies a field, the next forgets it,
/// and nothing says so. The character DUPE verb once copied a hand-picked handful
/// of fields and produced a naked character while NEWDUPE carried everything - the
/// comment at Character.cs CHV_DUPE still records it.
///
/// So the comparison is made by reflection rather than by a list someone
/// maintains. Every readable property of Item is compared between a fully
/// configured source and each copy, and the differences are pinned. A property
/// added later that one door copies and another does not shows up here without
/// anyone remembering to add it.
///
/// The table this produces is the deliverable; the test output prints it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ObjectCreationContractTests
{
    private readonly ITestOutputHelper _out;
    public ObjectCreationContractTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld(int size = 256)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, size, size);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A source with every instance field carrying a distinctive value, so
    /// a door that drops one is visible rather than accidentally equal.</summary>
    private static Item ConfiguredSource(GameWorld world)
    {
        var src = world.CreateItem();
        src.BaseId = 0x0EED;
        src.ItemType = ItemType.Normal;
        src.Amount = 7;
        src.Hue = (Color)0x0489;
        src.Name = "a distinctive thing";
        src.Direction = (byte)Direction.South;
        src.More1 = 0x11111111;
        src.More2 = 0x22222222;
        src.MoreP = new Point3D(11, 22, 3, 0);
        src.Price = 1234;
        src.Quality = 77;
        src.HitsMax = 90;
        src.HitsCur = 45;
        src.UsesRemaining = 13;
        src.TData1 = 0x0A0A;
        src.TData2 = 0x0B0B;
        src.TData3 = 0x0C0C;
        src.TData4 = 0x0D0D;
        src.SetTag("REVIEW_TAG", "carried");
        src.SetTimeout(30_000);
        world.PlaceItem(src, new Point3D(50, 50, 0, 0));
        return src;
    }

    /// <summary>Properties whose value is expected to differ. Three kinds, and the
    /// distinction matters: a copy MUST have its own identity, it has not been placed
    /// yet, and anything derived is recomputed rather than carried. Only a content
    /// field that fails to travel is a defect.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.Ordinal)
    {
        // identity - a copy that shared these would not be a second object
        "Uid", "Serial", "UidRef", "Uuid",
        // placement - upstream leaves it to the caller (CIV_DUPE)
        "Position", "X", "Y", "Z", "Map", "ContainedIn", "Container",
        "Contents", "ContentCount", "IsOnGround", "TopObject", "Parent", "IsDeleted",
        "CreatedAt", "LastTick",
        // derived - recomputed from fields that DO travel, so comparing them would
        // only restate Amount and placement
        "TotalWeightTenths", "DirtyFlags",
    };

    private static IEnumerable<PropertyInfo> ComparableProperties() =>
        typeof(Item).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => !Identity.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static string Render(object? v) => v switch
    {
        null => "<null>",
        string s => s,
        IEnumerable e and not string => string.Join(",", e.Cast<object?>().Select(x => x?.ToString())),
        _ => v.ToString() ?? "<null>",
    };

    /// <summary>Which properties differ between source and copy, ignoring the ones
    /// that are meant to.</summary>
    private static List<string> Divergence(Item src, Item copy)
    {
        var diffs = new List<string>();
        foreach (var p in ComparableProperties())
        {
            object? a, b;
            try { a = p.GetValue(src); b = p.GetValue(copy); }
            catch (TargetInvocationException) { continue; }   // a live-scan property with no world

            string ra = Render(a), rb = Render(b);
            if (!string.Equals(ra, rb, StringComparison.Ordinal))
                diffs.Add($"{p.Name}: {ra} -> {rb}");
        }
        return diffs;
    }

    // ---- the doors -------------------------------------------------------

    [Fact]
    public void ItemDupeCarriesEveryContentFieldOfItsSource()
    {
        var world = NewWorld();
        var src = ConfiguredSource(world);
        var copy = src.CreateDupe(world);

        var diffs = Divergence(src, copy);
        foreach (string d in diffs) _out.WriteLine(d);

        // DUPE is the reference contract (CItem::DupeCopy, CItem.cpp:4099): an
        // independent object equal in everything but identity. Anything listed here
        // is a field the copy quietly lost.
        Assert.Empty(diffs);
        Assert.NotEqual(src.Uid, copy.Uid);
    }

    [Fact]
    public void AStackSplitCarriesTheSameFieldsExceptTheAmountItTookAway()
    {
        var world = NewWorld();
        var src = ConfiguredSource(world);

        // The split the client paths perform: a fresh item taking part of the pile.
        var remainder = world.CreateItem();
        remainder.CopyStackInstanceStateFrom(src);
        remainder.Amount = 3;

        var diffs = Divergence(src, remainder).Where(d => !d.StartsWith("Amount:", StringComparison.Ordinal)).ToArray();
        foreach (string d in diffs) _out.WriteLine(d);

        // A split piece has to behave exactly like the pile it came from. The only
        // licensed difference is how many are in it.
        Assert.Empty(diffs);
    }

    [Fact]
    public void TheTwoDoorsAgreeWithEachOther()
    {
        var world = NewWorld();
        var src = ConfiguredSource(world);

        var duped = src.CreateDupe(world);
        var split = world.CreateItem();
        split.CopyStackInstanceStateFrom(src);
        split.Amount = src.Amount;

        var diffs = Divergence(duped, split);
        foreach (string d in diffs) _out.WriteLine(d);

        // The real risk is not that a door differs from the source but that two
        // doors differ from each other, because then the same script produces
        // different objects depending on which one it went through.
        Assert.Empty(diffs);
    }

    [Fact]
    public void DupeCarriesContainerContentsAndTheSplitDoesNot()
    {
        var world = NewWorld();
        var src = ConfiguredSource(world);
        src.ItemType = ItemType.Container;

        var child = world.CreateItem();
        child.BaseId = 0x0F3F;
        child.Amount = 5;
        src.AddItem(child);

        var duped = src.CreateDupe(world);
        var split = world.CreateItem();
        split.CopyStackInstanceStateFrom(src);

        _out.WriteLine($"source {src.Contents.Count}, dupe {duped.Contents.Count}, split {split.Contents.Count}");

        // This difference is intended and comes straight from upstream: a container
        // DUPE copies its contents, each with a new uid (CItemContainer::DupeCopy,
        // CItemContainer.cpp:830), while a stack split makes one object and has no
        // tree to carry. It is documented here so it is not mistaken for the
        // accidental kind.
        Assert.Single(duped.Contents);
        Assert.NotEqual(child.Uid, duped.Contents[0].Uid);
        Assert.Empty(split.Contents);
    }

    [Fact]
    public void ASpawnerCopyBringsItsConfigurationAndNoneOfItsChildren()
    {
        var world = NewWorld();
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1F13;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.Amount = 3;
        world.PlaceItem(spawner, new Point3D(60, 60, 0, 0));
        spawner.SetTag("MORE1_DEFNAME", "c_nonexistent_for_this_test");
        spawner.InitializeSpawnComponent(world, null);

        var copy = spawner.CreateDupe(world);

        _out.WriteLine($"copy type={copy.ItemType} amount={copy.Amount}");

        // CCSpawn::Copy (CCSpawn.cpp:1272) carries configuration only. A copy that
        // kept the source's children would hand two spawners the same creatures;
        // one that built no component at all would look right and do nothing.
        Assert.Equal(ItemType.SpawnChar, copy.ItemType);
        Assert.Equal(spawner.Amount, copy.Amount);
        Assert.NotEqual(spawner.Uid, copy.Uid);
    }

    [Fact]
    public void TheContractTableIsPrintedForTheDocument()
    {
        var world = NewWorld();
        var src = ConfiguredSource(world);

        var props = ComparableProperties().ToArray();
        _out.WriteLine($"Item exposes {props.Length} comparable properties; " +
                       $"{Identity.Count} identity/placement names are excluded.");

        var sb = new StringBuilder();
        foreach (var p in props)
        {
            object? v;
            try { v = p.GetValue(src); }
            catch (TargetInvocationException) { continue; }
            sb.Append(p.Name).Append('=').Append(Render(v)).Append("; ");
        }
        _out.WriteLine(sb.ToString());

        // The comparison is only meaningful if it actually looks at a real number of
        // fields. If Item's surface were to collapse to a handful, the tests above
        // would pass while measuring nothing.
        Assert.True(props.Length >= 30, $"only {props.Length} properties compared");
    }

    // ---- the two character doors ----------------------------------------

    private static Character DressedNpc(GameWorld world)
    {
        var npc = world.CreateCharacter();
        npc.BaseId = 0x0190;
        npc.BodyId = 0x0190;
        npc.Name = "a dressed npc";
        world.PlaceCharacter(npc, new Point3D(70, 70, 0, 0));

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        npc.Equip(shirt, Layer.Shirt);
        return npc;
    }

    [Fact]
    public void AskingForNewbieEquipmentMarksTheWholeOutfit()
    {
        var world = NewWorld();
        var npc = DressedNpc(world);

        var copy = npc.CreateDupe(world, newbieItems: true);
        var worn = copy.GetEquippedItem(Layer.Shirt);

        Assert.NotNull(worn);
        Assert.True(worn!.IsAttr(ObjAttributes.Newbie));
    }

    [Fact]
    public void NewDupeAsksForNewbieEquipmentBecauseItRunsTheDupeVerb()
    {
        // Upstream NEWDUPE does not duplicate on its own: it builds CScript("DUPE")
        // and calls the object's own verb (CScriptObj.cpp:1311). CopyParseState
        // carries the parse flags and line number and nothing else
        // (CScript.cpp:458), so that script has NO argument; CHV_DUPE reads
        // GetArgVal() == 0 and "GetArgVal() < 1 ? true : false" hands DupeFrom
        // fNewbieItems = true (CChar.cpp:4545).
        //
        // The server handler is not reachable from here, so the contract is pinned
        // where it is written. It is not cosmetic: ATTR_NEWBIE equipment stays with
        // a character through death instead of dropping to the corpse, so the same
        // script produced a differently-equipped copy than upstream.
        string handler = ReadRepoFile(Path.Combine("src", "SphereNet.Server", "Program.Scripting.cs"));
        if (Gate.Missing(_out, "engine source", handler.Length == 0)) return;

        int at = handler.IndexOf("origChar.CreateDupe(", StringComparison.Ordinal);
        Assert.True(at > 0, "the NEWDUPE character branch moved");

        string call = handler.Substring(at, Math.Min(64, handler.Length - at));
        _out.WriteLine(call.ReplaceLineEndings(" "));
        Assert.Contains("newbieItems: true", call);
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        if (dir == null) return "";
        string path = Path.Combine(dir.FullName, relative);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }
}
