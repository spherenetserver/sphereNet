namespace SphereNet.Game.Movement;

public sealed class SpeedHackDetector
{
    private readonly double _rateThreshold;
    private readonly int _burstWindow;
    private readonly int _cooldownMs;

    private int _consecutiveViolations;
    private long _lastViolationMs;

    public SpeedHackDetector(double rateThreshold = 1.5, int burstWindow = 3, int cooldownMs = 60_000)
    {
        _rateThreshold = rateThreshold;
        _burstWindow = burstWindow;
        _cooldownMs = cooldownMs;
    }

    public SpeedVerdict Analyze(MovementHistory history, bool mounted, bool running, long nowMs)
    {
        if (history.Count < 3)
            return SpeedVerdict.Normal;

        int expectedDelay = MovementEngine.GetMoveDelay(mounted, running);
        double minAllowedInterval = expectedDelay / _rateThreshold;

        double avgInterval = history.AverageIntervalMs(6);

        if (avgInterval >= minAllowedInterval)
        {
            _consecutiveViolations = 0;
            return SpeedVerdict.Normal;
        }

        int burstCount = history.CountBurstMoves((long)minAllowedInterval, 6);

        if (burstCount >= _burstWindow)
        {
            _consecutiveViolations++;

            if (_consecutiveViolations >= 3)
                return SpeedVerdict.Kick;

            if (_cooldownMs > 0 && _lastViolationMs > 0 && (nowMs - _lastViolationMs) < _cooldownMs)
                return SpeedVerdict.Warning;

            _lastViolationMs = nowMs;
            return SpeedVerdict.Violation;
        }

        return SpeedVerdict.Warning;
    }

    public void Reset()
    {
        _consecutiveViolations = 0;
        _lastViolationMs = 0;
    }
}
