using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Panel.Logging;
using SphereNet.Panel.Updates;

// ── Locate SphereNet.Server.exe ─────────────────────────────────────────────

var baseDir   = AppDomain.CurrentDomain.BaseDirectory;

// ── Global crash logging ─────────────────────────────────────────────────────
// Last-resort safety net. Background threads (e.g. the AsyncStreamReader that
// mirrors the child server's output) run outside any try/catch, so an
// unhandled exception there terminates the Host silently — leaving only a
// 0xe0434352 in the Windows event log and no clue why. Persist the full
// exception to logs/host-crash.log so the cause is always recoverable, even
// when the console is already gone.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        WriteCrashLog(baseDir, ex, terminating: e.IsTerminating);
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    WriteCrashLog(baseDir, e.Exception, terminating: false);
    e.SetObserved();
};

var serverExe = Path.Combine(baseDir, "SphereNet.Server.exe");

// Development fallback: look for server exe in the solution bin folder
if (!File.Exists(serverExe))
{
    // Old per-project output layout (pre-unified bin)
    foreach (var cfg in new[] { "Debug", "Release" })
    {
        var devPath = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "src", "SphereNet.Server", "bin", cfg, "net9.0", "SphereNet.Server.exe"));
        if (File.Exists(devPath)) { serverExe = devPath; break; }
    }
}

if (!File.Exists(serverExe))
{
    Console.Error.WriteLine($"[Host] SphereNet.Server.exe not found near: {baseDir}");
    return 1;
}

// ── Read sphere.ini (minimal — just what Host needs) ────────────────────────

string? iniPath     = FindIni(baseDir) ?? FindIni(Path.GetDirectoryName(serverExe)!);
string? scriptsPath = null;
int     panelPort   = 2599;
string  adminPass   = "";
string  serverName  = "SphereNet";
UpdateSettings? updateSettings = null;

if (iniPath != null)
{
    var parser = new IniParser();
    parser.Load(iniPath);
    panelPort   = parser.GetInt ("SPHERE", "AdminPanelPort", 0);
    adminPass   = parser.GetValue("SPHERE", "AdminPassword") ?? "";
    serverName  = parser.GetValue("SPHERE", "ServName") ?? "SphereNet";

    // Panel'in "check for update" akisi. AppUpdateRepo bos ise updater hic
    // kurulmaz ve /api/update/* 404 doner — panel de sayfayi gizler.
    var updateRepo = Trimmed(parser.GetValue("SPHERE", "AppUpdateRepo"));
    if (updateRepo != null)
    {
        updateSettings = new UpdateSettings(
            Repo:         updateRepo,
            Channel:      Trimmed(parser.GetValue("SPHERE", "AppUpdateChannel")) ?? "nightly",
            Runtime:      Trimmed(parser.GetValue("SPHERE", "AppUpdateRuntime")) ?? "win-x64",
            CheckMinutes: parser.GetInt("SPHERE", "AppUpdateCheckMinutes", 15),
            Token:        Trimmed(parser.GetValue("SPHERE", "AppUpdateToken")));
    }

    // Determine scripts path from config
    var scpDir = parser.GetValue("SPHERE", "ScpFilesDir");
    if (scpDir != null)
    {
        var full = Path.IsPathRooted(scpDir) ? scpDir
            : Path.Combine(Path.GetDirectoryName(iniPath)!, scpDir);
        if (Directory.Exists(full)) scriptsPath = full;
    }

    if (scriptsPath == null)
    {
        // Fallback: look for a "scripts" folder near sphere.ini
        var fallback = Path.Combine(Path.GetDirectoryName(iniPath)!, "scripts");
        if (Directory.Exists(fallback)) scriptsPath = fallback;
    }

    if (panelPort <= 0)
    {
        var servPort = parser.GetInt("SPHERE", "ServPort", 2593);
        panelPort = servPort + 3;
    }
}
else
{
    Console.WriteLine("[Host] sphere.ini not found — using defaults.");
}

// ── Wire up components ───────────────────────────────────────────────────────

var logSink = new PanelLogSink();
var ipc     = new IpcBridge();
var proc    = new ServerProcess(serverExe, ipc, logSink);

var panelCtx = HostPanelContext.Build(proc, ipc, iniPath, scriptsPath);
panelCtx.AdminPassword  = adminPass;
panelCtx.ServerName     = serverName;
panelCtx.UpdateSettings = updateSettings;

// Keep PanelContext AdminPassword in sync when setup wizard saves changes
proc.RunningChanged += running =>
{
    if (!running)
        Console.WriteLine("[Host] Server stopped.");
    else
        Console.WriteLine("[Host] Server started.");
};

// Signals the main thread to tear the Host down. Declared before the panel
// starts because OnHostExit below closes over it and must be wired up before
// the panel can serve a request that uses it.
var done = new ManualResetEventSlim(false);

// Applying an update overwrites SphereNet.Host.exe, which this process holds a
// file lock on — so the swap can only happen after we exit. UpdateService has
// already launched the external updater, which is waiting on our PID; it does
// the swap and relaunches us. Run the teardown off the request thread so the
// panel can still return its HTTP response before we go down.
panelCtx.OnHostExit = () =>
{
    _ = Task.Run(() =>
    {
        Console.WriteLine("[Host] Update: exiting so the updater can replace the binaries…");
        if (proc.IsRunning) proc.Stop();
        done.Set();
    });
    return true;
};

// ── Start panel web server ───────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
using var panelHost     = new PanelHost(panelCtx, panelPort, logSink, loggerFactory.CreateLogger("Panel"));
panelHost.Start();

var panelUrl = $"http://localhost:{panelPort}";
Console.WriteLine($"[Host] Panel → {panelUrl}");
Console.WriteLine("[Host] Press Ctrl+C to stop. Type commands to forward to the game server.");

// Auto-open browser after a brief delay so the panel is ready
_ = Task.Delay(1200).ContinueWith(_ => OpenBrowser(panelUrl));

// ── Ctrl+C / SIGTERM ─────────────────────────────────────────────────────────

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Host] Shutting down…");
    if (proc.IsRunning) proc.Stop();
    done.Set();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (proc.IsRunning) proc.Stop();
};

// Host console: forward typed commands to the game server via IPC
var consoleThread = new Thread(() =>
{
    Console.WriteLine("[Host] Ready. Type commands to send to the game server (e.g. 'status', 'save').");
    while (!done.IsSet)
    {
        try
        {
            var line = Console.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!proc.IsRunning) { Console.WriteLine("[Host] Server is not running."); continue; }
            var result = Task.Run(() => ipc.QueryAsync<string[]>("exec", new { raw = line }))
                            .GetAwaiter().GetResult();
            if (result is { Length: > 0 })
                foreach (var l in result) Console.WriteLine($"  {l}");
        }
        catch { break; }
    }
}) { IsBackground = true, Name = "HostConsole" };
consoleThread.Start();

done.Wait();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static void OpenBrowser(string url)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* ignore if no default browser */ }
}

static void WriteCrashLog(string dir, Exception ex, bool terminating)
{
    // Durable record first — the console may be unavailable at this point.
    try
    {
        var logDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logDir);
        var line = $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (terminating={terminating}) ===={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(logDir, "host-crash.log"), line);
    }
    catch { /* nothing more we can safely do */ }

    try
    {
        Console.Error.WriteLine($"[Host] FATAL unhandled exception → logs/host-crash.log: {ex.Message}");
    }
    catch { /* console gone — the file write above is the record that matters */ }
}

// Empty ini values ("AppUpdateToken=") must read as "unset", not as an empty
// string — an empty token would otherwise be sent as an Authorization header.
static string? Trimmed(string? value)
{
    var t = value?.Trim();
    return string.IsNullOrEmpty(t) ? null : t;
}

static string? FindIni(string dir)
{
    foreach (var candidate in new[] {
        Path.Combine(dir, "config", "sphere.ini"),
        Path.Combine(dir, "sphere.ini"),
    })
        if (File.Exists(candidate)) return candidate;
    return null;
}
