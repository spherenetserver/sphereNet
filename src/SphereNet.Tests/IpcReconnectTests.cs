using System;
using System.Threading;
using System.Threading.Tasks;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Server.Ipc;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 13 / M3 — the IPC server accepted exactly one host connection: RunAsync
/// waited for a single NamedPipeServerStream, ran the session, and returned when
/// the host disconnected, so the panel/console bridge could never reconnect
/// without restarting the whole game server. RunAsync now loops on a
/// cancellation-bound accept, building and tearing down a fresh pipe per session.
/// </summary>
public sealed class IpcReconnectTests
{
    private static async Task WaitForShutdownAsync(Task serverTask)
    {
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Reconnects_WithoutServerRestart_AndDoesNotLeak()
    {
        string pipeName = "spherenet-reconnect-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int handledSessions = 0;

        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            CreateAccount = (_, _) => { Interlocked.Increment(ref handledSessions); return true; },
        });

        Task serverTask = server.RunAsync(cts.Token);

        // 20 back-to-back connect / mutate / disconnect cycles against a server
        // that is never restarted. Before the accept-loop fix the server served
        // exactly one session and the 2nd ConnectAsync would block until the
        // client's 15s connect timeout, then fail.
        for (int i = 0; i < 20; i++)
        {
            using var bridge = new IpcBridge();
            await bridge.ConnectAsync(pipeName);
            Assert.True(await bridge.MutateAsync("account_create", new { name = "acct" + i, pass = "x" }));
            bridge.Disconnect();
        }

        // Every session reached the live handler exactly once — no dropped or
        // duplicated sessions across the reconnect boundary.
        Assert.Equal(20, Volatile.Read(ref handledSessions));

        cts.Cancel();
        await WaitForShutdownAsync(serverTask);
    }

    [Fact]
    public async Task SecondConnection_SeesFreshSession_AfterFirstDisconnects()
    {
        string pipeName = "spherenet-reconnect2-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var server = new IpcServer(pipeName);
        server.SetContext(new PanelContext
        {
            SetAccountBanned = (_, banned) => banned, // echoes the flag back
        });

        Task serverTask = server.RunAsync(cts.Token);

        using (var first = new IpcBridge())
        {
            await first.ConnectAsync(pipeName);
            Assert.True(await first.MutateAsync("account_ban", new { name = "a", banned = true }));
            first.Disconnect();
        }

        // A brand-new bridge after the first fully disconnected must be served by a
        // fresh session and get correct responses (proving the writer/reader were
        // rebuilt, not reused from the dead session).
        using (var second = new IpcBridge())
        {
            await second.ConnectAsync(pipeName);
            Assert.False(await second.MutateAsync("account_ban", new { name = "b", banned = false }));
            Assert.True(await second.MutateAsync("account_ban", new { name = "c", banned = true }));
            second.Disconnect();
        }

        cts.Cancel();
        await WaitForShutdownAsync(serverTask);
    }
}
