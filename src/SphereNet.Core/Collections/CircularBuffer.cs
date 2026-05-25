namespace SphereNet.Core.Collections;

public sealed class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public void Push(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length)
            _count++;
    }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int start = _count < _buffer.Length ? 0 : _head;
            return _buffer[(start + index) % _buffer.Length];
        }
    }

    public void Clear()
    {
        Array.Clear(_buffer);
        _head = 0;
        _count = 0;
    }

    public T[] ToArray()
    {
        var result = new T[_count];
        int start = _count < _buffer.Length ? 0 : _head;
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}
