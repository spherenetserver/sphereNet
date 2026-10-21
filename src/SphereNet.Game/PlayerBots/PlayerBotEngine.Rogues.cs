using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Rogues. Some bots are player killers and some are thieves (PLAYERBOT.ROLE,
/// decided once per bot by <see cref="PkPercent"/> and <see cref="ThiefPercent"/>).
/// A player killer keeps out of guarded towns, waits about dungeon mouths, moongates
/// and the roads, and sets on a lone player or bot it rates itself against; then
/// goes through the body. A thief works the town crowd: steps up to someone, opens
/// their pack and steals from it with its skill, then hides and walks off. Every
/// blow, snoop and theft goes through the bot's client, so the crime rules - the
/// murderer and criminal flags, the guards, the murderer's resurrection town - apply
/// exactly as to a player.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string RoleTag = "PLAYERBOT.ROLE";

    /// <summary>Percent of fighter bots that are player killers.</summary>
    public int PkPercent { get; set; }
    /// <summary>Percent of the other bots that are thieves.</summary>
    public int ThiefPercent { get; set; }

    public long WindowPkAttacks { get; private set; }
    public long WindowThefts { get; private set; }

    private string RoleOf(BotMind mind)
    {
        var ch = mind.Char;
        if (ch.TryGetTag(RoleTag, out string? role) && role != null) return role;
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
        return role;
    }

    private bool IsPk(BotMind mind) => RoleOf(mind) == "pk";
    private bool IsThief(BotMind mind) => RoleOf(mind) == "thief";

    private bool IsRogueBot(Character c) => _minds.TryGetValue(c.Uid.Value, out var m) && RoleOf(m) != "-";

    /// <summary>Where a player killer spends its time: out of town, where people
    /// pass by - a dungeon mouth, a moongate, a shrine, a landmark.</summary>
    private BotDestination? PickPkHaunt(BotMind mind, List<BotDestination> pool)
    {
        if (!IsPk(mind) || _rng.Next(100) >= 75) return null;
        var haunts = pool.Where(d => d.City.Length == 0 && d != mind.At &&
                                     d.Type is "DungeonEntrance" or "Moongate" or "Shrine" or "Landmark" or
                                         "AdventureArea" or "Graveyard")
            .ToList();
        return haunts.Count > 0 ? haunts[_rng.Next(haunts.Count)] : null;
    }

    /// <summary>A player killer out of a guarded region picks a victim now and then:
    /// a living player or bot, not a staff member, not another rogue, not in its
    /// party, and not much tougher than itself.</summary>
    private Character? PickVictim(BotMind mind, long now, bool guarded)
    {
        if (guarded || now < mind.NextRogueMs || !IsPk(mind) || mind.Client == null) return null;
        mind.NextRogueMs = now + 5000;
        var ch = mind.Char;
        var party = Parties?.FindParty(ch.Uid);
        Character? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(ch.Position, 10))
        {
            if (c == ch || c.IsDeleted || c.IsDead || !c.IsPlayer || c.MapIndex != ch.MapIndex) continue;
            if (c.IsInvisible || c.IsStatFlag(StatFlag.Hidden) || c.PrivLevel >= PrivLevel.Counsel) continue;
            if (IsRogueBot(c) || (party != null && party.IsMember(c.Uid))) continue;
            if (c.MaxHits > ch.MaxHits * 1.4) continue;
            int d = ch.Position.GetDistanceTo(c.Position);
            if (d < bestDist) { best = c; bestDist = d; }
        }
        if (best == null || _rng.Next(100) >= 60) return null;
        mind.NextRogueMs = now + _rng.Next(60_000, 180_000);
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
