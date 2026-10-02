using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Party;

/// <summary>
/// What the party membership operations need from the outside world: character
/// lookup, the trigger chain, packet and system-message delivery, the client-active
/// test and the clock. A client handler builds one around its own engines; script
/// verbs and other server-side callers use <see cref="PartyManager.DefaultIo"/>.
/// </summary>
public sealed class PartyIo
{
    public required Func<Serial, Character?> FindChar { get; init; }
    public TriggerDispatcher? Triggers { get; init; }
    public Action<Serial, PacketWriter>? Send { get; init; }
    public Action<Character, string>? SysMessage { get; init; }

    /// <summary>CChar::IsClientActive - the character has a connected client.</summary>
    public Func<Character, bool> IsClientActive { get; init; } = static ch => ch.IsOnline && !ch.IsDeleted;

    /// <summary>CChar::CanSee between two characters.</summary>
    public Func<Character, Character, bool> CanSee { get; init; } = static (viewer, target) => viewer.CanSee(target);

    /// <summary>CClient::addReSync for a character's client: sent after every party
    /// remove packet (CPartyDef::SendMemberMsg, CParty.cpp:170). Wired at startup.</summary>
    public static Action<Character>? ResyncCharacter { get; set; }

    /// <summary>The server clock in milliseconds (CWorldGameTime::GetCurrentTime).</summary>
    public Func<long> NowMs { get; init; } = static () => Environment.TickCount64;

    /// <summary>Delivery through the static owner hooks - the path script verbs use.</summary>
    public static PartyIo ForWorld(GameWorld world, TriggerDispatcher? triggers) => new()
    {
        FindChar = world.FindChar,
        Triggers = triggers,
        Send = (uid, packet) =>
        {
            if (world.FindChar(uid) is { } ch)
                Character.SendPacketToOwner?.Invoke(ch, packet);
        },
        SysMessage = static (ch, text) => Character.SendOwnerMessage?.Invoke(ch, text),
        NowMs = () => world.GameClockMs,
    };

    /// <summary>Delivery through one client's engines: its own system messages go to
    /// that client directly, everyone else's through the owner hook.</summary>
    public static PartyIo ForClient(GameWorld world, TriggerDispatcher? triggers,
        Action<Serial, PacketWriter>? send, Character? self, Action<string> selfMessage) => new()
    {
        FindChar = world.FindChar,
        Triggers = triggers,
        Send = send,
        SysMessage = (ch, text) =>
        {
            if (self != null && ReferenceEquals(ch, self))
                selfMessage(text);
            else
                Character.SendOwnerMessage?.Invoke(ch, text);
        },
        NowMs = () => world.GameClockMs,
    };
}

/// <summary>
/// The party membership contract of Source-X CPartyDef (CParty.cpp) and
/// CClient::OnTarg_Party_Add (CClientTarg.cpp:2398). Every entry point - the party
/// protocol, the context menu, the script verbs and the disconnect path - goes through
/// these, so triggers, vetoes, the invitation record and the party/waypoint packets
/// come from one place.
/// </summary>
public sealed partial class PartyManager
{
    /// <summary>The invitation record lives on the INVITER: the uid it last invited
    /// and the time before which it may not invite again (CClientTarg.cpp:2481).</summary>
    public const string LastInviteTag = "PARTY_LASTINVITE";
    public const string LastInviteTimeTag = "PARTY_LASTINVITETIME";

    /// <summary>The I/O used by callers that are not a client (script verbs).
    /// Wired at startup; null in a headless world.</summary>
    public PartyIo? DefaultIo { get; set; }

    private static TriggerResult Fire(PartyIo io, Character ch, CharTrigger trigger, Character? src, long n1 = 0) =>
        io.Triggers?.FireCharTrigger(ch, trigger, new TriggerArgs { CharSrc = src, N1 = n1 })
        ?? TriggerResult.Default;

    private static bool TryGetUidTag(Character ch, string key, out uint uid)
    {
        uid = 0;
        if (!ch.TryGetTag(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        if (!ScriptNumber.TryParseToken(raw, out long value))
            return false;
        uid = unchecked((uint)value);
        return true;
    }

    // ------------------------------------------------------------------
    // Invitation (CClient::OnTarg_Party_Add, CClientTarg.cpp:2398-2486)
    // ------------------------------------------------------------------

    /// <summary>Invite <paramref name="target"/> into the inviter's party. The
    /// record is written on the inviter, so a new invitation replaces the previous
    /// outgoing one.</summary>
    public bool Invite(Character inviter, Character? target, PartyIo io)
    {
        if (target == null)
        {
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartySelect));
            return false;
        }
        if (ReferenceEquals(target, inviter))
        {
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyNoSelfAdd));
            return false;
        }
        if (!io.IsClientActive(target))
        {
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyNonpcadd));
            return false;
        }

        var myParty = FindParty(inviter.Uid);
        if (myParty != null)
        {
            if (myParty.Master != inviter.Uid)
            {
                io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyNotleader));
                return false;
            }
            if (myParty.IsFull)
            {
                io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyIsFull));
                return false;
            }
        }

        // A GM adds a lower-privileged player outright (CClientTarg.cpp:2437).
        if (inviter.IsGmMode && target.PrivLevel < inviter.PrivLevel)
        {
            AcceptEvent(target, inviter.Uid, forced: true, io);
            return true;
        }

        var theirParty = FindParty(target.Uid);
        if (theirParty != null)
        {
            if (ReferenceEquals(theirParty, myParty))
            {
                io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyAlreadyInThis));
                return true;
            }
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyAlreadyIn));
            return false;
        }

        if (target.TryGetTag("PARTY_AUTODECLINEINVITE", out string? autoDecline) &&
            ScriptNumber.TryParseToken(autoDecline ?? "", out long declines) && declines != 0)
        {
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyAutodecline));
            return false;
        }

        long now = io.NowMs();
        if (inviter.TryGetTag(LastInviteTimeTag, out string? timeRaw) &&
            ScriptNumber.TryParseToken(timeRaw ?? "", out long notBefore) && now - notBefore <= 0)
        {
            io.SysMessage?.Invoke(inviter, ServerMessages.Get(Msg.PartyAddTooFast));
            return false;
        }

        if (Fire(io, target, CharTrigger.PartyInvite, inviter) == TriggerResult.True)
            return false;

        io.SysMessage?.Invoke(inviter, ServerMessages.GetFormatted(Msg.PartyInvite, target.Name ?? ""));
        io.SysMessage?.Invoke(target, ServerMessages.GetFormatted(Msg.PartyInviteTarg, inviter.Name ?? ""));

        inviter.SetTag(LastInviteTag, target.Uid.Value.ToString());
        inviter.SetTag(LastInviteTimeTag, (now + Random.Shared.Next(2, 6) * 1000L).ToString());

        io.Send?.Invoke(target.Uid, new PacketPartyInvitation(inviter.Uid.Value));
        return true;
    }

    /// <summary>CPartyDef::DeclineEvent (CParty.cpp:420): only the invitation the
    /// inviter still holds for this character can be declined, and declining it
    /// clears it.</summary>
    public bool DeclineEvent(Character? decliner, Serial inviterUid, PartyIo io)
    {
        var inviter = io.FindChar(inviterUid);
        if (inviter == null || decliner == null || inviterUid == decliner.Uid)
            return false;
        if (!TryGetUidTag(inviter, LastInviteTag, out uint invited) || invited != decliner.Uid.Value)
            return false;

        inviter.RemoveTag(LastInviteTag);
        io.SysMessage?.Invoke(decliner, ServerMessages.GetFormatted(Msg.PartyDecline2, inviter.Name ?? ""));
        io.SysMessage?.Invoke(inviter, ServerMessages.GetFormatted(Msg.PartyDecline1, decliner.Name ?? ""));
        return true;
    }

    /// <summary>CPartyDef::AcceptEvent (CParty.cpp:443). Both sides need an active
    /// client. The ordinary accept answers the invitation the inviter holds for this
    /// character (and consumes it) and needs the inviter to still see them; a forced
    /// add skips only those two checks and the leader-only rule, and pulls the
    /// character out of any other party first.</summary>
    public bool AcceptEvent(Character? accept, Serial inviterUid, bool forced, PartyIo io, bool sendMessages = true)
    {
        var inviter = io.FindChar(inviterUid);
        if (inviter == null || !io.IsClientActive(inviter) || accept == null ||
            !io.IsClientActive(accept) || ReferenceEquals(inviter, accept))
            return false;

        var party = FindParty(inviter.Uid);
        if (!forced)
        {
            if (!TryGetUidTag(inviter, LastInviteTag, out uint invited) || invited != accept.Uid.Value)
                return false;
            inviter.RemoveTag(LastInviteTag);
            if (!io.CanSee(inviter, accept))
                return false;
        }

        var current = FindParty(accept.Uid);
        bool stillListedElsewhere = false;
        if (current != null)
        {
            if (ReferenceEquals(current, party))
                return true;
            if (!forced)
                return false;
            // Source-X drops the character's party pointer whether or not the removal
            // went through (CParty.cpp:470-471); a refused removal leaves them still
            // listed in the old party, which detaches them the next time it sends
            // them anything (SendMemberMsg, CParty.cpp:156).
            RemoveMember(current, accept.Uid, accept.Uid, io);
            stillListedElsewhere = current.IsMember(accept.Uid);
        }

        if (Fire(io, accept, CharTrigger.PartyAdd, inviter) == TriggerResult.True)
            return false;

        string joined = ServerMessages.GetFormatted(Msg.PartyJoined, accept.Name ?? "");
        if (party == null)
        {
            // new CPartyDef(inviter, accept): the inviter is the master.
            party = CreateParty(inviter.Uid);
            party.AddMember(accept.Uid);
            if (stillListedElsewhere)
                _partyPointer[accept.Uid] = party;
            SendAddList(party, io);
            if (sendMessages)
                io.SysMessage?.Invoke(inviter, joined);
        }
        else
        {
            if (party.IsFull || (!forced && party.Master != inviter.Uid))
                return false;
            if (sendMessages)
                SysMessageAll(party, joined, io);
            party.AddMember(accept.Uid);
            if (stillListedElsewhere)
                _partyPointer[accept.Uid] = party;
            SendAddList(party, io);
        }

        if (sendMessages)
            io.SysMessage?.Invoke(accept, ServerMessages.Get(Msg.PartyAdded));

        // Sphere 0.56T (stock and custom versions): @PartyJoin on the character who has
        // just joined, SRC = the one who brought them in. It follows the join - the
        // pack's body adds the in-party events from there - so it cannot refuse it;
        // the Source-X @PartyAdd above is still the veto.
        io.Triggers?.FireCharTriggerIfUsed(accept, "PartyJoin", new TriggerArgs { CharSrc = inviter });
        return true;
    }

    // ------------------------------------------------------------------
    // Removal and disband (CParty.cpp:296-418)
    // ------------------------------------------------------------------

    /// <summary>CPartyDef::RemoveMember. <paramref name="commandUid"/> is the one asking:
    /// a member may remove only themselves, the master anyone. With
    /// <paramref name="disband"/> (the default, as in CParty.h:94) removing the master
    /// disbands the party; without it the next member is promoted.</summary>
    public bool RemoveMember(PartyDef party, Serial removeUid, Serial commandUid, PartyIo io, bool disband = true)
    {
        if (party.MemberCount <= 0)
            return false;

        Serial master = party.Master;
        if (removeUid != commandUid && commandUid != master)
            return false;

        var removeChar = io.FindChar(removeUid);
        if (removeChar == null || !party.IsMember(removeUid))
            return false;
        if (disband && removeUid == master)
            return Disband(party, master, io);

        var src = io.FindChar(commandUid);
        if (src != null && Fire(io, removeChar, CharTrigger.PartyRemove, src) == TriggerResult.True)
            return false;
        if (Fire(io, removeChar, CharTrigger.PartyLeave, removeChar) == TriggerResult.True)
            return false;

        string? changeLeader = null;
        if (removeUid == master)
        {
            if (party.MemberCount < 2)
                return Disband(party, master, io);
            var newMaster = io.FindChar(party.Members[1]);
            if (newMaster == null)
                return Disband(party, master, io);
            party.SetMaster(newMaster.Uid);
            SendAddList(party, io);
            changeLeader = ServerMessages.GetFormatted(Msg.PartyChangeLeader, newMaster.Name ?? "");
        }

        // The one leaving is told first, with an empty list, then detached.
        SendRemoveListFor(party, removeChar.Uid, io);
        DetachChar(party, removeChar, io);
        io.SysMessage?.Invoke(removeChar, ServerMessages.Get(Msg.PartyLeave2));

        SysMessageAll(party, ServerMessages.GetFormatted(Msg.PartyLeave1, removeChar.Name ?? ""), io);

        if (party.MemberCount <= 1)
        {
            SysMessageAll(party, ServerMessages.Get(Msg.PartyLeaveLastPerson), io);
            // The master captured on entry, as upstream passes it: after the leader
            // left and somebody was promoted it no longer matches, Disband refuses,
            // and the promoted member stays in a party of one.
            return Disband(party, master, io);
        }

        if (changeLeader != null)
            SysMessageAll(party, changeLeader, io);

        var remaining = party.Members.Select(m => m.Value).ToArray();
        SendAll(party, new PacketPartyRemoveMember(removeUid.Value, remaining), io, isRemove: true);
        return true;
    }

    /// <summary>CPartyDef::Disband (CParty.cpp:375): asked of the master, whose
    /// @PartyDisband may refuse; then every member, last first, gets @PartyRemove with
    /// ARGN1=1 (SRC = the master), the empty member list, and is detached.</summary>
    public bool Disband(PartyDef party, Serial masterUid, PartyIo io)
    {
        if (party.MemberCount <= 0 || party.Master != masterUid)
            return false;

        var master = io.FindChar(masterUid);
        if (master != null && Fire(io, master, CharTrigger.PartyDisband, master) == TriggerResult.True)
            return false;

        SysMessageAll(party, ServerMessages.Get(Msg.PartyDisbanded), io);

        var formerMembers = party.Members.ToList();
        for (int i = formerMembers.Count - 1; i >= 0; i--)
        {
            var ch = io.FindChar(formerMembers[i]);
            if (ch == null)
                continue;
            Fire(io, ch, CharTrigger.PartyRemove, master, n1: 1);
            SendRemoveListFor(party, ch.Uid, io);
            DetachChar(party, ch, io);
        }

        party.Disband();
        _parties.Remove(party);
        return true;
    }

    /// <summary>CPartyDef::DetachChar (CParty.cpp:46): the members still listed drop
    /// the character's map pin (UpdateWaypointAll: active clients only, never the
    /// character itself), and the character's own invitation record is cleared.</summary>
    private static void DetachChar(PartyDef party, Character ch, PartyIo io)
    {
        if (!party.RemoveMember(ch.Uid))
            return;
        var dropPin = new PacketWaypointRemove(ch.Uid.Value);
        foreach (var uid in party.Members)
            if (io.FindChar(uid) is { } member && io.IsClientActive(member))
                io.Send?.Invoke(uid, dropPin);
        ch.RemoveTag(LastInviteTag);
        ch.RemoveTag(LastInviteTimeTag);
        Character.NotoSaveUpdate?.Invoke(ch);
    }

    /// <summary>CChar::m_pParty for a character still listed in a party that refused
    /// to let them go when they were force-added elsewhere: the newer party wins
    /// <see cref="FindParty"/> until the old one detaches them.</summary>
    private readonly Dictionary<Serial, PartyDef> _partyPointer = [];

    /// <summary>CPartyDef::SendMemberMsg (CParty.cpp:145): a member whose party pointer
    /// names another party is detached from this one instead; an active client gets
    /// the packet, and after a remove packet a full resync (addReSync). False when the
    /// member was detached.</summary>
    private bool SendMember(PartyDef party, Serial dest, PacketWriter packet, PartyIo io, bool isRemove)
    {
        var ch = io.FindChar(dest);
        if (!ReferenceEquals(FindParty(dest), party))
        {
            if (ch != null && party.IsMember(dest))
            {
                DetachChar(party, ch, io);
                return false;
            }
            return true;
        }
        if (ch == null || !io.IsClientActive(ch))
            return true;
        io.Send?.Invoke(dest, packet);
        if (isRemove)
            PartyIo.ResyncCharacter?.Invoke(ch);
        return true;
    }

    private void SendAll(PartyDef party, PacketWriter packet, PartyIo io, bool isRemove = false)
    {
        foreach (var uid in party.Members.ToList())
            SendMember(party, uid, packet, io, isRemove);
    }

    private void SendAddList(PartyDef party, PartyIo io)
    {
        if (party.MemberCount <= 0)
            return;
        SendAll(party, new PacketPartyMemberList(party.Members.Select(m => m.Value).ToArray()), io);
    }

    private void SendRemoveListFor(PartyDef party, Serial removed, PartyIo io) =>
        SendMember(party, removed, new PacketPartyRemoveMember(removed.Value, Array.Empty<uint>()), io, isRemove: true);

    private static void SysMessageAll(PartyDef party, string text, PartyIo io)
    {
        if (io.SysMessage == null)
            return;
        foreach (var uid in party.Members.ToList())
            if (io.FindChar(uid) is { } ch)
                io.SysMessage(ch, text);
    }
}
