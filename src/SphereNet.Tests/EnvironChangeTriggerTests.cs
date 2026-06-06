using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @EnvironChange trigger's per-character light tracking
// (Character.UpdateEnvironLight). Program drives it on region transitions with the
// surface/dungeon light level (28 underground, else global light); the comparison
// + fire live here so they are unit-testable. Character.OnEnvironChange is nulled
// between tests by ResetEngineStatics.
public class EnvironChangeTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void UpdateEnvironLight_OnlyFiresOnAnActualChange_AfterBaseline()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        var fires = new List<int>();
        Character.OnEnvironChange = (c, light) => { if (c == ch) fires.Add(light); };

        ch.UpdateEnvironLight(0);   // first call → baseline only, no fire
        Assert.Empty(fires);

        ch.UpdateEnvironLight(28);  // surface → dungeon
        Assert.Equal([28], fires);

        ch.UpdateEnvironLight(28);  // unchanged → no fire
        Assert.Equal([28], fires);

        ch.UpdateEnvironLight(0);   // dungeon → surface
        Assert.Equal([28, 0], fires);
    }
}
