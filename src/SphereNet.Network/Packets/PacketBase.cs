namespace SphereNet.Network.Packets;

/// <summary>
/// Base class for incoming packets (client → server).
/// Maps to Packet class in Source-X receive.h.
/// </summary>
public abstract class PacketHandler
{
    public byte PacketId { get; }
    public int ExpectedLength { get; }

    protected PacketHandler(byte packetId, int expectedLength)
    {
        PacketId = packetId;
        ExpectedLength = expectedLength;
    }

    /// <summary>
    /// Process the received packet. Called from NetworkInput after decryption.
    /// </summary>
    public abstract void OnReceive(PacketBuffer buffer, State.NetState state);
}

/// <summary>
/// Base class for outgoing packets (server → client).
/// Maps to PacketSend class in Source-X send.h.
/// </summary>
public abstract class PacketWriter
{
    public byte PacketId { get; }

    protected PacketWriter(byte packetId)
    {
        PacketId = packetId;
    }

    /// <summary>
    /// Build the packet into the buffer.
    /// </summary>
    public abstract PacketBuffer Build();

    /// <summary>
    /// Create a fixed-length packet buffer with the opcode written.
    /// </summary>
    protected PacketBuffer CreateFixed(int totalLength)
    {
        var buf = new PacketBuffer(totalLength);
        buf.WriteByte(PacketId);
        return buf;
    }

    /// <summary>
    /// Create a variable-length packet buffer with opcode and placeholder length.
    /// Call buf.WriteLengthAt(1) when done writing.
    /// </summary>
    protected PacketBuffer CreateVariable(int estimatedSize = 128)
    {
        var buf = new PacketBuffer(estimatedSize);
        buf.WriteByte(PacketId);
        buf.WriteUInt16(0); // placeholder for length
        return buf;
    }

    /// <summary>Return a minimal buffer flagged over-budget so the send path drops
    /// it (with <paramref name="reason"/>) instead of emitting content that would
    /// wrap an inner length field or force a huge client-side allocation. The
    /// opcode is present so the drop log names the packet.</summary>
    protected PacketBuffer RejectOversize(string reason)
    {
        var buf = new PacketBuffer(4);
        buf.WriteByte(PacketId);
        buf.MarkOversize(reason);
        return buf;
    }
}

/// <summary>Central size budgets for script-driven variable packets (gumps,
/// tooltips). A builder that would exceed one of these rejects the packet
/// (RejectOversize) rather than emit a frame that wraps an inner length field or
/// makes the client allocate an unbounded buffer. These sit under the absolute
/// 65535 wire ceiling that PacketBuffer.WriteLengthAt already enforces.</summary>
public static class PacketBudget
{
    /// <summary>Max ASCII layout bytes. Fits the 0xB0 ushort layout-length field
    /// and keeps real gumps (well under 64 KB) working.</summary>
    public const int MaxGumpLayoutBytes = 65535;
    /// <summary>Max text entries. Under the 0xB0 ushort count field.</summary>
    public const int MaxGumpTexts = 4096;
    /// <summary>Max characters per text entry. Under the ushort per-entry length
    /// field in both the 0xB0 and 0xDD forms.</summary>
    public const int MaxGumpTextChars = 16384;
    /// <summary>Max total DECOMPRESSED bytes (layout + text block). The 0xDD form
    /// compresses, so the wire size can be tiny while the client is told to
    /// allocate this much — cap it to stop a compression bomb.</summary>
    public const int MaxGumpDecompressedBytes = 512 * 1024;

    /// <summary>Max tooltip (OPL) properties per object.</summary>
    public const int MaxOplProperties = 512;
    /// <summary>Max characters in one OPL property's argument string. Under the
    /// ushort byte-length field (chars*2) so it cannot wrap.</summary>
    public const int MaxOplArgsChars = 8192;

    /// <summary>Validate a gump's sizes; returns a reason string if over budget,
    /// else null. <paramref name="textBlockBytes"/> is the uncompressed text-block
    /// size (2 + chars*2 per entry).</summary>
    public static string? CheckGump(int layoutBytes, IReadOnlyList<string> texts, long textBlockBytes)
    {
        if (layoutBytes > MaxGumpLayoutBytes)
            return $"gump layout {layoutBytes} bytes exceeds cap {MaxGumpLayoutBytes}";
        if (texts.Count > MaxGumpTexts)
            return $"gump text-entry count {texts.Count} exceeds cap {MaxGumpTexts}";
        for (int i = 0; i < texts.Count; i++)
            if (texts[i].Length > MaxGumpTextChars)
                return $"gump text entry {i} length {texts[i].Length} exceeds cap {MaxGumpTextChars}";
        long decompressed = layoutBytes + textBlockBytes;
        if (decompressed > MaxGumpDecompressedBytes)
            return $"gump decompressed size {decompressed} bytes exceeds cap {MaxGumpDecompressedBytes}";
        return null;
    }
}
