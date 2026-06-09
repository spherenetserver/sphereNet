using System.IO.Compression;

namespace SphereNet.Network.Packets;

/// <summary>
/// RFC 1950 zlib stream helper for compressed packet payloads
/// (0xDD compressed gump, 0xD8 custom house design).
/// </summary>
public static class ZlibUtil
{
    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        // zlib header: CMF=0x78, FLG=0x9C (deflate, default compression)
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        uint adler = Adler32(data);
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)(adler & 0xFF));
        return output.ToArray();
    }

    /// <summary>Inverse of <see cref="Compress"/> — skips the 2-byte zlib
    /// header and inflates. Used by round-trip tests.</summary>
    public static byte[] Decompress(byte[] data, int offset, int length)
    {
        using var input = new MemoryStream(data, offset + 2, length - 2);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    public static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
