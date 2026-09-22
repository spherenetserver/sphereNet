using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SphereNet.Game.Diagnostics;

/// <summary>Allocation-free checkpoints for the sequential world tick.</summary>
public struct WorldTickProbe
{
    private long _start, _last, _allocationStart, _allocationLast;
    private long _worstTime, _worstAllocation;
    private string? _timePhase, _allocationPhase;

    public static WorldTickProbe Begin()
    {
        long stamp = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        return new WorldTickProbe { _start = stamp, _last = stamp,
            _allocationStart = allocated, _allocationLast = allocated };
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

    public readonly void Report(ILogger logger, ref long lastReportMs)
    {
        double totalMs = (_last - _start) * 1000.0 / Stopwatch.Frequency;
        long totalBytes = _allocationLast - _allocationStart;
        if (totalMs < 100 && totalBytes < 16 * 1024 * 1024) return;
        long now = Environment.TickCount64;
        if (lastReportMs != 0 && now - lastReportMs < 10000) return;
        lastReportMs = now;
        logger.LogWarning("[world_tick_detail] total={TotalMs:F1}ms alloc={AllocationKB}KB " +
            "slowest={Phase} {PhaseMs:F1}ms allocation_source={AllocationPhase} {PhaseKB}KB",
            totalMs, totalBytes / 1024, _timePhase,
            _worstTime * 1000.0 / Stopwatch.Frequency, _allocationPhase, _worstAllocation / 1024);
    }
}
