using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The per-tick pathfinding budget goes to the same creatures however many workers are
/// running (review finding B5).
///
/// The budget used to be a counter the parallel workers raced to decrement, so the
/// winners were whoever reached it first. That is not a harmless tie-break: the losers
/// take a 150ms path defer, which is state the next tick reads. Sorting the decisions
/// before Apply — which the tick does — fixes the ORDER things are applied in and
/// cannot undo a different set of creatures having been chosen.
///
/// So "deterministic" described the order of application, not the world it produced.
/// These tests pin the stronger reading: same world, same tick, same winners.
/// </summary>
public sealed class PathBudgetDeterminismTests
{
    private readonly ITestOutputHelper _out;
    public PathBudgetDeterminismTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, NpcAI Ai, List<Character> Chasers) Chase(int count)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var prey = world.CreateCharacter();
        prey.IsPlayer = true;
        prey.IsOnline = true;
        prey.MaxHits = 100; prey.Hits = 100;
        world.PlaceCharacter(prey, new Point3D(200, 200, 0, 0));
        world.AddOnlinePlayer(prey);

        var ai = new NpcAI(world, new SphereConfig());
        var chasers = new List<Character>();
        for (int i = 0; i < count; i++)
        {
            var npc = world.CreateCharacter();
            npc.BaseId = 0x00D0;
            npc.NpcBrain = NpcBrainType.Monster;
            npc.MaxHits = 60; npc.Hits = 60;
            npc.Int = 100;
            npc.SetTag("NPCAI", ((int)NpcAIFlags.Path).ToString());
            // Every chaser the SAME distance from the prey: the band is 2..13, so
            // spreading them out would make the far ones ineligible and turn a
            // fairness test into a distance test.
            double angle = i * 2 * Math.PI / count;
            short ox = (short)(200 + (int)Math.Round(Math.Cos(angle) * 6));
            short oy = (short)(200 + (int)Math.Round(Math.Sin(angle) * 6));
            if (ox == 200 && oy == 200) ox = 206;
            world.PlaceCharacter(npc, new Point3D(ox, oy, 0, 0));
            npc.FightTarget = prey.Uid;
            chasers.Add(npc);
        }
        world.OnTick();
        return (world, ai, chasers);
    }

    private static uint[] Admitted(NpcAI ai, IReadOnlyList<Character> npcs, int budget, long tick)
    {
        ai.BeginTickPathfindBudget(budget, npcs, tick);
        return ai.AdmittedPathfinders.OrderBy(u => u).ToArray();
    }

    [Fact]
    public void TheSameTickPicksTheSameWinnersEveryTime()
    {
        var (_, ai, chasers) = Chase(12);

        uint[] first = Admitted(ai, chasers, budget: 2, tick: 7);
        for (int repeat = 0; repeat < 20; repeat++)
            Assert.Equal(first, Admitted(ai, chasers, budget: 2, tick: 7));

        _out.WriteLine($"tick 7 admits: {string.Join(", ", first.Select(u => u.ToString("X8")))}");

        // Twenty repetitions of the same tick. With the racing counter this was a
        // question about thread scheduling; the answer is now a function of the world
        // and the tick number, which is what a test can hold the engine to.
        Assert.Equal(2, first.Length);
    }

    [Fact]
    public void TheParallelPhaseOnlyREADSTheChoice()
    {
        var (_, ai, chasers) = Chase(16);

        // The selection runs once, serially, before the fan-out. The review's
        // acceptance condition - same inputs, 1/2/4/8 workers, same selected uids -
        // holds by construction once the parallel phase cannot change the set; this
        // asserts that property rather than re-running the selector from many threads,
        // which is something the engine never does.
        ai.BeginTickPathfindBudget(3, chasers, tickNumber: 42);
        uint[] before = ai.AdmittedPathfinders.OrderBy(u => u).ToArray();

        foreach (int workers in new[] { 1, 2, 4, 8 })
        {
            System.Threading.Tasks.Parallel.ForEach(chasers,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers },
                npc => ai.BuildDecision(npc, Environment.TickCount64));

            uint[] after = ai.AdmittedPathfinders.OrderBy(u => u).ToArray();
            Assert.Equal(before, after);
        }

        _out.WriteLine($"unchanged across 1/2/4/8 workers: " +
                       string.Join(", ", before.Select(u => u.ToString("X8"))));
        Assert.Equal(3, before.Length);
    }

    [Fact]
    public void TheBudgetIsRespected()
    {
        var (_, ai, chasers) = Chase(20);

        Assert.Equal(2, Admitted(ai, chasers, budget: 2, tick: 1).Length);
        Assert.Equal(5, Admitted(ai, chasers, budget: 5, tick: 1).Length);

        // A budget of zero admits nobody rather than everybody - the check used to be
        // "decrement went negative", which is a different thing to get wrong.
        Assert.Empty(Admitted(ai, chasers, budget: 0, tick: 1));
    }

    [Fact]
    public void NobodyIsDeferredForever()
    {
        var (_, ai, chasers) = Chase(10);

        var seen = new HashSet<uint>();
        for (long tick = 0; tick < 40; tick++)
            foreach (uint uid in Admitted(ai, chasers, budget: 2, tick))
                seen.Add(uid);

        _out.WriteLine($"{seen.Count} of {chasers.Count} creatures got a turn within 40 ticks");

        // Determinism is easy on its own: always pick the first two. The racing counter
        // avoided starving the tail only by being unpredictable, so replacing it with a
        // fixed order would have traded one defect for another. The start rotates with
        // the tick, so every chaser gets its turn.
        Assert.Equal(chasers.Count, seen.Count);
    }

    [Fact]
    public void ACreatureWithNothingToChaseIsNotAdmitted()
    {
        var (world, ai, chasers) = Chase(6);
        foreach (var npc in chasers)
            npc.FightTarget = Serial.Invalid;

        // Admission is for creatures that would actually run a search. Handing tickets
        // to idle ones would spend the budget on nothing and defer the chasers.
        Assert.Empty(Admitted(ai, chasers, budget: 4, tick: 3));
    }
}
