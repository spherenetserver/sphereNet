using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Network.Manager;
using SphereNet.Network.State;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// PACKETx= / OUTPACKETx= script filters as Source-X runs them
/// (CClient::xPacketFilter / xOutPacketFilter, CClientEvent.cpp:3151-3248), through the
/// server's real wiring, and the 0xEB KR toolbar (CClient::Event_UseToolbar).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptPacketFilterServerTests
{
    private static readonly Type P = typeof(SphereNet.Server.Program);

    private const string Script =
        "[FUNCTION f_pf_war]\n" +
        "IF (<ARGN1> == 114)\n" +
        " IF (<LOCAL.NUM> == 5)\n" +
        "  IF (<LOCAL.0> == 114) && (<LOCAL.1> == 1) && (<LOCAL.3> == 0x32)\n" +
        "   IF (<LOCAL.CONNECTIONTYPE> == 4) && (STRCMPI(<LOCAL.ACCOUNT>,filtertester) == 0) && (<LOCAL.CHAR> != 0)\n" +
        "    RETURN 1\n" +
        "   ENDIF\n" +
        "  ENDIF\n" +
        " ENDIF\n" +
        "ENDIF\n" +
        "RETURN 0\n\n" +
        "[FUNCTION f_pf_unknown]\n" +
        "RETURN 1\n\n" +
        "[FUNCTION f_pf_pass]\n" +
        "RETURN 0\n\n" +
        "[FUNCTION f_pf_out]\n" +
        "IF (<ARGN1> == 115) && (<LOCAL.1> == 7)\n" +
        " RETURN 1\n" +
        "ENDIF\n" +
        "RETURN 0\n";

    private sealed class Fixture : IDisposable
    {
        private readonly FieldInfo _hooksField = P.GetField("_systemHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly FieldInfo _configField = P.GetField("_config", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly FieldInfo _logField = P.GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly Dictionary<int, GameClient> _clients =
            (Dictionary<int, GameClient>)P.GetField("_clients", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        private readonly object? _oldHooks, _oldConfig, _oldLog;
        private readonly string _path;

        public NetworkManager Network { get; }
        public NetState State { get; }
        public SphereConfig Config { get; } = new();

        public Fixture(bool loggedIn = true)
        {
            _oldHooks = _hooksField.GetValue(null);
            _oldConfig = _configField.GetValue(null);
            _oldLog = _logField.GetValue(null);

            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            _path = Path.Combine(Path.GetTempPath(), $"pktfilter-{Guid.NewGuid():N}.scp");
            File.WriteAllText(_path, Script);
            stack.Resources.LoadResourceFile(_path);
            _hooksField.SetValue(null, new ScriptSystemHooks(stack.Runner));
            _configField.SetValue(null, Config);
            _logField.SetValue(null, NullLogger.Instance);

            Network = new NetworkManager(1, NullLoggerFactory.Instance) { UseCrypt = false, UseNoCrypt = true };
            State = Network.GetState(0)!;
            typeof(NetState)
                .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(State, true);
            State.IsSeeded = true;
            State.ConnectionType = ConnectType.Game;
            var crypto = State.Crypto;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            crypto.GetType().GetField("_initialized", flags)!.SetValue(crypto, true);
            crypto.GetType().GetField("_encType", flags)!.SetValue(crypto, EncryptionType.None);

            if (loggedIn)
            {
                var logs = LoggerFactory.Create(_ => { });
                var world = TestHarness.CreateWorld();
                var client = new GameClient(State, world, new AccountManager(logs), logs.CreateLogger<GameClient>());
                var ch = world.CreateCharacter();
                ch.IsPlayer = true;
                world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
                var account = new Account { Name = "filtertester" };
                TestHarness.AttachCharacter(client, ch, account);
                _clients[State.Id] = client;
            }
        }

        public void Install() =>
            P.GetMethod("InstallPacketScriptFilters", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [Network, Config]);

        public void Feed(byte[] bytes)
        {
            State.InjectReceived(bytes);
            typeof(NetworkManager)
                .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Network, [State]);
        }

        public void Dispose()
        {
            _clients.Remove(State.Id);
            _hooksField.SetValue(null, _oldHooks);
            _configField.SetValue(null, _oldConfig);
            _logField.SetValue(null, _oldLog);
            Network.Dispose();
            File.Delete(_path);
        }
    }

    [Fact]
    public void Config_ReadsDecimalPacketAndOutPacketKeys()
    {
        string ini = Path.Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}.ini");
        File.WriteAllText(ini,
            "[SPHERE]\nPACKET114=f_pf_war\nOUTPACKET28=f_pf_out\nPACKET255=f_too_high\n" +
            "PACKETX=f_not_a_number\nPACKET3=" + new string('f', 31) + "\n");
        try
        {
            var parser = new IniParser();
            parser.Load(ini);
            var cfg = new SphereConfig();
            cfg.LoadFromIni(parser);
            Assert.Equal("f_pf_war", cfg.PacketFilters[114]);
            Assert.Equal("f_pf_out", cfg.OutPacketFilters[28]);
            Assert.Null(cfg.PacketFilters[3]);          // name over 30 characters
            Assert.Equal(1, cfg.PacketFilters.Count(n => n != null));
            Assert.DoesNotContain(parser.UnreadKeys(), k => k.EndsWith("|PACKET114", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(ini);
        }
    }

    [Fact]
    public void Return1_CancelsCoreWarModeHandling_WithSourceXArguments()
    {
        using var f = new Fixture();
        f.Config.PacketFilters[0x72] = "f_pf_war";
        f.Install();
        int warCalls = 0;
        f.State.WarModeHandler = (_, _) => warCalls++;

        f.Feed([0x72, 0x01, 0x00, 0x32, 0x00, 0x73, 0x42]);

        Assert.Equal(0, warCalls);
        Assert.Equal(0, f.State.ReceivedData.Length);
        Assert.Contains(TestHarness.GetQueuedPackets(f.State), p => p.Span.SequenceEqual(new byte[] { 0x73, 0x42 }));
    }

    [Fact]
    public void Return0_LetsTheServerHandleThePacket()
    {
        using var f = new Fixture();
        f.Config.PacketFilters[0x72] = "f_pf_pass";
        f.Install();
        int warCalls = 0;
        f.State.WarModeHandler = (_, _) => warCalls++;

        f.Feed([0x72, 0x01, 0x00, 0x32, 0x00]);

        Assert.Equal(1, warCalls);
    }

    [Fact]
    public void UnregisteredOpcodes_AreFilteredToo_AndNeedNoLogin()
    {
        using var f = new Fixture(loggedIn: false);
        f.Config.PacketFilters[0xF7] = "f_pf_unknown";
        f.Install();
        int unknown = 0;
        f.Network.OnUnknownPacket += (_, _, _) => unknown++;

        f.Feed([0xF7, 0x00, 0x04, 0x09]);

        Assert.Equal(0, unknown);
        Assert.Equal(0, f.State.ReceivedData.Length);
    }

    [Fact]
    public void NoFilterConfigured_InstallsNothing()
    {
        using var f = new Fixture();
        f.Install();
        Assert.Null(f.Network.PacketScriptHook);
        Assert.Null(f.Network.OutPacketScriptHook);
        Assert.Null(f.State.OutPacketScriptHook);
    }

    [Fact]
    public void OutPacket_Return1_KeepsThePacketFromTheClient()
    {
        using var f = new Fixture();
        f.Config.OutPacketFilters[0x73] = "f_pf_out";
        f.Install();

        f.State.SendRaw([0x73, 0x07]);
        f.State.SendRaw([0x73, 0x08]);

        var sent = TestHarness.GetQueuedPackets(f.State).Select(p => p.Span.ToArray()).ToList();
        Assert.DoesNotContain(sent, p => p.SequenceEqual(new byte[] { 0x73, 0x07 }));
        Assert.Contains(sent, p => p.SequenceEqual(new byte[] { 0x73, 0x08 }));
    }

    // ---- 0xEB KR toolbar ----

    private static (GameClient Client, Character Player, TriggerDispatcher Dispatcher) ToolbarClient()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1501);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var dispatcher = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: dispatcher);
        return (client, player, dispatcher);
    }

    [Fact]
    public void Toolbar_FiresUserKRToolbar_ThenSelectsTheVirtue()
    {
        var (client, player, dispatcher) = ToolbarClient();
        var toolbar = new List<(long, long, long)>();
        var virtue = new List<(long, object?)>();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserKRToolbar", (_, a) => { toolbar.Add((a.N1, a.N2, a.N3)); return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserVirtue", (_, a) => { virtue.Add((a.N1, a.O1)); return TriggerResult.Default; });

        client.HandleUseToolbar(5, 3);

        Assert.Equal((5L, 3L, 0L), Assert.Single(toolbar));
        var v = Assert.Single(virtue);
        Assert.Equal(3L, v.Item1);
        Assert.Same(player, v.Item2);
    }

    [Fact]
    public void Toolbar_Return1_StopsTheAction()
    {
        var (client, _, dispatcher) = ToolbarClient();
        int virtue = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserKRToolbar", (_, _) => TriggerResult.True);
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserVirtue", (_, _) => { virtue++; return TriggerResult.Default; });

        client.HandleUseToolbar(5, 3);

        Assert.Equal(0, virtue);
    }

    [Fact]
    public void Toolbar_ObjectUseOnSelf_IsTheMacroForm_PaperdollNotDismount()
    {
        var (client, player, _) = ToolbarClient();
        TestHarness.ClearQueuedPackets(client.NetState);

        client.HandleUseToolbar(4, player.Uid.Value);

        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x88);
    }

    [Fact]
    public void Toolbar_PacketReachesTheClient()
    {
        using var f = new Fixture();
        var client = ((Dictionary<int, GameClient>)P.GetField("_clients", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)[f.State.Id];
        var dispatcher = new TriggerDispatcher();
        var seen = new List<(long, long)>();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserKRToolbar", (_, a) => { seen.Add((a.N1, a.N2)); return TriggerResult.True; });
        client.SetEngines(triggerDispatcher: dispatcher);
        f.State.UseToolbarHandler = (s, t, a) => client.HandleUseToolbar(t, a);

        f.Feed([0xEB, 0x00, 0x01, 0x00, 0x06, 0x03, 0x00, 0x00, 0x00, 0x00, 0x15]);

        Assert.Equal((3L, 0x15L), Assert.Single(seen));
    }
}
