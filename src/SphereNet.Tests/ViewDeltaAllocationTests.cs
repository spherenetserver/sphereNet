using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Shared scene for the view-delta allocation and equivalence tests: a crowd of
/// NPCs and ground items around a point, watched by hidden player viewers (hidden
/// so two viewers standing together do not appear in each other's delta and the
/// streams they are sent can be compared byte for byte).
/// </summary>
internal sealed class ViewDeltaScene
{
    public const short CenterX = 1000, CenterY = 1000;

    public readonly ILoggerFactory Lf = TestHarness.CreateLoggerFactory();
    public readonly GameWorld World = TestHarness.CreateWorld();
    public readonly AccountManager Accounts;
    public readonly List<Character> Npcs = [];
    public readonly List<Item> Items = [];

    public ViewDeltaScene(int npcs, int items, int stackedOnOneTile = 0)
    {
        Accounts = new AccountManager(Lf);
        for (int i = 0; i < npcs; i++)
            AddNpc((short)(CenterX + (i % 19) - 9), (short)(CenterY + (i / 19) % 19 - 9));
        for (int i = 0; i < items; i++)
            AddItem((short)(CenterX + (i % 17) - 8), (short)(CenterY + (i / 17) % 17 - 8));
        // More than the 80-per-tile cap on one tile, so the tile-count path runs.
        for (int i = 0; i < stackedOnOneTile; i++)
            AddItem(CenterX + 3, CenterY + 3);
    }

    public Character AddNpc(short x, short y)
    {
        var npc = World.CreateCharacter();
        npc.Name = "npc";
        npc.IsPlayer = false;
        npc.BodyId = 0x0190;
        World.PlaceCharacter(npc, new Point3D(x, y, 0, 0));
        Npcs.Add(npc);
        return npc;
    }

    public Item AddItem(short x, short y)
    {
        var item = World.CreateItem();
        item.BaseId = 0x0EED;
        World.PlaceItem(item, new Point3D(x, y, 0, 0));
        Items.Add(item);
        return item;
    }

    public GameClient AddViewer(int id, short x = CenterX, short y = CenterY)
    {
        var state = TestHarness.CreateActiveNetState(Lf, id);
        var client = new GameClient(state, World, Accounts, Lf.CreateLogger<GameClient>());
        var viewer = World.CreateCharacter();
        viewer.Name = "viewer" + id;
        viewer.IsPlayer = true;
        viewer.PrivLevel = PrivLevel.Player;
        viewer.SetStatFlag(StatFlag.Hidden);
        World.PlaceCharacter(viewer, new Point3D(x, y, 0, 0));
        TestHarness.AttachCharacter(client, viewer);
        return client;
    }

    /// <summary>Everything a delta carries, in the order apply consumes it. The two
    /// sets are only ever probed with Contains, so they are compared as sets.</summary>
    public static string Snapshot(ClientViewDelta d)
    {
        var sb = new System.Text.StringBuilder();
        var cc = new List<uint>(d.CurrentChars); cc.Sort();
        var ci = new List<uint>(d.CurrentItems); ci.Sort();
        sb.Append("CC:").AppendJoin(',', cc).Append('\n');
        sb.Append("CI:").AppendJoin(',', ci).Append('\n');
        sb.Append("NC:");
        foreach (var (ch, h) in d.NewChars) sb.Append(ch.Uid.Value).Append(h ? "h" : "").Append(',');
        sb.Append("\nUC:");
        foreach (var ch in d.UpdatedChars) sb.Append(ch.Uid.Value).Append(',');
        sb.Append("\nNI:");
        foreach (var (it, h) in d.NewItems) sb.Append(it.Uid.Value).Append(h ? "h" : "").Append(',');
        sb.Append("\nUI:");
        foreach (var it in d.UpdatedItems) sb.Append(it.Uid.Value).Append(',');
        return sb.ToString();
    }

    public static List<byte[]> Packets(NetState state)
    {
        var list = new List<byte[]>();
        foreach (var p in TestHarness.GetQueuedPackets(state))
            list.Add(p.Span.ToArray());
        return list;
    }
}

/// <summary>
/// Allocation regression guard for the view-delta build (multicore audit M09). A
/// client whose view is refreshed but whose surroundings are already known used to
/// allocate a fresh delta - two HashSets, four Lists and a tile-count Dictionary,
/// grown entry by entry - on every build: ~86 KB per refreshed client in a crowd of
/// 599 visible objects, whatever the worker count. The delta collections are now
/// per-client scratch whose capacity survives the apply, so a steady build
/// allocates next to nothing.
/// Measured with GC.GetAllocatedBytesForCurrentThread around the build call only
/// (the build is what the parallel phase runs; apply is serial and sends packets).
/// </summary>
public class ViewDeltaAllocationTests
{
    private readonly ITestOutputHelper _out;
    public ViewDeltaAllocationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void BuildViewDelta_SteadyKnownScene_AllocatesLittlePerCall()
    {
        var scene = new ViewDeltaScene(npcs: 99, items: 500);
        var client = scene.AddViewer(41_001);

        // First cycle sends everything; the next ones see a known, unchanged scene.
        for (int i = 0; i < 3; i++)
            client.ApplyViewDelta(client.BuildViewDelta()!);
        var probe = client.BuildViewDelta()!;
        Assert.Equal(99, probe.CurrentChars.Count);
        Assert.Equal(500, probe.CurrentItems.Count);
        Assert.Empty(probe.NewChars);
        Assert.Empty(probe.NewItems);
        client.ApplyViewDelta(probe);

        const int iterations = 200;
        long buildBytes = 0;
        long cycleStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var delta = client.BuildViewDelta()!;
            buildBytes += GC.GetAllocatedBytesForCurrentThread() - b0;
            client.ApplyViewDelta(delta);
        }
        long cycleBytes = GC.GetAllocatedBytesForCurrentThread() - cycleStart;

        double perBuild = buildBytes / (double)iterations;
        double perCycle = cycleBytes / (double)iterations;
        _out.WriteLine($"BuildViewDelta: {perBuild:F0} bytes/call; build+apply: {perCycle:F0} bytes/cycle " +
                       $"(599 known visible objects, {iterations} iterations)");

        // Before the scratch reuse this was ~86 KB per build. 4 KB leaves room for
        // incidental allocations further down the visibility checks while failing
        // loudly if the delta collections are allocated per call again.
        Assert.True(perBuild < 4_096,
            $"BuildViewDelta allocated {perBuild:F0} bytes/call - expected < 4096. " +
            "The per-client view-delta scratch is no longer reused.");
    }
}

/// <summary>
/// The reused view-delta scratch must not change what a refresh produces: the same
/// delta contents, the same packets in the same order and the same known sets as a
/// freshly allocated delta, across repeated refreshes, a parallel build and objects
/// entering and leaving view - and a delta that is built but not yet applied must
/// never be overwritten by the next build.
/// </summary>
public class ViewDeltaScratchEquivalenceTests
{
    /// <summary>Keeps <paramref name="reference"/>'s scratch rented for good, so every
    /// build it does from now on takes the freshly-allocated path - the behaviour
    /// before the scratch existed.</summary>
    private static void PinScratch(GameClient reference)
    {
        var pinned = reference.BuildViewDelta();
        Assert.NotNull(pinned);
        var fresh = reference.BuildViewDelta();
        Assert.NotSame(pinned, fresh);
    }

    private static void Refresh(GameClient client) => client.ApplyViewDelta(client.BuildViewDelta()!);

    private static void AssertSameView(GameClient scratch, GameClient reference, string step)
    {
        var a = scratch.BuildViewDelta()!;
        var b = reference.BuildViewDelta()!;
        Assert.True(ViewDeltaScene.Snapshot(a) == ViewDeltaScene.Snapshot(b), $"{step}: delta contents differ");
        scratch.ApplyViewDelta(a);
        reference.ApplyViewDelta(b);

        Assert.Equal(reference.View.KnownChars.OrderBy(u => u), scratch.View.KnownChars.OrderBy(u => u));
        Assert.Equal(reference.View.KnownItems.OrderBy(u => u), scratch.View.KnownItems.OrderBy(u => u));
        var pa = ViewDeltaScene.Packets(scratch.NetState);
        var pb = ViewDeltaScene.Packets(reference.NetState);
        Assert.True(pa.Count == pb.Count, $"{step}: {pa.Count} packets vs {pb.Count}");
        for (int i = 0; i < pa.Count; i++)
            Assert.True(pa[i].AsSpan().SequenceEqual(pb[i]), $"{step}: packet {i} (0x{pa[i][0]:X2}) differs");
    }

    [Fact]
    public void ScratchDelta_MatchesFreshDelta_AcrossEnterLeaveAndUpdates()
    {
        var scene = new ViewDeltaScene(npcs: 60, items: 300, stackedOnOneTile: 85);
        var scratch = scene.AddViewer(42_001);
        var reference = scene.AddViewer(42_002);
        PinScratch(reference);

        AssertSameView(scratch, reference, "initial");
        Assert.Equal(60, scratch.View.KnownChars.Count); // the hidden viewers do not see each other
        // The stacked tile (85 + one grid item) is capped at 80.
        Assert.Equal(299 + 80, scratch.View.KnownItems.Count);
        for (int i = 0; i < 3; i++)
            AssertSameView(scratch, reference, $"steady {i}");

        // Leave, enter, move within view, change state, delete.
        var leaver = scene.Npcs[0];
        scene.World.MoveCharacter(leaver, new Point3D(1200, 1200, 0, 0));
        var mover = scene.Npcs[1];
        scene.World.MoveCharacter(mover, new Point3D((short)(mover.X + 1), mover.Y, 0, 0));
        var entrant = scene.AddNpc(ViewDeltaScene.CenterX + 5, ViewDeltaScene.CenterY - 5);
        var newItem = scene.AddItem(ViewDeltaScene.CenterX - 4, ViewDeltaScene.CenterY + 6);
        scene.Items[1].Hue = new Color(0x0021);
        var gone = scene.Items[2];
        scene.World.RemoveItem(gone);
        AssertSameView(scratch, reference, "changes");

        Assert.DoesNotContain(leaver.Uid.Value, scratch.View.KnownChars);
        Assert.Contains(entrant.Uid.Value, scratch.View.KnownChars);
        Assert.Contains(newItem.Uid.Value, scratch.View.KnownItems);
        Assert.DoesNotContain(gone.Uid.Value, scratch.View.KnownItems);
        Assert.Contains(ViewDeltaScene.Packets(scratch.NetState),
            p => p[0] == 0x1D && ((uint)(p[1] << 24 | p[2] << 16 | p[3] << 8 | p[4])) == leaver.Uid.Value);

        // Come back into view: new again, for both.
        scene.World.MoveCharacter(leaver, new Point3D(ViewDeltaScene.CenterX - 2, ViewDeltaScene.CenterY + 2, 0, 0));
        AssertSameView(scratch, reference, "return");
        Assert.Contains(leaver.Uid.Value, scratch.View.KnownChars);
        for (int i = 0; i < 2; i++)
            AssertSameView(scratch, reference, $"steady after {i}");
    }

    [Fact]
    public void BuildViewDelta_RepeatedSteadyBuilds_ReuseOneInstanceWithIdenticalContents()
    {
        var scene = new ViewDeltaScene(npcs: 40, items: 200);
        var client = scene.AddViewer(43_001);
        Refresh(client);

        var first = client.BuildViewDelta()!;
        string expected = ViewDeltaScene.Snapshot(first);
        client.ApplyViewDelta(first);
        for (int i = 0; i < 5; i++)
        {
            var again = client.BuildViewDelta()!;
            Assert.Same(first, again); // the scratch, capacity and all
            Assert.Equal(expected, ViewDeltaScene.Snapshot(again));
            client.ApplyViewDelta(again);
        }
    }

    [Fact]
    public void BuildViewDelta_WhilePreviousDeltaPending_DoesNotOverwriteIt()
    {
        var scene = new ViewDeltaScene(npcs: 10, items: 20);
        var client = scene.AddViewer(44_001);
        Refresh(client);

        var pending = client.BuildViewDelta()!;
        string before = ViewDeltaScene.Snapshot(pending);

        // The world changes and the same client is built again before the first
        // delta was applied: that build must not reuse the pending one.
        scene.World.MoveCharacter(scene.Npcs[0], new Point3D(1300, 1300, 0, 0));
        scene.AddItem(ViewDeltaScene.CenterX + 1, ViewDeltaScene.CenterY + 1);
        var second = client.BuildViewDelta()!;
        Assert.NotSame(pending, second);
        Assert.Equal(before, ViewDeltaScene.Snapshot(pending));
        Assert.NotEqual(before, ViewDeltaScene.Snapshot(second));

        // Applying the pending delta frees the scratch for the next build.
        client.ApplyViewDelta(pending);
        Assert.Same(pending, client.BuildViewDelta());
    }

    [Fact]
    public void BuildViewDelta_AbandonedDelta_ReleaseFreesScratch()
    {
        var scene = new ViewDeltaScene(npcs: 5, items: 5);
        var client = scene.AddViewer(45_001);
        var abandoned = client.BuildViewDelta()!;
        Assert.NotSame(abandoned, client.BuildViewDelta());
        abandoned.Release();
        Assert.Same(abandoned, client.BuildViewDelta());
    }

    [Fact]
    public void ParallelBuild_MatchesSerialBuild_ForEveryClient()
    {
        var scene = new ViewDeltaScene(npcs: 80, items: 400, stackedOnOneTile: 90);
        var clients = new List<GameClient>();
        for (int i = 0; i < 24; i++)
            clients.Add(scene.AddViewer(46_000 + i,
                (short)(ViewDeltaScene.CenterX + (i % 6) * 4 - 10), (short)(ViewDeltaScene.CenterY + (i / 6) * 4 - 6)));

        for (int round = 0; round < 4; round++)
        {
            if (round == 2)
            {
                scene.World.MoveCharacter(scene.Npcs[3], new Point3D(1500, 1500, 0, 0));
                scene.AddNpc(ViewDeltaScene.CenterX, ViewDeltaScene.CenterY + 7);
                scene.World.RemoveItem(scene.Items[10]);
            }

            var parallel = new ConcurrentDictionary<int, (ClientViewDelta Delta, string Snap)>();
            Parallel.ForEach(clients, new ParallelOptions { MaxDegreeOfParallelism = 8 }, c =>
            {
                var d = c.BuildViewDelta()!;
                parallel[c.NetState.Id] = (d, ViewDeltaScene.Snapshot(d));
            });

            foreach (var c in clients)
            {
                var (pd, psnap) = parallel[c.NetState.Id];
                pd.Release(); // abandoned, as an aborted multicore tick leaves it
                var serial = c.BuildViewDelta()!;
                Assert.Same(pd, serial); // every client built into its own scratch
                Assert.Equal(psnap, ViewDeltaScene.Snapshot(serial));
                c.ApplyViewDelta(serial);
            }
        }

        // Each client's visible set is exactly what a fresh build computes.
        foreach (var c in clients)
        {
            var d = c.BuildViewDelta()!;
            Assert.Equal(d.CurrentChars.OrderBy(u => u), c.View.KnownChars.OrderBy(u => u));
            Assert.Equal(d.CurrentItems.OrderBy(u => u), c.View.KnownItems.OrderBy(u => u));
            c.ApplyViewDelta(d);
        }
    }
}
