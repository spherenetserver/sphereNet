using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Server.Plugins;

namespace SphereNet.Tests;

/// <summary>
/// The plugin bridge over a real loopback socket: a client must authenticate with the
/// token on its first line, receives only the events it subscribed to, and gets one
/// reply per command, matched by id. See docs/PLUGINS.md.
/// </summary>
public sealed class PluginBridgeTests
{
    private const string Token = "s3cret";

    private static PluginBridge StartBridge(Func<string, JsonElement, object?>? execute = null)
    {
        var bridge = new PluginBridge(IPAddress.Loopback, 0, Token,
            execute ?? ((op, _) => op == "echo" ? "echoed" : throw new ArgumentException($"unknown op '{op}'")),
            NullLogger.Instance);
        bridge.Start();
        return bridge;
    }

    private sealed class Conn : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private StreamReader _reader = null!;
        private Stream _stream = null!;

        public static async Task<Conn> OpenAsync(int port)
        {
            var c = new Conn();
            await c._tcp.ConnectAsync(IPAddress.Loopback, port);
            c._stream = c._tcp.GetStream();
            c._reader = new StreamReader(c._stream, Encoding.UTF8);
            return c;
        }

        public async Task SendAsync(object message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            await _stream.WriteAsync(bytes);
        }

        public async Task<JsonElement?> ReadAsync(int timeoutMs = 3000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                string? line = await _reader.ReadLineAsync(cts.Token);
                return line == null ? null : JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (OperationCanceledException) { return null; }
            catch (IOException) { return null; }
        }

        public void Dispose() => _tcp.Dispose();
    }

    private static async Task<Conn> AuthAsync(PluginBridge bridge, params string[] subscribe)
    {
        var c = await Conn.OpenAsync(bridge.Port);
        await c.SendAsync(new { op = "auth", token = Token, subscribe });
        var reply = await c.ReadAsync();
        Assert.True(reply?.GetProperty("ok").GetBoolean());
        Assert.Equal(PluginBridge.ProtocolVersion, reply!.Value.GetProperty("version").GetInt32());
        return c;
    }

    private static async Task WaitForClientsAsync(PluginBridge bridge)
    {
        for (int i = 0; i < 100 && !bridge.HasClients; i++)
            await Task.Delay(10);
        Assert.True(bridge.HasClients);
    }

    [Fact]
    public async Task AWrongTokenIsRefusedAndDisconnected()
    {
        using var bridge = StartBridge();
        using var c = await Conn.OpenAsync(bridge.Port);

        await c.SendAsync(new { op = "auth", token = "wrong" });
        var reply = await c.ReadAsync();

        Assert.False(reply?.GetProperty("ok").GetBoolean());
        Assert.Null(await c.ReadAsync());
        Assert.False(bridge.HasClients);
    }

    [Fact]
    public async Task AClientReceivesOnlyTheEventsItSubscribedTo()
    {
        using var bridge = StartBridge();
        using var c = await AuthAsync(bridge, "player.*", "save.completed");
        await WaitForClientsAsync(bridge);

        bridge.Publish("server.started", new { name = "x" });
        bridge.Publish("player.login", new { name = "Alice" });
        bridge.Publish("save.started", new { forced = false });
        bridge.Publish("save.completed", new { ok = true });

        var first = await c.ReadAsync();
        var second = await c.ReadAsync();
        Assert.Equal("player.login", first?.GetProperty("event").GetString());
        Assert.Equal("Alice", first!.Value.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal("save.completed", second?.GetProperty("event").GetString());
    }

    [Fact]
    public async Task ACommandIsAnsweredWithItsId()
    {
        using var bridge = StartBridge();
        using var c = await AuthAsync(bridge);

        await c.SendAsync(new { id = 7, op = "echo" });
        var ok = await c.ReadAsync();
        await c.SendAsync(new { id = 8, op = "nope" });
        var bad = await c.ReadAsync();

        Assert.Equal(7, ok?.GetProperty("id").GetInt32());
        Assert.True(ok!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("echoed", ok.Value.GetProperty("result").GetString());
        Assert.Equal(8, bad?.GetProperty("id").GetInt32());
        Assert.False(bad!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown op", bad.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ADisconnectedClientIsForgotten()
    {
        using var bridge = StartBridge();
        var c = await AuthAsync(bridge, "*");
        await WaitForClientsAsync(bridge);

        c.Dispose();
        for (int i = 0; i < 200 && bridge.HasClients; i++)
            await Task.Delay(10);

        Assert.False(bridge.HasClients);
    }

    [Theory]
    [InlineData("*", "player.login", true)]
    [InlineData("player.*", "player.login", true)]
    [InlineData("player.*", "players", false)]
    [InlineData("save.completed", "save.completed", true)]
    [InlineData("save.completed", "save.started", false)]
    [InlineData("custom.*", "custom.quest.done", true)]
    public void SubscriptionPatterns(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, PluginBridge.Matches(pattern, name));
    }

    [Fact]
    public void TheBridgeKeysAreReadFromTheIni()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_plug_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            PluginBridgePort=2600
            PluginBridgeToken=abc
            PluginBridgeAddress=0.0.0.0
            """);
        try
        {
            var ini = new IniParser();
            ini.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(ini);
            Assert.Equal(2600, config.PluginBridgePort);
            Assert.Equal("abc", config.PluginBridgeToken);
            Assert.Equal("0.0.0.0", config.PluginBridgeAddress);
        }
        finally { try { File.Delete(tmp); } catch { } }

        var defaults = new SphereConfig();
        Assert.Equal(0, defaults.PluginBridgePort);
        Assert.Equal("127.0.0.1", defaults.PluginBridgeAddress);
    }
}
