using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.State;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Tests;

/// <summary>
/// Regressions for the movement / map audit (findings 1-11 and 23): each case is
/// the audit's counter-example, with the expected value taken from Source-X.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class MovementMapAuditRegressionTests
{
    private const ushort WallArt = 0x80;
    private const ushort CeilingArt = 0x81;
    private const ushort RoofFloorArt = 0x82;

    private static GameWorld World()
    {
        // A load never costs a step anything here (the S-curve roll always loses).
        MovementEngine.WeightLossRoll = max => max - 1;
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256, landTile: 3);
        var w = new GameWorld(NullLoggerFactory.Instance);
        w.InitMap(0, 256, 256);
        w.MapData = md;
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        Character.ResolveCharByUid = w.FindChar;
        return w;
    }

    private static Character Char(GameWorld w, short x = 100, short y = 100, sbyte z = 0, bool player = true)
    {
        var c = w.CreateCharacter();
        c.IsPlayer = player;
        c.IsOnline = player; // a logged-out character is no obstacle (CWorldSearch)
        c.BodyId = 0x190;
        c.Name = "Walker";
        c.Str = 50; c.Dex = 50; c.Int = 50;
        c.MaxHits = 100; c.Hits = 100;
        c.MaxStam = 100; c.Stam = 100;
        w.PlaceCharacter(c, new Point3D(x, y, z, 0));
        return c;
    }

    private static void Art(MapDataManager md, ushort id, TileFlag flags, byte height)
    {
        md.SetSyntheticItemTile(id, new ItemTileData { Flags = flags, Height = height, Name = "art" });
        DefinitionLoader.SetItemDef(id, new ItemDef(ResourceId.Invalid) { DispIndex = id, Height = height });
    }

    private static GameClient Client(GameWorld w, Character c, int id = 1)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, w, new AccountManager(lf), id);
        client.SetEngines(movement: new MovementEngine(w));
        TestHarness.AttachCharacter(client, c);
        return client;
    }

    private static int Rejects(GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState).Count(p => p.Span.Length > 0 && p.Span[0] == 0x21);

    // ---- 1. MAPn remap: world map, data file and client map are three numbers ----

    /// <summary>A real map{n}.mul / staidx{n}.mul / statics{n}.mul set for an 8x8
    /// map: flat land at <paramref name="landZ"/> and one static.</summary>
    private static void WriteMulSet(string dir, int file, sbyte landZ, (ushort Id, byte X, byte Y, sbyte Z) stat)
    {
        using (var b = new BinaryWriter(File.Create(Path.Combine(dir, $"map{file}.mul"))))
        {
            b.Write(0);
            for (int i = 0; i < 64; i++) { b.Write((ushort)3); b.Write(landZ); }
        }
        using (var b = new BinaryWriter(File.Create(Path.Combine(dir, $"staidx{file}.mul"))))
        {
            b.Write(0); b.Write(7); b.Write(0);   // block 0: offset 0, one 7-byte record
        }
        using (var b = new BinaryWriter(File.Create(Path.Combine(dir, $"statics{file}.mul"))))
        {
            b.Write(stat.Id); b.Write(stat.X); b.Write(stat.Y); b.Write(stat.Z); b.Write((ushort)0);
        }
    }

    [Fact]
    public void RemappedFacet_ReadsItsDataFile_ForTerrainStaticsWalkAndLos()
    {
        // sphere.ini MAP7=8,8,64,0,7: world map 7 reads map0/statics0 (Source-X
        // CServerMapBlock / CServerStaticsBlock resolve GetMapFileNum(m_map) on every
        // read, CServerMap.cpp:372/:471). The readers used to be filed under the file
        // number while the world asked with 7: height 0, no statics, walls gone.
        string dir = Path.Combine(Path.GetTempPath(), "remap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteMulSet(dir, 0, 20, (WallArt, 3, 2, 20));
            using var md = new MapDataManager(dir);
            md.InitMap(7, 0, 8, 8);
            Art(md, WallArt, TileFlag.Wall | TileFlag.Impassable, 20);

            Assert.Equal(0, md.GetMapFileNum(7));
            Assert.Equal(20, (int)md.GetTerrainTile(7, 2, 2).Z);
            Assert.Single(md.GetStatics(7, 3, 2));

            var w = new GameWorld(NullLoggerFactory.Instance);
            w.InitMap(7, 8, 8);
            w.MapData = md;
            ObjBase.ResolveWorld = () => w;
            Item.ResolveWorld = () => w;
            MovementEngine.WeightLossRoll = max => max - 1;
            var c = w.CreateCharacter();
            c.IsPlayer = true; c.BodyId = 0x190; c.Dex = 50; c.MaxStam = 100; c.Stam = 100;
            w.PlaceCharacter(c, new Point3D(2, 2, 20, 7));
            var engine = new MovementEngine(w);

            Assert.False(engine.TryMove(c, Direction.East, false, 1));   // the wall at 3,2
            Assert.False(w.CanSeeLOS(new Point3D(2, 2, 20, 7), new Point3D(6, 2, 20, 7)));
            Assert.True(engine.TryMove(c, Direction.North, false, 2));   // open ground: control
            Assert.Equal(20, (int)c.Z);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void TwoFacetsSharingOneFile_AndAFacetOnAnotherFile_EachReadTheirOwnData()
    {
        string dir = Path.Combine(Path.GetTempPath(), "remap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteMulSet(dir, 0, 20, (WallArt, 3, 2, 20));
            WriteMulSet(dir, 1, 5, (CeilingArt, 7, 7, 5));
            using (var md = new MapDataManager(dir))
            {
                md.InitMap(0, 0, 8, 8);   // MAP0=8,8,64,0
                md.InitMap(1, 0, 8, 8);   // MAP1=8,8,64,0 - Trammel on Felucca's file
                md.InitMap(2, 1, 8, 8);   // MAP2=8,8,64,1 - redirected to map1.mul

                Assert.Equal(20, (int)md.GetTerrainTile(0, 2, 2).Z);
                Assert.Equal(20, (int)md.GetTerrainTile(1, 2, 2).Z);
                Assert.Equal(5, (int)md.GetTerrainTile(2, 2, 2).Z);
                Assert.Single(md.GetStatics(0, 3, 2));
                Assert.Single(md.GetStatics(1, 3, 2));
                Assert.Empty(md.GetStatics(2, 3, 2));
                Assert.Equal((0, 0, 1), (md.GetMapFileNum(0), md.GetMapFileNum(1), md.GetMapFileNum(2)));
            } // shared readers are disposed once, without throwing
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void MapLine_KeepsWorldFileAndClientNumbersApart()
    {
        // CUOMapList::Load (CUOMapList.cpp:86): MAPn = maxx,maxy,sector,mapfile,mapid.
        // n is the world map, mapfile the data, mapid what the client is told.
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "[SPHERE]\nMAP0=6144,4096,64,0,0\nMAP7=1280,4096,64,0,3\n");
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);
            var m7 = Assert.Single(config.Maps, m => m.MapIndex == 7);
            Assert.Equal((0, 3), (m7.MapReadId, m7.MapSendId));
        }
        finally { File.Delete(tmp); }

        var w = new GameWorld(NullLoggerFactory.Instance);
        w.InitMap(7, 1280, 4096);
        w.SetClientMapId(7, 3);
        Assert.Equal(3, w.GetClientMapId(7));
        Assert.Equal(0, w.GetClientMapId(0));   // an unlisted map is told its own number
    }

    // ---- 2. 0xF0 batch: no virtual time ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementBatch_EarnsNoTimeOfItsOwn_ExcessStepsWaitForTheServerClock(bool credit)
    {
        // Source-X walks each 0xF0 step through Event_Walk and its real-clock check
        // (receive.cpp:4524, CClientEvent.cpp:906). Ten run steps in one packet used to
        // move the character ten tiles at once; an 0x02 stream at the same clock moves
        // one.
        var oldClock = GameClient.MoveClock;
        var oldTol = GameClient.MoveToleranceMs;
        var oldCredit = GameClient.MovementCreditEnabled;
        var oldCreditMax = GameClient.MovementCreditMaxMs;
        try
        {
            long now = 1_000_000;
            GameClient.MoveClock = () => now;
            GameClient.MoveToleranceMs = 0;
            GameClient.MovementCreditEnabled = credit;
            GameClient.MovementCreditMaxMs = 200;

            var w = World();
            var c = Char(w);
            c.Direction = Direction.East;
            var client = Client(w, c);
            client.HandleMovementBatch(Enumerable.Range(0, 10)
                .Select(n => new MovementStep(0x82, (byte)n, 0, 2)).ToArray());

            Assert.Equal(101, c.X);           // one step is due now, as on the 0x02 path
            Assert.Equal(0, Rejects(client)); // the rest wait; nothing is refused

            // The main loop drains the queue against the real clock.
            for (int t = 0; t < 3_000; t += 10)
            {
                now += 10;
                client.TickMovementQueue(now);
            }
            Assert.Equal(110, c.X);
            Assert.Equal(0, Rejects(client));
            Assert.Equal(10, client.NetState.WalkSequence);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTol;
            GameClient.MovementCreditEnabled = oldCredit;
            GameClient.MovementCreditMaxMs = oldCreditMax;
        }
    }

    [Fact]
    public void MovementBatch_AStepTheWalkRefuses_EndsTheBatch()
    {
        // receive.cpp:4527-4531: a failed Event_Walk stops the loop.
        var oldClock = GameClient.MoveClock;
        try
        {
            long now = 5_000;
            GameClient.MoveClock = () => now;
            var w = World();
            Art(w.MapData!, WallArt, TileFlag.Wall | TileFlag.Impassable, 20);
            w.MapData!.AddSyntheticStatic(0, 101, 100, WallArt, 0);
            var c = Char(w);
            c.Direction = Direction.East;
            var client = Client(w, c);
            client.HandleMovementBatch([new MovementStep(0x02, 0, 0, 1), new MovementStep(0x02, 1, 0, 1)]);
            Assert.Equal(100, c.X);
            Assert.Equal(1, Rejects(client));
            for (int t = 0; t < 1_000; t += 10) { now += 10; client.TickMovementQueue(now); }
            Assert.Equal(100, c.X);
        }
        finally { GameClient.MoveClock = oldClock; }
    }

    // ---- 3. SPEEDMODE 4 ----

    [Fact]
    public void SpeedMode4_RootsAPlayer_ButNotGmMode()
    {
        // OnFreezeCheck: m_pPlayer->m_speedMode & 0x04 prevents movement
        // (CCharAct.cpp:4535-4536); CanMove skips it in GM mode (:4575).
        var w = World();
        var c = Char(w);
        c.SpeedMode = 4;
        Assert.False(new MovementEngine(w).TryMove(c, Direction.East, false, 1));
        Assert.Equal(100, c.X);

        var w2 = World();
        var c2 = Char(w2);
        c2.SpeedMode = 4;
        c2.Direction = Direction.East;
        Assert.False(Client(w2, c2).HandleMove((byte)Direction.East, 0, 0));
        Assert.Equal(100, c2.X);

        c2.PrivLevel = PrivLevel.GM;
        Assert.True(new MovementEngine(w2).TryMove(c2, Direction.East, false, 1));
    }

    // ---- 4/5. shove order and PASSWALLS ----

    [Fact]
    public void PersonalSpace_CanWaiveTheCost_BeforeTheStaminaRuleIsJudged()
    {
        // ShoveCharAtPosition runs @PersonalSpace / @charShove first and reads ARGN1 /
        // ARGN3 back, then applies the full-stamina rule (CCharAct.cpp:4641-4668).
        var w = World();
        var c = Char(w);
        Char(w, 101);
        c.Stam = 50;
        int fired = 0;
        Character.OnPersonalSpace = (_, _, a) => { fired++; a.StaminaRequired = 0; a.RequireFullStamina = false; return false; };
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 1));
        Assert.Equal(1, fired);
        Assert.Equal(101, c.X);
    }

    [Fact]
    public void Shove_DefaultRule_StillNeedsFullStamina()
    {
        var w = World();
        var c = Char(w);
        Char(w, 101);
        c.Stam = 50;
        Assert.False(new MovementEngine(w).TryMove(c, Direction.East, false, 1));

        c.Stam = 100;
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 2));
        Assert.Equal(90, (int)c.Stam);   // the push costs 10
    }

    [Fact]
    public void PassWalls_StillMeetsCreatureBumping()
    {
        // CanMoveWalkTo returns before bumping only for GM mode (CCharAct.cpp:4755);
        // PASSWALLS is a wall ability.
        var w = World();
        var c = Char(w);
        Char(w, 101);
        c.CanMask = (ulong)CanFlags.C_PassWalls;
        int fired = 0;
        Character.OnCharShove = (_, _, _) => { fired++; return true; };
        Assert.False(new MovementEngine(w).TryMove(c, Direction.East, false, 1));
        Assert.Equal(1, fired);
        Assert.Equal(100, c.X);

        Character.OnCharShove = null;
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 2));
    }

    // ---- 6/7. GM mode and the stamina gate ----

    [Fact]
    public void Freeze_HoldsAGmWithGmModeOff()
    {
        // CanMove: if (!IsPriv(PRIV_GM)) { ... OnFreezeCheck ... } (CCharAct.cpp:4575).
        var w = World();
        var c = Char(w);
        c.PrivLevel = PrivLevel.GM;
        c.SetStatFlag(StatFlag.Freeze);
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 1));

        c.TrySetProperty("GM", "0");
        Assert.False(c.IsGmMode);
        Assert.False(new MovementEngine(w).TryMove(c, Direction.East, false, 2));
        Assert.Equal(101, c.X);
    }

    [Fact]
    public void EmptyStamina_RefusesTheStep_EvenWithNoStaminaPool()
    {
        // CanMove: Stat_GetVal(STAT_DEX) <= 0 && !STATF_DEAD (CCharAct.cpp:4586).
        var w = World();
        var c = Char(w);
        c.MaxStam = 0;
        c.Stam = 0;
        Assert.False(new MovementEngine(w).TryMove(c, Direction.East, false, 1));

        c.SetStatFlag(StatFlag.Dead);   // a ghost walks
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 2));
    }

    // ---- 8/10. IsVerticalSpace ----

    [Theory]
    [InlineData((byte)32, (sbyte)24, false)]   // tall body under a 24 ceiling: no room to mount
    [InlineData((byte)8, (sbyte)18, true)]     // short body under 18: room enough
    public void VerticalSpace_UsesTheCharactersHeight(byte height, sbyte ceilingZ, bool expected)
    {
        // IsVerticalSpace: GetHeightMount() + z + (fForceMount ? 4 : 0) >= top fails
        // (CCharStatus.cpp:1785-1802). It was a fixed 16.
        var w = World();
        var c = Char(w);
        c.HeightOverride = height;
        Art(w.MapData!, CeilingArt, TileFlag.Surface | TileFlag.Roof, 1);
        w.MapData!.AddSyntheticStatic(0, 100, 100, CeilingArt, ceilingZ);
        Assert.Equal(expected, w.Standing.HasVerticalSpace(c, 0, 100, 100, 0, 4));
    }

    [Fact]
    public void IsVerticalSpace_ScriptProperty_BothForms()
    {
        // CHC_ISVERTICALSPACE (CChar.cpp:2934-2950): no argument = my position, else
        // a point; IsVerticalSpace(pt, false).
        var w = World();
        var c = Char(w);
        Art(w.MapData!, CeilingArt, TileFlag.Surface | TileFlag.Roof, 1);
        w.MapData!.AddSyntheticStatic(0, 110, 100, CeilingArt, 10);

        Assert.True(c.TryGetProperty("ISVERTICALSPACE", out string own));
        Assert.Equal("1", own);
        Assert.True(c.TryGetProperty("ISVERTICALSPACE 110,100,0,0", out string low));
        Assert.Equal("0", low);   // 16 + 0 >= 10
        Assert.True(c.TryGetProperty("ISVERTICALSPACE 105,100,0,0", out string open));
        Assert.Equal("1", open);
        Assert.False(c.TryGetProperty("ISVERTICALSPACE nowhere_at_all", out _));
    }

    // ---- 9. STATF_INDOORS ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Walking_UpdatesIndoors_FromTheAreaFlag(bool underground)
    {
        // StatFlag_Mod(STATF_INDOORS, (uiBlockFlags & CAN_I_ROOF) ||
        // pArea->IsFlag(REGION_FLAG_UNDERGROUND)) on every committed step (CCharAct.cpp:4831).
        var w = World();
        var c = Char(w);
        var region = new Region { Name = "probe", Flags = underground ? RegionFlag.Underground : RegionFlag.None };
        region.AddRect(90, 90, 120, 120);
        w.AddRegion(region);
        if (!underground)
            c.SetStatFlag(StatFlag.InDoors);
        Assert.True(new MovementEngine(w).TryMove(c, Direction.East, false, 1));
        Assert.Equal(underground, c.IsStatFlag(StatFlag.InDoors));
    }

    [Fact]
    public void Walking_OntoARoofFloor_SetsIndoors()
    {
        var w = World();
        var c = Char(w);
        // A roof tile underfoot (CAN_I_ROOF on the floor stood on), just above the land.
        Art(w.MapData!, RoofFloorArt, TileFlag.Surface | TileFlag.Roof, 0);
        w.MapData!.AddSyntheticStatic(0, 101, 100, RoofFloorArt, 2);
        var engine = new MovementEngine(w);
        Assert.True(engine.TryMove(c, Direction.East, false, 1));
        Assert.True(c.IsStatFlag(StatFlag.InDoors));
        Assert.True(engine.TryMove(c, Direction.East, false, 2));
        Assert.False(c.IsStatFlag(StatFlag.InDoors));
    }

    // ---- 11. a route through someone the walker can push past ----

    [Fact]
    public void Pathfinding_PlansThroughAPushableCharacter_ButNotAnUnpushableOne()
    {
        // CPathFinder::FillMap asks CanMoveWalkTo(..., fCheckChars, fCheckOnly,
        // fPathFinding) for each cell (CPathFinder.cpp:235): a player in a one-tile
        // corridor is pushable for a creature at full stamina.
        var w = World();
        var npc = Char(w, player: false);
        Char(w, 102);   // a player standing in the corridor
        Art(w.MapData!, WallArt, TileFlag.Wall | TileFlag.Impassable, 20);
        for (short x = 94; x <= 110; x++)
        {
            w.MapData!.AddSyntheticStatic(0, x, 99, WallArt, 0);
            w.MapData!.AddSyntheticStatic(0, x, 101, WallArt, 0);
        }
        var goal = new Point3D(104, 100, 0, 0);
        Assert.NotNull(new Pathfinder(w).FindPath(npc.Position, goal, 0, self: npc, maxNodes: 1000, maxRadius: 5));

        // Below full stamina the push is refused, and so is the route.
        npc.Stam = 50;
        Assert.Null(new Pathfinder(w).FindPath(npc.Position, goal, 0, self: npc, maxNodes: 1000, maxRadius: 5));

        // One creature does not push past another (NPCSHOVENPC off).
        var w2 = World();
        var npc2 = Char(w2, player: false);
        Char(w2, 102, player: false);
        Art(w2.MapData!, WallArt, TileFlag.Wall | TileFlag.Impassable, 20);
        for (short x = 94; x <= 110; x++)
        {
            w2.MapData!.AddSyntheticStatic(0, x, 99, WallArt, 0);
            w2.MapData!.AddSyntheticStatic(0, x, 101, WallArt, 0);
        }
        Assert.Null(new Pathfinder(w2).FindPath(npc2.Position, goal, 0, self: npc2, maxNodes: 1000, maxRadius: 5));
    }

    // ---- 23. direction validation ----

    [Theory]
    [InlineData((byte)0x08)]
    [InlineData((byte)0x0F)]
    [InlineData((byte)0x8A)]
    public void InvalidDirection_IsRefused_NotFolded(byte raw)
    {
        // Event_Walk: dir = rawdir & 0xF; dir >= DIR_QTY -> PacketMovementRej
        // (CClientEvent.cpp:864-869).
        var w = World();
        var c = Char(w);
        c.Direction = Direction.North;
        var client = Client(w, c);
        Assert.False(client.HandleMove(raw, 0, 0));
        Assert.Equal(Direction.North, c.Direction);
        Assert.Equal((100, 100), ((int)c.X, (int)c.Y));
        Assert.Equal(1, Rejects(client));
    }

    /// <summary>With the server sequence at 0 (login, reject, 0x20) only a seq-0 step
    /// is valid (Source-X receive.cpp:270-271). A stale in-flight seq 1 used to be
    /// accepted and moved the server a tile the client had already taken back.</summary>
    [Fact]
    public void AfterAResetOnlySequenceZeroWalks()
    {
        var w = World();
        var c = Char(w);
        c.Direction = Direction.East;
        var client = Client(w, c);

        Assert.False(client.HandleMove((byte)Direction.East, 1, 0));
        Assert.Equal(100, c.X);

        Assert.True(client.HandleMove((byte)Direction.East, 0, 0));
        Assert.Equal(101, c.X);
    }

    [Fact]
    public void ValidRunningDirection_StillWalks()
    {
        var w = World();
        var c = Char(w);
        c.Direction = Direction.East;
        Assert.True(Client(w, c).HandleMove(0x82, 0, 0));
        Assert.Equal(101, c.X);
    }
}
