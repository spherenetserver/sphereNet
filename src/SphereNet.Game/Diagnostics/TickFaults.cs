using System.Collections.Concurrent;
using SphereNet.Game.Objects;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Containment for one object's tick. Upstream wraps every object it ticks in its
/// own exception block and moves on to the next one (CWorldTicker, EXC_TRY/EXC_CATCH
/// per object); a fault here used to leave the loop, so every object after the
/// faulty one in the same pass went without its tick - and when the same script threw
/// on every pass, that part of the world simply stopped while players stayed on.
///
/// The object is reported and the pass continues. Reports are throttled per object
/// so a script that throws ten times a second does not flood the log.
/// </summary>
public static class TickFaults
{
    private const long RepeatLogIntervalMs = 60_000;
    private static readonly ConcurrentDictionary<uint, long> LastLogged = new();

    /// <summary>Where a fault is written. Wired to the server log at startup.</summary>
    public static Action<string, Exception>? Log { get; set; }

    /// <summary>How many faults have been contained since startup.</summary>
    public static long Count => Interlocked.Read(ref _count);
    private static long _count;

    public static void Report(ObjBase obj, string phase, Exception ex)
    {
        Interlocked.Increment(ref _count);
        long now = Environment.TickCount64;
        uint uid = obj.Uid.Value;
        if (LastLogged.TryGetValue(uid, out long last) && now - last < RepeatLogIntervalMs)
            return;
        LastLogged[uid] = now;

        string name;
        try { name = obj.Name; } catch { name = "?"; }
        Log?.Invoke(
            $"[tick_fault] {phase} failed for {obj.GetType().Name} 0{uid:X8} '{name}' at {obj.Position}; " +
            "the object was skipped and the tick continued (repeats from it are logged once a minute)",
            ex);
    }

    /// <summary>Test isolation.</summary>
    public static void Reset()
    {
        LastLogged.Clear();
        Interlocked.Exchange(ref _count, 0);
        Log = null;
    }
}
