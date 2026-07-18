using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// B7: multi bodies must reach the client marked as multis, otherwise ClassicUO renders
/// them as a plain static tile and (for customizable foundations) silently drops the 0xD8
/// design stream. 0x1A carries multi-ness in the graphic range (>= 0x4000); 0xF3 carries it
/// in an explicit data-type byte (2 = multi). The stored id stays the raw multi index.
/// </summary>
public sealed class MultiWirePacketTests
{
    private const ushort RawMultiId = 0x0064; // a raw multi.mul block index (< 0x4000)

    // 0x1A: [0]=id, [1..2]=length, [3..6]=serial, [7..8]=graphic (big-endian).
    [Fact]
    public void WorldItem0x1A_SetsMultiBit_OnlyWhenMulti()
    {
        var multi = new PacketWorldItem(0x40000001, RawMultiId, 1, 100, 100, 0, 0, isMulti: true).Build();
        Assert.Equal(0x1A, multi.Data[0]);
        // graphic == RawMultiId | 0x4000 == 0x4064 → high byte carries the 0x40 multi bit.
        Assert.Equal(0x40, multi.Data[7]);
        Assert.Equal(0x64, multi.Data[8]);

        var plain = new PacketWorldItem(0x40000001, RawMultiId, 1, 100, 100, 0, 0, isMulti: false).Build();
        Assert.Equal(0x00, plain.Data[7]); // raw id, no multi bit
        Assert.Equal(0x64, plain.Data[8]);
    }

    // 0xF3 (fixed): [0]=id, [1..2]=0x0001, [3]=data type (0=item, 2=multi).
    [Fact]
    public void WorldItemSA0xF3_SetsDataTypeTwo_OnlyWhenMulti()
    {
        var multi = new PacketWorldItemSA(0x40000001, RawMultiId, 1, 100, 100, 0, 0, isMulti: true).Build();
        Assert.Equal(0xF3, multi.Data[0]);
        Assert.Equal(0x02, multi.Data[3]); // data type = multi

        var plain = new PacketWorldItemSA(0x40000001, RawMultiId, 1, 100, 100, 0, 0, isMulti: false).Build();
        Assert.Equal(0x00, plain.Data[3]); // data type = item
    }
}
