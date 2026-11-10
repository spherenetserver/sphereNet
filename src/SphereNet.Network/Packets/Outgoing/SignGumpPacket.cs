namespace SphereNet.Network.Packets.Outgoing;

/// <summary>0x8B - a gump with text on it, as a sign shows (Source-X PacketSignGump,
/// send.cpp:2768-2799; CClient::addGumpTextDisp). The object's serial, the gump
/// graphic, then two strings each written as a 16-bit length that counts the
/// terminating NUL followed by that many ASCII bytes; a string that is absent is a
/// zero length and nothing else.</summary>
public sealed class PacketSignGump(uint serial, ushort gump, string? name, string? text) : PacketWriter(0x8B)
{
    public override PacketBuffer Build()
    {
        var buf = CreateVariable(13 + (name?.Length ?? 0) + (text?.Length ?? 0) + 2);
        buf.WriteUInt32(serial);
        buf.WriteUInt16(gump);
        WriteString(buf, name);
        WriteString(buf, text);
        buf.WriteLengthAt(1);
        return buf;
    }

    private static void WriteString(PacketBuffer buf, string? value)
    {
        if (value == null)
        {
            buf.WriteUInt16(0);
            return;
        }
        int len = value.Length + 1;
        buf.WriteUInt16((ushort)len);
        buf.WriteAsciiFixed(value, len);
    }
}
