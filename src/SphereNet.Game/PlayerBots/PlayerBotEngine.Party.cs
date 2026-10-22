using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Party;

namespace SphereNet.Game.PlayerBots;

/// <summary>Grouping up with players. A player's party invitation is answered the way
/// a client answers it (the accept sub-command of 0xBF 0x06); once in, the bot stays
/// at its leader's side, joins the fight the leader picks, and leaves the party when
/// the leader has been gone for a while.</summary>
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

    /// <summary>The real player leading this bot's party, or null.</summary>
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

        // The leader's fight is the party's fight.
        if (leader!.FightTarget.IsValid && _world.FindChar(leader.FightTarget) is { IsDead: false, IsDeleted: false } foe &&
            foe != ch && !foe.IsPlayer && mind.Client != null && !mind.Avoids(foe, now))
        {
            Engage(mind, foe, now);
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
