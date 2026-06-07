using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

public class SoundVisualParityTests
{
    [Fact]
    public void PacketEffectHued_BuildsSourceXLayout()
    {
        var packet = new PacketEffectHued(
            type: 3,
            srcSerial: 0x01020304,
            dstSerial: 0x05060708,
            effectId: 0x1234,
            srcX: 100,
            srcY: 200,
            srcZ: 5,
            dstX: 300,
            dstY: 400,
            dstZ: 6,
            speed: 7,
            duration: 8,
            fixedDir: true,
            explode: false,
            hue: 0x0045,
            renderMode: 0x00000009);

        var span = packet.Build().Span;

        Assert.Equal(36, span.Length);
        Assert.Equal(0xC0, span[0]);
        Assert.Equal(3, span[1]);
        Assert.Equal(0x01020304u, ReadUInt32(span, 2));
        Assert.Equal(0x05060708u, ReadUInt32(span, 6));
        Assert.Equal(0x1234, ReadUInt16(span, 10));
        Assert.Equal(7, span[22]);
        Assert.Equal(8, span[23]);
        Assert.Equal(1, span[26]);
        Assert.Equal(0, span[27]);
        Assert.Equal(0x0044u, ReadUInt32(span, 28));
        Assert.Equal(0x00000009u, ReadUInt32(span, 32));
    }

    [Fact]
    public void PacketEffectParticle_BuildsSourceXLayout()
    {
        var packet = new PacketEffectParticle(
            type: 1,
            srcSerial: 0x01000001,
            dstSerial: 0x01000002,
            effectId: 0x0F0F,
            srcX: 10,
            srcY: 20,
            srcZ: 1,
            dstX: 30,
            dstY: 40,
            dstZ: 2,
            speed: 5,
            duration: 6,
            fixedDir: true,
            explode: true,
            hue: 0,
            renderMode: 0x00000002,
            particleEffectId: 0x1111,
            explodeId: 0x2222,
            explodeSound: 0x3333,
            effectUid: 0x44444444,
            particleType: 0);

        var span = packet.Build().Span;

        Assert.Equal(49, span.Length);
        Assert.Equal(0xC7, span[0]);
        Assert.Equal(1, span[1]);
        Assert.Equal(1, span[26]);
        Assert.Equal(1, span[27]);
        Assert.Equal(0u, ReadUInt32(span, 28));
        Assert.Equal(2u, ReadUInt32(span, 32));
        Assert.Equal(0x1111, ReadUInt16(span, 36));
        Assert.Equal(0x2222, ReadUInt16(span, 38));
        Assert.Equal(0x3333, ReadUInt16(span, 40));
        Assert.Equal(0x44444444u, ReadUInt32(span, 42));
        Assert.Equal(0xFF, span[46]);
        Assert.Equal(0, ReadUInt16(span, 47));
    }

    [Fact]
    public void PacketDragAnimation_BuildsSourceXLayout()
    {
        var packet = new PacketDragAnimation(
            itemId: 0x0F7A,
            hue: 0x0044,
            amount: 3,
            sourceSerial: 0x40000001,
            sourceX: 100,
            sourceY: 200,
            sourceZ: 5,
            targetSerial: 0,
            targetX: 101,
            targetY: 201,
            targetZ: 6);

        var span = packet.Build().Span;

        Assert.Equal(26, span.Length);
        Assert.Equal(0x23, span[0]);
        Assert.Equal(0x0F7A, ReadUInt16(span, 1));
        Assert.Equal(0, span[3]);
        Assert.Equal(0x0044, ReadUInt16(span, 4));
        Assert.Equal(3, ReadUInt16(span, 6));
        Assert.Equal(0x40000001u, ReadUInt32(span, 8));
        Assert.Equal(100, ReadUInt16(span, 12));
        Assert.Equal(200, ReadUInt16(span, 14));
        Assert.Equal(5, span[16]);
        Assert.Equal(0u, ReadUInt32(span, 17));
        Assert.Equal(101, ReadUInt16(span, 21));
        Assert.Equal(201, ReadUInt16(span, 23));
        Assert.Equal(6, span[25]);
    }

    [Fact]
    public void ObjBaseSoundCommand_BroadcastsSoundPacket()
    {
        PacketWriter? sent = null;
        ObjBase.BroadcastNearby = (_, _, packet, _) => sent = packet;
        try
        {
            var item = new Item
            {
                Position = new Point3D(10, 20, 3, 0)
            };
            item.UidRef = Serial.NewItem(1);

            Assert.True(item.TryExecuteCommand("SOUND", "0x0042,2", null!));

            var span = Assert.IsType<PacketSound>(sent).Build().Span;
            Assert.Equal(0x54, span[0]);
            Assert.Equal(2, span[1]);
            Assert.Equal(0x0042, ReadUInt16(span, 2));
            Assert.Equal(10, ReadUInt16(span, 6));
            Assert.Equal(20, ReadUInt16(span, 8));
            Assert.Equal(3, ReadUInt16(span, 10));
        }
        finally
        {
            ObjBase.BroadcastNearby = null;
        }
    }

    [Fact]
    public void ObjBaseEffectCommand_SelectsParticlePacketWhenParticleArgsPresent()
    {
        PacketWriter? sent = null;
        ObjBase.BroadcastNearby = (_, _, packet, _) => sent = packet;
        try
        {
            var item = new Item
            {
                Position = new Point3D(10, 20, 3, 0)
            };
            item.UidRef = Serial.NewItem(2);

            Assert.True(item.TryExecuteCommand("EFFECT", "1,0x0F0F,5,6,1,0,2,0x1111,0x2222", null!));

            var span = Assert.IsType<PacketEffectParticle>(sent).Build().Span;
            Assert.Equal(0xC7, span[0]);
            Assert.Equal(1, span[1]);
            Assert.Equal(0x0F0F, ReadUInt16(span, 10));
            Assert.Equal(5, span[22]);
            Assert.Equal(6, span[23]);
            Assert.Equal(0x1111, ReadUInt16(span, 36));
            Assert.Equal(0x2222, ReadUInt16(span, 38));
        }
        finally
        {
            ObjBase.BroadcastNearby = null;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, int offset)
        => (ushort)((span[offset] << 8) | span[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset)
        => ((uint)span[offset] << 24)
           | ((uint)span[offset + 1] << 16)
           | ((uint)span[offset + 2] << 8)
           | span[offset + 3];
}
