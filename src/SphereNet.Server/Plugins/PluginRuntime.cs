using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SphereNet.Plugins;

namespace SphereNet.Server.Plugins;

/// <summary>
/// In-process .NET plugins: DLLs listed under [PLUGINS] in sphere.ini, each loaded into
/// its own <see cref="AssemblyLoadContext"/> so its dependencies come from its own folder
/// and cannot clash with the server's. Only the contract assembly
/// (SphereNet.Plugins.Abstractions) and the logging abstractions are shared.
///
/// A plugin hears the same events with the same data as a bridge client, delivered on
/// the plugin's own thread from a bounded queue. The only plugin code the game loop runs
/// is the synchronous hooks a plugin asks for - incoming packet filters and login checks
/// - and each call is timed. A plugin that keeps throwing, or whose hook stalls the loop,
/// is switched off.
/// </summary>
public sealed class PluginRuntime : IDisposable
{
    private const int QueueSize = 4096;
    private const int MaxFaults = 10;
    /// <summary>A hook slower than this is reported (at most once a minute per plugin).</summary>
    private const double SlowHookMs = 5;
    /// <summary>A hook slower than this counts as a failure.</summary>
    private const double StalledHookMs = 100;

    private readonly Func<string, JsonElement, object?> _execute;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<LoadedPlugin> _plugins = [];
    private int _subscriptionCount;

    private sealed record PacketFilterEntry(LoadedPlugin Plugin, bool[]? Opcodes, Func<IncomingPacket, bool> Filter);
    private sealed record LoginCheckEntry(LoadedPlugin Plugin, Func<LoginAttempt, string?> Check);

    // Copy-on-write snapshots: the game loop reads them without a lock.
    private readonly object _hookLock = new();
    private PacketFilterEntry[] _packetFilters = [];
    private LoginCheckEntry[] _loginChecks = [];
    private bool[] _packetGate = new bool[256];

    public PluginRuntime(Func<string, JsonElement, object?> execute, ILoggerFactory loggerFactory)
    {
        _execute = execute;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("PluginRuntime");
    }

    /// <summary>Whether any loaded plugin has an event subscription.</summary>
    public bool HasSubscribers => Volatile.Read(ref _subscriptionCount) > 0;

    /// <summary>Whether a plugin filters incoming packets with this opcode.</summary>
    public bool WantsPacket(byte opcode) => Volatile.Read(ref _packetGate)[opcode];

    /// <summary>Whether any plugin checks logins.</summary>
    public bool HasLoginChecks => Volatile.Read(ref _loginChecks).Length > 0;

    /// <summary>Run the incoming packet filters for this opcode. False = drop the packet.
    /// Game loop only.</summary>
    public bool AllowIncomingPacket(byte opcode, ReadOnlySpan<byte> frame, string ip, string? account)
    {
        IncomingPacket? packet = null;
        foreach (var f in Volatile.Read(ref _packetFilters))
        {
            if (f.Plugin.Disabled || (f.Opcodes != null && !f.Opcodes[opcode]))
                continue;
            packet ??= new IncomingPacket(opcode, frame.ToArray(), ip, account);
            var p = packet;
            if (!RunHook(f.Plugin, "packet filter", () => f.Filter(p), fallback: true))
                return false;
        }
        return true;
    }

    /// <summary>Run the login checks; the first reason returned refuses the login.
    /// Null = allowed. Game loop only.</summary>
    public string? CheckLogin(string account, string ip)
    {
        var attempt = new LoginAttempt(account, ip);
        foreach (var c in Volatile.Read(ref _loginChecks))
        {
            if (c.Plugin.Disabled)
                continue;
            string? reason = RunHook(c.Plugin, "login check", () => c.Check(attempt), fallback: null);
            if (!string.IsNullOrWhiteSpace(reason))
                return $"{c.Plugin.Name}: {reason}";
        }
        return null;
    }

    /// <summary>Call one synchronous hook, timing it. A throw or a stall counts as a
    /// failure of the plugin and the fallback answer is used.</summary>
    private static T RunHook<T>(LoadedPlugin plugin, string what, Func<T> hook, T fallback)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return hook();
        }
        catch (Exception ex)
        {
            plugin.Logger.LogError(ex, "Plugin {Name}: {Hook} failed", plugin.Name, what);
            plugin.RecordFault();
            return fallback;
        }
        finally
        {
            plugin.RecordHookTime(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, what);
        }
    }

    private IDisposable AddPacketFilter(LoadedPlugin plugin, Func<IncomingPacket, bool> filter, byte[] opcodes)
    {
        ArgumentNullException.ThrowIfNull(filter);
        bool[]? mask = null;
        if (opcodes is { Length: > 0 })
        {
            mask = new bool[256];
            foreach (byte op in opcodes)
                mask[op] = true;
        }
        var entry = new PacketFilterEntry(plugin, mask, filter);
        lock (_hookLock)
        {
            _packetFilters = [.. _packetFilters, entry];
            RebuildGateLocked();
        }
        return new Unsubscriber(() =>
        {
            lock (_hookLock)
            {
                _packetFilters = _packetFilters.Where(e => e != entry).ToArray();
                RebuildGateLocked();
            }
        });
    }

    private IDisposable AddLoginCheck(LoadedPlugin plugin, Func<LoginAttempt, string?> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var entry = new LoginCheckEntry(plugin, check);
        lock (_hookLock)
            _loginChecks = [.. _loginChecks, entry];
        return new Unsubscriber(() =>
        {
            lock (_hookLock)
                _loginChecks = _loginChecks.Where(e => e != entry).ToArray();
        });
    }

    private void RemoveHooks(LoadedPlugin plugin)
    {
        lock (_hookLock)
        {
            _packetFilters = _packetFilters.Where(e => e.Plugin != plugin).ToArray();
            _loginChecks = _loginChecks.Where(e => e.Plugin != plugin).ToArray();
            RebuildGateLocked();
        }
    }

    private void RebuildGateLocked()
    {
        var gate = new bool[256];
        foreach (var f in _packetFilters)
        {
            if (f.Opcodes == null)
                Array.Fill(gate, true);
            else
                for (int i = 0; i < 256; i++)
                    gate[i] |= f.Opcodes[i];
        }
        Volatile.Write(ref _packetGate, gate);
    }

    /// <summary>One row per loaded plugin, for the panel.</summary>
    public IReadOnlyList<PluginStatus> Describe()
    {
        LoadedPlugin[] plugins;
        lock (_plugins)
            plugins = [.. _plugins];
        var filters = Volatile.Read(ref _packetFilters);
        var checks = Volatile.Read(ref _loginChecks);
        return plugins.Select(p => new PluginStatus(
            p.Name,
            p.Instance?.GetType().FullName ?? "",
            !p.Disabled,
            p.Faults,
            p.SlowHooks,
            p.SubscriptionCount,
            filters.Count(f => f.Plugin == p),
            checks.Count(c => c.Plugin == p),
            p.Queued)).ToList();
    }

    /// <summary>Names of the plugins that loaded and are still enabled.</summary>
    public IReadOnlyList<string> ActivePlugins
    {
        get { lock (_plugins) return _plugins.Where(p => !p.Disabled).Select(p => p.Name).ToList(); }
    }

    /// <summary>Load every ISpherePlugin type in the DLL at <paramref name="path"/> and call
    /// OnLoad. Returns how many loaded; failures are logged, never thrown.</summary>
    public int Load(string name, string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Plugin {Name}: {Path} not found", name, path);
            return 0;
        }

        Assembly assembly;
        try
        {
            var context = new PluginLoadContext(Path.GetFullPath(path));
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin {Name}: {Path} could not be loaded", name, path);
            return 0;
        }

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        int loaded = 0;
        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } &&
                                              typeof(ISpherePlugin).IsAssignableFrom(t)))
        {
            string pluginName = loaded == 0 ? name : $"{name}.{type.Name}";
            ISpherePlugin instance;
            try { instance = (ISpherePlugin)Activator.CreateInstance(type)!; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Name}: {Type} could not be created", pluginName, type.FullName);
                continue;
            }
            if (Add(pluginName, instance))
                loaded++;
        }
        if (loaded == 0 && types.Length > 0 && !types.Any(t => typeof(ISpherePlugin).IsAssignableFrom(t)))
            _logger.LogWarning("Plugin {Name}: {Path} has no public ISpherePlugin class", name, path);
        return loaded;
    }

    /// <summary>Start a plugin instance: give it its host and call OnLoad. False (and
    /// logged) when OnLoad throws; the plugin then stays off.</summary>
    public bool Add(string name, ISpherePlugin instance)
    {
        var plugin = new LoadedPlugin(this, name, _loggerFactory.CreateLogger($"Plugin.{name}"))
        {
            Instance = instance,
        };
        lock (_plugins)
            _plugins.Add(plugin);
        plugin.Start();
        try
        {
            instance.OnLoad(plugin);
            _logger.LogInformation("Plugin {Name} loaded ({Type})", name, instance.GetType().FullName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin {Name}: OnLoad failed", name);
            plugin.Disable();
            return false;
        }
    }

    /// <summary>Hand an event to every plugin subscribed to it. Never blocks.</summary>
    public void Publish(string name, object? data)
    {
        if (!HasSubscribers)
            return;
        LoadedPlugin[] targets;
        lock (_plugins)
            targets = _plugins.Where(p => !p.Disabled && p.Wants(name)).ToArray();
        if (targets.Length == 0)
            return;
        JsonElement element;
        try { element = JsonSerializer.SerializeToElement(data, PluginJson.Options); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Event {Event} could not be serialized", name);
            return;
        }
        var e = new PluginEvent(name, DateTime.UtcNow, element);
        foreach (var plugin in targets)
            plugin.Enqueue(e);
    }

    public void Dispose()
    {
        LoadedPlugin[] plugins;
        lock (_plugins)
            plugins = [.. _plugins];
        foreach (var p in plugins)
            p.Unload();
    }

    private void AdjustSubscriptions(int delta) => Interlocked.Add(ref _subscriptionCount, delta);

    /// <summary>One plugin instance: its subscriptions, its event queue and the host
    /// interface it was given.</summary>
    private sealed class LoadedPlugin(PluginRuntime runtime, string name, ILogger logger) : IPluginHost
    {
        private readonly List<(string Pattern, Action<PluginEvent> Handler)> _subs = [];
        private readonly Channel<PluginEvent> _queue = Channel.CreateBounded<PluginEvent>(
            new BoundedChannelOptions(QueueSize) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        private int _faults;
        private Task? _dispatcher;

        public ISpherePlugin? Instance;
        public volatile bool Disabled;
        public string Name => name;
        private int _slowHooks;
        private long _lastSlowReportTicks = long.MinValue / 2;

        public int Faults => Volatile.Read(ref _faults);
        public int SlowHooks => Volatile.Read(ref _slowHooks);
        public int Queued => _queue.Reader.Count;
        public int SubscriptionCount { get { lock (_subs) return _subs.Count; } }

        public IDisposable AddIncomingPacketFilter(Func<IncomingPacket, bool> filter, params byte[] opcodes) =>
            runtime.AddPacketFilter(this, filter, opcodes);

        public IDisposable AddLoginCheck(Func<LoginAttempt, string?> check) =>
            runtime.AddLoginCheck(this, check);

        /// <summary>One failure: a handler or hook threw, or a hook stalled the loop.</summary>
        public void RecordFault()
        {
            int faults = Interlocked.Increment(ref _faults);
            if (faults >= MaxFaults && !Disabled)
            {
                logger.LogError("Plugin {Name} failed {Count} times and is switched off", name, faults);
                Disable();
            }
        }

        public void RecordHookTime(double ms, string what)
        {
            if (ms >= StalledHookMs)
            {
                logger.LogWarning("Plugin {Name}: {Hook} held the game loop for {Ms:F0} ms", name, what, ms);
                RecordFault();
            }
            else if (ms >= SlowHookMs)
            {
                Interlocked.Increment(ref _slowHooks);
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _lastSlowReportTicks);
                if (now - last >= 60_000 && Interlocked.CompareExchange(ref _lastSlowReportTicks, now, last) == last)
                    logger.LogWarning("Plugin {Name}: {Hook} took {Ms:F1} ms on the game loop", name, what, ms);
            }
        }

        public string PluginName => name;
        public ILogger Logger => logger;

        public void Start() => _dispatcher = Task.Run(DispatchAsync);

        public bool Wants(string eventName)
        {
            lock (_subs)
                return _subs.Any(s => PluginBridge.Matches(s.Pattern, eventName));
        }

        public void Enqueue(PluginEvent e) => _queue.Writer.TryWrite(e);

        public IDisposable Subscribe(string pattern, Action<PluginEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var entry = (pattern.Trim(), handler);
            lock (_subs)
                _subs.Add(entry);
            runtime.AdjustSubscriptions(1);
            return new Unsubscriber(() =>
            {
                bool removed;
                lock (_subs)
                    removed = _subs.Remove(entry);
                if (removed)
                    runtime.AdjustSubscriptions(-1);
            });
        }

        public Task<JsonElement> ExecuteAsync(string op, object? fields = null,
            CancellationToken cancellationToken = default)
        {
            if (Disabled)
                return Task.FromException<JsonElement>(new InvalidOperationException("plugin is disabled"));
            // The command may wait for the main loop; never block the caller's thread on it.
            return Task.Run(() =>
            {
                var request = fields == null
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.SerializeToElement(fields, PluginJson.Options)
                        .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                request["op"] = JsonSerializer.SerializeToElement(op);
                var element = JsonSerializer.SerializeToElement(request);
                object? result = runtime._execute(op, element);
                return JsonSerializer.SerializeToElement(result, PluginJson.Options);
            }, cancellationToken);
        }

        private async Task DispatchAsync()
        {
            await foreach (var e in _queue.Reader.ReadAllAsync())
            {
                if (Disabled)
                    continue;
                Action<PluginEvent>[] handlers;
                lock (_subs)
                    handlers = _subs.Where(s => PluginBridge.Matches(s.Pattern, e.Name)).Select(s => s.Handler).ToArray();
                foreach (var handler in handlers)
                {
                    try { handler(e); }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Plugin {Name} failed on {Event}", name, e.Name);
                        RecordFault();
                        if (Disabled)
                            break;
                    }
                }
            }
        }

        public void Disable()
        {
            if (Disabled)
                return;
            Disabled = true;
            int count;
            lock (_subs)
            {
                count = _subs.Count;
                _subs.Clear();
            }
            runtime.AdjustSubscriptions(-count);
            runtime.RemoveHooks(this);
        }

        public void Unload()
        {
            if (!Disabled && Instance != null)
            {
                try { Instance.OnUnload(); }
                catch (Exception ex) { logger.LogWarning(ex, "Plugin {Name} failed to unload", name); }
            }
            Disable();
            _queue.Writer.TryComplete();
            try { _dispatcher?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    /// <summary>Resolves a plugin's own dependencies from its folder (its .deps.json),
    /// and leaves the contract and logging abstractions to the server so the plugin's
    /// ISpherePlugin is the server's ISpherePlugin.</summary>
    private sealed class PluginLoadContext(string pluginPath)
        : AssemblyLoadContext($"Plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? "";
            if (name == "SphereNet.Plugins.Abstractions" ||
                name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.Ordinal))
                return null;
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}

/// <summary>A loaded plugin as the panel shows it.</summary>
public sealed record PluginStatus(string Name, string Type, bool Enabled, int Faults, int SlowHooks,
    int Subscriptions, int PacketFilters, int LoginChecks, int QueuedEvents);

/// <summary>The JSON shape shared by the bridge and in-process plugins.</summary>
public static class PluginJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
