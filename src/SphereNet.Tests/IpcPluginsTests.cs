using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>In Host mode the panel's plugin page comes from the server over IPC; the
/// overview must survive the round trip whole.</summary>
public sealed class IpcPluginsTests
{
    [Fact]
    public async Task ThePluginOverviewCrossesTheHostBridge()
    {
        string pipeName = "sn-plugins-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        var connected = new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc);
        server.SetContext(new PanelContext
        {
            GetPlugins = () => new PluginOverview(true, "127.0.0.1:2599",
            [
                new PluginInfo("dll", "Welcome", true, "SphereNet.Plugins.Sample.WelcomePlugin", 1, 2, 1, 3, 4, 0, 5, null),
                new PluginInfo("bridge", "127.0.0.1:50123", true, "player.*", 1, 0, 0, 0, 0, 7, 0, connected),
            ]),
        });
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        var ctx = HostPanelContext.Build(new ServerProcess("x.exe", bridge, new SphereNet.Panel.Logging.PanelLogSink()), bridge, null, null);

        var overview = await Task.Run(() => ctx.GetPlugins!());

        Assert.True(overview.BridgeListening);
        Assert.Equal("127.0.0.1:2599", overview.BridgeEndpoint);
        Assert.Equal(2, overview.Plugins.Count);
        Assert.Equal(new PluginInfo("dll", "Welcome", true, "SphereNet.Plugins.Sample.WelcomePlugin", 1, 2, 1, 3, 4, 0, 5, null),
            overview.Plugins[0]);
        Assert.Equal(7, overview.Plugins[1].DroppedEvents);
        Assert.Equal(connected, overview.Plugins[1].ConnectedUtc!.Value.ToUniversalTime());

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }
}
