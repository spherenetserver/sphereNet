using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Manager;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The first packet of a connection: encryption detection over a TCP byte stream.
///
/// Detection works on whole login packets, but a socket hands over whatever arrived.
/// The same bytes must mean the same thing however they were cut: the login and what
/// follows it in one read, the login alone, the game login in two pieces at any point.
/// Also here: what the detection is allowed to accept (UseNoCrypt), what it may write
/// to the log (never the password), and the relay key cache it reads.
/// All credentials are synthetic.
/// </summary>
public sealed class LoginCryptoStreamTests
{
    private const uint LoginSeed = 0x7F000001;
    private const uint LoginKey1 = 0x11111111;
    private const uint LoginKey2 = 0x22222222;

    private static readonly MethodInfo s_processInput =
        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static CryptConfig ConfigWith(params CryptoClientKey[] keys)
    {
        var config = new CryptConfig();
        var list = (List<CryptoClientKey>)typeof(CryptConfig)
            .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(config)!;
        list.AddRange(keys);
        return config;
    }

    private static CryptConfig LoginKeyConfig() =>
        ConfigWith(new CryptoClientKey(70000000, LoginKey1, LoginKey2, EncryptionType.Login));

    private static byte[] LoginPacket(string account, string password)
    {
        var b = new byte[62];
        b[0] = 0x80;
        for (int i = 0; i < account.Length && i < 29; i++) b[1 + i] = (byte)account[i];
        for (int i = 0; i < password.Length && i < 30; i++) b[31 + i] = (byte)password[i];
        return b;
    }

    private static byte[] GameLoginPacket(uint authId, string account, string password)
    {
        var b = new byte[65];
        b[0] = 0x91;
        b[1] = (byte)(authId >> 24); b[2] = (byte)(authId >> 16); b[3] = (byte)(authId >> 8); b[4] = (byte)authId;
        for (int i = 0; i < account.Length && i < 29; i++) b[5 + i] = (byte)account[i];
        for (int i = 0; i < password.Length && i < 29; i++) b[35 + i] = (byte)password[i];
        return b;
    }

    private static byte[] StatusRequest(uint serial) =>
        [0x34, 0xED, 0xED, 0xED, 0xED, 0x04,
         (byte)(serial >> 24), (byte)(serial >> 16), (byte)(serial >> 8), (byte)serial];

    private static byte[] LoginEncrypt(byte[] plain)
    {
        var wire = (byte[])plain.Clone();
        new LoginEncryption(LoginSeed, LoginKey1, LoginKey2).Decrypt(wire, 0, wire.Length);
        return wire;
    }

    private static byte[] TwofishEncrypt(uint seed, byte[] plain)
    {
        var wire = (byte[])plain.Clone();
        new TwofishGameEncryption(seed).Decrypt(wire, 0, wire.Length);
        return wire;
    }

    private static (NetworkManager Mgr, NetState State, List<string> Events) Connection(
        uint seed, CryptConfig? config, bool useCrypt, bool useNoCrypt, ILoggerFactory? lf = null)
    {
        lf ??= NullLoggerFactory.Instance;
        var mgr = new NetworkManager(1, lf) { CryptConfig = config, UseCrypt = useCrypt, UseNoCrypt = useNoCrypt };
        var state = mgr.GetState(0)!;
        typeof(NetState).GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.Id = 1;
        state.Seed = seed;
        state.IsSeeded = true;
        var events = new List<string>();
        state.LoginRequestHandler = (_, acct, _) => events.Add("login:" + acct);
        state.GameLoginHandler = (_, acct, _, _) => events.Add("game:" + acct);
        state.ServerSelectHandler = (_, idx) => events.Add("select:" + idx);
        state.StatusRequestHandler = (_, type, serial) => events.Add($"status:{type}:{serial:X8}");
        state.CharDeleteHandler = (_, idx, _) => events.Add("delete:" + idx);
        return (mgr, state, events);
    }

    private static void Feed(NetworkManager mgr, NetState state, byte[] wire, params int[] cuts)
    {
        int at = 0;
        foreach (int end in cuts.Append(wire.Length))
        {
            if (end <= at) continue;
            state.InjectReceived(wire[at..end]);
            s_processInput.Invoke(mgr, [state]);
            at = end;
        }
    }

    // ---- N02: what follows the first encrypted packet is decrypted too --------

    public static IEnumerable<object[]> LoginCuts() =>
    [
        [Array.Empty<int>()],      // one read: login + server select
        [new[] { 62 }],             // login, then the select
        [new[] { 62, 63 }],         // login, then the select in two pieces
        [new[] { 30, 64 }],         // pieces that straddle both packets
    ];

    [Theory]
    [MemberData(nameof(LoginCuts))]
    public void AnEncryptedLoginAndTheNextPacketDecodeTheSameHoweverTheyArrive(int[] cuts)
    {
        var (mgr, state, events) = Connection(LoginSeed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false);
        byte[] wire = LoginEncrypt([.. LoginPacket("streamacct", "synthPw1"), 0xA0, 0x00, 0x07]);

        Feed(mgr, state, wire, cuts);

        Assert.False(state.IsClosing);
        Assert.Equal(["login:streamacct", "select:7"], events);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    public static IEnumerable<object[]> GameCuts() =>
    [
        [Array.Empty<int>()],
        [new[] { 65 }],
        [new[] { 65, 70 }],
        [new[] { 40, 68 }],
    ];

    [Theory]
    [MemberData(nameof(GameCuts))]
    public void ATwofishGameLoginAndTheNextPacketDecodeTheSameHoweverTheyArrive(int[] cuts)
    {
        const uint seed = 0x5A17C0DE;
        var (mgr, state, events) = Connection(seed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false);
        byte[] wire = TwofishEncrypt(seed, [.. GameLoginPacket(seed, "gameacct", "synthPw2"), .. StatusRequest(0x00001234)]);

        Feed(mgr, state, wire, cuts);

        Assert.False(state.IsClosing);
        Assert.Equal(EncryptionType.Twofish, state.Crypto.EncType);
        Assert.Equal(["game:gameacct", "status:4:00001234"], events);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    // ---- N03: a game login cut anywhere is waited for, not refused -------------

    [Theory]
    [InlineData(false)]   // plaintext
    [InlineData(true)]    // Twofish
    public void AGameLoginCutAtAnyPointStillLogsIn(bool encrypted)
    {
        const uint seed = 0x5A17C0DF;
        var failures = new List<int>();
        for (int cut = 1; cut < 65; cut++)
        {
            var (mgr, state, events) = encrypted
                ? Connection(seed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false)
                : Connection(seed, null, useCrypt: false, useNoCrypt: true);
            byte[] plain = GameLoginPacket(seed, "cutacct", "synthPw3");
            byte[] wire = encrypted ? TwofishEncrypt(seed, plain) : plain;

            Feed(mgr, state, wire, cut);

            if (state.IsClosing || !events.SequenceEqual(["game:cutacct"]))
                failures.Add(cut);
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]   // ENC_BFISH
    [InlineData(true)]    // ENC_BTFISH
    public void ABlowfishGameLoginCutAtAnyPointStillLogsIn(bool withTwofish)
    {
        // The prefix check follows the direct GameCryptStart candidates, so a legacy
        // client's first chunk of 62-64 bytes is waited for too.
        const uint seed = 0x5A17C0DF;
        var failures = new List<int>();
        for (int cut = 1; cut < 65; cut++)
        {
            var (mgr, state, events) = Connection(seed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false);
            byte[] plain = GameLoginPacket(seed, "cutacct", "synthPw3");
            byte[] wire = withTwofish
                ? ReferenceClientCrypto.ReferenceClient.BlowfishTwofish(seed, plain)
                : ReferenceClientCrypto.ReferenceClient.Blowfish(plain);

            Feed(mgr, state, wire, cut);

            if (state.IsClosing || !events.SequenceEqual(["game:cutacct"]))
                failures.Add(cut);
        }

        Assert.Empty(failures);
    }

    // ---- N06: no password in any log line --------------------------------------

    private sealed class Capture : ILoggerProvider
    {
        public readonly List<string> Lines = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Lines);
        public void Dispose() { }

        private sealed class Sink(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add(formatter(state, ex));
            }
        }
    }

    private const string SyntheticPassword = "Qx7SynthPwZ";

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2")));

    private static void AssertNoPassword(IEnumerable<string> lines)
    {
        byte[] pw = System.Text.Encoding.ASCII.GetBytes(SyntheticPassword);
        foreach (string line in lines)
        {
            Assert.DoesNotContain(SyntheticPassword[..3], line);
            Assert.DoesNotContain(Hex(pw.AsSpan(0, 2)), line);

            // Every dump of a packet that carries a password has that field masked.
            foreach (var (tokens, isRawCandidate) in Dumps(line))
            {
                if (isRawCandidate)
                {
                    for (int i = 31; i < Math.Min(65, tokens.Length); i++)
                        Assert.True(tokens[i] == "**", $"raw byte {i} unmasked in: {line}");
                    continue;
                }
                int field = tokens.Length == 0 ? -1 : tokens[0] switch { "80" => 31, "91" => 35, "83" => 1, _ => -1 };
                if (field >= 0 && tokens.Length > field)
                    Assert.True(tokens[field] == "**", $"password byte unmasked in: {line}");
            }
        }
    }

    private static IEnumerable<(string[] Tokens, bool IsRawCandidate)> Dumps(string line)
    {
        foreach (string key in new[] { "data=[", "raw=[" })
        {
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) continue;
            int start = at + key.Length;
            int end = line.IndexOf(']', start);
            if (end < 0) continue;
            var tokens = line[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t != "...").ToArray();
            yield return (tokens, key == "raw=[");
        }
    }

    [Fact]
    public void APlaintextLoginNeverLogsThePassword()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var (mgr, state, events) = Connection(LoginSeed, null, useCrypt: false, useNoCrypt: true, lf);
        mgr.DebugPackets = true;

        byte[] delete = new byte[39];
        delete[0] = 0x83;
        System.Text.Encoding.ASCII.GetBytes(SyntheticPassword).CopyTo(delete, 1);
        Feed(mgr, state, [.. LoginPacket("logacct", SyntheticPassword), .. delete]);

        Assert.Equal(["login:logacct", "delete:0"], events);
        Assert.Contains(capture.Lines, l => l.Contains("0x80", StringComparison.Ordinal) && l.Contains("data=[", StringComparison.Ordinal));
        AssertNoPassword(capture.Lines);
    }

    [Fact]
    public void APlaintextGameLoginNeverLogsThePassword()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var (mgr, state, events) = Connection(0x0BADF00D, null, useCrypt: false, useNoCrypt: true, lf);
        mgr.DebugPackets = true;

        Feed(mgr, state, GameLoginPacket(0x0BADF00D, "logacct", SyntheticPassword));

        Assert.Equal(["game:logacct"], events);
        AssertNoPassword(capture.Lines);
    }

    [Fact]
    public void ARefusedPlaintextLoginNeverLogsThePassword()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var (mgr, state, _) = Connection(LoginSeed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false, lf);
        mgr.DebugPackets = true;

        Feed(mgr, state, LoginPacket("logacct", SyntheticPassword));

        Assert.True(state.IsClosing);
        Assert.Contains(capture.Lines, l => l.Contains("raw=[", StringComparison.Ordinal));
        AssertNoPassword(capture.Lines);
    }

    [Fact]
    public void AnEncryptedLoginNeverLogsThePassword()
    {
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var (mgr, state, events) = Connection(LoginSeed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false, lf);
        mgr.DebugPackets = true;

        Feed(mgr, state, LoginEncrypt(LoginPacket("logacct", SyntheticPassword)));

        Assert.Equal(["login:logacct"], events);
        AssertNoPassword(capture.Lines);
    }

    [Fact]
    public void AnEncryptedLoginThatFailsDetectionNeverLogsThePassword()
    {
        // The right key, but the password field carries a control byte, so every
        // candidate is rejected and the failure warning with its diagnostic is written.
        var capture = new Capture();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var (mgr, state, _) = Connection(LoginSeed, LoginKeyConfig(), useCrypt: true, useNoCrypt: false, lf);
        mgr.DebugPackets = true;

        var plain = LoginPacket("logacct", SyntheticPassword);
        plain[31 + SyntheticPassword.Length] = 0x01;
        Feed(mgr, state, LoginEncrypt(plain));

        Assert.True(state.IsClosing);
        Assert.Contains(capture.Lines, l => l.Contains("passValid=False", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Lines, l => l.Contains("pass='", StringComparison.Ordinal));
        AssertNoPassword(capture.Lines);
    }

    // ---- N08: an empty key list does not turn plaintext back on ---------------

    [Theory]
    [InlineData(true)]    // no CryptConfig at all
    [InlineData(false)]   // a CryptConfig with no keys (missing/empty/unparsable file)
    public void WithoutKeysUseNoCryptOffStillRefusesPlaintext(bool nullConfig)
    {
        var (mgr, state, events) = Connection(LoginSeed, nullConfig ? null : new CryptConfig(),
            useCrypt: true, useNoCrypt: false);

        Feed(mgr, state, LoginPacket("plainacct", "synthPw4"));

        Assert.True(state.IsClosing);
        Assert.Empty(events);
    }

    [Fact]
    public void UseNoCryptOnStillAcceptsPlaintextWithoutKeys()
    {
        var (mgr, state, events) = Connection(LoginSeed, new CryptConfig(), useCrypt: true, useNoCrypt: true);

        Feed(mgr, state, LoginPacket("plainacct", "synthPw4"));

        Assert.False(state.IsClosing);
        Assert.Equal(["login:plainacct"], events);
    }

    [Fact]
    public void AnEmptyKeyListIsReportedAtStartup()
    {
        var mgr = new NetworkManager(1, NullLoggerFactory.Instance)
        {
            CryptConfig = new CryptConfig(), UseCrypt = true, UseNoCrypt = false
        };
        Assert.NotEmpty(mgr.GetCryptPolicyWarnings());

        mgr.CryptConfig = LoginKeyConfig();
        Assert.Empty(mgr.GetCryptPolicyWarnings());

        mgr.UseCrypt = false;
        mgr.UseNoCrypt = false;
        Assert.NotEmpty(mgr.GetCryptPolicyWarnings());   // nothing can log in
    }

    // ---- N09: the relay key cache honours its TTL on every read ----------------

    private static System.Collections.IDictionary PendingRelays() =>
        (System.Collections.IDictionary)typeof(CryptoState)
            .GetField("_pendingRelays", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

    [Fact]
    public void AnExpiredRelayEntryIsNotReturnedEvenWithoutAPurge()
    {
        const uint authId = 0x6E000001;
        long now = Environment.TickCount64;
        PendingRelays()[authId] = (0x11111111u, 0x22222222u, 70011400u, now - 65_000);
        // A purge just ran, so nothing else will clear it.
        typeof(CryptoState).GetField("_lastRelayPurge", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, now);

        bool found = CryptoState.TryGetRelayKeys(authId, out _, out _, out _);
        PendingRelays().Remove(authId);

        Assert.False(found);
    }

    [Fact]
    public void ExpiredRelayEntriesArePurgedByTheTickWithoutNewStores()
    {
        const uint authId = 0x6E000002;
        long now = Environment.TickCount64;
        PendingRelays()[authId] = (0x11111111u, 0x22222222u, 0u, now - 65_000);
        typeof(CryptoState).GetField("_lastRelayPurge", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, now - 61_000);

        using var mgr = new NetworkManager(1, NullLoggerFactory.Instance);
        mgr.Tick();

        bool stillThere = PendingRelays().Contains(authId);
        PendingRelays().Remove(authId);
        Assert.False(stillThere);
    }

    [Fact]
    public void TheRelayCacheIsBounded()
    {
        const uint firstId = 0x6F000000;
        const int filler = 5000;
        long now = Environment.TickCount64;
        var pending = PendingRelays();
        try
        {
            // Old but not expired: only a size bound removes these.
            for (uint i = 0; i < filler; i++)
                pending[firstId + i] = (1u, 2u, 0u, now - 30_000);

            CryptoState.StoreRelayKeys(0x6EFFFFFF, 3, 4, 0);

            Assert.True(pending.Count <= 4096 + 64, $"relay cache grew to {pending.Count}");
            Assert.True(CryptoState.TryGetRelayKeys(0x6EFFFFFF, out uint k1, out _, out _));
            Assert.Equal(3u, k1);
        }
        finally
        {
            for (uint i = 0; i < filler; i++)
                pending.Remove(firstId + i);
        }
    }
}
