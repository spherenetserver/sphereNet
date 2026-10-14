using SphereNet.Network.Encryption;

namespace SphereNet.Tests;

/// <summary>
/// A reader that receives the server's compressed stream in TCP-sized pieces has to
/// know whether a piece held a whole packet. Decoding one that stopped mid-packet and
/// taking all of it threw away the bits of the code cut at its end, so the rest of the
/// packet decoded from the wrong bit (the bots' stream lost its packet boundaries).
/// </summary>
public sealed class HuffmanPartialInputTests
{
    private static byte[] Compress(byte[] packet) => HuffmanCompression.Compress(packet, 0, packet.Length);

    [Fact]
    public void OnlyAWholePacketIsComplete()
    {
        var packet = new byte[300];
        new Random(11).NextBytes(packet);
        var compressed = Compress(packet);

        for (int cut = 1; cut < compressed.Length; cut++)
        {
            HuffmanCompression.DecompressFromServer(compressed, 0, cut, out _, out bool partComplete);
            Assert.False(partComplete, $"a cut at {cut} of {compressed.Length} bytes read as complete");
        }

        var whole = HuffmanCompression.DecompressFromServer(compressed, 0, compressed.Length, out int used, out bool complete);
        Assert.True(complete);
        Assert.Equal(compressed.Length, used);
        Assert.Equal(packet, whole);
    }

    [Fact]
    public void TwoPacketsSplitAnywhereDecodeIntact()
    {
        var rng = new Random(5);
        var first = new byte[120];
        var second = new byte[80];
        rng.NextBytes(first);
        rng.NextBytes(second);
        var stream = Compress(first).Concat(Compress(second)).ToArray();

        for (int cut = 1; cut < stream.Length; cut++)
        {
            // Feed the stream in two pieces, keeping a partial packet until more arrives.
            var buffer = new List<byte>(stream.Take(cut));
            var output = new List<byte>();
            bool fedRest = false;
            while (true)
            {
                var arr = buffer.ToArray();
                int used = 0;
                bool complete = false;
                byte[] bytes = arr.Length == 0 ? [] : HuffmanCompression.DecompressFromServer(arr, 0, arr.Length, out used, out complete);
                if (complete)
                {
                    output.AddRange(bytes);
                    buffer.RemoveRange(0, used);
                    continue;
                }
                if (fedRest) break;
                buffer.AddRange(stream.Skip(cut));
                fedRest = true;
            }
            Assert.Equal(first.Concat(second).ToArray(), output.ToArray());
        }
    }
}
