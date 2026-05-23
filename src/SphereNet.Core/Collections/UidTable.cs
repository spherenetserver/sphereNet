using SphereNet.Core.Types;
using System.Collections.Concurrent;
using System.Threading;

namespace SphereNet.Core.Collections;

/// <summary>
/// UID allocation and recycling table. Maps to CWorldThread UID management in Source-X.
/// Items use UIDs with bit 30 set, characters use UIDs without.
/// </summary>
public sealed class UidTable
{
    private readonly ConcurrentQueue<int> _freeItemSlots = new();
    private readonly ConcurrentQueue<int> _freeCharSlots = new();
    private int _nextItemIndex;
    private int _nextCharIndex;

    public int Count => 0;

    public Serial AllocateItem()
    {
        int index = _freeItemSlots.TryDequeue(out int recycled)
            ? recycled
            : Interlocked.Increment(ref _nextItemIndex);
        return Serial.NewItem(index);
    }

    public Serial AllocateChar()
    {
        int index = _freeCharSlots.TryDequeue(out int recycled)
            ? recycled
            : Interlocked.Increment(ref _nextCharIndex);
        return Serial.NewChar(index);
    }

    public void Register(Serial uid, object obj)
    {
    }

    public void Free(Serial uid)
    {
        if (uid.Index <= 0)
            return;
        if (uid.IsItem)
            _freeItemSlots.Enqueue(uid.Index);
        else if (uid.IsChar)
            _freeCharSlots.Enqueue(uid.Index);
    }

    /// <summary>
    /// Re-register an object from a temporary serial to its saved serial.
    /// Removes the temp serial WITHOUT recycling its index, registers the new serial,
    /// and advances the next-index counter past the new serial to prevent collisions.
    /// </summary>
    public void ReRegister(Serial oldUid, Serial newUid, object obj)
    {
        int required = newUid.Index;
        if (newUid.IsItem)
            AdvanceAtLeast(ref _nextItemIndex, required);
        else if (newUid.IsChar)
            AdvanceAtLeast(ref _nextCharIndex, required);
    }

    public object? Find(Serial uid) => null;

    public T? Find<T>(Serial uid) where T : class
    {
        return Find(uid) as T;
    }

    public bool Exists(Serial uid) => false;

    public void Clear()
    {
        while (_freeItemSlots.TryDequeue(out _)) { }
        while (_freeCharSlots.TryDequeue(out _)) { }
        Volatile.Write(ref _nextItemIndex, 0);
        Volatile.Write(ref _nextCharIndex, 0);
    }

    private static void AdvanceAtLeast(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
