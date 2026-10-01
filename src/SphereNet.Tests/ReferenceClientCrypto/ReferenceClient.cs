namespace SphereNet.Tests.ReferenceClientCrypto;

/// <summary>
/// Client-side encryption for the legacy-crypto tests, driven only through the
/// verbatim client encryptor copies in this folder. The server classes under test
/// never produce their own test input, so a round trip can only pass when the server
/// matches the client byte for byte.
///
/// The copied Blowfish keeps its tables in statics and builds them lazily, so every
/// use is serialised behind one lock to keep parallel test classes from rebuilding
/// the tables underneath each other.
/// </summary>
internal static class ReferenceClient
{
    private static readonly object Gate = new();

    /// <summary>Game-stream table switch point (CRYPT_GAME_TABLE_TRIGGER).</summary>
    public const int BlowfishTableTrigger = 21036;

    /// <summary>
    /// A fresh client Blowfish game stream over <paramref name="plain"/>, fed in the
    /// given write sizes (default: one write).
    ///
    /// The copied encryptor's own rollover branch re-reads the start of the span when
    /// a single call crosses the 21036-byte table switch, so writes are additionally
    /// cut at that point: each call then ends exactly on the switch and the next call
    /// performs it, which is the stream both the client tables and the Source-X
    /// decryptor define.
    /// </summary>
    public static byte[] Blowfish(byte[] plain, params int[] writes)
    {
        lock (Gate)
        {
            var client = new ClientBlowfishCipher();
            client.Initialize();
            return BlowfishContinue(client, plain, writes);
        }
    }

    /// <summary>Blowfish then Twofish over the same bytes: the 2.0.0x-2.0.3 game stream.</summary>
    public static byte[] BlowfishTwofish(uint seed, byte[] plain)
    {
        byte[] blown = Blowfish(plain);
        var tf = new ClientTwofishCipher();
        tf.Initialize(seed, use_md5: false);
        byte[] wire = new byte[plain.Length];
        tf.Encrypt(blown, wire, blown.Length);
        return wire;
    }

    /// <summary>Twofish game stream (3.0.0+), with the MD5 receive table set up.</summary>
    public static ClientTwofishCipher Twofish(uint seed)
    {
        var tf = new ClientTwofishCipher();
        tf.Initialize(seed, use_md5: true);
        return tf;
    }

    public static byte[] TwofishEncrypt(ClientTwofishCipher tf, byte[] plain)
    {
        byte[] wire = new byte[plain.Length];
        tf.Encrypt(plain, wire, plain.Length);
        return wire;
    }

    private static byte[] BlowfishContinue(ClientBlowfishCipher client, byte[] plain, int[] writes)
    {
        byte[] wire = new byte[plain.Length];
        int pos = 0;
        int streamPos = 0;
        int w = 0;
        while (pos < plain.Length)
        {
            int write = writes.Length == 0 ? plain.Length - pos : Math.Min(writes[w++ % writes.Length], plain.Length - pos);
            while (write > 0)
            {
                int n = Math.Min(write, BlowfishTableTrigger - streamPos);
                int ix = 0, ox = 0;
                client.Encrypt(plain.AsSpan(pos, n), wire.AsSpan(pos, n), n, ref ix, ref ox);
                pos += n;
                write -= n;
                streamPos += n;
                if (streamPos == BlowfishTableTrigger && pos < plain.Length)
                    streamPos = 0; // the next call switches the table
            }
        }
        return wire;
    }

    /// <summary>Client login crypt, standard rotation (1.25.37+), with the client's own key layout.</summary>
    public static byte[] LoginStandard(uint seed, uint k1, uint k2, uint k3, byte[] plain)
    {
        var c = new ClientLoginCipher();
        c.Initialize(seed, k1, k2, k3);
        byte[] wire = new byte[plain.Length];
        c.Encrypt(plain, wire, plain.Length);
        return wire;
    }

    /// <summary>Client login crypt, old single rotation (up to 1.25.35).</summary>
    public static byte[] LoginOld(uint seed, uint maskHiKey, uint maskLoKey, byte[] plain)
    {
        // Encrypt_OLD XORs the low mask with k2 and the high mask with k1.
        var c = new ClientLoginCipher();
        c.Initialize(seed, maskHiKey, maskLoKey, 0);
        byte[] wire = new byte[plain.Length];
        c.Encrypt_OLD(plain, wire, plain.Length);
        return wire;
    }
}
