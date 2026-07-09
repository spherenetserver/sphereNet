using System.IO.Pipes;
using System.Text.Json;
using SphereNet.Panel;

namespace SphereNet.Server.Ipc;

/// <summary>
/// Named-pipe IPC server. Requests are dispatched serially; delegates that
/// touch game state are responsible for marshalling work to the main loop.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly string _pipeName;
    private PanelContext? _ctx;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IpcServer(string pipeName) => _pipeName = pipeName;

    public void SetContext(PanelContext ctx) => _ctx = ctx;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        CancellationToken runCt = linkedCts.Token;

        _pipe = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Console.WriteLine($"[IPC] Waiting for host on pipe '{_pipeName}'...");
        await _pipe.WaitForConnectionAsync(runCt).ConfigureAwait(false);
        Console.WriteLine("[IPC] Host connected.");

        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_pipe, leaveOpen: true);

        Task statsTask = StatsLoopAsync(runCt);
        try
        {
            await ReadLoopAsync(runCt).ConfigureAwait(false);
        }
        finally
        {
            linkedCts.Cancel();
            try { await statsTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested) { }
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_ctx?.GetStats != null)
            {
                try
                {
                    var stats = _ctx.GetStats();
                    await WriteAsync(new { t = "stats", data = stats }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IPC] Stats snapshot failed: {ex.Message}");
                }
            }

            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                // Keep request order deterministic and prevent concurrent mutation
                // callbacks from entering the game server.
                await HandleAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Console.WriteLine($"[IPC] Host disconnected: {ex.Message}");
        }
    }

    private async Task HandleAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? messageType = root.GetStr("t");

            if (messageType == "cmd")
            {
                HandleLegacyCommand(root);
                return;
            }

            string? id = root.GetStr("id");
            string operation = root.TryGetProp("qry") ?? root.TryGetProp("mut") ?? "";
            object? data = null;
            bool ok = true;
            string? code = null;
            string? error = null;

            try { data = Dispatch(operation, root); }
            catch (TimeoutException ex) { ok = false; code = "timeout"; error = ex.Message; }
            catch (ArgumentException ex) { ok = false; code = "invalid_request"; error = ex.Message; }
            catch (Exception ex) { ok = false; code = "remote_error"; error = ex.Message; }

            if (!string.IsNullOrEmpty(id))
                await WriteAsync(new { t = "rsp", id, ok, code, err = error, data }, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[IPC] Bad message: {ex.Message}");
        }
    }

    private void HandleLegacyCommand(JsonElement root)
    {
        switch (root.GetStr("cmd"))
        {
            case "save": _ctx?.OnSave?.Invoke(); break;
            case "resync": _ctx?.OnResync?.Invoke(); break;
            case "shutdown": _ctx?.OnShutdown?.Invoke(); break;
            case "gc": _ctx?.OnGc?.Invoke(); break;
            case "respawn": _ctx?.OnRespawn?.Invoke(); break;
            case "restock": _ctx?.OnRestock?.Invoke(); break;
            case "broadcast": _ctx?.OnBroadcast?.Invoke(root.GetStr("msg") ?? ""); break;
        }
    }

    private object? Dispatch(string operation, JsonElement root) => operation switch
    {
        "stats" => _ctx?.GetStats?.Invoke(),
        "players" => (object?)(_ctx?.GetOnlinePlayers?.Invoke() ?? []),
        "accounts" => _ctx?.GetAllAccounts?.Invoke() ?? [],
        "account" => _ctx?.GetAccount?.Invoke(root.GetStr("name") ?? ""),
        "debug" => _ctx?.GetDebugState?.Invoke() ?? new DebugState(false, false),
        "exec" => _ctx?.ExecuteCommand?.Invoke(root.GetStr("raw") ?? "") ?? [],
        "gump" => _ctx?.GetGumpPng?.Invoke(root.GetRequiredInt("id")),
        "dialogs" => _ctx?.ListDialogNames?.Invoke() ?? [],
        "dialog_source" => _ctx?.GetDialogSource?.Invoke(root.GetStr("name") ?? ""),

        "account_create" =>
            _ctx?.CreateAccount?.Invoke(root.GetRequiredStr("name"), root.GetRequiredStr("pass")) ?? false,
        "account_delete" =>
            _ctx?.DeleteAccount?.Invoke(root.GetRequiredStr("name")) ?? false,
        "account_ban" =>
            _ctx?.SetAccountBanned?.Invoke(root.GetRequiredStr("name"), root.GetRequiredBool("banned")) ?? false,
        "account_set_pass" =>
            _ctx?.SetAccountPassword?.Invoke(root.GetRequiredStr("name"), root.GetRequiredStr("pass")) ?? false,
        "account_set_plevel" =>
            _ctx?.SetAccountPrivLevel?.Invoke(root.GetRequiredStr("name"), root.GetRequiredInt("level")) ?? false,
        "set_packet_debug" => _ctx?.SetPacketDebug?.Invoke(root.GetRequiredBool("on")) ?? false,
        "set_script_debug" => _ctx?.SetScriptDebug?.Invoke(root.GetRequiredBool("on")) ?? false,

        "server_save" => _ctx?.OnSave?.Invoke() ?? false,
        "server_shutdown" => _ctx?.OnShutdown?.Invoke() ?? false,
        "server_resync" => _ctx?.OnResync?.Invoke() ?? false,
        "server_gc" => _ctx?.OnGc?.Invoke() ?? false,
        "server_respawn" => _ctx?.OnRespawn?.Invoke() ?? false,
        "server_restock" => _ctx?.OnRestock?.Invoke() ?? false,
        "server_broadcast" => _ctx?.OnBroadcast?.Invoke(root.GetStr("msg") ?? "") ?? false,
        "audit" => InvokeVoid(() => _ctx?.AuditLog?.Invoke(root.GetStr("msg") ?? "")),

        _ => throw new ArgumentException($"Unknown IPC operation '{operation}'")
    };

    private static bool InvokeVoid(Action action)
    {
        action();
        return true;
    }

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var writer = _writer ?? throw new IOException("IPC writer is unavailable");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json = JsonSerializer.Serialize(message, JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writeLock.Dispose();
        _disposeCts.Dispose();
    }
}

file static class JsonEx
{
    public static string? GetStr(this JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string GetRequiredStr(this JsonElement element, string key) =>
        element.GetStr(key) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{key}' is required");

    public static bool GetRequiredBool(this JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"'{key}' must be a boolean");

    public static int GetRequiredInt(this JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.TryGetInt32(out int result)
            ? result
            : throw new ArgumentException($"'{key}' must be an integer");

    public static string? TryGetProp(this JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
