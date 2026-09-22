using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// 0x71 request sub-commands, as sphereproto.h numbers them and ClassicUO sends
/// them: 3 = BBOARDF_REQ_FULL (the whole message, answered with sub 2), 4 =
/// BBOARDF_REQ_HEAD (just the header, answered with sub 1). They were wired the
/// other way round, so a board answered a header request with the full body and a
/// read request with a bare header.
/// </summary>
public sealed class BulletinBoardSubcommandTests
{
    private static byte[] Payload(byte sub, uint board, uint msg) =>
    [
        sub,
        (byte)(board >> 24), (byte)(board >> 16), (byte)(board >> 8), (byte)board,
        (byte)(msg >> 24), (byte)(msg >> 16), (byte)(msg >> 8), (byte)msg,
    ];

    [Theory]
    [InlineData((byte)3, "full")]
    [InlineData((byte)4, "head")]
    public void RequestSubcommandsFollowTheProtocol(byte sub, string expected)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);
        string? seen = null;
        state.BulletinBoardRequestHeadHandler = (_, _, _) => seen = "head";
        state.BulletinBoardRequestMessageHandler = (_, _, _) => seen = "full";

        new PacketBulletinBoard().OnReceive(new PacketBuffer(Payload(sub, 0x40006A87, 0x40037DCF)), state);

        Assert.Equal(expected, seen);
    }
}
