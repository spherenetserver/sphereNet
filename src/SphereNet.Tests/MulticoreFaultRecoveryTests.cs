using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A tick phase that throws must not take its batch with it (review work item D03).
///
/// Two of the tick's batches are taken from their queue before they are used: the
/// timer wheel's Advance REMOVES the NPCs it returns, and the dirty drain CONSUMES the
/// objects it returns. Anything that throws part of the way through therefore destroys
/// the remainder — and the remainder is not a retry away, because nothing will put it
/// back. The NPCs behind the failure stop acting until a player walks into their
/// sector; the objects behind it never reach a screen at all.
///
/// The multicore path already recovered its NPC batch. The single-threaded path did
/// not, which is the wrong way round: that is the path the server FALLS BACK to after
/// a multicore failure, so the first failure moved the shard onto the loop with no
/// recovery in it. The dirty drain had no recovery on either path.
///
/// What is NOT asserted here: that a phase timeout interrupts a worker already inside
/// a long operation. It does not — a cancellation token stops new iterations from
/// starting, not a running one — and proving that needs the separate long-worker
/// process test the plan keeps open.
/// </summary>
public sealed class MulticoreFaultRecoveryTests
{
    private readonly ITestOutputHelper _out;
    public MulticoreFaultRecoveryTests(ITestOutputHelper output) => _out = output;

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 256, 256);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    /// <summary>NPCs scheduled in the wheel, as a tick would find them: due now.</summary>
    private static (TimerWheel Wheel, List<Character> Npcs) DueNpcs(GameWorld world, int count, long now)
    {
        var wheel = new TimerWheel(now - 5000);
        var npcs = new List<Character>();
        for (int i = 0; i < count; i++)
        {
            var npc = world.CreateCharacter();
            npc.Name = $"npc{i}";
            world.PlaceCharacter(npc, new Point3D((short)(100 + i), 100, 0, 0));
            npc.NextNpcActionTime = now + 1000;
            wheel.Schedule(npc, now - 4000);
            npcs.Add(npc);
        }
        return (wheel, npcs);
    }

    private static bool IsScheduled(TimerWheel wheel, Character npc)
    {
        // The wheel has no "contains" of its own; Schedule refuses an NPC it already
        // holds, so a Schedule that does not change the count means it is in there.
        int before = wheel.Count;
        wheel.Schedule(npc, npc.NextNpcActionTime);
        bool wasScheduled = wheel.Count == before;
        return wasScheduled;
    }

    // ---- the NPC batch ---------------------------------------------------

    [Fact]
    public void AnNpcActionThatThrowsDoesNotTakeTheRestOfTheBatchOutOfTheWheel()
    {
        var world = World();
        long now = 1_000_000;
        var (wheel, npcs) = DueNpcs(world, 6, now);
        var acted = new List<string>();

        var boom = Record.Exception(() => SphereNet.Server.Program.RunDueNpcs(wheel, now,
            npc =>
            {
                acted.Add(npc.Name);
                if (npc.Name == "npc2") throw new InvalidOperationException("a script @Timer threw");
            },
            _ => true));

        _out.WriteLine($"acted=[{string.Join(",", acted)}] exception={boom?.GetType().Name} wheel={wheel.Count}");

        // The failure still reaches the tick handler: it decides whether to abandon
        // the tick and fall back, and hiding it here would make a broken NPC look like
        // a slow one.
        Assert.IsType<InvalidOperationException>(boom);

        // Every NPC is back in the wheel - including the one that threw, which gets
        // another turn rather than falling silent for good.
        foreach (var npc in npcs)
            Assert.True(IsScheduled(wheel, npc), $"{npc.Name} was dropped from the schedule");
    }

    [Fact]
    public void ANormalBatchSchedulesEveryNpcExactlyOnce()
    {
        // The control. Every assertion above would pass just as well if the recovery
        // pass had started scheduling NPCs twice, or if acting had stopped happening.
        var world = World();
        long now = 1_000_000;
        var (wheel, npcs) = DueNpcs(world, 6, now);
        var acted = new List<string>();

        SphereNet.Server.Program.RunDueNpcs(wheel, now, npc => acted.Add(npc.Name), _ => true);

        Assert.Equal(6, acted.Count);
        Assert.Equal(6, wheel.Count);
        foreach (var npc in npcs)
            Assert.True(IsScheduled(wheel, npc));
    }

    [Fact]
    public void AnNpcThatShouldLeaveTheWheelIsNotPutBackByTheRecovery()
    {
        // Recovery must not resurrect a schedule the tick deliberately ended: a dead,
        // deleted or sleeping-sector creature leaves the wheel, and a failure later in
        // the batch is no reason to bring it back.
        var world = World();
        long now = 1_000_000;
        var (wheel, npcs) = DueNpcs(world, 4, now);

        Record.Exception(() => SphereNet.Server.Program.RunDueNpcs(wheel, now,
            npc => { if (npc.Name == "npc3") throw new InvalidOperationException("boom"); },
            npc => npc.Name != "npc1"));

        Assert.False(IsScheduled(wheel, npcs[1]), "npc1 asked to leave the wheel and was put back anyway");
        Assert.True(IsScheduled(wheel, npcs[0]));
        Assert.True(IsScheduled(wheel, npcs[3]));
    }

    // ---- the shared invariant -------------------------------------------

    [Fact]
    public void WhatTheWalkDidNotReachGoesBack()
    {
        var batch = new[] { "a", "b", "c", "d", "e" };
        var walked = new List<string>();
        var recovered = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            SphereNet.Server.Program.WalkRecoverableBatch(batch,
                s => { walked.Add(s); if (s == "c") throw new InvalidOperationException("boom"); },
                recovered.Add));

        _out.WriteLine($"walked=[{string.Join(",", walked)}] recovered=[{string.Join(",", recovered)}]");

        // "c" counts as unreached: it was taken from the queue and its step did not
        // finish.
        Assert.Equal(new[] { "a", "b", "c" }, walked);
        Assert.Equal(new[] { "c", "d", "e" }, recovered);
    }

    [Fact]
    public void ASuccessfulWalkRecoversNothing()
    {
        var recovered = new List<string>();
        SphereNet.Server.Program.WalkRecoverableBatch(new[] { "a", "b" }, _ => { }, recovered.Add);
        Assert.Empty(recovered);
    }

    [Fact]
    public void ARecoveryThatItselfThrowsDoesNotCostTheOthersTheirs()
    {
        var recovered = new List<string>();

        var ex = Record.Exception(() =>
            SphereNet.Server.Program.WalkRecoverableBatch(new[] { "a", "b", "c" },
                _ => throw new InvalidOperationException("the step failed"),
                s =>
                {
                    if (s == "b") throw new NotSupportedException("and putting this one back failed too");
                    recovered.Add(s);
                }));

        // The original failure is what the tick handler must see - not the one from
        // the recovery pass, which would send an operator looking in the wrong place.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal(new[] { "a", "c" }, recovered);
    }
}
