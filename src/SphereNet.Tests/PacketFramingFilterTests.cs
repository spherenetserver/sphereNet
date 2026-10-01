using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Incoming framing against the Source-X registry (CPacketManager.cpp
/// registerStandardPackets + the Packet(n) expected lengths in receive.cpp), the
/// general packet filter call site (CNetworkInput.cpp:367/387), and the 0x6F body
/// validation.
/// </summary>
public sealed class PacketFramingFilterTests
{
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

    private static void Process(NetworkManager mgr, NetState state) =>
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);

    private static readonly byte[] Ping = [0x73, 0x42];

    /// <summary>Feed <paramref name="first"/> followed by a ping in one read and
    /// assert both were framed: nothing left over, connection open, ping echoed.</summary>
    private static void AssertFollowingPingSurvives(byte[] first)
    {
        var (mgr, state) = Connection();
        state.InjectReceived([.. first, .. Ping]);
        Process(mgr, state);

        Assert.False(state.IsClosing);
        Assert.Equal(0, state.ReceivedData.Length);
        var echoed = TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();
        Assert.Contains(echoed, p => p.SequenceEqual(Ping));
    }

    // ---- N13: fixed lengths ----

    [Fact]
    public void Scroll_0xA6_IsFiveBytes_AndTheNextPacketIsProcessed()
    {
        Assert.Equal(5, PacketDefinitions.GetPacketLength(0xA6));
        AssertFollowingPingSurvives([0xA6, 0x01, 0x02, 0x03, 0x04]);
    }

    [Fact]
    public void UseHotbar_0xEB_IsElevenBytes_AndTheNextPacketIsProcessed()
    {
        Assert.Equal(11, PacketDefinitions.GetPacketLength(0xEB));
        AssertFollowingPingSurvives([0xEB, 0x00, 0x01, 0x00, 0x06, 0x03, 0x00, 0x00, 0x00, 0x00, 0x15]);
    }

    [Theory]
    [InlineData(new byte[] { 0x3F, 0x00, 0x05, 0xAA, 0xBB })]                               // StaticUpdate (variable)
    [InlineData(new byte[] { 0x69, 0x00, 0x04, 0x01 })]                                     // Options (variable)
    [InlineData(new byte[] { 0xD0, 0x00, 0x03 })]                                           // ConfigFile (variable)
    [InlineData(new byte[] { 0xE8, 0, 0, 0, 1, 0, 2, 0, 0, 0, 3, 1, 1 })]                    // RemoveUIHighlight (13)
    public void SourceXPacketUnknownRegistrations_AreConsumed_AndTheNextPacketIsProcessed(byte[] packet) =>
        AssertFollowingPingSurvives(packet);

    [Theory]
    [InlineData(0x3F)]
    [InlineData(0x69)]
    [InlineData(0xA6)]
    [InlineData(0xD0)]
    [InlineData(0xE8)]
    [InlineData(0xEB)]
    public void SourceXRegisteredOpcodes_HaveAHandler(int opcode)
    {
        using var mgr = new NetworkManager(1, NullLoggerFactory.Instance);
        Assert.NotNull(mgr.Packets.GetHandler((byte)opcode));
    }

    /// <summary>Every fixed-length handler agrees with the framing table, so a handler
    /// never reads a body the framer cut differently.</summary>
    [Fact]
    public void FixedLengthHandlers_MatchTheFramingTable()
    {
        using var mgr = new NetworkManager(1, NullLoggerFactory.Instance);
        var wrong = new List<string>();
        for (int op = 0; op < 256; op++)
        {
            var h = mgr.Packets.GetHandler((byte)op);
            if (h == null || h.ExpectedLength <= 0 || op == 0x08)
                continue;
            int table = PacketDefinitions.GetPacketLength((byte)op);
            if (table != h.ExpectedLength)
                wrong.Add($"0x{op:X2}: handler {h.ExpectedLength}, table {table}");
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    // ---- N13: header minimum for variable packets ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void VariablePacket_DeclaringLessThanItsHeader_IsRejected(int declared)
    {
        // 0xF7 is variable and unregistered: before, a declared length of 1/2 was
        // "consumed" and the rest of its own header was parsed as the next packet.
        var (mgr, state) = Connection();
        int unknown = 0;
        mgr.OnUnknownPacket += (_, _, _) => unknown++;
        state.InjectReceived([0xF7, 0x00, (byte)declared, 0x73, 0x42]);
        Process(mgr, state);

        Assert.True(state.IsClosing);
        Assert.Equal(0, unknown);
    }

    [Fact]
    public void VariablePacket_DeclaringExactlyItsHeader_IsAccepted()
    {
        AssertFollowingPingSurvives([0xF7, 0x00, 0x03]);
    }

    // ---- N13: 0xEB hotbar reaches the game layer ----

    [Fact]
    public void UseHotbar_RoutesTypeAndArgument()
    {
        var (mgr, state) = Connection();
        byte type = 0;
        uint arg = 0;
        int calls = 0;
        state.UseToolbarHandler = (_, t, a) => { calls++; type = t; arg = a; };
        state.InjectReceived([0xEB, 0x00, 0x01, 0x00, 0x06, 0x04, 0x00, 0x40, 0x00, 0x12, 0x34, .. Ping]);
        Process(mgr, state);

        Assert.Equal(1, calls);
        Assert.Equal(4, type);
        Assert.Equal(0x40001234u, arg);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    // ---- N12: the filter call site ----

    [Fact]
    public void Filter_RunsForUnhandledPackets_AndCanSwallowThem()
    {
        var (mgr, state) = Connection();
        var seen = new List<byte[]>();
        int unknown = 0;
        mgr.OnUnknownPacket += (_, _, _) => unknown++;
        mgr.PacketScriptHook = (_, _, raw) => { seen.Add(raw); return true; };
        state.InjectReceived([0xF7, 0x00, 0x05, 0x01, 0x02]);
        Process(mgr, state);

        var raw = Assert.Single(seen);
        Assert.Equal(new byte[] { 0xF7, 0x00, 0x05, 0x01, 0x02 }, raw);
        Assert.Equal(0, unknown);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void Filter_ReturningFalse_LeavesUnhandledPacketToTheUnknownPath()
    {
        var (mgr, state) = Connection();
        int unknown = 0, filtered = 0;
        mgr.OnUnknownPacket += (_, _, _) => unknown++;
        mgr.PacketScriptHook = (_, _, _) => { filtered++; return false; };
        state.InjectReceived([0xF7, 0x00, 0x04, 0x01, .. Ping]);
        Process(mgr, state);

        Assert.Equal(2, filtered);   // 0xF7 and the ping
        Assert.Equal(1, unknown);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void Filter_ReturningTrue_CancelsCoreHandling()
    {
        var (mgr, state) = Connection();
        int warCalls = 0;
        state.WarModeHandler = (_, _) => warCalls++;
        mgr.PacketScriptHook = (_, op, _) => op == 0x72;
        state.InjectReceived([0x72, 0x01, 0x00, 0x32, 0x00, .. Ping]);
        Process(mgr, state);

        Assert.Equal(0, warCalls);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p => p.Span.SequenceEqual(Ping));
    }

    [Fact]
    public void Filter_Gate_SkipsOpcodesNoScriptHooks()
    {
        var (mgr, state) = Connection();
        var calls = new List<byte>();
        mgr.PacketScriptHook = (_, op, _) => { calls.Add(op); return false; };
        mgr.PacketScriptHookGate = op => op == 0x72;
        int warCalls = 0;
        state.WarModeHandler = (_, _) => warCalls++;
        state.InjectReceived([0x72, 0x01, 0x00, 0x32, 0x00, .. Ping]);
        Process(mgr, state);

        Assert.Equal(new byte[] { 0x72 }, calls);
        Assert.Equal(1, warCalls);
    }

    // ---- N12: output filter (CNetworkOutput.cpp:406) ----

    [Fact]
    public void OutputFilter_ReturningTrue_DropsThePacket()
    {
        var state = TestHarness.CreateActiveNetState(Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { }), 3);
        var seen = new List<byte[]>();
        state.OutPacketScriptHook = (_, raw) => { seen.Add(raw); return raw[0] == 0x73; };
        state.OutPacketScriptHookGate = op => op is 0x73 or 0x22;
        state.SendRaw([0x73, 0x01]);
        state.SendRaw([0x22, 0x00, 0x01]);
        state.SendRaw([0x55]);

        var queued = TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();
        Assert.DoesNotContain(queued, p => p[0] == 0x73);
        Assert.Contains(queued, p => p[0] == 0x22);
        Assert.Contains(queued, p => p[0] == 0x55);
        Assert.Equal(2, seen.Count); // 0x55 is not hooked
    }

    [Fact]
    public void OutputFilter_SendFromInsideTheFilter_DoesNotRecurse()
    {
        var state = TestHarness.CreateActiveNetState(Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { }), 4);
        int calls = 0;
        state.OutPacketScriptHook = (s, raw) =>
        {
            calls++;
            s.SendRaw([0x73, 0x09]); // a script answering with ARGO.SENDPACKET
            return true;
        };
        state.OutPacketScriptHookGate = op => op == 0x73;
        state.SendRaw([0x73, 0x01]);

        Assert.Equal(1, calls);
        var queued = TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray()).ToList();
        Assert.Equal(new byte[] { 0x73, 0x09 }, Assert.Single(queued));
    }

    // ---- N11: 0x6F body validation ----

    private static (int Gold, int Trade, uint G, uint P) Trade(byte[] body)
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        int goldCalls = 0, tradeCalls = 0;
        uint g = 99, p = 99;
        state.SecureTradeGoldHandler = (_, _, gold, plat) => { goldCalls++; g = gold; p = plat; };
        state.SecureTradeHandler = (_, _, _, _) => tradeCalls++;
        new PacketSecureTrade().OnReceive(new PacketBuffer(body), state);
        return (goldCalls, tradeCalls, g, p);
    }

    [Fact]
    public void SecureTrade_GoldUpdateWithoutBody_IsIgnored()
    {
        var r = Trade([0x03, 0x40, 0x00, 0x00, 0x01]);
        Assert.Equal(0, r.Gold);
        Assert.Equal(0, r.Trade);
    }

    [Fact]
    public void SecureTrade_GoldUpdateWithHalfABody_IsIgnored()
    {
        var r = Trade([0x03, 0x40, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05]);
        Assert.Equal(0, r.Gold);
    }

    [Fact]
    public void SecureTrade_ExplicitZeroOffer_StillApplies()
    {
        var r = Trade([0x03, 0x40, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(1, r.Gold);
        Assert.Equal(0u, r.G);
        Assert.Equal(0u, r.P);
    }

    [Fact]
    public void SecureTrade_ClassicUoGoldUpdate_Applies()
    {
        var r = Trade([0x03, 0x40, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0xF4, 0x00, 0x00, 0x00, 0x02]);
        Assert.Equal(1, r.Gold);
        Assert.Equal(500u, r.G);
        Assert.Equal(2u, r.P);
    }

    [Fact]
    public void SecureTrade_CloseAndCheck_KeepWorking()
    {
        Assert.Equal(1, Trade([0x01, 0x40, 0x00, 0x00, 0x01]).Trade);                // ClassicUO close: no param
        Assert.Equal(1, Trade([0x02, 0x40, 0x00, 0x00, 0x01, 0, 0, 0, 1]).Trade);    // check mark
    }

    [Fact]
    public void SecureTrade_MissingSession_IsIgnored()
    {
        var r = Trade([0x01, 0x40]);
        Assert.Equal(0, r.Trade);
    }
}
