using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the newly-wired @MurderDecay trigger. A murderer's kill count ages off
// one at a time in Character.TickNotorietyDecay; the trigger fires on each
// decrement with the new count. The decay clock is injected (nowMs parameter), so
// the test is fully deterministic. The Character.OnMurderDecay hook is nulled
// between tests by ResetEngineStatics.
public class NotorietyDecayTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void NotorietyDecay_FiresMurderDecay_WithDecrementedCount()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Kills = 3;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var decays = new List<int>();
        Character.OnMurderDecay = (self, newKills) => { if (self == ch) decays.Add(newKills); };

        long decayMs = Character.MurderDecayTimeSeconds * 1000L;
        const long t0 = 1_000_000;

        ch.TickNotorietyDecay(t0);              // arms the decay timer, no decrement yet
        Assert.Empty(decays);
        Assert.Equal(3, ch.Kills);

        ch.TickNotorietyDecay(t0 + decayMs + 1); // one kill ages off
        Assert.Equal([2], decays);
        Assert.Equal(2, ch.Kills);

        ch.TickNotorietyDecay(t0 + 2 * decayMs + 2); // next one ages off
        Assert.Equal([2, 1], decays);
        Assert.Equal(1, ch.Kills);
    }

    [Fact]
    public void NotorietyDecay_NoKills_DoesNotFire()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Kills = 0;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        int fires = 0;
        Character.OnMurderDecay = (_, _) => fires++;

        long decayMs = Character.MurderDecayTimeSeconds * 1000L;
        ch.TickNotorietyDecay(1_000_000);
        ch.TickNotorietyDecay(1_000_000 + decayMs + 1);

        Assert.Equal(0, fires);
    }
}
