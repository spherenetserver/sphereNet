using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @NPCSeeNewPlayer first-sight memory (Character.SeeNewPlayer). An NPC
// fires the trigger the first time it perceives a player and not again until the
// per-player TTL lapses; NpcAI drives it from a gated, throttled range scan. The
// clock is injected so the test is deterministic. Character.OnNpcSeeNewPlayer is
// nulled between tests by ResetEngineStatics.
public class NpcSeeNewPlayerTriggerTests
{
    private static (Character npc, Character player) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        return (npc, player);
    }

    [Fact]
    public void SeeNewPlayer_FiresOnFirstSight_ThenNotUntilTtlLapses()
    {
        var (npc, player) = Setup();
        var sightings = new List<uint>();
        Character.OnNpcSeeNewPlayer = (n, p) => { if (n == npc) sightings.Add(p.Uid.Value); };

        const long ttl = 60_000;

        Assert.True(npc.SeeNewPlayer(player, 1_000_000, ttl));       // first sight → fire
        Assert.Equal([player.Uid.Value], sightings);

        // Continued sight refreshes the timer (so a player standing there is not
        // re-greeted), hence no fire.
        Assert.False(npc.SeeNewPlayer(player, 1_010_000, ttl));
        Assert.Single(sightings);

        // The player leaves (no sightings) and returns > TTL after the last one.
        Assert.True(npc.SeeNewPlayer(player, 1_010_000 + ttl + 1, ttl));
        Assert.Equal(2, sightings.Count);
    }

    [Fact]
    public void SeeNewPlayer_DistinctPlayersEachFireOnce()
    {
        var (npc, player) = Setup();
        var other = SphereNet.Game.Objects.ObjBase.ResolveWorld!().CreateCharacter();
        other.IsPlayer = true;

        int fires = 0;
        Character.OnNpcSeeNewPlayer = (_, _) => fires++;

        npc.SeeNewPlayer(player, 1_000_000);
        npc.SeeNewPlayer(other, 1_000_000);
        npc.SeeNewPlayer(player, 1_000_500); // already seen → no fire

        Assert.Equal(2, fires);
    }
}
