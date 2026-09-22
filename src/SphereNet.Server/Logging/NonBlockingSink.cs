using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace SphereNet.Server.Logging;

/// <summary>
/// Hands log events to a background writer so that writing one can never stall the
/// caller.
///
/// The console sink writes on the calling thread, and for most of the server that
/// thread is the main loop. A Windows console write is not reliably fast: with
/// QuickEdit on, selecting anything in the window suspends output until the
/// selection is cleared, and the writer blocks in the middle of whatever it was
/// doing. Over a slow remote session the redraw alone is enough. With SCRIPTDEBUG
/// on, the server writes a line per trigger and a line per matched body, so the
/// world tick is inside that write thousands of times a second.
///
/// The result on a live shard was a single item timer callback measured at 15.4
/// SECONDS while allocating 18 KB and causing no GC at all — the signature of a
/// thread that is blocked rather than busy — and a panel stats request timing out
/// behind it. Diagnostics must not be able to do that: a dropped debug line costs
/// nothing, a frozen shard costs everything.
///
/// So events go into a bounded queue and a background thread drains it. When the
/// queue is full the event is DROPPED rather than made to wait, and the count is
/// reported once the writer catches up, so a gap in the log always says so. The
/// file sink is deliberately left alone: it is the durable record, and it already
/// has its own concession for the packet-debug firehose.
/// </summary>
internal sealed class NonBlockingSink : ILogEventSink, IDisposable
{
    /// <summary>Queue depth. Deep enough to ride out a console stall of a few
    /// seconds at ordinary rates, small enough that a permanently wedged console
    /// costs a few MB and not the heap.</summary>
    private const int Capacity = 16_384;

    private readonly Action<LogEvent> _write;
    private readonly IDisposable? _owned;
    private readonly BlockingCollection<LogEvent> _queue =
        new(new ConcurrentQueue<LogEvent>(), Capacity);
    private readonly Thread _writer;
    private static readonly MessageTemplateParser TemplateParser = new();
    private long _dropped;

    public NonBlockingSink(Action<LogEvent> write, IDisposable? owned = null)
    {
        _write = write;
        _owned = owned;
        _writer = new Thread(Drain)
        {
            IsBackground = true,
            Name = "log-writer",
            // Below the main loop: when the box is saturated, the shard ticks and
            // the log waits, not the other way round.
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();
    }

    public void Emit(LogEvent logEvent)
    {
        // TryAdd, never Add: Add blocks when the queue is full, which is the exact
        // thing this class exists to prevent.
        if (!_queue.IsAddingCompleted && _queue.TryAdd(logEvent))
            return;
        Interlocked.Increment(ref _dropped);
    }

    private void Drain()
    {
        foreach (var logEvent in _queue.GetConsumingEnumerable())
        {
            long dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                TryWrite(new LogEvent(logEvent.Timestamp, LogEventLevel.Warning, null,
                    TemplateParser.Parse(
                        "[log] dropped " + dropped + " console line(s): the console could not keep up"),
                    []));
            TryWrite(logEvent);
        }
    }

    private void TryWrite(LogEvent logEvent)
    {
        // A sink that throws must not kill the writer thread and take every later
        // line with it — that is how a shard ends up running blind.
        try { _write(logEvent); }
        catch { /* the console is the one thing we cannot report a console fault to */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // Bounded: shutdown waits for the backlog, but not for a console that is
        // never coming back.
        _writer.Join(TimeSpan.FromSeconds(2));
        _owned?.Dispose();
        _queue.Dispose();
    }
}
