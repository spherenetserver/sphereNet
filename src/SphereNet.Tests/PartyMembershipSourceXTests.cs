using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// One party membership contract for every entry point (Source-X CPartyDef,
/// CParty.cpp; CClient::OnTarg_Party_Add, CClientTarg.cpp:2398):
/// the invitation record lives on the INVITER (PARTY_LASTINVITE), both sides need an
/// active client to accept, a leader's disband runs @PartyDisband on the leader and
/// then @PartyRemove ARGN1=1 on every member, and the PARTY.* script verbs reach the
/// same triggers, vetoes and packets as the client protocol.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PartyMembershipSourceXTests
{
    private const ushort PartySub = 0x0006;

    private sealed class Bench
    {
        public required GameWorld World { get; init; }
        public required PartyManager Parties { get; init; }
        public required TriggerDispatcher Triggers { get; init; }
        public Dictionary<string, (GameClient Client, Character Ch)> P { get; } = new();
        public List<(Serial To, PacketWriter Packet)> Sent { get; } = [];
        public List<Serial> Resynced { get; } = [];
        public GameClient C(string n) => P[n].Client;
        public Character Ch(string n) => P[n].Ch;
    }

    private static Bench Setup(params string[] names)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var bench = new Bench { World = world, Parties = new PartyManager(), Triggers = new TriggerDispatcher() };
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);
        short x = 100;
        int port = 8801;
        foreach (var name in names)
        {
            var client = TestHarness.CreateClient(lf, world, accounts, port++);
            client.SetEngines(partyManager: bench.Parties, triggerDispatcher: bench.Triggers);
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.IsOnline = true;
            ch.Name = name;
            world.PlaceCharacter(ch, new Point3D(x++, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch);
            client.SendToChar = (to, packet) => bench.Sent.Add((to, packet));
            bench.P[name] = (client, ch);
        }
        // Script verbs use the manager's own I/O, as the server wires it.
        bench.Parties.DefaultIo = PartyIo.ForWorld(world, bench.Triggers);
        Character.ResolvePartyManager = () => bench.Parties;
        Character.ResolvePartyFinder = uid => bench.Parties.FindParty(uid);
        Character.SendPacketToOwner = (ch, packet) => bench.Sent.Add((ch.Uid, packet));
        PartyIo.ResyncCharacter = ch => bench.Resynced.Add(ch.Uid);
        return bench;
    }

    private static byte[] WithUid(byte cmd, Serial uid) =>
    [
        cmd,
        (byte)(uid.Value >> 24), (byte)(uid.Value >> 16),
        (byte)(uid.Value >> 8), (byte)uid.Value,
    ];

    private static void Invite(Bench b, string inviter, string target, bool advanceClock = true)
    {
        if (advanceClock)
            b.World.SetGameClockMs(b.World.GameClockMs + 6000);
        b.C(inviter).HandleExtendedCommand(PartySub, WithUid(1, b.Ch(target).Uid));
    }

    private static void Accept(Bench b, string who, string inviter) =>
        b.C(who).HandleExtendedCommand(PartySub, WithUid(8, b.Ch(inviter).Uid));

    private static void Decline(Bench b, string who, string inviter) =>
        b.C(who).HandleExtendedCommand(PartySub, WithUid(9, b.Ch(inviter).Uid));

    private static void Remove(Bench b, string who, string member) =>
        b.C(who).HandleExtendedCommand(PartySub, WithUid(2, b.Ch(member).Uid));

    private static Bench PartyOfThree()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        Invite(b, "Alice", "Carol");
        Accept(b, "Carol", "Alice");
        Assert.Equal(3, b.Parties.FindParty(b.Ch("Alice").Uid)!.MemberCount);
        b.Sent.Clear();
        b.Resynced.Clear();
        return b;
    }

    private static uint[] RemainingOf(PacketPartyRemoveMember packet) =>
        (uint[])typeof(PacketPartyRemoveMember)
            .GetField("_remainingMembers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(packet)!;

    private static uint PinOf(PacketWaypointRemove packet) =>
        (uint)typeof(PacketWaypointRemove)
            .GetField("_serial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(packet)!;

    private static List<(Serial To, uint Pin)> Pins(Bench b) =>
        b.Sent.Where(s => s.Packet is PacketWaypointRemove)
            .Select(s => (s.To, PinOf((PacketWaypointRemove)s.Packet))).ToList();

    // --- P01: the invitation record is the inviter's ---------------------

    [Fact]
    public void ASecondInvitationReplacesTheInvitersFirst()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Invite(b, "Alice", "Carol");

        Accept(b, "Bob", "Alice");
        Assert.Null(b.Parties.FindParty(b.Ch("Bob").Uid));

        Accept(b, "Carol", "Alice");
        Assert.True(b.Parties.FindParty(b.Ch("Alice").Uid)?.IsMember(b.Ch("Carol").Uid));
    }

    [Fact]
    public void TheRecordIsWrittenOnTheInviterAndConsumedByTheAnswer()
    {
        var b = Setup("Alice", "Bob");
        Invite(b, "Alice", "Bob");

        Assert.True(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTag, out string? invited));
        Assert.Equal(b.Ch("Bob").Uid.Value.ToString(), invited);
        Assert.True(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTimeTag, out _));
        Assert.False(b.Ch("Bob").TryGetTag(PartyManager.LastInviteTag, out _));

        Accept(b, "Bob", "Alice");
        Assert.False(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTag, out _));
    }

    [Fact]
    public void InvitingAgainBeforeTheWaitIsOverIsTooFast()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Invite(b, "Alice", "Carol", advanceClock: false);

        // The second invitation never went out: the record still names Bob.
        Assert.True(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTag, out string? invited));
        Assert.Equal(b.Ch("Bob").Uid.Value.ToString(), invited);
    }

    [Fact]
    public void DecliningClearsTheInvitersRecordAndTheInvitationIsGone()
    {
        var b = Setup("Alice", "Bob");
        Invite(b, "Alice", "Bob");

        Decline(b, "Bob", "Alice");
        Assert.False(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTag, out _));

        Accept(b, "Bob", "Alice");
        Assert.Null(b.Parties.FindParty(b.Ch("Bob").Uid));
    }

    [Fact]
    public void ADeclineOfAnInvitationTheInviterNoLongerHoldsLeavesTheNewOneAlone()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Invite(b, "Alice", "Carol");

        Decline(b, "Bob", "Alice");

        Accept(b, "Carol", "Alice");
        Assert.True(b.Parties.FindParty(b.Ch("Alice").Uid)?.IsMember(b.Ch("Carol").Uid));
    }

    // --- P02: both sides need an active client ---------------------------

    [Fact]
    public void AnInviterWhoLoggedOutCannotBeJoined()
    {
        var b = Setup("Alice", "Bob");
        Invite(b, "Alice", "Bob");

        b.C("Alice").OnDisconnect();
        Assert.False(b.Ch("Alice").IsOnline);

        Accept(b, "Bob", "Alice");

        Assert.Null(b.Parties.FindParty(b.Ch("Bob").Uid));
        Assert.Null(b.Parties.FindParty(b.Ch("Alice").Uid));
    }

    [Fact]
    public void AnAcceptorWithoutAnActiveClientCannotJoin()
    {
        var b = Setup("Alice", "Bob");
        Invite(b, "Alice", "Bob");
        b.Ch("Bob").IsOnline = false;

        Accept(b, "Bob", "Alice");

        Assert.Null(b.Parties.FindParty(b.Ch("Alice").Uid));
    }

    // --- P03: the leader's disband ---------------------------------------

    [Fact]
    public void TheLeadersDisbandAsksTheLeaderAndTellsEveryMember()
    {
        var b = PartyOfThree();
        var disbandOn = new List<(Serial On, Serial? Src)>();
        var removeOn = new List<(Serial On, Serial? Src, long N1)>();
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyDisband", (obj, args) =>
        {
            disbandOn.Add((((Character)obj).Uid, args.CharSrc?.Uid));
            return TriggerResult.Default;
        });
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyRemove", (obj, args) =>
        {
            removeOn.Add((((Character)obj).Uid, args.CharSrc?.Uid, args.N1));
            return TriggerResult.Default;
        });
        var alice = b.Ch("Alice").Uid;

        Remove(b, "Alice", "Alice");

        Assert.Equal([(alice, (Serial?)alice)], disbandOn);
        Assert.Equal(3, removeOn.Count);
        Assert.All(removeOn, r => { Assert.Equal(1, r.N1); Assert.Equal(alice, r.Src); });
        Assert.Equal(new[] { "Alice", "Bob", "Carol" }.Select(n => b.Ch(n).Uid).OrderBy(u => u.Value),
            removeOn.Select(r => r.On).OrderBy(u => u.Value));
        Assert.Equal(0, b.Parties.ActivePartyCount);

        // Every member is sent the empty member list followed by a client resync
        // (SendMemberMsg, CParty.cpp:170).
        foreach (var name in new[] { "Alice", "Bob", "Carol" })
        {
            var uid = b.Ch(name).Uid;
            Assert.Contains(b.Sent, s => s.To == uid && s.Packet is PacketPartyRemoveMember r && RemainingOf(r).Length == 0);
            Assert.Equal(1, b.Resynced.Count(r => r == uid));
        }

        // Pins go only to the members still listed as each one is detached, last
        // first (DetachChar -> UpdateWaypointAll): Carol's to Alice and Bob, Bob's to
        // Alice, and nobody is left to hear about Alice.
        uint aliceV = b.Ch("Alice").Uid.Value, bobV = b.Ch("Bob").Uid.Value, carolV = b.Ch("Carol").Uid.Value;
        Assert.Equal(
            new[] { (b.Ch("Alice").Uid, carolV), (b.Ch("Bob").Uid, carolV), (b.Ch("Alice").Uid, bobV) }
                .OrderBy(p => p.Item1.Value).ThenBy(p => p.Item2),
            Pins(b).OrderBy(p => p.To.Value).ThenBy(p => p.Pin));
        Assert.DoesNotContain(Pins(b), p => p.Pin == aliceV);
    }

    [Fact]
    public void TheLeadersPartyDisbandVetoIsAskedOfTheLeader()
    {
        var b = PartyOfThree();
        var alice = b.Ch("Alice");
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyDisband",
            (obj, _) => ReferenceEquals(obj, alice) ? TriggerResult.True : TriggerResult.Default);

        Remove(b, "Alice", "Alice");

        Assert.Equal(3, b.Parties.FindParty(alice.Uid)?.MemberCount);
    }

    // --- P04: script verbs go through the same contract -------------------

    [Fact]
    public void ScriptDisbandHonoursThePartyDisbandVeto()
    {
        var b = PartyOfThree();
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyDisband", (_, _) => TriggerResult.True);

        b.Ch("Alice").TryExecuteCommand("PARTY.DISBAND", "", null!);

        Assert.Equal(1, b.Parties.ActivePartyCount);
        Assert.Equal(3, b.Parties.FindParty(b.Ch("Alice").Uid)!.MemberCount);
    }

    [Fact]
    public void ScriptDisbandRunsTheMembersRemoveWithArgn1()
    {
        var b = PartyOfThree();
        var n1 = new List<long>();
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyRemove", (_, args) => { n1.Add(args.N1); return TriggerResult.Default; });

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.DISBAND", "", null!));

        Assert.Equal([1L, 1L, 1L], n1);
        Assert.Equal(0, b.Parties.ActivePartyCount);
        Assert.Contains(b.Sent, s => s.To == b.Ch("Bob").Uid && s.Packet is PacketPartyRemoveMember);
    }

    [Fact]
    public void ScriptRemoveMemberHonoursThePartyRemoveVeto()
    {
        var b = PartyOfThree();
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyRemove", (_, _) => TriggerResult.True);

        b.Ch("Alice").TryExecuteCommand("PARTY.REMOVEMEMBER", $"0{b.Ch("Bob").Uid.Value:X}", null!);

        Assert.True(b.Parties.FindParty(b.Ch("Alice").Uid)!.IsMember(b.Ch("Bob").Uid));
    }

    [Fact]
    public void ScriptRemoveMemberOfAnOrdinaryMemberTellsTheRest()
    {
        var b = PartyOfThree();

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.REMOVEMEMBER", "@1", null!));

        var party = b.Parties.FindParty(b.Ch("Alice").Uid)!;
        Assert.Equal(2, party.MemberCount);
        Assert.False(party.IsMember(b.Ch("Bob").Uid));
        Assert.Contains(b.Sent, s => s.To == b.Ch("Carol").Uid && s.Packet is PacketPartyRemoveMember r && RemainingOf(r).Length == 2);
        Assert.Contains(b.Sent, s => s.To == b.Ch("Bob").Uid && s.Packet is PacketPartyRemoveMember r && RemainingOf(r).Length == 0);
    }

    [Fact]
    public void ScriptRemoveMemberOfTheLeaderDisbands()
    {
        var b = PartyOfThree();
        int disbands = 0;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyDisband", (_, _) => { disbands++; return TriggerResult.Default; });

        b.Ch("Alice").TryExecuteCommand("PARTY.REMOVEMEMBER", $"0{b.Ch("Alice").Uid.Value:X}", null!);

        Assert.Equal(1, disbands);
        Assert.Equal(0, b.Parties.ActivePartyCount);
    }

    [Fact]
    public void ScriptAddMemberIsAnOrdinaryJoin()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        Serial? addSrc = null;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyAdd", (_, args) => { addSrc = args.CharSrc?.Uid; return TriggerResult.Default; });

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBER", $"0{b.Ch("Carol").Uid.Value:X}", null!));

        Assert.Equal(b.Ch("Alice").Uid, addSrc);
        Assert.Equal(3, b.Parties.FindParty(b.Ch("Alice").Uid)!.MemberCount);
        Assert.Contains(b.Sent, s => s.To == b.Ch("Carol").Uid && s.Packet is PacketPartyMemberList);
        // AcceptEvent consumed the record the verb wrote.
        Assert.False(b.Ch("Alice").TryGetTag(PartyManager.LastInviteTag, out _));
    }

    [Fact]
    public void ScriptAddMemberHonoursThePartyAddVetoAndTheActiveClientRule()
    {
        var b = Setup("Alice", "Bob", "Carol", "Dave");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyAdd",
            (obj, _) => ReferenceEquals(obj, b.Ch("Carol")) ? TriggerResult.True : TriggerResult.Default);
        b.Ch("Dave").IsOnline = false;

        b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBER", $"0{b.Ch("Carol").Uid.Value:X}", null!);
        b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBER", $"0{b.Ch("Dave").Uid.Value:X}", null!);

        Assert.Equal(2, b.Parties.FindParty(b.Ch("Alice").Uid)!.MemberCount);
    }

    [Fact]
    public void ScriptAddMemberNeedsTheMasterToSeeTheGuestButTheForcedAddDoesNot()
    {
        var b = Setup("Alice", "Bob", "Carol");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        b.World.MoveCharacter(b.Ch("Carol"), new Point3D(400, 400, 0, 0));
        string carol = $"0{b.Ch("Carol").Uid.Value:X}";

        b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBER", carol, null!);
        Assert.Null(b.Parties.FindParty(b.Ch("Carol").Uid));

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBERFORCED", carol, null!));
        Assert.True(b.Parties.FindParty(b.Ch("Alice").Uid)!.IsMember(b.Ch("Carol").Uid));
    }

    [Fact]
    public void ScriptForcedAddPullsTheGuestOutOfTheirOldPartyThroughTheRemoval()
    {
        var b = Setup("Alice", "Bob", "Carol", "Dave");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        Invite(b, "Carol", "Dave");
        Accept(b, "Dave", "Carol");
        var removed = new List<Serial>();
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyRemove", (obj, _) => { removed.Add(((Character)obj).Uid); return TriggerResult.Default; });

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBERFORCED", $"0{b.Ch("Dave").Uid.Value:X}", null!));

        Assert.Contains(b.Ch("Dave").Uid, removed);
        Assert.Equal(b.Ch("Alice").Uid, b.Parties.FindParty(b.Ch("Dave").Uid)?.Master);
        Assert.Equal(1, b.Parties.Parties.Count(p => p.IsMember(b.Ch("Dave").Uid)));
    }

    [Fact]
    public void ADisconnectingLeaderHandsThePartyOnWithTheRemoveTriggers()
    {
        var b = PartyOfThree();
        int leaves = 0;
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyLeave", (_, _) => { leaves++; return TriggerResult.Default; });

        b.C("Alice").OnDisconnect();

        Assert.Equal(1, leaves);
        var party = b.Parties.FindParty(b.Ch("Bob").Uid);
        Assert.NotNull(party);
        Assert.Equal(b.Ch("Bob").Uid, party!.Master);
        Assert.Equal(2, party.MemberCount);
    }

    [Fact]
    public void TheMemberPromotedWhenALeaderDisconnectsFromAPartyOfTwoIsLeftInAPartyOfOne()
    {
        // RemoveMember asks Disband with the master captured on entry
        // (CParty.cpp:361); after the promotion that is no longer the master, so the
        // disband is refused and the promoted member keeps a party of one.
        var b = Setup("Alice", "Bob");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");

        b.C("Alice").OnDisconnect();

        Assert.Null(b.Parties.FindParty(b.Ch("Alice").Uid));
        var party = b.Parties.FindParty(b.Ch("Bob").Uid);
        Assert.NotNull(party);
        Assert.Equal(1, party!.MemberCount);
        Assert.Equal(b.Ch("Bob").Uid, party.Master);
    }

    [Fact]
    public void AForcedAddProceedsWhenTheOldPartyRefusesToLetGo()
    {
        // AcceptEvent clears the party pointer whatever the removal answered
        // (CParty.cpp:470-471): the guest joins, still listed in the old party, which
        // detaches them the next time it sends them anything (SendMemberMsg :156).
        var b = Setup("Alice", "Bob", "Carol", "Dave", "Eve");
        Invite(b, "Alice", "Bob");
        Accept(b, "Bob", "Alice");
        Invite(b, "Carol", "Dave");
        Accept(b, "Dave", "Carol");
        var dave = b.Ch("Dave");
        b.Triggers.RegisterCharEvent("EVENTSPLAYER", "PartyLeave",
            (obj, _) => ReferenceEquals(obj, dave) ? TriggerResult.True : TriggerResult.Default);
        var carolsParty = b.Parties.FindParty(b.Ch("Carol").Uid)!;

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.ADDMEMBERFORCED", $"0{dave.Uid.Value:X}", null!));

        var alicesParty = b.Parties.FindParty(b.Ch("Alice").Uid)!;
        Assert.True(alicesParty.IsMember(dave.Uid));
        Assert.True(carolsParty.IsMember(dave.Uid));
        Assert.Same(alicesParty, b.Parties.FindParty(dave.Uid));

        Invite(b, "Carol", "Eve");
        Accept(b, "Eve", "Carol");

        Assert.False(carolsParty.IsMember(dave.Uid));
        Assert.True(carolsParty.IsMember(b.Ch("Eve").Uid));
        Assert.Same(alicesParty, b.Parties.FindParty(dave.Uid));
    }

    [Fact]
    public void ARemovalResyncsTheRemovedMemberAndEveryoneTold()
    {
        var b = PartyOfThree();

        Assert.True(b.Ch("Alice").TryExecuteCommand("PARTY.REMOVEMEMBER", "@1", null!));

        Assert.Equal(new[] { "Alice", "Bob", "Carol" }.Select(n => b.Ch(n).Uid.Value).OrderBy(v => v),
            b.Resynced.Select(u => u.Value).OrderBy(v => v));
    }
}
