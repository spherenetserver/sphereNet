using System;
using System.Diagnostics;
using System.Threading;
using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// One reproducible record of what the server was carrying and how it coped
/// (port plan PLAN-701 / PLAN-703).
///
/// The point is comparability: two runs are only worth comparing if the same
/// numbers were captured the same way, so this gathers the whole set in one pass
/// rather than leaving each caller to assemble its own. A soak run takes one of
/// these at intervals; the differences between them are the result.
///
/// The plan is explicit that thresholds come from a first baseline on the target
/// hardware, so nothing here judges the numbers - it only records them.
/// </summary>
public readonly record struct LoadProfileSnapshot(
    // --- what the server is carrying (PLAN-701) ---
    int Players,
    int Npcs,
    int Items,
    int Spawners,
    int Fighting,
    long MovesAccepted,
    long MovesRejected,
    long TriggersFired,
    // --- how it is coping (PLAN-703) ---
    int TickP50Ms,
    int TickP95Ms,
    int TickP99Ms,
    int TickMaxMs,
    int TicksSampled,
    long LastSaveMs,
    long SaveCount,
    long WorkingSetMb,
    long ManagedHeapMb,
    double LastGcPauseMs,
    int Gen0,
    int Gen1,
    int Gen2,
    int QueuedOutboundPackets,
    int UnexplainedObjects)
{
    /// <summary>One line per field, in a stable order, so two snapshots diff
    /// cleanly in a log or a spreadsheet.</summary>
    public override string ToString() =>
        $"players={Players} npcs={Npcs} items={Items} spawners={Spawners} fighting={Fighting} " +
        $"moves={MovesAccepted}/{MovesRejected} triggers={TriggersFired} " +
        $"tick(p50/p95/p99/max)={TickP50Ms}/{TickP95Ms}/{TickP99Ms}/{TickMaxMs}ms n={TicksSampled} " +
        $"save(last/count)={LastSaveMs}ms/{SaveCount} " +
        $"mem(ws/heap)={WorkingSetMb}/{ManagedHeapMb}MB gc={Gen0}/{Gen1}/{Gen2} pause={LastGcPauseMs:F0}ms " +
        $"outq={QueuedOutboundPackets} unexplained={UnexplainedObjects}";
}

/// <summary>Counters the profile reads. Each is a single interlocked add on an
/// existing choke point - the movement entry, the trigger dispatch and the save
/// - so carrying them costs nothing measurable but makes a load profile
/// reproducible instead of anecdotal.</summary>
public static class LoadProfile
{
    private static long s_movesAccepted;
    private static long s_movesRejected;
    private static long s_triggersFired;
    private static long s_saveCount;
    private static long s_lastSaveMs;

    public static void CountMove(bool accepted)
    {
        if (accepted) Interlocked.Increment(ref s_movesAccepted);
        else Interlocked.Increment(ref s_movesRejected);
    }

    public static void CountTrigger() => Interlocked.Increment(ref s_triggersFired);

    public static void CountSave(long elapsedMs)
    {
        Interlocked.Increment(ref s_saveCount);
        Interlocked.Exchange(ref s_lastSaveMs, elapsedMs);
    }

    /// <summary>Zero the counters. A soak run resets once at the start so the
    /// numbers describe the run rather than the process.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref s_movesAccepted, 0);
        Interlocked.Exchange(ref s_movesRejected, 0);
        Interlocked.Exchange(ref s_triggersFired, 0);
        Interlocked.Exchange(ref s_saveCount, 0);
        Interlocked.Exchange(ref s_lastSaveMs, 0);
    }

    public static long MovesAccepted => Interlocked.Read(ref s_movesAccepted);
    public static long MovesRejected => Interlocked.Read(ref s_movesRejected);
    public static long TriggersFired => Interlocked.Read(ref s_triggersFired);
    public static long SaveCount => Interlocked.Read(ref s_saveCount);
    public static long LastSaveMs => Interlocked.Read(ref s_lastSaveMs);

    /// <summary>Take the snapshot.
    ///
    /// <paramref name="ticks"/> supplies the tick percentiles, <paramref name="outboundQueue"/>
    /// the connection backlog and <paramref name="unexplained"/> the object-audit
    /// count; each is optional so a caller without that subsystem still gets a
    /// usable record rather than nothing.</summary>
    public static LoadProfileSnapshot Capture(
        GameWorld world,
        TickHistogram? ticks = null,
        int outboundQueue = 0,
        int unexplained = -1)
    {
        int players = 0, npcs = 0, items = 0, spawners = 0, fighting = 0;
        foreach (var obj in world.GetAllObjects())
        {
            switch (obj)
            {
                case Objects.Characters.Character ch when !ch.IsDeleted:
                    if (ch.IsPlayer) players++;
                    else npcs++;
                    if (ch.FightTarget.IsValid) fighting++;
                    break;
                case Item item when !item.IsDeleted:
                    items++;
                    // All three spawner kinds, as the pack uses them.
                    if (item.ItemType is ItemType.SpawnChar or ItemType.SpawnItem
                        or ItemType.SpawnChampion) spawners++;
                    break;
            }
        }

        var gc = GC.GetGCMemoryInfo();
        double pause = gc.PauseDurations.IsEmpty ? 0 : gc.PauseDurations[0].TotalMilliseconds;

        return new LoadProfileSnapshot(
            Players: players,
            Npcs: npcs,
            Items: items,
            Spawners: spawners,
            Fighting: fighting,
            MovesAccepted: MovesAccepted,
            MovesRejected: MovesRejected,
            TriggersFired: TriggersFired,
            TickP50Ms: ticks?.P50 ?? 0,
            TickP95Ms: ticks?.P95 ?? 0,
            TickP99Ms: ticks?.P99 ?? 0,
            TickMaxMs: ticks?.MaxMs ?? 0,
            TicksSampled: ticks?.Count ?? 0,
            LastSaveMs: LastSaveMs,
            SaveCount: SaveCount,
            WorkingSetMb: Environment.WorkingSet / (1024 * 1024),
            ManagedHeapMb: GC.GetTotalMemory(false) / (1024 * 1024),
            LastGcPauseMs: pause,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            QueuedOutboundPackets: outboundQueue,
            UnexplainedObjects: unexplained);
    }
}
