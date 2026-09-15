using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The wheel against a scheduler that cannot be wrong (review work item D07).
///
/// A hashed timing wheel is 256 slots of 100 ms, so the same slot comes round every
/// 25.6 seconds and a deadline further out than that is met by an entry that has to be
/// left where it is and looked at again a revolution later. Tests written against that
/// mechanism tend to test the mechanism. This one compares it with the dumbest correct
/// scheduler there is — a list of deadlines, scanned — over a fixed-seed sequence of
/// schedules, removals and advances, and asserts the property that matters rather than
/// the shape of the machinery:
///
///   every NPC fires exactly once, at the first advance at or after its deadline,
///   never earlier, and one that was removed never fires at all.
///
/// The contract it pins, because callers have to know it: Schedule with a deadline that
/// has already passed does NOT fire immediately — it is clamped to the next slot, so
/// the effective deadline is at least one 100 ms tick away.
/// </summary>
public sealed class TimerWheelModelTests
{
    private readonly ITestOutputHelper _out;
    public TimerWheelModelTests(ITestOutputHelper output) => _out = output;

    private const long SlotMs = 100;

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Character Npc(GameWorld world, int i)
    {
        var npc = world.CreateCharacter();
        npc.Name = $"npc{i}";
        world.PlaceCharacter(npc, new Point3D((short)(100 + (i % 50)), (short)(100 + i / 50), 0, 0));
        return npc;
    }

    /// <summary>The reference: deadlines in a list. Fires everything due, in no
    /// particular order, exactly once.</summary>
    private sealed class ReferenceScheduler
    {
        private readonly Dictionary<uint, long> _due = [];
        public void Schedule(uint uid, long deadline)
        {
            // The wheel refuses a second scheduling for a uid it already holds, and
            // clamps a deadline that has already passed to the next slot.
            if (!_due.ContainsKey(uid)) _due[uid] = deadline;
        }
        public void Remove(uint uid) => _due.Remove(uid);
        public bool Holds(uint uid) => _due.ContainsKey(uid);
        public List<uint> Advance(long now)
        {
            var fired = _due.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (uint uid in fired) _due.Remove(uid);
            return fired;
        }
        public int Count => _due.Count;
    }

    /// <summary>What Schedule actually promises: never before the next slot.</summary>
    private static long EffectiveDeadline(long requested, long wheelNow) =>
        requested <= wheelNow ? wheelNow + SlotMs : requested;

    /// <summary>When the wheel can first deliver a deadline.
    ///
    /// This is the quantisation the wheel's shape imposes, and the reason the model
    /// scheduler cannot simply be "fire everything whose deadline has passed": a
    /// deadline is parked in the slot that covers it, rounded UP, and that slot is
    /// walked when the clock reaches its start. A deadline of 1,004,927 is therefore
    /// delivered at 1,005,000 and not a millisecond earlier, whatever the tick rate.
    /// The engine ticks every 100 ms, so the visible cost is under one tick - but a
    /// caller that advances the wheel at irregular intervals has to know that the
    /// deadline it asked for is rounded up, not honoured to the millisecond.</summary>
    private static long QuantisedDeadline(long deadline) =>
        (deadline + SlotMs - 1) / SlotMs * SlotMs;

    // ---- the comparison --------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1337)]
    public void TheWheelAgreesWithAListOfDeadlines(int seed)
    {
        var world = World();
        var rng = new Random(seed);
        long now = 1_000_000;
        var wheel = new TimerWheel(now);
        var reference = new ReferenceScheduler();
        var npcs = Enumerable.Range(0, 40).Select(i => Npc(world, i)).ToList();
        var byUid = npcs.ToDictionary(n => n.Uid.Value, n => n);

        var fireCount = new Dictionary<uint, int>();
        var earliestAllowed = new Dictionary<uint, long>();
        int scheduled = 0, removed = 0, fired = 0;
        long worstLateness = 0;

        for (int step = 0; step < 4000; step++)
        {
            switch (rng.Next(3))
            {
                case 0:     // schedule something, sometimes already overdue
                {
                    var npc = npcs[rng.Next(npcs.Count)];
                    long deadline = now + rng.Next(-500, 40_000);
                    if (reference.Holds(npc.Uid.Value)) break;     // the wheel would refuse it
                    wheel.Schedule(npc, deadline);
                    long effective = EffectiveDeadline(deadline, now);
                    reference.Schedule(npc.Uid.Value, QuantisedDeadline(effective));
                    earliestAllowed[npc.Uid.Value] = effective;
                    scheduled++;
                    break;
                }
                case 1:     // cancel something
                {
                    var npc = npcs[rng.Next(npcs.Count)];
                    wheel.Remove(npc);
                    reference.Remove(npc.Uid.Value);
                    removed++;
                    break;
                }
                default:    // let time pass, in irregular steps
                {
                    now += rng.Next(1, 900);
                    var wheelFired = wheel.Advance(now).Select(n => n.Uid.Value).ToList();
                    var referenceFired = reference.Advance(now);

                    foreach (uint uid in wheelFired)
                    {
                        fireCount[uid] = fireCount.GetValueOrDefault(uid) + 1;
                        // Never early against the deadline the caller ASKED for, not
                        // against the rounded one: rounding up may only ever delay.
                        Assert.True(now >= earliestAllowed[uid],
                            $"npc {byUid[uid].Name} fired at {now}, before its deadline {earliestAllowed[uid]}");
                        long late = now - earliestAllowed[uid];
                        if (late > worstLateness) worstLateness = late;
                    }
                    fired += wheelFired.Count;

                    // Same set, not merely the same count: a wheel that fired the
                    // wrong NPC at the right moment would pass a count check.
                    Assert.Equal(referenceFired.OrderBy(u => u), wheelFired.OrderBy(u => u));
                    break;
                }
            }
            Assert.Equal(reference.Count, wheel.Count);
        }

        _out.WriteLine($"seed {seed}: {scheduled} scheduled, {removed} removed, {fired} fired, " +
                       $"{wheel.Count} still waiting; worst lateness {worstLateness}ms");
        Assert.True(fired > 100, "the sequence did not exercise firing");

        // The quantisation is one slot, and the advances here are up to 900 ms apart,
        // so nothing may be later than the slot boundary plus the gap between ticks.
        Assert.True(worstLateness < SlotMs + 900,
            $"something fired {worstLateness}ms after its deadline");
    }

    // ---- the boundaries --------------------------------------------------

    [Theory]
    [InlineData(99)]        // inside the first slot
    [InlineData(100)]       // exactly one slot
    [InlineData(101)]       // just past it
    [InlineData(25_599)]    // just inside one revolution
    [InlineData(25_600)]    // exactly one revolution: the same slot again
    [InlineData(25_601)]    // just past a revolution
    [InlineData(30_000)]
    [InlineData(60_000)]
    public void ADeadlineFiresOnTheFirstTickAtOrAfterIt(long offset)
    {
        var world = World();
        long start = 1_000_000;
        var wheel = new TimerWheel(start);
        var npc = Npc(world, 1);
        long deadline = start + offset;
        wheel.Schedule(npc, deadline);

        long firedAt = -1;
        for (long t = start + SlotMs; t <= start + offset + 30_000; t += SlotMs)
        {
            if (wheel.Advance(t).Count > 0)
            {
                firedAt = t;
                break;
            }
        }

        _out.WriteLine($"deadline +{offset}ms fired at +{firedAt - start}ms " +
                       $"(late by {firedAt - deadline}ms)");

        // Never early, and never more than one tick late: the slot a deadline is
        // parked in is rounded UP, and the entry is only released when its own
        // deadline has actually passed.
        Assert.True(firedAt >= deadline, $"fired {deadline - firedAt}ms early");
        Assert.True(firedAt - deadline < SlotMs,
            $"fired {firedAt - deadline}ms late, more than one tick");
        Assert.Equal(0, wheel.Count);
    }

    // ---- identity --------------------------------------------------------

    [Fact]
    public void AUidThatComesBackAsANewCreatureDoesNotInheritTheOldSchedule()
    {
        // The wheel is keyed by uid, and uids are reused: an NPC is deleted and the
        // next creature to be created can be handed the same one. A stale slot entry
        // still naming that uid must not fire for whoever holds it now.
        var world = World();
        long now = 1_000_000;
        var wheel = new TimerWheel(now);

        var first = Npc(world, 1);
        uint uid = first.Uid.Value;
        wheel.Schedule(first, now + 200);
        wheel.Remove(first);
        first.Delete();

        var second = Npc(world, 2);
        second.UidRef = new Serial(uid);          // the same uid, a different creature

        long t = now;
        var firedAnything = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            t += SlotMs;
            firedAnything.AddRange(wheel.Advance(t).Select(n => n.Name));
        }

        _out.WriteLine($"after a uid came back: fired [{string.Join(",", firedAnything)}]");
        Assert.Empty(firedAnything);
        Assert.Equal(0, wheel.Count);
    }

    [Fact]
    public void ARemovedNpcNeverFiresEvenWithItsEntryStillInASlot()
    {
        var world = World();
        long now = 1_000_000;
        var wheel = new TimerWheel(now);
        var npc = Npc(world, 1);

        wheel.Schedule(npc, now + 200);
        wheel.Remove(npc);
        Assert.Equal(0, wheel.Count);

        var fired = new List<Character>();
        for (long t = now + SlotMs; t <= now + 30_000; t += SlotMs)
            fired.AddRange(wheel.Advance(t));

        Assert.Empty(fired);
    }

    [Fact]
    public void RescheduleAfterAFireIsHonouredRatherThanRefused()
    {
        // The whole loop the tick runs: fire, act, schedule again. A wheel that kept
        // the uid in its scheduled map after firing would refuse the re-schedule and
        // the creature would stop for good.
        var world = World();
        long now = 1_000_000;
        var wheel = new TimerWheel(now);
        var npc = Npc(world, 1);

        int fires = 0;
        wheel.Schedule(npc, now + 200);
        for (int round = 0; round < 20; round++)
        {
            now += 300;
            foreach (var due in wheel.Advance(now).ToList())
            {
                fires++;
                wheel.Schedule(due, now + 200);
            }
        }

        _out.WriteLine($"twenty rounds of fire-and-reschedule: {fires} fires, {wheel.Count} pending");
        Assert.Equal(20, fires);
        Assert.Equal(1, wheel.Count);
    }

    // ---- what Count does not say -----------------------------------------

    [Fact]
    public void CountIsLiveSchedulesAndNotTheMemoryTheSlotsHold()
    {
        // Remove leaves the slot entry where it is and retires it by generation when
        // the slot is next walked. Dense rescheduling therefore holds more entries
        // than Count reports, for up to one revolution - worth knowing before reading
        // Count as a memory figure.
        var world = World();
        long now = 1_000_000;
        var wheel = new TimerWheel(now);
        var npcs = Enumerable.Range(0, 50).Select(i => Npc(world, i)).ToList();

        for (int round = 0; round < 20; round++)
        {
            foreach (var npc in npcs)
            {
                wheel.Remove(npc);
                wheel.Schedule(npc, now + 20_000);      // far enough to stay parked
            }
        }

        int entries = SlotEntryCount(wheel);
        _out.WriteLine($"50 NPCs rescheduled 20 times: Count={wheel.Count}, slot entries={entries}");
        Assert.Equal(50, wheel.Count);
        Assert.True(entries > wheel.Count,
            "the stale entries were expected to still be in their slots");

        // And they are reclaimed by walking, not by counting: one full revolution
        // retires every stale entry.
        for (long t = now; t <= now + 25_600 + 20_000; t += SlotMs)
            wheel.Advance(t);
        _out.WriteLine($"after a full revolution: Count={wheel.Count}, slot entries={SlotEntryCount(wheel)}");
        Assert.Equal(0, SlotEntryCount(wheel));
    }

    private static int SlotEntryCount(TimerWheel wheel)
    {
        var field = typeof(TimerWheel).GetField("_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var slots = (System.Collections.IList)field.GetValue(wheel)!;
        int total = 0;
        foreach (System.Collections.IList slot in slots)
            total += slot.Count;
        return total;
    }

    // ---- the clock jumping forward ---------------------------------------

    [Fact]
    public void ALongJumpOnAnEmptyWheelCostsWhatItCosts()
    {
        // A process that stalled - a long save, a paused VM - comes back to a clock
        // that has moved a long way. The wheel walks slot by slot, so the cost is the
        // elapsed time divided by the slot duration, however empty it is. Measured
        // rather than asserted away, with a generous bound: the point is that an hour
        // is not seconds.
        var wheel = new TimerWheel(0);
        var sw = Stopwatch.StartNew();
        wheel.Advance(60L * 60 * 1000);          // one hour, empty
        sw.Stop();

        _out.WriteLine($"one-hour jump on an empty wheel: {sw.Elapsed.TotalMilliseconds:F1} ms " +
                       $"({60 * 60 * 1000 / SlotMs} slot steps)");
        Assert.True(sw.Elapsed.TotalSeconds < 5,
            $"a one-hour clock jump took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void ALongJumpStillFiresEverythingThatCameDueDuringIt()
    {
        var world = World();
        long now = 1_000_000;
        var wheel = new TimerWheel(now);
        var npcs = Enumerable.Range(0, 30).Select(i => Npc(world, i)).ToList();
        for (int i = 0; i < npcs.Count; i++)
            wheel.Schedule(npcs[i], now + 1000 + i * 60_000);      // spread over half an hour

        var fired = wheel.Advance(now + 60L * 60 * 1000).ToList();

        _out.WriteLine($"one-hour jump with 30 deadlines inside it: {fired.Count} fired, " +
                       $"{wheel.Count} left");
        Assert.Equal(30, fired.Count);
        Assert.Equal(0, wheel.Count);
    }
}
