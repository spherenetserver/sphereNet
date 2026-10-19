using System.Buffers;

namespace SphereNet.Network.Packets;

/// <summary>
/// The byte arrays behind packet buffers. A packet is rented on the thread that
/// builds it (the main loop) and returned on the thread that flushes it (a send
/// worker), thousands of times a tick with a full server. ArrayPool.Shared keeps
/// a small stack per core per size and drops what does not fit, so under that
/// one-way traffic most returns were thrown away and most rents allocated anew -
/// measured as a large share of the main loop's time and the gen1 collections
/// that followed.
///
/// This pool keeps, per power-of-two size from 16 bytes to 64 KB, a bounded shared
/// stack (slots allocated once) behind a small stack per thread. A thread rents from
/// and returns to its own stack; only when that runs empty or full does it take or
/// hand over a batch from the shared one under its lock, so the builder and the
/// flushers meet at the lock once per batch, not once per packet. Renting and
/// returning allocate nothing; anything larger goes to ArrayPool.Shared as before.
/// </summary>
public static class PacketArrayPool
{
    private const int MinShift = 4;          // 16 bytes
    private const int MaxShift = 16;         // 64 KB
    private const int Sizes = MaxShift - MinShift + 1;
    private const int LocalCap = 64;
    private const int Batch = 32;

    private sealed class Bucket(int capacity)
    {
        public readonly byte[]?[] Slots = new byte[]?[capacity];
        public int Count;
    }

    private sealed class Local
    {
        public readonly byte[]?[][] Slots = Enumerable.Range(0, Sizes).Select(_ => new byte[]?[LocalCap]).ToArray();
        public readonly int[] Counts = new int[Sizes];
    }

    private static readonly Bucket[] s_buckets = Enumerable.Range(MinShift, Sizes)
        .Select(shift => new Bucket(Cap(shift)))
        .ToArray();

    [ThreadStatic] private static Local? t_local;

    /// <summary>Arrays kept per size in the shared stack: generous for the small sizes
    /// almost every packet uses, fewer for the large ones.</summary>
    private static int Cap(int shift) => shift <= 8 ? 16384 : shift <= 12 ? 1024 : 64;

    private static int ShiftFor(int length)
    {
        int shift = MinShift;
        while ((1 << shift) < length) shift++;
        return shift;
    }

    /// <summary>An array of at least <paramref name="minimumLength"/> bytes.</summary>
    public static byte[] Rent(int minimumLength)
    {
        if (minimumLength > 1 << MaxShift)
            return ArrayPool<byte>.Shared.Rent(minimumLength);
        int shift = ShiftFor(Math.Max(1, minimumLength));
        int slot = shift - MinShift;
        var local = t_local ??= new Local();
        var mine = local.Slots[slot];
        if (local.Counts[slot] == 0)
        {
            // Refill a batch from the shared stack.
            var bucket = s_buckets[slot];
            lock (bucket)
            {
                int take = Math.Min(Batch, bucket.Count);
                for (int i = 0; i < take; i++)
                {
                    mine[i] = bucket.Slots[--bucket.Count];
                    bucket.Slots[bucket.Count] = null;
                }
                local.Counts[slot] = take;
            }
            if (local.Counts[slot] == 0)
                return GC.AllocateUninitializedArray<byte>(1 << shift);
        }
        int n = --local.Counts[slot];
        var array = mine[n]!;
        mine[n] = null;
        return array;
    }

    /// <summary>Give an array back. One of this pool's sizes is kept while there is
    /// room (else the collector has it); anything else was ArrayPool.Shared's and
    /// goes back there.</summary>
    public static void Return(byte[] array)
    {
        int length = array.Length;
        if (length < 1 << MinShift || length > 1 << MaxShift || (length & (length - 1)) != 0)
        {
            ArrayPool<byte>.Shared.Return(array);
            return;
        }
        int slot = ShiftFor(length) - MinShift;
        var local = t_local ??= new Local();
        var mine = local.Slots[slot];
        if (local.Counts[slot] == LocalCap)
        {
            // Hand a batch over to the shared stack (what does not fit is dropped).
            var bucket = s_buckets[slot];
            lock (bucket)
            {
                for (int i = 0; i < Batch; i++)
                {
                    var moving = mine[--local.Counts[slot]];
                    mine[local.Counts[slot]] = null;
                    if (bucket.Count < bucket.Slots.Length)
                        bucket.Slots[bucket.Count++] = moving;
                }
            }
        }
        mine[local.Counts[slot]++] = array;
    }
}
