using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Host mode: the paperdoll info and picture cross the named-pipe bridge intact,
/// with the serial and the frame flag reaching the server (the arguments are named
/// so they cannot collide with the IPC envelope's own keys).
/// </summary>
public sealed class IpcPaperdollTests
{
    [Fact]
    public async Task PaperdollCrossesTheHostBridge()
    {
        string pipeName = "sn-paperdoll-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            GetPaperdoll = serial => serial == 0x1234
                ? new PaperdollInfo(serial, "Lord Test", "the Brave", "Lord Test, the Brave",
                    0x191, true, true, 1, "acct", false, true,
                    [new PaperdollItemInfo(22, 0x40000010, 0x1F03, 0x0021, "robe")])
                : null,
            GetPaperdollPng = (serial, frame) => serial == 0x1234
                ? (frame ? new byte[70001] : [1, 2, 3])
                : null,
        });
        Task serverTask = server.RunAsync(cts.Token);

        using var bridge = new IpcBridge();
        await bridge.ConnectAsync(pipeName);
        var ctx = HostPanelContext.Build(
            new ServerProcess("x.exe", bridge, new SphereNet.Panel.Logging.PanelLogSink()), bridge, null, null);

        var info = await Task.Run(() => ctx.GetPaperdoll!(0x1234));
        Assert.NotNull(info);
        Assert.Equal("Lord Test, the Brave", info!.PaperdollText);
        Assert.True(info.IsFemale);
        var robe = Assert.Single(info.Equipment);
        Assert.Equal((22, 0x40000010u, 0x1F03, 0x21, "robe"),
            (robe.Layer, robe.Serial, robe.DispId, robe.Hue, robe.Name));
        Assert.Null(await Task.Run(() => ctx.GetPaperdoll!(0x9999)));

        Assert.Equal(new byte[] { 1, 2, 3 }, await Task.Run(() => ctx.GetPaperdollPng!(0x1234, false)));
        Assert.Equal(70001, (await Task.Run(() => ctx.GetPaperdollPng!(0x1234, true)))!.Length);
        Assert.Null(await Task.Run(() => ctx.GetPaperdollPng!(0x9999, false)));

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    }
}
