using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// What P= means with two, three and four values.
///
/// Upstream parses the list and DEFAULTS the parts it was not given to zero -
/// CPointBase::Read starts from z 0 and map 0 and fills in what the string carries
/// (CPointBase.cpp:972-1002). So "P=x,y" is not "keep the z I had", it is z 0, which
/// matters because the worldgen scripts write exactly that form a quarter of a million
/// times over: an object created at z 0 and dropped at "P=678,998" is meant to settle
/// from there, not to inherit a height from whatever made it.
///
/// The packs write 22,006 four-value assignments naming maps 1 to 5, so the map
/// component earns a test of its own; a shard without that map refuses the placement
/// and logs it, rather than silently moving the object somewhere else.
/// </summary>
public sealed class PointAssignmentTests
{
    private static Item PlacedAt(SphereNet.Game.World.GameWorld world, Point3D at)
    {
        var it = world.CreateItem();
        world.PlaceItem(it, at);
        return it;
    }

    private static SphereNet.Game.World.GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>The worldgen form. The z it had is NOT kept - upstream defaults it.</summary>
    [Fact]
    public void TwoValuesMeanZeroZeroForTheRest()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        Assert.True(it.TrySetProperty("P", "678,998"));
        Assert.Equal(new Point3D(678, 998, 0, 0), it.Position);
    }

    [Fact]
    public void ThreeValuesCarryTheHeight()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        Assert.True(it.TrySetProperty("P", "678,998,5"));
        Assert.Equal(new Point3D(678, 998, 5, 0), it.Position);
    }

    [Fact]
    public void FourValuesCarryTheMap()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        Assert.True(it.TrySetProperty("P", "678,998,5,0"));
        Assert.Equal(new Point3D(678, 998, 5, 0), it.Position);
    }

    /// <summary>Spaces around the separators are part of the format upstream accepts
    /// (" ,\t"), and the packs write them.</summary>
    [Fact]
    public void SeparatorsMayCarrySpaces()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        Assert.True(it.TrySetProperty("P", " 678 , 998 , 5 "));
        Assert.Equal(new Point3D(678, 998, 5, 0), it.Position);
    }

    /// <summary>A map this shard did not load leaves the object where it was - the
    /// placement is refused and logged rather than quietly landing it somewhere else.
    /// Upstream instead rewrites the map to 0 and logs; refusing keeps a map-2 line
    /// from scattering its objects across map 0, and the operator sees either way.</summary>
    [Fact]
    public void AMapThisShardDoesNotHaveLeavesTheObjectAlone()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        it.TrySetProperty("P", "678,998,5,9");
        Assert.Equal(new Point3D(10, 20, 7, 0), it.Position);
    }

    /// <summary>A malformed list changes nothing.</summary>
    [Fact]
    public void AMalformedListIsIgnored()
    {
        var world = World();
        var it = PlacedAt(world, new Point3D(10, 20, 7, 0));

        it.TrySetProperty("P", "678");
        Assert.Equal(new Point3D(10, 20, 7, 0), it.Position);

        it.TrySetProperty("P", "north,south");
        Assert.Equal(new Point3D(10, 20, 7, 0), it.Position);
    }
}
