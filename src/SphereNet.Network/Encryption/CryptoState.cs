using System.Buffers;
using System.Collections.Concurrent;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;

namespace SphereNet.Network.Encryption;

/// <summary>
/// Per-connection crypto state. Handles auto-detection and decryption
/// of incoming client data. Maps to CCrypto in Source-X.
/// </summary>
public sealed class CryptoState
{
    private LoginEncryption? _loginCrypt;
    private TwofishGameEncryption? _twofishCrypt;
    private BlowfishGameEncryption? _blowfishCrypt;
    private Md5GameEncryption? _md5Encrypt;
    private EncryptionType _encType = EncryptionType.None;
    private uint _key1;
    private uint _key2;
    private uint _seed;
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public EncryptionType EncType => _encType;
    public uint Key1 => _key1;
    public uint Key2 => _key2;
    public string LastDetectionDiagnostic { get; private set; } = "";

    /// <summary>Client version number recovered from relay keys during game login detection.</summary>
    public uint RelayClientVersion { get; private set; }

    /// <summary>
    /// Pending relay keys: authId → (MasterHi=Key1, MasterLo=Key2) from the login detection.
    /// Source-X RelayGameCryptStart uses these to derive the game Twofish seed.
    /// An entry is valid for 60 seconds after the relay (0x8C) that stored it: a game
    /// login later than that does not get the keys (the age is checked on every read),
    /// and expired entries are purged by the network tick whether or not new relays
    /// arrive. The cache is also bounded, oldest entries first, so a flood of relays
    /// that never log in cannot grow it.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, (uint Key1, uint Key2, uint ClientVersion, long StoredAt)> _pendingRelays = new();
    private static long _lastRelayPurge = Environment.TickCount64;
    private const long RelayTtlMs = 60_000;
    private const int MaxRelayEntries = 4096;

    private static readonly EncryptionType[] DirectGameCandidates =
        [EncryptionType.Blowfish, EncryptionType.BlowfishTwofish, EncryptionType.Twofish];

    public static void StoreRelayKeys(uint authId, uint key1, uint key2, uint clientVersion = 0)
    {
        long now = Environment.TickCount64;
        _pendingRelays[authId] = (key1, key2, clientVersion, now);

        PurgeExpiredRelayKeys(now);
        if (_pendingRelays.Count > MaxRelayEntries)
            TrimRelayKeys(now);
    }

    /// <summary>Drop relay entries past their TTL; runs at most once per TTL period.
    /// Called from the network tick, so it does not depend on new relays arriving.</summary>
    public static void PurgeExpiredRelayKeys(long now)
    {
        if (now - Interlocked.Read(ref _lastRelayPurge) <= RelayTtlMs)
            return;
        Interlocked.Exchange(ref _lastRelayPurge, now);
        foreach (var kv in _pendingRelays)
        {
            if (now - kv.Value.StoredAt > RelayTtlMs)
                _pendingRelays.TryRemove(kv.Key, out _);
        }
    }

    private static void TrimRelayKeys(long now)
    {
        foreach (var kv in _pendingRelays)
            if (now - kv.Value.StoredAt > RelayTtlMs)
                _pendingRelays.TryRemove(kv.Key, out _);

        int excess = _pendingRelays.Count - MaxRelayEntries;
        if (excess <= 0)
            return;
        foreach (var kv in _pendingRelays.OrderBy(kv => kv.Value.StoredAt).Take(excess))
            _pendingRelays.TryRemove(kv.Key, out _);
    }

    public static bool TryGetRelayKeys(uint authId, out uint key1, out uint key2, out uint clientVersion)
    {
        if (_pendingRelays.TryRemove(authId, out var keys) &&
            Environment.TickCount64 - keys.StoredAt <= RelayTtlMs)
        {
            key1 = keys.Key1;
            key2 = keys.Key2;
            clientVersion = keys.ClientVersion;
            return true;
        }
        key1 = key2 = 0;
        clientVersion = 0;
        return false;
    }

    public byte[]? DetectAndDecryptLogin(uint seed, ReadOnlySpan<byte> rawData, CryptConfig cryptConfig, bool useCrypt, bool useNoCrypt)
    {
        _seed = seed;
        LastDetectionDiagnostic = "";
        if (rawData.IsEmpty)
            return null;

        if (useNoCrypt)
        {
            if (IsValidLoginPacket(rawData))
            {
                _encType = EncryptionType.None;
                _initialized = true;
                return rawData.ToArray();
            }
        }

        if (!useCrypt)
            return null;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(rawData.Length);
        try
        {
            foreach (var clientKey in cryptConfig.Keys)
            {
                foreach (var mode in GetLoginEncryptionModes(clientKey))
                {
                    for (int keyOrder = 0; keyOrder < 2; keyOrder++)
                    {
                        bool swappedKeys = keyOrder == 1;
                        uint key1 = swappedKeys ? clientKey.Key2 : clientKey.Key1;
                        uint key2 = swappedKeys ? clientKey.Key1 : clientKey.Key2;

                        rawData.CopyTo(scratch);
                        var testCrypt = new LoginEncryption(seed, key1, key2, mode);
                        testCrypt.Decrypt(scratch, 0, rawData.Length);

                        if (IsValidLoginPacket(scratch.AsSpan(0, rawData.Length)))
                        {
                            _key1 = key1;
                            _key2 = key2;
                            _encType = clientKey.EncType;
                            _loginCrypt = testCrypt;
                            _initialized = true;
                            return scratch.AsSpan(0, rawData.Length).ToArray();
                        }

                        CaptureLoginDiagnostic(scratch.AsSpan(0, rawData.Length), clientKey, swappedKeys, mode);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }

        if (useNoCrypt && IsValidLoginPacket(rawData))
        {
            _encType = EncryptionType.None;
            _initialized = true;
            return rawData.ToArray();
        }

        return null;
    }

    private static ReadOnlySpan<LoginEncryptionMode> GetLoginEncryptionModes(CryptoClientKey key)
    {
        // Source-X DecryptLogin switches on the key's client version; 1.25.36 has its
        // own multiply/shift formula that no other key uses.
        if (LoginEncryption.ModeForClientVersion(key.ClientVersion) == LoginEncryptionMode.Special12536)
            return [LoginEncryptionMode.Special12536];

        return key.EncType == EncryptionType.Login
            ? [LoginEncryptionMode.Old, LoginEncryptionMode.Standard]
            : [LoginEncryptionMode.Standard, LoginEncryptionMode.Old];
    }

    private void CaptureLoginDiagnostic(ReadOnlySpan<byte> data, CryptoClientKey key,
        bool swappedKeys = false, LoginEncryptionMode mode = LoginEncryptionMode.Standard)
    {
        if (data.Length < 62 || data[0] != 0x80)
            return;

        // The account name is previewed; the password never is - this text goes into a
        // warning - only whether it decoded to a valid field.
        string account = PreviewLoginField(data.Slice(1, 30));
        bool accountValid = IsValidLoginField(data.Slice(1, 30), requireTerminator: true);
        bool passwordValid = IsValidLoginField(data.Slice(31, 30), requireTerminator: false);
        if (!accountValid && LastDetectionDiagnostic.Contains("accountValid=True", StringComparison.Ordinal))
            return;
        if (!passwordValid && LastDetectionDiagnostic.Contains("passValid=True", StringComparison.Ordinal))
            return;

        string variant = $" mode={mode}" + (swappedKeys ? " keyOrder=swapped" : "");
        LastDetectionDiagnostic =
            $"candidate ver={key.ClientVersion} enc={key.EncType} key1=0x{key.Key1:X8} key2=0x{key.Key2:X8}{variant} accountValid={accountValid} passValid={passwordValid} account='{account}'";
    }

    private static bool IsValidLoginPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 62 || data[0] != 0x80)
            return false;

        // Non-zero padding after the first null is tolerated, but both account
        // and password prefixes must decrypt to printable text. Accepting an
        // account-only candidate can route a wrong cipher into auth and surface
        // as a misleading "wrong password".
        return IsValidLoginField(data.Slice(1, 30), requireTerminator: true) &&
               IsValidLoginField(data.Slice(31, 30), requireTerminator: false);
    }

    private static bool IsValidLoginField(ReadOnlySpan<byte> field, bool requireTerminator)
    {
        int nullIdx = field.IndexOf((byte)0);
        int len = nullIdx >= 0 ? nullIdx : field.Length;
        if (len == 0)
            return !requireTerminator || nullIdx >= 0;

        if (requireTerminator && nullIdx < 0)
            return false;

        for (int i = 0; i < len; i++)
        {
            byte b = field[i];
            if (b < 0x20 || b > 0x7E)
                return false;
        }
        return true;
    }

    private static string PreviewLoginField(ReadOnlySpan<byte> field)
    {
        int nullIdx = field.IndexOf((byte)0);
        int len = nullIdx >= 0 ? nullIdx : field.Length;
        Span<char> chars = stackalloc char[Math.Min(len, 16)];
        for (int i = 0; i < chars.Length; i++)
        {
            byte b = field[i];
            chars[i] = b is >= 0x20 and <= 0x7E ? (char)b : '.';
        }
        return new string(chars);
    }

    /// <summary>
    /// Decrypt incoming data on an already-initialized connection.
    /// </summary>
    public void Decrypt(byte[] data, int offset, int length)
    {
        if (_encType == EncryptionType.None)
            return;

        if (_loginCrypt != null)
        {
            _loginCrypt.Decrypt(data, offset, length);
            return;
        }

        switch (_encType)
        {
            case EncryptionType.Twofish:
                _twofishCrypt?.Decrypt(data, offset, length);
                return;
            case EncryptionType.BlowfishTwofish:
                _twofishCrypt?.Decrypt(data, offset, length);
                _blowfishCrypt?.Decrypt(data, offset, length);
                return;
            case EncryptionType.Blowfish:
                _blowfishCrypt?.Decrypt(data, offset, length);
                return;
        }

        _loginCrypt?.Decrypt(data, offset, length);
    }

    /// <summary>
    /// Encrypt outgoing data (after Huffman compression) for game connections.
    /// Source-X CNetworkOutput: compress → EncryptMD5 → send.
    /// Uses MD5 digest of the initial Twofish cipher table (not Twofish itself).
    /// </summary>
    public void Encrypt(byte[] data, int offset, int length)
    {
        if (_encType == EncryptionType.None)
            return;

        _md5Encrypt?.Encrypt(data, offset, length);
    }

    /// <summary>
    /// Detect encryption on a game login (0x91) connection after relay.
    /// Implements Source-X RelayGameCryptStart: derives Twofish seed from
    /// master keys and authId, then applies both Twofish and login XOR decryption.
    /// </summary>
    public byte[]? DetectAndDecryptGameLogin(uint newSeed, ReadOnlySpan<byte> rawData,
        CryptConfig cryptConfig, bool useCrypt, bool useNoCrypt)
    {
        _seed = newSeed;
        RelayClientVersion = 0;   // only a relay found by THIS detection may set it
        if (rawData.IsEmpty)
            return null;

        // 1) ENC_NONE — check unencrypted
        if (useNoCrypt)
        {
            if (rawData[0] == 0x91 && rawData.Length >= 65 && rawData[34] == 0x00 && rawData[64] == 0x00)
            {
                _encType = EncryptionType.None;
                _initialized = true;
                return rawData.ToArray();
            }
        }

        if (!useCrypt)
            return null;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(rawData.Length);
        try
        {

        // 2) RelayGameCryptStart — exact port of Source-X CCrypto::RelayGameCryptStart.
        if (TryGetRelayKeys(newSeed, out uint relayKey1, out uint relayKey2, out uint relayVer))
        {
            RelayClientVersion = relayVer;
            // Derive new seed (same as Source-X RelayGameCryptStart)
            uint xored = relayKey1 ^ relayKey2;
            uint swapped = ((xored >> 24) & 0xFF) |
                           ((xored >> 8) & 0xFF00) |
                           ((xored << 8) & 0xFF0000) |
                           ((xored << 24) & 0xFF000000);
            uint derivedSeed = swapped ^ newSeed;

            for (int encTry = 0; encTry <= 3; encTry++)
            {
                rawData.CopyTo(scratch);
                TwofishGameEncryption? thisTf = null;
                BlowfishGameEncryption? thisBf = null;

                switch (encTry)
                {
                    case 0: // ENC_NONE — no game-layer decryption
                        break;
                    case 1: // ENC_BFISH — Blowfish only (1.26.x – 2.0.0)
                        thisBf = new BlowfishGameEncryption();
                        thisBf.Decrypt(scratch, 0, rawData.Length);
                        break;
                    case 2: // ENC_BTFISH — Twofish then Blowfish (2.0.0x – 2.0.3)
                        thisTf = new TwofishGameEncryption(derivedSeed);
                        thisBf = new BlowfishGameEncryption();
                        thisTf.Decrypt(scratch, 0, rawData.Length);
                        thisBf.Decrypt(scratch, 0, rawData.Length);
                        break;
                    case 3: // ENC_TFISH — Twofish only (3.0.0+)
                        thisTf = new TwofishGameEncryption(derivedSeed);
                        thisTf.Decrypt(scratch, 0, rawData.Length);
                        break;
                }

                if (scratch[0] == 0x91)
                {
                    var loginDecrypt = new LoginEncryption(0, relayKey1, relayKey2, maskLo: 0, maskHi: 0);
                    loginDecrypt.Decrypt(scratch, 0, rawData.Length);

                    if (scratch[0] == 0x91 && rawData.Length >= 65 && scratch[34] == 0x00 && scratch[64] == 0x00)
                    {
                        _key1 = relayKey1;
                        _key2 = relayKey2;
                        _encType = (EncryptionType)encTry;
                        _twofishCrypt = thisTf;
                        _blowfishCrypt = thisBf;
                        _md5Encrypt = encTry == (int)EncryptionType.Twofish && thisTf != null
                            ? new Md5GameEncryption(thisTf.Md5Digest) : null;
                        _loginCrypt = null;
                        _initialized = true;
                        return scratch.AsSpan(0, rawData.Length).ToArray();
                    }
                }
            }
        }

        // 3) GameCryptStart — no relay keys: the game cipher alone, keyed by the
        //    connection seed. Source-X tries ENC_BFISH, ENC_BTFISH, ENC_TFISH in this
        //    order (ENC_NONE was step 1). Blowfish ignores the seed.
        foreach (var gameEnc in DirectGameCandidates)
        {
            rawData.CopyTo(scratch);
            TwofishGameEncryption? testTf = gameEnc != EncryptionType.Blowfish
                ? new TwofishGameEncryption(newSeed) : null;
            BlowfishGameEncryption? testBf = gameEnc != EncryptionType.Twofish
                ? new BlowfishGameEncryption() : null;
            testTf?.Decrypt(scratch, 0, rawData.Length);
            testBf?.Decrypt(scratch, 0, rawData.Length);

            if (scratch[0] == 0x91 && rawData.Length >= 65 && scratch[34] == 0x00 && scratch[64] == 0x00)
            {
                _encType = gameEnc;
                _loginCrypt = null;
                _twofishCrypt = testTf;
                _blowfishCrypt = testBf;
                // Source-X CCrypto::Encrypt MD5s only ENC_TFISH output.
                _md5Encrypt = gameEnc == EncryptionType.Twofish && testTf != null
                    ? new Md5GameEncryption(testTf.Md5Digest) : null;
                _initialized = true;
                return scratch.AsSpan(0, rawData.Length).ToArray();
            }
        }

        // 4) LoginXOR fallback — for older clients
        foreach (var clientKey in cryptConfig.Keys)
        {
            if (clientKey.EncType != EncryptionType.Login)
                continue;

            rawData.CopyTo(scratch);
            var testCrypt = new LoginEncryption(newSeed, clientKey.Key1, clientKey.Key2,
                LoginEncryption.ModeForClientVersion(clientKey.ClientVersion));
            testCrypt.Decrypt(scratch, 0, rawData.Length);

            if (scratch[0] == 0x91 && rawData.Length >= 65 && scratch[34] == 0x00 && scratch[64] == 0x00)
            {
                _key1 = clientKey.Key1;
                _key2 = clientKey.Key2;
                _encType = clientKey.EncType;
                _loginCrypt = testCrypt;
                _twofishCrypt = null;
                _initialized = true;
                return scratch.AsSpan(0, rawData.Length).ToArray();
            }
        }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }

        if (useNoCrypt && rawData[0] == 0x91 && rawData.Length >= 65)
        {
            _encType = EncryptionType.None;
            _initialized = true;
            return rawData.ToArray();
        }

        return null;
    }

    public void Reset()
    {
        _loginCrypt = null;
        _twofishCrypt = null;
        _blowfishCrypt = null;
        _md5Encrypt = null;
        _encType = EncryptionType.None;
        _key1 = 0;
        _key2 = 0;
        _seed = 0;
        _initialized = false;
        RelayClientVersion = 0;
        LastDetectionDiagnostic = "";
    }

    /// <summary>
    /// Whether <paramref name="prefix"/> (fewer than 65 bytes) decrypts, under one of
    /// the game-login candidates, to the start of a 0x91 game login: opcode 0x91 and,
    /// once byte 34 has arrived, the account terminator. Used to tell "the game login
    /// has not fully arrived yet" from "detection failed". The ciphers are stream
    /// ciphers, so a prefix decrypts the same as the start of the whole packet. Reads
    /// relay keys without consuming them and changes no state.
    /// </summary>
    public static bool IsGameLoginPrefix(uint seed, ReadOnlySpan<byte> prefix, CryptConfig cryptConfig)
    {
        if (prefix.IsEmpty)
            return false;

        byte[] scratch = new byte[prefix.Length];
        bool Matches() => scratch[0] == 0x91 && (scratch.Length <= 34 || scratch[34] == 0x00);

        if (_pendingRelays.TryGetValue(seed, out var relay) &&
            Environment.TickCount64 - relay.StoredAt <= RelayTtlMs)
        {
            uint xored = relay.Key1 ^ relay.Key2;
            uint swapped = ((xored >> 24) & 0xFF) | ((xored >> 8) & 0xFF00) |
                           ((xored << 8) & 0xFF0000) | ((xored << 24) & 0xFF000000);
            uint derivedSeed = swapped ^ seed;
            for (int encTry = 0; encTry <= 3; encTry++)
            {
                prefix.CopyTo(scratch);
                if (encTry is 2 or 3)
                    new TwofishGameEncryption(derivedSeed).Decrypt(scratch, 0, scratch.Length);
                if (encTry is 1 or 2)
                    new BlowfishGameEncryption().Decrypt(scratch, 0, scratch.Length);
                new LoginEncryption(0, relay.Key1, relay.Key2, maskLo: 0, maskHi: 0).Decrypt(scratch, 0, scratch.Length);
                if (Matches())
                    return true;
            }
        }

        // The direct GameCryptStart candidates, in the same order and composition
        // as TryDetect step 3 (Blowfish ignores the seed).
        foreach (var gameEnc in DirectGameCandidates)
        {
            prefix.CopyTo(scratch);
            if (gameEnc != EncryptionType.Blowfish)
                new TwofishGameEncryption(seed).Decrypt(scratch, 0, scratch.Length);
            if (gameEnc != EncryptionType.Twofish)
                new BlowfishGameEncryption().Decrypt(scratch, 0, scratch.Length);
            if (Matches())
                return true;
        }

        foreach (var clientKey in cryptConfig.Keys)
        {
            if (clientKey.EncType != EncryptionType.Login)
                continue;
            prefix.CopyTo(scratch);
            new LoginEncryption(seed, clientKey.Key1, clientKey.Key2,
                LoginEncryption.ModeForClientVersion(clientKey.ClientVersion)).Decrypt(scratch, 0, scratch.Length);
            if (Matches())
                return true;
        }
        return false;
    }
}
