using SphereNet.Network.Encryption;

namespace SphereNet.Tests;

/// <summary>
/// The send path compresses each unicast packet straight into the connection's send
/// buffer (<see cref="HuffmanCompression.CompressInto"/>) instead of building a new
/// array per packet. The bytes on the wire must not change: they are checked against
/// the previous array-returning implementation, kept here as the reference.
/// </summary>
public sealed class HuffmanCompressIntoTests
{
    /// <summary>The implementation the send path used before (Source-X CHuffman::Compress).</summary>
    private static byte[] Reference(byte[] input)
    {
        var table = HuffmanCompression.ServerCodeTable;
        var output = new List<byte>();
        byte bitIdx = 0;
        byte xOutVal = 0;
        for (int i = 0; i <= input.Length; i++)
        {
            ushort value = table[i == input.Length ? 256 : input[i]];
            byte nBits = (byte)(value & 0xF);
            value >>= 4;
            while (nBits > 0)
            {
                nBits--;
                xOutVal = (byte)((uint)xOutVal << 1);
                xOutVal |= (byte)(((uint)value >> nBits) & 0x1u);
                if (++bitIdx == 8)
                {
                    bitIdx = 0;
                    output.Add(xOutVal);
                }
            }
        }
        if (bitIdx > 0)
            output.Add((byte)((uint)xOutVal << (8 - bitIdx)));
        return [.. output];
    }

    [Fact]
    public void TheWireBytesAreUnchanged()
    {
        var rng = new Random(1234);
        for (int n = 0; n < 500; n++)
        {
            var packet = new byte[rng.Next(0, 3000)];
            rng.NextBytes(packet);
            var output = new byte[HuffmanCompression.MaxCompressedLength(packet.Length)];

            int written = HuffmanCompression.CompressInto(packet, output);

            Assert.Equal(Reference(packet), output.AsSpan(0, written).ToArray());
            Assert.Equal(Reference(packet), HuffmanCompression.Compress(packet, 0, packet.Length));
        }
    }

    [Fact]
    public void TheBoundHoldsForTheLongestCodes()
    {
        var table = HuffmanCompression.ServerCodeTable.ToArray();
        int longest = Enumerable.Range(0, 256).MaxBy(b => table[b] & 0xF);
        var packet = Enumerable.Repeat((byte)longest, 5000).ToArray();

        int written = HuffmanCompression.CompressInto(packet, new byte[HuffmanCompression.MaxCompressedLength(packet.Length)]);

        Assert.True(written <= HuffmanCompression.MaxCompressedLength(packet.Length));
        Assert.Equal(packet, HuffmanCompression.DecompressFromServer(
            Reference(packet), 0, written, out _));
    }

    [Fact]
    public void CompressIntoAllocatesNothing()
    {
        var packet = new byte[600];
        new Random(7).NextBytes(packet);
        var output = new byte[HuffmanCompression.MaxCompressedLength(packet.Length)];
        HuffmanCompression.CompressInto(packet, output); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            HuffmanCompression.CompressInto(packet, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ATooSmallOutputIsRefused()
    {
        Assert.Throws<ArgumentException>(() => HuffmanCompression.CompressInto(new byte[10], new byte[5]));
    }
}
