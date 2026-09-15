using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Serilog.Core;
using Serilog.Events;
using SphereNet.Panel.Hubs;

namespace SphereNet.Panel.Logging;

public sealed class PanelLogSink : ILogEventSink, IDisposable
{
    private IHubContext<ServerHub>? _hub;
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private Timer? _flushTimer;

    /// <summary>How many lines may wait for the panel. A shard logging faster than the
    /// panel can take them - a script erroring in a loop, DebugPackets on - used to
    /// grow this queue without end, and before SetHubContext runs there is nothing
    /// draining it at all. The panel is a viewer, not a log store: when it falls
    /// behind, the OLDEST lines go, because the newest are the ones an operator
    /// watching a problem needs.</summary>
    private const int MaxBuffered = 5000;

    /// <summary>How many lines one flush may send. Draining the whole queue into a
    /// single SignalR message turns a flood into one enormous frame; what is left
    /// waits for the next tick 150 ms later.</summary>
    private const int MaxBatch = 500;

    private long _dropped;
    private int _sending;
    private long _sendFailures;

    /// <summary>Lines thrown away because the panel could not keep up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Lines waiting to be sent.</summary>
    public int PendingCount => _buffer.Count;

    /// <summary>Broadcasts that faulted. A panel that has stopped receiving is
    /// otherwise indistinguishable from a quiet server.</summary>
    public long SendFailureCount => Interlocked.Read(ref _sendFailures);

    public void SetHubContext(IHubContext<ServerHub> hub)
    {
        _hub = hub;
        _flushTimer = new Timer(FlushBuffer, null, 150, 150);
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp.UtcDateTime,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Properties.TryGetValue("SourceContext", out var src)
                ? src.ToString().Trim('"') : ""
        );

        AddEntry(entry);
    }

    public void AddEntry(LogEntry entry)
    {
        _buffer.Enqueue(entry);

        // Trim after the add rather than refusing it: the newest line is the one worth
        // keeping, so the queue gives up its oldest to make room.
        while (_buffer.Count > MaxBuffered && _buffer.TryDequeue(out _))
            Interlocked.Increment(ref _dropped);
    }

    internal void FlushBuffer(object? state)
    {
        if (_hub == null || _buffer.IsEmpty) return;

        // One flush at a time. The timer keeps firing every 150 ms whatever the
        // broadcast is doing, so a slow or stuck client used to have several
        // unobserved sends in flight at once, each holding its own batch.
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

        var batch = new List<LogEntry>(Math.Min(MaxBatch, _buffer.Count));
        while (batch.Count < MaxBatch && _buffer.TryDequeue(out var e))
            batch.Add(e);

        if (batch.Count == 0)
        {
            Volatile.Write(ref _sending, 0);
            return;
        }

        _hub.Clients.All.SendAsync("ReceiveLogBatch", batch)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Interlocked.Increment(ref _sendFailures);
                Volatile.Write(ref _sending, 0);
            }, TaskScheduler.Default);
    }

    public void Dispose() => _flushTimer?.Dispose();
}
