using System.Linq;
using SphereNet.Game.Scheduling;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 10 / H8 — a failed/timed-out multicore tick used to be replayed by calling
/// RunSingleThreadTick for the SAME tick, double-applying the part the multicore
/// attempt had already run. The fix abandons the partial tick (no replay) and
/// instead reschedules the NPCs the tick had consumed from the timer wheel.
///
/// These lock in the TimerWheel properties that recovery relies on: Advance
/// consumes a due NPC exactly once, re-scheduling a consumed NPC puts it back
/// (so a failed tick can recover the batch it Advanced), and Schedule is
/// idempotent (so recovering a batch the apply phase already partly rescheduled
/// never double-fires).
/// </summary>
public sealed class TimerWheelReschedulingTests
{
    [Fact]
    public void Advance_FiresDueNpcExactlyOnce_ThenConsumesIt()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 50);
        var due1 = wheel.Advance(200).ToList();
        Assert.Single(due1, n => n == npc); // fired once
        Assert.Equal(0, wheel.Count);              // and removed from the wheel

        // Consumed: a later Advance does not fire it again.
        var due2 = wheel.Advance(400).ToList();
        Assert.DoesNotContain(npc, due2);
    }

    [Fact]
    public void RescheduleAfterConsume_RecoversTheNpc()
    {
        // Models the failed-tick recovery: Advance consumed the NPC, then the
        // recovery reschedules it so it isn't dropped from the wheel.
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 50);
        var due1 = wheel.Advance(200).ToList();
        Assert.Contains(npc, due1);
        Assert.Equal(0, wheel.Count); // gone from the wheel after Advance

        wheel.Schedule(npc, 250);     // recovery
        Assert.Equal(1, wheel.Count);
        var due2 = wheel.Advance(400).ToList();
        Assert.Contains(npc, due2);   // fires again — not lost
    }

    [Fact]
    public void Schedule_IsIdempotentPerNpc()
    {
        // The recovery reschedules a whole batch, some of which the apply phase
        // may already have rescheduled; the duplicate add must be a no-op.
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 50);
        wheel.Schedule(npc, 50); // duplicate
        Assert.Equal(1, wheel.Count);

        var due = wheel.Advance(200).ToList();
        Assert.Single(due, n => n == npc); // fires exactly once, not twice
    }
}
