using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Three standard registrations this server was missing.
///
/// Upstream's registerStandardPackets carries 73 entries, 3 of them PacketUnknown, so
/// 70 that mean something. Nine of those had no counterpart here, and a client sending
/// one reached the unknown path: it asked and heard nothing back.
///
/// These three are the ones that route into work this server already does:
///   0xB6 the pre-AOS tooltip request, which upstream sends down the same path as the
///        newer 0xD6 (PacketToolTipReq -> Event_ToolTip, receive.cpp:2396);
///   0xE0 the in-game bug report, which reaches the same @UserBugReport trigger the
///        crash report does (receive.cpp:4273);
///   0xF1 the time sync question, answered immediately with 0xF2 carrying the current
///        time three times over (send.cpp:5223).
///
/// Of the rest, 0x3F (UltimaLive static update) and 0xE8 (remove UI highlight) are
/// no-ops upstream too - one dumps a debug line with its body commented out, the other
/// only skips its fields - so registering them would add nothing. 0x8D, 0xA7 and 0xF9
/// (KR character creation, tip-of-the-day, global chat) are covered by
/// EnhancedClientPacketTests; 0xEB (hotbar) is not covered here.
/// </summary>
public sealed class StandardPacketRegistrationTests
{
    private static NetworkManager Manager() =>
        new(1, TestHarness.CreateLoggerFactory());

    [Theory]
    [InlineData(0xB6)]
    [InlineData(0xE0)]
    [InlineData(0xF1)]
    public void TheOpcodeIsRegistered(int opcode)
    {
        using var network = Manager();
        Assert.Contains((byte)opcode, PacketManagerTests.GetRegisteredOpcodesFor(network));
    }

    /// <summary>The time sync answer: 25 bytes, the same stamp three times.</summary>
    [Fact]
    public void TheTimeSyncAnswerCarriesTheStampThreeTimes()
    {
        var buf = new SphereNet.Network.Packets.Outgoing.PacketTimeSyncResponse(
            0x0000_0123_4567_89ABL).Build();
        var span = buf.Span;

        Assert.Equal(25, span.Length);
        Assert.Equal(0xF2, span[0]);
        for (int i = 0; i < 3; i++)
        {
            int at = 1 + i * 8;
            long got = 0;
            for (int b = 0; b < 8; b++) got = (got << 8) | span[at + b];
            Assert.Equal(0x0000_0123_4567_89ABL, got);
        }
    }

    /// <summary>The pre-AOS request reaches the tooltip route, the same one 0xD6 uses.</summary>
    [Fact]
    public void TheOldTooltipRequestAsksForThatSerial()
    {
        uint asked = 0;
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance)
        {
            AOSTooltipHandler = (_, serial) => asked = serial
        };

        new PacketOldToolTipReq().OnReceive(
            new PacketBuffer([0x40, 0x00, 0x00, 0x07]), state);

        Assert.Equal(0x40000007u, asked);
    }

    /// <summary>A truncated one changes nothing rather than throwing.</summary>
    [Fact]
    public void ATruncatedOldTooltipRequestIsIgnored()
    {
        uint asked = 0;
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance)
        {
            AOSTooltipHandler = (_, serial) => asked = serial
        };

        var ex = Record.Exception(() =>
            new PacketOldToolTipReq().OnReceive(new PacketBuffer([0x40, 0x00]), state));

        Assert.Null(ex);
        Assert.Equal(0u, asked);
    }
}
