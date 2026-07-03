using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

// Wave M1 (wiki/housing-movement-scriptpack.txt): negative-coordinate map
// edge-cases. Truncating integer division let X/Y in [-63,-1] resolve to
// sector 0, so off-map placements slipped past the bounds guard and crashed
// the map readers with a negative cell index.
public class MovementEdgeCaseTests
{
    [Fact]
    public void GetSector_RejectsNegativeCoordinates()
    {
        var world = TestHarness.CreateWorld();
        Assert.Null(world.GetSector(new Point3D(-1, 100, 0, 0)));
        Assert.Null(world.GetSector(new Point3D(100, -63, 0, 0)));
        Assert.NotNull(world.GetSector(new Point3D(0, 0, 0, 0)));
    }

    [Fact]
    public void PlaceAndMoveCharacter_RefuseOffMapPositions()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();

        Assert.False(world.PlaceCharacter(ch, new Point3D(-5, 100, 0, 0)));

        Assert.True(world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0)));
        world.MoveCharacter(ch, new Point3D(-1, 100, 0, 0));
        // The move must not leave the char at a negative (crash-prone) spot.
        Assert.True(ch.X >= 0 && ch.Y >= 0);
    }

    [Fact]
    public void Pathfinder_OnTheMapEdge_DoesNotCrash()
    {
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(0, 0, 0, 0)); // far NW corner

        var pf = new SphereNet.Game.Movement.Pathfinder(world);
        // Exploring neighbors from (0,0) previously passed -1 into the map
        // readers. A null/short result is fine — no exception is the contract.
        var path = pf.FindPath(npc.Position, new Point3D(5, 5, 0, 0), 0,
            SphereNet.Core.Enums.CanFlags.None, npc, 100);
        Assert.True(path == null || path.Count >= 0);
    }
}
