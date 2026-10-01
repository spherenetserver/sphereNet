using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.World;

/// <summary>Due-ordered queue of items that holds AT MOST ONE entry per item.
///
/// An indexed binary min-heap: every item remembers where its entry sits (a
/// <see cref="Slot"/> field on the item), so re-arming moves the existing entry and
/// clearing or deleting removes it, both in O(log n) without allocation. This is
/// upstream's model - CWorldTicker::AddTimedObject erases an object's existing
/// timed-object entry before inserting the new timeout, and DelTimedObject removes
/// it outright - and replaces the old lazy scheme, where every re-arm pushed another
/// entry and the stale ones were only recognised when they surfaced. Under that
/// scheme 100,000 re-arms of one item left 100,000 dead entries that a single drain
/// had to walk (none of them counted against the per-tick budget), and a stale entry
/// with a far-future deadline sat in memory until that deadline arrived.
///
/// Ties on the deadline come out in the order they were (re)armed.
///
/// Serial-phase only, like the drains that consume it.</summary>
internal sealed class ItemDeadlineQueue
{
    /// <summary>Per-item handle: which queue holds the item's entry, and where.</summary>
    internal struct Slot
    {
        internal ItemDeadlineQueue? Owner;
        internal int Index;
    }

    private struct Entry
    {
        public Item Item;
        public long Deadline;
        public long Seq;
    }

    private const int MinCapacity = 64;

    private readonly bool _decay;
    private Entry[] _heap = new Entry[MinCapacity];
    private int _count;
    private long _seq;

    /// <param name="decay">True for the decay queue (uses
    /// <see cref="Item.DecayQueueSlot"/>), false for the item-timer queue
    /// (<see cref="Item.TimerQueueSlot"/>).</param>
    public ItemDeadlineQueue(bool decay) => _decay = decay;

    /// <summary>Entries held - one per armed item.</summary>
    public int Count => _count;

    private ref Slot SlotOf(Item item) =>
        ref _decay ? ref item.DecayQueueSlot : ref item.TimerQueueSlot;

    public bool Contains(Item item) => SlotOf(item).Owner == this;

    public bool TryGetDeadline(Item item, out long deadline)
    {
        ref Slot s = ref SlotOf(item);
        if (s.Owner == this)
        {
            deadline = _heap[s.Index].Deadline;
            return true;
        }
        deadline = 0;
        return false;
    }

    /// <summary>Arm <paramref name="item"/> at <paramref name="deadline"/>, replacing
    /// any entry it already has. Returns false when the item already held exactly
    /// this deadline here (nothing changed).</summary>
    public bool Set(Item item, long deadline)
    {
        ref Slot s = ref SlotOf(item);
        if (s.Owner == this)
        {
            int i = s.Index;
            long old = _heap[i].Deadline;
            if (old == deadline)
                return false;
            _heap[i].Deadline = deadline;
            _heap[i].Seq = ++_seq;
            if (deadline < old) SiftUp(i);
            else SiftDown(i);
            return true;
        }

        // An item belongs to one world; registering it here takes it off any other
        // queue of the same kind (a test world it was created in, say).
        s.Owner?.Remove(item);

        if (_count == _heap.Length)
            Array.Resize(ref _heap, _heap.Length * 2);
        int at = _count++;
        _heap[at] = new Entry { Item = item, Deadline = deadline, Seq = ++_seq };
        s.Owner = this;
        s.Index = at;
        SiftUp(at);
        return true;
    }

    /// <summary>Drop the item's entry, if this queue holds one.</summary>
    public bool Remove(Item item)
    {
        ref Slot s = ref SlotOf(item);
        if (s.Owner != this)
            return false;
        int i = s.Index;
        s.Owner = null;
        s.Index = -1;
        RemoveAt(i);
        return true;
    }

    public bool TryPeek(out Item item, out long deadline)
    {
        if (_count == 0)
        {
            item = null!;
            deadline = 0;
            return false;
        }
        item = _heap[0].Item;
        deadline = _heap[0].Deadline;
        return true;
    }

    public bool TryDequeue(out Item item, out long deadline)
    {
        if (!TryPeek(out item, out deadline))
            return false;
        ref Slot s = ref SlotOf(item);
        s.Owner = null;
        s.Index = -1;
        RemoveAt(0);
        return true;
    }

    /// <summary>Every queued item with its deadline, in no particular order.</summary>
    public IEnumerable<(Item Item, long Deadline)> UnorderedItems
    {
        get
        {
            for (int i = 0; i < _count; i++)
                yield return (_heap[i].Item, _heap[i].Deadline);
        }
    }

    private void RemoveAt(int i)
    {
        int last = --_count;
        if (i != last)
        {
            _heap[i] = _heap[last];
            _heap[last] = default;
            SlotOf(_heap[i].Item).Index = i;
            if (i > 0 && Less(i, (i - 1) >> 1)) SiftUp(i);
            else SiftDown(i);
        }
        else
        {
            _heap[last] = default;
        }

        // Give memory back after a large backlog drained.
        if (_heap.Length > 1024 && _count < _heap.Length / 4)
            Array.Resize(ref _heap, Math.Max(MinCapacity, _heap.Length / 2));
    }

    private bool Less(int a, int b)
    {
        ref Entry x = ref _heap[a];
        ref Entry y = ref _heap[b];
        return x.Deadline < y.Deadline || (x.Deadline == y.Deadline && x.Seq < y.Seq);
    }

    private void Swap(int a, int b)
    {
        (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
        SlotOf(_heap[a].Item).Index = a;
        SlotOf(_heap[b].Item).Index = b;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!Less(i, parent))
                break;
            Swap(i, parent);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1;
            if (left >= _count)
                break;
            int min = left;
            int right = left + 1;
            if (right < _count && Less(right, left))
                min = right;
            if (!Less(min, i))
                break;
            Swap(i, min);
            i = min;
        }
    }
}
