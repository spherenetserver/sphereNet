using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Security;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static IPBlockList? _ipBlockList;
    private static ConnectionRateLimiter? _connRateLimiter;

    private static void InitializeAdminSurfaces()
    {
            // --- Security: shared IP block list & connection rate limiter ---
            _ipBlockList = new IPBlockList();
            _connRateLimiter = new ConnectionRateLimiter();
            _network.ConnectionAcceptFilter = ip =>
            {
                if ((SphereNet.Game.Diagnostics.BotEngine.BotModeActive || _trustLoopback)
                    && System.Net.IPAddress.IsLoopback(ip))
                    return false;
                string ipStr = ip.ToString();
                if (_ipBlockList.IsBlocked(ipStr))
                    return true;
                _connRateLimiter.RegisterAttempt(ipStr);
                return _connRateLimiter.ShouldThrottle(ipStr);
            };

            SphereNet.Game.Diagnostics.BotEngine.OnBotModeChanged += active =>
            {
                if (active)
                {
                    _connRateLimiter!.Reset("127.0.0.1");
                    _connRateLimiter.Reset("::1");
                    _network.ClientMaxIP = 0;
                }
                else
                {
                    _network.ClientMaxIP = _config.ClientMaxIP;
                }
            };

            // --- 9. Admin Console ---
            int telnetPort = _config.ServPort + 1;
            _telnet = new TelnetConsole(_world, _accounts, _config,
                () => _network.ActiveConnections,
                _loggerFactory.CreateLogger("Telnet"), _loggerFactory,
                _ipBlockList);
            _telnet.Start(telnetPort);
            _telnet.OnSaveRequested += RequestSaveOnMainLoop;
            _telnet.OnShutdownRequested += () => _running = false;
            _telnet.OnResyncRequested += PerformScriptResync;
            _telnet.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
            _telnet.OnDebugToggleRequested += ToggleDebugPackets;
            _telnet.OnScriptDebugToggleRequested += ToggleScriptDebug;

            // Console command processor (shares logic with telnet)
            _consoleProcessor = new AdminCommandProcessor(_world, _accounts, _config,
                () => _network.ActiveConnections, _loggerFactory, _ipBlockList);
            _consoleProcessor.OnSaveRequested += RequestSaveOnMainLoop;
            _consoleProcessor.OnShutdownRequested += () => _running = false;
            _consoleProcessor.OnResyncRequested += PerformScriptResync;
            _consoleProcessor.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
            _consoleProcessor.OnDebugToggleRequested += ToggleDebugPackets;
            _consoleProcessor.OnScriptDebugToggleRequested += ToggleScriptDebug;
            _consoleProcessor.OnBotRequested += (count, behavior) => HandleBotCommand(count, behavior, false);
            _consoleProcessor.OnStressRequested += (items, npcs, hostile) => _stressEngine?.QueueGenerate(items, npcs, hostile);

            // Audit logging for admin commands
            _telnet.Processor.OnCommandExecuted += (source, cmd) =>
                _log.LogWarning("[AUDIT] [{Source}] {Command}", source, cmd);
            _consoleProcessor.OnCommandExecuted += (source, cmd) =>
                _log.LogWarning("[AUDIT] [{Source}] {Command}", source, cmd);

            int webPort = _config.ServPort + 2;
            _webStatus = new WebStatusServer(_world, _accounts,
                () => _network.ActiveConnections,
                _loggerFactory.CreateLogger("WebStatus"),
                GetRuntimeMetrics);
            _webStatus.Start(webPort);

            // --- 10. Admin Panel (SphereNet.Panel) ---
            int panelPort = _config.AdminPanelPort > 0 ? _config.AdminPanelPort : _config.ServPort + 3;
            var panelCtx = new PanelContext
            {
                ServerName  = _config.ServName,
                StartTime   = _serverStartTime,
                AdminPassword = _config.AdminPassword,

                GetStats = () =>
                {
                    var (chars, items, sectors) = _world.GetStats();
                    var uptime = DateTime.UtcNow - _serverStartTime;
                    var runtime = GetTickTelemetrySnapshot();
                    var maps = _world.GetMapStats()
                        .Select(m => new MapStats(m.MapId, m.Chars, m.Items, m.Sectors, m.ActiveSectors, m.OnlinePlayers))
                        .ToList();
                    return new ServerStats(
                        _config.ServName,
                        uptime.ToString(@"d\.hh\:mm\:ss"),
                        (int)uptime.TotalSeconds,
                        _clients.Values.Count(c => c.IsPlaying),
                        chars,
                        items,
                        sectors,
                        _world.TickCount,
                        GC.GetTotalMemory(false) / 1024 / 1024,
                        _accounts.Count,
                        AvgTickMs: runtime.AvgMs,
                        MaxTickMs: runtime.MaxMs,
                        P50TickMs: runtime.P50Ms,
                        P95TickMs: runtime.P95Ms,
                        P99TickMs: runtime.P99Ms,
                        MulticoreEnabled: runtime.MulticoreEnabled,
                        Maps: maps);
                },

                GetOnlinePlayers = () => _clients.Values
                    .Where(c => c.IsPlaying && c.Character != null)
                    .Select(c => new PlayerInfo(
                        c.Character!.Name,
                        c.Account?.Name ?? "",
                        c.Character.Position.Map,
                        c.Character.Position.X,
                        c.Character.Position.Y,
                        c.NetState.RemoteEndPoint?.Address.ToString() ?? ""))
                    .ToList(),

                GetAllAccounts = () => _accounts.GetAllAccounts()
                    .Select(a => new AccountInfo(
                        a.Name, (int)a.PrivLevel, a.IsBanned,
                        a.LastIp, a.LastLogin, a.CreateDate, a.CharCount))
                    .ToList(),

                GetAccount = name =>
                {
                    var a = _accounts.FindAccount(name);
                    return a is null ? null
                        : new AccountInfo(a.Name, (int)a.PrivLevel, a.IsBanned,
                            a.LastIp, a.LastLogin, a.CreateDate, a.CharCount);
                },

                CreateAccount  = (name, pass) => _accounts.CreateAccount(name, pass) is not null,
                DeleteAccount  = name => _accounts.DeleteAccount(name),
                SetAccountBanned   = (name, banned) => _accounts.SetAccountBlocked(name, banned),
                SetAccountPassword = (name, pass)   => _accounts.SetAccountPassword(name, pass),
                SetAccountPrivLevel = (name, level) =>
                    _accounts.SetAccountPrivLevel(name, (SphereNet.Core.Enums.PrivLevel)level),

                // Reuse AdminCommandProcessor so panel and telnet share the same logic
                OnSave     = RequestSaveOnMainLoop,
                OnShutdown = () => _running = false,
                OnResync   = PerformScriptResync,
                OnGc       = () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); },
                OnRespawn  = () => _consoleProcessor?.ProcessCommand("RESPAWN",  _ => { }),
                OnRestock  = () => _consoleProcessor?.ProcessCommand("RESTOCK",  _ => { }),
                OnBroadcast = msg => _consoleProcessor?.ProcessCommand($"BROADCAST {msg}", _ => { }),

                ExecuteCommand = cmd =>
                {
                    var lines = new List<string>();
                    _consoleProcessor?.ProcessCommand(cmd, lines.Add);
                    return [.. lines];
                },
                AuditLog = msg => _log.LogWarning("Panel audit: {Message}", msg),

                // Paths
                IniPath     = _iniPath,
                ScriptsPath = _scriptDirs.Count > 0 ? _scriptDirs[0] : null,

                // Server lifecycle
                IsServerRunning = () => _running,
                OnRestart = () =>
                {
                    _log.LogInformation("Panel: restart requested — shutting down game engine…");
                    _running = false;
                },

                // Debug toggles — same logic as ToggleDebugPackets / ToggleScriptDebug
                GetDebugState = () => new SphereNet.Panel.DebugState(_config.DebugPackets, _config.ScriptDebug),
                SetPacketDebug = on =>
                {
                    _config.DebugPackets = on;
                    if (_network != null)
                    {
                        _network.DebugPackets = on;
                        foreach (var ns in _network.GetActiveStates())
                            ns.DebugPackets = on;
                    }
                    _logLevelSwitch.MinimumLevel = on
                        ? Serilog.Events.LogEventLevel.Debug
                        : Serilog.Events.LogEventLevel.Information;
                    _log.LogInformation("Panel: DebugPackets={Value}", on);
                },
                SetScriptDebug = on =>
                {
                    _config.ScriptDebug = on;
                    _triggerDispatcher.ScriptDebug = on;
                    if (_triggerRunner != null)
                        _triggerRunner.ScriptDebug = on;
                    if (on)
                        _logLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
                    _log.LogInformation("Panel: ScriptDebug={Value}", on);
                },
            };

            // In managed mode: start IPC server so Host can communicate with us.
            // Panel web server lives in SphereNet.Host, not here.
            if (_managed && !string.IsNullOrEmpty(_pipeName))
            {
                var ipc = new SphereNet.Server.Ipc.IpcServer(_pipeName);
                ipc.SetContext(panelCtx);
                _ = ipc.RunAsync(CancellationToken.None);
            }
    }

    // --- Console Commands ---

    /// <summary>
    /// Write a line to the console form (GUI) or stdout (headless).
    /// </summary>
    private static void ConsoleAppend(string text)
    {
        if (_log == null)
            Console.WriteLine(text);
    }

    private static void HandleConsoleCommand(string input)
    {
        if (_consoleProcessor == null) return;
        _consoleProcessor.ProcessCommand(input, ConsoleAppend);
    }

    private static void ToggleDebugPackets(Action<string> output)
    {
        if (_network == null || _config == null) return;
        bool newState = !_network.DebugPackets;
        _network.DebugPackets = newState;
        _config.DebugPackets = newState;

        // Update all existing connections
        foreach (var ns in _network.GetActiveStates())
            ns.DebugPackets = newState;

        // Switch Serilog minimum level
        _logLevelSwitch.MinimumLevel = newState
            ? Serilog.Events.LogEventLevel.Debug
            : Serilog.Events.LogEventLevel.Information;

        string state = newState ? "ON" : "OFF";
        output($"DebugPackets toggled: {state}");
        _log?.LogInformation("DebugPackets toggled: {State}", state);

#if WINFORMS
        _consoleForm?.SetDebugState(newState);
#endif
    }

    private static void ToggleScriptDebug(Action<string> output)
    {
        if (_config == null) return;
        bool newState = !_config.ScriptDebug;
        _config.ScriptDebug = newState;
        _triggerDispatcher.ScriptDebug = newState;
        if (_triggerRunner != null)
            _triggerRunner.ScriptDebug = newState;

        if (newState)
            _logLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

        string state = newState ? "ON" : "OFF";
        output($"ScriptDebug toggled: {state}");
        _log?.LogInformation("ScriptDebug toggled: {State}", state);
    }

    /// <summary>
    /// Resolve SERV.* property lookups for script engine.
    /// Maps to Source-X SER.* / SERV.* server properties.
    /// </summary>

    private static void ParseLogFileLevel(string raw,
        out Serilog.Events.LogEventLevel minLevel,
        out HashSet<Serilog.Events.LogEventLevel>? whitelist)
    {
        minLevel = Serilog.Events.LogEventLevel.Warning;
        whitelist = null;

        if (string.IsNullOrWhiteSpace(raw))
            return;

        // Accept '|', ',' and ';' as separators so the value reads
        // naturally regardless of which convention the operator picks.
        var parts = raw.Split(['|', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return;

        if (parts.Length == 1)
        {
            if (Enum.TryParse<Serilog.Events.LogEventLevel>(parts[0],
                ignoreCase: true, out var single))
            {
                minLevel = single;
            }
            return;
        }

        var set = new HashSet<Serilog.Events.LogEventLevel>();
        foreach (var token in parts)
        {
            if (Enum.TryParse<Serilog.Events.LogEventLevel>(token,
                ignoreCase: true, out var lvl))
            {
                set.Add(lvl);
            }
        }
        if (set.Count == 0)
            return;

        whitelist = set;
        // Don't let restrictedToMinimumLevel filter out the lowest
        // whitelisted entry before our ByIncludingOnly filter runs.
        var lowest = Serilog.Events.LogEventLevel.Fatal;
        foreach (var lvl in set)
        {
            if (lvl < lowest) lowest = lvl;
        }
        minLevel = lowest;
    }
}
