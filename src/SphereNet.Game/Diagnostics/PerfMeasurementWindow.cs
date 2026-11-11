using System.Diagnostics;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// One load-test measurement window, opened and read by the console verbs
/// <c>PERF MARK</c> / <c>PERF REPORT</c>.
///
/// The server's own telemetry cannot answer "how did the server behave between these
/// two moments": the tick ring keeps the last 2048 ticks whatever happened before, the
/// [npc_budget] counters reset every 30 seconds, and the GC numbers are process
/// totals. A load runner needs the warm-up (logins, first view builds) kept OUT of the
/// numbers it compares against a baseline, so the window starts when the runner says
/// so and every counter here covers exactly the same span.
///
/// Main-thread only: the tick, the loop and the NPC budget all record from the main
/// loop, and the console verbs run there too. Recording costs one list append per
/// tick and a compare per loop iteration; nothing records while no window is open.
/// </summary>
public sealed class PerfMeasurementWindow
{
    /// <summary>Tick samples kept; at 10 ticks/s this is over an hour of window.</summary>
    public const int MaxTickSamples = 65_536;

    private readonly List<long> _tickUs = new(1024);
    private readonly long _startTimestamp;
    private readonly long _startTick;
    private readonly int _startGen0, _startGen1, _startGen2;
    private readonly TimeSpan _startGcPause;
    private readonly long _startAllocBytes;
    private long _droppedTickSamples;
    private long _loopIterations;
    private long _loopMaxUs;
    private long _loopOver50Ms;
    private long _loopOver100Ms;
    private int _saves;
    private double _saveMaxCaptureMs;
    private double _saveMaxMainThreadMs;
    private int _multicoreFallbacks;

    public PerfMeasurementWindow(long startTick)
    {
        _startTick = startTick;
        _startTimestamp = Stopwatch.GetTimestamp();
        _startGen0 = GC.CollectionCount(0);
        _startGen1 = GC.CollectionCount(1);
        _startGen2 = GC.CollectionCount(2);
        _startGcPause = GC.GetTotalPauseDuration();
        _startAllocBytes = GC.GetTotalAllocatedBytes();
    }

    /// <summary>NPC budget counters for this window only (the server-wide ones reset
    /// on their own 30 s cadence).</summary>
    public NpcBudgetTelemetry NpcBudget { get; } = new();

    public long StartTick => _startTick;
    public int TickSampleCount => _tickUs.Count;

    public void RecordTick(long elapsedUs)
    {
        if (_tickUs.Count >= MaxTickSamples)
        {
            _droppedTickSamples++;
            return;
        }
        _tickUs.Add(Math.Max(0, elapsedUs));
    }

    /// <summary>One whole main-loop iteration (input, periodic jobs, output, ticks,
    /// yield). A save's capture and a GC pause land here even when no tick is slow.</summary>
    public void RecordLoopIteration(long elapsedUs)
    {
        _loopIterations++;
        if (elapsedUs > _loopMaxUs) _loopMaxUs = elapsedUs;
        if (elapsedUs > 50_000) _loopOver50Ms++;
        if (elapsedUs > 100_000) _loopOver100Ms++;
    }

    public void RecordSave(double captureMs, double mainThreadMs)
    {
        _saves++;
        if (captureMs > _saveMaxCaptureMs) _saveMaxCaptureMs = captureMs;
        if (mainThreadMs > _saveMaxMainThreadMs) _saveMaxMainThreadMs = mainThreadMs;
    }

    public void RecordMulticoreFallback() => _multicoreFallbacks++;

    private long _cpuTicks, _cpuSumUs, _offCpuSumUs, _offCpuMaxUs, _starvedTicks;

    /// <summary>One tick's wall time beside the processor time the main thread was
    /// given during it (negative = the host could not say; the tick is then left out).
    /// The difference is time the thread spent NOT running - waiting for a core, for
    /// worker threads, or for a collection - which no change to the tick's own code
    /// can win back.</summary>
    public void RecordTickCpu(long elapsedUs, long cpuUs)
    {
        if (cpuUs < 0) return;
        _cpuTicks++;
        _cpuSumUs += cpuUs;
        long off = Math.Max(0, elapsedUs - cpuUs);
        _offCpuSumUs += off;
        if (off > _offCpuMaxUs) _offCpuMaxUs = off;
        // "Starved": the tick took at least 20 ms and the thread ran for under half of it.
        if (elapsedUs >= 20_000 && cpuUs * 2 < elapsedUs) _starvedTicks++;
    }

    /// <summary>The leaf phases of a tick, in the order the tick runs them. The
    /// [slow_tick] line names them for one slow tick; a window needs them for the
    /// AVERAGE tick, or "the tick got 20 ms slower" cannot be placed without a
    /// profiler attached.</summary>
    public static readonly string[] PhaseNames =
        ["world_tick", "npc_build", "npc_commit", "bots", "dirty", "view_build", "apply", "post_apply", "flush"];

    private readonly long[] _phaseSumUs = new long[PhaseNames.Length];
    private readonly long[] _phaseMaxUs = new long[PhaseNames.Length];
    private long _phaseTicks;

    /// <summary>One tick's phase times, microseconds, in <see cref="PhaseNames"/> order.</summary>
    public void RecordPhases(ReadOnlySpan<long> phaseUs)
    {
        _phaseTicks++;
        int n = Math.Min(phaseUs.Length, _phaseSumUs.Length);
        for (int i = 0; i < n; i++)
        {
            long us = Math.Max(0, phaseUs[i]);
            _phaseSumUs[i] += us;
            if (us > _phaseMaxUs[i]) _phaseMaxUs[i] = us;
        }
    }

    private PerfWindowPhase[] BuildPhases()
    {
        var phases = new PerfWindowPhase[PhaseNames.Length];
        for (int i = 0; i < phases.Length; i++)
            phases[i] = new PerfWindowPhase
            {
                Name = PhaseNames[i],
                AvgMs = _phaseTicks > 0 ? Math.Round(_phaseSumUs[i] / 1000.0 / _phaseTicks, 3) : 0,
                MaxMs = Math.Round(_phaseMaxUs[i] / 1000.0, 3),
            };
        return phases;
    }

    /// <summary>Nearest-rank percentile over microsecond samples, in ms.</summary>
    public static double PercentileMs(long[] sortedUs, double p)
    {
        if (sortedUs.Length == 0) return 0;
        int index = (int)Math.Ceiling(p * sortedUs.Length) - 1;
        return sortedUs[Math.Clamp(index, 0, sortedUs.Length - 1)] / 1000.0;
    }

    public PerfWindowReport BuildReport(long currentTick, int workerCount, bool multicoreEnabled,
        int playingClients, int chars, int items)
    {
        var sorted = _tickUs.ToArray();
        Array.Sort(sorted);
        double elapsedS = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        long allocBytes = Math.Max(0, GC.GetTotalAllocatedBytes() - _startAllocBytes);
        double pauseMs = (GC.GetTotalPauseDuration() - _startGcPause).TotalMilliseconds;
        var p95 = NpcBudget.LatenessP95;
        var p99 = NpcBudget.LatenessP99;
        return new PerfWindowReport
        {
            ElapsedSeconds = Math.Round(elapsedS, 3),
            Ticks = currentTick - _startTick,
            WorkerCount = workerCount,
            MulticoreEnabled = multicoreEnabled,
            MulticoreFallbacks = _multicoreFallbacks,
            PlayingClients = playingClients,
            Chars = chars,
            Items = items,
            Tick = new PerfWindowTick
            {
                Samples = sorted.Length,
                DroppedSamples = _droppedTickSamples,
                AvgMs = sorted.Length > 0 ? Math.Round(sorted.Average() / 1000.0, 3) : 0,
                P50Ms = PercentileMs(sorted, 0.50),
                P95Ms = PercentileMs(sorted, 0.95),
                P99Ms = PercentileMs(sorted, 0.99),
                MaxMs = sorted.Length > 0 ? sorted[^1] / 1000.0 : 0,
                CpuSamples = _cpuTicks,
                AvgCpuMs = _cpuTicks > 0 ? Math.Round(_cpuSumUs / 1000.0 / _cpuTicks, 3) : 0,
                AvgOffCpuMs = _cpuTicks > 0 ? Math.Round(_offCpuSumUs / 1000.0 / _cpuTicks, 3) : 0,
                MaxOffCpuMs = Math.Round(_offCpuMaxUs / 1000.0, 3),
                StarvedTicks = _starvedTicks,
            },
            Loop = new PerfWindowLoop
            {
                Iterations = _loopIterations,
                MaxMs = _loopMaxUs / 1000.0,
                Over50Ms = _loopOver50Ms,
                Over100Ms = _loopOver100Ms,
            },
            Gc = new PerfWindowGc
            {
                Gen0 = GC.CollectionCount(0) - _startGen0,
                Gen1 = GC.CollectionCount(1) - _startGen1,
                Gen2 = GC.CollectionCount(2) - _startGen2,
                PauseMs = Math.Round(pauseMs, 3),
                PausePercent = elapsedS > 0 ? Math.Round(pauseMs / (elapsedS * 1000.0) * 100.0, 3) : 0,
                AllocatedMB = Math.Round(allocBytes / 1048576.0, 3),
                AllocMBPerSec = elapsedS > 0 ? Math.Round(allocBytes / 1048576.0 / elapsedS, 3) : 0,
                HeapMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                WorkingSetMB = Math.Round(Environment.WorkingSet / 1048576.0, 1),
            },
            NpcBudget = new PerfWindowNpcBudget
            {
                Ticks = NpcBudget.Ticks,
                Due = NpcBudget.Due,
                Deferred = NpcBudget.Deferred,
                MaxDuePerTick = NpcBudget.MaxDuePerTick,
                MaxDeferredPerTick = NpcBudget.MaxDeferredPerTick,
                OldestWaitMs = NpcBudget.OldestWaitMs,
                LatenessSamples = NpcBudget.LatenessSamples,
                LatenessP95Ms = p95.ValueMs,
                LatenessP99Ms = p99.ValueMs,
                LatenessMaxMs = NpcBudget.LatenessMaxMs,
            },
            Save = new PerfWindowSave
            {
                Count = _saves,
                MaxCaptureMs = Math.Round(_saveMaxCaptureMs, 3),
                MaxMainThreadMs = Math.Round(_saveMaxMainThreadMs, 3),
            },
            Phases = BuildPhases(),
        };
    }
}

public sealed class PerfWindowReport
{
    public double ElapsedSeconds { get; init; }
    public long Ticks { get; init; }
    public int WorkerCount { get; init; }
    public bool MulticoreEnabled { get; init; }
    public int MulticoreFallbacks { get; init; }
    public int PlayingClients { get; init; }
    public int Chars { get; init; }
    public int Items { get; init; }
    public PerfWindowTick Tick { get; init; } = new();
    public PerfWindowLoop Loop { get; init; } = new();
    public PerfWindowGc Gc { get; init; } = new();
    public PerfWindowNpcBudget NpcBudget { get; init; } = new();
    public PerfWindowSave Save { get; init; } = new();
    /// <summary>Where an average tick of the window went, phase by phase.</summary>
    public PerfWindowPhase[] Phases { get; init; } = [];
}

public sealed class PerfWindowTick
{
    public int Samples { get; init; }
    public long DroppedSamples { get; init; }
    public double AvgMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
    /// <summary>Ticks whose main-thread processor time could be read (0 = the host
    /// cannot say, and the four fields below mean nothing).</summary>
    public long CpuSamples { get; init; }
    /// <summary>Processor time the main thread used per tick.</summary>
    public double AvgCpuMs { get; init; }
    /// <summary>Tick time the main thread was not running, per tick and at worst.</summary>
    public double AvgOffCpuMs { get; init; }
    public double MaxOffCpuMs { get; init; }
    /// <summary>Ticks of 20 ms or more in which the thread ran for under half the time.</summary>
    public long StarvedTicks { get; init; }
}

public sealed class PerfWindowLoop
{
    public long Iterations { get; init; }
    public double MaxMs { get; init; }
    public long Over50Ms { get; init; }
    public long Over100Ms { get; init; }
}

public sealed class PerfWindowGc
{
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
    public double PauseMs { get; init; }
    public double PausePercent { get; init; }
    public double AllocatedMB { get; init; }
    public double AllocMBPerSec { get; init; }
    public double HeapMB { get; init; }
    public double WorkingSetMB { get; init; }
}

public sealed class PerfWindowNpcBudget
{
    public long Ticks { get; init; }
    public long Due { get; init; }
    public long Deferred { get; init; }
    public int MaxDuePerTick { get; init; }
    public int MaxDeferredPerTick { get; init; }
    public long OldestWaitMs { get; init; }
    public int LatenessSamples { get; init; }
    public int LatenessP95Ms { get; init; }
    public int LatenessP99Ms { get; init; }
    public int LatenessMaxMs { get; init; }
}

public sealed class PerfWindowSave
{
    public int Count { get; init; }
    public double MaxCaptureMs { get; init; }
    public double MaxMainThreadMs { get; init; }
}

/// <summary>One tick phase over the window: its share of an average tick and its
/// worst single tick.</summary>
public sealed class PerfWindowPhase
{
    public string Name { get; init; } = "";
    public double AvgMs { get; init; }
    public double MaxMs { get; init; }
}
