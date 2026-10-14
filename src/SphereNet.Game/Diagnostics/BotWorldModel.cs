using SphereNet.Core.Collections;

namespace SphereNet.Game.Diagnostics;

public sealed class BotWorldModel
{
    public uint CharUid;
    public short X, Y;
    public sbyte Z;
    public byte Direction;
    public ushort Body;

    public short Hits, MaxHits;
    public short Mana, MaxMana;
    public short Stam, MaxStam;

    public bool IsDead;
    public bool IsWarMode;
    public bool IsMounted;

    public readonly Dictionary<uint, KnownMobile> Mobiles = new();

    public bool HasPendingTarget;
    public uint TargetCursorId;

    public int MoveRejectCount;
    public int TotalMoveRequests;
    public int TotalMoveRejects;
    /// <summary>Moves the server actually acknowledged. Separate from
    /// TotalMoveRequests on purpose: what was asked for and what happened are two
    /// different numbers, and a load report needs both.</summary>
    public int TotalMoveAcks;
    /// <summary>Requests this bot sent and did not wait for an answer to (attack, war
    /// mode, gump response, targeting). A load report that adds these to confirmed
    /// actions is counting its own packets.</summary>
    public int TotalRequestsSent;
    /// <summary>Answers that arrived for a move nobody is waiting for any more -
    /// the previous request timed out and this is its late reply. Counted rather than
    /// applied to whatever move is in flight now.</summary>
    public int LateMoveAcks;
    /// <summary>Steps not sent because the bot already ran into that tile.</summary>
    public int SkippedBlockedSteps;

    // Tiles a step into was refused, until when. A player who walks into the sea
    // does not try the same tile again; a bot that did made terrain rejects nearly
    // all of its reject rate, which hid anything else the rate could show.
    private readonly Dictionary<(short X, short Y), long> _blockedUntil = new();
    private const long BlockedTileMs = 300_000;

    public void NoteBlocked(short x, short y, long nowMs)
    {
        if (_blockedUntil.Count >= 4096)
            _blockedUntil.Clear();
        _blockedUntil[(x, y)] = nowMs + BlockedTileMs;
    }

    public bool IsKnownBlocked(short x, short y, long nowMs) =>
        _blockedUntil.TryGetValue((x, y), out long until) && until > nowMs;

    /// <summary>The tile a step in <paramref name="dir"/> (run bit ignored) leads to.</summary>
    public static (short Dx, short Dy) StepDelta(byte dir) => (dir & 0x07) switch
    {
        0 => (0, -1),
        1 => (1, -1),
        2 => (1, 0),
        3 => (1, 1),
        4 => (0, 1),
        5 => (-1, 1),
        6 => (-1, 0),
        _ => (-1, -1),
    };


    // Navigation state
    public short DestX, DestY;
    public bool HasDestination;
    public int WaypointIndex;
    public BotTravelPhase TravelPhase;
    public int CurrentCityIndex = -1;
    public int TargetCityIndex = -1;
    public int PoiVisitCount;
    public long LastDestReachedMs;
    public int ConsecutiveStuck;

    // Item & Container state
    public readonly Dictionary<uint, BotKnownItem> KnownItems = new();
    public readonly Dictionary<uint, BotContainerState> OpenContainers = new();
    public readonly Dictionary<byte, BotKnownItem> Equipment = new();
    public uint BackpackSerial;
    public int Gold;

    // Gump state
    public BotGumpState? ActiveGump;
    public long ActiveGumpReceivedMs;

    // Vendor state
    public BotVendorState? ActiveVendor;

    // Journal
    public readonly CircularBuffer<BotJournalEntry> Journal = new(50);

    // Action tracking
    public BotActionResult LastActionResult;
    public long LastActionTimeMs;

    // Anomaly tracking
    // Last position the server itself reported (login, 0x20, 0x21); the bot works
    // out X/Y between those from its accepted steps, but never Z.
    public short ServerX, ServerY;
    public sbyte ServerZ;
    // Set by a 0x21 that put the bot far from where its steps said it was, or that
    // moved Z on the same tile; the anomaly scanner reports each one once.
    public string? PendingSnap;
    public string? PendingZJump;

    // This bot's own window in the secure trade it is in, 0 when none, and when it
    // opened.
    public uint TradeContainer;
    public long TradeOpenedMs;

    /// <summary>The newest system message the server sent since
    /// <paramref name="sinceMs"/> - why a drop or a trade went nowhere. Call with
    /// <see cref="SyncRoot"/> held.</summary>
    public string? LastSystemMessage(long sinceMs)
    {
        var entries = Journal.ToArray();
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.TimestampMs < sinceMs) break;
            if (e.SpeakerSerial == 0xFFFFFFFF || e.SpeakerSerial == 0)
                return e.Text;
        }
        return null;
    }
    public int ConsecutivePickupRejects;
    public int PendingPacketCount;
    public long LastAnomalyScanMs;

    public void NoteServerPosition(short x, short y, sbyte z)
    {
        ServerX = x;
        ServerY = y;
        ServerZ = z;
    }

    /// <summary>A 0x21 moved the bot from (<paramref name="predictedX"/>,
    /// <paramref name="predictedY"/>) to the position given. Taking back a step or
    /// two is what a reject is for; more than ten tiles means the bot and the server
    /// disagreed about where it was. Z is compared only against a server-reported Z
    /// on the same tile, as the bot has no map to follow it over hills.</summary>
    public void NoteCorrection(short predictedX, short predictedY, short x, short y, sbyte z)
    {
        int dist = Math.Max(Math.Abs(x - predictedX), Math.Abs(y - predictedY));
        if (dist > 10 && (predictedX != 0 || predictedY != 0))
            PendingSnap = $"Position snapped {dist} tiles ({predictedX},{predictedY} -> {x},{y})";
        if (x == ServerX && y == ServerY && Math.Abs(z - ServerZ) > 20)
            PendingZJump = $"Z jumped {Math.Abs(z - ServerZ)} on the spot ({ServerZ} -> {z}) at {x},{y}";
        NoteServerPosition(x, y, z);
    }

    public int DistanceTo(short tx, short ty)
    {
        int dx = Math.Abs(X - tx);
        int dy = Math.Abs(Y - ty);
        return Math.Max(dx, dy);
    }

    // Guards the dictionaries (Mobiles/KnownItems/Equipment/OpenContainers):
    // the bot's background receive loop mutates them while the behavior thread
    // reads them (FindNearest etc.). Hold this around any iterate-or-mutate.
    public readonly object SyncRoot = new();

    public KnownMobile? FindNearest(Func<KnownMobile, bool> predicate, int maxRange = 18)
    {
        KnownMobile? best = null;
        int bestDist = int.MaxValue;
        lock (SyncRoot)
        {
            foreach (var m in Mobiles.Values)
            {
                if (!predicate(m)) continue;
                int d = DistanceTo(m.X, m.Y);
                if (d < bestDist && d <= maxRange) { bestDist = d; best = m; }
            }
        }
        return best;
    }

    public void RemoveStale(long nowMs, long maxAgeMs = 60_000)
    {
        lock (SyncRoot)
        {
            var remove = new List<uint>();
            foreach (var kv in Mobiles)
                if (nowMs - kv.Value.LastSeenMs > maxAgeMs) remove.Add(kv.Key);
            foreach (var id in remove) Mobiles.Remove(id);
        }
    }

    public byte GetDirectionTo(short tx, short ty)
    {
        int dx = tx - X;
        int dy = ty - Y;
        if (dx == 0 && dy == 0) return Direction;

        int adx = Math.Abs(dx);
        int ady = Math.Abs(dy);

        if (ady == 0) return dx > 0 ? (byte)2 : (byte)6;
        if (adx == 0) return dy > 0 ? (byte)4 : (byte)0;
        if (adx > ady * 2) return dx > 0 ? (byte)2 : (byte)6;
        if (ady > adx * 2) return dy > 0 ? (byte)4 : (byte)0;

        if (dx > 0) return dy > 0 ? (byte)3 : (byte)1;
        return dy > 0 ? (byte)5 : (byte)7;
    }
}

public sealed class KnownMobile
{
    public uint Serial;
    public ushort Body;
    public short X, Y;
    public sbyte Z;
    public byte Notoriety;
    public long LastSeenMs;

    private static readonly HashSet<ushort> MountBodies =
        [0x00C8, 0x00E2, 0x00CC, 0x00E4, 0x00D2, 0x00DA, 0x00DC];

    public bool IsHumanBody => Body is 0x0190 or 0x0191;
    public bool IsMountBody => MountBodies.Contains(Body);
    public bool IsMonster => Notoriety is 5 or 6;
    public bool IsLikelyHealer => IsHumanBody && Notoriety == 7;
}

public enum BotTravelPhase
{
    InCity,
    TravelingToCity,
}

public sealed class BotKnownItem
{
    public uint Serial;
    public ushort ItemId;
    public short X, Y;
    public sbyte Z;
    public ushort Amount;
    public ushort Hue;
    public uint ContainerSerial;
    public byte Layer;
    public long LastSeenMs;
}

public sealed class BotContainerState
{
    public uint Serial;
    public ushort GumpId;
    public readonly List<BotKnownItem> Items = new();
}

public sealed class BotGumpState
{
    public uint GumpId;
    public uint Serial;
    public bool IsOpen;
    public int X, Y;
    public int LayoutLength;
    public int TextLineCount;
    public long ReceivedMs;
}

public sealed class BotVendorState
{
    public uint VendorSerial;
    public bool IsBuyList;
    public readonly List<BotVendorItem> Items = new();
}

public sealed class BotVendorItem
{
    public uint Serial;
    public ushort ItemId;
    public ushort Amount;
    public int Price;
    public string Name = string.Empty;
}

public sealed class BotJournalEntry
{
    public long TimestampMs;
    public uint SpeakerSerial;
    public string Speaker = string.Empty;
    public string Text = string.Empty;
}

public enum BotActionResult
{
    None,
    Success,
    /// <summary>The request left the client and nothing here observes the server's
    /// answer. An attack that reaches the server and is refused looks exactly like one
    /// that lands, so a load report must not count this as a game action (review work
    /// item D09).</summary>
    Sent,
    Rejected,
    TimedOut,
    Desynced,
    Disconnected,
    InvalidState,
}
