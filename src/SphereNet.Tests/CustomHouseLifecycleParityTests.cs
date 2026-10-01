using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CItemMultiCustom parity for the custom-house design lifecycle: the
/// foundation a new building starts from (constructor/ResetStructure), AddRoof,
/// Begin/End/SwitchToLevel, the @HouseDesignBegin/@HouseDesignExit contracts, the
/// CommitChanges order, fixture creation (OnComponentCreate, telepad pairing),
/// the r_WriteVal properties and the r_Verb script verbs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CustomHouseLifecycleParityTests
{
    private const ushort FoundationId = 0x0010;  // 7x7 foundation (small)
    private const ushort LargeFoundationId = 0x0011; // 14 wide (large)
    private const ushort StairMultiId = 0x0709;
    private const ushort FloorTile = 0x0604;
    private const ushort WallTile = 0x0608;
    private const ushort DoorTile = 0x0600;
    private const ushort RoofTile = 0x060C;
    private const ushort TelepadTile = 0x1822;

    private sealed class Env
    {
        public required GameWorld World;
        public required MultiRegistry Registry;
        public required HousingEngine Housing;
        public required CustomHousingEngine Custom;
    }

    private static Env NewEnv()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(DoorTile, new ItemTileData
        { Flags = TileFlag.Door | TileFlag.Impassable, Height = 20, Name = "door" });
        md.SetSyntheticItemTile(FloorTile, new ItemTileData { Flags = TileFlag.Background, Name = "floor" });
        md.SetSyntheticItemTile(WallTile, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });
        md.SetSyntheticItemTile(RoofTile, new ItemTileData { Flags = TileFlag.Roof, Height = 3, Name = "roof" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new MultiRegistry();
        var small = new MultiDef { Id = FoundationId, Name = "small foundation", MultiTypeName = "t_multi_custom" };
        small.Components.Add(new MultiComponent { TileId = FloorTile, DeltaX = -3, DeltaY = -3, DeltaZ = 7, Visible = true });
        small.Components.Add(new MultiComponent { TileId = FloorTile, DeltaX = 3, DeltaY = 3, DeltaZ = 7, Visible = true });
        small.Components.Add(new MultiComponent { TileId = WallTile, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = false });
        small.RecalcBounds();
        registry.Register(small);

        var large = new MultiDef { Id = LargeFoundationId, Name = "large foundation", MultiTypeName = "t_multi_custom" };
        large.Components.Add(new MultiComponent { TileId = FloorTile, DeltaX = -7, DeltaY = -3, DeltaZ = 7, Visible = true });
        large.Components.Add(new MultiComponent { TileId = FloorTile, DeltaX = 6, DeltaY = 3, DeltaZ = 7, Visible = true });
        large.RecalcBounds();
        registry.Register(large);

        var stair = new MultiDef { Id = StairMultiId };
        stair.Components.Add(new MultiComponent { TileId = WallTile, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        stair.Components.Add(new MultiComponent { TileId = WallTile, DeltaX = 0, DeltaY = -1, DeltaZ = 5, Visible = true });
        registry.Register(stair);

        var housing = new HousingEngine(world, registry);
        return new Env { World = world, Registry = registry, Housing = housing,
            Custom = new CustomHousingEngine(world, housing) };
    }

    private static Character NewChar(GameWorld world, short x, short y, PrivLevel priv = PrivLevel.Player)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = priv;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static (Item Multi, House House) NewHouse(Env env, Character owner, ushort id = FoundationId,
        short x = 100, short y = 100)
    {
        var multi = env.World.CreateItem();
        multi.ItemType = ItemType.MultiCustom;
        multi.BaseId = id;
        multi.SetTag("HOUSE.OWNER", $"0{owner.Uid.Value:X}");
        env.World.PlaceItem(multi, new Point3D(x, y, 0, 0));
        CustomHousingEngine.InitializeFoundationDesign(multi, env.Registry.Get(id)!, env.World.MapData);
        var house = env.Housing.RegisterExistingMulti(multi)!;
        house.Owner = owner.Uid;
        return (multi, house);
    }

    private static GameClient NewClient(Env env, Character ch, SphereNet.Game.Scripting.TriggerDispatcher? dispatcher,
        int id)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, env.World, new AccountManager(lf), id);
        TestHarness.AttachCharacter(client, ch);
        client.SetEngines(housingEngine: env.Housing, triggerDispatcher: dispatcher, customHousing: env.Custom);
        return client;
    }

    private static string WriteScript(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_hdlife_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, body);
        return path;
    }

    private static bool SawModePacket(NetState state, uint serial, bool begin)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length < 10 || s[0] != 0xBF || s[3] != 0x00 || s[4] != 0x20) continue;
            uint ser = (uint)((s[5] << 24) | (s[6] << 16) | (s[7] << 8) | s[8]);
            if (ser == serial && s[9] == (begin ? 0x04 : 0x05)) return true;
        }
        return false;
    }

    // ---- H01: foundation ------------------------------------------------

    [Fact]
    public void ANewCustomFoundationIsCommittedWithItsVisiblePieces()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var house = env.Housing.PlaceHouse(owner, FoundationId, new Point3D(120, 120, 0, 0),
            customFoundation: true);
        Assert.NotNull(house);

        var committed = env.Custom.GetCommittedDesign(house!.MultiItem);
        Assert.Equal(2, committed.Tiles.Count);   // the invisible component stays out
        Assert.Contains(committed.Tiles, t => t.TileId == FloorTile && t.X == -3 && t.Y == -3 && t.Z == 7);
        Assert.Contains(committed.Tiles, t => t.TileId == FloorTile && t.X == 3 && t.Y == 3 && t.Z == 7);
    }

    [Fact]
    public void ClearPutsTheFoundationBackRatherThanEmptyingTheDesign()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 100, 100, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        Assert.True(env.Custom.Build(owner, WallTile, 1, 1));

        env.Custom.Clear(owner);

        var working = env.Custom.GetSession(owner.Uid)!.Working;
        Assert.Equal(2, working.Tiles.Count);
        Assert.All(working.Tiles, t => Assert.Equal(FloorTile, t.TileId));
    }

    // ---- RemoveItem (Erase) -----------------------------------------------

    private (Env Env, Character Owner, Item Multi) NewDesignSession()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 100, 100, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        return (env, owner, multi);
    }

    [Fact]
    public void EraseMatchesThePieceOnTheSamePlaneNotTheExactZ()
    {
        var (env, owner, _) = NewDesignSession();
        Assert.True(env.Custom.Build(owner, WallTile, 1, 1));          // z 7, plane 1

        Assert.False(env.Custom.Erase(owner, FloorTile, 1, 1, 7));     // wrong id
        Assert.True(env.Custom.Erase(owner, WallTile, 1, 1, 12));      // plane 1 too

        Assert.DoesNotContain(env.Custom.GetSession(owner.Uid)!.Working.Tiles,
            t => t.X == 1 && t.Y == 1);
    }

    [Fact]
    public void AFirstFloorFloorPieceLeavesDirtOnlyAwayFromTheWestAndNorthEdges()
    {
        var (env, owner, _) = NewDesignSession();
        Assert.True(env.Custom.Build(owner, FloorTile, 1, 1));
        Assert.True(env.Custom.Erase(owner, FloorTile, 1, 1, 7));
        var tiles = env.Custom.GetSession(owner.Uid)!.Working.Tiles;
        Assert.Contains(tiles, t => t.TileId == CustomHousingEngine.DirtTile && t.X == 1 && t.Y == 1 && t.Z == 7);

        // (-3,-3) is the design area's north-west corner: no dirt there.
        Assert.True(env.Custom.Erase(owner, FloorTile, -3, -3, 7));
        Assert.DoesNotContain(tiles, t => t.X == -3 && t.Y == -3);

        // The dirt itself cannot be removed on the first floor.
        Assert.False(env.Custom.Erase(owner, CustomHousingEngine.DirtTile, 1, 1, 7));
    }

    [Fact]
    public void AtGroundLevelOnlyTheRowSouthOfTheAreaCanBeErased()
    {
        var (env, owner, _) = NewDesignSession();
        Assert.True(env.Custom.Build(owner, WallTile, 1, 4));          // south step, z 0
        var design = env.Custom.GetSession(owner.Uid)!.Working;
        design.Tiles.Add(new SphereNet.Network.Packets.Outgoing.HouseDesignTile(WallTile, 1, 2, 0));

        Assert.False(env.Custom.Erase(owner, WallTile, 1, 2, 0));      // inside the area
        Assert.True(env.Custom.Erase(owner, WallTile, 1, 4, 0));       // the bottom row
    }

    [Fact]
    public void ErasingAStairPieceTakesTheWholeStaircase()
    {
        var (env, owner, _) = NewDesignSession();
        Assert.True(env.Custom.Stairs(owner, StairMultiId, 0, 2));
        Assert.True(env.Custom.Erase(owner, WallTile, 0, 1, 12));
        Assert.DoesNotContain(env.Custom.GetSession(owner.Uid)!.Working.Tiles, t => t.StairId != 0);
    }

    [Fact]
    public void ScriptRemoveItemHasNoClientRules()
    {
        var (env, owner, multi) = NewDesignSession();
        CustomHousingEngine.Active = env.Custom;
        Assert.True(env.Custom.Build(owner, FloorTile, 1, 1));
        Assert.True(env.Custom.Stairs(owner, StairMultiId, 0, 2));

        // id 0 = any piece on that square/plane; no dirt is left behind.
        Assert.True(multi.TryExecuteCommand("REMOVEITEM", "0,1,1,7", null!));
        // A stair piece goes alone - RemoveStairs is a client-only rule.
        Assert.True(multi.TryExecuteCommand("REMOVEITEM", $"{WallTile},0,1,12", null!));

        var tiles = env.Custom.GetSession(owner.Uid)!.Working.Tiles;
        Assert.DoesNotContain(tiles, t => t.X == 1 && t.Y == 1);
        Assert.DoesNotContain(tiles, t => t.X == 0 && t.Y == 1);
        Assert.Contains(tiles, t => t.X == 0 && t.Y == 2 && t.StairId == 1);
    }

    // ---- H02: roof ------------------------------------------------------

    [Fact]
    public void ARoofOffsetIsAddedToTheCurrentFloor()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 100, 100, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        env.Custom.SetLevel(owner, 2);

        Assert.True(env.Custom.Roof(owner, RoofTile, 1, 1, 3));

        Assert.Contains(env.Custom.GetSession(owner.Uid)!.Working.Tiles,
            t => t.TileId == RoofTile && t.Z == 30);
    }

    [Theory]
    [InlineData(WallTile, 3)]   // not a roof tile
    [InlineData(RoofTile, 100)] // above 12
    [InlineData(RoofTile, -6)]  // below -3
    [InlineData(RoofTile, 4)]   // not a multiple of 3
    public void AnInvalidRoofIsRefusedEvenForAGm(ushort tile, int z)
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 100, 100, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);

        Assert.False(env.Custom.Roof(owner, tile, 1, 1, z));
    }

    // ---- H03: session lifecycle -------------------------------------------

    [Fact]
    public void BeginHidesTheDesignerOnTheBuildingAndAdvancesTheWorkingRevision()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);
        uint mainRevision = uint.Parse(multi.Tags.Get(HouseDesign.RevisionTag)!);

        var session = env.Custom.Begin(owner, multi)!;

        Assert.Equal(new Point3D(100, 100, 7, 0), owner.Position);
        Assert.True(owner.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(mainRevision + 1, session.Working.Revision);
    }

    [Fact]
    public void SwitchingLevelMovesTheDesignerAndIsCappedByFoundationSize()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var (small, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, small);

        env.Custom.SetLevel(owner, 2);
        Assert.Equal(27, owner.Z);

        env.Custom.SetLevel(owner, 4);              // a small foundation has 3
        Assert.Equal(3, env.Custom.GetSession(owner.Uid)!.Level);
        Assert.Equal(47, owner.Z);

        var (large, _) = NewHouse(env, owner, LargeFoundationId, 150, 150);
        env.Custom.Begin(owner, large);
        env.Custom.SetLevel(owner, 4);
        Assert.Equal(4, env.Custom.GetSession(owner.Uid)!.Level);
    }

    [Fact]
    public void CommitKeepsDesignModeOpen()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        Assert.True(env.Custom.Build(owner, WallTile, 1, 1));

        Assert.NotNull(env.Custom.Commit(owner));

        Assert.NotNull(env.Custom.GetSession(owner.Uid));
        Assert.True(owner.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void DisconnectEndsDesignMode()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);
        var client = NewClient(env, owner, null, 7001);
        client.BeginHouseCustomization(multi);
        Assert.NotNull(env.Custom.GetSession(owner.Uid));

        client.OnDisconnect();

        Assert.Null(env.Custom.GetSession(owner.Uid));
        Assert.False(owner.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(Serial.Invalid, env.Custom.GetDesigner(multi));
    }

    [Fact]
    public void AGmTakingOverEndsTheOwnersSessionAndTellsTheirClient()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var gm = NewChar(env.World, 60, 60, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        var ownerClient = NewClient(env, owner, null, 7002);
        var gmClient = NewClient(env, gm, null, 7003);
        ownerClient.BeginHouseCustomization(multi);
        TestHarness.ClearQueuedPackets(ownerClient.NetState);

        gmClient.BeginHouseCustomization(multi);

        Assert.Null(env.Custom.GetSession(owner.Uid));
        Assert.NotNull(env.Custom.GetSession(gm.Uid));
        Assert.Equal(gm.Uid, env.Custom.GetDesigner(multi));
        Assert.True(SawModePacket(ownerClient.NetState, multi.Uid.Value, begin: false));
        Assert.False(owner.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void DeletingTheBuildingOnlyDropsTheDesignersLink()
    {
        // ~CItemMultiCustom (CItemMultiCustom.cpp:43) just clears the architect
        // client's m_pHouseDesign: no mode-end packet, no @HouseDesignExit, no reveal.
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);
        var client = NewClient(env, owner, null, 7004);
        client.BeginHouseCustomization(multi);
        TestHarness.ClearQueuedPackets(client.NetState);

        env.World.RemoveItem(multi);

        Assert.Null(env.Custom.GetSession(owner.Uid));
        Assert.Null(env.Custom.GetSessionMulti(owner.Uid));
        Assert.False(SawModePacket(client.NetState, multi.Uid.Value, begin: false));
    }

    [Fact]
    public void WithASecuredContainerPresentLockdownsOnlyLeaveTheList()
    {
        // TransferSecuredToMovingCrate clears _lLockDowns instead of the secure list
        // (CItemMulti.cpp:1494), so TransferLockdownsToMovingCrate then finds nothing.
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_hdbegin_n2]
            ON=@HouseDesignBegin
            ARGN2=1
            ARGN3=0
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            stack.Dispatcher.BuildUsedTriggerCache();
            var env = NewEnv();
            var owner = NewChar(env.World, 50, 50);
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdbegin_n2"));
            var (multi, house) = NewHouse(env, owner);
            var chest = env.World.CreateItem();
            chest.BaseId = 0x0E40;
            chest.ItemType = ItemType.Container;
            env.World.PlaceItem(chest, new Point3D(99, 99, 7, 0));
            Assert.True(house.SecureContainer(chest.Uid, owner.Uid));
            var locked = env.World.CreateItem();
            env.World.PlaceItem(locked, new Point3D(101, 101, 7, 0));
            Assert.True(house.Lockdown(locked.Uid, owner.Uid));

            NewClient(env, owner, stack.Dispatcher, 7012).BeginHouseCustomization(multi);

            var crate = house.ResolveMovingCrate()!;
            Assert.Equal(crate.Uid, chest.ContainedIn);
            Assert.False(chest.IsAttr(ObjAttributes.Secure));
            Assert.False(house.IsLockedDown(locked.Uid));
            Assert.True(locked.IsOnGround);
            Assert.True(locked.IsAttr(ObjAttributes.LockedDown));
        }
        finally { File.Delete(path); }
    }

    // ---- H04: @HouseDesignBegin read-back ---------------------------------

    [Fact]
    public void HouseDesignBeginArgumentsAreReadBackAndActedOn()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_hdbegin_args]
            ON=@HouseDesignBegin
            ARGN1=0
            ARGN2=1
            ARGN3=1
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            stack.Dispatcher.BuildUsedTriggerCache();
            var env = NewEnv();
            var owner = NewChar(env.World, 50, 50);
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdbegin_args"));
            var (multi, house) = NewHouse(env, owner);
            var sign = env.World.CreateItem();
            sign.ItemType = ItemType.SignGump;
            env.World.PlaceItem(sign, new Point3D(100, 105, 0, 0));
            multi.Link = sign.Uid;

            var locked = env.World.CreateItem();
            locked.BaseId = 0x0E75;
            env.World.PlaceItem(locked, new Point3D(101, 101, 7, 0));
            Assert.True(house.Lockdown(locked.Uid, owner.Uid));
            var guest = NewChar(env.World, 102, 102);

            var client = NewClient(env, owner, stack.Dispatcher, 7005);
            client.BeginHouseCustomization(multi);

            // N2=1: lockdowns go into the moving crate and stop being lockdowns.
            Assert.False(house.IsLockedDown(locked.Uid));
            var crate = house.ResolveMovingCrate();
            Assert.NotNull(crate);
            Assert.Equal(crate!.Uid, locked.ContainedIn);
            // N3=1: everyone but the designer is ejected to the sign.
            Assert.Equal(100, guest.X);
            Assert.Equal(105, guest.Y);
            Assert.NotNull(env.Custom.GetSession(owner.Uid));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithoutAHookedBeginTriggerNothingIsTransferredOrEjected()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Dispatcher.BuildUsedTriggerCache();
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50);
        var (multi, house) = NewHouse(env, owner);
        var locked = env.World.CreateItem();
        env.World.PlaceItem(locked, new Point3D(101, 101, 7, 0));
        Assert.True(house.Lockdown(locked.Uid, owner.Uid));
        var guest = NewChar(env.World, 102, 102);

        var client = NewClient(env, owner, stack.Dispatcher, 7006);
        client.BeginHouseCustomization(multi);

        Assert.True(house.IsLockedDown(locked.Uid));
        Assert.Equal(102, guest.X);
    }

    // ---- H05: @HouseDesignExit veto ---------------------------------------

    [Fact]
    public void ExitReturnOneKeepsDesignModeButAForcedExitCannotBeVetoed()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_hdexit_veto]
            ON=@HouseDesignExit
            TAG.EXIT_FORCED=<ARGN1>
            RETURN 1
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            var env = NewEnv();
            var owner = NewChar(env.World, 50, 50);
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdexit_veto"));
            var (multi, _) = NewHouse(env, owner);
            var client = NewClient(env, owner, stack.Dispatcher, 7007);
            client.BeginHouseCustomization(multi);

            client.HandleEncodedCommand(EncodedCommandRegistry.Close, multi.Uid.Value,
                new PacketBuffer(Array.Empty<byte>()));

            Assert.Equal("0", owner.Tags.Get("EXIT_FORCED"));
            Assert.NotNull(env.Custom.GetSession(owner.Uid));   // back in design mode
            Assert.True(owner.IsStatFlag(StatFlag.Hidden));

            client.OnDisconnect();

            Assert.Equal("1", owner.Tags.Get("EXIT_FORCED"));
            Assert.Null(env.Custom.GetSession(owner.Uid));
            Assert.False(owner.IsStatFlag(StatFlag.Hidden));
        }
        finally { File.Delete(path); }
    }

    // ---- H06: CommitItem filter before the commit trigger -----------------

    [Fact]
    public void TheCommitTriggerSeesTheDesignAfterThePerPieceFilter()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_hdcommit_after_filter]
            ON=@HouseDesignCommit
            TAG.SEEN_NEW=<ARGN2>
            TAG.SEEN_MAXZ=<LOCAL.MAXZ>
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            var env = NewEnv();
            var owner = NewChar(env.World, 50, 50, PrivLevel.GM);
            owner.Events.Add(stack.Resources.ResolveDefName("e_hdcommit_after_filter"));
            var (multi, _) = NewHouse(env, owner);
            var client = NewClient(env, owner, stack.Dispatcher, 7008);
            client.BeginHouseCustomization(multi);
            Assert.True(env.Custom.Build(owner, WallTile, 1, 1));   // z 7
            env.Custom.SetLevel(owner, 2);
            Assert.True(env.Custom.Build(owner, DoorTile, 1, 1));   // z 27, filtered out
            CustomHousingEngine.KeepCommitItem = (_, _, tile) => tile.TileId != DoorTile;

            client.HandleEncodedCommand(EncodedCommandRegistry.Commit, multi.Uid.Value,
                new PacketBuffer(Array.Empty<byte>()));

            // foundation (2) + the wall; the door was removed before the counting.
            Assert.Equal("3", owner.Tags.Get("SEEN_NEW"));
            Assert.Equal("07", owner.Tags.Get("SEEN_MAXZ"));   // <LOCAL.MAXZ> reads in Sphere hex
            Assert.DoesNotContain(env.Custom.GetCommittedTiles(multi), t => t.TileId == DoorTile);
            Assert.Equal(0, env.Custom.CountFixtures(env.Custom.GetCommittedDesign(multi)));
        }
        finally { File.Delete(path); }
    }

    // ---- H07: fixtures ----------------------------------------------------

    [Fact]
    public void ACommittedDoorStartsLockedWithHouseEventsAndOpensOnlyWithTheKey()
    {
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        Assert.True(env.Custom.Build(owner, DoorTile, 1, 1));
        Assert.NotNull(env.Custom.Commit(owner));
        env.Custom.End(owner, forced: true);

        var door = env.World.GetItemsInRange(new Point3D(101, 101, 7, 0), 0)
            .Single(i => i.BaseId == DoorTile && !i.IsDeleted);
        Assert.Equal(ItemType.DoorLocked, door.ItemType);
        Assert.Equal(multi.Uid, door.Link);
        Assert.Contains(ResourceId.FromString(CustomHousingEngine.ComponentEvent, ResType.Events), door.Events);
        Assert.Contains(ResourceId.FromString(CustomHousingEngine.DoorEvent, ResType.Events), door.Events);

        // A stranger cannot open it.
        var stranger = NewChar(env.World, 102, 101);
        var strangerClient = NewClient(env, stranger, null, 7009);
        strangerClient.HandleDoubleClick(door.Uid.Value);
        Assert.False(door.TryGetTag("DOOR_OPEN", out _));

        // The holder of the house key can.
        var keyHolder = NewChar(env.World, 102, 102);
        var pack = env.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        keyHolder.Equip(pack, Layer.Pack);
        var key = env.World.CreateItem();
        key.BaseId = 0x100F;
        key.ItemType = ItemType.Key;
        key.Link = multi.Uid;
        pack.TryAddItem(key);
        var keyClient = NewClient(env, keyHolder, null, 7010);
        keyClient.HandleDoubleClick(door.Uid.Value);
        Assert.True(door.TryGetTag("DOOR_OPEN", out _));
        Assert.Equal(ItemType.DoorLocked, door.ItemType);   // opening does not unlock
    }

    [Fact]
    public void TelepadsBecomeRealItemsPointingAtTheirPartner()
    {
        SphereNet.Game.Definitions.DefinitionLoader.SetItemDef(TelepadTile,
            new SphereNet.Scripting.Definitions.ItemDef(ResourceId.Invalid)
            { DispIndex = TelepadTile, Type = ItemType.Telepad });
        var env = NewEnv();
        var owner = NewChar(env.World, 50, 50, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        Assert.True(env.Custom.Build(owner, TelepadTile, 1, 1));
        Assert.True(env.Custom.Build(owner, TelepadTile, -2, 2));
        Assert.NotNull(env.Custom.Commit(owner));

        var pads = env.World.GetItemsInRange(multi.Position, 5)
            .Where(i => i.BaseId == TelepadTile && !i.IsDeleted).ToList();
        Assert.Equal(2, pads.Count);
        var a = pads.Single(p => p.X == 101);
        var b = pads.Single(p => p.X == 98);
        Assert.All(pads, p => Assert.Equal(ItemType.Telepad, p.ItemType));
        Assert.Equal(new Point3D(98, 102, 7, 0), a.MoreP);
        Assert.Equal(new Point3D(101, 101, 7, 0), b.MoreP);
        Assert.Contains(ResourceId.FromString(CustomHousingEngine.TelepadEvent, ResType.Events), a.Events);
        Assert.False(a.Link.IsValid);   // a link would teleport to the house centre
        Assert.Equal(2, env.Custom.CountFixtures(env.Custom.GetCommittedDesign(multi)));
    }

    // ---- H08: properties --------------------------------------------------

    [Fact]
    public void DesignPropertiesReadTheLiveModel()
    {
        var env = NewEnv();
        CustomHousingEngine.Active = env.Custom;
        var owner = NewChar(env.World, 50, 50, PrivLevel.GM);
        var (multi, _) = NewHouse(env, owner);
        env.Custom.Begin(owner, multi);
        Assert.True(env.Custom.Build(owner, DoorTile, 1, 1));
        Assert.True(env.Custom.Stairs(owner, StairMultiId, -1, 1));   // stair id 1, not a fixture
        uint revision = env.Custom.Commit(owner)!.Value;

        Assert.True(multi.TryGetProperty("DESIGNER", out string designer));
        Assert.Equal($"0{owner.Uid.Value:X}", designer);
        Assert.True(multi.TryGetProperty("REVISION", out string rev));
        Assert.Equal(revision.ToString(), rev);
        Assert.True(multi.TryGetProperty("COMPONENTS", out string comps));
        Assert.Equal("5", comps);   // 2 foundation + door + 2 stair pieces; no revision tag
        Assert.True(multi.TryGetProperty("FIXTURES", out string fixtures));
        Assert.Equal("1", fixtures);
        Assert.True(multi.TryGetProperty("DESIGN.2.ID", out string id));
        Assert.Equal(DoorTile.ToString(), id);
        Assert.True(multi.TryGetProperty("DESIGN.2.FIXTURE", out string isFixture));
        Assert.Equal("1", isFixture);

        env.Custom.End(owner, forced: true);
        Assert.True(multi.TryGetProperty("DESIGNER", out designer));
        Assert.Equal(0, Convert.ToInt32(designer, 16));
    }

    // ---- H09: script verbs ------------------------------------------------

    [Fact]
    public void ScriptAddItemAndCommitGoThroughTheEngine()
    {
        var env = NewEnv();
        CustomHousingEngine.Active = env.Custom;
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);
        uint before = uint.Parse(multi.Tags.Get(HouseDesign.RevisionTag)!);
        uint? broadcast = null;
        env.Custom.DesignCommitted = (_, r) => broadcast = r;

        Assert.True(multi.TryExecuteCommand("ADDITEM", $"{DoorTile},1,1,7", null!));
        Assert.Equal(2, env.Custom.GetCommittedTiles(multi).Count);   // not committed yet
        Assert.True(multi.TryExecuteCommand("COMMIT", "", null!));

        var committed = env.Custom.GetCommittedDesign(multi);
        Assert.Equal(3, committed.Tiles.Count);
        Assert.True(uint.Parse(multi.Tags.Get(HouseDesign.RevisionTag)!) > before);
        Assert.Equal(uint.Parse(multi.Tags.Get(HouseDesign.RevisionTag)!), broadcast);
        // The door became a real, locked fixture.
        Assert.Contains(env.World.GetItemsInRange(new Point3D(101, 101, 7, 0), 0),
            i => i.BaseId == DoorTile && i.ItemType == ItemType.DoorLocked);
    }

    [Fact]
    public void ScriptAddMultiExpandsTheMultiUnderOneStairId()
    {
        var env = NewEnv();
        CustomHousingEngine.Active = env.Custom;
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);

        Assert.True(multi.TryExecuteCommand("ADDMULTI", $"0{0x4000 + StairMultiId:X},2,2,7", null!));
        Assert.True(multi.TryExecuteCommand("COMMIT", "", null!));

        var tiles = env.Custom.GetCommittedTiles(multi);
        Assert.Contains(tiles, t => t.TileId == WallTile && t.X == 2 && t.Y == 2 && t.Z == 7 && t.StairId == 1);
        Assert.Contains(tiles, t => t.TileId == WallTile && t.X == 2 && t.Y == 1 && t.Z == 12 && t.StairId == 1);
    }

    [Fact]
    public void ScriptCustomizeAndEndCustomizeRunTheRealSession()
    {
        var env = NewEnv();
        CustomHousingEngine.Active = env.Custom;
        var owner = NewChar(env.World, 50, 50);
        var (multi, _) = NewHouse(env, owner);
        var client = NewClient(env, owner, null, 7011);
        SphereNet.Game.Objects.ObjBase.ResolveClientConsole = ch => ch == owner ? client : null;

        Assert.True(multi.TryExecuteCommand("CUSTOMIZE", $"0{owner.Uid.Value:X}", null!));
        Assert.NotNull(env.Custom.GetSession(owner.Uid));
        Assert.True(SawModePacket(client.NetState, multi.Uid.Value, begin: true));

        Assert.True(multi.TryExecuteCommand("ENDCUSTOMIZE", "", null!));
        Assert.Null(env.Custom.GetSession(owner.Uid));
        Assert.True(SawModePacket(client.NetState, multi.Uid.Value, begin: false));
    }
}
