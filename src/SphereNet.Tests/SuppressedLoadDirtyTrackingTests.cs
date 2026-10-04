using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// An object built while dirty notification is suppressed (world load, stress-test
/// generation) must still report its next change.
///
/// An object only reports its clean→dirty transition. The setters raise the flags
/// during the suppressed load too, and nothing cleared them, so every object loaded
/// from the save stayed "already dirty" for good: a later hide, body, hue or ground
/// move on it never reached the dirty set, and nearby clients saw it only after
/// walking.
/// </summary>
public sealed class SuppressedLoadDirtyTrackingTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ACharacterLoadedUnderSuppressionReportsItsNextChange()
    {
        var world = CreateWorld();
        world.SuppressDirtyNotify = true;
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        npc.Hue = new Color(0x21);
        world.SuppressDirtyNotify = false;
        world.ConsumeDirtyObjects();
        Assert.False(world.HasDirty);

        npc.Hue = new Color(0x22);

        Assert.True(world.HasDirty);
    }

    [Fact]
    public void AnItemLoadedUnderSuppressionReportsItsNextChange()
    {
        var world = CreateWorld();
        world.SuppressDirtyNotify = true;
        var item = world.CreateItem();
        item.Hue = new Color(0x21);
        world.SuppressDirtyNotify = false;
        world.ConsumeDirtyObjects();

        item.Hue = new Color(0x22);

        Assert.True(world.HasDirty);
    }
}
