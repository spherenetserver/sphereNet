using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Reactions: what a person does without being asked. A bot healed, cured or blessed
/// by somebody says thanks and remembers the favour; one whose pack is rifled or
/// robbed objects, calls the guards in town, thinks less of the thief and - a brave
/// one, out of town - goes for them; a fight breaking out beside a bot standing about
/// sends the townsfolk a few steps back and turns the fighters' heads; a bot in a
/// fight still answers a player in a word; one a player stands on, or walks after for
/// long, says so; and the bot a player was talking with says goodbye when they walk
/// off mid-talk.
///
/// The world tells the engine through three static hooks the host wires
/// (<see cref="Magic.SpellEngine.OnHelpfulEffect"/>,
/// <see cref="Skills.Information.ActiveSkillEngine.OnBandageHealed"/>,
/// <see cref="Skills.Information.ActiveSkillEngine.OnTheftAttempt"/>); they only queue
/// what the bot says or does next. Everything else is looked at once a second for the
/// bots a connected player can see (<see cref="AttendAudience"/>).
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Percent chance a clean theft or snoop is felt by the bot it was done
    /// to (a fumbled one always is).</summary>
    public int TheftNoticePercent { get; set; } = 35;
    /// <summary>How far from a watching player the bots' manners are looked after.</summary>
    private const int MannersRange = 12;
    /// <summary>How near a fight a bot standing about takes notice of it.</summary>
    private const int FightNoticeRange = 8;
    private const long ThanksGapMs = 20_000;

    private int RuleOr(string key, int fallback) => _bondRules.TryGetValue(key, out int v) ? v : fallback;

    // ------------------------------------------------------------------
    //  Helped: a heal, a cure, a blessing
    // ------------------------------------------------------------------

    /// <summary>A beneficial spell landed on somebody (the host wires the spell
    /// engine's hook here).</summary>
    public void OnHelpfulSpell(Character caster, Character target, SpellType spell)
    {
        if (!target.IsPlayerBot || !_minds.TryGetValue(target.Uid.Value, out var mind)) return;
        string? kind = spell switch
        {
            SpellType.Heal or SpellType.GreaterHeal => "heal",
            SpellType.Cure or SpellType.ArchCure => "cure",
            // Raised from the dead: thanked when the bot finds itself alive.
            SpellType.Resurrection => null,
            _ => "buff",
        };
        if (kind != null) OnHelped(mind, caster, kind);
    }

    /// <summary>A bandage healed or cured somebody (the host wires the healing skill's
    /// hook here).</summary>
    public void OnBandaged(Character healer, Character patient, bool cured)
    {
        // A bot that tends somebody looks at its patient (a cast turns the caster already).
        if (healer.IsPlayerBot && _minds.TryGetValue(healer.Uid.Value, out var tender) && IsStanding(tender))
            Face(tender, patient.Position);
        if (patient.IsPlayerBot && _minds.TryGetValue(patient.Uid.Value, out var mind))
            OnHelped(mind, healer, cured ? "cure" : "heal");
    }

    private void OnHelped(BotMind mind, Character helper, string kind)
    {
        var ch = mind.Char;
        // An NPC healer's spell is its trade; a bot's own is no favour.
        if (helper == ch || ch.IsDead || !helper.IsPlayer || helper.IsDeleted) return;
        long now = NowMs();
        var m = mind.Manners;
        uint uid = helper.Uid.Value;
        if (m.Thanked.TryGetValue(uid, out long last) && now - last < ThanksGapMs) return;
        m.Thanked[uid] = now;
        if (m.Thanked.Count > 32)
            foreach (uint old in m.Thanked.Where(kv => now - kv.Value > 600_000).Select(kv => kv.Key).ToList())
                m.Thanked.Remove(old);
        string category = "thanks_" + kind;
        if (helper.IsPlayerBot)
        {
            // Between bots: a word now and then, where somebody hears it.
            if (_rng.Next(100) < 30 && mind.PendingCategory == null && IsWatched(ch.Position))
                QueueReply(mind, category, now, to: helper, fallback: "thanks_help");
            return;
        }
        WindowThanks++;
        mind.Note(now, $"{kind} from {helper.GetName()}");
        if (Due(mind, helper, "helped", now, RuleOr("helped_gap", 120) * 1000L))
        {
            AddBond(mind, helper, RuleOr("helped", 2), now, "helped it");
            Memorize(mind, helper, "helped", PlaceName(ch.Position));
        }
        Remember(mind, helper);
        m.LookAt = helper;
        m.LookUntilMs = now + 6000;
        if (IsStanding(mind)) Face(mind, helper.Position);
        if (mind.PendingCategory == null)
        {
            string? lang = _watchers.TryGetValue(uid, out var w) ? w.Language : null;
            QueueReply(mind, category, now, to: helper, lang: lang, fallback: "thanks_help");
        }
    }

    // ------------------------------------------------------------------
    //  Robbed
    // ------------------------------------------------------------------

    /// <summary>Somebody tried a theft or a snoop on somebody (the host wires the
    /// skills' hook here). The bot it was tried on may notice: it objects, calls the
    /// guards where there are any, holds it against a player, and a brave fighter out
    /// of town goes for a thief the law already knows.</summary>
    public void OnTheft(Character thief, Character mark, Item? item, bool success, bool snoop)
    {
        if (!mark.IsPlayerBot || mark.IsDead || thief == mark || !_minds.TryGetValue(mark.Uid.Value, out var mind)) return;
        // A fumbled try is felt; a clean one only now and then.
        if (success && _rng.Next(100) >= TheftNoticePercent) return;
        long now = NowMs();
        var m = mind.Manners;
        if (now < m.NextTheftReactMs) return;
        m.NextTheftReactMs = now + 15_000;
        WindowTheftReacts++;
        mind.Note(now, $"notices {thief.GetName()} {(snoop ? "in its pack" : "stealing from it")}");
        if (thief.HasClient)
        {
            AddBond(mind, thief, snoop ? RuleOr("snooped", -3) : RuleOr("stolen", -10), now,
                snoop ? "went through its pack" : "stole from it");
            // A companion who robs it is one no more.
            if (!snoop && mind.Companion == thief) EndCompanionship(mind, now, quietly: true);
        }
        m.LookAt = thief;
        m.LookUntilMs = now + 8000;
        if (IsStanding(mind)) Face(mind, thief.Position);
        if (mind.Scene != null) EndScene(mind.Scene);
        string? lang = _watchers.TryGetValue(thief.Uid.Value, out var w) ? w.Language : null;
        QueueReply(mind, snoop ? "theft_snoop" : "theft_steal", now, to: thief, lang: lang, fallback: "theft_steal",
            action: () => AfterTheft(mind, thief));
        // Said at once, not typed out at leisure.
        mind.PendingSayMs = now + _rng.Next(300, 900);
    }

    private void AfterTheft(BotMind mind, Character thief)
    {
        var ch = mind.Char;
        if (ch.IsDead || thief.IsDeleted || thief.IsDead || !Within(thief.Position, ch.Position, 14)) return;
        long now = NowMs();
        if (IsGuardedAt(ch.Position))
        {
            // The words reach the guards like a player's; they come for one the
            // witnesses have made a criminal.
            Speak(mind, "theft_guards", fallback: "guards_call", force: true,
                extra: new Dictionary<string, string> { ["other"] = BaseName(thief), ["place"] = PlaceName(ch.Position).ToUpperInvariant() });
            return;
        }
        // Out of town a brave fighter goes for the thief - when that is no crime: one
        // the witnesses flagged, never somebody still blue to it.
        if (mind.Client == null || !mind.IsFighter || mind.Nerve < BraveNerve || IsRogueBot(ch) ||
            mind.Mode is not (BotMode.Linger or BotMode.Travel) || thief.PrivLevel >= PrivLevel.Counsel ||
            GameClient.ComputeNotoriety(_world, ch, thief) == NotoGood)
            return;
        StartFightWith(mind, thief, now);
    }

    /// <summary>Nerve from which a fighter robbed out of town strikes back.</summary>
    public double BraveNerve { get; set; } = 1.3;

    /// <summary>The one place the reactions hand a foe to the fight code.</summary>
    private void StartFightWith(BotMind mind, Character foe, long now) => Engage(mind, foe, now);

    // ------------------------------------------------------------------
    //  A fight near by
    // ------------------------------------------------------------------

    /// <summary>A fight has broken out within sight of a bot standing about:
    /// <paramref name="attacker"/> struck <paramref name="victim"/> a moment ago. The
    /// townsfolk back away a few steps and look on; fighters turn to watch. Whether a
    /// bot joins in is the fight code's to decide: this is where it would be asked.</summary>
    internal void OnNearbyFight(BotMind mind, Character attacker, Character victim, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        if (now < m.NextFightReactMs || mind.Mode != BotMode.Linger || ch.IsDead || attacker == ch || victim == ch ||
            mind.Favor != null || mind.Companion != null || mind.BankStage != 0 || IsRogueBot(ch))
            return;
        int dist = Chebyshev(ch.Position, victim.Position);
        if (dist > FightNoticeRange) return;
        m.NextFightReactMs = now + _rng.Next(20_000, 40_000);
        WindowFightReacts++;
        if (mind.Scene != null) EndScene(mind.Scene);
        mind.Note(now, $"a fight near by: {attacker.GetName()} on {victim.GetName()}");
        if (mind.IsFighter)
        {
            m.LookAt = victim;
            m.LookUntilMs = now + 10_000;
            Face(mind, victim.Position);
            if (_rng.Next(100) < 35) Speak(mind, "fight_watch", fallback: null);
        }
        else
        {
            m.EndIdle();
            m.BackOffTo = victim.Position;
            m.BackOffUntilMs = now + 6000;
            m.BackOffRun = dist <= 3;
            if (_rng.Next(100) < 35) Speak(mind, "fight_scared", fallback: null);
        }
        mind.NextActMs = Math.Min(mind.NextActMs, now + _rng.Next(200, 700));
    }

    // ------------------------------------------------------------------
    //  Spoken to in a fight
    // ------------------------------------------------------------------

    /// <summary>A player's line while the bot fights or runs: when it is meant for this
    /// bot (named, the bot they were talking with, their companion or party member) it
    /// answers in a word - busy, or a call for help when it is badly hurt - and fights on.</summary>
    private void BusyReply(BotMind mind, Character speaker, BotWatcher w, List<string> words, long now, string lang)
    {
        var m = mind.Manners;
        if (now < m.NextBusyReplyMs || mind.PendingCategory != null) return;
        bool meant = words.Contains(FirstName(mind.Char).ToLowerInvariant()) ||
                     (w.Focus == mind && now < w.FocusUntilMs) || mind.Companion == speaker || PartyLeader(mind) == speaker;
        if (!meant) return;
        m.NextBusyReplyMs = now + 8000;
        var ch = mind.Char;
        bool hurt = ch.MaxHits > 0 && ch.Hits * 2 < ch.MaxHits;
        QueueReply(mind, hurt ? "fight_help" : "fight_busy", now, to: speaker, lang: lang, fallback: "fight_busy");
        mind.PendingSayMs = now + _rng.Next(300, 800);
        WindowReplies++;
    }

    // ------------------------------------------------------------------
    //  Once a second, for the bots a player can see
    // ------------------------------------------------------------------

    private readonly List<BotMind> _attendScratch = [];

    /// <summary>Once a second, after the audience is counted: creatures left standing
    /// are removed where nobody looks, and around each connected player the bots'
    /// manners are looked after.</summary>
    private void AttendAudience(long now)
    {
        if (_strayMounts.Count > 0 && now >= _nextStraySweepMs) SweepStrayMounts(now);
        foreach (var w in _watching)
            if (w.Crowd > 0)
                AttendWatcher(w, now);
    }

    private void AttendWatcher(BotWatcher w, long now)
    {
        var player = w.Char;
        // The bot they were talking with, left standing mid-talk: a goodbye.
        if (w.Focus is { } partner && now < w.FocusUntilMs && now - w.LastSpokeMs < 45_000 && !player.IsDead &&
            partner.Mode == BotMode.Linger && partner.PendingCategory == null && !partner.Char.IsDead &&
            partner.Companion != player && partner.Favor == null &&
            !Within(partner.Char.Position, player.Position, 8) && Within(partner.Char.Position, player.Position, HearRange))
        {
            w.FocusUntilMs = now;
            partner.Manners.LookAt = null;
            Face(partner, player.Position);
            Speak(partner, "bye_leaving", fallback: "respond_bye", to: player, lang: w.Language);
        }

        Character? attacker = null, victim = null;
        long tick = Environment.TickCount64;
        _attendScratch.Clear();
        foreach (var c in _world.GetCharsInRange(player.Position, MannersRange))
        {
            if (c.IsDeleted || c.IsDead || c.MapIndex != player.MapIndex) continue;
            // A fight: somebody here was struck within the last few seconds by
            // someone still about.
            if (victim == null && c.Attackers.Count > 0)
                foreach (var hit in c.Attackers)
                {
                    if (tick - hit.LastHitTick > 6000 || _world.FindChar(hit.Uid) is not { IsDead: false, IsDeleted: false } by ||
                        !Within(by.Position, c.Position, 12))
                        continue;
                    attacker = by;
                    victim = c;
                    break;
                }
            if (c.IsPlayerBot && _minds.TryGetValue(c.Uid.Value, out var mind)) _attendScratch.Add(mind);
        }
        foreach (var mind in _attendScratch)
        {
            AttendBot(mind, w, now);
            if (victim != null) OnNearbyFight(mind, attacker!, victim, now);
        }
    }

    /// <summary>One bot a player can see: it faces whoever it trades with, looks up at
    /// a player passing close by, and says so when the player stands on its tile or has
    /// walked after it for long.</summary>
    private void AttendBot(BotMind mind, BotWatcher w, long now)
    {
        var ch = mind.Char;
        var player = w.Char;
        var m = mind.Manners;
        if (player.IsDead || player.IsInvisible || player.IsStatFlag(StatFlag.Hidden)) return;
        uint uid = player.Uid.Value;
        int dist = Chebyshev(ch.Position, player.Position);
        if (mind.Trade is { } trade && IsStanding(mind))
        {
            var other = trade.Initiator == ch ? trade.Partner : trade.Initiator;
            if (other != null && !other.IsDeleted) Face(mind, other.Position);
        }
        if (mind.Mode == BotMode.Linger)
        {
            if (mind.Scene != null || mind.PendingCategory != null || mind.SettleTo != null) return;
            if (dist == 0)
            {
                // Stood on: a word after a while, and a step aside.
                if (m.CrowdedBy != uid)
                {
                    m.CrowdedBy = uid;
                    m.CrowdedSinceMs = now;
                }
                else if (now - m.CrowdedSinceMs > 6000 && now >= m.NextCrowdedSayMs)
                {
                    m.NextCrowdedSayMs = now + 60_000;
                    m.CrowdedBy = 0;
                    Speak(mind, "crowded", fallback: null, to: player, lang: w.Language);
                    if (m.Idle != BotIdleKind.Seat)
                    {
                        m.BackOffTo = player.Position;
                        m.BackOffUntilMs = now + 1200;
                        m.BackOffRun = false;
                        mind.NextActMs = Math.Min(mind.NextActMs, now + 600);
                    }
                }
                return;
            }
            if (m.CrowdedBy == uid) m.CrowdedBy = 0;
            // A glance at someone passing close by.
            if (dist <= 4 && now >= m.NextGlanceMs && m.LookAt == null && m.BackOffTo == null &&
                m.Idle is not (BotIdleKind.Seat or BotIdleKind.Train))
            {
                m.NextGlanceMs = now + _rng.Next(15_000, 40_000);
                if (_rng.Next(100) < GlancePercent)
                {
                    m.LookAt = player;
                    m.LookUntilMs = now + _rng.Next(2000, 4000);
                    Face(mind, player.Position);
                }
            }
            return;
        }
        if (mind.Mode != BotMode.Travel || mind.Guiding == player || mind.Companion == player || mind.Leader != null ||
            ch.IsDead)
            return;
        // Walked after: the same player within a few steps for a good stretch of road.
        if (dist > 3)
        {
            if (m.FollowedBy == uid && dist > 6) m.FollowedBy = 0;
            return;
        }
        if (m.FollowedBy != uid)
        {
            m.FollowedBy = uid;
            m.FollowedSinceMs = now;
            m.FollowedFrom = ch.Position;
            return;
        }
        if (now - m.FollowedSinceMs < 12_000 || now < m.NextFollowedSayMs ||
            (m.FollowedFrom.Map == ch.MapIndex && Chebyshev(m.FollowedFrom, ch.Position) < 12) || PartyLeader(mind) == player)
            return;
        m.NextFollowedSayMs = now + 120_000;
        m.FollowedBy = 0;
        // It stops, turns and asks.
        mind.NextActMs = Math.Max(mind.NextActMs, now + _rng.Next(2000, 3500));
        Face(mind, player.Position);
        Speak(mind, "followed", fallback: null, to: player, lang: w.Language);
    }
}
