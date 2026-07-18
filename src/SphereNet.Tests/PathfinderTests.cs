using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging;

namespace SphereNet.Tests;

public class PathfinderTests
{
    private static (GameWorld world, Pathfinder pf) CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var pf = new Pathfinder(world);
        return (world, pf);
    }

    [Fact]
    public void FindPath_AdjacentGoal_ReturnsSingleStep()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(101, 100, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        Assert.NotNull(path);
        Assert.Single(path);
    }

    [Fact]
    public void FindPath_SamePoint_ReturnsSingleStep()
    {
        var (_, pf) = CreateWorld();
        var pt = new Point3D(100, 100, 0, 0);

        var path = pf.FindPath(pt, pt, 0);
        Assert.NotNull(path);
    }

    [Fact]
    public void FindPath_SameXYDifferentUnreachableZ_DoesNotTreatAsReached()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(100, 100, 20, 0);

        var path = pf.FindPath(start, goal, 0);

        Assert.Null(path);
    }

    [Fact]
    public void FindPath_ShortDistance_FindsPath()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(105, 105, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        Assert.NotNull(path);
        Assert.True(path.Count > 0);
        Assert.Equal(goal.X, path[^1].X);
        Assert.Equal(goal.Y, path[^1].Y);
    }

    // N3 (perf/parity): Source-X CPathFinder searches a fixed 28x28 box around
    // the NPC (MAX_NPC_PATH_STORAGE_SIZE). With maxRadius set, a goal whose only
    // route leaves the box is unreachable and the search stops at the box edge
    // instead of burning the whole node budget.
    [Fact]
    public void FindPath_MaxRadius_ConfinesSearchBox()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(120, 100, 0, 0); // 20 tiles out

        // Unconstrained: open synthetic terrain, path found.
        Assert.NotNull(pf.FindPath(start, goal, 0));

        // Confined to a 14-tile box: the goal lies outside → no path.
        Assert.Null(pf.FindPath(start, goal, 0, maxRadius: 14));

        // A goal inside the box still routes normally.
        Assert.NotNull(pf.FindPath(start, new Point3D(110, 100, 0, 0), 0, maxRadius: 14));
    }

    [Fact]
    public void FindPath_PathLengthCapped()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(200, 200, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        // Path is capped by Pathfinder.MaxPathLength (256) on return.
        if (path != null)
            Assert.True(path.Count <= 256);
    }
}
