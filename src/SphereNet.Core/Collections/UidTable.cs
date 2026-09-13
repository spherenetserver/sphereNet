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

    // Freed uids wait here until a sweep releases them. Upstream never recycles a
    // uid at the moment of deletion: the free list is REBUILT from the empty slots
    // during garbage collection, and its own comment says the array has to be "enough
    // even for huge shards to survive till next garbage collection" (CWorld.cpp:655).
    //
    // Handing a uid straight back is what breaks a script holding a stale reference:
    // NEW or ACT captured before a delete would resolve to whatever took the uid next,
    // so NEW.NAME edited a stranger instead of reading as nothing.
    private readonly ConcurrentQueue<int> _pendingItemSlots = new();
    private readonly ConcurrentQueue<int> _pendingCharSlots = new();
    private int _nextItemIndex;
    private int _nextCharIndex;

    public int Count => 0;

    private const int MaxIndex = 0x0FFF_FFFF;

    public Serial AllocateItem()
    {
        int index = _freeItemSlots.TryDequeue(out int recycled)
            ? recycled
            : Interlocked.Increment(ref _nextItemIndex);
        if (index < 0 || index > MaxIndex)
            throw new InvalidOperationException("Item UID space exhausted");
        return Serial.NewItem(index);
    }

    public Serial AllocateChar()
    {
        int index = _freeCharSlots.TryDequeue(out int recycled)
            ? recycled
            : Interlocked.Increment(ref _nextCharIndex);
        if (index < 0 || index > MaxIndex)
            throw new InvalidOperationException("Character UID space exhausted");
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
            _pendingItemSlots.Enqueue(uid.Index);
        else if (uid.IsChar)
            _pendingCharSlots.Enqueue(uid.Index);
    }

    /// <summary>Release the uids freed since the last call back into the allocator.
    /// This is the sweep half of the reference contract (CWorld.cpp:655): between
    /// sweeps a deleted object's uid stays unused, so a stale script reference reads
    /// as nothing rather than resolving to whatever was created after it.</summary>
    public void ReleaseFreedUids()
    {
        while (_pendingItemSlots.TryDequeue(out int item))
            _freeItemSlots.Enqueue(item);
        while (_pendingCharSlots.TryDequeue(out int ch))
            _freeCharSlots.Enqueue(ch);
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
        while (_pendingItemSlots.TryDequeue(out _)) { }
        while (_pendingCharSlots.TryDequeue(out _)) { }
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
