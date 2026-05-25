using SphereNet.Core.Collections;
using SphereNet.Core.Enums;

namespace SphereNet.Game.Movement;

public sealed class MovementHistory
{
    private readonly CircularBuffer<MovementRecord> _records;

    public MovementHistory(int capacity = 20)
    {
        _records = new CircularBuffer<MovementRecord>(capacity);
    }

    public int Count => _records.Count;

    public void Record(long timestampMs, Direction dir, bool running, bool mounted)
    {
        _records.Push(new MovementRecord(timestampMs, dir, running, mounted));
    }

    public void Clear() => _records.Clear();

    public double AverageIntervalMs(int lastN)
    {
        int count = _records.Count;
        if (count < 2) return double.MaxValue;

        int pairs = Math.Min(lastN, count) - 1;
        if (pairs <= 0) return double.MaxValue;

        int startIdx = count - pairs - 1;
        long totalMs = 0;
        for (int i = 0; i < pairs; i++)
        {
            totalMs += _records[startIdx + i + 1].TimestampMs - _records[startIdx + i].TimestampMs;
        }

        return (double)totalMs / pairs;
    }

    public long MinIntervalMs(int lastN)
    {
        int count = _records.Count;
        if (count < 2) return long.MaxValue;

        int pairs = Math.Min(lastN, count) - 1;
        if (pairs <= 0) return long.MaxValue;

        int startIdx = count - pairs - 1;
        long min = long.MaxValue;
        for (int i = 0; i < pairs; i++)
        {
            long interval = _records[startIdx + i + 1].TimestampMs - _records[startIdx + i].TimestampMs;
            if (interval < min) min = interval;
        }

        return min;
    }

    public int CountBurstMoves(long thresholdMs, int lastN)
    {
        int count = _records.Count;
        if (count < 2) return 0;

        int pairs = Math.Min(lastN, count) - 1;
        if (pairs <= 0) return 0;

        int startIdx = count - pairs - 1;
        int burstCount = 0;
        for (int i = 0; i < pairs; i++)
        {
            long interval = _records[startIdx + i + 1].TimestampMs - _records[startIdx + i].TimestampMs;
            if (interval < thresholdMs)
                burstCount++;
        }

        return burstCount;
    }
}
