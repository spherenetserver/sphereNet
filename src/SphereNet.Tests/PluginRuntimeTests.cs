using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Plugins;
using SphereNet.Server.Plugins;

namespace SphereNet.Tests;

/// <summary>
/// In-process plugins: a DLL listed under [PLUGINS] loads into its own context and gets
/// the bridge's events and commands; event handlers run off the caller's thread; a
/// plugin that keeps throwing is switched off. See docs/PLUGINS.md.
/// </summary>
public sealed class PluginRuntimeTests
{
    private sealed class Recorder
    {
        public readonly ConcurrentQueue<(string Op, JsonElement Request)> Calls = new();

        public object? Execute(string op, JsonElement request)
        {
            Calls.Enqueue((op, request));
            return op == "ping" ? "pong" : true;
        }

        public async Task<(string Op, JsonElement Request)> NextAsync()
        {
            for (int i = 0; i < 300; i++)
            {
                if (Calls.TryDequeue(out var call))
                    return call;
                await Task.Delay(10);
            }
            throw new TimeoutException("no command arrived");
        }
    }

    [Fact]
    public async Task TheSamplePluginLoadsFromItsDllAndGreetsAPlayer()
    {
        var recorder = new Recorder();
        using var runtime = new PluginRuntime(recorder.Execute, NullLoggerFactory.Instance);
        string dll = Path.Combine(AppContext.BaseDirectory, "SphereNet.Plugins.Sample.dll");

        Assert.Equal(1, runtime.Load("Welcome", dll));
        Assert.True(runtime.HasSubscribers);

        runtime.Publish("player.login", new { account = "a", name = "Alice", serial = 1u });
        var (op, request) = await recorder.NextAsync();

        Assert.Equal("broadcast", op);
        Assert.Equal("Welcome, Alice!", request.GetProperty("text").GetString());
    }

    [Fact]
    public void AMissingDllLoadsNothing()
    {
        using var runtime = new PluginRuntime(new Recorder().Execute, NullLoggerFactory.Instance);

        Assert.Equal(0, runtime.Load("Ghost", Path.Combine(AppContext.BaseDirectory, "no-such-plugin.dll")));
        Assert.False(runtime.HasSubscribers);
    }

    private sealed class CountingPlugin : ISpherePlugin
    {
        public int Seen;
        public int ThreadId;
        public IDisposable? Subscription;
        public void OnLoad(IPluginHost host) =>
            Subscription = host.Subscribe("save.*", _ =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref Seen);
            });
    }

    [Fact]
    public async Task EventsAreDeliveredOffTheCallersThreadAndStopOnDispose()
    {
        using var runtime = new PluginRuntime(new Recorder().Execute, NullLoggerFactory.Instance);
        var plugin = new CountingPlugin();
        Assert.True(runtime.Add("Counter", plugin));

        runtime.Publish("save.completed", new { ok = true });
        runtime.Publish("player.login", new { name = "x" });
        for (int i = 0; i < 300 && Volatile.Read(ref plugin.Seen) == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, plugin.Seen);
        Assert.NotEqual(Environment.CurrentManagedThreadId, plugin.ThreadId);

        plugin.Subscription!.Dispose();
        Assert.False(runtime.HasSubscribers);
    }

    private sealed class ThrowingPlugin : ISpherePlugin
    {
        public int Calls;
        public void OnLoad(IPluginHost host) =>
            host.Subscribe("*", _ =>
            {
                Interlocked.Increment(ref Calls);
                throw new InvalidOperationException("boom");
            });
    }

    [Fact]
    public async Task APluginThatKeepsThrowingIsSwitchedOff()
    {
        using var runtime = new PluginRuntime(new Recorder().Execute, NullLoggerFactory.Instance);
        var plugin = new ThrowingPlugin();
        runtime.Add("Bad", plugin);

        for (int i = 0; i < 15; i++)
            runtime.Publish("tick", null);
        for (int i = 0; i < 300 && runtime.ActivePlugins.Count > 0; i++)
            await Task.Delay(10);

        Assert.Empty(runtime.ActivePlugins);
        Assert.Equal(10, plugin.Calls);
        Assert.False(runtime.HasSubscribers);
    }

    private sealed class FailingLoad : ISpherePlugin
    {
        public void OnLoad(IPluginHost host) => throw new InvalidOperationException("no");
    }

    [Fact]
    public void APluginWhoseOnLoadThrowsStaysOff()
    {
        using var runtime = new PluginRuntime(new Recorder().Execute, NullLoggerFactory.Instance);

        Assert.False(runtime.Add("Broken", new FailingLoad()));
        Assert.Empty(runtime.ActivePlugins);
    }

    private sealed class Pinger : ISpherePlugin
    {
        public IPluginHost Host = null!;
        public void OnLoad(IPluginHost host) => Host = host;
    }

    [Fact]
    public async Task ACommandReturnsItsResult()
    {
        var recorder = new Recorder();
        using var runtime = new PluginRuntime(recorder.Execute, NullLoggerFactory.Instance);
        var plugin = new Pinger();
        runtime.Add("Pinger", plugin);

        var result = await plugin.Host.ExecuteAsync("ping");

        Assert.Equal("pong", result.GetString());
        Assert.Equal("ping", (await recorder.NextAsync()).Request.GetProperty("op").GetString());
    }

    [Fact]
    public void ThePluginsSectionIsReadInOrder()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_plugins_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            ServName=x
            [PLUGINS]
            Welcome=plugins/Welcome.dll
            Discord=plugins/Discord/Discord.dll
            """);
        try
        {
            var ini = new SphereNet.Core.Configuration.IniParser();
            ini.Load(tmp);
            var config = new SphereNet.Core.Configuration.SphereConfig();
            config.LoadFromIni(ini);
            Assert.Equal(["Welcome", "Discord"], config.Plugins.Select(p => p.Key));
            Assert.Equal("plugins/Discord/Discord.dll", config.Plugins[1].Value);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
