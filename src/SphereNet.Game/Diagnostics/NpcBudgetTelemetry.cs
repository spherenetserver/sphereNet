using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Window counters for the per-tick NPC action budget: how many NPCs came due, how
/// many the budget deferred, how long the oldest due NPC had been waiting, and how
/// late the actions that did run were (p95/p99).
///
/// Lateness is <c>now - NextNpcActionTime</c>, the time between when an NPC was due
/// to act and the tick that actually ran it, so a deferral that repeats across
/// several ticks accumulates rather than resetting. NPCs woken without a due time
/// (NextNpcActionTime = 0) carry no lateness and are counted only as due.
/// Main-thread only; telemetry, it changes nothing about which NPC acts.
/// </summary>
public sealed class NpcBudgetTelemetry
{
    private readonly TickHistogram _lateness = new(10_000, 1);
    private long _ticks;
    private long _due;
    private long _deferred;
    private int _maxDuePerTick;
    private int _maxDeferredPerTick;
    private long _oldestWaitMs;

    public long Ticks => _ticks;
    public long Due => _due;
    public long Deferred => _deferred;
    public int MaxDuePerTick => _maxDuePerTick;
    public int MaxDeferredPerTick => _maxDeferredPerTick;
    /// <summary>Longest wait (ms past its due time) of any NPC due in the window,
    /// deferred ones included.</summary>
    public long OldestWaitMs => _oldestWaitMs;
    public int LatenessSamples => _lateness.Count;
    public TickPercentile LatenessP95 => _lateness.GetPercentile(0.95);
    public TickPercentile LatenessP99 => _lateness.GetPercentile(0.99);
    public int LatenessMaxMs => _lateness.MaxMs;

    /// <summary>Record one tick's batch. <paramref name="due"/> is the batch before
    /// the budget cut; the first <paramref name="limit"/> entries act this tick.</summary>
    public void RecordBatch(IReadOnlyList<Character> due, int limit, long nowMs)
    {
        int count = due.Count;
        if (count == 0) return;
        int acting = Math.Min(count, Math.Max(0, limit));
        int deferred = count - acting;

        _ticks++;
        _due += count;
        _deferred += deferred;
        if (count > _maxDuePerTick) _maxDuePerTick = count;
        if (deferred > _maxDeferredPerTick) _maxDeferredPerTick = deferred;

        for (int i = 0; i < count; i++)
        {
            long dueAt = due[i].NextNpcActionTime;
            if (dueAt <= 0) continue;
            long late = Math.Max(0, nowMs - dueAt);
            if (late > _oldestWaitMs) _oldestWaitMs = late;
            if (i < acting)
                _lateness.Record((int)Math.Min(int.MaxValue, late));
        }
    }

    public void Reset()
    {
        _lateness.Reset();
        _ticks = 0;
        _due = 0;
        _deferred = 0;
        _maxDuePerTick = 0;
        _maxDeferredPerTick = 0;
        _oldestWaitMs = 0;
    }
}
