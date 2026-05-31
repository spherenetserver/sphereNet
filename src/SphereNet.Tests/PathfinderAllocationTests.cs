using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Deterministic allocation micro-benchmark for the A* pathfinder. The combat
/// stress scenario is too stochastic to attribute GC pressure precisely, so we
/// measure allocation-per-call directly with a fixed start/goal on open terrain
/// using GC.GetAllocatedBytesForCurrentThread() (single-threaded, GC-independent,
/// monotonic). Acts as a regression guard: if the per-call scratch collections
/// (PriorityQueue + HashSet + two Dictionaries) stop being pooled, the bytes
/// allocated per FindPath jump well past the threshold and this test fails.
/// </summary>
public class PathfinderAllocationTests
{
    private readonly ITestOutputHelper _out;
    public PathfinderAllocationTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld world, Pathfinder pf) CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        return (world, new Pathfinder(world));
    }

    [Fact]
    public void FindPath_PooledScratch_AllocatesLittlePerCall()
    {
        var (_, pf) = CreateWorld();
        // A long open-terrain diagonal so A* explores enough nodes that the
        // scratch collections would (without pooling) grow and churn.
        var start = new Point3D(200, 200, 0, 0);
        var goal = new Point3D(320, 320, 0, 0);

        // Warm up: first call rents the thread-static scratch collections and
        // grows JIT/type caches. Those are one-time, not per-call, costs.
        var warm = pf.FindPath(start, goal, 0);
        Assert.NotNull(warm);
        Assert.True(warm!.Count > 0);

        const int iterations = 300;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            var path = pf.FindPath(start, goal, 0);
            Assert.NotNull(path);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        double perCall = (after - before) / (double)iterations;
        _out.WriteLine($"FindPath allocation: {perCall:F0} bytes/call over {iterations} iterations " +
                       $"(path length {warm.Count})");

        // The returned path List is allocated fresh every call (it is the
        // caller's result, ~1.7 KB for this path); everything else on the hot
        // path is allocation-free — the A* scratch collections are pooled per
        // thread, and the per-neighbour walkability check goes through the
        // non-allocating GameWorld.IsPathTileBlockedByObject rather than the
        // yield-based range queries. (Before that work this path allocated
        // ~486 KB/call.) 8 KB leaves headroom for the result list while still
        // failing loudly if either allocation source is reintroduced.
        Assert.True(perCall < 8_192,
            $"FindPath allocated {perCall:F0} bytes/call — expected < 8192. " +
            "A* scratch pooling or the allocation-free walkability check has regressed.");
    }
}
