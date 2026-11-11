using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SphereNet.Game.Diagnostics;

/// <summary>Allocation-free checkpoints for the sequential world tick.</summary>
public struct WorldTickProbe
{
    private long _start, _last, _allocationStart, _allocationLast;
    private long _worstTime, _worstAllocation;
    private long _cpuStartUs;
    private string? _timePhase, _allocationPhase;

    public static WorldTickProbe Begin()
    {
        long stamp = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        return new WorldTickProbe { _start = stamp, _last = stamp,
            _allocationStart = allocated, _allocationLast = allocated,
            _cpuStartUs = ThreadCpuClock.NowUs() };
    }

    public void Mark(string phase)
    {
        long stamp = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        long elapsed = stamp - _last, bytes = allocated - _allocationLast;
        if (elapsed > _worstTime) { _worstTime = elapsed; _timePhase = phase; }
        if (bytes > _worstAllocation) { _worstAllocation = bytes; _allocationPhase = phase; }
        _last = stamp;
        _allocationLast = allocated;
    }

    public readonly void Report(ILogger logger, ref long lastReportMs, bool force = false)
    {
        double totalMs = (_last - _start) * 1000.0 / Stopwatch.Frequency;
        long totalBytes = _allocationLast - _allocationStart;
        if (!force && totalMs < 100 && totalBytes < 16 * 1024 * 1024) return;
        long now = Environment.TickCount64;
        // A forced report (the tick was cancelled for running too long) is the one
        // that names the phase behind the stall, so the throttle does not apply.
        if (!force && lastReportMs != 0 && now - lastReportMs < 10000) return;
        lastReportMs = now;
        // cpu = what this thread was actually given during the world tick; a total far
        // above it was spent waiting for a core (or the collector), not in the phase
        // named below.
        long cpuUs = ThreadCpuClock.SinceUs(_cpuStartUs);
        logger.LogWarning("[world_tick_detail] total={TotalMs:F1}ms cpu={CpuMs}ms alloc={AllocationKB}KB " +
            "slowest={Phase} {PhaseMs:F1}ms allocation_source={AllocationPhase} {PhaseKB}KB",
            totalMs, ThreadCpuClock.FormatMs(cpuUs), totalBytes / 1024, _timePhase,
            _worstTime * 1000.0 / Stopwatch.Frequency, _allocationPhase, _worstAllocation / 1024);
    }
}
