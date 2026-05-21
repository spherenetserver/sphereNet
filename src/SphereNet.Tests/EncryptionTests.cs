using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;

namespace SphereNet.Tests;

public class EncryptionTests
{
    private static CryptConfig EmptyCryptConfig() => new();

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
}
