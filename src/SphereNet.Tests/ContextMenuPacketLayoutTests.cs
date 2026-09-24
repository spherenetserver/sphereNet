using System.Buffers.Binary;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>0xBF.0x14 in both of upstream's layouts (PacketDisplayPopup, send.cpp:4130).
/// The packet announced format 1 and wrote a 4-byte cliloc, which no client parses,
/// so every context-menu line came out shifted.</summary>
public sealed class ContextMenuPacketLayoutTests
{
    [Fact]
    public void FormatTwoWritesClilocThenTagThenFlags()
    {
        var span = new PacketContextMenu(0x1234, [(3, 3006103, 0x20)], newFormat: true).Build().Span;
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(span[5..]));
        Assert.Equal(1, span[11]);
        Assert.Equal(3006103u, BinaryPrimitives.ReadUInt32BigEndian(span[12..]));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(span[16..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(span[18..])); // no colour word follows
        Assert.Equal(20, span.Length);
    }

    [Fact]
    public void FormatOneWritesTagThenShortClilocThenFlags()
    {
        var span = new PacketContextMenu(0x1234, [(3, 3006103, 0)], newFormat: false).Build().Span;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(span[5..]));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(span[12..]));
        Assert.Equal(6103, BinaryPrimitives.ReadUInt16BigEndian(span[14..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(span[16..]));
        Assert.Equal(18, span.Length);
    }
}
