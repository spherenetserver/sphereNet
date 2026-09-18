using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// TOPOBJ.&lt;key&gt; asks the outermost object, which may be this one.
///
/// Upstream hands back GetTopLevelObj() with no further test (OBR_TOPOBJ,
/// CObjBase.cpp:936), so an object that is already at the top answers about itself.
/// Here the item read refused exactly that case - a top-level item asking for its own
/// keys through TOPOBJ got an empty string - and a character, which is always its own
/// top level, had no such read at all: every &lt;TOPOBJ.x&gt; on a character was the
/// unresolved "0".
///
/// There is one implementation now rather than one per class, which is how the
/// character came to be missing it.
/// </summary>
public sealed class TopObjRefHeadTests
{
    private static SphereNet.Game.World.GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static string Read(ObjBase o, string key)
    {
        o.TryGetProperty(key, out string v);
        return v;
    }

    /// <summary>A contained item reaches the container it is in.</summary>
    [Fact]
    public void AContainedItemReachesItsContainer()
    {
        var world = World();
        var box = world.CreateItem();
        world.PlaceItem(box, new Point3D(100, 100, 0, 0));
        box.TrySetProperty("NAME", "the box");

        var it = world.CreateItem();
        Assert.True(box.TryAddItem(it));

        Assert.Equal("the box", Read(it, "TOPOBJ.NAME"));
        Assert.Equal(Read(box, "UID"), Read(it, "TOPOBJ.UID"));
    }

    /// <summary>An item already at the top answers about itself - the case that used
    /// to come back empty.</summary>
    [Fact]
    public void ATopLevelItemAnswersAboutItself()
    {
        var world = World();
        var it = world.CreateItem();
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        it.TrySetProperty("NAME", "loose thing");

        Assert.Equal("loose thing", Read(it, "TOPOBJ.NAME"));
        Assert.Equal(Read(it, "UID"), Read(it, "TOPOBJ.UID"));
    }

    /// <summary>A character is always its own top level, and had no read at all.</summary>
    [Fact]
    public void ACharacterAnswersAboutItself()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.Equal(Read(ch, "UID"), Read(ch, "TOPOBJ.UID"));
    }

    /// <summary>An equipped item reaches its wearer, not the ground.</summary>
    [Fact]
    public void AnEquippedItemReachesItsWearer()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var worn = world.CreateItem();
        Assert.True(ch.Equip(worn, SphereNet.Core.Enums.Layer.Ring));

        Assert.Equal(Read(ch, "UID"), Read(worn, "TOPOBJ.UID"));
    }
}
