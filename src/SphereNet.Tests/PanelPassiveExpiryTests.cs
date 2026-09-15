using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Panel;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Logging;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The panel client that only listens (review work item D08).
///
/// A panel session that sends commands revalidates its token on every one of them, so
/// an expired one is caught there. A session that just watches the log and stats stream
/// sends nothing, and the only thing that ever notices its token has expired is the
/// periodic purge — whose event closes the connection. That purge shared a try block
/// with the stats broadcast, after it, so anything that made the broadcast throw took
/// the expiry with it: with the game server down (the stats callback throws) or a
/// client slow enough to fault the send, a listener that should have been cut off kept
/// receiving everything.
///
/// The second half is the pressure the same stream puts on the server. The log sink
/// queued without a bound and sent without watching the result, so a shard logging
/// faster than the panel can take it grew a queue with no end, drained the whole of it
/// into one SignalR frame, and left several of those in flight at once whenever a
/// client was slow.
/// </summary>
public sealed class PanelPassiveExpiryTests
{
    private readonly ITestOutputHelper _out;
    public PanelPassiveExpiryTests(ITestOutputHelper output) => _out = output;

    private static ServerStats Stats() => new(
        ServerName: "test", Uptime: "0:00", UptimeSeconds: 0, OnlinePlayers: 0,
        TotalChars: 0, TotalItems: 0, TotalSectors: 0, TickCount: 0, MemoryMB: 0,
        Accounts: 0);

    // ---- expiry is not a stats concern ----------------------------------

    [Fact]
    public async Task AFailingStatsPushStillExpiresTokens()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var tokens = new TokenStore(TimeSpan.FromMinutes(30), () => now);
        string token = tokens.Create();

        var invalidated = new List<string>();
        tokens.TokenInvalidated += t => invalidated.Add(t);

        // The game server is down, so producing the stats throws - the shape this takes
        // in practice, and the one that used to skip the purge on every round.
        now = now.AddHours(1);
        await PanelHost.StatsRound(
            () => throw new InvalidOperationException("game server unavailable"),
            (_, _) => Task.CompletedTask,
            tokens, NullLogger.Instance, CancellationToken.None);

        _out.WriteLine($"after a failing stats round: {tokens.Count} tokens, " +
                       $"{invalidated.Count} invalidated");

        // The listener's connection is closed by that event and by nothing else.
        Assert.Equal(0, tokens.Count);
        Assert.Equal([token], invalidated);
    }

    [Fact]
    public async Task ABroadcastThatFaultsStillExpiresTokens()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var tokens = new TokenStore(TimeSpan.FromMinutes(30), () => now);
        tokens.Create();
        var invalidated = new List<string>();
        tokens.TokenInvalidated += invalidated.Add;

        now = now.AddHours(1);
        await PanelHost.StatsRound(Stats,
            (_, _) => Task.FromException(new TimeoutException("a client that stopped reading")),
            tokens, NullLogger.Instance, CancellationToken.None);

        Assert.Single(invalidated);
        Assert.Equal(0, tokens.Count);
    }

    [Fact]
    public async Task AStatsRoundStillPushesWhenNothingHasExpired()
    {
        // The control: the separation must not have cost the stats push itself.
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var tokens = new TokenStore(TimeSpan.FromMinutes(30), () => now);
        tokens.Create();
        int pushes = 0;

        await PanelHost.StatsRound(Stats, (_, _) => { pushes++; return Task.CompletedTask; },
            tokens, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, pushes);
        Assert.Equal(1, tokens.Count);
    }

    [Fact]
    public async Task ATokenStillInsideItsLifetimeIsLeftAlone()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var tokens = new TokenStore(TimeSpan.FromMinutes(30), () => now);
        tokens.Create();
        var invalidated = new List<string>();
        tokens.TokenInvalidated += invalidated.Add;

        now = now.AddMinutes(29);
        await PanelHost.StatsRound(Stats, (_, _) => Task.CompletedTask,
            tokens, NullLogger.Instance, CancellationToken.None);

        // A purge that fired early would log every operator out mid-session.
        Assert.Empty(invalidated);
        Assert.Equal(1, tokens.Count);
    }

    // ---- the log stream under pressure ----------------------------------

    [Fact]
    public void ALogFloodIsBoundedAndSaysHowMuchItDropped()
    {
        using var sink = new PanelLogSink();

        // Nothing is draining yet - SetHubContext is what starts the flush timer - and
        // this is exactly the window a shard logs through while the panel is starting.
        for (int i = 0; i < 20_000; i++)
            sink.AddEntry(new LogEntry(DateTime.UtcNow, "Information", $"line {i}", "test"));

        _out.WriteLine($"20,000 lines into an undrained sink: {sink.PendingCount} pending, " +
                       $"{sink.DroppedCount} dropped");

        // A viewer, not a log store: the queue has a ceiling and the loss is countable
        // rather than silent. The file log still has every line.
        Assert.True(sink.PendingCount <= 5000, $"the queue grew to {sink.PendingCount}");
        Assert.Equal(20_000 - sink.PendingCount, sink.DroppedCount);
    }

    [Fact]
    public void TheNewestLinesAreTheOnesKept()
    {
        using var sink = new PanelLogSink();
        for (int i = 0; i < 20_000; i++)
            sink.AddEntry(new LogEntry(DateTime.UtcNow, "Information", $"line {i}", "test"));

        var remaining = DrainBuffer(sink);

        // An operator watching a problem needs the lines around it, which are the most
        // recent ones; the oldest are the ones that go.
        Assert.Equal("line 19999", remaining[^1].Message);
        Assert.StartsWith("line 15", remaining[0].Message);
    }

    private static List<LogEntry> DrainBuffer(PanelLogSink sink)
    {
        var field = typeof(PanelLogSink).GetField("_buffer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var queue = (System.Collections.Concurrent.ConcurrentQueue<LogEntry>)field.GetValue(sink)!;
        return [.. queue];
    }
}
