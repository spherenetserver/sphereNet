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

/// <summary>Trips: town life, road trips, dungeon runs, the way back to the road when
/// there is none, and the choice of where to go next. The step itself is in the Walk
/// part, portals in the Portals part.</summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Times a stay is lengthened while a player is there to see it.</summary>
    public int MaxLingerExtensions { get; set; } = 1;

    private void ActLinger(BotMind mind, long now)
    {
        // Acting in a scene: stay put until it is played out.
        if (mind.Scene != null)
        {
            mind.NextActMs = now + 1000;
            return;
        }
        // Errands at a stop: the rune home marked, the purse squared with the bank.
        if (mind.BankStage == 0 && TryMarkHomeRune(mind, now)) return;
        if (ActBank(mind, now)) return;
        bool watched = IsWatched(mind.Char.Position);
        if (now >= mind.LingerUntilMs)
        {
            // Where players are, a crowd lingers: a bank or a healer keeps a bot a
            // while longer when someone is there to see it.
            if (mind.LingerExtensions < MaxLingerExtensions && mind.At is { } at && IsLingerPlace(at) &&
                _rng.Next(100) < 60 && watched)
            {
                mind.LingerExtensions++;
                mind.LingerUntilMs = now + _rng.Next(60_000, 150_000);
                mind.NextActMs = now + _rng.Next(3000, 8000);
                return;
            }
            MaybeMoveHouse(mind, now);
            mind.Haunting = false;
            // Far from home with no road back: a caster recalls on its rune.
            if (KnowsTravel(mind) && !HomeOnThisRoad(mind) && TryRecallHome(mind, now, "no road home"))
                return;
            mind.Goal = PickShopping(mind, now) ?? PickDestination(mind, spawnable: false);
            if (mind.Goal == null) { mind.NextActMs = now + 30_000; return; }
            mind.Char.SetTag(GoalTag, mind.Goal.Name);
            mind.Mode = BotMode.Travel;
            mind.Route = null;
            mind.NextActMs = now + 200;
            mind.Note(now, $"off to {mind.Goal.Name}{(mind.Haunting ? " (to wait about it)" : "")}");
            MaybeTakeCompany(mind, mind.Goal, now);
            return;
        }

        // Idle life: a step or two about, livelier where a player is watching. What is
        // said is up to the scenes played for the players around (see Direct).
        if (mind.At != null && !StepOffDoorway(mind) && _rng.Next(100) < (watched ? 60 : 25))
            WanderNear(mind, mind.LingerAnchor, 3, run: false);
        mind.NextActMs = now + (watched ? _rng.Next(3000, 8000) : _rng.Next(4000, 12_000));
    }

    /// <summary>Whether a place in the bot's home town hangs off the road it is on.</summary>
    private bool HomeOnThisRoad(BotMind mind)
    {
        int here = Nav.FindNearest(mind.Char.Position, 64);
        if (here < 0) return false;
        int component = Nav.Waypoints[here].Component;
        foreach (var d in Nav.Destinations)
            if (d.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase) && Nav.ComponentOf(d) == component)
                return true;
        return false;
    }

    /// <summary>Percent chance per step that a bot walking where a player sees it stops
    /// a moment to look about.</summary>
    public int PausePerMille { get; set; } = 20;
    public long WindowPauses { get; private set; }

    private static bool IsLingerPlace(BotDestination d) =>
        d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Healer", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Inn", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Tavern", StringComparison.OrdinalIgnoreCase) ||
        d.Type.Equals("Moongate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plan the trip: leave by the door of where it stands, arrive by the door
    /// of where it goes (or onto the portal tile, for a portal). False when there is
    /// no road; then <see cref="OnNoRoute"/> has decided what happens instead.</summary>
    private bool PlanTrip(BotMind mind, BotDestination goal, long now)
    {
        var ch = mind.Char;
        // Leave by the door: from the node the spot the bot stands on is linked
        // to, when it stands on one; else the nearest node.
        int from = mind.BadStarts.Count == 0 ? LinkedNodeAt(mind.At, ch.Position) : -1;
        if (from < 0) from = Nav.FindNearest(ch.Position, 24, mind.BadStarts);
        if (from < 0) from = Nav.FindNearest(ch.Position, 64, mind.BadStarts);
        // Arrive by the door too: pick the stopping spot first, aim at its node.
        int spot = goal.PickArrival(_rng);
        int to = spot >= 0 && goal.ArrivalLinks[spot].Count > 0 ? goal.ArrivalLinks[spot][0] : goal.NearestWaypoint;
        long r0 = Stopwatch.GetTimestamp();
        var route = from >= 0 ? Nav.Route(from, to) : null;
        long routeUs = (Stopwatch.GetTimestamp() - r0) * 1_000_000 / Stopwatch.Frequency;
        if (routeUs > WindowMaxRouteUs) WindowMaxRouteUs = routeUs;
        if (route == null)
        {
            OnNoRoute(mind, goal, from, now);
            return false;
        }
        mind.NoRouteCount = 0;
        mind.NoRouteSinceMs = 0;
        mind.Route = route;
        mind.Leg = 0;
        mind.GoalSpot = spot;
        var stop = spot >= 0 ? goal.Arrivals[spot] : goal.Pos;
        mind.PortalTrip = CrossesPortal(mind, goal);
        if (mind.PortalTrip)
            stop = NearestPortalTile(goal, stop);
        else if (mind.Haunting && goal.IsPortalType && to >= 0)
            // Waiting about a portal, not going through it: the road's end before it.
            stop = Nav.Waypoints[to].Pos;
        mind.FinalSpot = stop;
        mind.ResetLeg(now);
        mind.Running = !ch.IsDead && _rng.Next(100) < 30;
        if (!ch.IsDead && _rng.Next(100) < 20) Speak(mind, "traveling");
        return true;
    }

    private void ActTravel(BotMind mind, long now)
    {
        var ch = mind.Char;
        var goal = mind.Goal;
        if (goal == null) { mind.Mode = BotMode.Linger; mind.LingerUntilMs = 0; return; }

        if (mind.Route == null && !PlanTrip(mind, goal, now))
            return;

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
                bool walked = StepToward(mind, door.Position, run: false, searchWithin: 12);
                mind.NextActMs = now + StepDelay(mind, walked, false, now);
                return;
            }
        }

        var route = mind.Route!;
        var target = mind.LegTarget(Nav);
        bool finalLeg = mind.Leg >= route.Count;
        int range;
        if (!finalLeg)
            range = Nav.Waypoints[route[mind.Leg]].ArrivalRange;
        else if (mind.PortalTrip)
        {
            // A portal is stepped on, never stopped short of; a taken tile is swapped
            // for a free one of the same portal.
            range = 0;
            if (SomeoneOn(target, ch) && FreePortalTile(goal, target, ch) is { } free)
            {
                mind.FinalSpot = free;
                target = free;
            }
        }
        else
            // The stopping spot is stepped on; one tile short does when someone else
            // stands on it, three short once the last steps stall: a visitor in the way
            // is no reason to grind against them.
            range = now - mind.LegProgressMs > 4000 ? 3 : SomeoneOn(target, ch) ? 1 : 0;

        int dist = ch.Position.GetDistanceTo(target);
        if (mind.LegStartDist < 0) mind.LegStartDist = dist;
        bool onTile = ch.X == target.X && ch.Y == target.Y;
        if (dist <= range && (Math.Abs(ch.Z - target.Z) <= 8 || onTile))
        {
            mind.BadStarts.Clear();
            mind.DoorTried = false;
            if (finalLeg)
            {
                Arrive(mind, goal, now);
                return;
            }
            mind.Leg++;
            mind.ResetLeg(now);
            target = mind.LegTarget(Nav);
            finalLeg = mind.Leg >= route.Count;
            dist = ch.Position.GetDistanceTo(target);
            mind.LegStartDist = dist;
        }

        // The leg's progress clock: only a real gain restarts it (three tiles, or
        // any gain while walking straight), so a wall walk that inches closer cannot
        // hold the rescue off for ever.
        if (dist < mind.BestLegDist) mind.BestLegDist = dist;
        if (dist < mind.ProgressDist &&
            (mind.FollowSide == 0 || mind.ProgressDist == int.MaxValue || dist <= mind.ProgressDist - 3))
        {
            mind.ProgressDist = dist;
            mind.LegProgressMs = now;
        }
        long stalledMs = now - mind.LegProgressMs;
        long legCap = 90_000 + Math.Max(0, mind.LegStartDist) * 800L;

        if (mind.Leg == 0 && mind.BadStarts.Count < 3 && stalledMs > 12_000)
        {
            // The first node will not come. A shut door near by is the usual
            // reason (a shop's door on another wall): go and open it. Otherwise
            // the node is the nearest one through a wall or on another floor:
            // start from the next nearest instead.
            if (mind.Client != null && !mind.DoorTried && FindNearDoor(mind, now) is { } shut)
            {
                mind.DoorTried = true;
                mind.DoorTarget = shut;
                mind.DoorSinceMs = now;
                mind.ResetLeg(now);
                mind.NextActMs = now + 200;
                return;
            }
            mind.BadStarts.Add(route[0]);
            mind.Route = null;
            mind.ResetLeg(now);
            mind.NextActMs = now + 200;
            mind.Note(now, $"first node {Nav.Waypoints[route[0]].Name} will not come; another start");
            return;
        }
        if (stalledMs > 60_000 || now - mind.LegStartedMs > legCap)
        {
            Rescue(mind, goal, target, dist, now);
            return;
        }

        // Stalled on any leg: a shut door near by is the usual reason (the way into a
        // shop on another wall), so go and open it, once per leg.
        if (stalledMs > 8000 && !mind.DoorTried && mind.Client != null && FindNearDoor(mind, now) is { } stuckDoor)
        {
            mind.DoorTried = true;
            mind.DoorTarget = stuckDoor;
            mind.DoorSinceMs = now;
            WindowStallDoors++;
            mind.NextActMs = now + 200;
            mind.Note(now, $"stalled {stalledMs / 1000}s; opens the door at {stuckDoor.Position}");
            return;
        }

        // The last leg still stalled: another stopping spot (or portal tile) of the
        // same place may be reachable where this one is not.
        if (finalLeg && stalledMs > 20_000 && mind.SpotTries == 0)
        {
            mind.SpotTries = 1;
            if (mind.PortalTrip)
            {
                if (FreePortalTile(goal, target, ch) is { } other)
                {
                    mind.FinalSpot = other;
                    WindowSpotSwitches++;
                }
            }
            else if (goal.Arrivals.Count > 1)
            {
                int other = goal.PickArrival(_rng);
                if (other >= 0 && other != mind.GoalSpot)
                {
                    mind.GoalSpot = other;
                    mind.FinalSpot = goal.Arrivals[other];
                    mind.BestLegDist = int.MaxValue;
                    mind.ProgressDist = int.MaxValue;
                    mind.LegProgressMs = now;
                    mind.FollowSide = 0;
                    WindowSpotSwitches++;
                }
            }
        }

        // Travelling with another: keep pace with it.
        if (mind.Leader != null && PaceWithLeader(mind, target, now))
        {
            mind.LegProgressMs = now;
            return;
        }
        bool run = mind.Running && ch.Stam > ch.MaxStam / 3;
        long st0 = Stopwatch.GetTimestamp();
        _stepCorridor = !finalLeg && mind.Leg > 0 ? (route[mind.Leg - 1], route[mind.Leg]) : null;
        mind.LegLink = _stepCorridor;
        bool steppedLeg;
        try { steppedLeg = StepToward(mind, target, run); }
        finally { _stepCorridor = null; }
        long stepUs = (Stopwatch.GetTimestamp() - st0) * 1_000_000 / Stopwatch.Frequency;
        if (stepUs > WindowMaxStepUs) WindowMaxStepUs = stepUs;
        mind.NextActMs = now + StepDelay(mind, steppedLeg, run, now);
        // Where a player sees it, a walker now and then stops a moment to look about.
        if (steppedLeg && !run && !finalLeg && mind.Leader == null && _rng.Next(1000) < PausePerMille &&
            IsWatched(ch.Position))
        {
            mind.NextActMs += _rng.Next(1000, 3000);
            WindowPauses++;
        }
    }

    /// <summary>No ground made on a leg for a minute (or the leg ran far over its
    /// time): lift the bot onto the leg's target rather than let it grind against a
    /// wall, and learn from it.</summary>
    private void Rescue(BotMind mind, BotDestination goal, Point3D target, int dist, long now)
    {
        var ch = mind.Char;
        var route = mind.Route!;
        StuckRescues++;
        WindowRescues++;
        // Learn the spot: the way out of where it stood, or into where it was
        // going, does not exist in this world. Nobody is sent there again.
        // A bot stuck right by its target because others fill the doorway proves
        // nothing about the way, so a rescue from within three tiles counts only when
        // what stopped it was the place itself, not people.
        bool farOff = dist > 3 || mind.LastBlock == BotBlock.Static;
        if (mind.Leg == 0 && mind.At != null && mind.At.ArrivalAt(ch.Position) is int outOf and >= 0)
            LearnBadSpot(mind.At, outOf);
        else if (mind.Leg >= route.Count && mind.GoalSpot >= 0 && farOff && !mind.PortalTrip)
            LearnBadSpot(goal, mind.GoalSpot);
        else if (mind.Leg > 0 && mind.Leg < route.Count && farOff)
            LearnBadLink(route[mind.Leg - 1], route[mind.Leg]);
        if (WindowRescues <= 15)
            _logger.LogInformation(
                "[playerbots] rescue {Name} at {Pos} -> {Target} (leg {Leg}/{Legs} to {Goal}, mode {Mode}, dead {Dead}, last refusal {Refusal}, trace {Trace})",
                ch.Name, ch.Position, target, mind.Leg, route.Count, goal.Name, mind.Mode, ch.IsDead,
                mind.LastRefusal ?? "-", string.Join(" | ", mind.Trace().TakeLast(6).Select(t => t.Text)));
        mind.Note(now, $"rescued {ch.Position} -> {target}");
        // A portal tile is not stepped onto by a lift (the teleporter only answers a
        // step): the lift sets it down beside the tile and the next act steps on.
        if (mind.PortalTrip && mind.Leg >= route.Count)
            target = BesideTile(target);
        Teleport(ch, target);
        mind.ResetLeg(now);
        mind.NextActMs = now + 500;
    }

    /// <summary>A standable tile next to <paramref name="tile"/>, or the tile itself.</summary>
    private Point3D BesideTile(Point3D tile)
    {
        for (int k = 0; k < 8; k++)
        {
            int x = tile.X, y = tile.Y;
            WalkCheck.Offset((Direction)k, ref x, ref y);
            var p = new Point3D((short)x, (short)y, tile.Z, tile.Map);
            if (FindTeleport(p) != null) continue;
            if (BotNavGraph.TrySeat(_world.MapData, p, out var seated) && Math.Abs(seated.Z - tile.Z) <= 8)
                return seated;
        }
        return tile;
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
        mind.Note(now, $"arrived at {goal.Name}");
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
        if (goal.Type == RoadGoalType)
        {
            // Back on the road: now a real trip can be planned.
            mind.BeginLinger(null, now, _rng, short_: true);
            mind.LingerUntilMs = now + _rng.Next(1000, 4000);
            return;
        }
        // A portal it is still this side of: its tiles are tried, then written off.
        if (mind.PortalTrip)
        {
            if (RetryPortal(mind, goal, now)) return;
            mind.BeginLinger(goal, now, _rng, short_: true);
            return;
        }
        mind.GateTries = 0;
        // Come to buy: find the vendor that sells what it needs.
        if (mind.ShoppingFor != null)
        {
            mind.BeginLinger(goal, now, _rng, short_: true);
            StartShopping(mind, now);
            return;
        }
        // A dungeon mouth with no portal is walked into: step onto its spot (the way
        // in), then live inside.
        if (!mind.Haunting && goal.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase) &&
            !IsUnderground(ch.Position))
        {
            mind.CrawlUntilMs = now + _rng.Next(600_000, 1_500_000);
            mind.BeginLinger(goal, now, _rng, short_: true);
            mind.LingerUntilMs = now + 2000;
            return;
        }
        mind.BeginLinger(goal, now, _rng, short_: IsUnderground(ch.Position));
    }

    // ------------------------------------------------------------------
    //  No road
    // ------------------------------------------------------------------

    internal const string RoadGoalType = "Road";
    public long StrandedRescues { get; private set; }
    public long WindowStrandedRescues { get; private set; }
    public long WindowNoRoute { get; private set; }

    /// <summary>A trip with no road to it. Off the roads altogether (a roof, a bridge,
    /// somewhere a gate put it), the bot first walks back to the nearest node it can
    /// find; a third failure, or five minutes of them, and it is lifted onto the road
    /// (onto its home town's bank in the end). On the roads but with none leading
    /// there, it simply chooses again.</summary>
    private void OnNoRoute(BotMind mind, BotDestination goal, int from, long now)
    {
        var ch = mind.Char;
        WindowNoRoute++;
        mind.NoRouteCount++;
        if (mind.NoRouteSinceMs == 0) mind.NoRouteSinceMs = now;
        int wide = Nav.FindNearest(ch.Position, 128, null, zBand: 40);
        bool giveUp = mind.NoRouteCount >= 3 || now - mind.NoRouteSinceMs > 300_000;
        mind.Note(now, $"no road to {goal.Name} (from {(from >= 0 ? Nav.Waypoints[from].Name : "nowhere")}, try {mind.NoRouteCount})");
        if (from < 0 && wide >= 0 && !giveUp && goal.Type != RoadGoalType)
        {
            // Walk back to the road first.
            var node = Nav.Waypoints[wide];
            mind.Goal = new BotDestination
            {
                Name = "road",
                Type = RoadGoalType,
                City = "",
                Pos = node.Pos,
                NearestWaypoint = wide,
            };
            ch.RemoveTag(GoalTag);
            mind.Route = [wide];
            mind.Leg = 0;
            mind.GoalSpot = -1;
            mind.PortalTrip = false;
            mind.FinalSpot = node.Pos;
            mind.ResetLeg(now);
            mind.NextActMs = now + 200;
            return;
        }
        if (giveUp || from < 0)
        {
            StrandedRescue(mind, now, mind.NoRouteCount >= 5 ? -1 : wide);
            return;
        }
        // A road, but none to there: choose again later.
        mind.Goal = null;
        ch.RemoveTag(GoalTag);
        mind.BeginLinger(null, now, _rng, short_: true);
    }

    private void StrandedRescue(BotMind mind, long now, int node)
    {
        var ch = mind.Char;
        // A caster with a rune home goes there the way a player would.
        if (TryRecallHome(mind, now, "no road from here"))
        {
            mind.NoRouteCount = 0;
            mind.NoRouteSinceMs = 0;
            return;
        }
        Point3D? spot = node >= 0 ? Nav.Waypoints[node].Pos : HomeSpot(mind);
        StrandedRescues++;
        WindowStrandedRescues++;
        mind.NoRouteCount = node >= 0 ? mind.NoRouteCount : 0;
        mind.NoRouteSinceMs = 0;
        _logger.LogInformation("[playerbots] {Name} has no road from {Pos}; lifted to {Spot}", ch.Name, ch.Position,
            spot?.ToString() ?? "nowhere");
        mind.Note(now, $"no road from {ch.Position}; lifted to {spot?.ToString() ?? "nowhere"}");
        if (spot is { } where) Teleport(ch, where);
        mind.Goal = null;
        mind.BeginLinger(null, now, _rng, short_: true);
        mind.LingerUntilMs = now + _rng.Next(1000, 4000);
    }

    /// <summary>A stopping spot in the bot's home town, its bank first.</summary>
    private Point3D? HomeSpot(BotMind mind)
    {
        string home = mind.Home;
        BotDestination? best = null;
        foreach (var d in Nav.Destinations)
        {
            if (d.City.Length == 0 || (home.Length > 0 && !d.City.Equals(home, StringComparison.OrdinalIgnoreCase))) continue;
            if (d.IsPortalType) continue;
            if (best == null || d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase)) best = d;
            if (d.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase)) break;
        }
        if (best == null && home.Length > 0)
            foreach (var d in Nav.Destinations)
                if (d.City.Length > 0 && !d.IsPortalType) { best = d; break; }
        if (best == null) return null;
        int spot = best.PickArrival(_rng);
        return spot >= 0 ? best.Arrivals[spot] : best.Pos;
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

    internal static bool IsUnderground(Point3D p) => p.Map == 0 && p.X >= 5120 && p.Y < 2304;

    // ------------------------------------------------------------------
    //  Choices
    // ------------------------------------------------------------------

    private static readonly string[] SpawnTypes = ["Bank", "Inn", "Tavern", "Healer", "Provisioner", "VendorProvisioner"];

    /// <summary>A place that can be gone to: not a portal that is not there, and with a
    /// stopping spot not known to be unreachable.</summary>
    private static bool IsUsable(BotDestination d) =>
        !IsDeadPortal(d) && (d.Arrivals.Count == 0 || d.BadArrivals.Count < d.Arrivals.Count);

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
            if (here < 0) here = Nav.FindNearest(mind.Char.Position, 128, null, zBand: 40);
            if (here >= 0) component = Nav.Waypoints[here].Component;
        }
        if (component >= 0)
            pool = pool.Where(d => Nav.ComponentOf(d) == component);

        if (spawnable)
        {
            // Born in town, where there is somewhere to go.
            pool = pool.Where(d => d.City.Length > 0 &&
                                   SpawnTypes.Any(t => d.Type.Equals(t, StringComparison.OrdinalIgnoreCase)) &&
                                   Nav.DestinationsOn(Nav.ComponentOf(d)) >= 4);
        }
        else if (mind != null)
        {
            var list0 = pool.Where(IsUsable).ToList();
            if (IsUnderground(mind.Char.Position))
            {
                // Inside: room to room while the run lasts, then the way out.
                string[] want = NowMs() < mind.CrawlUntilMs && mind.Char.Hits * 2 > mind.Char.MaxHits
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
            if (PickPkHaunt(mind, list0) is { } haunt)
            {
                mind.Haunting = true;
                return haunt;
            }
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
        var list = pool.Where(d => (mind == null || d != mind.At) && IsUsable(d)).ToList();
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
