using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.PlayerBots;

/// <summary>Walking: town life, road trips, dungeon runs, and the step that moves a
/// bot one tile toward a point.</summary>
public sealed partial class PlayerBotEngine
{
    private void ActLinger(BotMind mind, long now)
    {
        // Acting in a scene: stay put until it is played out.
        if (mind.Scene != null)
        {
            mind.NextActMs = now + 1000;
            return;
        }
        if (now >= mind.LingerUntilMs)
        {
            // Where players are, a crowd lingers: a bank or a healer keeps a bot a
            // while longer when someone is there to see it.
            if (mind.LingerExtensions < 3 && mind.At is { } at && IsLingerPlace(at) &&
                _rng.Next(100) < 60 && IsWatched(mind.Char.Position))
            {
                mind.LingerExtensions++;
                mind.LingerUntilMs = now + _rng.Next(60_000, 150_000);
                mind.NextActMs = now + _rng.Next(4000, 12_000);
                return;
            }
            MaybeMoveHouse(mind, now);
            mind.Goal = PickShopping(mind, now) ?? PickDestination(mind, spawnable: false);
            if (mind.Goal == null) { mind.NextActMs = now + 30_000; return; }
            mind.Char.SetTag(GoalTag, mind.Goal.Name);
            mind.Mode = BotMode.Travel;
            mind.Route = null;
            mind.NextActMs = now + 200;
            return;
        }

        // Idle life: now and then a step or two about. What is said is up to the
        // scenes played for the players around (see Direct); nobody near, no talk.
        if (_rng.Next(100) < 25 && mind.At != null)
            WanderNear(mind, mind.LingerAnchor, 3, run: false);
        mind.NextActMs = now + _rng.Next(4000, 12_000);
    }

    private static bool IsLingerPlace(BotDestination d) =>
        d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Healer", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Inn", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Tavern", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Moongate", StringComparison.OrdinalIgnoreCase);

    private void ActTravel(BotMind mind, long now)
    {
        var ch = mind.Char;
        var goal = mind.Goal;
        if (goal == null) { mind.Mode = BotMode.Linger; mind.LingerUntilMs = 0; return; }

        if (mind.Route == null)
        {
            // Leave by the door: from the node the spot the bot stands on is linked
            // to, when it stands on one; else the nearest node.
            int from = mind.BadStarts.Count == 0 ? LinkedNodeAt(mind.At, ch.Position) : -1;
            if (from < 0) from = Nav.FindNearest(ch.Position, 24, mind.BadStarts);
            if (from < 0) from = Nav.FindNearest(ch.Position, 64, mind.BadStarts);
            // Arrive by the door too: pick the stopping spot first, aim at its node.
            int spot = goal.PickArrival(_rng);
            mind.GoalSpot = spot;
            int to = spot >= 0 && goal.ArrivalLinks[spot].Count > 0 ? goal.ArrivalLinks[spot][0] : goal.NearestWaypoint;
            long r0 = Stopwatch.GetTimestamp();
            var route = from >= 0 ? Nav.Route(from, to) : null;
            long routeUs = (Stopwatch.GetTimestamp() - r0) * 1_000_000 / Stopwatch.Frequency;
            if (routeUs > WindowMaxRouteUs) WindowMaxRouteUs = routeUs;
            if (route == null)
            {
                // Nowhere reachable from here: give the trip up and choose again later.
                mind.Goal = null;
                ch.RemoveTag(GoalTag);
                mind.BeginLinger(null, now, _rng, short_: true);
                return;
            }
            mind.Route = route;
            mind.Leg = 0;
            mind.FinalSpot = spot >= 0 ? goal.Arrivals[spot] : goal.Pos;
            mind.ResetLeg(now);
            mind.Running = !ch.IsDead && _rng.Next(100) < 30;
            if (!ch.IsDead && _rng.Next(100) < 20) Speak(mind, "traveling");
        }

        // Shut in: walk to the door it found and open it, then carry on.
        if (mind.DoorTarget != null)
        {
            var door = mind.DoorTarget;
            if (door.IsDeleted || now - mind.DoorSinceMs > 20_000 ||
                (door.TryGetTag("DOOR_OPEN", out string? isOpen) && isOpen == "1"))
            {
                mind.DoorTarget = null;
                mind.ResetLeg(now);
            }
            else if (ch.Position.GetDistanceTo(door.Position) <= 1)
            {
                mind.Client?.HandleDoubleClick(door.Uid.Value);
                WindowDoors++;
                mind.DoorTarget = null;
                mind.ResetLeg(now);
                mind.NextActMs = now + 600;
                return;
            }
            else
            {
                bool walked = StepToward(mind, door.Position, run: false);
                mind.NextActMs = now + (walked ? MovementEngine.GetMoveDelay(ch.IsMounted, false) : 400);
                return;
            }
        }

        var target = mind.LegTarget(Nav);
        // The stopping spot is stepped on; one tile short does when someone else
        // stands on it, three short once the last steps stall: a visitor in the way
        // is no reason to grind against them.
        bool finalLeg = mind.Leg >= mind.Route.Count;
        int range = !finalLeg ? Nav.Waypoints[mind.Route[mind.Leg]].ArrivalRange
            : now - mind.LegProgressMs > 4000 ? 3
            : SomeoneOn(target, ch) ? 1 : 0;
        if (ch.Position.GetDistanceTo(target) <= range && Math.Abs(ch.Z - target.Z) <= 8)
        {
            mind.BadStarts.Clear();
            mind.DoorTried = false;
            if (mind.Leg >= mind.Route.Count)
            {
                Arrive(mind, goal, now);
                return;
            }
            mind.Leg++;
            mind.ResetLeg(now);
            target = mind.LegTarget(Nav);
        }

        // No ground made on this leg for a minute: lift the bot onto the leg's
        // target rather than let it grind against a wall.
        int dist = ch.Position.GetDistanceTo(target);
        if (dist < mind.BestLegDist) { mind.BestLegDist = dist; mind.LegProgressMs = now; }
        else if (mind.Leg == 0 && mind.BadStarts.Count < 3 && now - mind.LegProgressMs > 20_000)
        {
            // The first node will not come. A shut door near by is the usual
            // reason (a shop's door on another wall): go and open it. Otherwise
            // the node is the nearest one through a wall or on another floor:
            // start from the next nearest instead.
            if (mind.Client != null && !mind.DoorTried && FindNearDoor(ch) is { } shut)
            {
                mind.DoorTried = true;
                mind.DoorTarget = shut;
                mind.DoorSinceMs = now;
                mind.ResetLeg(now);
                mind.NextActMs = now + 200;
                return;
            }
            mind.BadStarts.Add(mind.Route[0]);
            mind.Route = null;
            mind.ResetLeg(now);
            mind.NextActMs = now + 200;
            return;
        }
        else if (now - mind.LegProgressMs > 60_000)
        {
            StuckRescues++;
            WindowRescues++;
            // Learn the spot: the way out of where it stood, or into where it was
            // going, does not exist in this world. Nobody is sent there again.
            // A bot stuck right by its target (a doorway full of others) proves
            // nothing about the way, so only a rescue from more than three tiles counts.
            bool farOff = dist > 3;
            if (mind.Leg == 0 && mind.At != null && mind.At.ArrivalAt(ch.Position) is int outOf and >= 0)
                LearnBadSpot(mind.At, outOf);
            else if (mind.Leg >= mind.Route.Count && mind.GoalSpot >= 0 && farOff)
                LearnBadSpot(goal, mind.GoalSpot);
            else if (mind.Leg > 0 && mind.Leg < mind.Route.Count && farOff)
                LearnBadLink(mind.Route[mind.Leg - 1], mind.Route[mind.Leg]);
            if (WindowRescues <= 15)
                _logger.LogInformation(
                    "[playerbots] rescue {Name} at {Pos} -> {Target} (leg {Leg}/{Legs} to {Goal}, mode {Mode}, dead {Dead})",
                    ch.Name, ch.Position, target, mind.Leg, mind.Route?.Count ?? 0, goal.Name, mind.Mode, ch.IsDead);
            Teleport(ch, target);
            mind.ResetLeg(now);
            mind.NextActMs = now + 500;
            return;
        }

        // Stalled on any leg: a shut door near by is the usual reason (the way into a
        // shop on another wall), so go and open it, once per leg. No tile search: a
        // search costs a walk check per opened node, measured at up to a second on a
        // town map, which a stall that happens a hundred times a minute cannot pay.
        long stalledMs = now - mind.LegProgressMs;
        if (stalledMs > 8000 && !mind.DoorTried && mind.Client != null && FindNearDoor(ch) is { } stuckDoor)
        {
            mind.DoorTried = true;
            mind.DoorTarget = stuckDoor;
            mind.DoorSinceMs = now;
            WindowStallDoors++;
            mind.NextActMs = now + 200;
            return;
        }
        // Stalled near the leg's end: a tile search, on a deadline so one hard case
        // cannot hold the tick (the sectors' per-tile index keeps a search to a few
        // milliseconds in a town).
        if (mind.TilePath == null && dist <= StallSearchRange && mind.StallSearches < 2 &&
            stalledMs > (mind.StallSearches == 0 ? 5000 : 25_000) && CanSearch())
        {
            mind.StallSearches++;
            WindowSearches++;
            WindowStallSearches++;
            long s0 = Stopwatch.GetTimestamp();
            mind.TilePath = _pathfinder.FindPath(ch.Position, target, ch.MapIndex, CanFlags.None, ch,
                maxNodes: StallSearchMaxNodes, maxRadius: dist + 8,
                deadline: s0 + (long)(StallSearchMs * Stopwatch.Frequency / 1000.0));
            long us = (Stopwatch.GetTimestamp() - s0) * 1_000_000 / Stopwatch.Frequency;
            if (us > WindowMaxSearchUs) WindowMaxSearchUs = us;
            mind.TileIndex = 0;
            mind.FollowSide = 0;
            if (mind.TilePath == null) WindowFailedSearches++;
        }

        // The last leg still stalled: another stopping spot of the same place may be
        // reachable where this one is not.
        if (finalLeg && stalledMs > 20_000 && mind.SpotTries == 0 && goal.Arrivals.Count > 1)
        {
            mind.SpotTries = 1;
            int other = goal.PickArrival(_rng);
            if (other >= 0 && other != mind.GoalSpot)
            {
                mind.GoalSpot = other;
                mind.FinalSpot = goal.Arrivals[other];
                mind.BestLegDist = int.MaxValue;
                mind.LegProgressMs = now;
                mind.FollowSide = 0;
                WindowSpotSwitches++;
            }
        }

        bool run = mind.Running && ch.Stam > ch.MaxStam / 3;
        long st0 = Stopwatch.GetTimestamp();
        bool steppedLeg = StepToward(mind, target, run);
        long stepUs = (Stopwatch.GetTimestamp() - st0) * 1_000_000 / Stopwatch.Frequency;
        if (stepUs > WindowMaxStepUs) WindowMaxStepUs = stepUs;
        if (steppedLeg)
            mind.NextActMs = now + MovementEngine.GetMoveDelay(ch.IsMounted, run);
        else
            mind.NextActMs = now + 400;
    }

    private bool SomeoneOn(Point3D tile, Character self)
    {
        foreach (var c in _world.GetCharsInRange(tile, 0))
            if (c != self && !c.IsDeleted && c.X == tile.X && c.Y == tile.Y && Math.Abs(c.Z - tile.Z) <= 8)
                return true;
        return false;
    }

    private void Arrive(BotMind mind, BotDestination goal, long now)
    {
        var ch = mind.Char;
        if (goal.Type == CorpseGoalType)
        {
            // The clock for taking things off starts at the body, not at the healer:
            // the walk back can be long.
            mind.ReclaimUntilMs = now + 240_000;
            mind.Mode = BotMode.Reclaim;
            mind.Goal = null;
            mind.Route = null;
            ch.RemoveTag(GoalTag);
            mind.NextActMs = now + 600;
            return;
        }
        // A moongate: walk into the gate itself; the gate decides where it goes.
        if (goal.Type.Equals("Moongate", StringComparison.OrdinalIgnoreCase) && mind.GateTries++ == 0)
        {
            var gate = _world.GetItemsInRange(goal.Pos, 4)
                .FirstOrDefault(i => !i.IsDeleted && i.ItemType == ItemType.Moongate);
            if (gate != null)
            {
                mind.FinalSpot = gate.Position;
                mind.ResetLeg(now);
                mind.NextActMs = now + 300;
                return;
            }
        }
        mind.GateTries = 0;
        // Come to buy: find the vendor that sells what it needs.
        if (mind.ShoppingFor != null)
        {
            mind.BeginLinger(goal, now, _rng, short_: true);
            StartShopping(mind, now);
            return;
        }
        // A dungeon mouth: step onto its spot (the way in), then live inside.
        if (goal.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase) && !IsUnderground(ch.Position))
        {
            mind.CrawlUntilMs = now + _rng.Next(600_000, 1_500_000);
            mind.BeginLinger(goal, now, _rng, short_: true);
            mind.LingerUntilMs = now + 2000;
            return;
        }
        mind.BeginLinger(goal, now, _rng, short_: IsUnderground(ch.Position));
    }

    /// <summary>One tile toward <paramref name="target"/>, with no search. Straight
    /// at it (or 45 degrees either side) while that is open; against an obstacle,
    /// the bot keeps it on one hand and walks its edge (the Bug2 rule) until it is
    /// nearer the target than where it met the obstacle and the way ahead is clear.
    /// A shut door is opened first. A tile search is kept for short hops
    /// (<paramref name="searchWithin"/> tiles, a fight's closing in) because one
    /// costs a walk check per neighbour of every node it opens.</summary>
    private bool StepToward(BotMind mind, Point3D target, bool run, int searchWithin = 0)
    {
        var ch = mind.Char;
        if (ch.Position.X == target.X && ch.Position.Y == target.Y) return false;
        int dist = ch.Position.GetDistanceTo(target);

        if (mind.TilePath != null && mind.TileIndex < mind.TilePath.Count)
        {
            var next = mind.TilePath[mind.TileIndex];
            if (next.GetDistanceTo(ch.Position) == 1 && Move(ch, ch.Position.GetDirectionTo(next), run, 0))
            {
                mind.TileIndex++;
                WindowSteps++;
                return true;
            }
            mind.TilePath = null;
        }

        var dir = ch.Position.GetDirectionTo(target);
        if (mind.FollowSide != 0)
        {
            // Leave the wall once nearer than where it was met and the way is open.
            if (dist < mind.FollowFromDist && Move(ch, dir, run, 0))
            {
                mind.FollowSide = 0;
                WindowSteps++;
                return true;
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
                if (Move(ch, d, run, 0))
                {
                    mind.FollowHeading = d;
                    WindowSteps++;
                    return true;
                }
            }
            if (mind.Client != null) TryOpenDoorAhead(mind, mind.FollowHeading);
            return false;
        }

        foreach (int turn in StraightFirst)
        {
            var d = (Direction)(((int)dir + turn) & 7);
            if (Move(ch, d, run, 0))
            {
                WindowSteps++;
                return true;
            }
        }

        // A shut door ahead: open it the way the client's auto-open does for a player.
        if (mind.Client != null && TryOpenDoorAhead(mind, dir))
            return false;

        if (searchWithin > 0 && dist <= searchWithin && CanSearch())
        {
            WindowSearches++;
            long s0 = Stopwatch.GetTimestamp();
            mind.TilePath = _pathfinder.FindPath(ch.Position, target, ch.MapIndex, CanFlags.None, ch,
                maxNodes: PathSearchMaxNodes, maxRadius: dist + 4);
            long us = (Stopwatch.GetTimestamp() - s0) * 1_000_000 / Stopwatch.Frequency;
            if (us > WindowMaxSearchUs) WindowMaxSearchUs = us;
            if (mind.TilePath == null) WindowFailedSearches++;
            mind.TileIndex = 0;
            if (mind.TilePath != null) return false;
        }

        // Meet the obstacle: put it on one hand by turning the other way (wall on
        // the right means heading 90 degrees left of where it was going).
        mind.FollowSide = _rng.Next(2) == 0 ? 1 : -1;
        mind.FollowHeading = (Direction)(((int)dir - mind.FollowSide * 2) & 7);
        mind.FollowSteps = 0;
        mind.FollowFromDist = dist;
        return false;
    }

    /// <summary>The road node linked to the arrival spot of <paramref name="at"/>
    /// that <paramref name="pos"/> stands on (within 2 tiles), or -1.</summary>
    private int LinkedNodeAt(BotDestination? at, Point3D pos)
    {
        if (at == null) return -1;
        for (int i = 0; i < at.Arrivals.Count; i++)
            if (at.ArrivalLinks[i].Count > 0 && at.Arrivals[i].GetDistanceTo(pos) <= 2)
                return at.ArrivalLinks[i][0];
        return -1;
    }

    private Objects.Items.Item? FindNearDoor(Character ch)
    {
        Objects.Items.Item? best = null;
        int bestDist = int.MaxValue;
        foreach (var item in _world.GetItemsInRange(ch.Position, 8))
        {
            if (item.IsDeleted || item.MapIndex != ch.MapIndex || Math.Abs(item.Z - ch.Z) > 15) continue;
            if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
            if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) continue;
            if (item.TryGetTag("DOOR_OPEN", out string? open) && open == "1") continue;
            int d = ch.Position.GetDistanceTo(item.Position);
            if (d < bestDist) { bestDist = d; best = item; }
        }
        return best;
    }

    private bool TryOpenDoorAhead(BotMind mind, Direction dir)
    {
        var ch = mind.Char;
        foreach (int turn in StraightFirst)
        {
            var d = (Direction)(((int)dir + turn) & 7);
            int x = ch.X, y = ch.Y;
            WalkCheck.Offset(d, ref x, ref y);
            var tile = new Point3D((short)x, (short)y, ch.Z, ch.MapIndex);
            foreach (var item in _world.GetItemsInRange(tile, 0))
            {
                if (item.IsDeleted || Math.Abs(item.Z - ch.Z) > 15) continue;
                if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
                if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) continue;
                if (item.TryGetTag("DOOR_OPEN", out string? open) && open == "1") continue;
                mind.Client!.HandleDoubleClick(item.Uid.Value);
                WindowDoors++;
                return true;
            }
        }
        return false;
    }

    /// <summary>A random step that keeps within <paramref name="radius"/> of
    /// <paramref name="anchor"/>.</summary>
    private void WanderNear(BotMind mind, Point3D anchor, int radius, bool run)
    {
        var dir = (Direction)_rng.Next(8);
        int px = mind.Char.X, py = mind.Char.Y;
        WalkCheck.Offset(dir, ref px, ref py);
        var probe = new Point3D((short)px, (short)py, mind.Char.Z, mind.Char.MapIndex);
        if (probe.GetDistanceTo(anchor) <= radius)
            Move(mind.Char, dir, run, 0);
    }

    /// <summary>One step through the player walk path, timed for the telemetry.</summary>
    private bool Move(Character ch, Direction dir, bool run, byte sequence)
    {
        var from = ch.Position;
        int tx = from.X, ty = from.Y;
        WalkCheck.Offset(dir, ref tx, ref ty);
        if (MindOf(ch) is { } walker && walker.AvoidsTile(tx, ty, from.Map, Environment.TickCount64))
            return false;
        // One step in sixteen is timed for the telemetry; the clock read is not free.
        bool timed = (++WindowMoves & 15) == 0;
        long m0 = timed ? Stopwatch.GetTimestamp() : 0;
        bool moved = _movement.TryMoveDetailed(ch, dir, run, sequence, out var diag);
        // Carried off by the step itself: the tile was a teleporter or a gate.
        if (moved && (ch.Position.Map != from.Map || ch.Position.GetDistanceTo(from) > 12) && MindOf(ch) is { } carried)
            carried.RememberPortal(tx, ty, from.Map, Environment.TickCount64);
        // The viewers get the step as from a walking player. Like the walk handler,
        // a step that landed anywhere but the next tile (teleporter, script) sends no
        // 0x77: the move hook has already told the viewers about the jump.
        if (moved && ch.Position.Map == from.Map && ch.X == tx && ch.Y == ty)
            MindOf(ch)?.Client?.BroadcastOwnStep(run);
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

    private static readonly int[] StraightFirst = [0, 1, -1];
    private static readonly int[] SideFirst = [1, -1, 0, 2, -2];

    private bool CanSearch()
    {
        if (_searchesLeft <= 0 || Stopwatch.GetTimestamp() > _searchDeadline) return false;
        _searchesLeft--;
        return true;
    }

    private void Teleport(Character ch, Point3D pos)
    {
        if (_world.MoveCharacter(ch, pos))
            OnAppeared?.Invoke(ch);
    }

    internal static bool IsUnderground(Point3D p) => p.Map == 0 && p.X >= 5120 && p.Y < 2304;

    // ------------------------------------------------------------------
    //  Choices
    // ------------------------------------------------------------------

    private static readonly string[] SpawnTypes = ["Bank", "Inn", "Tavern", "Healer", "Provisioner", "VendorProvisioner"];

    private BotDestination? PickDestination(BotMind? mind, bool spawnable)
    {
        var all = Nav.Destinations;
        if (all.Count == 0) return null;
        IEnumerable<BotDestination> pool = all;
        int component = -1;
        if (mind != null)
        {
            // Only what can be walked to from here: the piece of road the bot is on.
            int here = Nav.FindNearest(mind.Char.Position, 24);
            if (here < 0) here = Nav.FindNearest(mind.Char.Position, 64);
            if (here >= 0) component = Nav.Waypoints[here].Component;
        }
        if (component >= 0)
            pool = pool.Where(d => Nav.ComponentOf(d) == component);

        if (spawnable)
        {
            // Born in town, where there is somewhere to go.
            var sizes = Nav.Destinations.GroupBy(Nav.ComponentOf).ToDictionary(g => g.Key, g => g.Count());
            pool = pool.Where(d => d.City.Length > 0 &&
                                   SpawnTypes.Any(t => d.Type.Equals(t, StringComparison.OrdinalIgnoreCase)) &&
                                   sizes.GetValueOrDefault(Nav.ComponentOf(d)) >= 4);
        }
        else if (mind != null)
        {
            var list0 = pool.ToList();
            if (IsUnderground(mind.Char.Position))
            {
                // Inside: room to room while the run lasts, then the way out.
                string[] want = Environment.TickCount64 < mind.CrawlUntilMs && mind.Char.Hits * 2 > mind.Char.MaxHits
                    ? ["DungeonRoom", "DungeonDescend"]
                    : ["DungeonAscend", "DungeonEntrance"];
                var inside = list0.Where(d => want.Any(t => d.Type.Equals(t, StringComparison.OrdinalIgnoreCase)) && d != mind.At).ToList();
                if (inside.Count > 0) return inside[_rng.Next(inside.Count)];
            }
            else if (!list0.Any(d => d.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase)) || _rng.Next(100) < 4)
            {
                // Away from home with no road back, or just a trip: the moongate.
                var gates = list0.Where(d => d.Type.Equals("Moongate", StringComparison.OrdinalIgnoreCase) && d != mind.At).ToList();
                if (gates.Count > 0) return gates[_rng.Next(gates.Count)];
            }
            // A fighter goes hunting now and then; less often while players are about
            // to see it in town.
            int huntPct = IsWatched(mind.Char.Position, 40) ? 8 : 25;
            if (!IsUnderground(mind.Char.Position) && mind.IsFighter && _rng.Next(100) < huntPct)
            {
                var mouths = list0.Where(d => d.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase)).ToList();
                if (mouths.Count > 0) return mouths[_rng.Next(mouths.Count)];
            }
            // A player killer keeps to where people pass, out of town.
            if (PickPkHaunt(mind, list0) is { } haunt) return haunt;
            // Town life: toward the players, else most trips stay in the home town.
            pool = list0.Where(d => d.City.Length > 0);
            if (!IsPk(mind) && PickNearPlayers(mind, pool) is { } pulled) return pulled;
            if (_rng.Next(100) < 85)
            {
                var home = pool.Where(d => d.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase)).ToList();
                if (home.Count > 0) pool = home;
            }
        }
        // A place whose every stopping spot is known to be unreachable is not a
        // place to go: aiming at its middle would only end in a rescue each time.
        var list = pool.Where(d => (mind == null || d != mind.At) &&
                                   (d.Arrivals.Count == 0 || d.BadArrivals.Count < d.Arrivals.Count)).ToList();
        if (list.Count == 0) return null;
        if (spawnable && PickSpawnTown(list.Select(d => d.City).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) is { } town)
            list = list.Where(d => d.City.Equals(town, StringComparison.OrdinalIgnoreCase)).ToList();
        return WeightedPick(list, PlaceWeight);
    }

    private string? PickArchetype()
    {
        if (_archetypes.Count == 0) return null;
        int total = _archetypes.Sum(a => a.Weight);
        int roll = _rng.Next(total);
        foreach (var (name, w) in _archetypes)
        {
            if (roll < w) return name;
            roll -= w;
        }
        return _archetypes[^1].DefName;
    }

    private int ResolveCharDef(string defName)
    {
        if (_resources == null) return -1;
        var rid = _resources.ResolveDefName(defName);
        if (rid.IsValid && rid.Type == ResType.CharDef && DefinitionLoader.GetCharDef(rid.Index) != null)
            return rid.Index;
        return -1;
    }

    private int Roll(int min, int max, int fallback)
    {
        if (max <= 0) return min > 0 ? min : fallback;
        if (min <= 0 || min >= max) return max;
        return _rng.Next(min, max + 1);
    }

    private static string LingerCategory(BotDestination? at) => (at?.Type ?? "").ToLowerInvariant() switch
    {
        "bank" => "bank_actions",
        "inn" or "tavern" => "inn_visit",
        "healer" => "healer_visit",
        "stables" or "stable" => "stable_visit",
        "shrine" => "shrine_visit",
        "dungeonroom" or "dungeondescend" or "dungeonascend" => "dungeon_whispers",
        "landmark" => "landmark_visit",
        _ => "shopping",
    };
}
