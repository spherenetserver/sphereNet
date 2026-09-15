using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.State;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Whether a mobile is on the observer's screen, through the transitions that change
/// the answer (review work item D05, the character half).
///
/// The item half of this matrix found a real defect by modelling what the CLIENT had
/// been told rather than counting packets; this applies the same method to creatures,
/// where the rules are less about position and more about permission: hidden, dead,
/// offline, and whoever is allowed to see through each of those.
///
/// Two failure directions matter and they are not symmetrical. A creature that stays on
/// screen when it should have gone is a leak - a hidden player still visible, a ghost
/// visible to the living. One that disappears and never comes back is a broken screen
/// the player cannot fix without walking away and back. Both are asserted here, and
/// against three observers, because every one of these gates reads differently for a
/// GM with AllShow than for an ordinary player.
/// </summary>
public sealed class ViewDeltaCharacterTransitionTests
{
    private readonly ITestOutputHelper _out;
    public ViewDeltaCharacterTransitionTests(ITestOutputHelper output) => _out = output;

    private enum Seen { Never, Drawn, Deleted }

    /// <summary>Replays a client's outbound history into "is this mobile on screen".
    /// The harness snapshot does not drain, so the whole history is replayed each
    /// time: the answer is the last thing the client was told about that uid.</summary>
    private sealed class ClientModel(NetState state)
    {
        private readonly Dictionary<uint, Seen> _seen = [];
        public readonly List<string> Log = [];

        public Seen this[Character ch] => _seen.GetValueOrDefault(ch.Uid.Value, Seen.Never);

        public void Pump()
        {
            _seen.Clear();
            Log.Clear();
            foreach (var packet in TestHarness.GetQueuedPackets(state))
            {
                var span = packet.Span;
                if (span.Length == 0) continue;
                switch (span[0])
                {
                    case 0x78: Record(Read(span, 3), Seen.Drawn, "0x78"); break;   // draw mobile
                    case 0x77: Record(Read(span, 1), Seen.Drawn, "0x77"); break;   // mobile moving
                    case 0x1D: Record(Read(span, 1), Seen.Deleted, "0x1D"); break; // delete
                }
            }
        }

        private void Record(uint uid, Seen seen, string opcode)
        {
            _seen[uid] = seen;
            Log.Add($"{opcode}->{seen}");
        }

        private static uint Read(ReadOnlySpan<byte> s, int at) =>
            s.Length < at + 4 ? 0u
            : (uint)((s[at] << 24) | (s[at + 1] << 16) | (s[at + 2] << 8) | s[at + 3]);
    }

    private sealed class Stage
    {
        public GameWorld World = null!;
        public Character Subject = null!;
        public (string Name, GameClient Client, ClientModel Model)[] Observers = [];

        public void Tick()
        {
            foreach (var (_, client, model) in Observers)
            {
                client.ViewNeedsRefresh = true;
                var delta = client.BuildViewDelta();
                if (delta != null) client.ApplyViewDelta(delta);
                model.Pump();
            }
        }

        public ClientModel Plain => Observers[0].Model;
        public ClientModel Gm => Observers[1].Model;
        public ClientModel Modern => Observers[2].Model;
    }

    private Stage NewStage()
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        map.AddSyntheticMap(1, 512, 512, landZ: 0, landTile: 3);

        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.InitMap(1, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var accounts = new AccountManager(lf);
        var stage = new Stage { World = world };

        // The mobile every observer is watching: an ordinary player standing nearby.
        var subject = world.CreateCharacter();
        subject.IsPlayer = true;
        subject.IsOnline = true;
        subject.Name = "Subject";
        subject.MaxHits = 100; subject.Hits = 100;
        world.PlaceCharacter(subject, new Point3D(100, 100, 0, 0));
        stage.Subject = subject;

        (string, GameClient, ClientModel) Observer(string name, int id, Action<Character, NetState> shape)
        {
            var state = TestHarness.CreateActiveNetState(lf, id);
            var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.IsOnline = true;
            ch.MaxHits = 100; ch.Hits = 100;
            world.PlaceCharacter(ch, new Point3D(102, 100, 0, 0));
            shape(ch, state);
            TestHarness.AttachCharacter(client, ch);
            return (name, client, new ClientModel(state));
        }

        stage.Observers =
        [
            Observer("plain", 1, (_, _) => { }),
            Observer("gm", 2, (ch, _) => { ch.PrivLevel = PrivLevel.GM; ch.AllShow = true; }),
            Observer("modern", 3, (_, state) => state.ClientVersionNumber = 70_020_000),
        ];

        stage.Tick();
        return stage;
    }

    private static Character ObserverChar(Stage stage, int index) =>
        (Character)typeof(GameClient).GetField("_character",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(stage.Observers[index].Client)!;

    // ---- hidden ----------------------------------------------------------

    [Fact]
    public void HidingTakesAPlayerOffTheOrdinaryScreenAndLeavesThemOnStaffs()
    {
        var stage = NewStage();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);

        stage.Subject.SetStatFlag(StatFlag.Hidden);
        stage.Tick();

        _out.WriteLine($"hidden: plain={stage.Plain[stage.Subject]} gm={stage.Gm[stage.Subject]} " +
                       $"modern={stage.Modern[stage.Subject]}");

        // Leaving them drawn is the leak; the GM keeps them because AllShow is what
        // AllShow is for.
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);
        Assert.Equal(Seen.Deleted, stage.Modern[stage.Subject]);
        Assert.Equal(Seen.Drawn, stage.Gm[stage.Subject]);
    }

    [Fact]
    public void ComingOutOfHidingPutsThemBackWithoutAnybodyMoving()
    {
        var stage = NewStage();
        stage.Subject.SetStatFlag(StatFlag.Hidden);
        stage.Tick();
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);

        stage.Subject.ClearStatFlag(StatFlag.Hidden);
        stage.Tick();

        // A screen that needs a walk away and back to repair itself is the other half
        // of the same defect.
        _out.WriteLine($"revealed: plain saw [{string.Join(",", stage.Plain.Log)}]");
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }

    // ---- death and resurrection -------------------------------------------

    [Fact]
    public void AGhostIsHiddenFromTheLivingAndVisibleToStaff()
    {
        var stage = NewStage();
        stage.Subject.Hits = 0;
        stage.Subject.SetStatFlag(StatFlag.Dead);
        stage.Subject.SetStatFlag(StatFlag.Insubstantial);   // an unmanifested ghost
        stage.Tick();

        _out.WriteLine($"ghost: plain={stage.Plain[stage.Subject]} gm={stage.Gm[stage.Subject]}");
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);
        Assert.Equal(Seen.Drawn, stage.Gm[stage.Subject]);
    }

    [Fact]
    public void AManifestedGhostIsVisibleToEverybody()
    {
        var stage = NewStage();
        stage.Subject.Hits = 0;
        stage.Subject.SetStatFlag(StatFlag.Dead);
        stage.Subject.SetStatFlag(StatFlag.Insubstantial);
        stage.Tick();
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);

        // Manifesting is the ghost choosing to be seen (the war-mode toggle upstream
        // clears INSUBSTANTIAL).
        stage.Subject.ClearStatFlag(StatFlag.Insubstantial);
        stage.Tick();

        _out.WriteLine($"manifested: plain saw [{string.Join(",", stage.Plain.Log)}]");
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }

    [Fact]
    public void ResurrectionBringsTheBodyBackToEveryScreen()
    {
        var stage = NewStage();
        stage.Subject.Hits = 0;
        stage.Subject.SetStatFlag(StatFlag.Dead);
        stage.Subject.SetStatFlag(StatFlag.Insubstantial);
        stage.Tick();
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);

        stage.Subject.ClearStatFlag(StatFlag.Dead);
        stage.Subject.ClearStatFlag(StatFlag.Insubstantial);
        stage.Subject.Hits = 50;
        stage.Tick();

        _out.WriteLine($"resurrected: plain={stage.Plain[stage.Subject]} gm={stage.Gm[stage.Subject]}");
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
        Assert.Equal(Seen.Drawn, stage.Gm[stage.Subject]);
    }

    // ---- logging out ------------------------------------------------------

    [Fact]
    public void AnOfflinePlayerLeavesTheOrdinaryScreenAndStaysOnStaffs()
    {
        var stage = NewStage();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);

        stage.Subject.IsOnline = false;          // logged out, body still in the world
        stage.Tick();

        _out.WriteLine($"offline: plain={stage.Plain[stage.Subject]} gm={stage.Gm[stage.Subject]}");
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);
        Assert.Equal(Seen.Drawn, stage.Gm[stage.Subject]);
    }

    [Fact]
    public void ALingeringPlayerIsStillOnScreenForEverybody()
    {
        // A dropped connection is not a logout: the body stays attackable for the
        // linger period, so it has to stay visible or people will swing at nothing.
        var stage = NewStage();
        stage.Subject.IsOnline = false;
        stage.Subject.SetTag("CLIENT_LINGER_UNTIL", (Environment.TickCount64 + 60_000).ToString());
        Assert.True(stage.Subject.IsClientLingering);
        stage.Tick();

        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }

    // ---- moving away ------------------------------------------------------

    [Fact]
    public void TheSameCoordinatesOnAnotherMapAreNotTheSamePlace()
    {
        var stage = NewStage();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);

        stage.World.MoveCharacter(stage.Subject, new Point3D(100, 100, 0, 1));
        stage.Tick();

        // Identical x and y, different world. A view that compared coordinates without
        // the map would leave them standing on every screen they had been on.
        _out.WriteLine($"after the map change: plain={stage.Plain[stage.Subject]}");
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);

        var observer = ObserverChar(stage, 0);
        stage.World.MoveCharacter(observer, new Point3D(102, 100, 0, 1));
        stage.Tick();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }

    [Fact]
    public void AUidThatComesBackAsSomebodyElseIsDrawnFresh()
    {
        var stage = NewStage();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
        uint uid = stage.Subject.Uid.Value;

        // The character is deleted and the uid is handed to a new creature, which is
        // what a busy shard does with its serial space.
        stage.World.DeleteObject(stage.Subject);
        stage.Subject.Delete();
        stage.Tick();
        Assert.Equal(Seen.Deleted, stage.Plain[stage.Subject]);

        var replacement = stage.World.CreateCharacter();
        replacement.UidRef = new Serial(uid);
        replacement.Name = "Somebody else";
        replacement.MaxHits = 100; replacement.Hits = 100;
        stage.World.PlaceCharacter(replacement, new Point3D(101, 100, 0, 0));
        stage.Tick();

        // The client was told to forget the uid, so the new occupant has to be
        // announced rather than assumed to be already there.
        _out.WriteLine($"after the uid came back: plain saw [{string.Join(",", stage.Plain.Log)}]");
        Assert.Equal(Seen.Drawn, stage.Plain[replacement]);
    }

    // ---- nothing happening -------------------------------------------------

    [Fact]
    public void AStandingStillWorldSendsNothingAboutAnybody()
    {
        var stage = NewStage();
        int before = stage.Plain.Log.Count;

        for (int i = 0; i < 5; i++) stage.Tick();

        _out.WriteLine($"five idle ticks: {before} -> {stage.Plain.Log.Count} packets");
        Assert.Equal(before, stage.Plain.Log.Count);
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }
    // ---- the observer's own end -------------------------------------------

    [Fact]
    public void ShorteningTheViewRangeTakesAwayWhatNoLongerFits()
    {
        var stage = NewStage();
        var far = stage.World.CreateCharacter();
        far.IsPlayer = true;
        far.IsOnline = true;
        far.MaxHits = 100; far.Hits = 100;
        stage.World.PlaceCharacter(far, new Point3D(112, 100, 0, 0));   // 10 tiles away
        stage.Tick();
        Assert.Equal(Seen.Drawn, stage.Plain[far]);

        // The client asks for a smaller window (the 0xC8 view-range request). What no
        // longer fits has to be taken off the screen, or it stays there for ever: it
        // will not move, and nothing else will change near it.
        stage.Observers[0].Client.NetState.ViewRange = 5;
        stage.Tick();

        _out.WriteLine($"view range 18 -> 5: far={stage.Plain[far]} near={stage.Plain[stage.Subject]}");
        Assert.Equal(Seen.Deleted, stage.Plain[far]);
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }

    [Fact]
    public void AResyncRebuildsTheScreenFromNothing()
    {
        // What a reconnect does: forget everything the client knew and say it all
        // again. The failure it guards against is a known-set that survives the
        // client's own state being thrown away, after which the delta says nothing
        // because as far as it knows the client already has it.
        var stage = NewStage();
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);

        stage.Observers[0].Client.Resync();
        stage.Tick();

        _out.WriteLine($"after a resync: plain saw [{string.Join(",", stage.Plain.Log)}]");
        Assert.Equal(Seen.Drawn, stage.Plain[stage.Subject]);
    }
}
