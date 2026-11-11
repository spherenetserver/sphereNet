using System.Globalization;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// The parts of a death that have to hold together across a restart and across a
/// dungeon's portals.
/// <list type="bullet">
/// <item><b>Still a ghost after a restart.</b> A death is noted on the character
/// (<c>PLAYERBOT.DEAD</c>: the corpse, when it died, how long it hovers, the last spot
/// of open ground it stood on); a ghost taken back from the save goes on from there
/// instead of dying a second time.</item>
/// <item><b>Raised by its own dungeon.</b> A dungeon's map coordinates say nothing
/// about where it is: a ghost carried off is raised at the healer nearest by road to
/// the spot of open ground it last stood on (the dungeon's mouth), not the one nearest
/// by raw x/y.</item>
/// <item><b>Back for the body through the portals.</b> A body on another piece of road
/// (a dungeon level behind a mouth and its stairs) is walked back to portal by portal,
/// at a run, while the way is short enough to make before the body rots; only a body
/// too far, or with no such way, is handed back.</item>
/// </list>
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary><c>corpse|unix second of death|ghost ms|x,y,map</c> of the last open
    /// ground stood on.</summary>
    public const string DeadTag = "PLAYERBOT.DEAD";

    /// <summary>A death noted longer ago than this is not gone on from.</summary>
    private const long StaleDeathMs = 1_800_000;
    /// <summary>Road a raised bot will cover, portals included, to get back to its body
    /// (at a run, before the body rots).</summary>
    public int CorpseRunRange { get; set; } = 600;
    /// <summary>How long a way back through portals may take before what is left on the
    /// body is handed back.</summary>
    private const long CorpseRunMs = 420_000;
    private const int CorpseRunMaxHops = 6;

    public long WindowCorpseRuns { get; private set; }
    public long WindowDeathsResumed { get; private set; }

    // ------------------------------------------------------------------
    //  The death, kept
    // ------------------------------------------------------------------

    /// <summary>A fresh death: noted on the character so a restart does not start it over.</summary>
    private void NoteDeath(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        m.DeathNoted = true;
        var ground = DeathSurface(mind);
        ch.SetTag(DeadTag, string.Create(CultureInfo.InvariantCulture,
            $"{mind.CorpseUid.Value:x8}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Math.Max(0, mind.GhostUntilMs - now)}|{ground.X},{ground.Y},{ground.Map}"));
    }

    private static void ForgetDeath(Character ch) => ch.RemoveTag(DeadTag);

    /// <summary>A ghost that was already dead when this mind took it up (a restart, a
    /// bot logged out and back): its death goes on from where it had got to - the same
    /// corpse, what is left of its hovering, no second death in the news or the count.
    /// False for a death that happened now.</summary>
    private bool ResumeDeath(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        if (!ch.TryGetTag(DeadTag, out string? tag) || string.IsNullOrEmpty(tag)) return false;
        var parts = tag.Split('|');
        // A note this very mind wrote belongs to an earlier death of this session.
        if (m.DeathNoted || parts.Length < 3 ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint corpseUid) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long diedAt) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ghostMs))
        {
            ForgetDeath(ch);
            return false;
        }
        long elapsed = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - diedAt) * 1000;
        // Another body of its own right here: it has died again since.
        var here = FindOwnCorpse(ch);
        if (elapsed < 0 || elapsed > StaleDeathMs || (here != null && here.Uid.Value != corpseUid))
        {
            ForgetDeath(ch);
            return false;
        }
        m.DeathNoted = true;
        mind.DeadSinceMs = Math.Max(1, now - elapsed);
        mind.GhostUntilMs = now + Math.Max(0, ghostMs - elapsed);
        mind.EndFightState();
        mind.Goal = null;
        mind.Route = null;
        ch.RemoveTag(GoalTag);
        var corpse = _world.FindItem(new Serial(corpseUid));
        mind.CorpseUid = IsOwnCorpse(corpse, ch) ? corpse!.Uid : SavedCorpse(ch);
        if (parts.Length >= 4)
        {
            var xy = parts[3].Split(',');
            if (xy.Length == 3 && short.TryParse(xy[0], out short x) && short.TryParse(xy[1], out short y) &&
                byte.TryParse(xy[2], out byte map) && (x != 0 || y != 0))
                m.LastSurface = new Point3D(x, y, 0, map);
        }
        mind.LingerAnchor = ch.Position;
        WindowDeathsResumed++;
        _logger.LogInformation("[playerbots] {Name} is still a ghost at {Pos} (dead {Seconds}s); corpse {Corpse}", ch.Name,
            ch.Position, elapsed / 1000, mind.CorpseUid.IsValid ? mind.CorpseUid.ToString() : "not found");
        mind.Note(now, $"still a ghost (dead {elapsed / 1000}s)");
        mind.NextActMs = now + _rng.Next(1000, 2000);
        return true;
    }

    // ------------------------------------------------------------------
    //  Where a ghost belongs
    // ------------------------------------------------------------------

    /// <summary>The open ground a dead bot belongs to. Above ground, where it stands.
    /// Underground, the last open ground it stood on (the mouth it went in by); not
    /// known (a ghost from an older save), the mouth whose far side lies nearest.</summary>
    private Point3D DeathSurface(BotMind mind)
    {
        var here = mind.Char.Position;
        if (!IsUnderground(here)) return here;
        var last = mind.Manners.LastSurface;
        if ((last.X != 0 || last.Y != 0) && last.Map == here.Map) return last;
        BotDestination? mouth = null;
        int best = 1000;
        foreach (var d in Nav.Destinations)
        {
            if (d.Target is not { } far || far.Map != here.Map || d.Pos.Map != here.Map || IsUnderground(d.Pos) ||
                !d.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase))
                continue;
            int dist = far.GetDistanceTo(here);
            if (dist < best) { best = dist; mouth = d; }
        }
        return mouth?.Pos ?? here;
    }

    /// <summary>The healer a ghost is carried to: the one with the shortest road from
    /// the open ground the ghost belongs to; with no road from there, the nearest to
    /// that ground. Never judged by a dungeon's own coordinates.</summary>
    private BotDestination? HealerFor(Character ch)
    {
        _minds.TryGetValue(ch.Uid.Value, out var mind);
        // Asked again while a lift waits for nobody to look: the answer of this death.
        if (mind is { DeadSinceMs: not 0 } && mind.Manners.LiftHealer is { } known &&
            mind.Manners.LiftHealerDeathMs == mind.DeadSinceMs)
            return known;
        var healer = FindHealerFor(mind, ch);
        if (mind != null)
        {
            mind.Manners.LiftHealer = healer;
            mind.Manners.LiftHealerDeathMs = mind.DeadSinceMs;
        }
        return healer;
    }

    private BotDestination? FindHealerFor(BotMind? mind, Character ch)
    {
        var from = mind != null ? DeathSurface(mind) : ch.Position;
        int node = Nav.Waypoints.Count > 0 ? Nav.FindNearest(from, 64) : -1;
        BotDestination? best = null, nearest = null;
        int bestRoad = int.MaxValue, nearestDist = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (!IsHealerPlace(d) || d.City.Length == 0) continue;
            int dist = d.Pos.Map == from.Map ? d.Pos.GetDistanceTo(from) : int.MaxValue - 1;
            if (dist < nearestDist) { nearestDist = dist; nearest = d; }
            if (node < 0 || d.NearestWaypoint < 0 || d.Pos.Map != from.Map || dist >= bestRoad) continue;
            if (Nav.Waypoints[d.NearestWaypoint].Component != Nav.Waypoints[node].Component) continue;
            var route = Nav.Route(node, d.NearestWaypoint);
            if (route == null) continue;
            int road = RoadLength(route, from, d.Pos);
            if (road < bestRoad) { bestRoad = road; best = d; }
        }
        return best ?? nearest;
    }

    // ------------------------------------------------------------------
    //  Back for the body
    // ------------------------------------------------------------------

    private readonly Dictionary<BotDestination, int> _portalLandsOn = [];

    private int ComponentAt(Point3D p)
    {
        int node = Nav.FindNearest(p, 24);
        if (node < 0) node = Nav.FindNearest(p, 64);
        return node >= 0 ? Nav.Waypoints[node].Component : -1;
    }

    /// <summary>The piece of road a portal place sets a walker down on, or -1.</summary>
    private int LandsOn(BotDestination d)
    {
        if (_portalLandsOn.TryGetValue(d, out int component)) return component;
        component = d.Target is { } far ? ComponentAt(far) : -1;
        _portalLandsOn[d] = component;
        return component;
    }

    /// <summary>The portal place to take first from one piece of road to get to
    /// another, by the fewest portals (at most four); the nearest of them when several
    /// are as good. Null when no chain of portals leads there.</summary>
    private BotDestination? PortalToward(int from, int to, Point3D here, out int hops)
    {
        hops = 0;
        if (from < 0 || to < 0 || from == to) return null;
        var portals = new List<BotDestination>();
        foreach (var d in Nav.Destinations)
            if (d.IsPortalType && !d.Portalless && !d.WalkedThrough && IsUsable(d) && LandsOn(d) >= 0 &&
                LandsOn(d) != Nav.ComponentOf(d))
                portals.Add(d);
        // Breadth first over the pieces of road, remembering the first portal taken.
        var seen = new HashSet<int> { from };
        var frontier = new List<(int Component, BotDestination First)>();
        BotDestination? best = null;
        int bestDist = int.MaxValue;
        foreach (var d in portals)
        {
            if (Nav.ComponentOf(d) != from) continue;
            if (LandsOn(d) == to)
            {
                int dist = d.Pos.GetDistanceTo(here);
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
            else
                frontier.Add((LandsOn(d), d));
        }
        hops = 1;
        if (best != null) return best;
        foreach (var f in frontier) seen.Add(f.Component);
        for (hops = 2; hops <= 4 && frontier.Count > 0; hops++)
        {
            var next = new List<(int Component, BotDestination First)>();
            foreach (var (component, first) in frontier)
                foreach (var d in portals)
                {
                    if (Nav.ComponentOf(d) != component) continue;
                    if (LandsOn(d) == to) return first;
                    if (seen.Add(LandsOn(d))) next.Add((LandsOn(d), first));
                }
            frontier = next;
        }
        return null;
    }

    /// <summary>A raised bot whose body is not just round the corner: walk back for it
    /// when a way leads there - the same piece of road, or through a chain of portals -
    /// and it is short enough to cover before the body rots. False when there is no
    /// such way: the caller hands back what is on the body.</summary>
    private bool StartCorpseRun(BotMind mind, Item? corpse, long now)
    {
        var ch = mind.Char;
        if (mind.Client == null || !IsOwnCorpse(corpse, ch) || corpse!.MapIndex != ch.MapIndex || Nav.Waypoints.Count == 0)
            return false;
        int mine = ComponentAt(ch.Position), its = ComponentAt(corpse.Position);
        if (mine < 0 || its < 0) return false;
        var m = mind.Manners;
        if (mine == its)
        {
            if (corpse.Position.GetDistanceTo(ch.Position) > CorpseRunRange) return false;
            m.CorpseRunUntilMs = 0;
            StartCorpseTrip(mind, now);
            WindowCorpseRuns++;
            return true;
        }
        if (PortalToward(mine, its, ch.Position, out int hops) is not { } portal) return false;
        // The road to the first portal, and a guess for what lies behind each one.
        if (portal.Pos.GetDistanceTo(ch.Position) + hops * 80 > CorpseRunRange) return false;
        m.CorpseRunUntilMs = now + CorpseRunMs;
        m.CorpseRunHops = 0;
        DropResRobe(ch);
        WindowCorpseRuns++;
        TakePortal(mind, portal, now, hops);
        return true;
    }

    private void TakePortal(BotMind mind, BotDestination portal, long now, int hops)
    {
        mind.Manners.CorpseRunHops++;
        mind.Goal = portal;
        mind.Char.SetTag(GoalTag, portal.Name);
        mind.Mode = BotMode.Travel;
        mind.Route = null;
        mind.Haunting = false;
        mind.NextActMs = now + _rng.Next(600, 1500);
        mind.Note(now, $"back for its body through {portal.Name} ({hops} portal{(hops == 1 ? "" : "s")} away)");
    }

    /// <summary>A bot on its way back to a body behind portals has stopped (through a
    /// portal, or short of one): the next portal, the body itself once it is on the
    /// same piece of road, or - out of time or of ways - what is left handed back.
    /// True when the stay is not to begin.</summary>
    private bool ResumeCorpseRun(BotMind mind, long now)
    {
        var m = mind.Manners;
        if (m.CorpseRunUntilMs == 0) return false;
        var ch = mind.Char;
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (!IsOwnCorpse(corpse, ch))
        {
            // Rotted or emptied away meanwhile.
            m.CorpseRunUntilMs = 0;
            mind.CorpseUid = Serial.Invalid;
            ch.RemoveTag(CorpseTag);
            mind.Note(now, "its body is gone");
            return false;
        }
        int mine = ComponentAt(ch.Position), its = ComponentAt(corpse!.Position);
        if (mine >= 0 && mine == its)
        {
            m.CorpseRunUntilMs = 0;
            // Here for the body, not for a hunt.
            mind.CrawlUntilMs = 0;
            StartCorpseTrip(mind, now);
            return true;
        }
        if (now <= m.CorpseRunUntilMs && m.CorpseRunHops < CorpseRunMaxHops &&
            PortalToward(mine, its, ch.Position, out int hops) is { } portal)
        {
            TakePortal(mind, portal, now, hops);
            return true;
        }
        m.CorpseRunUntilMs = 0;
        WindowGiveUps++;
        mind.Note(now, "no way back to its body in time");
        HandBack(mind, corpse);
        return false;
    }

    /// <summary>Whether the bot is on its way back to its body (it runs).</summary>
    private static bool OnCorpseRun(BotMind mind) =>
        mind.Manners.CorpseRunUntilMs != 0 || mind.Goal?.Type == CorpseGoalType;
}
