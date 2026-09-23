namespace SphereNet.Panel;

public record ScheduleStatus(bool Pending, bool Restart, DateTime? DueUtc);

/// <summary>
/// A shutdown or restart that happens after a delay, announced in game as it
/// approaches: once when scheduled, then at 10, 5, 2 and 1 minutes and 30 and 10
/// seconds left. Players get time to find a safe spot instead of dropping mid-fight.
/// One schedule at a time; cancelling announces that too.
/// </summary>
public sealed class ShutdownScheduler
{
    private static readonly int[] WarningMarks = [600, 300, 120, 60, 30, 10];

    private readonly Func<string, bool>? _broadcast;
    private readonly Func<bool>? _shutdown;
    private readonly Func<bool>? _restart;
    private readonly Func<DateTime> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private ScheduleStatus _status = new(false, false, null);

    public ShutdownScheduler(Func<string, bool>? broadcast, Func<bool>? shutdown, Func<bool>? restart,
        Func<DateTime>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _broadcast = broadcast;
        _shutdown = shutdown;
        _restart = restart;
        _clock = clock ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public ScheduleStatus Status { get { lock (_gate) return _status; } }

    /// <summary>The task running the current countdown; tests await it.</summary>
    internal Task? Running { get; private set; }

    public bool TrySchedule(int seconds, bool restart, string? message, out ScheduleStatus status)
    {
        lock (_gate)
        {
            if (_status.Pending)
            {
                status = _status;
                return false;
            }
            seconds = Math.Clamp(seconds, 10, 24 * 3600);
            _cts = new CancellationTokenSource();
            _status = new ScheduleStatus(true, restart, _clock().AddSeconds(seconds));
            status = _status;
            var ct = _cts.Token;
            string note = string.IsNullOrWhiteSpace(message) ? "" : " " + message.Trim();
            Running = Task.Run(() => RunAsync(seconds, restart, note, ct));
            return true;
        }
    }

    public ScheduleStatus Cancel()
    {
        lock (_gate)
        {
            if (!_status.Pending) return _status;
            bool restart = _status.Restart;
            _cts?.Cancel();
            _status = new ScheduleStatus(false, false, null);
            _broadcast?.Invoke($"The scheduled server {(restart ? "restart" : "shutdown")} has been cancelled.");
            return _status;
        }
    }

    internal static string Describe(int seconds) =>
        seconds >= 60
            ? $"{seconds / 60} minute{(seconds / 60 == 1 ? "" : "s")}"
            : $"{seconds} second{(seconds == 1 ? "" : "s")}";

    private async Task RunAsync(int seconds, bool restart, string note, CancellationToken ct)
    {
        string what = restart ? "restart" : "shutdown";
        try
        {
            _broadcast?.Invoke($"Server {what} in {Describe(seconds)}.{note}");
            int left = seconds;
            foreach (int mark in WarningMarks)
            {
                if (mark >= left) continue;
                await _delay(TimeSpan.FromSeconds(left - mark), ct).ConfigureAwait(false);
                left = mark;
                _broadcast?.Invoke($"Server {what} in {Describe(left)}.{note}");
            }
            await _delay(TimeSpan.FromSeconds(left), ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                _status = new ScheduleStatus(false, false, null);
            }
            _broadcast?.Invoke($"Server {what} now.");
            if (restart) _restart?.Invoke();
            else _shutdown?.Invoke();
        }
        catch (OperationCanceledException) { }
    }
}
