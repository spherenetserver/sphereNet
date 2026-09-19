using System.Text;
using Microsoft.Extensions.Logging;

namespace SphereNet.Server;

public static partial class Program
{
    /// <summary>Where a crash record goes. Resolved once at boot so the handler needs
    /// nothing but a path when it runs - by then the config may be half-built.</summary>
    private static string _crashLogPath = "";

    /// <summary>Boot time, for the one line a reader always wants first: how long had it
    /// been running.</summary>
    private static readonly DateTime _processStart = DateTime.Now;

    /// <summary>
    /// Catch what the tick loop cannot.
    ///
    /// The main loop already contains a fault in any of its phases, so a bad script or a
    /// bad packet does not take the shard down. What it cannot contain is an exception on
    /// one of the other threads - the network accept loop, a timer, a save worker, the
    /// console reader - or one thrown during boot before the loop exists. Those ended the
    /// process with whatever the console happened to still be showing, which on a
    /// detached or reconnected session is nothing at all.
    ///
    /// The Host has had this since an RDP drop killed it silently; the server, which is
    /// the process holding the world, had none.
    /// </summary>
    private static void InstallCrashRecorder(string logDir)
    {
        try
        {
            _crashLogPath = Path.Combine(logDir, "server-crash.log");
        }
        catch
        {
            _crashLogPath = "server-crash.log";
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                RecordCrash(ex, "AppDomain.UnhandledException", e.IsTerminating);
        };

        // Not fatal on modern .NET, but a fault nobody observed is a fault nobody can
        // explain later - and it is often the first sign of the one that is.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RecordCrash(e.Exception, "TaskScheduler.UnobservedTaskException", terminating: false);
            e.SetObserved();
        };
    }

    /// <summary>Write one crash record: the exception chain, and the state of the shard
    /// around it.
    ///
    /// A stack trace says where it broke. What turns that into a diagnosis is what the
    /// server was doing - how long it had been up, which tick it was on, how many
    /// players and objects it was holding, whether the main loop had already been
    /// failing. Every one of those is a line, and the file is appended so a crash loop
    /// leaves a history rather than overwriting its own evidence.</summary>
    private static void RecordCrash(Exception ex, string source, bool terminating)
    {
        string text;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {source} " +
                          $"(terminating={terminating}) ====");
            sb.AppendLine($"thread   : {Environment.CurrentManagedThreadId} " +
                          $"'{Thread.CurrentThread.Name ?? "(unnamed)"}'");
            sb.AppendLine($"uptime   : {DateTime.Now - _processStart:c}");
            sb.AppendLine(DescribeShardState());
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            text = sb.ToString();
        }
        catch (Exception describeFailure)
        {
            // Never let the recorder be the reason there is no record.
            text = $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {source} ====" +
                   Environment.NewLine + ex + Environment.NewLine +
                   "(state description failed: " + describeFailure.Message + ")" +
                   Environment.NewLine + Environment.NewLine;
        }

        try
        {
            string path = string.IsNullOrEmpty(_crashLogPath) ? "server-crash.log" : _crashLogPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, text);
        }
        catch { /* nothing more we can safely do */ }

        // Then through the logger, which may reach the console, the panel and Sentry -
        // but only after the file, because any of those can be gone.
        try { _log?.LogCritical(ex, "FATAL ({Source}) - recorded to {Path}", source, _crashLogPath); }
        catch { }

        try { Console.Error.WriteLine($"[server] FATAL ({source}) -> {_crashLogPath}: {ex.Message}"); }
        catch { }
    }

    /// <summary>The shard's state in a few lines. Each read is guarded on its own: at
    /// crash time any of these may be half-initialised, and a reader would rather have
    /// four of the five than none.</summary>
    private static string DescribeShardState()
    {
        var sb = new StringBuilder();
        sb.Append("state    : ");
        sb.Append("tick=").Append(Safe(() => _tickCounter.ToString()));
        sb.Append(" loopFaults=").Append(Safe(() => _mainLoopFailureCount.ToString()));
        sb.Append(" objects=").Append(Safe(() => _world.TotalObjects.ToString()));
        sb.Append(" chars/items=").Append(Safe(() =>
        {
            var (chars, items, _) = _world.GetStats();
            return chars + "/" + items;
        }));
        sb.Append(" clients=").Append(Safe(() => _clients.Count.ToString()));
        sb.Append(" gcHeapMB=").Append(Safe(() => (GC.GetTotalMemory(false) / 1048576).ToString()));
        return sb.ToString();

        static string Safe(Func<string> read)
        {
            try { return read(); } catch { return "?"; }
        }
    }
}
