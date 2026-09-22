using System.Diagnostics;
using Serilog.Events;
using Serilog.Parsing;
using SphereNet.Server.Logging;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Writing a log line must not be able to stall the caller.
///
/// It could, and on a live shard it did — twice, from two different debug flags
/// through the same synchronous console sink:
///
///   [world_callback_detail] kind=item_timer total=15354.0ms alloc=18KB
///   [slow_tick] total=4004,8ms dominant=apply apply=4004,2ms gc=0,0ms/0/0/0
///
/// Both with zero GC and almost no allocation across the whole stall, which is the
/// signature of a blocked thread rather than a busy one, and both with a p50 of
/// 0.2 ms either side. A Windows console write is not reliably fast: a selection in
/// the window suspends output until it is cleared, and the writer waits inside it.
/// With SCRIPTDEBUG or DEBUGPACKETS on, the thread doing that writing is the main
/// loop, thousands of times a second.
///
/// These pin the three properties that make that impossible, not the incident.
/// </summary>
public sealed class NonBlockingLogSinkTests(ITestOutputHelper output)
{
    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent Event(string text) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, null, Parser.Parse(text), []);

    [Fact]
    public void EmitReturnsAtOnceWhileTheInnerSinkIsWedged()
    {
        using var wedged = new ManualResetEventSlim(false);
        int written = 0;
        using var sink = new NonBlockingSink(_ =>
        {
            wedged.Wait(TimeSpan.FromSeconds(30));   // the stuck console
            Interlocked.Increment(ref written);
        });

        // The first event reaches the writer thread and parks there. Everything
        // after it must still be handed over without waiting.
        sink.Emit(Event("first"));
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5_000; i++)
            sink.Emit(Event("line"));
        clock.Stop();

        output.WriteLine($"5,000 emits against a wedged sink took {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.ElapsedMilliseconds < 1_000,
            $"Emit blocked behind the inner sink ({clock.ElapsedMilliseconds} ms)");

        wedged.Set();
    }

    [Fact]
    public void EventsReachTheInnerSinkInOrder()
    {
        var seen = new List<string>();
        using (var sink = new NonBlockingSink(e =>
        {
            lock (seen) seen.Add(e.MessageTemplate.Text);
        }))
        {
            for (int i = 0; i < 200; i++)
                sink.Emit(Event(i.ToString()));
        }   // Dispose drains the backlog before returning.

        Assert.Equal(200, seen.Count);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString()), seen);
    }

    [Fact]
    public void AnOverflowIsDroppedAndThenReported()
    {
        using var wedged = new ManualResetEventSlim(false);
        var seen = new List<string>();
        var sink = new NonBlockingSink(e =>
        {
            if (e.MessageTemplate.Text == "first")
                wedged.Wait(TimeSpan.FromSeconds(30));
            lock (seen) seen.Add(e.MessageTemplate.Text);
        });

        sink.Emit(Event("first"));
        // Far more than the queue holds, so the overflow has to go somewhere. It
        // goes nowhere — dropping a debug line is the whole point — but the gap
        // must not be silent, or a log with a hole in it reads as a server that
        // stopped doing anything.
        for (int i = 0; i < 40_000; i++)
            sink.Emit(Event("flood"));

        wedged.Set();
        sink.Dispose();

        string[] notices;
        lock (seen)
            notices = seen.Where(t => t.StartsWith("[log] dropped ")).ToArray();
        output.WriteLine($"{seen.Count} written, {notices.Length} drop notice(s): " +
                         (notices.FirstOrDefault() ?? "-"));
        Assert.NotEmpty(notices);
        Assert.Contains("console could not keep up", notices[0]);
    }

    [Fact]
    public void AThrowingInnerSinkDoesNotSilenceEverythingAfterIt()
    {
        var seen = new List<string>();
        using (var sink = new NonBlockingSink(e =>
        {
            if (e.MessageTemplate.Text == "boom") throw new IOException("console gone");
            lock (seen) seen.Add(e.MessageTemplate.Text);
        }))
        {
            sink.Emit(Event("before"));
            sink.Emit(Event("boom"));
            sink.Emit(Event("after"));
        }

        // A console that faults once — an RDP session dropping, say — must not take
        // the writer thread down with it and leave the shard running blind.
        Assert.Equal(["before", "after"], seen);
    }
}
