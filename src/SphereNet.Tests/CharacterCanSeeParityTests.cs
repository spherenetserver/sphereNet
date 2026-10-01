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
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// One visibility rule for a viewer and a mobile: Source-X CChar::CanSee
/// (CCharStatus.cpp:1074-1260). The CANSEE script property, the per-tick view delta
/// and the appear / move notifications must all give the same answer, because a
/// notification that disagrees with the delta draws a mobile that the next tick
/// deletes, or holds back one the next tick draws.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharacterCanSeeParityTests
{
    private const ushort WallTile = 0x0080;

    private sealed class Fixture
    {
        public GameWorld World = null!;
        public MapDataManager Map = null!;
        public ILoggerFactory Lf = null!;
        public AccountManager Accounts = null!;
        private int _nextId = 7100;

        public Character Player(short x, PrivLevel priv = PrivLevel.Player, bool online = true)
        {
            var ch = World.CreateCharacter();
            ch.IsPlayer = true;
            ch.IsOnline = online;
            ch.PrivLevel = priv;
            ch.MaxHits = 100; ch.Hits = 100;
            World.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
            return ch;
        }

        public Character Npc(short x)
        {
            var ch = World.CreateCharacter();
            ch.IsPlayer = false;
            ch.MaxHits = 100; ch.Hits = 100;
            World.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
            return ch;
        }

        public GameClient Client(Character viewer)
        {
            var state = TestHarness.CreateActiveNetState(Lf, _nextId++);
            var client = new GameClient(state, World, Accounts, Lf.CreateLogger<GameClient>());
            TestHarness.AttachCharacter(client, viewer);
            return client;
        }
    }

    private static Fixture NewFixture()
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(WallTile, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return new Fixture { World = world, Map = map, Lf = lf, Accounts = new AccountManager(lf) };
    }

    private static string Read(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out string value));
        return value;
    }

    private static bool InDelta(GameClient client, Character target) =>
        client.BuildViewDelta()!.CurrentChars.Contains(target.Uid.Value);

    private static bool AfterAppear(GameClient client, Character target)
    {
        client.NotifyCharacterAppear(target);
        return client.HasKnownChar(target.Uid.Value);
    }

    // ---- #17: CANSEE is CanSee(obj), not a wall test ------------------------

    [Fact]
    public void CanSeeHidesAHiddenPlayerFromAnOrdinaryPlayer()
    {
        // Audit case script_hidden: a hidden target on open ground answered 1.
        // Source-X: concealed target, viewer plevel < 2 -> false (CCharStatus.cpp:1189-1210).
        var f = NewFixture();
        var viewer = f.Player(100);
        var hidden = f.Player(103);
        hidden.SetStatFlag(StatFlag.Hidden);

        Assert.Equal("0", Read(viewer, $"CANSEE 0{hidden.Uid.Value:X}"));
        hidden.ClearStatFlag(StatFlag.Hidden);
        Assert.Equal("1", Read(viewer, $"CANSEE 0{hidden.Uid.Value:X}"));
    }

    [Fact]
    public void CanSeeIgnoresWallsWhichAreCanSeeLosBusiness()
    {
        // Audit cases script_wall / script_open_control: a visible target behind a
        // wall answered 0. CanSee has no LOS test; that is CANSEELOS (CObjBase.cpp:1141).
        var f = NewFixture();
        var viewer = f.Player(100);
        var target = f.Player(104);
        f.Map.AddSyntheticStatic(0, 102, 100, WallTile, 0);
        Assert.False(f.World.CanSeeLOS(viewer.Position, target.Position));

        Assert.Equal("1", Read(viewer, $"CANSEE 0{target.Uid.Value:X}"));
        Assert.Equal("1", Read(viewer, $"CANSEE.0{target.Uid.Value:X}"));
    }

    [Fact]
    public void CanSeeStopsAtTheViewersVisualRange()
    {
        // GetDistSight(target) > GetVisualRange() -> false (CCharStatus.cpp:1171).
        var f = NewFixture();
        var viewer = f.Player(100);
        var near = f.Player(118);
        var far = f.Player(119);

        Assert.Equal("1", Read(viewer, $"CANSEE 0{near.Uid.Value:X}"));
        Assert.Equal("0", Read(viewer, $"CANSEE 0{far.Uid.Value:X}"));
    }

    [Fact]
    public void CanSeeLetsStaffSeeAHiddenPlayerAndOnlyGmModeSeeAnOfflineOne()
    {
        var f = NewFixture();
        var counsel = f.Player(100, PrivLevel.Counsel);
        var gm = f.Player(101, PrivLevel.GM);
        var player = f.Player(102);
        var hidden = f.Player(103);
        hidden.SetStatFlag(StatFlag.Hidden);
        var offline = f.Player(104, online: false);

        Assert.Equal("1", Read(counsel, $"CANSEE 0{hidden.Uid.Value:X}"));
        // Disconnected: only IsPriv(PRIV_GM) (CCharStatus.cpp:1253).
        Assert.Equal("0", Read(player, $"CANSEE 0{offline.Uid.Value:X}"));
        Assert.Equal("0", Read(counsel, $"CANSEE 0{offline.Uid.Value:X}"));
        Assert.Equal("1", Read(gm, $"CANSEE 0{offline.Uid.Value:X}"));
        Assert.True(gm.TrySetProperty("GM", "0"));
        Assert.Equal("0", Read(gm, $"CANSEE 0{offline.Uid.Value:X}"));
    }

    [Fact]
    public void CanSeeAnInvisibleItemNeedsGmModeOrASeenByTag()
    {
        // CChar::CanSeeItem (CCharStatus.cpp:1265-1279).
        var f = NewFixture();
        var player = f.Player(100);
        var gm = f.Player(101, PrivLevel.GM);
        var item = f.World.CreateItem();
        item.SetAttr(ObjAttributes.Invis);
        f.World.PlaceItem(item, new Point3D(102, 100, 0, 0));

        Assert.Equal("0", Read(player, $"CANSEE 0{item.Uid.Value:X}"));
        Assert.Equal("1", Read(gm, $"CANSEE 0{item.Uid.Value:X}"));
        item.SetTag($"SeenBy_0{player.Uid.Value:x}", "1");
        Assert.Equal("1", Read(player, $"CANSEE 0{item.Uid.Value:X}"));
    }

    // ---- #20: the delta and the appear notification agree --------------------

    [Fact]
    public void ACounselorDoesNotGetAHiddenOwnerFromEitherPath()
    {
        // Audit case view_notify_staff_higher_hidden: the appear path treated every
        // Counsel+ as staff and drew a hidden Owner the delta hid.
        // CANSEESAMEPLEVEL 0: plevelMe < plevelChar -> false (CCharStatus.cpp:1214-1219).
        var f = NewFixture();
        var counsel = f.Player(100, PrivLevel.Counsel);
        var owner = f.Player(103, PrivLevel.Owner);
        owner.SetStatFlag(StatFlag.Hidden);
        var client = f.Client(counsel);

        Assert.False(InDelta(client, owner));
        Assert.False(AfterAppear(client, owner));
    }

    [Fact]
    public void SeeHiddenLetsAnOrdinaryViewerSeeThroughBothPaths()
    {
        // Audit case view_notify_seehidden_allow: @SeeHidden decides for a concealed
        // player (CCharStatus.cpp:1193-1201); the appear path ignored it.
        var f = NewFixture();
        var viewer = f.Player(100);
        var hidden = f.Player(103);
        hidden.SetStatFlag(StatFlag.Hidden);
        var client = f.Client(viewer);
        Character.OnSeeHidden = (_, _, _) => 0;

        Assert.True(InDelta(client, hidden));
        Assert.True(AfterAppear(client, hidden));
        Assert.Equal("1", Read(viewer, $"CANSEE 0{hidden.Uid.Value:X}"));
    }

    [Fact]
    public void AGhostUnderDeadCannotSeeLivingLosesALivingNpcOnBothPaths()
    {
        // Audit case view_notify_dead_cannot_see_living (CCharStatus.cpp:1241, 1004-1025).
        var f = NewFixture();
        Character.DeadCannotSeeLiving = 1;
        var ghost = f.Player(100);
        ghost.SetStatFlag(StatFlag.Dead);
        var npc = f.Npc(103);
        var client = f.Client(ghost);

        Assert.False(InDelta(client, npc));
        Assert.False(AfterAppear(client, npc));
        Assert.Equal("0", Read(ghost, $"CANSEE 0{npc.Uid.Value:X}"));
    }

    [Fact]
    public void SpiritSpeakDoesNotShowAnUnmanifestedGhostOnEitherPath()
    {
        // Audit case view_notify_spirit_ghost: the two native paths disagreed.
        // Source-X CanSee has no Spirit Speak rule: an insubstantial ghost is a
        // concealed character (CCharStatus.cpp:1189), and Spirit Speak only lets the
        // living hear it.
        var f = NewFixture();
        var listener = f.Player(100);
        listener.SetStatFlag(StatFlag.SpiritSpeak);
        var ghost = f.Player(103);
        ghost.SetStatFlag(StatFlag.Dead);
        ghost.SetStatFlag(StatFlag.Insubstantial);
        var client = f.Client(listener);

        Assert.False(InDelta(client, ghost));
        Assert.False(AfterAppear(client, ghost));

        // Manifested (war toggle clears INSUBSTANTIAL, CClientEvent.cpp:1028): both show it.
        ghost.ClearStatFlag(StatFlag.Insubstantial);
        Assert.True(InDelta(client, ghost));
        Assert.True(AfterAppear(client, ghost));
    }

    [Fact]
    public void AnInsubstantialLivingCharacterIsConcealedFromPlayers()
    {
        // .INVIS sets STATF_INSUBSTANTIAL (CChar.cpp:4658); CanSee treats it like
        // hidden (CCharStatus.cpp:1189).
        var f = NewFixture();
        var viewer = f.Player(100);
        var gm = f.Player(103, PrivLevel.GM);
        gm.SetStatFlag(StatFlag.Insubstantial);
        var client = f.Client(viewer);

        Assert.False(InDelta(client, gm));
        Assert.False(AfterAppear(client, gm));
    }

    [Fact]
    public void AMovingMobileThatTheViewerMayNoLongerSeeIsRemoved()
    {
        var f = NewFixture();
        var counsel = f.Player(100, PrivLevel.Counsel);
        var owner = f.Player(103, PrivLevel.Owner);
        var client = f.Client(counsel);
        Assert.True(AfterAppear(client, owner));

        owner.SetStatFlag(StatFlag.Hidden);
        var old = owner.Position;
        f.World.MoveCharacter(owner, new Point3D(104, 100, 0, 0));
        client.NotifyCharMoved(owner, old);

        Assert.False(client.HasKnownChar(owner.Uid.Value));
    }

    // ---- #21: ALLSHOW keeps the plevel line ---------------------------------

    [Fact]
    public void AllShowDoesNotReachAboveTheViewersPlevel()
    {
        // Audit case view_allshow_respects_higher_plevel.
        // IsPriv(PRIV_ALLSHOW) -> return plevelMe >= plevelChar (CCharStatus.cpp:1180-1181).
        var f = NewFixture();
        var counsel = f.Player(100, PrivLevel.Counsel);
        counsel.AllShow = true;
        var owner = f.Player(103, PrivLevel.Owner);
        owner.SetStatFlag(StatFlag.Hidden);
        var player = f.Player(104);
        player.SetStatFlag(StatFlag.Hidden);
        var client = f.Client(counsel);

        Assert.False(InDelta(client, owner));
        Assert.False(AfterAppear(client, owner));
        Assert.False(Character.CanSeeHidden(counsel, owner));
        Assert.Equal("0", Read(counsel, $"CANSEE 0{owner.Uid.Value:X}"));

        Assert.True(InDelta(client, player));
        Assert.True(AfterAppear(client, player));
        Assert.Equal("1", Read(counsel, $"CANSEE 0{player.Uid.Value:X}"));
    }

    // ---- a logged-out player reaches the view only with ALLSHOW ---------------

    [Fact]
    public void OnlyAllShowBringsAnOfflinePlayerIntoTheView()
    {
        // The view's CWorldSearch walks the sector's m_Chars_Disconnect list only with
        // ALLSHOW (CWorldSearch.cpp:271, CClientMsg.cpp:331); GM mode alone (the CANSEE
        // rule, CCharStatus.cpp:1253) never reaches it.
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        var plain = f.Player(101);
        var offline = f.Player(103, online: false);
        var gmClient = f.Client(gm);
        var plainClient = f.Client(plain);

        Assert.False(InDelta(gmClient, offline));
        Assert.False(AfterAppear(gmClient, offline));
        Assert.False(InDelta(plainClient, offline));
        Assert.False(AfterAppear(plainClient, offline));

        gm.AllShow = true;
        Assert.True(InDelta(gmClient, offline));
        Assert.True(AfterAppear(gmClient, offline));
    }

    [Fact]
    public void AGmSeesTheOfflinePlayerGreyed()
    {
        // The delta draws what it shows a staff viewer of an offline body with the
        // 0x80 greyed flag; the appear path draws it the same way.
        var f = NewFixture();
        var gm = f.Player(100, PrivLevel.GM);
        gm.AllShow = true;
        var offline = f.Player(103, online: false);
        var client = f.Client(gm);

        var delta = client.BuildViewDelta()!;
        Assert.Contains(delta.NewChars, e => e.Character == offline && e.HiddenAsAllShow);

        client.NotifyCharacterAppear(offline);
        var draw = TestHarness.GetQueuedPackets(client.NetState)
            .Select(p => p.Span.ToArray())
            .Last(s => s.Length > 0 && s[0] == 0x78);
        // 0x78: cmd, len(2), serial(4), body(2), x(2), y(2), z(1), dir(1), hue(2), flags(1)
        Assert.NotEqual(0, draw[17] & 0x80);
    }
}
