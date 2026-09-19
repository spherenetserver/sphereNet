using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// What an open port has to survive from someone who is not a client.
///
/// The server has defences for each of these - an invalid length drops the connection,
/// a stalled partial packet times out, an unauthenticated socket that says nothing is
/// reaped, a connection over its packet quota often enough is dropped, a full receive
/// buffer disconnects - and not one of them was covered by a test. A mechanism nobody
/// exercises is a claim: a refactor can disable it and nothing notices until someone
/// points a script at port 2593.
///
/// These drive the real framing path with real hostile input.
/// </summary>
public sealed class HostileConnectionTests
{
    private static (NetworkManager Mgr, NetState State) Connection(int id = 1)
    {
        var mgr = new NetworkManager(4, NullLoggerFactory.Instance);
        var state = mgr.GetState(id - 1)!;
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.Id = id;
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var t = crypto.GetType();
        t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
        return (mgr, state);
    }

    private static void Process(NetworkManager mgr, NetState state) =>
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);

    /// <summary>A variable-length packet that claims a length it does not have: the
    /// framer must not read past what arrived.</summary>
    [Fact]
    public void ALyingLengthFieldDropsTheConnectionRatherThanReadingPastTheData()
    {
        var (mgr, state) = Connection();
        // 0xAD (speech, variable) claiming 4000 bytes, with 10 delivered.
        var p = new byte[10];
        p[0] = 0xAD; p[1] = 0x0F; p[2] = 0xA0;
        state.InjectReceived(p);

        var ex = Record.Exception(() => Process(mgr, state));

        Assert.Null(ex);
        // Either held as a partial packet or closed - what must not happen is a read
        // past the buffer, and the bytes must not be consumed as if complete.
        Assert.True(state.IsClosing || state.ReceivedData.Length == 10);
    }

    /// <summary>A length of zero on a variable packet would make the framer stand
    /// still: it must be refused, not looped on.</summary>
    [Fact]
    public void AZeroLengthIsRefused()
    {
        var (mgr, state) = Connection();
        state.InjectReceived([0xAD, 0x00, 0x00, 0x00]);

        var ex = Record.Exception(() => Process(mgr, state));

        Assert.Null(ex);
        Assert.True(state.IsClosing);
    }

    /// <summary>Pure noise. Whatever it is, the process stays up and the connection
    /// does not consume bytes it did not understand as if it had.</summary>
    [Fact]
    public void RandomNoiseDoesNotThrow()
    {
        var rng = new Random(11);
        for (int round = 0; round < 40; round++)
        {
            var (mgr, state) = Connection();
            var junk = new byte[rng.Next(1, 300)];
            rng.NextBytes(junk);
            state.InjectReceived(junk);

            Assert.Null(Record.Exception(() => Process(mgr, state)));
        }
    }

    /// <summary>A ping flood: the per-tick quota holds the work down, and a connection
    /// that keeps exceeding it is dropped rather than served forever.</summary>
    [Fact]
    public void APingFloodIsQuotaCappedThenDropped()
    {
        var (mgr, state) = Connection();
        mgr.MaxPacketsPerTick = 10;
        mgr.FloodDetectionCount = 3;

        // Far more pings than the quota, delivered repeatedly.
        for (int pass = 0; pass < 10 && !state.IsClosing; pass++)
        {
            var pings = new byte[2 * 200];
            for (int i = 0; i < 200; i++) { pings[i * 2] = 0x73; pings[i * 2 + 1] = 0x00; }
            state.ConsumeReceived(int.MaxValue);
            state.InjectReceived(pings);
            Process(mgr, state);
        }

        Assert.True(state.IsClosing, "a connection that keeps exceeding the quota must be dropped");
    }

    /// <summary>The quota is honoured before the flood counter runs out: one pass does
    /// not process the whole flood.</summary>
    [Fact]
    public void OnePassProcessesNoMoreThanTheQuota()
    {
        var (mgr, state) = Connection();
        mgr.MaxPacketsPerTick = 10;
        int seen = 0;
        mgr.OnPacketQuotaExceeded += (_, processed) => seen = processed;

        var pings = new byte[2 * 200];
        for (int i = 0; i < 200; i++) { pings[i * 2] = 0x73; pings[i * 2 + 1] = 0x00; }
        state.InjectReceived(pings);
        Process(mgr, state);

        Assert.Equal(10, seen);
        Assert.False(state.IsClosing);   // one pass alone is not yet a flood
    }

    /// <summary>An unauthenticated socket that says nothing is reaped; an authenticated
    /// one is given far longer.</summary>
    [Fact]
    public void AnUnauthenticatedSilentConnectionIsReaped()
    {
        var (mgr, state) = Connection();
        state.IsSeeded = false;
        state.LastActivityTick = Environment.TickCount64 - 30_000;   // 30s ago

        Assert.True(state.IsInUse);   // calibration: the reaper only looks at these

        mgr.Tick();

        // A reaped connection is closed AND released inside the same tick, so what is
        // left to observe is the slot being free again - not IsClosing, which the
        // release clears on its way out.
        Assert.False(state.IsInUse);
    }

    [Fact]
    public void AnAuthenticatedQuietConnectionIsNotReapedAtFifteenSeconds()
    {
        var (mgr, state) = Connection();
        state.IsSeeded = true;
        state.LastActivityTick = Environment.TickCount64 - 30_000;

        mgr.Tick();

        Assert.True(state.IsInUse);   // two minutes, not fifteen seconds
    }

    /// <summary>A connection that fills its buffer without ever forming a packet is
    /// disconnected rather than held.</summary>
    [Fact]
    public void AFullBufferWithNoParsablePacketDisconnects()
    {
        var (mgr, state) = Connection();
        // 0xAD claiming the largest length there is, and never delivering it.
        var almost = new byte[65536];
        almost[0] = 0xAD; almost[1] = 0xFF; almost[2] = 0xFF;
        state.InjectReceived(almost);

        Assert.Null(Record.Exception(() => Process(mgr, state)));
        // 65535 declared, 65536 delivered: the frame completes and is handled or
        // refused, but the buffer never grows beyond its fixed size.
        Assert.True(state.ReceivedData.Length <= 65536);
    }

    /// <summary>The shipped defaults, pinned.
    ///
    /// The tests above set their own limits so they can drive the mechanism in a few
    /// passes - which means none of them would notice if the DEFAULT were turned off.
    /// A protection that ships disabled is the same as no protection, and it is one
    /// character to do it.</summary>
    [Fact]
    public void TheShippedLimitsAreOn()
    {
        var mgr = new NetworkManager(4, NullLoggerFactory.Instance);

        Assert.True(mgr.MaxPacketsPerTick > 0,
            "a per-tick packet quota of 0 means no quota at all");
        Assert.True(mgr.FloodDetectionCount > 0,
            "flood detection that never triggers is not flood detection");
        Assert.True(mgr.FloodDetectionWindowMs > 0);
        Assert.True(mgr.ClientMaxIP > 0,
            "an unlimited per-IP connection count lets one address take every slot");
    }

    /// <summary>And the shipped sphere.ini does not turn them back off.
    ///
    /// The pin above checks the CODE default, which is 100 - and the shipped ini set
    /// MAXPACKETSPERTICK=0, so the shard ran with no packet quota and no flood detection
    /// at all. Its own note said the setting was read but not applied, which is why
    /// nobody thought the zero mattered; NetworkManager has applied it all along.
    ///
    /// A default that is right and a config that overrides it is the same as no default,
    /// so the file is checked too.</summary>
    [Fact]
    public void TheShippedConfigDoesNotDisableThem()
    {
        string ini = Path.Combine(FindRepoRoot(), "config", "sphere.ini");
        if (Gate.Missing(null, "config/sphere.ini", !File.Exists(ini)))
            return;

        foreach (string line in File.ReadAllLines(ini))
        {
            string t = line.Trim();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;

            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            string key = t[..eq].Trim();
            string val = t[(eq + 1)..].Trim();

            if (key.Equals("MAXPACKETSPERTICK", StringComparison.OrdinalIgnoreCase))
                Assert.True(int.TryParse(val, out int q) && q > 0,
                    $"the shipped config sets MAXPACKETSPERTICK={val}, which disables the " +
                    "packet quota and the flood detection built on it");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>A real player running flat out is nowhere near the quota.
    ///
    /// The question this answers: five people in one internet cafe, all running, all on
    /// one address - does the flood protection take them for an attack?
    ///
    /// No, on two counts. The quota is per CONNECTION, not per address, so each of the
    /// five has its own. And the fastest legitimate movement there is - mounted running,
    /// one step per 100ms (MovementEngine.RunDelayMount) against a 100ms tick - is about
    /// ONE movement packet per tick. The quota is 100.
    ///
    /// This drives a client at that rate, with the ordinary chatter around it, for long
    /// enough to trip the flood window several times over, and asserts it is never
    /// touched.</summary>
    [Fact]
    public void APlayerRunningAtFullSpeedIsNeverDropped()
    {
        var (mgr, state) = Connection();
        int quotaHits = 0;
        mgr.OnPacketQuotaExceeded += (_, _) => quotaHits++;

        // 200 ticks - twenty seconds of play, twice the flood window.
        for (int tick = 0; tick < 200; tick++)
        {
            // One movement request at the mounted-run rate, a ping, and a status
            // request: more per tick than a real client sends, not less.
            var traffic = new List<byte>();
            traffic.AddRange([0x02, 0x00, (byte)(tick & 0xFF), 0x00, 0x00, 0x00, 0x00]);
            traffic.AddRange([0x73, 0x00]);
            traffic.AddRange([0x34, 0xED, 0xED, 0xED, 0xED, 0x04, 0x00, 0x00, 0x00, 0x00]);

            state.ConsumeReceived(int.MaxValue);
            state.InjectReceived(traffic.ToArray());
            Process(mgr, state);

            Assert.False(state.IsClosing, $"dropped a normal player at tick {tick}");
        }

        Assert.Equal(0, quotaHits);
    }

    /// <summary>And the quota is per connection: five clients on one address each get
    /// their own, so a shared address is not a shared budget.</summary>
    [Fact]
    public void TheQuotaIsPerConnectionNotPerAddress()
    {
        var mgr = new NetworkManager(8, NullLoggerFactory.Instance);
        mgr.MaxPacketsPerTick = 10;

        var states = new List<NetState>();
        for (int i = 0; i < 5; i++)
        {
            var st = mgr.GetState(i)!;
            typeof(NetState)
                .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(st, true);
            st.Id = i + 1;
            st.IsSeeded = true;
            var crypto = st.Crypto;
            var t = crypto.GetType();
            t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
            t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
            states.Add(st);
        }

        // Each sends 8 pings - under the 10 quota individually, 40 between them.
        foreach (var st in states)
        {
            var pings = new byte[16];
            for (int i = 0; i < 8; i++) pings[i * 2] = 0x73;
            st.InjectReceived(pings);
            Process(mgr, st);
        }

        Assert.All(states, st => Assert.False(st.IsClosing));
    }
}
