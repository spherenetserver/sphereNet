using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Persistence.Save;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What the save snapshot costs, and that it does not grow its lists while filling them.
///
/// The snapshot is the one phase that holds the world still: a shard reported a 622ms
/// loop stall on save, 386ms of it GC pause, with nineteen gen0 and seventeen gen1
/// collections inside it. Most of the capture had already been pre-allocated for exactly
/// that reason - a reusable writer per thread, exact-size slot arrays - and then the two
/// record lists were left to grow from nothing, which for 180,000 items is eighteen
/// reallocations whose last few copy megabyte arrays.
///
/// The numbers go to the test output, not into an assertion: a timing on a shared machine
/// is not something to fail a build over. What IS asserted is the shape - that the
/// capture allocates no more than the records it has to produce, within a margin that a
/// doubling series would blow through.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SnapshotAllocationTests(ITestOutputHelper outp)
{
    private static SphereNet.Game.World.GameWorld WorldWith(int itemCount, int charCount)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        for (int i = 0; i < itemCount; i++)
        {
            var it = world.CreateItem();
            it.BaseId = 0x0EED;
            world.PlaceItem(it, new Point3D((short)(100 + (i % 50)), (short)(100 + (i / 50 % 50)), 0, 0));
        }
        for (int i = 0; i < charCount; i++)
        {
            var ch = world.CreateCharacter();
            ch.BodyId = 0x190;
            world.PlaceCharacter(ch, new Point3D((short)(60 + (i % 40)), (short)(60 + (i / 40 % 40)), 0, 0));
        }
        return world;
    }


    /// <summary>The snapshot and its record lists are internal, so the capacities are
    /// read reflectively. The capacity is the only observable difference between a list
    /// that was sized and one that was grown.</summary>
    private static (int Items, int Chars, int ItemCap, int CharCap) Counts(object prepared)
    {
        object snap = prepared.GetType()
            .GetProperty("Snapshot", System.Reflection.BindingFlags.Instance |
                                     System.Reflection.BindingFlags.NonPublic)!
            .GetValue(prepared)!;
        object Read(string name) => snap.GetType().GetProperty(name)!.GetValue(snap)!;
        var items = Read("Items");
        var chars = Read("Characters");
        int Cap(object list) => (int)list.GetType().GetProperty("Capacity")!.GetValue(list)!;
        int Count(object list) => (int)list.GetType().GetProperty("Count")!.GetValue(list)!;
        return (Count(items), Count(chars), Cap(items), Cap(chars));
    }

    /// <summary>Report the capture's time and allocation at a size worth reporting.</summary>
    [Fact]
    public void ReportTheCaptureCost()
    {
        var world = WorldWith(20_000, 2_000);
        var saver = new WorldSaver(LoggerFactory.Create(_ => { }));

        // One warm capture so JIT and first-touch costs are not in the figure.
        saver.Prepare(world);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();
        var prepared = saver.Prepare(world);
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        outp.WriteLine($"22,000 objects: {sw.Elapsed.TotalMilliseconds:F0}ms, " +
                       $"{allocated / 1048576.0:F1}MB allocated, " +
                       $"gc0=+{GC.CollectionCount(0) - g0} gc1=+{GC.CollectionCount(1) - g1} " +
                       $"gc2=+{GC.CollectionCount(2) - g2}");

        var c = Counts(prepared);
        Assert.Equal(20_000, c.Items);
        Assert.Equal(2_000, c.Chars);
    }

    /// <summary>The record lists are sized before they are filled, so the capture never
    /// pays for a doubling series. Asserted through Capacity, which is the only
    /// observable difference between a sized list and a grown one.</summary>
    [Fact]
    public void TheRecordListsAreNotGrownWhileFilling()
    {
        var world = WorldWith(5_000, 500);
        var saver = new WorldSaver(LoggerFactory.Create(_ => { }));

        var prepared = saver.Prepare(world);

        // A list grown from nothing to 5,000 ends at capacity 8,192; one sized up front
        // ends at exactly what was asked for. Allowing equality-or-less is what
        // distinguishes them without pinning an implementation detail.
        var c = Counts(prepared);
        Assert.True(c.ItemCap <= 5_000 + 16,
            $"items capacity {c.ItemCap} for {c.Items} records - the list was grown " +
            "rather than sized");
        Assert.True(c.CharCap <= 500 + 16,
            $"chars capacity {c.CharCap} for {c.Chars} records");
    }
}
