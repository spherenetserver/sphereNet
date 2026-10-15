using System.Buffers.Binary;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// How the reference 2D client frames and reads what the server sends, for one client
/// version. A server packet the client frames at another length than the server wrote
/// misaligns every packet after it on that connection; a version-dependent body the
/// client reads in the other layout loses its fields. Neither shows on the server or
/// in its tests, which build and read packets with the server's own code.
///
/// The length table and its version gates are taken from ClassicUO
/// (Network/PacketsTable.cs, Utility/ClientVersion.cs); the body layouts from its
/// packet handlers (0x78 UpdateObject, 0xA9 in the login scene). Versions are packed
/// as the client packs them: major &lt;&lt; 24 | minor &lt;&lt; 16 | build &lt;&lt; 8 | extra.
/// </summary>
public static class ReferenceClientProtocol
{
    public static uint Pack(int major, int minor, int build, int extra) =>
        (uint)((major << 24) | (minor << 16) | (build << 8) | extra);

    /// <summary>The server's number for the same version (major * 10,000,000 +
    /// minor * 1,000,000 + build * 1,000 + extra), as NetState keeps it.</summary>
    public static uint ToServerNumber(uint packed) =>
        (packed >> 24) * 10_000_000 + ((packed >> 16) & 0xFF) * 1_000_000 + ((packed >> 8) & 0xFF) * 1_000 + (packed & 0xFF);

    public static readonly uint V500A = Pack(5, 0, 0, 'a');
    public static readonly uint V5090 = Pack(5, 0, 9, 0);
    public static readonly uint V6013 = Pack(6, 0, 1, 3);
    public static readonly uint V6017 = Pack(6, 0, 1, 8); // the reference client's own threshold
    public static readonly uint V6060 = Pack(6, 0, 6, 0);
    public static readonly uint V60142 = Pack(6, 0, 14, 2);
    public static readonly uint V7000 = Pack(7, 0, 0, 0);
    public static readonly uint V7090 = Pack(7, 0, 9, 0);
    public static readonly uint V70130 = Pack(7, 0, 13, 0);
    public static readonly uint V70180 = Pack(7, 0, 18, 0);
    public static readonly uint V70331 = Pack(7, 0, 33, 1);
    public static readonly uint V706400 = Pack(7, 0, 64, 0);
    public static readonly uint V7010400 = Pack(7, 0, 104, 0);

    // Base table, 0x00..0xFE; -1 = variable length (a 16-bit length follows the opcode).
    private static readonly short[] BaseLengths =
    [
        0x0068, 0x0005, 0x0007, -1, 0x0002, 0x0005, 0x0005, 0x0007, 0x000E, 0x0005, 0x000B, 0x010A, -1, 0x0003, -1, 0x003D, // 0x00
        0x00D7, -1, -1, 0x000A, 0x0006, 0x0009, 0x0001, -1, -1, -1, -1, 0x0025, -1, 0x0005, 0x0004, 0x0008, // 0x10
        0x0013, 0x0008, 0x0003, 0x001A, 0x0007, 0x0014, 0x0005, 0x0002, 0x0005, 0x0001, 0x0005, 0x0002, 0x0002, 0x0011, 0x000F, 0x000A, // 0x20
        0x0005, 0x0001, 0x0002, 0x0002, 0x000A, 0x028D, -1, 0x0008, 0x0007, 0x0009, -1, -1, -1, 0x0002, 0x0025, -1, // 0x30
        0x00C9, -1, -1, 0x0229, 0x02C9, 0x0005, -1, 0x000B, 0x0049, 0x005D, 0x0005, 0x0009, -1, -1, 0x0006, 0x0002, // 0x40
        -1, -1, -1, 0x0002, 0x000C, 0x0001, 0x000B, 0x006E, 0x006A, -1, -1, 0x0004, 0x0002, 0x0049, -1, 0x0031, // 0x50
        0x0005, 0x0009, 0x000F, 0x000D, 0x0001, 0x0004, -1, 0x0015, -1, -1, 0x0003, 0x0009, 0x0013, 0x0003, 0x000E, -1, // 0x60
        0x001C, -1, 0x0005, 0x0002, -1, 0x0023, 0x0010, 0x0011, -1, 0x0009, -1, 0x0002, -1, 0x000D, 0x0002, -1, // 0x70
        0x003E, -1, 0x0002, 0x0027, 0x0045, 0x0002, -1, -1, 0x0042, -1, -1, -1, 0x000B, -1, -1, -1, // 0x80
        0x0013, 0x0041, -1, 0x0063, -1, 0x0009, -1, 0x0002, -1, 0x001A, -1, 0x0102, 0x0135, 0x0033, -1, -1, // 0x90
        0x0003, 0x0009, 0x0009, 0x0009, 0x0095, -1, -1, 0x0004, -1, -1, 0x0005, -1, -1, -1, -1, 0x000D, // 0xA0
        -1, -1, -1, -1, -1, 0x0040, 0x0009, -1, -1, 0x0003, 0x0006, 0x0009, 0x0003, -1, -1, -1, // 0xB0
        0x0024, -1, -1, -1, 0x0006, 0x00CB, 0x0001, 0x0031, 0x0002, 0x0006, 0x0006, 0x0007, -1, 0x0001, -1, 0x004E, // 0xC0
        -1, 0x0002, 0x0019, -1, -1, -1, -1, -1, -1, 0x010C, -1, -1, 0x0009, -1, -1, -1, // 0xD0
        -1, -1, 0x000A, -1, -1, -1, 0x0005, 0x000C, 0x000D, 0x004B, 0x0003, -1, -1, -1, 0x000A, 0x0015, // 0xE0
        -1, 0x0009, 0x0019, 0x001A, -1, 0x0015, -1, -1, 0x006A, -1, -1, -1, -1, -1, -1, // 0xF0
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, short[]> Tables = new();

    /// <summary>The length the client frames <paramref name="opcode"/> with: a byte
    /// count, or -1 for a packet that carries its own length.</summary>
    public static int Length(byte opcode, uint version) =>
        opcode == 0xFF ? -1 : Tables.GetOrAdd(version, BuildTable)[opcode];

    // The client's calibration, applied in its order: a later gate's else branch
    // overrides an earlier one (0xEE/0xEF/0xF1 below 7.0.0 / 7.0.9).
    private static short[] BuildTable(uint v)
    {
        var t = (short[])BaseLengths.Clone();
        if (v >= V500A) { t[0x0B] = 0x07; t[0x16] = -1; t[0x31] = -1; }
        else { t[0x0B] = 0x10A; t[0x16] = 0x01; t[0x31] = 0x01; }
        t[0xE1] = v >= V5090 ? (short)-1 : (short)0x09;
        if (v >= V6013) { t[0xE3] = -1; t[0xE6] = 0x05; t[0xE7] = 0x0C; t[0xE8] = 0x0D; t[0xE9] = 0x4B; t[0xEA] = 0x03; }
        else { t[0xE3] = 0x4D; t[0xE6] = -1; t[0xE7] = -1; t[0xE8] = -1; t[0xE9] = -1; t[0xEA] = -1; }
        if (v >= V6017) { t[0x08] = 0x0F; t[0x25] = 0x15; }
        else { t[0x08] = 0x0E; t[0x25] = 0x14; }
        if (v >= V6060) { t[0xEE] = 0x2000; t[0xEF] = 0x2000; t[0xF1] = 0x09; }
        else { t[0xEE] = -1; t[0xEF] = 0x15; t[0xF1] = -1; }
        t[0xB9] = v >= V60142 ? (short)0x05 : (short)0x03;
        if (v >= V7000) { t[0xEE] = 0x0A; t[0xEF] = 0x15; }
        else { t[0xEE] = -1; t[0xEF] = 0x15; }
        if (v >= V7090) { t[0x24] = 0x09; t[0x99] = 0x1E; t[0xBA] = 0x0A; t[0xF3] = 0x1A; t[0xF1] = 0x09; t[0xF2] = 0x19; }
        else { t[0x24] = 0x07; t[0x99] = 0x1A; t[0xBA] = 0x06; t[0xF3] = 0x18; t[0xF1] = -1; t[0xF2] = -1; }
        t[0x00] = v >= V70180 ? (short)0x6A : (short)0x68;
        if (v >= V706400) { t[0xFA] = 0x01; t[0xFB] = 0x02; }
        if (v >= V7010400) { t[0xD5] = 0x09; t[0xFD] = 2; }
        return t;
    }

    // The first classic client that knows each packet, from Source-X's CanSendTo
    // gates (network/send.cpp, MINCLIVER_* in common/sphereproto.h; a letter release
    // such as 4.0.7a is taken as 4.0.7.0). An older client has no entry for the
    // opcode: it cannot skip the packet and loses the stream. uint.MaxValue = sent
    // only to the KR / enhanced clients.
    private static readonly Dictionary<byte, uint> MinimumVersions = new()
    {
        [0x0B] = Pack(4, 0, 7, 0),   // new damage number
        [0x16] = uint.MaxValue,      // enhanced health bar
        [0x17] = Pack(7, 0, 0, 0),   // health bar colour
        [0x29] = uint.MaxValue,      // drop accepted (KR)
        [0xD4] = Pack(5, 0, 0, 0),   // new book header
        [0xD6] = Pack(4, 0, 0, 0),   // property list (tooltip)
        [0xD8] = Pack(4, 0, 0, 0),   // custom house design
        [0xDC] = Pack(4, 0, 5, 0),   // tooltip hash
        [0xDD] = Pack(5, 0, 0, 0),   // compressed gump
        [0xDF] = Pack(5, 0, 2, 2),   // buff icon
        [0xE2] = Pack(7, 0, 0, 0),   // new mobile animation
        [0xE5] = Pack(7, 0, 84, 0),  // map waypoint add
        [0xE6] = Pack(7, 0, 84, 0),  // map waypoint remove
        [0xF2] = Pack(7, 0, 0, 0),   // time sync response
        [0xF3] = Pack(7, 0, 0, 0),   // new world item
        [0xF5] = Pack(7, 0, 13, 0),  // new map display
        [0xF6] = Pack(7, 0, 9, 0),   // smooth boat move
        [0xF9] = Pack(7, 0, 62, 2),  // global chat
    };

    // 0xBF sub-commands with their own gate.
    private static readonly Dictionary<ushort, uint> MinimumExtendedVersions = new()
    {
        [0x10] = Pack(4, 0, 0, 0),   // old tooltip version
        [0x1B] = Pack(4, 0, 0, 0),   // spellbook content
        [0x20] = Pack(4, 0, 0, 0),   // begin house customisation
        [0x22] = Pack(4, 0, 0, 0),   // old damage number
    };

    /// <summary>The first client version that knows this packet; 0 when every
    /// version does.</summary>
    public static uint MinimumVersion(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0) return 0;
        if (MinimumVersions.TryGetValue(packet[0], out uint v)) return v;
        if (packet[0] == 0xBF && packet.Length >= 5)
        {
            ushort sub = BinaryPrimitives.ReadUInt16BigEndian(packet[3..]);
            if (MinimumExtendedVersions.TryGetValue(sub, out v)) return v;
            // 0xBF.0x19 type 2: stat locks (4.0.1).
            if (sub == 0x19 && packet.Length >= 6 && packet[5] == 0x02) return Pack(4, 0, 1, 0);
        }
        return 0;
    }

    public static string Describe(uint packed) => packed == uint.MaxValue
        ? "KR/enhanced only"
        : $"{packed >> 24}.{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    /// <summary>Check one whole server packet against what a client of
    /// <paramref name="version"/> expects. Null when the client reads it as written,
    /// otherwise what goes wrong.</summary>
    public static string? Check(ReadOnlySpan<byte> packet, uint version)
    {
        if (packet.Length == 0) return "empty packet";
        byte op = packet[0];
        uint minimum = MinimumVersion(packet);
        if (minimum > version)
        {
            string name = op == 0xBF && packet.Length >= 5
                ? $"0xBF.0x{BinaryPrimitives.ReadUInt16BigEndian(packet[3..]):X2}"
                : $"0x{op:X2}";
            return $"{name} needs client {Describe(minimum)} or later";
        }
        int expected = Length(op, version);
        if (expected > 0)
        {
            if (packet.Length != expected)
                return $"0x{op:X2} is {packet.Length} bytes; the client frames it as {expected}";
            return null;
        }

        if (packet.Length < 3) return $"variable 0x{op:X2} shorter than its header ({packet.Length} bytes)";
        int declared = BinaryPrimitives.ReadUInt16BigEndian(packet[1..]);
        if (declared != packet.Length)
            return $"0x{op:X2} declares {declared} bytes and is {packet.Length}";

        return op switch
        {
            0x78 => CheckMobileIncoming(packet, version),
            0xA9 => CheckCharacterList(packet, version),
            _ => null,
        };
    }

    // 0x78: 19-byte header, then [serial][graphic][layer][hue?] per item up to a zero
    // serial. Before 7.0.33.1 the hue is there only when the graphic has 0x8000 set.
    private static string? CheckMobileIncoming(ReadOnlySpan<byte> p, uint version)
    {
        bool newLayout = version >= V70331;
        string layout = newLayout ? "7.0.33+" : "pre-7.0.33";
        int pos = 19;
        while (true)
        {
            if (pos + 4 > p.Length)
                return $"0x78 runs out at byte {pos} before its zero serial ({layout} layout)";
            uint serial = BinaryPrimitives.ReadUInt32BigEndian(p[pos..]);
            pos += 4;
            if (serial == 0) break;
            if (pos + 3 > p.Length) return $"0x78 item at byte {pos - 4} is cut short ({layout} layout)";
            ushort graphic = BinaryPrimitives.ReadUInt16BigEndian(p[pos..]);
            pos += 3;
            if (newLayout || (graphic & 0x8000) != 0) pos += 2;
        }
        return pos == p.Length ? null : $"0x78 read in the {layout} layout ends at byte {pos} of {p.Length}";
    }

    // 0xA9: count, 60 bytes per character slot, count, cities (89 bytes each from
    // 7.0.13.0, 63 before), then the 32-bit flags. A trailing 16-bit last-character
    // slot is accepted; the client stops reading at the flags.
    private static string? CheckCharacterList(ReadOnlySpan<byte> p, uint version)
    {
        int pos = 3;
        if (pos >= p.Length) return "0xA9 has no character count";
        int chars = p[pos++];
        pos += chars * 60;
        if (pos >= p.Length) return $"0xA9 with {chars} slots has no city count";
        int cities = p[pos++];
        int citySize = version >= V70130 ? 89 : 63;
        pos += cities * citySize + 4;
        if (pos == p.Length || pos + 2 == p.Length) return null;
        return $"0xA9 with {chars} slots and {cities} cities of {citySize} bytes ends at byte {pos}, packet is {p.Length}";
    }
}
