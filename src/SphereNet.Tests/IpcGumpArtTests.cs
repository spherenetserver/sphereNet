using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report (Host mode): every /gumpart request of the gump designer timed out
/// after 8 s. The Host sent the gump number as an argument named "id", which is
/// also the request id of the IPC envelope, so the argument replaced it and the
/// server's answer could never be matched to the request.
/// </summary>
public sealed class IpcGumpArtTests
{
    [Fact]
    public async Task GumpArtCrossesTheHostBridge()
    {
        string pipeName = "sn-gumpart-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            GetGumpPng = id => id == 2604 ? new byte[83131] : [(byte)(id & 0xFF)],
        });
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        var ctx = HostPanelContext.Build(new ServerProcess("x.exe", bridge, new SphereNet.Panel.Logging.PanelLogSink()), bridge, null, null);

        // The designer asks for a page of gumps at once.
        var results = await Task.WhenAll(Enumerable.Range(2600, 9)
            .Select(id => Task.Run(() => (id, png: ctx.GetGumpPng!(id)))));

        foreach (var (id, png) in results)
        {
            Assert.NotNull(png);
            Assert.Equal(id == 2604 ? 83131 : 1, png!.Length);
        }

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task AnArgumentNamedLikeTheEnvelopeIsRefused()
    {
        string pipeName = "sn-envelope-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext());
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        await Assert.ThrowsAsync<ArgumentException>(() => bridge.QueryAsync<byte[]>("gump", new { id = 1 }));

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }
}
