namespace SphereNet.Game.Diagnostics;

public sealed class TickHistogram
{
    private readonly int[] _buckets;
    private readonly int _bucketWidthMs;
    private int _count;
    private long _sum;
    private int _max;

    public TickHistogram(int maxMs = 500, int bucketWidthMs = 1)
    {
        _bucketWidthMs = bucketWidthMs;
        _buckets = new int[maxMs / bucketWidthMs + 1];
    }

    public int Count => _count;
    public double AverageMs => _count > 0 ? (double)_sum / _count : 0;
    public int MaxMs => _max;

    public void Record(int elapsedMs)
    {
        if (elapsedMs < 0) elapsedMs = 0;
        _count++;
        _sum += elapsedMs;
        if (elapsedMs > _max) _max = elapsedMs;

        int bucket = elapsedMs / _bucketWidthMs;
        if (bucket >= _buckets.Length) bucket = _buckets.Length - 1;
        _buckets[bucket]++;
    }

    public int Percentile(double p)
    {
        if (_count == 0) return 0;

        int target = (int)Math.Ceiling(_count * p);
        int cumulative = 0;

        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= target)
                return i * _bucketWidthMs;
        }

        return (_buckets.Length - 1) * _bucketWidthMs;
    }

    public int P50 => Percentile(0.50);
    public int P95 => Percentile(0.95);
    public int P99 => Percentile(0.99);

    public void Reset()
    {
        Array.Clear(_buckets);
        _count = 0;
        _sum = 0;
        _max = 0;
    }
}
