using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SphereNet.Server.Plugins;

/// <summary>
/// The out-of-process plugin bridge: a TCP listener (loopback by default) speaking
/// one JSON object per line, so a plugin can be written in any language with a
/// socket and a JSON parser.
///
/// A client authenticates with its first line and names the events it wants:
/// <c>{"op":"auth","token":"...","subscribe":["player.*","save.completed"]}</c>.
/// After that the server pushes <c>{"event":"...","time":"...","data":{...}}</c> lines
/// and answers commands <c>{"id":1,"op":"players"}</c> with
/// <c>{"id":1,"ok":true,"result":...}</c>.
///
/// The game loop never waits on a plugin. <see cref="Publish"/> drops the event into
/// a bounded channel and returns; a background pump serializes it once and hands the
/// line to each subscribed client's own bounded queue. A client that does not read
/// loses events (counted), and one that stays behind is disconnected. With no client
/// connected, publishing costs one field read (<see cref="HasClients"/>).
/// </summary>
public sealed class PluginBridge : IDisposable
{
    /// <summary>Protocol version sent in the auth reply.</summary>
    public const int ProtocolVersion = 1;

    private const int MaxLineBytes = 64 * 1024;
    private const int ClientQueueSize = 1024;
    private const int MaxConsecutiveDrops = 1024;
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions s_json = PluginJson.Options;

    private readonly IPAddress _address;
    private readonly int _requestedPort;
    private readonly byte[] _token;
    private readonly Func<string, JsonElement, object?> _execute;
    private readonly ILogger _logger;
    private readonly int _maxClients;
    private readonly Channel<(string Name, object? Data, DateTime Time)> _events =
        Channel.CreateBounded<(string, object?, DateTime)>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly List<Client> _clients = [];
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _clientCount;
    private long _droppedEvents;
    private Task? _acceptLoop;
    private Task? _pump;

    /// <param name="execute">Runs a command (<c>op</c>, the whole request) and returns
    /// its result; called on a client's reader task, so it must marshal to the main
    /// loop itself. A thrown exception becomes an error reply.</param>
    public PluginBridge(IPAddress address, int port, string token,
        Func<string, JsonElement, object?> execute, ILogger logger, int maxClients = 8)
    {
        _address = address;
        _requestedPort = port;
        _token = Encoding.UTF8.GetBytes(token);
        _execute = execute;
        _logger = logger;
        _maxClients = Math.Max(1, maxClients);
    }

    /// <summary>The port actually listened on (differs from the requested one only
    /// when 0 asked the OS to choose, as tests do).</summary>
    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    /// <summary>Whether any authenticated client is connected.</summary>
    public bool HasClients => Volatile.Read(ref _clientCount) > 0;

    /// <summary>Events dropped because the bridge's own queue was full.</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public void Start()
    {
        _listener = new TcpListener(_address, _requestedPort);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>One row per connected client, for the panel.</summary>
    public IReadOnlyList<BridgeClientStatus> Describe()
    {
        lock (_clients)
            return _clients.Select(c => new BridgeClientStatus(
                c.Remote, c.ConnectedUtc, string.Join(",", c.Subscriptions), c.Dropped, c.Queued)).ToList();
    }

    /// <summary>Queue an event for every client subscribed to <paramref name="name"/>.
    /// Never blocks; safe from any thread.</summary>
    public void Publish(string name, object? data)
    {
        if (!HasClients)
            return;
        if (!_events.Writer.TryWrite((name, data, DateTime.UtcNow)))
            Interlocked.Increment(ref _droppedEvents);
    }

    /// <summary>Whether <paramref name="name"/> matches a subscription pattern:
    /// <c>*</c>, an exact name, or a prefix ending in <c>.*</c>.</summary>
    public static bool Matches(string pattern, string name) =>
        pattern == "*" ||
        string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase) ||
        (pattern.EndsWith(".*", StringComparison.Ordinal) &&
         name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase));

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Plugin bridge accept failed");
                continue;
            }
            _ = Task.Run(() => ServeAsync(tcp));
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        string remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        tcp.NoDelay = true;
        using var stream = tcp.GetStream();
        var reader = new LineReader(stream);
        Client? client = null;
        try
        {
            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            authCts.CancelAfter(AuthTimeout);
            string? first = await reader.ReadLineAsync(authCts.Token);
            if (first == null)
                return;

            if (!TryAuthenticate(first, out string[] subscriptions, out string? error))
            {
                await WriteDirectAsync(stream, new { ok = false, error });
                _logger.LogWarning("Plugin bridge: refused {Remote}: {Error}", remote, error);
                return;
            }

            lock (_clients)
            {
                if (_clients.Count >= _maxClients)
                {
                    error = "too many plugin clients";
                }
                else
                {
                    client = new Client(stream, subscriptions, remote);
                    _clients.Add(client);
                    Interlocked.Increment(ref _clientCount);
                }
            }
            if (client == null)
            {
                await WriteDirectAsync(stream, new { ok = false, error });
                return;
            }

            _logger.LogInformation("Plugin bridge: {Remote} connected, events {Events}",
                remote, string.Join(",", subscriptions));
            client.Enqueue(JsonSerializer.Serialize(
                new { ok = true, version = ProtocolVersion, subscribe = subscriptions }, s_json));
            var writer = client.Writer = Task.Run(() => client.WriteLoopAsync(_cts.Token));

            while (!_cts.IsCancellationRequested && !client.Closed)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line == null)
                    break;
                if (line.Length == 0)
                    continue;
                client.Enqueue(HandleRequest(line));
            }

            client.Close();
            await writer;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("Plugin bridge: dropped {Remote}: {Reason}", remote, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin bridge: client {Remote} failed", remote);
        }
        finally
        {
            if (client != null)
            {
                client.Close();
                lock (_clients)
                {
                    if (_clients.Remove(client))
                        Interlocked.Decrement(ref _clientCount);
                }
                _logger.LogInformation("Plugin bridge: {Remote} disconnected", remote);
            }
            tcp.Dispose();
        }
    }

    private bool TryAuthenticate(string line, out string[] subscriptions, out string? error)
    {
        subscriptions = [];
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("op", out var op) || op.GetString() != "auth")
            {
                error = "first message must be {\"op\":\"auth\",...}";
                return false;
            }
            string token = root.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token))
            {
                error = "bad token";
                return false;
            }
            if (root.TryGetProperty("subscribe", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                subscriptions = subs.EnumerateArray()
                    .Select(s => s.GetString()?.Trim() ?? "")
                    .Where(s => s.Length > 0)
                    .ToArray();
            }
            return true;
        }
        catch (JsonException)
        {
            error = "not JSON";
            return false;
        }
    }

    private string HandleRequest(string line)
    {
        JsonElement id = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl))
                id = idEl.Clone();
            string op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
            if (op.Length == 0)
                return Reply(id, ok: false, error: "missing op");
            object? result = _execute(op, root.Clone());
            return Reply(id, ok: true, result: result);
        }
        catch (JsonException)
        {
            return Reply(id, ok: false, error: "not JSON");
        }
        catch (Exception ex)
        {
            return Reply(id, ok: false, error: ex.Message);
        }
    }

    private static string Reply(JsonElement id, bool ok, object? result = null, string? error = null)
    {
        var reply = new Dictionary<string, object?>();
        if (id.ValueKind != JsonValueKind.Undefined)
            reply["id"] = id;
        reply["ok"] = ok;
        if (ok)
            reply["result"] = result;
        else
            reply["error"] = error;
        return JsonSerializer.Serialize(reply, s_json);
    }

    private static async Task WriteDirectAsync(Stream stream, object message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, s_json) + "\n");
        await stream.WriteAsync(bytes);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var (name, data, time) in _events.Reader.ReadAllAsync(_cts.Token))
            {
                Client[] targets;
                lock (_clients)
                    targets = _clients.Where(c => c.Wants(name)).ToArray();
                if (targets.Length == 0)
                    continue;

                string line;
                try
                {
                    line = JsonSerializer.Serialize(new { @event = name, time, data }, s_json);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Plugin bridge: event {Event} could not be serialized", name);
                    continue;
                }
                foreach (var client in targets)
                    client.Enqueue(line);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested)
            return;
        // Give queued events (server.stopping) a moment to reach the clients.
        _events.Writer.TryComplete();
        try { _pump?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Client[] clients;
        lock (_clients)
            clients = [.. _clients];
        foreach (var c in clients)
            c.Close();
        try { Task.WaitAll(clients.Select(c => c.Writer ?? Task.CompletedTask).ToArray(), TimeSpan.FromMilliseconds(500)); }
        catch { }
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }

    /// <summary>One authenticated connection: its subscriptions and its outgoing queue.</summary>
    private sealed class Client(Stream stream, string[] subscriptions, string remote)
    {
        public string Remote => remote;
        public string[] Subscriptions => subscriptions;
        public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
        private long _dropped;
        public long Dropped => Interlocked.Read(ref _dropped);
        public int Queued => _out.Reader.Count;

        private readonly Channel<string> _out = Channel.CreateBounded<string>(
            new BoundedChannelOptions(ClientQueueSize) { SingleReader = true });
        private int _consecutiveDrops;

        public volatile bool Closed;

        public Task? Writer;

        public bool Wants(string name) => subscriptions.Any(p => Matches(p, name));

        public void Enqueue(string line)
        {
            if (_out.Writer.TryWrite(line))
            {
                Volatile.Write(ref _consecutiveDrops, 0);
                return;
            }
            // Not reading: lose this line, and give up on a client that never catches up.
            Interlocked.Increment(ref _dropped);
            // Closing the stream also ends its reader, which may be waiting for input.
            if (Interlocked.Increment(ref _consecutiveDrops) >= MaxConsecutiveDrops)
            {
                Close();
                try { stream.Dispose(); } catch { }
            }
        }

        public void Close()
        {
            Closed = true;
            _out.Writer.TryComplete();
        }

        public async Task WriteLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (string line in _out.Reader.ReadAllAsync(ct))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { Closed = true; }
            catch (ObjectDisposedException) { Closed = true; }
        }
    }

    /// <summary>Reads '\n'-terminated UTF-8 lines with a length limit, so a client
    /// cannot make the server buffer an endless line.</summary>
    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start, _end;
        private readonly MemoryStream _line = new();

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                for (int i = _start; i < _end; i++)
                {
                    if (_buffer[i] != (byte)'\n')
                        continue;
                    _line.Write(_buffer, _start, i - _start);
                    _start = i + 1;
                    string text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                    _line.SetLength(0);
                    return text;
                }
                _line.Write(_buffer, _start, _end - _start);
                _start = _end = 0;
                if (_line.Length > MaxLineBytes)
                    throw new InvalidDataException($"line longer than {MaxLineBytes} bytes");
                int n = await stream.ReadAsync(_buffer, ct);
                if (n == 0)
                    return null;
                _end = n;
            }
        }
    }
}

/// <summary>A connected bridge client as the panel shows it.</summary>
public sealed record BridgeClientStatus(string Remote, DateTime ConnectedUtc, string Subscriptions,
    long DroppedEvents, int QueuedLines);
