using System;
using System.Linq;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// `PARTY.SETMASTER`, the verb (parity matrix: Source-X verb long tail).
///
/// The party verb table has ten entries and SphereNet routed eight of them. Promoting a
/// member could only be done by WRITING the property (`PARTY.MASTER=<uid>`), so a pack
/// using upstream's verb form got nothing - and the `@index` form, which is how a script
/// promotes "the second person in the list" without knowing their uid, had nowhere to
/// go at all.
///
/// The tenth entry, MESSAGE, is inert upstream: PDV_MESSAGE is a bare `break`
/// (CParty.cpp:796). It is accepted here for that reason rather than implemented -
/// refusing it would make a pack that runs on Source-X fail on this engine over a verb
/// that does nothing on either.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PartySetMasterVerbTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly Func<PartyManager?>? _savedManager = Character.ResolvePartyManager;
    private readonly Func<Serial, PartyDef?>? _savedFinder = Character.ResolvePartyFinder;

    public PartySetMasterVerbTests(ITestOutputHelper output) => _out = output;

    /// <summary>Both resolvers are process-wide. Left pointing at this test's party
    /// manager they follow every later test in the run - which is how a party fixture
    /// ends up deciding a karma or notoriety result somewhere else entirely.</summary>
    public void Dispose()
    {
        Character.ResolvePartyManager = _savedManager;
        Character.ResolvePartyFinder = _savedFinder;
    }

    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    /// <summary>A party of three: alice leads, bob and carol follow.</summary>
    private static (PartyManager Parties, Character Alice, Character Bob, Character Carol) Party(GameWorld world)
    {
        var parties = new PartyManager();
        // Both resolvers, as the server wires them: the verbs reach the manager, the
        // PARTY.* properties reach the caller's own party.
        Character.ResolvePartyManager = () => parties;
        Character.ResolvePartyFinder = uid => parties.FindParty(uid);

        var alice = Player(world, 100);
        var bob = Player(world, 101);
        var carol = Player(world, 102);
        // AcceptInvite takes the MASTER first: alice leads, the other two join her.
        parties.AcceptInvite(alice.Uid, bob.Uid);
        parties.AcceptInvite(alice.Uid, carol.Uid);
        return (parties, alice, bob, carol);
    }

    [Fact]
    public void TheVerbPromotesTheMemberItNames()
    {
        var world = World();
        var (parties, alice, bob, _) = Party(world);
        Assert.Equal(alice.Uid, parties.FindParty(alice.Uid)!.Master);

        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", $"0{bob.Uid.Value:X}", null!));

        var party = parties.FindParty(alice.Uid)!;
        _out.WriteLine($"master {alice.Uid.Value:X} -> {party.Master.Value:X}");
        Assert.Equal(bob.Uid, party.Master);
        // Upstream moves the new leader to the front, because the list is what indexed
        // access and the member packets read.
        Assert.Equal(bob.Uid, party.Members[0]);
    }

    [Fact]
    public void TheIndexFormPromotesTheNthMember()
    {
        var world = World();
        var (parties, alice, _, carol) = Party(world);

        // @2 is the third entry: the leader is 0. This is how a script promotes
        // somebody without knowing their uid.
        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", "@2", null!));

        var party = parties.FindParty(alice.Uid)!;
        _out.WriteLine($"@2 promoted {party.Master.Value:X} (carol is {carol.Uid.Value:X})");
        Assert.Equal(carol.Uid, party.Master);
    }

    [Fact]
    public void IndexZeroLeavesThePartyExactlyAsItWas()
    {
        var world = World();
        var (parties, alice, _, _) = Party(world);
        var before = parties.FindParty(alice.Uid)!.Members.ToArray();

        // Upstream refuses this index explicitly (CParty.cpp:826) and the guard here
        // matches it - but the two are indistinguishable from outside, because member 0
        // IS the master and promoting them changes nothing. So this pins the OUTCOME
        // (the party is untouched) rather than pretending to prove which branch ran.
        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", "@0", null!));

        var party = parties.FindParty(alice.Uid)!;
        Assert.Equal(alice.Uid, party.Master);
        Assert.Equal(before, party.Members.ToArray());
    }

    [Fact]
    public void AnIndexPastTheEndChangesNothing()
    {
        var world = World();
        var (parties, alice, _, _) = Party(world);

        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", "@9", null!));

        Assert.Equal(alice.Uid, parties.FindParty(alice.Uid)!.Master);
    }

    [Fact]
    public void SomebodyOutsideThePartyCannotBeMadeItsLeader()
    {
        var world = World();
        var (parties, alice, _, _) = Party(world);
        var stranger = Player(world, 200);

        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", $"0{stranger.Uid.Value:X}", null!));

        // The guard lives in the party itself, which is what keeps a typo from handing
        // the party to a passer-by.
        var party = parties.FindParty(alice.Uid)!;
        Assert.Equal(alice.Uid, party.Master);
        Assert.DoesNotContain(stranger.Uid, party.Members);
    }

    [Fact]
    public void TheVerbAndThePropertyAgree()
    {
        // The property form was the only way in before. Both have to end up in the same
        // place, or a pack that mixes them gets two different answers.
        var world = World();
        var (parties, alice, bob, carol) = Party(world);

        Assert.True(alice.TrySetProperty("PARTY.MASTER", $"0{bob.Uid.Value:X}"));
        Assert.Equal(bob.Uid, parties.FindParty(alice.Uid)!.Master);

        Assert.True(alice.TryExecuteCommand("PARTY.SETMASTER", $"0{carol.Uid.Value:X}", null!));
        Assert.Equal(carol.Uid, parties.FindParty(alice.Uid)!.Master);
    }

    [Fact]
    public void MessageIsAcceptedAndDoesNothing()
    {
        var world = World();
        var (parties, alice, _, _) = Party(world);
        var before = parties.FindParty(alice.Uid)!;
        var membersBefore = before.Members.ToArray();

        Assert.True(alice.TryExecuteCommand("PARTY.MESSAGE", "anything at all", null!));

        var after = parties.FindParty(alice.Uid)!;
        Assert.Equal(before.Master, after.Master);
        Assert.Equal(membersBefore, after.Members.ToArray());
    }
}
