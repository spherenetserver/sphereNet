using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Network.Manager;
using SphereNet.Network.State;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// MAXSIZECLIENTIN / MAXSIZECLIENTOUT and f_onclient_exceed_network_quota
/// (Source-X CNetworkThread::tick, CNetworkThread.cpp:135-189, and
/// CClient::Event_ExceededNetworkQuota, CClientEvent.cpp:819-842).
///
/// The function belongs to the 10-second byte quota only. It used to run whenever the
/// per-pass packet quota (MaxPacketsPerTick) was hit - an ordinary throttle that a
/// staff character walking into a busy town reaches - and a script pack whose function
/// disconnects (the documented default) kicked that player.
/// </summary>
public sealed class NetworkByteQuotaTests
{
    private const long Now = 1_000_000;

    private static (NetworkManager Mgr, NetState State) Connection()
    {
        var mgr = new NetworkManager(2, NullLoggerFactory.Instance);
        var state = mgr.GetState(0)!;
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var t = crypto.GetType();
        t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
        return (mgr, state);
    }

    private sealed record Hit(byte Type, long Bytes, long Quota);

    private static List<Hit> Record(NetworkManager mgr, bool? verdict = true)
    {
        var hits = new List<Hit>();
        mgr.ByteQuotaExceeded = (_, type, bytes, quota) =>
        {
            hits.Add(new Hit(type, bytes, quota));
            return verdict;
        };
        return hits;
    }

    [Fact]
    public void ThePerPassPacketQuotaThrottlesButNeverReachesTheByteQuota()
    {
        var (mgr, state) = Connection();
        mgr.MaxPacketsPerTick = 10;
        mgr.FloodDetectionCount = 0;
        mgr.MaxSizeClientIn = 1_000_000;
        mgr.MaxSizeClientOut = 1_000_000;
        var hits = Record(mgr);
        int throttled = 0;
        mgr.OnPacketQuotaExceeded += (_, _) => throttled++;

        var pings = new byte[2 * 200];
        for (int i = 0; i < 200; i++) { pings[i * 2] = 0x73; pings[i * 2 + 1] = 0x00; }
        state.InjectReceived(pings);
        var process = typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int pass = 0; pass < 25; pass++)
            process.Invoke(mgr, [state]);
        mgr.CheckByteQuotas(Now);

        Assert.True(throttled > 0, "the packet quota still throttles");
        Assert.Empty(hits);
        Assert.False(state.IsClosing);
    }

    [Fact]
    public void ThePacketQuotaHookNoLongerAliasesTheNetworkQuotaFunction()
    {
        string src = File.ReadAllText(TestRepo.Tracked("src/SphereNet.Server/Program.EngineWiring.cs"));
        Assert.DoesNotContain("OnPacketQuotaExceeded +=", src);
        string hooks = File.ReadAllText(TestRepo.Tracked("src/SphereNet.Scripting/Execution/ScriptSystemHooks.cs"));
        Assert.DoesNotContain("\"quotaexceed\"", hooks);
    }

    [Fact]
    public void OutputOverItsQuotaIsTypeOneAndWinsOverInput()
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientOut = 100;
        mgr.MaxSizeClientIn = 50;
        var hits = Record(mgr);
        state.AddOutBytes(101);
        state.AddInBytes(500);

        mgr.CheckByteQuotas(Now);

        Assert.Equal([new Hit(1, 101, 100)], hits);
    }

    [Fact]
    public void InputOverItsQuotaIsTypeTwo()
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientOut = 100;
        mgr.MaxSizeClientIn = 50;
        var hits = Record(mgr);
        state.AddOutBytes(100);   // at the quota is not over it
        state.AddInBytes(51);

        mgr.CheckByteQuotas(Now);

        Assert.Equal([new Hit(2, 51, 50)], hits);
    }

    [Fact]
    public void AQuotaOfZeroIsDisabled()
    {
        var (mgr, state) = Connection();
        var hits = Record(mgr);
        state.AddOutBytes(10_000_000);
        state.AddInBytes(10_000_000);

        mgr.CheckByteQuotas(Now);

        Assert.Empty(hits);
        Assert.False(state.IsClosing);
        Assert.Equal(0, state.OutByteCounter);   // counters still restart every period
        Assert.Equal(0, state.InByteCounter);
    }

    [Fact]
    public void TheCheckRunsOncePerTenSecondsAndRestartsTheCounters()
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientIn = 100;
        var hits = Record(mgr);

        mgr.CheckByteQuotas(Now);                 // first period boundary
        state.AddInBytes(60);
        mgr.CheckByteQuotas(Now + 5_000);         // not yet ten seconds
        state.AddInBytes(60);
        Assert.Equal(120, state.InByteCounter);
        Assert.Empty(hits);

        mgr.CheckByteQuotas(Now + 10_001);
        Assert.Equal([new Hit(2, 120, 100)], hits);
        Assert.Equal(0, state.InByteCounter);

        // 60 bytes in the next period alone is under the quota.
        state.AddInBytes(60);
        mgr.CheckByteQuotas(Now + 20_002);
        Assert.Single(hits);
    }

    [Fact]
    public void AConnectionWithoutAClientIsClosed()
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientIn = 10;
        Record(mgr, verdict: null);
        state.AddInBytes(11);

        mgr.CheckByteQuotas(Now);

        Assert.True(state.IsClosing);
    }

    [Fact]
    public void NoHandlerClosesTheConnection()
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientOut = 10;
        state.AddOutBytes(11);

        mgr.CheckByteQuotas(Now);

        Assert.True(state.IsClosing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHandlerThatKeepsTheClientLeavesItConnected(bool log)
    {
        var (mgr, state) = Connection();
        mgr.MaxSizeClientOut = 10;
        Record(mgr, verdict: log);
        state.AddOutBytes(11);

        mgr.CheckByteQuotas(Now);

        Assert.False(state.IsClosing);
    }

    [Fact]
    public void TheIniKeysAreReadWithSourceXDefaults()
    {
        var cfg = new SphereConfig();
        Assert.Equal(80_000, cfg.MaxSizeClientOut);   // CServerConfig.cpp:336
        Assert.Equal(10_000, cfg.MaxSizeClientIn);    // CServerConfig.cpp:337

        string path = Path.Combine(Path.GetTempPath(), $"quota-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "[SPHERE]\nMaxSizeClientOut=0\nMaxSizeClientIn=0x400\n");
        try
        {
            var ini = new IniParser();
            ini.Load(path);
            cfg.LoadFromIni(ini);
            Assert.Equal(0, cfg.MaxSizeClientOut);
            Assert.Equal(0x400, cfg.MaxSizeClientIn);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- real sockets: what the counters count ---------------------------------

    [Fact]
    public void TheCountersCountTheBytesTheSocketMoved()
    {
        var mgr = new NetworkManager(2, NullLoggerFactory.Instance) { UseNoCrypt = true, UseCrypt = false };
        try
        {
            Assert.True(mgr.Start("127.0.0.1", 0));
            var listen = (Socket)typeof(NetworkManager)
                .GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(mgr)!;
            int port = ((IPEndPoint)listen.LocalEndPoint!).Port;
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            client.Connect(IPAddress.Loopback, port);
            int localPort = ((IPEndPoint)client.LocalEndPoint!).Port;

            NetState? state = null;
            Spin(() =>
            {
                mgr.CheckNewConnections();
                for (int i = 0; mgr.GetState(i) is { } s; i++)
                    if (s.IsInUse && s.RemoteEndPoint?.Port == localPort) { state = s; return true; }
                return false;
            });

            var login = new byte[62];
            login[0] = 0x80;
            "quotauser"u8.CopyTo(login.AsSpan(1));
            "pw"u8.CopyTo(login.AsSpan(31));
            byte[] sent = [0x7F, 0x00, 0x00, 0x01, .. login];
            client.Send(sent);
            Spin(() => { mgr.ProcessAllInput(); return state!.InByteCounter >= sent.Length; });
            Assert.Equal(sent.Length, state!.InByteCounter);

            state.Send(new SphereNet.Network.Packets.Outgoing.PacketDeleteObject(0x40000001));
            mgr.ProcessAllOutput();
            long outBytes = state.OutByteCounter;
            Assert.True(outBytes > 0);

            client.ReceiveTimeout = 2000;
            var buf = new byte[4096];
            long received = 0;
            while (received < outBytes)
            {
                int n = client.Receive(buf);
                if (n <= 0) break;
                received += n;
            }
            Assert.Equal(outBytes, received);
        }
        finally
        {
            mgr.Dispose();
        }
    }

    private static void Spin(Func<bool> step)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!step())
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException("loopback condition not reached");
            Thread.Sleep(1);
        }
    }
}

/// <summary>The script side: <c>f_onclient_exceed_network_quota</c> as the server
/// runs it, with its arguments and its RETURN semantics.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NetworkQuotaScriptHookTests
{
    private static readonly Type P = typeof(SphereNet.Server.Program);

    private sealed record Outcome(bool? Log, bool Closed, string? Tag);

    private static Outcome Run(string script, byte type = 1, long bytes = 90_000, long quota = 80_000,
        bool withAccount = true)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"quota-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        var hooksField = P.GetField("_systemHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var clients = (Dictionary<int, GameClient>)P.GetField("_clients", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var logField = P.GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldHooks = hooksField.GetValue(null);
        var oldLog = logField.GetValue(null);
        const int clientKey = 987_655;
        try
        {
            stack.Resources.LoadResourceFile(path);
            var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var accounts = new AccountManager(logs);
            var client = TestHarness.CreateClient(logs, world, accounts, clientKey);
            typeof(NetState)
                .GetField("<RemoteEndPoint>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client.NetState, new IPEndPoint(IPAddress.Parse("10.1.2.3"), 5000));
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch, withAccount ? accounts.CreateAccount("quotaacct", "pw") : null);
            clients[clientKey] = client;
            hooksField.SetValue(null, new ScriptSystemHooks(stack.Runner));
            logField.SetValue(null, NullLogger.Instance);

            var log = (bool?)P.GetMethod("OnByteQuotaExceeded", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [client.NetState, type, bytes, quota]);
            ch.TryGetTag("Q", out string? tag);
            return new Outcome(log, client.NetState.IsClosing, tag);
        }
        finally
        {
            clients.Remove(clientKey);
            hooksField.SetValue(null, oldHooks);
            logField.SetValue(null, oldLog);
            File.Delete(path);
        }
    }

    private const string Recorder =
        "[FUNCTION f_onclient_exceed_network_quota]\n" +
        "SRC.TAG.Q=<ARGN1>|<ARGN2>|<ARGN3>|<LOCAL.ACCOUNT>|<LOCAL.IP>\n";

    [Fact]
    public void TheFunctionSeesTypeBytesQuotaAccountIpAndTheClientAsSrc()
    {
        var o = Run(Recorder + "RETURN 1\n", type: 2, bytes: 12_345, quota: 10_000);
        Assert.Equal("2|12345|10000|quotaacct|10.1.2.3", o.Tag);
    }

    [Fact]
    public void WithoutAnAccountTheAccountIsNA()
    {
        var o = Run(Recorder + "RETURN 1\n", withAccount: false);
        Assert.NotNull(o.Tag);
        Assert.EndsWith("|NA|10.1.2.3", o.Tag);
    }

    [Fact]
    public void ReturnZeroKeepsTheClientAndLogs()
    {
        var o = Run(Recorder + "RETURN 0\n");
        Assert.Equal(true, o.Log);
        Assert.False(o.Closed);
    }

    [Fact]
    public void ReturnOneKeepsTheClientSilently()
    {
        var o = Run(Recorder + "RETURN 1\n");
        Assert.Equal(false, o.Log);
        Assert.False(o.Closed);
    }

    [Theory]
    [InlineData("RETURN 2\n")]
    [InlineData("")]
    public void AnyOtherOutcomeDisconnectsAndLogs(string tail)
    {
        var o = Run(Recorder + tail);
        Assert.NotNull(o.Tag);   // the function did run
        Assert.Equal(true, o.Log);
        Assert.True(o.Closed);
    }

    [Fact]
    public void WithoutTheFunctionTheClientIsDisconnected()
    {
        var o = Run("[FUNCTION f_other]\nRETURN 1\n");
        Assert.Equal(true, o.Log);
        Assert.True(o.Closed);
    }
}
