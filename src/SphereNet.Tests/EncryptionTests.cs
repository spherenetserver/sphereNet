using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;

namespace SphereNet.Tests;

public class EncryptionTests
{
    private static CryptConfig EmptyCryptConfig() => new();

    private static CryptConfig LoadCryptConfig(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_crypt_{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, contents);
        var config = new CryptConfig();
        config.Load(path);
        File.Delete(path);
        return config;
    }

    private static void AddCryptKey(CryptConfig config, CryptoClientKey key)
    {
        var keys = (List<CryptoClientKey>)typeof(CryptConfig)
            .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(config)!;
        keys.Add(key);
    }

    [Fact]
    public void Blowfish_EncryptDecrypt_RoundTrips()
    {
        var key = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
        var bf = new BlowfishEncryption(key);

        byte[] original = { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE };
        byte[] data = (byte[])original.Clone();

        bf.Encrypt(data, 0, 8);
        Assert.NotEqual(original, data);

        bf.Decrypt(data, 0, 8);
        Assert.Equal(original, data);
    }

    [Fact]
    public void Blowfish_DifferentKeys_ProduceDifferentCiphertext()
    {
        var key1 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var key2 = new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };

        byte[] data1 = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        byte[] data2 = (byte[])data1.Clone();

        new BlowfishEncryption(key1).Encrypt(data1, 0, 8);
        new BlowfishEncryption(key2).Encrypt(data2, 0, 8);

        Assert.NotEqual(data1, data2);
    }

    [Fact]
    public void Blowfish_MultiBlock_EncryptDecrypt()
    {
        var key = new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89 };
        var bf = new BlowfishEncryption(key);

        byte[] original = new byte[24];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        bf.Encrypt(data, 0, 24);
        bf.Decrypt(data, 0, 24);

        Assert.Equal(original, data);
    }

    [Fact]
    public void Twofish_EncryptDecrypt_RoundTrips()
    {
        var key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)i;

        var tf = new TwofishEncryption(key);

        byte[] original = new byte[16];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        tf.Encrypt(data, 0, 16);
        Assert.NotEqual(original, data);

        tf.Decrypt(data, 0, 16);
        Assert.Equal(original, data);
    }

    [Fact]
    public void Twofish_DifferentKeys_ProduceDifferentCiphertext()
    {
        var key1 = new byte[16]; key1[0] = 1;
        var key2 = new byte[16]; key2[0] = 2;

        byte[] data1 = new byte[16];
        byte[] data2 = new byte[16]; // same plaintext (zeros)

        new TwofishEncryption(key1).Encrypt(data1, 0, 16);
        new TwofishEncryption(key2).Encrypt(data2, 0, 16);

        Assert.NotEqual(data1, data2);
    }

    [Fact]
    public void Twofish_MultiBlock_RoundTrips()
    {
        var key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)(i * 3);
        var tf = new TwofishEncryption(key);

        byte[] original = new byte[48];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        tf.Encrypt(data, 0, 48);
        tf.Decrypt(data, 0, 48);

        Assert.Equal(original, data);
    }

    [Fact]
    public void LoginEncryption_Decrypt_ChangesData()
    {
        var enc = new LoginEncryption(0x12345678, 0, 0);
        byte[] data = { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        byte[] original = (byte[])data.Clone();

        enc.Decrypt(data, 0, 8);
        Assert.NotEqual(original, data);
    }

    [Fact]
    public void CryptoState_NoCryptLogin_DetectsValidLoginPacket()
    {
        byte[] packet = new byte[62];
        packet[0] = 0x80;
        packet[30] = 0x00;
        packet[60] = 0x00;

        var state = new CryptoState();
        var decoded = state.DetectAndDecryptLogin(0x12345678, packet, EmptyCryptConfig(),
            useCrypt: false, useNoCrypt: true);

        Assert.NotNull(decoded);
        Assert.Equal(packet, decoded);
        Assert.True(state.IsInitialized);
        Assert.Equal(EncryptionType.None, state.EncType);
    }

    [Fact]
    public void CryptoState_NoCryptLogin_RequiresNoCryptMode()
    {
        byte[] packet = new byte[62];
        packet[0] = 0x80;

        var state = new CryptoState();
        var decoded = state.DetectAndDecryptLogin(0x12345678, packet, EmptyCryptConfig(),
            useCrypt: false, useNoCrypt: false);

        Assert.Null(decoded);
        Assert.False(state.IsInitialized);
    }

    [Fact]
    public void CryptoState_LoginEncryption_DetectsKnownKey()
    {
        const uint seed = 0x12345678;
        const uint key1 = 0x11111111;
        const uint key2 = 0x22222222;
        var config = new CryptConfig();
        AddCryptKey(config, new CryptoClientKey(70000000, key1, key2, EncryptionType.Login));

        byte[] plain = new byte[62];
        plain[0] = 0x80;
        WriteAsciiFixed(plain, 1, 30, "acct");
        WriteAsciiFixed(plain, 31, 30, "pw");
        byte[] encrypted = (byte[])plain.Clone();
        new LoginEncryption(seed, key1, key2).Decrypt(encrypted, 0, encrypted.Length);

        var state = new CryptoState();
        var decoded = state.DetectAndDecryptLogin(seed, encrypted, config,
            useCrypt: true, useNoCrypt: false);

        Assert.NotNull(decoded);
        Assert.Equal(plain, decoded);
        Assert.True(state.IsInitialized);
        Assert.Equal(EncryptionType.Login, state.EncType);
    }

    [Fact]
    public void CryptoState_LoginEncryption_RejectsAccountOnlyFalsePositive()
    {
        byte[] raw =
        [
            0x55, 0x78, 0x9A, 0xF7, 0x49, 0x80, 0xE3, 0x38,
            0xE3, 0x8E, 0x38, 0x63, 0x4E, 0x58, 0x53, 0x56,
            0xD4, 0x95, 0xB5, 0x25, 0x6D, 0xC9, 0x1B, 0xF2,
            0x86, 0x3C, 0x61, 0x4F, 0x58, 0xD3, 0x96, 0x05,
            0x4B, 0xDE, 0x10, 0x80, 0xE6, 0x57, 0x1B, 0x2D,
            0x61, 0xA8, 0x50, 0x91, 0x70, 0x17, 0x2C, 0xC4,
            0x7D, 0x25, 0x6A, 0x90, 0x43, 0xE4, 0xAC, 0xB1,
            0xA6, 0x40, 0x4F, 0xB1, 0x8B, 0xD4
        ];
        var config = new CryptConfig();
        AddCryptKey(config, new CryptoClientKey(67000000, 0x0D93A5FD, 0x0B3DD527F, EncryptionType.Twofish));

        var state = new CryptoState();
        var decoded = state.DetectAndDecryptLogin(0x0100007F, raw, config,
            useCrypt: true, useNoCrypt: true);

        Assert.Null(decoded);
        Assert.False(state.IsInitialized);
        Assert.Contains("account='mortal'", state.LastDetectionDiagnostic);
    }

    [Fact]
    public void CryptoState_NoCryptGameLogin_DetectsValidRelayPacket()
    {
        byte[] packet = new byte[65];
        packet[0] = 0x91;
        packet[34] = 0x00;
        packet[64] = 0x00;

        var state = new CryptoState();
        var decoded = state.DetectAndDecryptGameLogin(0x01020304, packet, EmptyCryptConfig(),
            useCrypt: false, useNoCrypt: true);

        Assert.NotNull(decoded);
        Assert.Equal(packet, decoded);
        Assert.True(state.IsInitialized);
        Assert.Equal(EncryptionType.None, state.EncType);
    }

    [Fact]
    public void CryptoState_RelayKeys_AreSingleUse()
    {
        const uint authId = 0xAABBCCDD;
        CryptoState.StoreRelayKeys(authId, 0x11111111, 0x22222222, 70011400);

        Assert.True(CryptoState.TryGetRelayKeys(authId, out uint key1, out uint key2, out uint clientVersion));
        Assert.Equal(0x11111111u, key1);
        Assert.Equal(0x22222222u, key2);
        Assert.Equal(70011400u, clientVersion);

        Assert.False(CryptoState.TryGetRelayKeys(authId, out _, out _, out _));
    }

    [Fact]
    public void CryptConfig_ParsesSphereEncryptionMatrix()
    {
        var config = LoadCryptConfig("""
            [SPHERECRYPT]
            2000000 012345678 0ABCDEF01 ENC_BFISH
            3000000 011111111 022222222 ENC_BTFISH
            70011400 037062ADD 0ACCA227F ENC_TFISH
            """);

        Assert.Equal(3, config.Keys.Count);
        Assert.Equal(EncryptionType.Blowfish, config.FindKey(2000000)!.EncType);
        Assert.Equal(EncryptionType.BlowfishTwofish, config.FindKey(3000000)!.EncType);
        Assert.Equal(EncryptionType.Twofish, config.FindKey(70011400)!.EncType);
    }

    [Theory]
    [InlineData(EncryptionType.Blowfish)]
    [InlineData(EncryptionType.Twofish)]
    [InlineData(EncryptionType.BlowfishTwofish)]
    public void CryptoState_RelayGameLogin_DetectsEncryptionMatrix(EncryptionType encType)
    {
        const uint authId = 0x1234ABCD;
        const uint key1 = 0x37062ADD;
        const uint key2 = 0xACCA227F;
        const uint clientVersion = 70011400;

        byte[] plain = new byte[65];
        plain[0] = 0x91;
        plain[34] = 0x00;
        plain[64] = 0x00;
        WriteAsciiFixed(plain, 5, 30, "testacct");
        WriteAsciiFixed(plain, 35, 30, "testpass");
        byte[] encrypted = EncryptRelayGameLogin(plain, authId, key1, key2, encType);

        CryptoState.StoreRelayKeys(authId, key1, key2, clientVersion);
        var state = new CryptoState();
        var decoded = state.DetectAndDecryptGameLogin(authId, encrypted, EmptyCryptConfig(),
            useCrypt: true, useNoCrypt: false);

        Assert.NotNull(decoded);
        Assert.Equal(plain, decoded);
        Assert.Equal(encType, state.EncType);
        Assert.Equal(clientVersion, state.RelayClientVersion);
    }

    [Fact]
    public void CryptoState_RelayKeys_ExpireAfterTtlPurge()
    {
        const uint expiredAuthId = 0x01020304;
        const uint freshAuthId = 0x05060708;
        long now = Environment.TickCount64;

        StoreExpiredRelayForTest(expiredAuthId, 0x11111111, 0x22222222, 0,
            now - 120_000);
        CryptoState.StoreRelayKeys(freshAuthId, 0x33333333, 0x44444444, 70000000);

        Assert.False(CryptoState.TryGetRelayKeys(expiredAuthId, out _, out _, out _));
        Assert.True(CryptoState.TryGetRelayKeys(freshAuthId, out _, out _, out uint version));
        Assert.Equal(70000000u, version);
    }

    [Fact]
    public void Huffman_Decompress_EmptyInput_ReturnsEmpty()
    {
        var result = HuffmanCompression.Decompress([], 0, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Huffman_ServerCompress_DecompressFromServer_RoundTrips()
    {
        byte[] packet =
        [
            0x1B, 0x00, 0x25, 0x40, 0x00, 0x10, 0x00, 0x01,
            0x90, 0x03, 0xE8, 0x03, 0xE8, 0x00, 0x00, 0x00,
            0x00, 0x00
        ];

        var compressed = HuffmanCompression.Compress(packet, 0, packet.Length);
        var decompressed = HuffmanCompression.DecompressFromServer(compressed, 0, compressed.Length, out int consumed);

        Assert.Equal(packet, decompressed);
        Assert.InRange(consumed, 1, compressed.Length);
    }

    [Fact]
    public void Huffman_DecompressFromServer_CapsExpansion()
    {
        byte[] payload = new byte[300_000];
        Array.Fill<byte>(payload, (byte)'e');
        var compressed = HuffmanCompression.Compress(payload, 0, payload.Length);

        var decompressed = HuffmanCompression.DecompressFromServer(compressed, 0, compressed.Length, out int consumed);

        Assert.Equal(262_144, decompressed.Length);
        Assert.Equal(compressed.Length, consumed);
    }

    private static byte[] EncryptRelayGameLogin(byte[] plain, uint authId, uint key1, uint key2, EncryptionType encType)
    {
        byte[] encrypted = (byte[])plain.Clone();
        var login = new LoginEncryption(0, key1, key2, maskLo: 0, maskHi: 0);
        login.Decrypt(encrypted, 0, encrypted.Length);

        uint derivedSeed = DeriveRelaySeed(authId, key1, key2);
        switch (encType)
        {
            case EncryptionType.Blowfish:
                new BlowfishGameEncryption(derivedSeed).Decrypt(encrypted, 0, encrypted.Length);
                break;
            case EncryptionType.Twofish:
                new TwofishGameEncryption(derivedSeed).Decrypt(encrypted, 0, encrypted.Length);
                break;
            case EncryptionType.BlowfishTwofish:
                new BlowfishGameEncryption(derivedSeed).Decrypt(encrypted, 0, encrypted.Length);
                new TwofishGameEncryption(derivedSeed).Decrypt(encrypted, 0, encrypted.Length);
                break;
        }

        return encrypted;
    }

    private static uint DeriveRelaySeed(uint authId, uint key1, uint key2)
    {
        uint xored = key1 ^ key2;
        uint swapped = ((xored >> 24) & 0xFF) |
                       ((xored >> 8) & 0xFF00) |
                       ((xored << 8) & 0xFF0000) |
                       ((xored << 24) & 0xFF000000);
        return swapped ^ authId;
    }

    private static void WriteAsciiFixed(byte[] buffer, int offset, int length, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length - 1, bytes.Length));
    }

    private static void StoreExpiredRelayForTest(uint authId, uint key1, uint key2, uint clientVersion, long storedAt)
    {
        var pending = (System.Collections.IDictionary)typeof(CryptoState)
            .GetField("_pendingRelays", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        pending[authId] = (key1, key2, clientVersion, storedAt);
        typeof(CryptoState)
            .GetField("_lastRelayPurge", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, storedAt);
    }
}
