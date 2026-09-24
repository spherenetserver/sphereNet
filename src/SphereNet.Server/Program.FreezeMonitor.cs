using Microsoft.Extensions.Logging;

namespace SphereNet.Server;

/// <summary>
/// FREEZERESTARTTIME: a watch on the main loop. Upstream runs the core on its own
/// thread and a monitor checks it every FREEZERESTARTTIME seconds; a loop that has
/// not moved for two checks in a row is declared hung and its thread restarted
/// (StartupMonitorThread::runMonitorLoop, AbstractThread::checkStuck,
/// threads.cpp:702). A running .NET thread cannot be torn down from outside, so the
/// restart here is the process: the hang is logged as critical with the phase the
/// loop was in, recorded, and the process exits with a code the host treats as a
/// crash (HostRestartOnCrash starts it again). The setting was read and never used,
/// so a script stuck in an endless loop froze the shard with players still on.
/// </summary>
public static partial class Program
{
    private static long _loopHeartbeat;

    /// <summary>What the watch decides after one check; the process exit is kept
    /// outside so the rule can be tested on its own.</summary>
    internal sealed class FreezeWatch
    {
        private long _lastSeen = -1;
        private int _missed;

        /// <summary>True once the loop has not moved for two checks in a row
        /// (upstream's 0 -> 0xDEAD -> 0xDEADDEAD -> restart).</summary>
        public bool Check(long heartbeat)
        {
            if (heartbeat != _lastSeen)
            {
                _lastSeen = heartbeat;
                _missed = 0;
                return false;
            }
            return ++_missed >= 2;
        }
    }

    /// <summary>Exit code for a hang, distinct from a clean stop so the host restarts.</summary>
    internal const int FreezeExitCode = 3;

    private static void StartFreezeMonitor(int seconds)
    {
        if (seconds <= 0)
            return; // 0 = off, as upstream runs the core inline without a monitor
        // Upstream looks for a hang only in SECURE mode (runMonitorLoop:
        // "!g_Cfg.m_fSecure -> continue").
        if (!_config.Secure)
            return;
        var watch = new FreezeWatch();
        var thread = new Thread(() =>
        {
            while (_running)
            {
                for (int i = 0; i < seconds && _running; i++)
                    Thread.Sleep(1000);
                if (!_running)
                    break;
                if (!watch.Check(Interlocked.Read(ref _loopHeartbeat)))
                    continue;

                string message = $"'T_Main' hang detected: the main loop has not advanced for about {seconds * 2}s " +
                                 "(FREEZERESTARTTIME); exiting so the host restarts the server";
                _log?.LogCritical("{Message}", message);
                try { RecordCrash(new TimeoutException(message), "freeze-monitor", terminating: true); }
                catch { /* best effort - the exit below matters more */ }
                Environment.Exit(FreezeExitCode);
            }
        })
        {
            IsBackground = true,
            Name = "T_Monitor",
        };
        thread.Start();
    }
}
