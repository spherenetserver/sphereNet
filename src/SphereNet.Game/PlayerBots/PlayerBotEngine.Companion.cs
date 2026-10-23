using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Companions and friends looking out for a player. A friend (see the Bonds part) who
/// is asked to travel with a player becomes their companion (<c>PLAYERBOT.COMPANION</c>,
/// one bot per player): it leaves its own errands, keeps at the player's side, fights
/// what the player fights and what sets on them, keeps them healed, waits where told,
/// holds their things, and goes through a gate after them. When the player logs out it
/// waits a few minutes where they parted, then goes about its life; when they are back
/// it comes to them the way a person would - through a gate standing by, on a rune of
/// its own near them, or by road. If the player dies it raises them when it can and
/// stands over the body against looters (a looter of an innocent's corpse is a
/// criminal, and striking a criminal is no crime).
/// Friends nearby also look out for a player unasked, now and then: a heal or a cure
/// offered (done on a yes), a murderer about pointed out, help offered to someone
/// standing about a long while.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string CompanionTag = "PLAYERBOT.COMPANION";
    /// <summary>How long a companion waits where the player logged out.</summary>
    private const long CompanionWaitMs = 240_000;
    /// <summary>Within this the companion follows; farther it catches up.</summary>
    private const int CompanionNear = 40;

    public int CompanionCount => _order.Count(m => m.Companion != null);
    public long WindowOffers { get; private set; }

    /// <summary>A saved companion is taken back with the bot: the player may be
    /// offline (then the bot is told it misses them).</summary>
    private void ResolveCompanion(BotMind m)
    {
        if (!m.Char.TryGetTag(CompanionTag, out string? tag) || !uint.TryParse(tag, out uint uid)) return;
        var p = _world.FindChar(new Serial(uid));
        if (p == null || p.IsDeleted || !p.IsPlayer || p.IsPlayerBot || p.TryGetTag(MarkerTag, out _))
        {
            m.Char.RemoveTag(CompanionTag);
            return;
        }
        m.Companion = p;
        m.CompanionMissed = true;
    }

    private BotMind? CompanionOf(Character player)
    {
        foreach (var m in _order)
            if (m.Companion == player) return m;
        return null;
    }

    private void BecomeCompanion(BotMind m, Character player, long now)
    {
        m.Companion = player;
        m.Char.SetTag(CompanionTag, player.Uid.Value.ToString());
        m.CompanionMissed = false;
        m.CompanionAwayMs = 0;
        m.Protecting = false;
        m.Leader = null;
        if (m.Mode == BotMode.Travel) m.BeginLinger(null, now, _rng, short_: true);
        m.Note(now, $"goes about with {player.GetName()} now");
        Memorize(m, player, "met", PlaceName(m.Char.Position));
    }

    private void EndCompanionship(BotMind m, long now, bool quietly)
    {
        var p = m.Companion;
        m.Companion = null;
        m.Protecting = false;
        m.CompanionHoldUntilMs = 0;
        m.Char.RemoveTag(CompanionTag);
        if (p == null) return;
        m.Note(now, $"no longer goes about with {p.GetName()}{(quietly ? "" : " (let go)")}");
        if (HeldItems(m.Char, p.Uid.Value).Count > 0) StartGiveBack(m, p, now);
    }

    // ------------------------------------------------------------------
    //  Asked
    // ------------------------------------------------------------------

    private (string, Dictionary<string, string>?) AnswerCompanion(BotMind m, Character player, long now)
    {
        if (IsRogueBot(m.Char)) return ("favor_refuse", null);
        if (m.Companion == player) return ("companion_already", null);
        if (m.Companion != null) return ("companion_busy", new Dictionary<string, string> { ["other"] = BaseName(m.Companion) });
        if (CompanionOf(player) is { } other)
            return ("companion_taken", new Dictionary<string, string> { ["other"] = FirstName(other.Char) });
        if (Grudges(m, player)) return ("favor_refuse_grudge", null);
        if (BondLevel(m, player) < BotBondLevel.Friend) return ("companion_not_yet", null);
        if (Parties?.FindParty(m.Char.Uid) is { } party && !party.IsMember(player.Uid))
            return ("favor_busy", null);
        BecomeCompanion(m, player, now);
        return ("companion_yes", null);
    }

    private (string, Dictionary<string, string>?) AnswerDismiss(BotMind m, Character player, long now)
    {
        if (m.Companion != player) return ("dismiss_none", null);
        EndCompanionship(m, now, quietly: false);
        return ("companion_bye", null);
    }

    private (string, Dictionary<string, string>?) AnswerFriend(BotMind m, Character player)
    {
        if (IsRogueBot(m.Char)) return ("favor_refuse", null);
        if (Grudges(m, player)) return ("favor_refuse_grudge", null);
        return BondLevel(m, player) switch
        {
            BotBondLevel.Friend => ("friend_already", null),
            BotBondLevel.Acquaintance => ("friend_soon", null),
            _ => ("friend_new", null),
        };
    }

    /// <summary>"Do you remember me": a friend brings up something shared, an
    /// acquaintance says yes, a stranger no. The category, its tokens and a line said
    /// first (or null).</summary>
    private (string, Dictionary<string, string>?, string?) AnswerRemember(BotMind m, Character player, string lang)
    {
        var level = BondLevel(m, player);
        if (level == BotBondLevel.Stranger && !Knows(m, player)) return ("remember_no", null, null);
        if (PickMemory(m, player, lang) is { } memory) return (memory.Category, memory.Tokens, "remember_yes");
        return ("remember_yes", null, null);
    }

    // ------------------------------------------------------------------
    //  Going about together
    // ------------------------------------------------------------------

    /// <summary>One act of a companion's life. False when it has no companion or the
    /// companion is away and the bot goes about its own business meanwhile.</summary>
    private bool ActCompanion(BotMind m, long now)
    {
        var p = m.Companion;
        if (p == null) return false;
        var ch = m.Char;
        if (p.IsDeleted)
        {
            EndCompanionship(m, now, quietly: true);
            return false;
        }
        if (m.Guiding == p) return false;
        if (!p.HasClient)
        {
            // Logged out: it waits a while where they parted, then lives its own life.
            if (!m.CompanionMissed)
            {
                m.CompanionMissed = true;
                m.CompanionAwayMs = now;
                m.Note(now, $"{p.GetName()} is gone; waits a while");
            }
            if (now - m.CompanionAwayMs < CompanionWaitMs && m.Mode == BotMode.Linger)
            {
                m.LingerUntilMs = Math.Max(m.LingerUntilMs, now + 10_000);
                m.NextActMs = now + 3000;
                return true;
            }
            return false;
        }
        int dist = p.MapIndex == ch.MapIndex ? p.Position.GetDistanceTo(ch.Position) : int.MaxValue;
        if (p.IsDead) return GuardFallen(m, p, dist, now);

        // Told to wait: it waits, still standing up for the player.
        if (now < m.CompanionHoldUntilMs)
        {
            if (dist <= 12 && AttackerOf(p, m, now) is { } at)
            {
                Engage(m, at, now);
                return true;
            }
            if (ch.Position.GetDistanceTo(m.HoldAt) > 1)
            {
                bool back = StepToward(m, m.HoldAt, run: false, searchWithin: 20);
                m.NextActMs = now + StepDelay(m, back, false, now);
            }
            else
                m.NextActMs = now + 1000;
            return true;
        }

        if (dist > CompanionNear) return CatchUp(m, p, now);

        // Back together: said once it is by them, where they hear it.
        if (m.CompanionMissed && dist <= 8)
        {
            m.CompanionMissed = false;
            m.CatchGate = null;
            m.CompanionAwayMs = 0;
            m.Note(now, $"back with {p.GetName()}");
            QueueSoon(m, "companion_back", now, p);
        }
        // A trip to catch up is over once it is here.
        if (m.Mode == BotMode.Travel && m.Favor == null) m.BeginLinger(null, now, _rng, short_: true);

        // The player's fight is its fight.
        if (m.Client != null && p.FightTarget.IsValid &&
            _world.FindChar(p.FightTarget) is { IsDead: false, IsDeleted: false } foe && foe != ch &&
            !m.Avoids(foe, now) && foe.Position.GetDistanceTo(ch.Position) <= 14 &&
            (!foe.IsPlayer || Clients.GameClient.ComputeNotoriety(_world, ch, foe) != NotoGood))
        {
            Engage(m, foe, now);
            return true;
        }
        if (m.Client != null && AttackerOf(p, m, now) is { } attacker)
        {
            Engage(m, attacker, now);
            return true;
        }
        if (m.Protecting && m.Client != null && ThreatTo(p, m, now) is { } threat)
        {
            Engage(m, threat, now);
            return true;
        }

        if (dist > FollowRange)
        {
            bool run = dist > FollowRange + 3 || p.IsMounted;
            bool stepped = StepToward(m, p.Position, run, searchWithin: 40);
            m.NextActMs = now + StepDelay(m, stepped, run, now);
        }
        else
            m.NextActMs = now + 500;
        return true;
    }

    /// <summary>A monster coming at a player a companion watches over: hostile, within
    /// six tiles of them and in sight.</summary>
    private Character? ThreatTo(Character p, BotMind m, long now)
    {
        Character? best = null;
        int bestD = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(p.Position, 6))
        {
            if (c.IsDead || c.IsDeleted || c.IsPlayer || c.OwnerSerial.IsValid || m.Avoids(c, now)) continue;
            if (c.NpcBrain is not (NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon)) continue;
            if (!_world.CanSeeLOS(p.Position, c.Position)) continue;
            int d = c.Position.GetDistanceTo(p.Position);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>The companion is far: a gate standing by that leads near them, a rune
    /// of its own near them, or the road. False when it walks by road this act (the
    /// trip does it) or can do nothing (its own life goes on).</summary>
    private bool CatchUp(BotMind m, Character p, long now)
    {
        var ch = m.Char;
        if (!m.CompanionMissed)
        {
            m.CompanionMissed = true;
            m.CompanionAwayMs = now;
        }
        // A gate already being walked to.
        if (m.CatchGate is { } gate)
        {
            if (gate.IsDeleted || gate.Position.GetDistanceTo(ch.Position) > 10 || gate.MapIndex != ch.MapIndex)
                m.CatchGate = null;
            else
            {
                bool stepped = StepToward(m, gate.Position, run: true, searchWithin: 10);
                if (m.Carried) m.CatchGate = null;
                m.NextActMs = now + StepDelay(m, stepped, true, now);
                return true;
            }
        }
        if (now < m.NextCompanionCheckMs) return false;
        m.NextCompanionCheckMs = now + 5000;
        // Through the gate the player took (or any standing by that leads near them).
        foreach (var item in _world.GetItemsInRange(ch.Position, 8))
        {
            if (item.IsDeleted || item.ItemType is not (ItemType.Telepad or ItemType.Moongate)) continue;
            var to = item.MoreP;
            if (to.Map == p.MapIndex && (to.X != 0 || to.Y != 0) && to.GetDistanceTo(p.Position) <= 15)
            {
                m.CatchGate = item;
                m.Note(now, $"after {p.GetName()} through the gate at {item.Position}");
                return true;
            }
        }
        // A rune of its own near them.
        if (KnowsTravel(m) && now >= m.NextRecallMs)
            foreach (var rune in OwnRunes(ch))
                if (IsUsableRune(rune) && rune.TryGetRuneMark(out var mark) && mark.Map == p.MapIndex &&
                    mark.GetDistanceTo(p.Position) <= 60 && mark.GetDistanceTo(p.Position) + 20 < (p.MapIndex == ch.MapIndex ? p.Position.GetDistanceTo(ch.Position) : int.MaxValue))
                {
                    if (Recall(m, rune, now, $"to {p.GetName()}")) return true;
                    break;
                }
        // By road, to the place nearest them.
        if (p.MapIndex == ch.MapIndex)
        {
            int here = Nav.FindNearest(ch.Position, 64);
            int there = Nav.FindNearest(p.Position, 64);
            if (here >= 0 && there >= 0 && Nav.Waypoints[here].Component == Nav.Waypoints[there].Component)
            {
                var dest = NearestPlace(p.Position, 40);
                if (dest != null && Nav.ComponentOf(dest) == Nav.Waypoints[here].Component)
                {
                    if (m.Mode == BotMode.Travel && m.Goal == dest) return false;
                    m.Goal = dest;
                    ch.SetTag(GoalTag, dest.Name);
                    m.Mode = BotMode.Travel;
                    m.Route = null;
                    m.Note(now, $"walks after {p.GetName()} to {dest.Name}");
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>The companion is a ghost: raise them when it can, stand over the body
    /// against looters, keep by the ghost.</summary>
    private bool GuardFallen(BotMind m, Character p, int dist, long now)
    {
        var ch = m.Char;
        if (dist <= 10 && CanRaise(m, now))
        {
            Raise(m, p, null, now);
            m.NextActMs = now + 1500;
            return true;
        }
        bool canRes = m.IsCaster && HasSpell(ch, SpellType.Resurrection) && ch.GetSkill(SkillType.Magery) >= 700;
        if (dist <= 18 && Due(m, p, "fallen", now, 120_000))
            QueueSoon(m, canRes ? "companion_res_soon" : "companion_ghost", now, p);
        if (canRes && ch.Mana < 50 && now >= m.NextMeditateMs && m.Client != null && !ch.TryGetCastingSpell(out _))
        {
            m.NextMeditateMs = now + 12_000;
            m.Client.HandleUseSkill((int)SkillType.Meditation);
            WindowMeditations++;
        }
        // Looters at the body: a criminal now, and fair game.
        if (m.Client != null && PlayerCorpse(p, ch.Position, 14) is { } corpse)
            foreach (var c in _world.GetCharsInRange(corpse.Position, 3))
                if (c != ch && c != p && !c.IsDead && !c.IsDeleted && c.IsFlaggedAsCriminal && !m.Avoids(c, now))
                {
                    m.Note(now, $"{c.GetName()} loots {p.GetName()}'s body");
                    Engage(m, c, now);
                    return true;
                }
        if (dist > CompanionNear) return false;
        if (dist > FollowRange)
        {
            bool stepped = StepToward(m, p.Position, run: dist > 8, searchWithin: 30);
            m.NextActMs = now + StepDelay(m, stepped, dist > 8, now);
        }
        else
            m.NextActMs = now + 800;
        return true;
    }

    private Item? PlayerCorpse(Character p, Point3D near, int range)
    {
        foreach (var item in _world.GetItemsInRange(near, range))
            if (!item.IsDeleted && item.ItemType == ItemType.Corpse && item.IsOnGround &&
                item.TryGetTag("OWNER_UID", out string? owner) && ScriptNumber.TryParseUInt(owner, out uint uid) &&
                uid == p.Uid.Value)
                return item;
        return null;
    }

    // ------------------------------------------------------------------
    //  Looking out for a player unasked
    // ------------------------------------------------------------------

    /// <summary>Once a second per player (from the scene director): a friend near by may
    /// offer a heal or a cure, point out a murderer about, or ask someone who has stood
    /// about a long while whether they need anything. One offer per player in 45 s, one
    /// per bot a minute; a shy bot offers less.</summary>
    private void OfferHelp(BotWatcher w, long now)
    {
        var p = w.Char;
        if (p.Position.Map != w.IdleAnchor.Map || p.Position.GetDistanceTo(w.IdleAnchor) > 3)
        {
            w.IdleAnchor = p.Position;
            w.IdleSinceMs = now;
        }
        if (p.IsDead) return;
        // Something of theirs a bot still holds (a rune after a gate they went through)
        // goes back when they meet again; a companion keeps what it carries until asked.
        foreach (var c in _world.GetCharsInRange(p.Position, 6))
            if (c.IsPlayerBot && !c.IsDead && c.TryGetTag(HeldTag, out _) && _minds.TryGetValue(c.Uid.Value, out var holder) &&
                holder.GiveBackTo == null && holder.Companion != p && now >= holder.NextGiveBackMs &&
                holder.Favor == null && HeldItems(c, p.Uid.Value).Count > 0)
                StartGiveBack(holder, p, now);
        if (now - w.LastOfferMs < 45_000) return;
        foreach (var c in _world.GetCharsInRange(p.Position, 10))
        {
            if (!c.IsPlayerBot || c.IsDead || !_minds.TryGetValue(c.Uid.Value, out var m)) continue;
            if (m.Client == null || m.Favor != null || m.PendingCategory != null || m.Scene != null ||
                m.Mode is BotMode.Fight or BotMode.Flee || now < m.NextOfferMs || IsRogueBot(c))
                continue;
            bool companion = m.Companion == p;
            var level = BondLevel(m, p);
            string trait = TraitOf(m);
            bool friendly = companion || level == BotBondLevel.Friend ||
                            (level == BotBondLevel.Acquaintance && trait == "kind");
            if (!friendly || (trait == "shy" && _rng.Next(2) == 0)) continue;
            var ch = m.Char;
            bool hurt = p.MaxHits > 0 && p.Hits * 2 < p.MaxHits;
            string? category = null;
            Action? onYes = null;
            Dictionary<string, string>? tokens = null;
            // A companion tends its player without asking (see the party tending).
            if (!companion && p.IsPoisoned && m.IsCaster && ch.Mana >= 6 && HasSpell(ch, SpellType.Cure))
            {
                category = "offer_cure";
                onYes = CastOn(m, p, SpellType.Cure);
            }
            else if (!companion && hurt && m.IsCaster && ch.Mana >= 11 &&
                     (HasSpell(ch, SpellType.GreaterHeal) || HasSpell(ch, SpellType.Heal)))
            {
                category = "offer_heal";
                onYes = CastOn(m, p, HasSpell(ch, SpellType.GreaterHeal) ? SpellType.GreaterHeal : SpellType.Heal);
            }
            else if (RedNear(p, m) is { } red && Due(m, red, "warn", now, 300_000))
            {
                category = "warn_red";
                tokens = new Dictionary<string, string> { ["other"] = BaseName(red) };
                if (DirWord(p.Position, red.Position, w.Language ?? Chat.Language) is { } dir) tokens["dir"] = dir;
            }
            else if (now - w.IdleSinceMs > 120_000 && now - w.LastSpokeMs > 120_000 && IsGuardedAt(p.Position) &&
                     Due(m, p, "offerhelp", now, 1_200_000))
            {
                category = "offer_help";
                string lang = w.Language ?? Chat.Language;
                onYes = () =>
                {
                    var (cat, t) = AnswerCan(m, p, lang);
                    QueueReply(m, cat, NowMs(), to: p, lang: lang, fallback: null, tokens: t);
                };
            }
            if (category == null) continue;
            m.PendingOnYes = onYes;
            QueueReply(m, category, now, to: p, lang: w.Language, fallback: null, tokens: tokens);
            w.LastOfferMs = now;
            m.NextOfferMs = now + 60_000;
            WindowOffers++;
            return;
        }
    }

    /// <summary>A cast on a player done later (on their yes), if it still makes sense.</summary>
    private Action CastOn(BotMind m, Character p, SpellType spell) => () =>
    {
        var ch = m.Char;
        if (m.Client == null || p.IsDead || p.IsDeleted || ch.IsDead || !Within(p.Position, ch.Position, 10) ||
            ch.TryGetCastingSpell(out _))
            return;
        if (spell == SpellType.Cure ? !p.IsPoisoned : p.Hits >= p.MaxHits) return;
        m.Client.HandleCastSpell(spell, p.Uid.Value);
        m.Note(NowMs(), $"casts {spell} on {p.GetName()}, as offered");
        WindowFavors++;
    };

    /// <summary>A murderer the player may not have seen: within 18 tiles of them, in
    /// sight of the bot, not the bot's own friend.</summary>
    private Character? RedNear(Character p, BotMind m)
    {
        foreach (var c in _world.GetCharsInRange(p.Position, 18))
        {
            if (c == p || c == m.Char || c.IsDead || c.IsDeleted || !c.IsPlayer || !c.IsMurderer) continue;
            if (c.IsInvisible || c.IsStatFlag(StatFlag.Hidden)) continue;
            if (c.Position.GetDistanceTo(p.Position) < 4) continue; // standing right there: seen
            if (!_world.CanSeeLOS(m.Char.Position, c.Position)) continue;
            if (!c.IsPlayerBot && BondLevel(m, c) == BotBondLevel.Friend) continue;
            return c;
        }
        return null;
    }

    /// <summary>The compass word (<c>token_dir</c>, north first, clockwise) from one
    /// point to another.</summary>
    private string? DirWord(Point3D from, Point3D to, string lang)
    {
        int dx = to.X - from.X, dy = to.Y - from.Y;
        // North is up the map (-Y); compass points clockwise from it.
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (angle < 0) angle += 360;
        int index = (int)Math.Round(angle / 45) % 8;
        return Chat.Line("token_dir", index, lang);
    }
}
