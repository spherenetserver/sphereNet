namespace SphereNet.Server;

public enum TickYieldAction
{
    Spin,
    SleepOne,
    Hybrid,
    Adaptive,
}

public static class TickYieldStrategy
{
    /// <summary>
    /// Upper bound on a single adaptive sleep. Caps the worst-case added
    /// network-input latency (a packet arriving just after the loop sleeps waits at
    /// most this long) while still letting an idle loop stop hot-spinning.
    /// </summary>
    public const int AdaptiveMaxSleepMs = 5;

    public static TickYieldAction Resolve(int mode) => mode switch
    {
        0 => TickYieldAction.Spin,
        1 => TickYieldAction.SleepOne,
        3 => TickYieldAction.Adaptive,
        _ => TickYieldAction.Hybrid,
    };

    /// <summary>
    /// How long to sleep given the slack until the next tick is due. Pure and
    /// deterministic so the policy is unit-testable. It never sleeps past the
    /// deadline (tick cadence — and thus movement/ping processing — is preserved),
    /// never longer than <paramref name="maxSleepMs"/> (bounds input latency), and
    /// returns 0 when the deadline is imminent or overdue so a busy loop keeps
    /// spinning at full rate.
    /// </summary>
    public static int ComputeAdaptiveSleepMs(long msUntilNextDeadline, int maxSleepMs)
    {
        if (msUntilNextDeadline <= 1) return 0;
        long slack = msUntilNextDeadline - 1; // 1ms guard so we wake before the deadline
        if (slack > maxSleepMs) slack = maxSleepMs;
        return (int)slack;
    }

    public static void Yield(int mode, long msUntilNextDeadline = 0)
    {
        switch (Resolve(mode))
        {
            case TickYieldAction.Spin:
                Thread.SpinWait(100);
                break;
            case TickYieldAction.SleepOne:
                Thread.Sleep(1);
                break;
            case TickYieldAction.Adaptive:
                int ms = ComputeAdaptiveSleepMs(msUntilNextDeadline, AdaptiveMaxSleepMs);
                if (ms > 0) Thread.Sleep(ms);
                else Thread.SpinWait(100);
                break;
            default:
                Thread.SpinWait(100);
                Thread.Sleep(0);
                break;
        }
    }
}
