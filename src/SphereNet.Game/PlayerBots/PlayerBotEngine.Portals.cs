using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Portals: a dungeon's way in, the stairs between its levels, a moongate. All of them
/// work the same for a walker - a map teleporter ([TELEPORTERS]), a gate or a telepad
/// carries it on the moment it steps onto the tile, and only then. So a trip to a portal
/// ends ON its tile: the tiles are looked up in the running world (the road data and the
/// script pack need not agree to the tile), the last leg is never stopped short of, a
/// taken tile is swapped for a free one of the same portal, and a bot still on this side
/// at the end tries the other tiles before the spot is written off. Crossing is noticed
/// the moment the step carries the bot: the trip ends on the far side, a dungeon run is
/// started there, and the portals by the landing are stepped round for a while so it does
/// not walk straight back out.
/// </summary>
public sealed partial class PlayerBotEngine
{
    private bool _portalsResolved;

    public int PortalPlaces { get; private set; }
    public int PortallessPlaces { get; private set; }
    public long WindowPortalCrossings { get; private set; }
    public long WindowPortalMisses { get; private set; }

    /// <summary>Find each portal-type destination's portal tiles in the running world:
    /// map teleporters within three tiles of its stopping spots, and gates and telepads
    /// that lead somewhere. When the data names where the portal goes, only tiles that go
    /// there count. A place with none is portalless (a stair the script pack does not
    /// have, a gate that is not there); a dungeon mouth with none is simply walked into.
    /// Done once, before the first act.</summary>
    public void ResolvePortals()
    {
        _portalsResolved = true;
        int found = 0, missing = 0, walked = 0;
        var missingNames = new List<string>();
        foreach (var d in Nav.Destinations)
        {
            if (!d.IsPortalType) continue;
            var tiles = new List<Point3D>();
            var dests = new List<Point3D>();
            IEnumerable<Point3D> centers = d.Arrivals.Count > 0 ? d.Arrivals.Append(d.Pos) : [d.Pos];
            foreach (var c in centers)
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int x = c.X + dx, y = c.Y + dy;
                        if (x < 0 || y < 0) continue;
                        if (tiles.Exists(t => t.X == x && t.Y == y)) continue;
                        if (FindTeleport(new Point3D((short)x, (short)y, c.Z, c.Map)) is { } tp)
                        {
                            tiles.Add(new Point3D((short)x, (short)y, tp.Src.Z, c.Map));
                            dests.Add(tp.Dest);
                            continue;
                        }
                        foreach (var item in _world.GroundItemsOnTile(c.Map, x, y))
                        {
                            if (item.IsDeleted || !item.IsOnGround || item.X != x || item.Y != y) continue;
                            if (item.ItemType is not (ItemType.Moongate or ItemType.Telepad)) continue;
                            if (item.MoreP.X == 0 && item.MoreP.Y == 0) continue;
                            tiles.Add(item.Position);
                            dests.Add(item.MoreP);
                            break;
                        }
                    }
            // Where the data says the portal goes: tiles that go elsewhere belong to a
            // neighbour (two stairs side by side).
            if (d.Target is { } target && tiles.Count > 1)
            {
                var keep = new List<Point3D>();
                for (int i = 0; i < tiles.Count; i++)
                    if (dests[i].X != 0 && Math.Max(Math.Abs(dests[i].X - target.X), Math.Abs(dests[i].Y - target.Y)) <= 10)
                        keep.Add(tiles[i]);
                if (keep.Count > 0) tiles = keep;
            }
            d.PortalTiles = tiles;
            d.Portalless = tiles.Count == 0;
            // No portal, but the far side is on this same piece of road: a ramp or a
            // passage the road walks through.
            if (d.Portalless && d.Target is { } far)
            {
                int node = Nav.FindNearest(far, 8, null, zBand: 127);
                d.WalkedThrough = node >= 0 && Nav.Waypoints[node].Component == Nav.ComponentOf(d);
            }
            if (d.WalkedThrough) walked++;
            else if (d.Portalless)
            {
                missing++;
                if (missingNames.Count < 40) missingNames.Add(d.Name);
            }
            else found++;
        }
        PortalPlaces = found;
        PortallessPlaces = missing;
        if (found + missing + walked > 0)
            _logger.LogInformation(
                "[playerbots] portals: {Found} places with a portal in this world, {Walked} walked through, {Missing} without ({Names})",
                found, walked, missing, string.Join(", ", missingNames));
    }

    /// <summary>The map teleporter at a tile, whatever floor height the data recorded
    /// for it (the lookup itself wants the height within five).</summary>
    private World.GameWorld.MapTeleport? FindTeleport(Point3D p)
    {
        foreach (int dz in TeleportZProbe)
        {
            int z = p.Z + dz;
            if (z < sbyte.MinValue || z > sbyte.MaxValue) continue;
            if (_world.GetMapTeleport(new Point3D(p.X, p.Y, (sbyte)z, p.Map)) is { } tp) return tp;
        }
        return null;
    }

    // The data's heights can be a floor off (a teleporter at Z 0 recorded at Z 40).
    private static readonly int[] TeleportZProbe = [0, -8, 8, -16, 16, -24, 24, -32, 32, -40, 40, -48, 48];

    /// <summary>A place a trip is never chosen for: a stair between dungeon levels that
    /// this world does not have (stepping there leads nowhere). A dungeon mouth or a
    /// moongate with no portal is still a place: walked into, or waited about (a world
    /// without gate items still has its moongate squares).</summary>
    internal static bool IsDeadPortal(BotDestination d) =>
        d.Portalless && !d.WalkedThrough &&
        (d.Type.Equals("DungeonDescend", StringComparison.OrdinalIgnoreCase) ||
         d.Type.Equals("DungeonAscend", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this trip ends by stepping through the goal's portal.</summary>
    private static bool CrossesPortal(BotMind mind, BotDestination goal) =>
        !mind.Haunting && goal.IsPortalType && goal.PortalTiles is { Count: > 0 };

    /// <summary>The goal's portal tile nearest a point.</summary>
    private static Point3D NearestPortalTile(BotDestination goal, Point3D near)
    {
        var tiles = goal.PortalTiles!;
        var best = tiles[0];
        int bestD = int.MaxValue;
        foreach (var t in tiles)
        {
            int d = t.GetDistanceTo(near);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>Another tile of the same portal nobody stands on, or null.</summary>
    private Point3D? FreePortalTile(BotDestination goal, Point3D taken, Character self)
    {
        Point3D? best = null;
        int bestD = int.MaxValue;
        foreach (var t in goal.PortalTiles ?? [])
        {
            if (SameTile(t, taken) || SomeoneOn(t, self)) continue;
            int d = t.GetDistanceTo(self.Position);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>The bot's own step carried it off (a teleporter, a gate). A trip to that
    /// portal is over; any other trip is planned again from where it landed.</summary>
    private void OnCarried(BotMind mind, long now)
    {
        var ch = mind.Char;
        StepRoundPortalsAround(mind, ch.Position, now);
        var goal = mind.Goal;
        if (mind.Mode == BotMode.Travel && goal != null && goal.IsPortalType && !mind.Haunting)
        {
            OnPortalCrossed(mind, goal, now);
            return;
        }
        mind.Note(now, $"carried to {ch.Position} by a portal on the way");
        if (mind.Mode == BotMode.Travel && !ch.IsDead)
            mind.BeginLinger(NearestPlace(ch.Position, 6), now, _rng, short_: true);
        mind.ResetLeg(now);
    }

    private void OnPortalCrossed(BotMind mind, BotDestination goal, long now)
    {
        var ch = mind.Char;
        WindowPortalCrossings++;
        var at = NearestPlace(ch.Position, 6);
        mind.Note(now, $"through {goal.Name} to {ch.Position}{(at != null ? " (" + at.Name + ")" : "")}");
        // A run into a dungeon starts when it is inside, not before.
        if (goal.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase) && IsUnderground(ch.Position))
            mind.CrawlUntilMs = now + _rng.Next(600_000, 1_500_000);
        mind.BeginLinger(at, now, _rng, short_: true);
        // A look round, then on into the place.
        mind.LingerUntilMs = now + _rng.Next(2000, 8000);
        mind.ResetLeg(now);
    }

    /// <summary>The portals by a landing spot (the way back is usually a tile away) are
    /// stepped round for a while.</summary>
    private void StepRoundPortalsAround(BotMind mind, Point3D at, long now)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = at.X + dx, y = at.Y + dy;
                if (x < 0 || y < 0) continue;
                var p = new Point3D((short)x, (short)y, at.Z, at.Map);
                if (FindTeleport(p) != null)
                {
                    mind.RememberPortal(x, y, at.Map, now);
                    continue;
                }
                foreach (var item in _world.GroundItemsOnTile(at.Map, x, y))
                    if (!item.IsDeleted && item.X == x && item.Y == y &&
                        item.ItemType is ItemType.Moongate or ItemType.Telepad)
                    {
                        mind.RememberPortal(x, y, at.Map, now);
                        break;
                    }
            }
    }

    /// <summary>The destination whose spot or point is within <paramref name="range"/>
    /// of a point, nearest first; null when none.</summary>
    private BotDestination? NearestPlace(Point3D p, int range)
    {
        BotDestination? best = null;
        int bestD = range + 1;
        foreach (var d in Nav.Destinations)
        {
            if (d.Pos.Map != p.Map) continue;
            int dd = d.Pos.GetDistanceTo(p);
            foreach (var a in d.Arrivals) dd = Math.Min(dd, a.GetDistanceTo(p));
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    /// <summary>Arrived at a portal place and still on this side: step onto another of
    /// its tiles (or off and back onto the only one); a few tries, then the spot is
    /// written off like any spot that cannot be reached.</summary>
    private bool RetryPortal(BotMind mind, BotDestination goal, long now)
    {
        var ch = mind.Char;
        var tiles = goal.PortalTiles!;
        if (mind.GateTries < Math.Min(3, tiles.Count + 1))
        {
            mind.GateTries++;
            var next = FreePortalTile(goal, ch.Position, ch);
            if (next == null)
            {
                // The only tile, and it stands on it: off and back on.
                for (int k = 0; k < 8; k++)
                    if (Move(mind, (Direction)_rng.Next(8), run: false)) break;
                next = NearestPortalTile(goal, ch.Position);
            }
            mind.FinalSpot = next.Value;
            mind.PortalTrip = true;
            mind.ResetLeg(now);
            mind.NextActMs = now + 300;
            mind.Note(now, $"portal {goal.Name} did not carry it; tries {next.Value}");
            return true;
        }
        WindowPortalMisses++;
        mind.Note(now, $"portal {goal.Name} never carried it; spot written off");
        _logger.LogInformation("[playerbots] {Name} could not pass {Portal} at {Pos}", ch.Name, goal.Name, ch.Position);
        if (mind.GoalSpot >= 0) LearnBadSpot(goal, mind.GoalSpot);
        mind.GateTries = 0;
        return false;
    }
}
