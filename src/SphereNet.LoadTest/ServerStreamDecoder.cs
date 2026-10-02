using SphereNet.Network.Encryption;

namespace SphereNet.LoadTest;

/// <summary>
/// Streaming decoder for the game connection's server-to-client stream.
///
/// The server Huffman-compresses every packet on its own and ends it with the
/// terminator code, padded to a byte boundary. Following the stream bit by bit across
/// socket reads therefore yields packet boundaries without a packet-length table - and
/// a length table is exactly what goes stale with every client-version-dependent
/// packet, which would desynchronise the bot instead of measuring the server.
/// </summary>
internal sealed class ServerStreamDecoder
{
    private const int Terminator = 256;
    private static readonly int[,] s_tree = BuildTree();

    private byte[] _packet = new byte[4096];
    private int _length;
    private int _node;

    /// <summary>Feed raw socket bytes; <paramref name="onPacket"/> runs once per
    /// complete packet, with a span that is only valid during the call.</summary>
    public void Feed(ReadOnlySpan<byte> data, PacketHandler onPacket)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int b = data[i];
            for (int bit = 7; bit >= 0; bit--)
            {
                int next = s_tree[_node, (b >> bit) & 1];
                if (next > 0)
                {
                    _node = next;
                    continue;
                }
                int value = -next - 1;
                _node = 0;
                if (value == Terminator)
                {
                    if (_length > 0)
                        onPacket(_packet.AsSpan(0, _length));
                    _length = 0;
                    break; // the rest of this byte is padding
                }
                if (_length == _packet.Length)
                    Array.Resize(ref _packet, _packet.Length * 2);
                _packet[_length++] = (byte)value;
            }
        }
    }

    public delegate void PacketHandler(ReadOnlySpan<byte> packet);

    private static int[,] BuildTree()
    {
        // Node 0 is the root; children are node indexes (> 0) or leaves stored as
        // -(value + 1). Built from the same table the server encodes with.
        var table = HuffmanCompression.ServerCodeTable;
        var tree = new int[1024, 2];
        int nextNode = 1;
        for (int value = 0; value <= Terminator; value++)
        {
            int entry = table[value];
            int bits = entry & 0xF;
            int code = entry >> 4;
            int node = 0;
            for (int i = bits - 1; i >= 0; i--)
            {
                int bit = (code >> i) & 1;
                if (i == 0)
                {
                    tree[node, bit] = -(value + 1);
                }
                else
                {
                    if (tree[node, bit] == 0)
                        tree[node, bit] = nextNode++;
                    node = tree[node, bit];
                }
            }
        }
        return tree;
    }
}
