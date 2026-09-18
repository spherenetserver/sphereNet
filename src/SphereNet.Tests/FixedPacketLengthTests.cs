using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// A fixed-length packet must be the length the client reads it as.
///
/// The client parses a fixed-length opcode by a table, not by anything in the packet,
/// so a server that writes fewer bytes than the table says does not merely send a
/// malformed packet - it moves every byte after it. The stream is read at the wrong
/// offset from that point on and nothing later arrives intact.
///
/// That is what a refused drop did: it was sent as 0x28 with two bytes, and 0x28 is
/// five in the client's table and is never adjusted for any client version. A player
/// whose deposit was refused saw the item vanish and then could not move, because the
/// packets that would have said otherwise were being parsed as garbage. 0x27 is the
/// two-byte packet that means "put back what you are holding", and its handler does
/// exactly that.
/// </summary>
public sealed class FixedPacketLengthTests
{
    /// <summary>A refused move is 0x27, two bytes: opcode and reason.</summary>
    [Fact]
    public void ARefusedMoveIsTheTwoByteRejectPacket()
    {
        var bytes = new PacketDropReject(5).Build().Span.ToArray();

        Assert.Equal(0x27, bytes[0]);
        Assert.Equal(2, bytes.Length);
        Assert.Equal(5, bytes[1]);
    }

    /// <summary>And a refused pickup is the same packet, which is what it has always
    /// been - the two now agree rather than one of them inventing an opcode.</summary>
    [Fact]
    public void ARefusedPickupIsTheSamePacket()
    {
        var bytes = new PacketPickupFailed(0).Build().Span.ToArray();

        Assert.Equal(0x27, bytes[0]);
        Assert.Equal(2, bytes.Length);
    }

    /// <summary>The lengths the modern client is calibrated to, which differ from the
    /// legacy table and must not be "corrected" back to it: a container opens with the
    /// 7.0.9 type byte, a container line carries the 6.0.1.7 grid byte, and the feature
    /// flags are the 6.0.14 32-bit form.</summary>
    [Fact]
    public void TheVersionedPacketsKeepTheirModernLengths()
    {
        Assert.Equal(9, new PacketOpenContainer(0x40000001, 0x003C).Build().Span.Length);
        Assert.Equal(21, new PacketContainerItem(
            0x40000002, 0x0EED, 0, 1, 10, 10, 0x40000001, 0, true).Build().Span.Length);
        Assert.Equal(20, new PacketContainerItem(
            0x40000002, 0x0EED, 0, 1, 10, 10, 0x40000001, 0, false).Build().Span.Length);
    }
}
