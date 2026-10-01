using System;
using System.Linq;
using SphereNet.Network.Encryption;
using SphereNet.Tests.ReferenceClientCrypto;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The game-stream ciphers, per encryption type (parity matrix: relay/game crypto).
///
/// The existing encryption tests cover the block ciphers and the login crypt. The game
/// stream is a different thing built on top of them, and differs per type:
/// ENC_TFISH (3.0.0+) XORs a 256-byte table that is re-encrypted whenever it runs out;
/// ENC_BFISH (1.26-2.0.0) is 8-byte ciphertext feedback over fixed key schedules that
/// switch every 21036 bytes; ENC_BTFISH (2.0.0x-2.0.3) applies both.
///
/// The client side of every Blowfish vector comes from the client encryptor copy in
/// ReferenceClientCrypto/, never from the server class, so a server cipher that only
/// agrees with itself fails (byte-equality vectors: LegacyClientCryptoVectorTests).
/// </summary>
public sealed class GameStreamEncryptionTests
{
    private readonly ITestOutputHelper _out;
    public GameStreamEncryptionTests(ITestOutputHelper output) => _out = output;

    private const uint Seed = 0x7F000001;      // the client's own address, as UO sends it

    private static byte[] Payload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(i * 7 + 3);
        return data;
    }

    // ---- one cipher at a time --------------------------------------------

    [Theory]
    [InlineData(8)]          // exactly one feedback block
    [InlineData(65)]         // a game login
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(21037)]      // one byte past the key-schedule switch
    public void ABlowfishStreamFromTheClientDecryptsExactly(int length)
    {
        byte[] plain = Payload(length);
        byte[] wire = ReferenceClient.Blowfish(plain);                   // the client
        Assert.NotEqual(plain, wire);

        new BlowfishGameEncryption().Decrypt(wire, 0, wire.Length);      // the server
        Assert.Equal(plain, wire);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    public void ATwofishStreamComesBackAsItself(int length)
    {
        byte[] plain = Payload(length);
        byte[] wire = (byte[])plain.Clone();

        new TwofishGameEncryption(Seed).Decrypt(wire, 0, wire.Length);
        Assert.NotEqual(plain, wire);

        new TwofishGameEncryption(Seed).Decrypt(wire, 0, wire.Length);
        Assert.Equal(plain, wire);
    }

    // ---- both, and in which order ----------------------------------------

    [Fact]
    public void ABlowfishTwofishStreamComesBackOnlyInTheServerOrder()
    {
        byte[] plain = Payload(300);

        // The client applies Blowfish and then Twofish; the server undoes Twofish and
        // then Blowfish (Source-X CCrypto::Decrypt; CryptoState.Decrypt for
        // EncryptionType.BlowfishTwofish).
        byte[] wire = ReferenceClient.BlowfishTwofish(Seed, plain);
        Assert.NotEqual(plain, wire);

        byte[] asTheServerDoesIt = (byte[])wire.Clone();
        new TwofishGameEncryption(Seed).Decrypt(asTheServerDoesIt, 0, asTheServerDoesIt.Length);
        new BlowfishGameEncryption().Decrypt(asTheServerDoesIt, 0, asTheServerDoesIt.Length);
        Assert.Equal(plain, asTheServerDoesIt);

        // The order is not cosmetic: the Blowfish layer feeds its own ciphertext back
        // into the next block, so it has to see exactly the bytes the client's Blowfish
        // produced - with the Twofish layer still on them, it reads garbage.
        byte[] theOtherWayRound = (byte[])wire.Clone();
        new BlowfishGameEncryption().Decrypt(theOtherWayRound, 0, theOtherWayRound.Length);
        new TwofishGameEncryption(Seed).Decrypt(theOtherWayRound, 0, theOtherWayRound.Length);

        _out.WriteLine($"server order restores {asTheServerDoesIt.SequenceEqual(plain)}, " +
                       $"reversed restores {theOtherWayRound.SequenceEqual(plain)}");
        Assert.NotEqual(plain, theOtherWayRound);
    }

    // ---- what a socket actually does --------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ArrivingInPiecesIsTheSameAsArrivingAtOnce(int cipher)
    {
        // A real read gives whatever the network happened to deliver, so the keystream
        // position has to carry across calls. Splitting across the 256-byte table
        // boundary is the case that catches a position reset.
        byte[] plain = Payload(700);

        byte[] whole = (byte[])plain.Clone();
        Stream(cipher).Decrypt(whole, 0, whole.Length);

        byte[] pieces = (byte[])plain.Clone();
        var streaming = Stream(cipher);
        int offset = 0;
        foreach (int chunk in new[] { 17, 239, 1, 255, 188 })
        {
            streaming.Decrypt(pieces, offset, chunk);
            offset += chunk;
        }
        Assert.Equal(plain.Length, offset);

        _out.WriteLine($"cipher {cipher}: piecewise equals whole = {whole.SequenceEqual(pieces)}");
        Assert.Equal(whole, pieces);
    }

    /// <summary>1 = Blowfish, 2 = Twofish, 3 = the MD5 outgoing stream.</summary>
    private static IStream Stream(int cipher) => cipher switch
    {
        1 => new BlowfishStream(new BlowfishGameEncryption()),
        2 => new TwofishStream(new TwofishGameEncryption(Seed)),
        _ => new Md5Stream(new TwofishGameEncryption(Seed).Md5Digest),
    };

    private interface IStream { void Decrypt(byte[] data, int offset, int length); }

    private sealed class BlowfishStream(BlowfishGameEncryption c) : IStream
    {
        public void Decrypt(byte[] d, int o, int l) => c.Decrypt(d, o, l);
    }

    private sealed class TwofishStream(TwofishGameEncryption c) : IStream
    {
        public void Decrypt(byte[] d, int o, int l) => c.Decrypt(d, o, l);
    }

    private sealed class Md5Stream(byte[] digest) : IStream
    {
        private readonly Md5GameEncryption _c = new(digest);
        public void Decrypt(byte[] d, int o, int l) => _c.Encrypt(d, o, l);
    }

    // ---- one client's stream is not another's ------------------------------

    [Fact]
    public void ADifferentSeedIsADifferentKeystream()
    {
        // Twofish is keyed by the seed. Blowfish is not: its key schedules and start
        // register are fixed client tables (Source-X InitBlowFish takes no seed).
        byte[] c = Payload(64);
        byte[] d = (byte[])c.Clone();
        new TwofishGameEncryption(Seed).Decrypt(c, 0, c.Length);
        new TwofishGameEncryption(Seed + 1).Decrypt(d, 0, d.Length);
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void TheOutgoingDigestFollowsTheSeed()
    {
        // The server encrypts what it sends with the MD5 of the initial Twofish table,
        // so that digest has to be a function of the seed and nothing else.
        byte[] first = new TwofishGameEncryption(Seed).Md5Digest;
        byte[] again = new TwofishGameEncryption(Seed).Md5Digest;
        byte[] other = new TwofishGameEncryption(Seed + 1).Md5Digest;

        _out.WriteLine($"digest for {Seed:X8}: {Convert.ToHexString(first)[..16]}...");
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(16, first.Length);
    }
}
