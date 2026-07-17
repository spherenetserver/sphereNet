using System.Text;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 269 — uncompressed 0xB0 gump fallback. Source-X send.cpp:3617 picks the
/// compressed 0xDD gump for modern clients and the plain 0xB0 form for very old
/// (pre-3.0) clients. SphereNet always sent 0xDD; this adds the 0xB0 writer, routed
/// only to explicitly-old clients so the modern path is untouched. (The rest of
/// backlog 5.5 was a misconception — the gump layout engine + every dedicated
/// system gump already exist, and dialogs are script-driven in Source-X too.)
/// </summary>
public sealed class SourceXWave269Tests
{
    private static uint ReadU32(byte[] d, ref int p)
    {
        uint v = (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        p += 4;
        return v;
    }

    private static int ReadU16(byte[] d, ref int p)
    {
        int v = (d[p] << 8) | d[p + 1];
        p += 2;
        return v;
    }

    [Fact]
    public void PacketGumpDialogStandard_0xB0_WritesUncompressedLayoutAndUnicodeText()
    {
        string layout = "{ page 0 }{ text 10 10 0 0 }";
        var texts = new[] { "Hello", "World!" };

        byte[] d = new PacketGumpDialogStandard(0x12345678u, 0x0BADF00Du, 100, 60, layout, texts)
            .Build().Data;

        Assert.Equal(0xB0, d[0]);

        int pos = 3; // skip opcode + 2-byte length
        Assert.Equal(0x12345678u, ReadU32(d, ref pos)); // serial
        Assert.Equal(0x0BADF00Du, ReadU32(d, ref pos)); // gumpId
        Assert.Equal(100u, ReadU32(d, ref pos));        // x
        Assert.Equal(60u, ReadU32(d, ref pos));         // y

        int cmdLen = ReadU16(d, ref pos);
        Assert.Equal(layout.Length, cmdLen);
        Assert.Equal(layout, Encoding.ASCII.GetString(d, pos, cmdLen)); // uncompressed, not zlib
        pos += cmdLen;

        Assert.Equal(2, ReadU16(d, ref pos)); // text count

        int len0 = ReadU16(d, ref pos);
        Assert.Equal(5, len0);
        var sb = new StringBuilder();
        for (int i = 0; i < len0; i++)
        {
            sb.Append((char)((d[pos] << 8) | d[pos + 1])); // UTF-16 BE
            pos += 2;
        }
        Assert.Equal("Hello", sb.ToString());
    }
}
