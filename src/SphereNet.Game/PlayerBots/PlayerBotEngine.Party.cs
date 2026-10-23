using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Party;

namespace SphereNet.Game.PlayerBots;

/// <summary>Grouping up with players. A player's party invitation is answered the way
/// a client answers it (the accept sub-command of 0xBF 0x06); once in, the bot stays
/// at its leader's side, joins the fight the leader picks, stands up for the leader
/// when something sets on it, tends the party's wounds (heal, cure, bandage, raise),
/// takes orders (wait, follow, leave, status) said aloud or in party chat, answers
/// party chat there, and leaves the party when the leader has been gone for a while.</summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>The party engine, to find who leads a bot's party.</summary>
    public PartyManager? Parties { get; set; }

    private const int FollowRange = 3;
    private const long LeaderGoneMs = 120_000;

    /// <summary>Accept a real player's standing invitation, after a moment.</summary>
    private void CheckInvites(BotMind mind, long now)
    {
        var ch = mind.Char;
        // A player killer follows nobody into town.
        if (mind.Client == null || Parties?.FindParty(ch.Uid) != null || IsPk(mind)) return;
        if (mind.InviteFrom != null)
        {
            if (now < mind.InviteAnswerMs) return;
            var inviter = mind.InviteFrom;
            mind.InviteFrom = null;
            if (inviter.IsDeleted || !inviter.HasClient) return;
            Span<byte> data = stackalloc byte[5];
            data[0] = 8; // accept
            uint uid = inviter.Uid.Value;
            data[1] = (byte)(uid >> 24); data[2] = (byte)(uid >> 16); data[3] = (byte)(uid >> 8); data[4] = (byte)uid;
            mind.Client.HandleExtendedCommand(0x06, data.ToArray());
            if (Parties?.FindParty(ch.Uid) != null)
            {
                mind.LeaderSeenMs = now;
                mind.FollowCloseMs = now;
                if (_rng.Next(100) < 70) Speak(mind, "party_join", fallback: null);
            }
            return;
        }
        foreach (var other in _world.GetCharsInRange(ch.Position, 14))
        {
            if (!other.HasClient || other.IsDead) continue;
            if (other.TryGetTag(PartyManager.LastInviteTag, out string? invited) &&
                ScriptNumber.TryParseUInt(invited, out uint who) && who == ch.Uid.Value)
            {
                mind.InviteFrom = other;
                mind.InviteAnswerMs = now + _rng.Next(1200, 3500);
                return;
            }
        }
    }

    /// <summary>The nearest one attacking <paramref name="leader"/> within 10 tiles that
    /// the bot may strike: a creature, or a player not blue to it (striking back at a
    /// criminal, a murderer or one gone gray is no crime). Never a party member.</summary>
    private Character? AttackerOf(Character leader, BotMind mind, long now)
    {
        var ch = mind.Char;
        var party = Parties?.FindParty(ch.Uid);
        Character? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in _world.GetCharsInRange(leader.Position, 10))
        {
            if (c == ch || c == leader || c.IsDead || c.IsDeleted || c.FightTarget != leader.Uid) continue;
            if (party != null && party.IsMember(c.Uid)) continue;
            if (mind.Avoids(c, now)) continue;
            if (c.IsPlayer && Clients.GameClient.ComputeNotoriety(_world, ch, c) == NotoGood) continue;
            int d = c.Position.GetDistanceTo(ch.Position);
            if (d < bestDist) { best = c; bestDist = d; }
        }
        return best;
    }

    /// <summary>A party bot looks over its party every second and a half: a member's
    /// ghost is raised (a caster with Resurrection), a poisoned member cured, the worst
    /// wounded member under 70% healed - Greater Heal or Heal from a caster, else a
    /// bandage from one with Healing who stands within two tiles. All through the
    /// bot's client, so mana, reagents, skill and delays apply.</summary>
    private bool TryTendParty(BotMind mind, long now)
    {
        if (now < mind.NextTendMs || mind.Client == null) return false;
        mind.NextTendMs = now + 1500;
        var ch = mind.Char;
        var party = Parties?.FindParty(ch.Uid);
        // Its party, and the player it goes about with.
        var mate = mind.Companion is { HasClient: true } p ? p : null;
        if ((party == null && mate == null) || ch.TryGetCastingSpell(out _)) return false;
        Character? ghost = null, poisoned = null, worst = null;
        int worstPct = 70;
        IEnumerable<Serial> members = party?.Members ?? [];
        if (mate != null && (party == null || !party.IsMember(mate.Uid))) members = members.Append(mate.Uid);
        foreach (var uid in members)
        {
            if (uid == ch.Uid || _world.FindChar(uid) is not { IsDeleted: false } c || c.MapIndex != ch.MapIndex) continue;
            if (c.Position.GetDistanceTo(ch.Position) > 10 || !_world.CanSeeLOS(ch.Position, c.Position)) continue;
            if (c.IsDead) { ghost ??= c; continue; }
            if (c.IsPoisoned) { poisoned ??= c; continue; }
            int pct = c.MaxHits > 0 ? c.Hits * 100 / c.MaxHits : 100;
            if (pct < worstPct) { worst = c; worstPct = pct; }
        }
        var client = mind.Client;
        if (ghost != null && CanRaise(mind, now))
        {
            Raise(mind, ghost, null, now);
            return true;
        }
        if (poisoned != null && mind.IsCaster && ch.Mana >= 6 && HasSpell(ch, SpellType.Cure))
        {
            client.HandleCastSpell(SpellType.Cure, poisoned.Uid.Value);
            mind.NextTendMs = now + 2500;
            WindowPartyTends++;
            return true;
        }
        if (worst == null) return false;
        SpellType? heal = !mind.IsCaster ? null
            : ch.Mana >= 11 && HasSpell(ch, SpellType.GreaterHeal) ? SpellType.GreaterHeal
            : ch.Mana >= 4 && HasSpell(ch, SpellType.Heal) ? SpellType.Heal : null;
        if (heal != null)
        {
            client.HandleCastSpell(heal.Value, worst.Uid.Value);
            mind.NextTendMs = now + 2500;
            WindowPartyTends++;
            return true;
        }
        if (ch.GetSkill(SkillType.Healing) >= 500 && worst.Position.GetDistanceTo(ch.Position) <= 2 &&
            FindInPack(ch, i => i.ItemType == ItemType.Bandage) is { } bandage)
        {
            client.HandleDoubleClick(bandage.Uid.Value);
            if (client.HasPendingTarget)
                client.HandleTargetResponse(0, client.ActiveTargetCursorId, worst.Uid.Value, worst.X, worst.Y, worst.Z, 0);
            mind.NextTendMs = now + 6000;
            WindowPartyTends++;
            return true;
        }
        return false;
    }

    public long WindowPartyTends { get; private set; }
    public long WindowPartyOrders { get; private set; }

    /// <summary>An order to the bots of the speaker's party - wait, follow, leave, how
    /// are you holding up, heal me - said aloud or in party chat. A bot named in the
    /// line takes it alone, else all of them do; one of them says it is on it (each
    /// tells its own hit points and mana). False when the line is no order, or the
    /// speaker has no bot in a party.</summary>
    private bool TryPartyCommand(Character speaker, BotIntent intent, string text, bool viaParty, long now, string lang)
    {
        if (intent.Kind is not (BotIntentKind.Stay or BotIntentKind.Follow or BotIntentKind.LeaveParty or
                BotIntentKind.Status or BotIntentKind.Heal))
            return false;
        var party = Parties?.FindParty(speaker.Uid);
        if (party == null) return false;
        var bots = new List<BotMind>();
        foreach (var uid in party.Members)
            if (uid != speaker.Uid && _minds.TryGetValue(uid.Value, out var m) && !m.Char.IsDead &&
                (viaParty || m.Char.Position.GetDistanceTo(speaker.Position) <= HearRange))
                bots.Add(m);
        if (bots.Count == 0) return false;
        var words = BotIntents.Words(text);
        var named = bots.Where(m => words.Contains(FirstName(m.Char).ToLowerInvariant())).ToList();
        var to = named.Count > 0 ? named : bots;
        WindowPartyOrders++;
        if (intent.Kind == BotIntentKind.Heal)
        {
            var healer = to.FirstOrDefault(m => m.IsCaster) ?? to[0];
            var (category, action) = AnswerHeal(healer, speaker, now);
            QueueReply(healer, category, now, to: speaker, lang: lang, fallback: null, action: action, party: viaParty);
            return true;
        }
        bool first = true;
        foreach (var m in to)
        {
            string category;
            Dictionary<string, string>? tokens = null;
            Action? action = null;
            switch (intent.Kind)
            {
                case BotIntentKind.Stay:
                    m.HoldUntilMs = now + 600_000;
                    m.HoldAt = m.Char.Position;
                    category = "party_stay";
                    break;
                case BotIntentKind.Follow:
                    m.HoldUntilMs = 0;
                    m.FollowPausedUntilMs = 0;
                    m.FollowCloseMs = now;
                    category = "party_follow";
                    break;
                case BotIntentKind.LeaveParty:
                    var client = m.Client;
                    action = () => client?.HandlePartyLeave();
                    category = "party_leave";
                    break;
                default:
                    var c = m.Char;
                    tokens = new Dictionary<string, string>
                    {
                        ["hp"] = c.Hits.ToString(), ["maxhp"] = c.MaxHits.ToString(),
                        ["mana"] = c.Mana.ToString(), ["maxmana"] = c.MaxMana.ToString(),
                    };
                    category = "party_status";
                    break;
            }
            m.Note(now, $"party order from {speaker.GetName()}: {intent.Kind}");
            if (first || intent.Kind == BotIntentKind.Status)
                QueueReply(m, category, now, to: speaker, lang: lang, fallback: null, tokens: tokens, action: action,
                    party: viaParty);
            else
                action?.Invoke();
            first = false;
        }
        return true;
    }

    /// <summary>Party chat from a player. An order goes to the party's bots as said
    /// aloud; anything else is answered in party chat by the bot named, the one told
    /// privately, else - for a question or a greeting - by one of them.</summary>
    public void OnPartyChat(Character speaker, string text, Serial privateTo)
    {
        if (speaker.IsPlayerBot || string.IsNullOrWhiteSpace(text) || _minds.Count == 0) return;
        var party = Parties?.FindParty(speaker.Uid);
        if (party == null) return;
        long now = NowMs();
        var w = WatcherOf(speaker, now);
        w.Language = Intents.DetectLanguage(text) ?? w.Language;
        string lang = w.Language ?? Chat.Language;
        var intent = InContext(w, Intents.Parse(text), text, now);
        if (TryPartyCommand(speaker, intent, text, viaParty: true, now, lang)) return;
        var bots = new List<BotMind>();
        foreach (var uid in party.Members)
            if (uid != speaker.Uid && _minds.TryGetValue(uid.Value, out var m) && !m.Char.IsDead) bots.Add(m);
        if (bots.Count == 0) return;
        var words = BotIntents.Words(text);
        var target = bots.FirstOrDefault(m => m.Char.Uid == privateTo) ??
                     bots.FirstOrDefault(m => words.Contains(FirstName(m.Char).ToLowerInvariant()));
        bool named = target != null;
        if (target == null)
        {
            if (intent.Kind == BotIntentKind.None && (w.Focus == null || !bots.Contains(w.Focus))) return;
            target = w.Focus != null && bots.Contains(w.Focus) ? w.Focus : bots[_rng.Next(bots.Count)];
        }
        w.Focus = target;
        w.FocusUntilMs = now + FocusMs;
        Answer(target, speaker, w, intent, text, named, now, lang, party: true);
    }
    private Character? PartyLeader(BotMind mind)
    {
        var party = Parties?.FindParty(mind.Char.Uid);
        if (party == null || party.Master == mind.Char.Uid) return null;
        var leader = _world.FindChar(party.Master);
        return leader is { IsDeleted: false } && leader.HasClient ? leader : null;
    }

    /// <summary>One act of party life. Returns false when the bot is not following
    /// anyone, so its own plans go on.</summary>
    private bool ActFollow(BotMind mind, long now)
    {
        var ch = mind.Char;
        var party = Parties?.FindParty(ch.Uid);
        if (party == null) return false;
        var leader = PartyLeader(mind);
        // Told to wait: it waits (and does not count the leader gone meanwhile), but
        // still stands up for the leader and fights what comes at it.
        if (now < mind.HoldUntilMs)
        {
            mind.LeaderSeenMs = now;
            if (leader != null && AttackerOf(leader, mind, now) is { } at && at.Position.GetDistanceTo(ch.Position) <= 10)
            {
                Engage(mind, at, now);
                return true;
            }
            if (ch.Position.GetDistanceTo(mind.HoldAt) > 1)
            {
                bool back = StepToward(mind, mind.HoldAt, run: false, searchWithin: 20);
                mind.NextActMs = now + StepDelay(mind, back, false, now);
            }
            else
                mind.NextActMs = now + 1000;
            return true;
        }
        bool near = leader != null && leader.MapIndex == ch.MapIndex && leader.Position.GetDistanceTo(ch.Position) <= 40;
        if (near) mind.LeaderSeenMs = now;
        else
        {
            if (now - mind.LeaderSeenMs > LeaderGoneMs)
            {
                mind.Client?.HandlePartyLeave();
                if (_rng.Next(100) < 50) Speak(mind, "party_disband", fallback: null);
            }
            return false;
        }

        // Kept out of reach of the leader for a while: its own plans for a time.
        if (now < mind.FollowPausedUntilMs) return false;

        // The leader's fight is the party's fight: what it attacks (a player only when
        // that is no crime, not blue to the bot), and what attacks it.
        if (leader!.FightTarget.IsValid && _world.FindChar(leader.FightTarget) is { IsDead: false, IsDeleted: false } foe &&
            foe != ch && mind.Client != null && !mind.Avoids(foe, now) && !party.IsMember(foe.Uid) &&
            (!foe.IsPlayer || Clients.GameClient.ComputeNotoriety(_world, ch, foe) != NotoGood))
        {
            Engage(mind, foe, now);
            return true;
        }
        if (mind.Client != null && AttackerOf(leader, mind, now) is { } attacker)
        {
            Engage(mind, attacker, now);
            return true;
        }

        int dist = ch.Position.GetDistanceTo(leader.Position);
        if (mind.FollowCloseMs == 0 || dist <= FollowRange + 2) mind.FollowCloseMs = now;
        else if (now - mind.FollowCloseMs > 30_000)
        {
            // Half a minute and it never got near: the leader is where it cannot follow.
            mind.FollowPausedUntilMs = now + 30_000;
            mind.FollowCloseMs = now + 30_000;
            WindowGiveUps++;
            mind.Note(now, $"cannot keep up with {leader.GetName()} at {leader.Position}; goes its own way a while");
            return false;
        }
        if (dist > FollowRange)
        {
            bool run = dist > FollowRange + 3;
            bool stepped = StepToward(mind, leader.Position, run, searchWithin: 40);
            mind.NextActMs = now + StepDelay(mind, stepped, run, now);
        }
        else
            mind.NextActMs = now + 400;
        return true;
    }
}
