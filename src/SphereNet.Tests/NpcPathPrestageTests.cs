using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// N2 (perf): the A* route for a blocked combat/pet chase is precomputed in the
/// parallel BuildDecision phase and carried on the NpcDecision; the serial apply
/// seeds it into the path cache (when its own state still calls for a recompute)
/// so OnTickAction finds a warm cache instead of running A* on the serial path.
/// </summary>
public sealed class NpcPathPrestageTests
{
    private static (GameWorld World, NpcAI Ai) MakeWorld()
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig())
        {
            Flags = NpcAIFlags.Path | NpcAIFlags.AlwaysInt | NpcAIFlags.PersistentPath
        };
        return (world, ai);
    }

    [Fact]
    public void BuildDecision_BlockedChase_CarriesPrestagedPath()
    {
        var (world, ai) = MakeWorld();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(90, 90, 0, 0));

        var npc = world.CreateCharacter();
        npc.Hits = npc.MaxHits = 50;
        npc.NpcMaster = owner.Uid; // bypasses the active-area park in BuildDecision
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var enemy = world.CreateCharacter();
        enemy.Hits = enemy.MaxHits = 50;
        world.PlaceCharacter(enemy, new Point3D(104, 100, 0, 0)); // dist 4 ∈ [2, 14)
        npc.FightTarget = enemy.Uid;

        // A living character on the direct-step tile blocks the straight line,
        // which is the serial trigger for a full A* — the prestage must run it
        // in the build phase instead.
        var blocker = world.CreateCharacter();
        blocker.Hits = blocker.MaxHits = 50;
        world.PlaceCharacter(blocker, new Point3D(101, 100, 0, 0));

        npc.NextNpcActionTime = 0;
        var decision = ai.BuildDecision(npc, Environment.TickCount64);

        Assert.NotNull(decision);
        Assert.True(decision!.Value.PrestageRan);
        Assert.NotNull(decision.Value.PrestagedPath);
        Assert.NotEmpty(decision.Value.PrestagedPath!);
        Assert.Equal(enemy.Position, decision.Value.PrestageGoal);
        // The route detours around the blocker instead of walking through it.
        Assert.DoesNotContain(decision.Value.PrestagedPath!,
            p => p.X == blocker.X && p.Y == blocker.Y);
    }

    [Fact]
    public void BuildDecision_OpenLine_DoesNotPrestage()
    {
        var (world, ai) = MakeWorld();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(90, 90, 0, 0));

        var npc = world.CreateCharacter();
        npc.Hits = npc.MaxHits = 50;
        npc.NpcMaster = owner.Uid;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var enemy = world.CreateCharacter();
        enemy.Hits = enemy.MaxHits = 50;
        world.PlaceCharacter(enemy, new Point3D(104, 100, 0, 0));
        npc.FightTarget = enemy.Uid;

        npc.NextNpcActionTime = 0;
        var decision = ai.BuildDecision(npc, Environment.TickCount64);

        Assert.NotNull(decision);
        Assert.False(decision!.Value.PrestageRan); // direct step open → serial takes it
    }

    [Fact]
    public void SeedPrestagedPath_WarmsCache_AndFailureRecordsBackoff()
    {
        var (world, ai) = MakeWorld();

        var npc = world.CreateCharacter();
        npc.Hits = npc.MaxHits = 50;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        uint uid = npc.Uid.Value;

        var seed = typeof(NpcAI).GetMethod("SeedPrestagedPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pathCache = (Dictionary<uint, List<Point3D>>)typeof(NpcAI)
            .GetField("_pathCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ai)!;
        var nextPathfind = (Dictionary<uint, long>)typeof(NpcAI)
            .GetField("_nextPathfindMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ai)!;

        var goal = new Point3D(104, 100, 0, 0);
        var route = new List<Point3D> { new(101, 101, 0, 0), new(102, 101, 0, 0) };

        // Success: the prestaged route lands in the cache with a throttle stamp.
        seed.Invoke(ai, [npc, new NpcAI.NpcDecision(uid, NpcAI.NpcDecisionType.Legacy,
            npc.Position, npc.Direction, 0, route, goal, true)]);
        Assert.Same(route, pathCache[uid]);
        Assert.True(nextPathfind.TryGetValue(uid, out long throttle) &&
            throttle > Environment.TickCount64);

        // Failure on another NPC: records the fail backoff without a cache entry.
        var npc2 = world.CreateCharacter();
        npc2.Hits = npc2.MaxHits = 50;
        world.PlaceCharacter(npc2, new Point3D(120, 100, 0, 0));
        uint uid2 = npc2.Uid.Value;
        seed.Invoke(ai, [npc2, new NpcAI.NpcDecision(uid2, NpcAI.NpcDecisionType.Legacy,
            npc2.Position, npc2.Direction, 0, null, goal, true)]);
        Assert.False(pathCache.ContainsKey(uid2));
        Assert.True(nextPathfind.TryGetValue(uid2, out long backoff) &&
            backoff >= Environment.TickCount64 + 4_000); // ~PathFailBackoffMs
    }
}
