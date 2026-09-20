namespace SphereNet.Network.Packets.Outgoing;

/// <summary>Source-X PacketStatueAnimation: 0xBF / 0x19 / type 5.</summary>
public sealed class PacketStatueAnimation(uint serial, ushort animation, ushort frame) : PacketWriter(0xBF)
{
    public override PacketBuffer Build()
    {
        var buf = CreateVariable(17);
        buf.WriteUInt16(0x19);
        buf.WriteByte(5);
        buf.WriteUInt32(serial);
        buf.WriteByte(0);
        buf.WriteByte(0xFF);
        buf.WriteByte(1);
        buf.WriteUInt16(animation);
        buf.WriteUInt16(frame);
        buf.WriteLengthAt(1);
        return buf;
    }
}
