using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Death the way a player lives it. A ghost with a road to a healer within reach walks
/// there - as a ghost, on the road, like any trip - and is raised by what the world
/// offers a ghost: the healer's own resurrection (a healer raises the ghost that comes
/// within three tiles of it) or an ankh used with a double click. Raised, it walks back
/// to its body. Only a ghost with no such road, one a healer turns away (a criminal, a
/// murderer), or one nobody has raised in time is carried off the old way - and then
/// out of the players' sight.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>How far (straight) a healer may be for a ghost to walk to it; the road
    /// there may be up to twice as long.</summary>
    public int GhostWalkRange { get; set; } = 150;
    /// <summary>How long a ghost waits at the healer to be raised before it is raised
    /// the old way.</summary>
    public int GhostWaitAtHealerMs { get; set; } = 25_000;

    /// <summary>One act of a ghost on its own way to be raised. False when there is no
    /// such way (or it was given up): the caller carries it off instead.</summary>
    private bool ActGhostWalk(BotMind mind, long now)
    {
        var m = mind.Manners;
        // A new death starts over.
        if (m.GhostDeathMs != mind.DeadSinceMs)
        {
            m.EndGhost();
            m.GhostDeathMs = mind.DeadSinceMs;
        }
        switch (m.Ghost)
        {
            case BotGhostStage.None:
                return !m.GhostWalkTried && StartGhostWalk(mind, now);
            case BotGhostStage.Walking:
                if (now > m.GhostUntilMs)
                {
                    GiveUpGhostWalk(mind, now, "the walk took too long");
                    return false;
                }
                if (mind.Mode == BotMode.Travel && mind.Goal != null)
                    ActTravel(mind, now);
                if (mind.Mode == BotMode.Travel && mind.Goal != null) return true;
                if (mind.Mode == BotMode.Linger && mind.At != null && IsHealerPlace(mind.At))
                {
                    // There. No walking on into the place: a ghost goes to the healer.
                    mind.SettleTo = null;
                    m.Ghost = BotGhostStage.AtHealer;
                    m.GhostUntilMs = now + GhostWaitAtHealerMs;
                    mind.NextActMs = now + _rng.Next(400, 900);
                    return true;
                }
                GiveUpGhostWalk(mind, now, "the road did not lead there");
                return false;
            default:
                return ActGhostAtHealer(mind, now);
        }
    }

    private static bool IsHealerPlace(BotDestination d) =>
        d.Type.Equals("Healer", StringComparison.OrdinalIgnoreCase);

    private bool StartGhostWalk(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        m.GhostWalkTried = true;
        // A healer turns a criminal and a murderer away.
        if (ch.IsMurderer || ch.IsFlaggedAsCriminal || Nav.Waypoints.Count == 0) return false;
        int from = Nav.FindNearest(ch.Position, 24);
        if (from < 0) from = Nav.FindNearest(ch.Position, 64);
        if (from < 0) return false;
        BotDestination? best = null;
        int bestLength = int.MaxValue;
        foreach (var d in Nav.Destinations)
        {
            if (!IsHealerPlace(d) || d.City.Length == 0 || d.Pos.Map != ch.MapIndex || !IsUsable(d)) continue;
            int straight = d.Pos.GetDistanceTo(ch.Position);
            if (straight > GhostWalkRange || straight >= bestLength || d.NearestWaypoint < 0) continue;
            var route = Nav.Route(from, d.NearestWaypoint);
            if (route == null) continue;
            int length = RoadLength(route, ch.Position, d.Pos);
            if (length > GhostWalkRange * 2 || length >= bestLength) continue;
            best = d;
            bestLength = length;
        }
        if (best == null) return false;
        // No goal tag: a ghost taken back from a save starts over where it stands.
        mind.ShoppingFor = null;
        mind.ShopVendor = null;
        mind.Goal = best;
        mind.Mode = BotMode.Travel;
        mind.Route = null;
        mind.BadStarts.Clear();
        m.Ghost = BotGhostStage.Walking;
        m.GhostUntilMs = now + 120_000 + bestLength * 1200L;
        WindowGhostWalks++;
        mind.Note(now, $"walks to {best.Name} as a ghost ({bestLength} tiles of road)");
        mind.NextActMs = now + _rng.Next(300, 900);
        return true;
    }

    private int RoadLength(List<int> route, Point3D from, Point3D to)
    {
        int length = 0;
        var at = from;
        foreach (int node in route)
        {
            var p = Nav.Waypoints[node].Pos;
            length += at.GetDistanceTo(p);
            at = p;
        }
        return length + at.GetDistanceTo(to);
    }

    private void GiveUpGhostWalk(BotMind mind, long now, string why)
    {
        var m = mind.Manners;
        m.Ghost = BotGhostStage.None;
        m.GhostWalkTried = true;
        mind.Goal = null;
        mind.Route = null;
        mind.Mode = BotMode.Linger;
        mind.ResetLeg(now);
        WindowGiveUps++;
        mind.Note(now, $"ghost walk given up: {why}");
    }

    /// <summary>At the healer's: an ankh is used, a healer stood by until it does its
    /// work (it raises a ghost within three tiles of it).</summary>
    private bool ActGhostAtHealer(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        if (now > m.GhostUntilMs)
        {
            GiveUpGhostWalk(mind, now, "nobody raised it at the healer's");
            return false;
        }
        Character? healer = null;
        int healerDist = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(ch.Position, 14))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || c.NpcBrain != NpcBrainType.Healer || c.MapIndex != ch.MapIndex) continue;
            int d = c.Position.GetDistanceTo(ch.Position);
            if (d < healerDist) { healerDist = d; healer = c; }
        }
        Item? ankh = null;
        if (mind.Client != null)
        {
            int ankhDist = healerDist;
            foreach (var item in _world.GetItemsInRange(ch.Position, 14))
            {
                if (item.IsDeleted || !item.IsOnGround || item.ItemType != ItemType.Shrine || item.MapIndex != ch.MapIndex) continue;
                int d = item.Position.GetDistanceTo(ch.Position);
                if (d < ankhDist) { ankhDist = d; ankh = item; }
            }
        }
        if (healer == null && ankh == null)
        {
            // Nothing here raises a ghost: no point in standing about for long.
            m.GhostUntilMs = Math.Min(m.GhostUntilMs, now + 3000);
            mind.NextActMs = now + 1000;
            return true;
        }
        var spot = ankh?.Position ?? healer!.Position;
        int dist = Chebyshev(ch.Position, spot);
        if (dist > 2)
        {
            bool stepped = StepToward(mind, spot, run: false, searchWithin: 16);
            if (mind.NoGainMs("healer", spot, dist, now) > 10_000)
                m.GhostUntilMs = Math.Min(m.GhostUntilMs, now);
            mind.NextActMs = now + StepDelay(mind, stepped, false, now);
            return true;
        }
        Face(mind, spot);
        if (ankh != null)
        {
            mind.Client!.HandleDoubleClick(ankh.Uid.Value);
            mind.NextActMs = now + 1500;
            return true;
        }
        if (!m.GhostPleaded)
        {
            m.GhostPleaded = true;
            Speak(mind, "ghost_plea", fallback: "ghost");
        }
        mind.NextActMs = now + _rng.Next(1000, 2000);
        return true;
    }

    /// <summary>The bot is alive again and had set out as a ghost. Raised at the
    /// healer's it walked to: it goes back for its body on foot, as it came. Raised on
    /// the way by somebody else: false, and the one who raised it is thanked as usual.</summary>
    private bool OnRaisedOnGhostWalk(BotMind mind, long now)
    {
        var m = mind.Manners;
        var stage = m.Ghost;
        var ch = mind.Char;
        ForgetDeath(ch);
        if (stage == BotGhostStage.None) return false;
        var at = mind.At;
        m.EndGhost();
        if (stage != BotGhostStage.AtHealer)
        {
            mind.Goal = null;
            mind.Route = null;
            return false;
        }
        mind.DeadSinceMs = 0;
        mind.GhostUntilMs = 0;
        if (ch.IsInWarMode) mind.Client?.HandleWarMode(false);
        WindowGhostRaises++;
        mind.Note(now, "raised at the healer's it walked to");
        _logger.LogInformation("[playerbots] {Name} walked to {Where} as a ghost and was raised at {Pos}", ch.Name,
            at?.Name ?? "a healer", ch.Position);
        if (_rng.Next(100) < 40) Speak(mind, "death_res");
        mind.BeginLinger(at ?? NearestPlace(ch.Position, 8), now, _rng, short_: true);
        // The body lies back along the road it came: walked back to, not handed over.
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (IsOwnCorpse(corpse, ch) && corpse!.MapIndex == ch.MapIndex &&
            corpse.Position.GetDistanceTo(ch.Position) <= ReclaimRange)
            StartCorpseTrip(mind, now);
        else if (!StartCorpseRun(mind, corpse, now))
            HandBack(mind, corpse);
        return true;
    }
}
