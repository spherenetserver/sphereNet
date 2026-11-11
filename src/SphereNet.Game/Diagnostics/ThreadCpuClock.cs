using System.Runtime.InteropServices;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Processor time the calling thread has actually been given.
///
/// A tick is timed against the wall clock, and a slow one reads the same whether the
/// work was slow or the thread simply was not running: preempted on a host with more
/// runnable threads than cores, suspended for a collection, or parked waiting for
/// worker threads that had not been scheduled yet. Measured on a machine whose cores
/// were saturated by other processes, single calls that do a dictionary lookup showed
/// up as 100-400 ms "hot spots" - the time the main thread spent waiting for a core in
/// the middle of them. Reading the thread's own CPU time across the same span tells
/// the two apart: a 180 ms tick that used 14 ms of processor is a host problem, not a
/// code path to optimise.
///
/// Windows keeps a thread's times in scheduler-clock steps of 15.6 ms - too coarse to
/// split one 60 ms tick - but also counts the processor cycles the thread has run
/// (QueryThreadCycleTime), which is exact. The cycle count is turned into time with a
/// rate measured against those same coarse times once the thread has run for a few
/// seconds; until then the coarse value is what is returned. Returns -1 where the
/// platform call is missing.
/// </summary>
public static class ThreadCpuClock
{
    private static bool _unavailable;

    /// <summary>CPU time (user + kernel) of the calling thread, in microseconds, or
    /// -1 when it cannot be read.</summary>
    public static long NowUs()
    {
        if (_unavailable)
            return -1;
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsNowUs();
            else
            {
                // CLOCK_THREAD_CPUTIME_ID: 3 on Linux, 16 on macOS.
                int clockId = OperatingSystem.IsMacOS() ? 16 : 3;
                if (clock_gettime(clockId, out Timespec ts) == 0)
                    return ts.Seconds * 1_000_000L + ts.Nanoseconds / 1000;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        _unavailable = true;
        return -1;
    }

    // Cycles per microsecond of thread time (0 = not measured yet), and the offset
    // that makes the cycle-based reading continue where the coarse one left off for
    // the thread that measured it. One rate serves every thread: it is the
    // processor's cycle counter frequency.
    private static double s_cyclesPerUs;
    [ThreadStatic] private static ulong t_cyclesAtFirst;
    [ThreadStatic] private static long t_timesAtFirstUs;
    [ThreadStatic] private static long t_offsetUs;
    [ThreadStatic] private static byte t_state;    // 0 new, 1 calibrating, 2 on cycles
    private const long CalibrationSpanUs = 4_000_000;

    private static long WindowsNowUs()
    {
        nint thread = GetCurrentThread();
        double rate = s_cyclesPerUs;
        if (t_state == 2 && QueryThreadCycleTime(thread, out ulong running))
            return (long)(running / rate) + t_offsetUs;

        if (!GetThreadTimes(thread, out _, out _, out long kernel, out long user))
        {
            _unavailable = true;
            return -1;
        }
        long timesUs = (kernel + user) / 10;        // FILETIME units of 100 ns
        if (!QueryThreadCycleTime(thread, out ulong cycles))
            return timesUs;

        if (rate > 0)
        {
            // Another thread measured the rate; join it without a jump.
            t_offsetUs = timesUs - (long)(cycles / rate);
            t_state = 2;
            return timesUs;
        }
        if (t_state == 0)
        {
            t_cyclesAtFirst = cycles;
            t_timesAtFirstUs = timesUs;
            t_state = 1;
        }
        else if (timesUs - t_timesAtFirstUs >= CalibrationSpanUs && cycles > t_cyclesAtFirst)
        {
            rate = (cycles - t_cyclesAtFirst) / (double)(timesUs - t_timesAtFirstUs);
            t_offsetUs = timesUs - (long)(cycles / rate);
            t_state = 2;
            s_cyclesPerUs = rate;
        }
        return timesUs;
    }

    /// <summary>CPU time used since <paramref name="startUs"/> (a value from
    /// <see cref="NowUs"/>), or -1 when either reading is missing.</summary>
    public static long SinceUs(long startUs)
    {
        if (startUs < 0)
            return -1;
        long now = NowUs();
        return now < 0 ? -1 : Math.Max(0, now - startUs);
    }

    /// <summary>A CPU time for a log line: milliseconds to one decimal, or "?" when
    /// the host could not say.</summary>
    public static string FormatMs(long cpuUs) =>
        cpuUs >= 0
            ? (cpuUs / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : "?";

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint hThread, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(nint hThread, out ulong cycleTime);

    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int clockId, out Timespec ts);
}
