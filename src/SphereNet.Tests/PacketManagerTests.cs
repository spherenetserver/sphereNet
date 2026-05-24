using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class PacketManagerTests
{
    private sealed class TestPacketHandler(byte packetId) : PacketHandler(packetId, 0)
    {
        public bool Received { get; private set; }

        public override void OnReceive(PacketBuffer buffer, NetState state)
        {
            Received = true;
        }
    }

    [Fact]
    public void PacketManager_RegistersStandardExtendedAndEncodedHandlers()
    {
        var manager = new PacketManager();
        var standard = new TestPacketHandler(0x02);
        var extended = new TestPacketHandler(0xBF);
        var encoded = new TestPacketHandler(0xD7);

        manager.Register(standard);
        manager.RegisterExtended(0x001C, extended);
        manager.RegisterEncoded(0x0001, encoded);

        Assert.Same(standard, manager.GetHandler(0x02));
        Assert.Same(extended, manager.GetExtendedHandler(0x001C));
        Assert.Same(encoded, manager.GetEncodedHandler(0x0001));
        Assert.Null(manager.GetExtendedHandler(0xFFFF));
        Assert.Null(manager.GetEncodedHandler(0xFFFF));
    }

    [Fact]
    public void PacketBuffer_ReadPastEnd_SetsUnderrunFlag()
    {
        var buffer = new PacketBuffer([0x01]);

        Assert.Equal((ushort)0, buffer.ReadUInt16());
        Assert.True(buffer.IsUnderrun);
    }

    [Fact]
    public void PacketProfileRequest_TruncatedBio_DoesNotOverread()
    {
        var handler = new PacketProfileRequest();
        var buffer = new PacketBuffer([0x01, 0, 0, 0, 1, 0, 1, 0, 10]);
        var state = new NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger<NetState>.Instance);
        bool invoked = false;
        state.ProfileRequestHandler = (_, mode, serial, bio) =>
        {
            invoked = true;
            Assert.Equal((byte)1, mode);
            Assert.Equal("", bio);
        };

        handler.OnReceive(buffer, state);

        Assert.True(invoked);
        Assert.False(buffer.IsUnderrun);
    }
}
