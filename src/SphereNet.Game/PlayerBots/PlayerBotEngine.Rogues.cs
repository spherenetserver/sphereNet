using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Rogues. Some bots are player killers and some are thieves (PLAYERBOT.ROLE,
/// decided once per bot by <see cref="PkPercent"/> and <see cref="ThiefPercent"/>).
/// A player killer has a haunt of its own (PLAYERBOT.HAUNT: a dungeon mouth, a
/// moongate, a shrine, a landmark out of the guards' reach) and roams the road nodes
/// about it; it never steps into a guarded region. Every blue player or bot it sees
/// there it sets on, then goes through the body. Killed, it is raised at its haunt
/// after a few seconds with a new kit, and what it died with stays on its corpse for
/// whoever killed it. A thief works the town crowd: steps up to someone, opens
/// their pack and steals from it with its skill, then hides and walks off. Every
/// blow, snoop and theft goes through the bot's client, so the crime rules - the
/// murderer and criminal flags, the guards, the murderer's resurrection town - apply
/// exactly as to a player.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string RoleTag = "PLAYERBOT.ROLE";
    public const string HauntTag = "PLAYERBOT.HAUNT";

    /// <summary>How far about its haunt a player killer roams.</summary>
    public int PkRoam { get; set; } = 40;
    /// <summary>How far a player killer sees a victim.</summary>
    public int PkSight { get; set; } = 14;

    /// <summary>The kinds of place a player killer haunts: where people pass, out of town.</summary>
    private static readonly string[] PkHauntTypes =
        ["DungeonEntrance", "Moongate", "Shrine", "Landmark", "AdventureArea", "Graveyard"];

    // Source-X NOTO_GOOD: blue to the one looking.
    private const byte NotoGood = 1;

    public long WindowPkRespawns { get; private set; }
    public long WindowPkLifts { get; private set; }

    /// <summary>Percent of fighter bots that are player killers.</summary>
    public int PkPercent { get; set; }
    /// <summary>Percent of the other bots that are thieves.</summary>
    public int ThiefPercent { get; set; }

    public long WindowPkAttacks { get; private set; }
    public long WindowThefts { get; private set; }

    private string RoleOf(BotMind mind)
    {
        if (mind.Role != null) return mind.Role;
        var ch = mind.Char;
        if (ch.TryGetTag(RoleTag, out string? role) && role != null) return mind.Role = role;
        role = mind.IsFighter && _rng.Next(100) < PkPercent ? "pk"
            : _rng.Next(100) < ThiefPercent ? "thief"
            : "-";
        if (role == "thief")
        {
            // The trade's own skills, as a thief would have trained them.
            ch.SetSkill(SkillType.Stealing, (ushort)_rng.Next(700, 1001));
            ch.SetSkill(SkillType.Snooping, (ushort)_rng.Next(700, 1001));
            ch.SetSkill(SkillType.Hiding, (ushort)_rng.Next(600, 1001));
        }
        ch.SetTag(RoleTag, role);
        return mind.Role = role;
    }

    private bool IsPk(BotMind mind) => RoleOf(mind) == "pk";
    private bool IsThief(BotMind mind) => RoleOf(mind) == "thief";

    private bool IsRogueBot(Character c) => _minds.TryGetValue(c.Uid.Value, out var m) && RoleOf(m) != "-";

    private bool IsGuardedAt(Point3D p) => _world.FindRegion(p)?.IsGuarded == true;

    private bool IsPkHaunt(BotDestination d) =>
        d.City.Length == 0 && PkHauntTypes.Contains(d.Type) && Nav.HasMap(d.Pos.Map) && IsUsable(d) &&
        !IsGuardedAt(d.Pos);

    /// <summary>A player killer's haunt: the one it was given, else one is picked (on
    /// the map it stands on when there is one) and kept in PLAYERBOT.HAUNT. Null for
    /// any other bot, or when the roads hold no place out of the guards' reach.</summary>
    private BotDestination? HauntOf(BotMind mind)
    {
        if (!IsPk(mind)) return null;
        if (mind.Haunt != null) return mind.Haunt;
        var ch = mind.Char;
        if (ch.TryGetTag(HauntTag, out string? name) && name != null &&
            Nav.FindDestination(name) is { } kept && IsPkHaunt(kept))
            return mind.Haunt = kept;
        var pool = Nav.Destinations.Where(IsPkHaunt).ToList();
        if (pool.Count == 0) return null;
        var here = pool.Where(d => d.Pos.Map == ch.MapIndex).ToList();
        if (here.Count > 0) pool = here;
        mind.Haunt = pool[_rng.Next(pool.Count)];
        ch.SetTag(HauntTag, mind.Haunt.Name);
        return mind.Haunt;
    }

    private readonly Dictionary<string, List<Point3D>> _roamPoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a player killer walks about its haunt: the haunt's own spots and
    /// the road nodes within <see cref="PkRoam"/> of it, none of them in a guarded
    /// region. Road nodes are known to be reachable, a random tile is not.</summary>
    private List<Point3D> RoamPoints(BotDestination haunt)
    {
        if (_roamPoints.TryGetValue(haunt.Name, out var points)) return points;
        points = [];
        foreach (var p in haunt.Arrivals.Append(haunt.Pos))
            if (!IsGuardedAt(p)) points.Add(p);
        foreach (var w in Nav.Waypoints)
            if (w.Pos.Map == haunt.Pos.Map && w.Pos.GetDistanceTo(haunt.Pos) <= PkRoam && !IsGuardedAt(w.Pos) &&
                FindTeleport(w.Pos) == null)
                points.Add(w.Pos);
        return _roamPoints[haunt.Name] = points;
    }

    private Point3D? RoamPoint(BotDestination haunt)
    {
        var points = RoamPoints(haunt);
        return points.Count > 0 ? points[_rng.Next(points.Count)] : null;
    }

    /// <summary>A player killer's linger: roam from road node to road node about its
    /// haunt, stand a while, look about often (every act scans for a victim). One that
    /// finds itself far from its haunt (a saved bot, a long flight) or in a guarded
    /// region (a bot just made or adopted in town) is lifted back, as a stuck bot is.
    /// False for any other bot.</summary>
    private bool ActPkLinger(BotMind mind, long now)
    {
        if (HauntOf(mind) is not { } haunt) return false;
        var ch = mind.Char;
        int far = ch.MapIndex == haunt.Pos.Map ? ch.Position.GetDistanceTo(haunt.Pos) : int.MaxValue;
        if (far > PkRoam * 3 || IsGuardedAt(ch.Position))
        {
            LiftToHaunt(mind, haunt, now, far > PkRoam * 3 ? "far from its haunt" : "in a guarded region");
            return true;
        }
        mind.At = haunt;
        if (mind.PatrolTo is { } to)
        {
            int dist = ch.Position.GetDistanceTo(to);
            if (dist > 1 && mind.NoGainMs("roam", to, dist, now) < 20_000)
            {
                bool walked = StepToward(mind, to, run: false, searchWithin: PkRoam * 2);
                mind.NextActMs = now + StepDelay(mind, walked, false, now);
                return true;
            }
            mind.PatrolTo = null;
            mind.LingerAnchor = ch.Position;
            mind.LingerUntilMs = now + _rng.Next(20_000, 60_000);
        }
        if (now >= mind.LingerUntilMs)
        {
            mind.PatrolTo = RoamPoint(haunt);
            mind.LingerUntilMs = now + 60_000;
            mind.NextActMs = now + 200;
            return true;
        }
        if (_rng.Next(100) < 30) WanderNear(mind, mind.LingerAnchor, 3, run: false);
        mind.NextActMs = now + _rng.Next(1000, 2000);
        return true;
    }

    /// <summary>Set a player killer down at its haunt, out of the guards' reach.</summary>
    private void LiftToHaunt(BotMind mind, BotDestination haunt, long now, string why)
    {
        var ch = mind.Char;
        var spot = RoamPoint(haunt);
        WindowPkLifts++;
        _logger.LogInformation("[playerbots] {Name} (pk) {Why}; set down at {Haunt} {Spot}", ch.Name, why, haunt.Name,
            spot?.ToString() ?? "nowhere");
        mind.Note(now, $"{why}; set down at {haunt.Name}");
        if (spot is { } where) Teleport(ch, where);
        mind.BeginLinger(haunt, now, _rng, short_: true);
        mind.PatrolTo = null;
        mind.NextActMs = now + 1000;
    }

    /// <summary>A dead player killer is raised at its haunt with a new kit of its
    /// archetype; the corpse keeps what it died with. False for any other bot.</summary>
    private bool RaiseAtHaunt(BotMind mind, long now)
    {
        if (HauntOf(mind) is not { } haunt) return false;
        var ch = mind.Char;
        if (RoamPoint(haunt) is { } spot) Teleport(ch, spot);
        if (mind.Client != null) mind.Client.OnResurrect();
        else ch.Resurrect();
        if (ch.IsDead)
        {
            mind.NextActMs = now + 10_000;
            return true;
        }
        mind.DeadSinceMs = 0;
        mind.CorpseUid = Serial.Invalid;
        ch.RemoveTag(CorpseTag);
        DropResRobe(ch);
        ArmFromKit(ch);
        WindowPkRespawns++;
        _logger.LogInformation("[playerbots] {Name} (pk) raised at {Haunt} {Pos} with a new kit", ch.Name, haunt.Name,
            ch.Position);
        mind.Note(now, $"raised at its haunt {haunt.Name} with a new kit");
        mind.BeginLinger(haunt, now, _rng, short_: true);
        mind.PatrolTo = null;
        return true;
    }

    /// <summary>A player killer out of a guarded region sets on the nearest blue player
    /// or bot it can see (innocent to it, Source-X NOTO_GOOD), standing out of the
    /// guards' reach too: not a staff member, not another player killer, not in its
    /// party, not one it gave up on.</summary>
    private Character? PickVictim(BotMind mind, long now, bool guarded)
    {
        if (guarded || now < mind.NextRogueMs || !IsPk(mind) || mind.Client == null) return null;
        var ch = mind.Char;
        var party = Parties?.FindParty(ch.Uid);
        Character? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(ch.Position, PkSight))
        {
            if (c == ch || c.IsDeleted || c.IsDead || !c.IsPlayer || c.MapIndex != ch.MapIndex) continue;
            if (c.IsInvisible || c.IsStatFlag(StatFlag.Hidden) || c.PrivLevel >= PrivLevel.Counsel) continue;
            if (party != null && party.IsMember(c.Uid)) continue;
            if (_minds.TryGetValue(c.Uid.Value, out var other) && IsPk(other)) continue;
            int d = ch.Position.GetDistanceTo(c.Position);
            if (d >= bestDist || mind.Avoids(c, now) || IsGuardedAt(c.Position)) continue;
            if (Clients.GameClient.ComputeNotoriety(_world, ch, c) != NotoGood) continue;
            if (!_world.CanSeeLOS(ch.Position, c.Position)) continue;
            best = c;
            bestDist = d;
        }
        if (best == null) return null;
        mind.NextRogueMs = now + 3000;
        WindowPkAttacks++;
        return best;
    }

    // ------------------------------------------------------------------
    //  Thieving
    // ------------------------------------------------------------------

    /// <summary>One act of a thief at work in town; false when it is not working.
    /// Steps: walk up to the mark, open their pack (a snoop), steal something from
    /// it with the skill and its target cursor, then hide and leave.</summary>
    private bool ActThief(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        if (client == null || !IsThief(mind)) return false;
        if (mind.Mark == null)
        {
            if (now < mind.NextRogueMs || _world.FindRegion(ch.Position)?.IsGuarded != true) return false;
            mind.NextRogueMs = now + 20_000;
            var party = Parties?.FindParty(ch.Uid);
            var marks = _world.GetCharsInRange(ch.Position, 6)
                .Where(c => c != ch && !c.IsDeleted && !c.IsDead && c.IsPlayer && c.Backpack != null &&
                            c.PrivLevel < PrivLevel.Counsel && !IsRogueBot(c) &&
                            (party == null || !party.IsMember(c.Uid)))
                .ToList();
            if (marks.Count == 0 || _rng.Next(100) >= 40) return false;
            mind.Mark = marks[_rng.Next(marks.Count)];
            mind.RogueStage = 0;
        }
        var mark = mind.Mark;
        if (mark.IsDeleted || mark.IsDead || mark.MapIndex != ch.MapIndex || ch.Position.GetDistanceTo(mark.Position) > 12)
        {
            EndTheft(mind, now);
            return true;
        }
        if (ch.Position.GetDistanceTo(mark.Position) > 1)
        {
            bool walked = StepToward(mind, mark.Position, run: false, searchWithin: 14);
            if (mind.NoGainMs("mark", mark.Position, ch.Position.GetDistanceTo(mark.Position), now) > 10_000)
            {
                WindowGiveUps++;
                mind.Note(now, $"cannot get to {mark.GetName()}; lets the mark go");
                EndTheft(mind, now);
                return true;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return true;
        }
        var pack = mark.Backpack;
        switch (mind.RogueStage)
        {
            case 0:
                // A look inside first, as a thief does: the snoop.
                if (pack != null) client.HandleDoubleClick(pack.Uid.Value);
                mind.RogueStage = 1;
                mind.NextActMs = now + _rng.Next(1200, 2500);
                return true;
            case 1:
                var loot = pack == null ? null : PickPocket(pack);
                if (loot != null)
                {
                    client.HandleUseSkill((int)SkillType.Stealing);
                    if (client.HasPendingTarget)
                        client.HandleTargetResponse(0, client.ActiveTargetCursorId, loot.Uid.Value,
                            loot.X, loot.Y, loot.Z, 0);
                    WindowThefts++;
                }
                mind.RogueStage = 2;
                mind.NextActMs = now + _rng.Next(800, 1600);
                return true;
            default:
                client.HandleUseSkill((int)SkillType.Hiding);
                if (_rng.Next(100) < 30) Speak(mind, "thief_brag", fallback: null);
                EndTheft(mind, now);
                // And away from where it happened.
                mind.LingerUntilMs = 0;
                return true;
        }
    }

    /// <summary>What a thief reaches for: gold first, else something light.</summary>
    private Item? PickPocket(Item pack)
    {
        Item? light = null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            if (item.IsDeleted || item.IsAttr(ObjAttributes.Newbie)) continue;
            if (item.ItemType == ItemType.Gold) return item;
            if (item.ItemType is ItemType.Container) continue;
            light ??= item;
        }
        return light;
    }

    private void EndTheft(BotMind mind, long now)
    {
        mind.Mark = null;
        mind.RogueStage = 0;
        mind.NextRogueMs = now + _rng.Next(240_000, 480_000);
        mind.NextActMs = now + _rng.Next(1000, 3000);
    }
}
