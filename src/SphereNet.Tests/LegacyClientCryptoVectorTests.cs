using System.Linq;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Tests.ReferenceClientCrypto;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Legacy client encryption checked against INDEPENDENT client vectors: every
/// ciphertext here comes from the client encryptor copies in ReferenceClientCrypto/
/// (or, for 1.25.36, from the Source-X formula written out on the client side), never
/// from the server classes under test. A server cipher that only agrees with itself
/// cannot pass.
///
/// Reference: Source-X CCryptoBlowFish.cpp (InitTables / InitSeed / DecryptBlowFish /
/// DecryptBFByte), CCrypto.cpp (GameCryptStart, RelayGameCryptStart, Encrypt),
/// CCryptoLogin.cpp (DecryptLogin per client version).
/// </summary>
public sealed class LegacyClientCryptoVectorTests
{
    private readonly ITestOutputHelper _out;
    public LegacyClientCryptoVectorTests(ITestOutputHelper output) => _out = output;

    private const uint Seed = 0x7F000001;

    private static byte[] Payload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(i * 17 + 3);
        return data;
    }

    /// <summary>A 0x91 game login (account at 5..34, password at 35..64) followed by payload.</summary>
    private static byte[] GameLoginStream(int totalLength, uint authId = 0x1234ABCD)
    {
        byte[] data = Payload(Math.Max(totalLength, 65));
        Array.Clear(data, 0, 65);
        data[0] = 0x91;
        data[1] = (byte)(authId >> 24);
        data[2] = (byte)(authId >> 16);
        data[3] = (byte)(authId >> 8);
        data[4] = (byte)authId;
        WriteAscii(data, 5, "legacyacct");
        WriteAscii(data, 35, "legacypass");
        return data;
    }

    private static byte[] LoginPacket(string account = "oldtimer", string password = "s3cret")
    {
        byte[] p = new byte[62];
        p[0] = 0x80;
        WriteAscii(p, 1, account);
        WriteAscii(p, 31, password);
        p[61] = 0xFF;
        return p;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static CryptConfig RealCryptConfig()
    {
        var config = new CryptConfig();
        config.Load(TestRepo.PathOf("config/sphereCrypt.ini"));
        Assert.NotEmpty(config.Keys);
        return config;
    }

    private static int FirstMismatch(byte[] expected, byte[] actual)
    {
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                return i;
        return -1;
    }

    // ---- ENC_BFISH game stream: byte equality --------------------------------------

    [Theory]
    [InlineData(8)]
    [InlineData(65)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(21036)]
    [InlineData(21037)]   // one byte past the table switch
    [InlineData(50000)]   // two switches
    public void BlowfishStream_DecryptsClientBytesExactly(int length)
    {
        byte[] plain = Payload(length);
        byte[] wire = ReferenceClient.Blowfish(plain);

        byte[] server = (byte[])wire.Clone();
        new BlowfishGameEncryption().Decrypt(server, 0, server.Length);

        int miss = FirstMismatch(plain, server);
        _out.WriteLine($"len={length} firstMismatch={miss}" +
                       (miss >= 0 ? $" expected={plain[miss]} got={server[miss]}" : ""));
        Assert.Equal(-1, miss);
    }

    [Theory]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 7 })]
    [InlineData(new[] { 8 })]
    [InlineData(new[] { 9, 3, 65, 1 })]
    [InlineData(new[] { 21035, 2 })]       // a read that straddles the switch
    [InlineData(new[] { 4096, 12345, 4597, 1, 999 })]
    public void BlowfishStream_FragmentedReadsMatchOneRead(int[] reads)
    {
        // 8-byte ciphertext feedback must survive reads that end mid-block, and the
        // table switch at 21036 must survive a read that crosses it.
        byte[] plain = Payload(21036 + 600);
        byte[] wire = ReferenceClient.Blowfish(plain, 13, 500, 7);   // client writes unrelated to server reads

        byte[] server = (byte[])wire.Clone();
        var bf = new BlowfishGameEncryption();
        int pos = 0, r = 0;
        while (pos < server.Length)
        {
            int n = Math.Min(reads[r++ % reads.Length], server.Length - pos);
            bf.Decrypt(server, pos, n);
            pos += n;
        }

        Assert.Equal(-1, FirstMismatch(plain, server));
    }

    [Fact]
    public void BlowfishStream_IsCiphertextFeedbackNotAFixedKeystream()
    {
        // Same first block, different second block: with ciphertext feedback the third
        // block's keystream depends on what came before, so the ciphertexts diverge
        // from the first differing byte onward and the server must still read both.
        byte[] a = Payload(24);
        byte[] b = (byte[])a.Clone();
        b[9] ^= 0x55;

        byte[] wa = ReferenceClient.Blowfish(a);
        byte[] wb = ReferenceClient.Blowfish(b);
        Assert.Equal(wa[..8], wb[..8]);
        Assert.NotEqual(wa[16..], wb[16..]);

        new BlowfishGameEncryption().Decrypt(wa, 0, wa.Length);
        new BlowfishGameEncryption().Decrypt(wb, 0, wb.Length);
        Assert.Equal(a, wa);
        Assert.Equal(b, wb);
    }

    // ---- game login detection (direct connection, no relay keys) -------------------

    [Theory]
    [InlineData(65)]
    [InlineData(1000)]
    [InlineData(21037)]
    public void DirectBlowfishGameLogin_IsDetectedAndTheStreamContinues(int totalLength)
    {
        byte[] plain = GameLoginStream(totalLength);
        byte[] wire = ReferenceClient.Blowfish(plain);

        var state = new CryptoState();
        byte[]? login = state.DetectAndDecryptGameLogin(Seed, wire.AsSpan(0, 65), RealCryptConfig(),
            useCrypt: true, useNoCrypt: false);

        Assert.NotNull(login);
        Assert.Equal(plain[..65], login);
        Assert.Equal(EncryptionType.Blowfish, state.EncType);

        // The rest of the connection, delivered in uneven reads.
        byte[] rest = wire[65..];
        int pos = 0, step = 1;
        while (pos < rest.Length)
        {
            int n = Math.Min(step, rest.Length - pos);
            state.Decrypt(rest, pos, n);
            pos += n;
            step = step * 3 % 977 + 1;
        }
        Assert.Equal(-1, FirstMismatch(plain[65..], rest));

        // ENC_BFISH answers in the clear (Source-X CCrypto::Encrypt only MD5s ENC_TFISH).
        byte[] outgoing = Payload(40);
        byte[] sent = (byte[])outgoing.Clone();
        state.Encrypt(sent, 0, sent.Length);
        Assert.Equal(outgoing, sent);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(21037)]
    public void DirectBlowfishTwofishGameLogin_IsDetectedEndToEnd(int totalLength)
    {
        // 2.0.0x-2.0.3: the client runs Blowfish, then Twofish keyed by the seed.
        byte[] plain = GameLoginStream(totalLength);
        byte[] wire = ReferenceClient.BlowfishTwofish(Seed, plain);

        var state = new CryptoState();
        byte[]? login = state.DetectAndDecryptGameLogin(Seed, wire.AsSpan(0, 65), RealCryptConfig(),
            useCrypt: true, useNoCrypt: false);

        Assert.NotNull(login);
        Assert.Equal(plain[..65], login);
        Assert.Equal(EncryptionType.BlowfishTwofish, state.EncType);

        byte[] rest = wire[65..];
        for (int pos = 0; pos < rest.Length; pos += 333)
            state.Decrypt(rest, pos, Math.Min(333, rest.Length - pos));
        Assert.Equal(-1, FirstMismatch(plain[65..], rest));

        // The 2.0.3 client never MD5-decrypts what it receives.
        byte[] outgoing = Payload(40);
        byte[] sent = (byte[])outgoing.Clone();
        state.Encrypt(sent, 0, sent.Length);
        Assert.Equal(outgoing, sent);
    }

    [Theory]
    [InlineData(0x7F000001u)]
    [InlineData(0xDEADBEEFu)]
    public void DirectTwofishGameLogin_StillDetectedWithMd5Replies(uint seed)
    {
        byte[] plain = GameLoginStream(1000);
        var client = ReferenceClient.Twofish(seed);
        byte[] wire = ReferenceClient.TwofishEncrypt(client, plain);

        var state = new CryptoState();
        byte[]? login = state.DetectAndDecryptGameLogin(seed, wire.AsSpan(0, 65), RealCryptConfig(),
            useCrypt: true, useNoCrypt: false);

        Assert.NotNull(login);
        Assert.Equal(plain[..65], login);
        Assert.Equal(EncryptionType.Twofish, state.EncType);

        byte[] rest = wire[65..];
        state.Decrypt(rest, 0, rest.Length);
        Assert.Equal(plain[65..], rest);

        byte[] outgoing = Payload(300);
        byte[] sent = (byte[])outgoing.Clone();
        state.Encrypt(sent, 0, sent.Length);
        byte[] clientRead = new byte[sent.Length];
        client.Decrypt(sent, clientRead, sent.Length);
        Assert.Equal(outgoing, clientRead);
    }

    // ---- login crypt per client version (real sphereCrypt.ini keys) ----------------

    private static byte[] EncryptLogin12536(uint seed, uint masterHi, uint masterLo, byte[] plain)
    {
        // Client side of Source-X CCryptoLogin.cpp's 1.25.36 "special multi key" branch,
        // written out independently: same seed masks, the multiply/shift update, shift
        // counts of 32 or more yielding 0.
        uint lo = ((~seed ^ 0x00001357u) << 16) | ((seed ^ 0xFFFFAAAAu) & 0x0000FFFFu);
        uint hi = ((seed ^ 0x43210000u) >> 16) | ((~seed ^ 0xABCDFFFFu) & 0xFFFF0000u);
        byte[] wire = (byte[])plain.Clone();
        for (int i = 0; i < wire.Length; i++)
        {
            wire[i] ^= (byte)lo;
            uint oldLo = lo, oldHi = hi;
            uint shHi = (5 * oldHi * oldHi) & 0xFF;
            uint shLo = (3 * oldLo * oldLo) & 0xFF;
            hi = (shHi >= 32 ? 0u : masterHi >> (int)shHi) + oldHi * masterHi + oldLo * oldLo * 0x35CE9581u + 0x07AFCC37u;
            lo = (shLo >= 32 ? 0u : masterLo >> (int)shLo) + oldLo * masterLo - hi * hi * 0x4C3A1353u + 0x16EF783Fu;
        }
        return wire;
    }

    [Fact]
    public void Login_1_25_37_StandardRotation_IsDetected()
    {
        var config = RealCryptConfig();
        var key = config.FindKey(1253700);
        Assert.NotNull(key);

        // The client's double rotation XORs a hi key into each half-step and the lo key
        // into the low mask. Source-X DecryptLogin uses the ini's one hi key for both
        // half-steps, so both slots get it here. (The client copy derives hi-1 for the
        // first slot from its own version formula; that is only equivalent when the hi
        // key is odd, which 1.25.37's 0x378757DC is not - the ini key is the reference.)
        byte[] plain = LoginPacket();
        byte[] wire = ReferenceClient.LoginStandard(Seed, key!.Key1, key.Key1, key.Key2, plain);

        var state = new CryptoState();
        byte[]? decoded = state.DetectAndDecryptLogin(Seed, wire, config, useCrypt: true, useNoCrypt: false);
        Assert.NotNull(decoded);
        Assert.Equal(plain, decoded);
        Assert.Equal(EncryptionType.Login, state.EncType);
        Assert.Equal(key.Key1, state.Key1);
        Assert.Equal(key.Key2, state.Key2);
    }

    [Fact]
    public void Login_1_25_36_SpecialMultiKey_IsDetected()
    {
        var config = RealCryptConfig();
        var key = config.FindKey(1253600);
        Assert.NotNull(key);
        Assert.Equal(0x387FC5CCu, key!.Key1);   // CLIKEY_12536_HI1
        Assert.Equal(0x021510C6u, key.Key2);    // CLIKEY_12536_LO1

        byte[] plain = LoginPacket("multikey", "pw12536");
        byte[] wire = EncryptLogin12536(Seed, key.Key1, key.Key2, plain);

        var state = new CryptoState();
        byte[]? decoded = state.DetectAndDecryptLogin(Seed, wire, config, useCrypt: true, useNoCrypt: false);
        Assert.NotNull(decoded);
        Assert.Equal(plain, decoded);
        Assert.Equal(key.Key1, state.Key1);
        Assert.Equal(key.Key2, state.Key2);

        // The connection keeps decrypting with the same formula.
        byte[] more = LoginPacket("second", "packet");
        byte[] both = EncryptLogin12536(Seed, key.Key1, key.Key2, [.. plain, .. more]);
        byte[] tail = both[62..];
        state.Decrypt(tail, 0, tail.Length);
        Assert.Equal(more, tail);
    }

    [Fact]
    public void Login_1_25_35_OldRotation_IsDetected()
    {
        var config = RealCryptConfig();
        var key = config.FindKey(1253500);
        Assert.NotNull(key);

        byte[] plain = LoginPacket("t2aplayer", "oldpass");
        byte[] wire = ReferenceClient.LoginOld(Seed, key!.Key1, key.Key2, plain);

        var state = new CryptoState();
        byte[]? decoded = state.DetectAndDecryptLogin(Seed, wire, config, useCrypt: true, useNoCrypt: false);
        Assert.NotNull(decoded);
        Assert.Equal(plain, decoded);
        Assert.Equal(key.Key1, state.Key1);
        Assert.Equal(key.Key2, state.Key2);
    }

    [Fact]
    public void Login_1_25_36_IsNotReadByTheOrdinaryRotations()
    {
        // The special formula must stay tied to 1.25.36: with that key removed, the
        // same bytes must not slip through any other key or rotation.
        var config = RealCryptConfig();
        var key = config.FindKey(1253600)!;
        var without = new CryptConfig();
        var keys = (List<CryptoClientKey>)typeof(CryptConfig)
            .GetField("_keys", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(without)!;
        keys.AddRange(config.Keys.Where(k => k.ClientVersion != 1253600));

        byte[] wire = EncryptLogin12536(Seed, key.Key1, key.Key2, LoginPacket("multikey", "pw12536"));
        Assert.Null(new CryptoState().DetectAndDecryptLogin(Seed, wire, without, useCrypt: true, useNoCrypt: false));
    }
}
