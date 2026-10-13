using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Objects.Characters;
using SphereNet.Server.Plugins;

namespace SphereNet.Server;

/// <summary>
/// The plugin bridge's server side: starting it from sphere.ini, the events the
/// engine publishes, and the commands a plugin may send. See docs/PLUGINS.md.
/// </summary>
public static partial class Program
{
    /// <summary>Source-X EF_Script_Profiler (CServerConfig.h:52, EXPERIMENTAL 0x400).</summary>
    private const int EfScriptProfiler = 0x0000400;

    private static PluginBridge? _pluginBridge;
    private static PluginRuntime? _pluginRuntime;

    /// <summary>Start the bridge when PLUGINBRIDGEPORT is set. Without a token it
    /// stays off: an open port that runs console commands needs a secret.</summary>
    private static void StartPluginBridge()
    {
        if (_config.PluginBridgePort <= 0)
            return;
        if (string.IsNullOrWhiteSpace(_config.PluginBridgeToken))
        {
            _log.LogWarning("PluginBridgePort is set but PluginBridgeToken is empty; the plugin bridge stays off");
            return;
        }
        if (!IPAddress.TryParse(_config.PluginBridgeAddress, out var address))
        {
            _log.LogWarning("PluginBridgeAddress '{Address}' is not an IP address; the plugin bridge stays off",
                _config.PluginBridgeAddress);
            return;
        }
        if (!IPAddress.IsLoopback(address))
            _log.LogWarning("The plugin bridge listens on {Address}, reachable from other machines; " +
                            "anyone with the token can run console commands", address);

        try
        {
            var bridge = new PluginBridge(address, _config.PluginBridgePort, _config.PluginBridgeToken,
                ExecutePluginCommand, _loggerFactory.CreateLogger("PluginBridge"));
            bridge.Start();
            _pluginBridge = bridge;
            _log.LogInformation("Plugin bridge listening on {Address}:{Port}", address, bridge.Port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Plugin bridge could not start on {Address}:{Port}", address, _config.PluginBridgePort);
        }
    }

    /// <summary>Load the in-process plugins listed under [PLUGINS]. Runs before the
    /// main loop, so their OnLoad sees server.started arrive afterwards.</summary>
    private static void LoadInProcessPlugins()
    {
        if (_config.Plugins.Count == 0)
            return;
        var runtime = new PluginRuntime(ExecutePluginCommand, _loggerFactory);
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var (name, path) in _config.Plugins)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            runtime.Load(name, ResolvePath(baseDir, path.Trim()));
        }
        _pluginRuntime = runtime;
    }

    private static void StopPluginBridge()
    {
        _pluginRuntime?.Dispose();
        _pluginRuntime = null;
        _pluginBridge?.Dispose();
        _pluginBridge = null;
    }

    /// <summary>Publish an event to the bridge clients and the in-process plugins. The
    /// payload is built only when one of them listens, so with none it costs two field
    /// reads.</summary>
    private static void PluginEvent(string name, Func<object?> data)
    {
        var bridge = _pluginBridge is { HasClients: true } b ? b : null;
        var runtime = _pluginRuntime is { HasSubscribers: true } r ? r : null;
        if (bridge == null && runtime == null)
            return;
        object? payload = data();
        bridge?.Publish(name, payload);
        runtime?.Publish(name, payload);
    }

    private static object CharPayload(Character ch) => new
    {
        name = ch.Name,
        serial = ch.Uid.Value,
        player = ch.IsPlayer,
        map = ch.Position.Map,
        x = ch.Position.X,
        y = ch.Position.Y,
    };

    /// <summary>Wire the engine hooks that turn into bridge events.</summary>
    private static void WirePluginEvents()
    {
        _deathEngine.OnDeath += (victim, killer) => PluginEvent("char.death", () => new
        {
            victim = CharPayload(victim),
            killer = killer == null ? null : CharPayload(killer),
        });

        if (_pluginRuntime is { } runtime)
        {
            _network.PluginPacketGate = runtime.WantsPacket;
            _network.PluginPacketFilter = (state, opcode, frame) =>
            {
                _clients.TryGetValue(state.Id, out var client);
                return runtime.AllowIncomingPacket(opcode, frame,
                    state.RemoteEndPoint?.Address.ToString() ?? "", client?.Account?.Name);
            };
            SphereNet.Game.Clients.GameClient.LoginCheck = (account, ip) =>
                runtime.HasLoginChecks ? runtime.CheckLogin(account, ip?.ToString() ?? "") : null;
        }

        SphereNet.Game.Clients.GameClient.LoginRejected = (account, ip, reason) =>
            PluginEvent("login.failed", () => new { account, ip = ip?.ToString(), reason });

        _network.OnConnectionRejected += (ip, reason) =>
            PluginEvent("connection.rejected", () => new { ip = ip?.ToString(), reason });
    }

    /// <summary>SERV.PLUGINEVENT name[,text]: a script sends its own event to the
    /// plugins as custom.&lt;name&gt;.</summary>
    private static string HandleServPluginEvent(string data)
    {
        int comma = data.IndexOf(',');
        string name = (comma >= 0 ? data[..comma] : data).Trim();
        string text = comma >= 0 ? data[(comma + 1)..].Trim() : "";
        if (name.Length == 0 || name.Length > 64 ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'))
            return "0";
        PluginEvent("custom." + name, () => new { text });
        return "1";
    }

    /// <summary>A command from a plugin, run on its reader thread. Everything that
    /// touches the world goes through the main loop.</summary>
    private static object? ExecutePluginCommand(string op, JsonElement req)
    {
        string Str(string key) =>
            req.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        switch (op)
        {
            case "ping":
                return "pong";

            case "status":
                return InvokePanelOnMainLoop(() => (object)new
                {
                    name = _config.ServName,
                    uptimeSeconds = (long)(DateTime.UtcNow - _serverStartTime).TotalSeconds,
                    players = _clients.Values.Count(c => c.IsPlaying),
                    lastSaveOk = _lastSaveOk,
                    saveCount = _saveCount,
                }, "plugin status");

            case "players":
                return InvokePanelOnMainLoop(() => (object)_clients.Values
                    .Where(c => c.IsPlaying && c.Character != null)
                    .Select(c => new
                    {
                        account = c.Account?.Name ?? "",
                        name = c.Character!.Name,
                        serial = c.Character.Uid.Value,
                        map = c.Character.Position.Map,
                        x = c.Character.Position.X,
                        y = c.Character.Position.Y,
                        ip = c.NetState.RemoteEndPoint?.Address.ToString() ?? "",
                    })
                    .ToList(), "plugin players");

            case "broadcast":
            {
                string text = Str("text");
                if (text.Length == 0 || text.Length > 500 || text.Any(char.IsControl))
                    throw new ArgumentException("text must be one line of 1-500 characters");
                PluginAudit(op, text);
                return InvokePanelOnMainLoop(() =>
                {
                    _consoleProcessor?.ProcessCommand($"BROADCAST {text}", _ => { });
                    return (object)true;
                }, "plugin broadcast");
            }

            case "kick":
            {
                uint serial = req.TryGetProperty("serial", out var s) && s.TryGetUInt32(out uint v) ? v : 0;
                PluginAudit(op, $"0{serial:X}");
                return InvokePanelOnMainLoop(() =>
                {
                    var client = FindPlayingClient(serial);
                    client?.NetState.MarkClosing();
                    return (object)(client != null);
                }, "plugin kick");
            }

            case "save":
                PluginAudit(op, "");
                RequestSaveOnMainLoop();
                return true;

            case "function":
            {
                string name = Str("name"), args = Str("args");
                if (!SphereNet.Panel.PanelHost.IsValidServerFunction(name, args, out string? error))
                    throw new ArgumentException(error);
                PluginAudit(op, $"{name} {args}");
                return InvokePanelOnMainLoop(() => (object)RunPanelServerFunction(name, args), "plugin function");
            }

            case "command":
            {
                string line = Str("line");
                if (line.Length == 0 || line.Length > 500 || line.Any(char.IsControl))
                    throw new ArgumentException("line must be one line of 1-500 characters");
                PluginAudit(op, line);
                return InvokePanelOnMainLoop(() =>
                {
                    var lines = new List<string>();
                    _consoleProcessor?.ProcessCommand(line, lines.Add);
                    return (object)lines;
                }, "plugin command");
            }

            default:
                throw new ArgumentException($"unknown op '{op}'");
        }
    }

    /// <summary>Serilog sink body for the "log" event. The bridge's own log lines are
    /// left out, so a warning about a plugin client is not sent back to the plugins.</summary>
    private static void PublishLogToPlugins(Serilog.Events.LogEvent e)
    {
        // Lines from the bridge, the runtime and the plugins themselves stay out:
        // a plugin warning about a log event must not make another log event.
        if (e.Properties.TryGetValue("SourceContext", out var ctx) &&
            ctx.ToString().StartsWith("\"Plugin", StringComparison.Ordinal))
            return;
        PluginEvent("log", () => new
        {
            level = e.Level.ToString(),
            message = e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture),
            exception = e.Exception?.Message,
        });
    }

    /// <summary>The panel's plugin page. Reads only thread-safe counters, so it does
    /// not go through the main loop.</summary>
    private static SphereNet.Panel.PluginOverview DescribePlugins()
    {
        var rows = new List<SphereNet.Panel.PluginInfo>();
        if (_pluginRuntime is { } runtime)
        {
            foreach (var p in runtime.Describe())
                rows.Add(new SphereNet.Panel.PluginInfo("dll", p.Name, p.Enabled, p.Type,
                    p.Subscriptions, p.PacketFilters, p.LoginChecks, p.Faults, p.SlowHooks,
                    0, p.QueuedEvents, null));
        }
        if (_pluginBridge is { } bridge)
        {
            foreach (var c in bridge.Describe())
                rows.Add(new SphereNet.Panel.PluginInfo("bridge", c.Remote, true, c.Subscriptions,
                    c.Subscriptions.Length == 0 ? 0 : c.Subscriptions.Split(',').Length, 0, 0, 0, 0,
                    c.DroppedEvents, c.QueuedLines, c.ConnectedUtc));
        }
        string endpoint = _pluginBridge is { } b ? $"{_config.PluginBridgeAddress}:{b.Port}" : "";
        return new SphereNet.Panel.PluginOverview(_pluginBridge != null, endpoint, rows);
    }

    private static void PluginAudit(string op, string detail) =>
        _log.LogWarning("Plugin audit: {Op} {Detail}", op, detail);
}
