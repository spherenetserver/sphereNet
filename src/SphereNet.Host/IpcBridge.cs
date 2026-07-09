using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using SphereNet.Panel;

namespace SphereNet.Host;

/// <summary>
/// Named-pipe client that talks to SphereNet.Server's IpcServer.
/// Receives stats pushes; sends commands and queries.
/// </summary>
public sealed class IpcBridge : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource _cts = new();
    private Task? _readLoopTask;
    private readonly SemaphoreSlim _wl = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ServerStats? LastStats { get; private set; }
    public DebugState? LastDebugState { get; private set; }
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(string pipeName)
    {
        Task? previousReadLoop = _readLoopTask;
        Disconnect();
        if (previousReadLoop != null)
        {
            try { await previousReadLoop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _readLoopTask = null;
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000, _cts.Token).ConfigureAwait(false);
            _pipe = pipe;
            _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            _reader = new StreamReader(pipe, leaveOpen: true);
            IsConnected = true;
            _readLoopTask = ReadLoopAsync(_cts.Token);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        IsConnected = false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        FailPending(new PanelBackendUnavailableException("Game server IPC connection is unavailable"));
        _pending.Clear();
        try { _writer?.Dispose(); } catch (ObjectDisposedException) { }
        try { _reader?.Dispose(); } catch (ObjectDisposedException) { }
        try { _pipe?.Dispose(); } catch (ObjectDisposedException) { }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    // ── Fire-and-forget commands ────────────────────────────────────────────

    public Task SendCommandAsync(string cmd, CancellationToken ct = default)
        => WriteAsync(new { t = "cmd", cmd }, ct);

    // ── Request/response ────────────────────────────────────────────────────

    public Task<T?> QueryAsync<T>(string qry, object? args = null) where T : class
        => RequestAsync<T>("qry", qry, args);

    public async Task<bool> MutateAsync(string mut, object? args = null)
        => await RequestAsync<bool>("mut", mut, args).ConfigureAwait(false);

    private async Task<T?> RequestAsync<T>(string kind, string op, object? args)
    {
        if (!IsConnected)
            throw new PanelBackendUnavailableException("Game server is not connected");

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException("Could not allocate an IPC request id");

        var msg = BuildMsg(kind, id, op, args);

        try
        {
            try
            {
                await WriteAsync(msg, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (_cts.IsCancellationRequested)
            {
                throw new PanelBackendUnavailableException("Game server IPC connection was closed", ex);
            }

            JsonElement response;
            try
            {
                response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new PanelBackendTimeoutException($"IPC operation '{op}' timed out", ex);
            }

            bool ok = response.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                string? code = response.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
                string message = response.TryGetProperty("err", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString() ?? "Backend operation failed"
                    : "Backend operation failed";

                if (string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase))
                    throw new PanelBackendTimeoutException(message);
                throw new PanelBackendOperationException(message, code);
            }

            if (!response.TryGetProperty("data", out var data) ||
                data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return default;

            return data.Deserialize<T>(_json);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private static Dictionary<string, object?> BuildMsg(string kind, string id, string op, object? args)
    {
        var d = new Dictionary<string, object?>
        {
            ["t"]    = kind,
            ["id"]   = id,
            [kind]   = op,
        };
        if (args != null)
            foreach (var p in JsonSerializer.SerializeToElement(args, _json).EnumerateObject())
                d[p.Name] = p.Value;
        return d;
    }

    // ── Read loop ───────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        PanelBackendUnavailableException? failure = null;
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) break;
                ProcessMessage(line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            failure = new PanelBackendUnavailableException("Game server IPC connection was lost", ex);
        }
        finally
        {
            IsConnected = false;
            FailPending(failure ?? new PanelBackendUnavailableException("Game server IPC connection was closed"));
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "stats":
                    LastStats = root.GetProperty("data").Deserialize<ServerStats>(_json);
                    break;
                case "rsp":
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id != null && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(root.Clone());
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    // ── Write ───────────────────────────────────────────────────────────────

    private async Task WriteAsync(object msg, CancellationToken ct)
    {
        var writer = _writer;
        if (!IsConnected || writer == null)
            throw new PanelBackendUnavailableException("Game server is not connected");

        await _wl.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json = JsonSerializer.Serialize(msg, _json);
            await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            IsConnected = false;
            throw new PanelBackendUnavailableException("Could not write to the game server IPC connection", ex);
        }
        finally { _wl.Release(); }
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending.Values)
            pending.TrySetException(error);
    }

    public void Dispose()
    {
        Disconnect();
        try { _readLoopTask?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cts.Dispose();
        _wl.Dispose();
    }
}
