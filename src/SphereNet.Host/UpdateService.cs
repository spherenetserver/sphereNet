using System.Collections.Concurrent;
using System.Diagnostics;
using SphereNet.Panel;
using SphereNet.Panel.Logging;

namespace SphereNet.Host;

public sealed class UpdateService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(15);

    private readonly Func<bool> _isServerRunning;
    private readonly Action _saveServer;
    private readonly Action _stopServer;
    private readonly Action _startServer;
    private readonly string _hostBaseDir;
    private readonly IUpdateCommandRunner _runner;
    private readonly PanelLogSink? _logSink;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _log = new();
    private readonly object _stateLock = new();

    private bool _isRunning;
    private string _state = "Idle";
    private string _message = "No update has been run yet.";
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private int? _exitCode;
    private bool _requiresHostRestart;

    public UpdateService(
        Func<bool> isServerRunning,
        Action saveServer,
        Action stopServer,
        Action startServer,
        string hostBaseDir,
        PanelLogSink? logSink = null,
        IUpdateCommandRunner? runner = null)
    {
        _isServerRunning = isServerRunning;
        _saveServer = saveServer;
        _stopServer = stopServer;
        _startServer = startServer;
        _hostBaseDir = hostBaseDir;
        _logSink = logSink;
        _runner = runner ?? new ProcessUpdateCommandRunner(AppendLog);
    }

    public async Task<UpdateStartResult> StartAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            var busy = GetStatus();
            return new UpdateStartResult(false, "Update is already running.", busy);
        }

        ResetState();
        _ = Task.Run(RunUpdateAsync);
        var status = GetStatus();
        return new UpdateStartResult(true, "Update started.", status);
    }

    public UpdateStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new UpdateStatus(
                _isRunning,
                _state,
                _message,
                _startedAt,
                _finishedAt,
                _exitCode,
                _requiresHostRestart,
                _log.ToArray());
        }
    }

    private async Task RunUpdateAsync()
    {
        try
        {
            SetState("Preparing", "Finding repository root.");
            var repoRoot = FindRepositoryRoot(_hostBaseDir)
                ?? throw new InvalidOperationException($"Could not find a .git folder above '{_hostBaseDir}'.");

            AppendLog($"Repository: {repoRoot}");

            SetState("Checking", "Checking repository status.");
            var status = await RunRequiredAsync("git", "status --porcelain", repoRoot, GitTimeout).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status.Output))
                throw new InvalidOperationException("Repository has local changes. Commit, stash, or clean them before updating.");

            SetState("Pulling", "Pulling latest changes from GitHub.");
            await RunRequiredAsync("git", "pull --ff-only", repoRoot, GitTimeout).ConfigureAwait(false);

            if (_isServerRunning())
            {
                SetState("Saving", "Saving world state before update.");
                _saveServer();
                await Task.Delay(1500).ConfigureAwait(false);

                SetState("Stopping", "Stopping game server.");
                _stopServer();
            }

            SetState("Building", "Publishing latest server build.");
            await RunRequiredAsync(
                "powershell",
                "-NoProfile -ExecutionPolicy Bypass -File .\\build.ps1 -Configuration Release -Runtime win-x64 -ServerOnly",
                repoRoot,
                BuildTimeout).ConfigureAwait(false);

            SetState("Starting", "Starting updated game server.");
            _startServer();

            SetCompleted("Completed", "Update completed. Restart SphereNet.Host later to load Host/backend binary changes.", 0, true);
        }
        catch (Exception ex)
        {
            SetCompleted("Failed", ex.Message, 1, false);
            try
            {
                if (!_isServerRunning())
                    _startServer();
            }
            catch (Exception startEx)
            {
                AppendLog($"Failed to restart server after update failure: {startEx.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCommandResult> RunRequiredAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        AppendLog($"> {fileName} {arguments}");
        var result = await _runner.RunAsync(new UpdateCommand(fileName, arguments, workingDirectory, timeout))
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {result.ExitCode}.");

        return result;
    }

    private void ResetState()
    {
        while (_log.TryDequeue(out _)) { }
        lock (_stateLock)
        {
            _isRunning = true;
            _state = "Queued";
            _message = "Update queued.";
            _startedAt = DateTime.UtcNow;
            _finishedAt = null;
            _exitCode = null;
            _requiresHostRestart = false;
        }
        AppendLog("Update started.");
    }

    private void SetState(string state, string message)
    {
        lock (_stateLock)
        {
            _state = state;
            _message = message;
        }
        AppendLog(message);
    }

    private void SetCompleted(string state, string message, int exitCode, bool requiresHostRestart)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _state = state;
            _message = message;
            _finishedAt = DateTime.UtcNow;
            _exitCode = exitCode;
            _requiresHostRestart = requiresHostRestart;
        }
        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        foreach (var line in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _log.Enqueue(stamped);
            _logSink?.AddEntry(new LogEntry(DateTime.UtcNow, "Information", line, "Updater"));

            while (_log.Count > 200 && _log.TryDequeue(out _)) { }
        }
    }

    public static string? FindRepositoryRoot(string startDirectory)
    {
        var dir = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null)
                return null;

            dir = parent.FullName;
        }

        return null;
    }
}

public sealed record UpdateCommand(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    TimeSpan Timeout);

public sealed record UpdateCommandResult(int ExitCode, string Output);

public interface IUpdateCommandRunner
{
    Task<UpdateCommandResult> RunAsync(UpdateCommand command);
}

public sealed class ProcessUpdateCommandRunner(Action<string>? log = null) : IUpdateCommandRunner
{
    public async Task<UpdateCommandResult> RunAsync(UpdateCommand command)
    {
        using var cts = new CancellationTokenSource(command.Timeout);
        var output = new List<string>();
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data, output, log);
        process.ErrorDataReceived += (_, e) => Capture(e.Data, output, log);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new UpdateCommandResult(process.ExitCode, string.Join(Environment.NewLine, output));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new UpdateCommandResult(124, $"Command timed out after {command.Timeout.TotalSeconds:N0} seconds.");
        }
    }

    private static void Capture(string? line, List<string> output, Action<string>? log)
    {
        if (line == null)
            return;

        lock (output)
            output.Add(line);

        log?.Invoke(line);
    }
}
