using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// A packet that declares how many entries follow declares the truth.
///
/// This is the mistake that crashes a client rather than confusing it. The client reads
/// the count and then reads that many fixed-size entries; if the count is larger than
/// what was written, it reads past the end of the packet - into the next packet, or off
/// the buffer. A count that is too small is milder but still wrong: the trailing bytes
/// are parsed as the start of another packet and the stream desyncs.
///
/// Two static sweeps for this pattern produced nothing but noise - a serial or a hue
/// written before a loop looks exactly like a count to a regular expression - so this
/// asks the packets instead: build each with a known number of entries and read the
/// declared count back off the wire.
/// </summary>
public sealed class ListPacketCountTests
{
    private static ReadOnlySpan<byte> Wire(PacketWriter p) => p.Build().Span;

    private static int BigEndian16(ReadOnlySpan<byte> s, int at) => (s[at] << 8) | s[at + 1];

    private static PacketContainerContents.Entry Item(uint serial) =>
        new(serial, 0x0EED, 0, 1, 10, 20, 0x40000001, 0, 0);

    private static VendorItem Ware(uint serial) => new()
    {
        Serial = serial, ItemId = 0x0EED, Hue = 0, Amount = 1, Price = 5, Name = "thing"
    };

    /// <summary>0x3C: count is a big-endian word after the length.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void ContainerContentsDeclaresWhatItWrote(int n)
    {
        var items = Enumerable.Range(0, n).Select(i => Item(0x40001000u + (uint)i)).ToList();
        var wire = Wire(new PacketContainerContents(items));

        Assert.Equal(0x3C, wire[0]);
        Assert.Equal(n, BigEndian16(wire, 3));
        // And the body is exactly that many entries: 20 bytes each with the grid byte.
        Assert.Equal(5 + n * 20, BigEndian16(wire, 1));
        Assert.Equal(BigEndian16(wire, 1), wire.Length);
    }

    /// <summary>0x74: count is one byte after the container serial.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void VendorBuyListDeclaresWhatItWrote(int n)
    {
        var wares = Enumerable.Range(0, n).Select(i => Ware(0x40002000u + (uint)i)).ToList();
        var wire = Wire(new PacketVendorBuyList(0x40000001, wares));

        Assert.Equal(0x74, wire[0]);
        Assert.Equal(n, wire[7]);
        Assert.Equal(BigEndian16(wire, 1), wire.Length);
    }

    /// <summary>0x9E: same shape, sell side.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void VendorSellListDeclaresWhatItWrote(int n)
    {
        var wares = Enumerable.Range(0, n).Select(i => Ware(0x40003000u + (uint)i)).ToList();
        var wire = Wire(new PacketVendorSellList(0x40000002, wares));

        Assert.Equal(0x9E, wire[0]);
        Assert.Equal(n, BigEndian16(wire, 7));
        Assert.Equal(BigEndian16(wire, 1), wire.Length);
    }

    /// <summary>0x89: the corpse's worn layers, terminated rather than counted - so the
    /// check is that the terminator is there and the length agrees.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void CorpseEquipmentEndsWhereItSaysItDoes(int n)
    {
        var entries = Enumerable.Range(0, n)
            .Select(i => ((byte)(i + 1), 0x40004000u + (uint)i)).ToList();
        var wire = Wire(new PacketCorpseEquipment(0x40000003, entries));

        Assert.Equal(0x89, wire[0]);
        Assert.Equal(BigEndian16(wire, 1), wire.Length);
        Assert.Equal(0x00, wire[^1]);           // the list terminator
    }

    /// <summary>0x7C: the old menu, whose count the client uses to size its own list.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void MenuDisplayDeclaresWhatItWrote(int n)
    {
        var items = Enumerable.Range(0, n)
            .Select(i => new MenuItemEntry(0x0EED, 0, "line " + i)).ToList();
        var wire = Wire(new PacketMenuDisplay(0x40000004, 1, "pick one", items));

        Assert.Equal(0x7C, wire[0]);
        Assert.Equal(BigEndian16(wire, 1), wire.Length);
        // The count sits after serial(4), id(2) and the length-prefixed question.
        int at = 3 + 4 + 2;
        int questionLen = wire[at];
        Assert.Equal(n, wire[at + 1 + questionLen]);
    }

    /// <summary>The length field is the other half of the same promise: a packet whose
    /// header says one size and whose body is another desyncs the stream whatever the
    /// counts say.</summary>
    [Fact]
    public void EveryListPacketsLengthMatchesItsBody()
    {
        var packets = new PacketWriter[]
        {
            new PacketContainerContents([Item(0x40001001), Item(0x40001002)]),
            new PacketVendorBuyList(0x40000001, [Ware(0x40002001)]),
            new PacketVendorSellList(0x40000002, [Ware(0x40003001)]),
            new PacketCorpseEquipment(0x40000003, [((byte)1, 0x40004001u)]),
            new PacketMenuDisplay(0x40000004, 1, "q", [new MenuItemEntry(1, 0, "a")]),
        };

        foreach (var p in packets)
        {
            var wire = Wire(p);
            Assert.Equal(BigEndian16(wire, 1), wire.Length);
        }
    }
}
