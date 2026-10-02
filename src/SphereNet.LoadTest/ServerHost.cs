using System.Collections.Concurrent;
using System.Diagnostics;

namespace SphereNet.LoadTest;

/// <summary>
/// The server under test as a child process: its own runtime, thread pool and GC, so
/// the bots' work never shows up in its numbers. Commands go in on the headless
/// console (stdin); answers are read back from the log on stdout.
/// </summary>
internal sealed class ServerHost : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly List<(Func<string, bool> Match, TaskCompletionSource<string> Done)> _waiters = [];
    private readonly object _lock = new();
    private readonly StreamWriter? _log;
    private int _warnings;
    private int _errors;

    public int Warnings => _warnings;
    public int Errors => _errors;
    public List<string> ErrorLines { get; } = [];

    public ServerHost(string serverDll, string workingDir, IEnumerable<string> args, string logPath)
    {
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(serverDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Serilog's console theme would otherwise wrap lines in ANSI escapes.
        psi.Environment["NO_COLOR"] = "1";
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLine("[stderr] " + e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public bool HasExited => _process.HasExited;

    public void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary>Wait for a log line matching <paramref name="match"/> written AFTER
    /// this call. Throws on timeout or if the server exits first.</summary>
    public async Task<string> WaitForLineAsync(Func<string, bool> match, TimeSpan timeout, string what)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _waiters.Add((match, tcs));
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(250));
            if (done == tcs.Task) return tcs.Task.Result;
            if (_process.HasExited)
                throw new InvalidOperationException($"server exited (code {_process.ExitCode}) while waiting for {what}.\n{Tail()}");
        }
        lock (_lock) _waiters.RemoveAll(w => w.Done == tcs);
        throw new TimeoutException($"timed out after {timeout.TotalSeconds:F0}s waiting for {what}.\n{Tail()}");
    }

    public string Tail(int lines = 40) => string.Join(Environment.NewLine, _recent.TakeLast(lines));

    public async Task StopAsync()
    {
        if (_process.HasExited) return;
        try { Send("shutdown"); } catch { }
        var exited = _process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(30_000)) != exited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private void OnLine(string line)
    {
        _log?.WriteLine(line);
        _recent.Enqueue(line);
        while (_recent.Count > 400 && _recent.TryDequeue(out _)) { }
        if (line.Contains(" WRN] ", StringComparison.Ordinal)) Interlocked.Increment(ref _warnings);
        if (line.Contains(" ERR] ", StringComparison.Ordinal) || line.Contains(" FTL] ", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _errors);
            lock (_lock)
            {
                if (ErrorLines.Count < 20) ErrorLines.Add(line);
            }
        }
        lock (_lock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Match(line))
                {
                    _waiters[i].Done.TrySetResult(line);
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
        _log?.Dispose();
    }
}
