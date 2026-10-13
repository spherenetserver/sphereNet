using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Network.Manager;
using SphereNet.Network.State;
using SphereNet.Plugins;
using SphereNet.Server.Plugins;

namespace SphereNet.Tests;

/// <summary>
/// The synchronous plugin hooks: incoming packet filters (only for the opcodes asked
/// for, dropping a packet before any handler, script filter or ping shortcut) and login
/// checks (refusing a login whose password was accepted). A hook that throws lets the
/// packet or login through; a plugin whose hooks keep failing is switched off.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PluginHookTests
{
    private sealed class HookPlugin(Action<IPluginHost> setup) : ISpherePlugin
    {
        public void OnLoad(IPluginHost host) => setup(host);
    }

    private static PluginRuntime Runtime() =>
        new((_, _) => null, NullLoggerFactory.Instance);

    [Fact]
    public void AFilterSeesOnlyItsOpcodesAndCanDrop()
    {
        using var runtime = Runtime();
        var seen = new List<IncomingPacket>();
        runtime.Add("Filter", new HookPlugin(h => h.AddIncomingPacketFilter(p =>
        {
            seen.Add(p);
            return p.Data[1] != 0xFF;
        }, 0x73)));

        Assert.True(runtime.WantsPacket(0x73));
        Assert.False(runtime.WantsPacket(0x02));

        Assert.True(runtime.AllowIncomingPacket(0x73, [0x73, 0x01], "1.2.3.4", "acct"));
        Assert.False(runtime.AllowIncomingPacket(0x73, [0x73, 0xFF], "1.2.3.4", "acct"));
        Assert.Equal(2, seen.Count);
        Assert.Equal("1.2.3.4", seen[0].Ip);
        Assert.Equal("acct", seen[0].Account);
    }

    [Fact]
    public void AFilterThatThrowsLetsThePacketThroughAndIsSwitchedOffInTheEnd()
    {
        using var runtime = Runtime();
        runtime.Add("Broken", new HookPlugin(h => h.AddIncomingPacketFilter(_ => throw new InvalidOperationException())));

        for (int i = 0; i < 10; i++)
            Assert.True(runtime.AllowIncomingPacket(0x02, [0x02], "", null));

        Assert.Empty(runtime.ActivePlugins);
        Assert.False(runtime.WantsPacket(0x02));
    }

    [Fact]
    public void AFilterThatStallsTheLoopCountsAsAFailure()
    {
        using var runtime = Runtime();
        runtime.Add("Slow", new HookPlugin(h => h.AddIncomingPacketFilter(_ => { Thread.Sleep(120); return true; })));

        runtime.AllowIncomingPacket(0x02, [0x02], "", null);

        Assert.Equal(1, runtime.Describe().Single().Faults);
    }

    [Fact]
    public void ALoginCheckRefusesWithItsReason()
    {
        using var runtime = Runtime();
        runtime.Add("Gate", new HookPlugin(h => h.AddLoginCheck(a => a.Account == "evil" ? "banned elsewhere" : null)));

        Assert.True(runtime.HasLoginChecks);
        Assert.Null(runtime.CheckLogin("alice", "1.2.3.4"));
        Assert.Equal("Gate: banned elsewhere", runtime.CheckLogin("evil", "1.2.3.4"));
    }

    [Fact]
    public void RemovingAHookStopsIt()
    {
        using var runtime = Runtime();
        IDisposable? filter = null, check = null;
        runtime.Add("Temp", new HookPlugin(h =>
        {
            filter = h.AddIncomingPacketFilter(_ => false);
            check = h.AddLoginCheck(_ => "no");
        }));

        filter!.Dispose();
        check!.Dispose();

        Assert.False(runtime.WantsPacket(0x02));
        Assert.False(runtime.HasLoginChecks);
        Assert.Equal(0, runtime.Describe().Single().PacketFilters);
    }

    // ---- wired into the network and login paths ----

    private static (NetworkManager Mgr, NetState State) Connection()
    {
        var mgr = new NetworkManager(1, NullLoggerFactory.Instance) { UseCrypt = false, UseNoCrypt = true };
        var state = mgr.GetState(0)!;
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        crypto.GetType().GetField("_initialized", flags)!.SetValue(crypto, true);
        crypto.GetType().GetField("_encType", flags)!.SetValue(crypto, EncryptionType.None);
        return (mgr, state);
    }

    [Fact]
    public void ADroppedPingIsNeverAnswered()
    {
        var (mgr, state) = Connection();
        mgr.PluginPacketGate = op => op == 0x73;
        mgr.PluginPacketFilter = (_, _, frame) => frame[1] != 0x42;

        state.InjectReceived([0x73, 0x42, 0x73, 0x07]);
        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);

        var echoed = TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();
        Assert.DoesNotContain(echoed, p => p.SequenceEqual(new byte[] { 0x73, 0x42 }));
        Assert.Contains(echoed, p => p.SequenceEqual(new byte[] { 0x73, 0x07 }));
    }

    [Fact]
    public void APluginRefusedLoginIsToldBlockedAndReported()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(40_000, 50_000));
        var accounts = new AccountManager(lf);
        string name = "plug" + Guid.NewGuid().ToString("N")[..8];
        accounts.CreateAccount(name, "pw");
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        string? reported = null;
        GameClient.LoginCheck = (account, _) => account == name ? "not today" : null;
        GameClient.LoginRejected = (_, _, reason) => reported = reason;

        client.HandleLoginRequest(name, "pw");

        var denied = TestHarness.GetQueuedPackets(state).Single(p => p.Span[0] == 0x82);
        Assert.Equal(0x02, denied.Span[1]);
        Assert.Equal("plugin: not today", reported);
        Assert.True(state.IsClosing);
    }
}
