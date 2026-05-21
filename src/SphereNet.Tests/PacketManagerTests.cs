using SphereNet.Network.Packets;
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
}
