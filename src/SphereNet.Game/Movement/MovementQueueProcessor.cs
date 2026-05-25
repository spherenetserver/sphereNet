namespace SphereNet.Game.Movement;

public sealed class MovementQueueProcessor
{
    private readonly struct QueuedMove
    {
        public readonly byte Direction;
        public readonly byte Sequence;
        public readonly uint FastWalkKey;
        public readonly long EnqueuedAt;

        public QueuedMove(byte direction, byte sequence, uint fastWalkKey, long enqueuedAt)
        {
            Direction = direction;
            Sequence = sequence;
            FastWalkKey = fastWalkKey;
            EnqueuedAt = enqueuedAt;
        }
    }

    private readonly Queue<QueuedMove> _queue = new();
    private readonly int _capacity;

    public MovementQueueProcessor(int capacity = 10)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _queue.Count;
    public bool IsFull => _queue.Count >= _capacity;

    public bool Enqueue(byte dir, byte seq, uint fastWalkKey, long nowMs)
    {
        if (_queue.Count >= _capacity)
            return false;

        _queue.Enqueue(new QueuedMove(dir, seq, fastWalkKey, nowMs));
        return true;
    }

    public bool TryDequeue(out byte dir, out byte seq, out uint fastWalkKey)
    {
        if (_queue.TryDequeue(out var move))
        {
            dir = move.Direction;
            seq = move.Sequence;
            fastWalkKey = move.FastWalkKey;
            return true;
        }

        dir = 0;
        seq = 0;
        fastWalkKey = 0;
        return false;
    }

    public void Clear() => _queue.Clear();
}
