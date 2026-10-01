namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Fixed-width millisecond histogram for tick durations.
///
/// Samples above <see cref="RangeMs"/> are not folded into the last bucket: they are
/// counted separately as overflow. Folding them made a run of 1000 ms ticks report
/// p95 = p99 = 500 ms next to max = 1000 ms, and a gate threshold above the bucket
/// range then passed on numbers that were only the histogram's ceiling. A percentile
/// that lands in the overflow is reported by <see cref="GetPercentile"/> with
/// <see cref="TickPercentile.IsOverflow"/> set: its true value is somewhere between
/// <see cref="TickPercentile.LowerBoundMs"/> and the observed maximum, and
/// <see cref="Percentile"/> answers with that maximum (the conservative value) rather
/// than the bucket ceiling.
/// </summary>
public sealed class TickHistogram
{
    private readonly int[] _buckets;
    private readonly int _bucketWidthMs;
    private int _count;
    private int _overflow;
    private long _sum;
    private int _max;

    public TickHistogram(int maxMs = 500, int bucketWidthMs = 1)
    {
        if (bucketWidthMs <= 0) bucketWidthMs = 1;
        if (maxMs < 0) maxMs = 0;
        _bucketWidthMs = bucketWidthMs;
        _buckets = new int[maxMs / bucketWidthMs + 1];
    }

    public int Count => _count;
    public double AverageMs => _count > 0 ? (double)_sum / _count : 0;
    public int MaxMs => _max;

    /// <summary>Largest value the buckets resolve; anything above is overflow.</summary>
    public int RangeMs => _buckets.Length * _bucketWidthMs - 1;

    /// <summary>Samples above <see cref="RangeMs"/>.</summary>
    public int OverflowCount => _overflow;

    public void Record(int elapsedMs)
    {
        if (elapsedMs < 0) elapsedMs = 0;
        _count++;
        _sum += elapsedMs;
        if (elapsedMs > _max) _max = elapsedMs;

        int bucket = elapsedMs / _bucketWidthMs;
        if (bucket >= _buckets.Length)
        {
            _overflow++;
            return;
        }
        _buckets[bucket]++;
    }

    /// <summary>The percentile with its precision stated. In-range values are the
    /// bucket floor; an overflow value is only known to be above
    /// <see cref="RangeMs"/> and at most <see cref="MaxMs"/>.</summary>
    public TickPercentile GetPercentile(double p)
    {
        if (_count == 0) return new TickPercentile(0, false, 0, 0);

        int target = Math.Max(1, (int)Math.Ceiling(_count * p));
        int cumulative = 0;

        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= target)
            {
                int v = i * _bucketWidthMs;
                return new TickPercentile(v, false, v, _max);
            }
        }

        // The rank lies among the overflow samples.
        return new TickPercentile(_max, true, RangeMs + 1, _max);
    }

    /// <summary>Percentile in ms. When it falls in the overflow this is the observed
    /// maximum (an upper bound), never the bucket ceiling.</summary>
    public int Percentile(double p) => GetPercentile(p).ValueMs;

    public int P50 => Percentile(0.50);
    public int P95 => Percentile(0.95);
    public int P99 => Percentile(0.99);

    public void Reset()
    {
        Array.Clear(_buckets);
        _count = 0;
        _overflow = 0;
        _sum = 0;
        _max = 0;
    }
}

/// <summary>A histogram percentile. <see cref="IsOverflow"/> means the value lies
/// above the histogram's range: the real percentile is at least
/// <see cref="LowerBoundMs"/> and at most <see cref="MaxMs"/>, and
/// <see cref="ValueMs"/> carries the maximum.</summary>
public readonly record struct TickPercentile(int ValueMs, bool IsOverflow, int LowerBoundMs, int MaxMs)
{
    public override string ToString() => IsOverflow
        ? $">={LowerBoundMs}ms (overflow, max {MaxMs}ms)"
        : $"{ValueMs}ms";
}
