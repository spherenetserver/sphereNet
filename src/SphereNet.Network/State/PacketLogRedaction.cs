namespace SphereNet.Network.State;

/// <summary>
/// Hex dumps of inbound packets with the account password taken out.
///
/// Three client packets carry the account password in the clear once the transport
/// layer is undone: 0x80 (login server list request), 0x91 (game server login) and
/// 0x83 (character delete). Any log line that dumps one of them - the debug RECV line,
/// the encryption-detection failure warning - printed the password bytes with it, and
/// a log is read and kept by far more people than an account file is.
///
/// The encryption-detection dump is a special case: its bytes may still be ciphertext,
/// and the login ciphers are keyed by values that are public (sphereCrypt.ini) plus the
/// seed that the same line prints, so a ciphertext password is a password. That dump is
/// masked by position, whatever its first byte says.
/// </summary>
internal static class PacketLogRedaction
{
    internal const string MaskToken = "**";

    /// <summary>Where the password field sits in a client packet that has one.</summary>
    internal static bool TryGetPasswordField(byte opcode, out int offset, out int length)
    {
        switch (opcode)
        {
            case 0x80: offset = 31; length = 30; return true;   // opcode, account[30], password[30], key
            case 0x91: offset = 35; length = 30; return true;   // opcode, authId, account[30], password[30]
            case 0x83: offset = 1; length = 30; return true;    // opcode, password[30], slot, ip
            default: offset = 0; length = 0; return false;
        }
    }

    /// <summary>Hex of <paramref name="data"/> (at most <paramref name="maxBytes"/>),
    /// a packet starting at offset 0. The password field of a packet that carries one
    /// is printed as <see cref="MaskToken"/>.</summary>
    internal static string FormatPacket(ReadOnlySpan<byte> data, int maxBytes)
    {
        int len = Math.Min(data.Length, maxBytes);
        int maskStart = 0, maskEnd = 0;
        if (data.Length > 0 && TryGetPasswordField(data[0], out int off, out int flen))
        {
            maskStart = off;
            maskEnd = off + flen;
        }
        return Format(data, len, data.Length > maxBytes, (i) => i >= maskStart && i < maskEnd);
    }

    /// <summary>Hex of a first-packet buffer whose encryption is not known yet. For
    /// every place a login packet could start (<paramref name="packetStarts"/>) the
    /// bytes where a 0x80 or a 0x91 password would sit are masked, plaintext or not.</summary>
    internal static string FormatLoginCandidate(ReadOnlySpan<byte> data, int maxBytes, ReadOnlySpan<int> packetStarts)
    {
        int len = Math.Min(data.Length, maxBytes);
        // 0x80 password = [31, 61), 0x91 password = [35, 65): mask the union.
        var starts = packetStarts.ToArray();
        return Format(data, len, data.Length > maxBytes, i =>
        {
            foreach (int s in starts)
                if (i >= s + 31 && i < s + 65)
                    return true;
            return false;
        });
    }

    private static string Format(ReadOnlySpan<byte> data, int len, bool truncated, Func<int, bool> masked)
    {
        var sb = new System.Text.StringBuilder(len * 3 + 4);
        for (int i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            if (masked(i))
                sb.Append(MaskToken);
            else
                sb.Append(data[i].ToString("X2"));
        }
        if (truncated) sb.Append(" ...");
        return sb.ToString();
    }
}
