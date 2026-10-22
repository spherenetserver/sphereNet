using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// The step. A bot walks straight at its target (or 45 degrees either side) while that
/// is open, and when it is not, it looks at why:
/// <list type="bullet">
/// <item>somebody stands there: it waits a moment, then steps aside, and only a crowd
/// that stays put gets a tile search routed round people;</item>
/// <item>a wall or the lie of the land: a shut door ahead is opened, a road link other
/// bots already found a way along is followed, else a tile search is asked for
/// (it runs off the main thread, see the Search part) and meanwhile the bot keeps the
/// obstacle on one hand and walks its edge (Bug2);</item>
/// <item>out of breath or held fast: it waits.</item>
/// </list>
/// A tile path, once there, is followed step by step; somebody on it is waited for, a
/// shut door on it opened, anything else drops it and asks for a new one. Every step
/// goes through the player walk path (<see cref="MovementEngine.TryMoveDetailed"/>).
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>The road link the step about to be taken walks (set by the trip around
    /// its call to <see cref="StepToward"/>), for the shared tile paths.</summary>
    private (int From, int To)? _stepCorridor;

    /// <summary>One step toward <paramref name="target"/>. True when the bot moved.
    /// A tile search may be asked for when the target is within
    /// <paramref name="searchWithin"/> tiles.</summary>
    private bool StepToward(BotMind mind, Point3D target, bool run, int searchWithin = 60)
    {
        var ch = mind.Char;
        long now = NowMs();
        if (ch.Position.X == target.X && ch.Position.Y == target.Y) return false;
        int dist = ch.Position.GetDistanceTo(target);
        Watch(mind, target, dist, now);

        // Out of breath, or giving the one in the way a moment.
        if (now < mind.MobileWaitUntilMs || now < mind.BreathUntilMs) return false;

        if (mind.TilePath != null)
        {
            if (mind.TilePathFor.Map != target.Map || mind.TilePathFor.GetDistanceTo(target) > 3)
                DropPath(mind, "target changed");
            else if (FollowPath(mind, target, run, now, out bool steppedOnPath))
                return steppedOnPath;
        }

        var dir = ch.Position.GetDirectionTo(target);
        if (mind.FollowSide != 0)
            return FollowEdge(mind, target, dir, dist, run, now, searchWithin);

        BotBlock ahead = BotBlock.None;
        foreach (int turn in StraightFirst)
        {
            var d = (Direction)(((int)dir + turn) & 7);
            if (Move(mind, d, run))
            {
                mind.MobileTries = 0;
                WindowSteps++;
                return true;
            }
            if (turn == 0) ahead = mind.LastBlock;
        }

        switch (ahead)
        {
            case BotBlock.Frozen:
                return false;
            case BotBlock.Stamina:
                Breathe(mind, now);
                return false;
            case BotBlock.Mobile:
                if (WaitForMobile(mind, now)) return false;
                if (SideStep(mind, dir, run)) return true;
                // A crowd that stays put: a route round the people.
                if (dist <= searchWithin && now - mind.MobileSinceMs > 3000)
                    RequestSearch(mind, target, now, avoidChars: true);
                return false;
        }

        // A wall, a fence, water. A shut door ahead is opened the way the client's
        // auto-open does for a player.
        if (mind.Client != null && TryOpenDoorAhead(mind, dir))
            return false;
        // The way other bots found along this road link.
        if (TryCorridor(mind, target, now))
            return false;
        if (dist <= searchWithin)
            RequestSearch(mind, target, now);

        // Meet the obstacle: put it on one hand by turning the other way (wall on
        // the right means heading 90 degrees left of where it was going).
        mind.FollowSide = _rng.Next(2) == 0 ? 1 : -1;
        mind.FollowHeading = (Direction)(((int)dir - mind.FollowSide * 2) & 7);
        mind.FollowSteps = 0;
        mind.FollowFromDist = dist;
        return false;
    }

    /// <summary>Walk an obstacle's edge (Bug2) until nearer than where it was met and
    /// the way is open; a tile path arriving meanwhile takes over.</summary>
    private bool FollowEdge(BotMind mind, Point3D target, Direction dir, int dist, bool run, long now, int searchWithin)
    {
        // Leave the wall once nearer than where it was met and the way is open.
        if (dist < mind.FollowFromDist && Move(mind, dir, run))
        {
            mind.FollowSide = 0;
            WindowSteps++;
            return true;
        }
        // Still on the wall: the corridor or a search may know the way (both are
        // cheap to ask for again: the search keeps its own backoff).
        if (!TryCorridor(mind, target, now) && dist <= searchWithin)
            RequestSearch(mind, target, now);
        if (mind.TilePath != null)
        {
            mind.FollowSide = 0;
            return false;
        }
        if (++mind.FollowSteps > 40 + mind.FollowFromDist * 2)
        {
            // A long way round with no gain: try the other hand.
            mind.FollowSide = -mind.FollowSide;
            mind.FollowSteps = 0;
            mind.FollowHeading = (Direction)(((int)dir - mind.FollowSide * 2) & 7);
        }
        // Hand on the wall: try the turn toward it first (FollowSide +1 keeps it
        // on the right, so a right turn), then sweep round the other way; the
        // first open tile is the next step along its edge.
        int baseDir = (int)mind.FollowHeading + mind.FollowSide * 2;
        for (int k = 0; k < 8; k++)
        {
            var d = (Direction)((baseDir - mind.FollowSide * k) & 7);
            if (Move(mind, d, run))
            {
                mind.FollowHeading = d;
                WindowSteps++;
                return true;
            }
        }
        if (mind.LastBlock == BotBlock.Stamina) Breathe(mind, now);
        if (mind.Client != null) TryOpenDoorAhead(mind, mind.FollowHeading);
        return false;
    }

    /// <summary>One step along the tile path. True when the path decided this act
    /// (stepped, or waiting on it); false to walk without it.</summary>
    private bool FollowPath(BotMind mind, Point3D target, bool run, long now, out bool stepped)
    {
        stepped = false;
        var ch = mind.Char;
        var path = mind.TilePath!;
        while (mind.TileIndex < path.Count && SameTile(path[mind.TileIndex], ch.Position))
            mind.TileIndex++;
        if (mind.TileIndex >= path.Count)
        {
            DropPath(mind, "walked to its end");
            return false;
        }
        var next = path[mind.TileIndex];
        if (Chebyshev(next, ch.Position) != 1)
        {
            // Off the path (pushed, or it stepped round someone): back onto it.
            int join = JoinIndex(path, ch.Position, mind.TileIndex);
            if (join < 0)
            {
                DropPath(mind, $"off the path at {ch.Position}");
                return false;
            }
            mind.TileIndex = join;
            while (mind.TileIndex < path.Count && SameTile(path[mind.TileIndex], ch.Position))
                mind.TileIndex++;
            if (mind.TileIndex >= path.Count || Chebyshev(path[mind.TileIndex], ch.Position) != 1)
            {
                DropPath(mind, $"cannot rejoin at {ch.Position}");
                return false;
            }
            next = path[mind.TileIndex];
        }
        if (Move(mind, ch.Position.GetDirectionTo(next), run))
        {
            mind.TileIndex++;
            mind.MobileTries = 0;
            mind.FollowSide = 0;
            WindowSteps++;
            stepped = true;
            return true;
        }
        switch (mind.LastBlock)
        {
            case BotBlock.Mobile:
                if (WaitForMobile(mind, now)) return true;
                DropPath(mind, "someone stays in the way");
                RequestSearch(mind, target, now, avoidChars: true);
                return false;
            case BotBlock.Stamina:
                Breathe(mind, now);
                return true;
            case BotBlock.Frozen:
                return true;
        }
        // A shut door on the path is the way the search meant: open it.
        if (mind.Client != null && TryOpenDoorOn(mind, next))
            return true;
        // A diagonal round a corner the search allows but the step does not: the
        // same corner in two straight steps.
        if (next.X != ch.X && next.Y != ch.Y)
        {
            var viaX = ch.Position.GetDirectionTo(new Point3D(next.X, ch.Y, ch.Z, ch.MapIndex));
            var viaY = ch.Position.GetDirectionTo(new Point3D(ch.X, next.Y, ch.Z, ch.MapIndex));
            if (Move(mind, viaX, run) || Move(mind, viaY, run))
            {
                WindowCornerSplits++;
                WindowSteps++;
                stepped = true;
                return true;
            }
        }
        // Anything else: the path went stale (a door locked, something put down).
        ForgetCorridor(mind.TilePathCorridor);
        DropPath(mind, $"step to {next} refused ({mind.LastRefusal})");
        mind.NextSearchMs = 0;
        return false;
    }

    private void DropPath(BotMind mind, string? why = null)
    {
        if (why != null && mind.TilePath != null)
            mind.Note(NowMs(), $"path dropped at {mind.TileIndex}/{mind.TilePath.Count}: {why}");
        mind.TilePath = null;
        mind.TilePathCorridor = null;
        mind.TileIndex = 0;
    }

    /// <summary>Somebody on the tile: give them a moment (three times), the way a
    /// player waits for someone to pass. False once waiting is no longer the answer.</summary>
    private bool WaitForMobile(BotMind mind, long now)
    {
        if (mind.MobileTries == 0) mind.MobileSinceMs = now;
        mind.MobileTries++;
        if (mind.MobileTries <= 3)
        {
            mind.MobileWaitUntilMs = now + _rng.Next(300, 1200);
            WindowMobileWaits++;
            if (mind.MobileTries == 2 && _rng.Next(100) < BlockedTalkPercent)
                Speak(mind, "blocked", fallback: null);
            return true;
        }
        // Waited long enough; after a few more tries it starts waiting again (people move).
        if (mind.MobileTries > 8) mind.MobileTries = 0;
        return false;
    }

    /// <summary>Percent chance a bot kept waiting says something about it ("excuse me").</summary>
    public int BlockedTalkPercent { get; set; } = 10;

    /// <summary>A step to the side of someone in the way.</summary>
    private bool SideStep(BotMind mind, Direction dir, bool run)
    {
        int first = _rng.Next(2) == 0 ? 2 : -2;
        foreach (int turn in new[] { first, -first, first + Math.Sign(first), -first - Math.Sign(first) })
        {
            if (Move(mind, (Direction)(((int)dir + turn) & 7), run))
            {
                WindowSideSteps++;
                WindowSteps++;
                return true;
            }
        }
        return false;
    }

    private void Breathe(BotMind mind, long now)
    {
        mind.Running = false;
        mind.BreathUntilMs = now + 1500;
    }

    /// <summary>The delay before the next act after a step attempt: the walk pace after
    /// a step, the rest of a wait, else a short pause.</summary>
    private long StepDelay(BotMind mind, bool stepped, bool run, long now)
    {
        if (stepped) return MovementEngine.GetMoveDelay(mind.Char.IsMounted, run);
        long wait = Math.Max(mind.MobileWaitUntilMs, mind.BreathUntilMs) - now;
        if (wait > 0) return Math.Clamp(wait, 100, 1500);
        return 400;
    }

    // ------------------------------------------------------------------
    //  Progress
    // ------------------------------------------------------------------

    /// <summary>Note the distance to what the bot walks toward. A target that moved
    /// more than three tiles, or a walk taken up again after a pause, starts over.</summary>
    private static void Watch(BotMind mind, Point3D target, int dist, long now)
    {
        if (mind.WatchAnchor.Map != target.Map || mind.WatchAnchor.GetDistanceTo(target) > 3 ||
            now - mind.WatchLastMs > 5000)
        {
            mind.WatchAnchor = target;
            mind.WatchBest = dist;
            mind.WatchSinceMs = now;
        }
        else if (dist < mind.WatchBest)
        {
            mind.WatchBest = dist;
            mind.WatchSinceMs = now;
        }
        mind.WatchLastMs = now;
    }

    /// <summary>How long the bot has walked toward its current target without getting
    /// any nearer; 0 when it is not walking anywhere.</summary>
    private static long StalledMs(BotMind mind, long now) =>
        now - mind.WatchLastMs < 2000 ? now - mind.WatchSinceMs : 0;

    // ------------------------------------------------------------------
    //  Doors
    // ------------------------------------------------------------------

    private Item? FindNearDoor(BotMind mind, long now)
    {
        // A stalled bot asks every act; the answer keeps two seconds.
        if (now < mind.DoorScanUntilMs && (mind.DoorScan == null || !mind.DoorScan.IsDeleted))
            return mind.DoorScan;
        var ch = mind.Char;
        Item? best = null;
        int bestDist = int.MaxValue;
        foreach (var item in _world.GetItemsInRange(ch.Position, 8))
        {
            if (item.IsDeleted || item.MapIndex != ch.MapIndex || Math.Abs(item.Z - ch.Z) > 15) continue;
            if (!IsClosedUnlockedDoor(item)) continue;
            int d = ch.Position.GetDistanceTo(item.Position);
            if (d < bestDist) { bestDist = d; best = item; }
        }
        mind.DoorScan = best;
        mind.DoorScanUntilMs = now + 2000;
        return best;
    }

    private bool IsClosedUnlockedDoor(Item item)
    {
        if (!DoorHelper.IsDoorItem(item, _world.MapData)) return false;
        if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) return false;
        return !(item.TryGetTag("DOOR_OPEN", out string? open) && open == "1");
    }

    private bool TryOpenDoorAhead(BotMind mind, Direction dir)
    {
        var ch = mind.Char;
        foreach (int turn in StraightFirst)
        {
            var d = (Direction)(((int)dir + turn) & 7);
            int x = ch.X, y = ch.Y;
            WalkCheck.Offset(d, ref x, ref y);
            if (TryOpenDoorOn(mind, new Point3D((short)x, (short)y, ch.Z, ch.MapIndex)))
                return true;
        }
        return false;
    }

    /// <summary>Open a shut, unlocked door standing on <paramref name="tile"/>: a door
    /// item by a double click, a door that is part of the map (a static) by the open-door
    /// macro, facing it, as a player opens one.</summary>
    private bool TryOpenDoorOn(BotMind mind, Point3D tile)
    {
        var ch = mind.Char;
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.IsDeleted || item.X != tile.X || item.Y != tile.Y || Math.Abs(item.Z - ch.Z) > 15) continue;
            if (!IsClosedUnlockedDoor(item)) continue;
            mind.Client!.HandleDoubleClick(item.Uid.Value);
            WindowDoors++;
            return true;
        }
        if (DoorHelper.FindNearestStaticDoor(_world.MapData, ch.MapIndex, tile.X, tile.Y, 0,
                out short sx, out short sy, out sbyte sz, out _, out _) &&
            sx == tile.X && sy == tile.Y && Math.Abs(sz - ch.Z) < 20 &&
            !_world.IsMapStaticDoorOpen(ch.MapIndex, sx, sy, sz))
        {
            ch.Direction = ch.Position.GetDirectionTo(tile);
            mind.Client!.OpenDoor(1);
            if (!_world.IsMapStaticDoorOpen(ch.MapIndex, sx, sy, sz)) return false;
            WindowDoors++;
            WindowStaticDoors++;
            return true;
        }
        return false;
    }

    public long WindowStaticDoors { get; private set; }

    /// <summary>A bot lingering right by a door steps away from it, so it does not
    /// stand in the doorway others need.</summary>
    private bool StepOffDoorway(BotMind mind)
    {
        var ch = mind.Char;
        foreach (var item in _world.GetItemsInRange(ch.Position, 1))
        {
            if (item.IsDeleted || !item.IsOnGround || Math.Abs(item.Z - ch.Z) > 15) continue;
            if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
            var away = item.Position.GetDirectionTo(ch.Position);
            if (item.X == ch.X && item.Y == ch.Y) away = (Direction)_rng.Next(8);
            foreach (int turn in StraightFirst)
                if (Move(mind, (Direction)(((int)away + turn) & 7), run: false))
                {
                    WindowDoorwaySteps++;
                    return true;
                }
            return false;
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  The step itself
    // ------------------------------------------------------------------

    /// <summary>A random step that keeps within <paramref name="radius"/> of
    /// <paramref name="anchor"/>.</summary>
    private void WanderNear(BotMind mind, Point3D anchor, int radius, bool run)
    {
        var dir = (Direction)_rng.Next(8);
        int px = mind.Char.X, py = mind.Char.Y;
        WalkCheck.Offset(dir, ref px, ref py);
        var probe = new Point3D((short)px, (short)py, mind.Char.Z, mind.Char.MapIndex);
        if (probe.GetDistanceTo(anchor) <= radius)
            Move(mind, dir, run);
    }

    /// <summary>One step through the player walk path, timed for the telemetry. A
    /// refusal is noted on the mind with its reason.</summary>
    private bool Move(BotMind mind, Direction dir, bool run)
    {
        var ch = mind.Char;
        var from = ch.Position;
        int tx = from.X, ty = from.Y;
        WalkCheck.Offset(dir, ref tx, ref ty);
        long nowMs = NowMs();
        if (mind.AvoidsTile(tx, ty, from.Map, nowMs))
            return Refused(mind, "portal_avoid");
        if (LeadsOffTheRoads(tx, ty, from))
            return Refused(mind, "off_road_map");
        // A player killer does not cross into a guarded region; one that stands in one
        // (just adopted) may walk out.
        if (IsPk(mind) && IsGuardedAt(new Point3D((short)tx, (short)ty, from.Z, from.Map)) && !IsGuardedAt(from))
            return Refused(mind, "guard_zone");
        // One step in sixteen is timed for the telemetry; the clock read is not free.
        bool timed = (++WindowMoves & 15) == 0;
        long m0 = timed ? Stopwatch.GetTimestamp() : 0;
        bool moved = _movement.TryMoveDetailed(ch, dir, run, 0, out var diag);
        if (!moved)
            Refused(mind, _movement.LastRefusal ?? "refused");
        else
        {
            mind.LastBlock = BotBlock.None;
            // Carried off by the step itself: the tile was a teleporter or a gate.
            // What that means for the trip is decided once the act is over.
            if (ch.Position.Map != from.Map || ch.Position.GetDistanceTo(from) > 12)
            {
                mind.RememberPortal(tx, ty, from.Map, nowMs);
                mind.Carried = true;
            }
            // The viewers get the step as from a walking player. Like the walk
            // handler, a step that landed anywhere but the next tile (teleporter,
            // script) sends no 0x77: the move hook has already told the viewers.
            else if (ch.X == tx && ch.Y == ty)
                mind.Client?.BroadcastOwnStep(run);
        }
        if (!timed) return moved;
        long us = (Stopwatch.GetTimestamp() - m0) * 1_000_000 / Stopwatch.Frequency;
        WindowMoveUs += us * 16;
        if (us > WindowMaxMoveUs)
        {
            WindowMaxMoveUs = us;
            WindowMaxMoveWhere = $"{from} {dir}{(moved ? "" : " refused " + (_movement.LastRefusal ?? "?"))}";
        }
        if (us > 10_000 && _slowMovesLogged < 20)
        {
            _slowMovesLogged++;
            _logger.LogInformation(
                "[playerbots] slow step {Us}us {Name} {From} {Dir} moved={Moved} refusal={Refusal} statics={Statics} mobiles={Mobiles} reason={Reason}",
                us, ch.Name, from, dir, moved, _movement.LastRefusal, diag.FwdStaticTotal, diag.FwdMobileCount, diag.FwdReason);
        }
        return moved;
    }

    private int _slowMovesLogged;

    private bool Refused(BotMind mind, string reason)
    {
        mind.LastRefusal = reason;
        var block = Classify(reason);
        if (block != mind.LastBlock && block != BotBlock.Static)
            mind.Note(NowMs(), $"step refused: {reason}");
        mind.LastBlock = block;
        _refusals[reason] = _refusals.GetValueOrDefault(reason) + 1;
        return false;
    }

    internal static BotBlock Classify(string? reason) => reason switch
    {
        "character_on_tile" => BotBlock.Mobile,
        "stamina" => BotBlock.Stamina,
        "nomovetill" or "freeze" or "cast_freeze" or "speedmode" or "nonmover" => BotBlock.Frozen,
        _ => BotBlock.Static,
    };

    /// <summary>A teleporter, gate or telepad on the tile that sends a walker to a
    /// map the road network does not cover: a bot would be lost there.</summary>
    private bool LeadsOffTheRoads(int x, int y, Point3D from)
    {
        if (Nav.Waypoints.Count == 0) return false;
        var tile = new Point3D((short)x, (short)y, from.Z, from.Map);
        if (_world.GetMapTeleport(tile) is { } tp && !Nav.HasMap(tp.Dest.Map)) return true;
        foreach (var item in _world.GroundItemsOnTile(from.Map, x, y))
        {
            if (item.IsDeleted || item.X != x || item.Y != y) continue;
            if (item.ItemType is not (ItemType.Moongate or ItemType.Telepad)) continue;
            var dest = item.MoreP;
            if ((dest.X != 0 || dest.Y != 0) && !Nav.HasMap(dest.Map)) return true;
        }
        return false;
    }

    private static readonly int[] StraightFirst = [0, 1, -1];

    private static bool SameTile(Point3D a, Point3D b) => a.X == b.X && a.Y == b.Y && a.Map == b.Map;

    private static int Chebyshev(Point3D a, Point3D b) =>
        a.Map != b.Map ? int.MaxValue : Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>The first tile of <paramref name="path"/> from <paramref name="from"/>
    /// on that is one step from <paramref name="pos"/> (or the tile itself), or -1.</summary>
    private static int JoinIndex(List<Point3D> path, Point3D pos, int from = 0)
    {
        for (int i = Math.Max(0, from); i < path.Count; i++)
            if (Chebyshev(path[i], pos) <= 1 && Math.Abs(path[i].Z - pos.Z) <= 12)
                return i;
        return -1;
    }

    /// <summary>How far a player sees a bot vanish or appear.</summary>
    private const int UnseenRange = 18;
    /// <summary>A lift (stuck, stranded, map, a player killer back to its haunt) waits
    /// up to this long while a player can see where the bot stands or where it would
    /// appear; then it goes ahead anyway. 0 lifts at once.</summary>
    public long UnseenLiftWaitMs { get; set; }
    public long WindowLiftWaits { get; private set; }

    /// <summary>True while a lift of the bot to <paramref name="to"/> should wait for
    /// nobody to be looking; the bot tries again at a later act.</summary>
    private bool LiftSeen(BotMind mind, Point3D to, long now)
    {
        if (UnseenLiftWaitMs <= 0 ||
            (!IsWatched(mind.Char.Position, UnseenRange) && !IsWatched(to, UnseenRange)))
        {
            mind.LiftWaitSinceMs = 0;
            return false;
        }
        if (mind.LiftWaitSinceMs == 0)
        {
            mind.LiftWaitSinceMs = now;
            WindowLiftWaits++;
            mind.Note(now, $"a lift to {to} waits: a player is looking");
        }
        if (now - mind.LiftWaitSinceMs >= UnseenLiftWaitMs)
        {
            mind.LiftWaitSinceMs = 0;
            return false;
        }
        mind.NextActMs = now + _rng.Next(2000, 4000);
        return true;
    }

    private void Teleport(Character ch, Point3D pos)
    {
        if (_world.MoveCharacter(ch, pos))
            OnAppeared?.Invoke(ch);
    }
}
