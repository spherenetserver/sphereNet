using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Custom-house design editor: 0xD8 stream byte layout (verified by parsing
/// it back exactly the way the ClassicUO client does), DESIGN_n tag
/// persistence, and the CustomHousingEngine session state machine.
/// </summary>
public class CustomHouseDesignTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    // ---- 0xD8 packet layout ----

    private static List<HouseDesignTile> ParseDesignDetailed(PacketBuffer built,
        out uint serial, out uint revision, out int tileCount)
    {
        var span = built.Span;
        Assert.Equal(0xD8, span[0]);
        int len = (span[1] << 8) | span[2];
        Assert.Equal(span.Length, len);

        Assert.Equal(0x03, span[3]); // compression
        Assert.Equal(0x00, span[4]); // enable response
        serial = (uint)((span[5] << 24) | (span[6] << 16) | (span[7] << 8) | span[8]);
        revision = (uint)((span[9] << 24) | (span[10] << 16) | (span[11] << 8) | span[12]);
        tileCount = (span[13] << 8) | span[14];
        int planeCount = span[17];

        byte[] data = span.ToArray();
        var tiles = new List<HouseDesignTile>();
        int o = 18;
        for (int p = 0; p < planeCount; p++)
        {
            uint header = (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
            o += 4;
            // ClassicUO's exact field extraction:
            int dlen = (int)(((header & 0xFF0000) >> 16) | ((header & 0xF0) << 4));
            int clen = (int)(((header & 0xFF00) >> 8) | ((header & 0x0F) << 8));
            int mode = (int)((header & 0xF0000000) >> 28);
            Assert.Equal(0, mode);

            byte[] raw = ZlibUtil.Decompress(data, o, clen);
            Assert.Equal(dlen, raw.Length);
            o += clen;

            for (int i = 0; i + 5 <= raw.Length; i += 5)
            {
                tiles.Add(new HouseDesignTile(
                    (ushort)((raw[i] << 8) | raw[i + 1]),
                    (sbyte)raw[i + 2], (sbyte)raw[i + 3], (sbyte)raw[i + 4]));
            }
        }
        Assert.Equal(span.Length, o);
        return tiles;
    }

    [Fact]
    public void DesignDetailed_RoundTrips_ClassicUOParse()
    {
        var tiles = new List<HouseDesignTile>
        {
            new(0x0064, -3, -3, 7),
            new(0x0066, 4, -3, 7),
            new(0x4000, 0, 0, 27),
            new(0x0709, -7, 5, 0),
        };
        var built = new PacketHouseDesignDetailed(0x40001234, 5, tiles).Build();

        var parsed = ParseDesignDetailed(built, out uint serial, out uint revision, out int tileCount);

        Assert.Equal(0x40001234u, serial);
        Assert.Equal(5u, revision);
        Assert.Equal(tiles.Count, tileCount);
        Assert.Equal(tiles, parsed);
    }

    [Fact]
    public void DesignDetailed_SplitsLargeDesignsAcrossPlanes()
    {
        // 1600 tiles > 750/plane → 3 planes, all surviving the round trip.
        var tiles = new List<HouseDesignTile>();
        for (int i = 0; i < 1600; i++)
            tiles.Add(new HouseDesignTile((ushort)(0x100 + (i % 200)), (sbyte)(i % 15 - 7), (sbyte)(i / 120 - 6), 7));

        var built = new PacketHouseDesignDetailed(1, 1, tiles).Build();
        var parsed = ParseDesignDetailed(built, out _, out _, out int tileCount);

        Assert.Equal(1600, tileCount);
        Assert.Equal(tiles, parsed);
    }

    [Fact]
    public void DesignDetailed_EmptyDesign_HasZeroPlanes()
    {
        var built = new PacketHouseDesignDetailed(1, 1, []).Build();
        var parsed = ParseDesignDetailed(built, out _, out _, out int tileCount);
        Assert.Equal(0, tileCount);
        Assert.Empty(parsed);
    }

    // ---- DESIGN_n tag persistence ----

    [Fact]
    public void HouseDesign_TagRoundTrip_PreservesTilesAndRevision()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();

        var design = new HouseDesign { Revision = 7 };
        design.Tiles.Add(new HouseDesignTile(0x0064, -3, 2, 7));
        design.Tiles.Add(new HouseDesignTile(0x4000, 1, -5, 27));
        design.SaveToTags(multi);

        var loaded = HouseDesign.LoadFromTags(multi);
        Assert.Equal(7u, loaded.Revision);
        Assert.Equal(design.Tiles, loaded.Tiles);
    }

    [Fact]
    public void HouseDesign_LoadsScriptAdditemEntries()
    {
        // The script ADDITEM verb writes "tileId,dx,dy,dz,flags" with the id
        // in whatever form the script used — decimal or hex.
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.Tags.Set("DESIGN_0", "100,1,2,7,0");
        multi.Tags.Set("DESIGN_1", "0x4000,-3,-4,27,0");

        var loaded = HouseDesign.LoadFromTags(multi);
        Assert.Equal(2, loaded.Tiles.Count);
        Assert.Equal(new HouseDesignTile(100, 1, 2, 7), loaded.Tiles[0]);
        Assert.Equal(new HouseDesignTile(0x4000, -3, -4, 27), loaded.Tiles[1]);
    }

    // ---- Session state machine ----

    private static (CustomHousingEngine Engine, Character Ch, SphereNet.Game.Objects.Items.Item Multi)
        CreateSession()
    {
        var world = CreateWorld();
        var housing = new HousingEngine(world, new MultiRegistry());
        var engine = new CustomHousingEngine(world, housing);
        var ch = world.CreateCharacter();
        var multi = world.CreateItem();
        engine.Begin(ch, multi);
        return (engine, ch, multi);
    }

    [Fact]
    public void Session_BuildCommit_PersistsDesignAndBumpsRevision()
    {
        var (engine, ch, multi) = CreateSession();

        Assert.True(engine.Build(ch, 0x0064, 2, 3));      // story 1 → z 7
        engine.SetLevel(ch, 2);
        Assert.True(engine.Build(ch, 0x0066, 2, 3));      // story 2 → z 27
        Assert.True(engine.Stairs(ch, 0x0709, -7, 5));    // ground → z 0

        uint? revision = engine.Commit(ch);
        Assert.Equal(2u, revision); // initial revision 1 + 1
        Assert.Null(engine.GetSession(ch.Uid)); // session ended

        var committed = HouseDesign.LoadFromTags(multi);
        Assert.Equal(2u, committed.Revision);
        Assert.Contains(new HouseDesignTile(0x0064, 2, 3, 7), committed.Tiles);
        Assert.Contains(new HouseDesignTile(0x0066, 2, 3, 27), committed.Tiles);
        Assert.Contains(new HouseDesignTile(0x0709, -7, 5, 0), committed.Tiles);
    }

    [Fact]
    public void Build_RejectsInvalidGraphics_AndHonorsWhitelist()
    {
        var (engine, ch, _) = CreateSession(); // ch is a normal (non-GM) designer

        // Source-X IsValidItem range check: zero and multi-range graphics are
        // never valid design pieces — a crafted 0xD7 packet cannot smuggle them.
        Assert.False(engine.Build(ch, 0x0000, 2, 3));
        Assert.False(engine.Build(ch, HouseDesignValidItems.ItemIdMulti, 2, 3));
        Assert.False(engine.Build(ch, 0x8000, 2, 3));

        // In-range static graphic accepted while no whitelist is configured.
        Assert.True(engine.Build(ch, 0x0064, 2, 3));

        // Registering a whitelist restricts non-GM placement to known pieces.
        HouseDesignValidItems.RegisterValidItems([0x0066]);
        Assert.False(engine.Build(ch, 0x0067, 2, 4)); // not listed
        Assert.True(engine.Build(ch, 0x0066, 2, 5));  // listed
    }

    [Fact]
    public void Build_GmBypassesWhitelistButNotRange()
    {
        var (engine, ch, _) = CreateSession();
        ch.PrivLevel = SphereNet.Core.Enums.PrivLevel.GM;
        HouseDesignValidItems.RegisterValidItems([0x0066]); // lock others out for non-GMs

        Assert.True(engine.Build(ch, 0x0099, 2, 3));  // GM places an unlisted in-range piece
        Assert.False(engine.Build(ch, 0x9000, 2, 4)); // still rejected: not a static graphic
    }

    [Fact]
    public void Session_Erase_RemovesMatchingTileOnly()
    {
        var (engine, ch, _) = CreateSession();
        engine.Build(ch, 0x0064, 2, 3);
        engine.Build(ch, 0x0064, 2, 4);

        Assert.True(engine.Erase(ch, 0x0064, 2, 3, 7));
        Assert.False(engine.Erase(ch, 0x0064, 2, 3, 7)); // already gone

        var session = engine.GetSession(ch.Uid)!;
        Assert.Single(session.Working.Tiles);
        Assert.Equal(new HouseDesignTile(0x0064, 2, 4, 7), session.Working.Tiles[0]);
    }

    [Fact]
    public void Session_Revert_DiscardsWorkingChanges()
    {
        var (engine, ch, multi) = CreateSession();
        engine.Build(ch, 0x0064, 2, 3);
        engine.Commit(ch);

        engine.Begin(ch, multi);
        engine.Build(ch, 0x0066, 5, 5);
        engine.Clear(ch);
        Assert.Empty(engine.GetSession(ch.Uid)!.Working.Tiles);

        engine.Revert(ch);
        var working = engine.GetSession(ch.Uid)!.Working;
        Assert.Single(working.Tiles); // back to the committed design
        Assert.Equal(new HouseDesignTile(0x0064, 2, 3, 7), working.Tiles[0]);
    }

    [Fact]
    public void Session_BackupRestore_RoundTrips()
    {
        var (engine, ch, _) = CreateSession();
        engine.Build(ch, 0x0064, 2, 3);
        engine.BackupDesign(ch);
        engine.Clear(ch);
        Assert.Empty(engine.GetSession(ch.Uid)!.Working.Tiles);

        engine.RestoreDesign(ch);
        Assert.Single(engine.GetSession(ch.Uid)!.Working.Tiles);
    }

    [Fact]
    public void Session_DuplicateBuild_DoesNotStack()
    {
        var (engine, ch, _) = CreateSession();
        engine.Build(ch, 0x0064, 2, 3);
        engine.Build(ch, 0x0064, 2, 3); // repeated client click
        Assert.Single(engine.GetSession(ch.Uid)!.Working.Tiles);
    }

    [Fact]
    public void LevelToZ_MatchesClientPlaneTransform()
    {
        // ClassicUO: z = (plane - 1) % 4 * 20 + 7
        Assert.Equal(7, CustomHousingEngine.LevelToZ(1));
        Assert.Equal(27, CustomHousingEngine.LevelToZ(2));
        Assert.Equal(47, CustomHousingEngine.LevelToZ(3));
        Assert.Equal(67, CustomHousingEngine.LevelToZ(4));
    }

    // ---- Foundation placement + walk-geometry feed ----

    [Fact]
    public void PlaceHouse_CustomFoundation_NoComponentItems_RevisionInitialized()
    {
        var world = CreateWorld();
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x1404, Name = "foundation 7x7" };
        def.Components.Add(new MultiComponent { TileId = 0x0064, DeltaX = -3, DeltaY = -3, DeltaZ = 0, Visible = false });
        def.RecalcBounds();
        registry.Register(def);
        var housing = new HousingEngine(world, registry);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        var pos = new SphereNet.Core.Types.Point3D(1500, 1500, 0, 0);

        int itemsBefore = world.GetAllObjects().OfType<SphereNet.Game.Objects.Items.Item>().Count();
        var house = housing.PlaceHouse(owner, 0x1404, pos, customFoundation: true);

        Assert.NotNull(house);
        var multi = house!.MultiItem;
        Assert.Equal(SphereNet.Core.Enums.ItemType.MultiCustom, multi.ItemType);
        // The multi item + the two house keys (W-G: pack + bank spare) — design
        // tiles stay virtual, no component items are created.
        int itemsAfter = world.GetAllObjects().OfType<SphereNet.Game.Objects.Items.Item>().Count();
        Assert.Equal(itemsBefore + 3, itemsAfter);
        Assert.True(multi.TryGetTag(HouseDesign.RevisionTag, out string? rev));
        Assert.Equal("1", rev);

        // Regular placement of the same def DOES materialize components.
        var owner2 = world.CreateCharacter();
        owner2.IsPlayer = true;
        var house2 = housing.PlaceHouse(owner2, 0x1404, new SphereNet.Core.Types.Point3D(1600, 1600, 0, 0));
        Assert.NotNull(house2);
        Assert.Equal(SphereNet.Core.Enums.ItemType.Multi, house2!.MultiItem.ItemType);
        Assert.Single(house2.Components);
    }

    [Fact]
    public void GetCommittedTiles_CachesByRevision_AndInvalidatesOnCommit()
    {
        var world = CreateWorld();
        var housing = new HousingEngine(world, new MultiRegistry());
        var engine = new CustomHousingEngine(world, housing);
        var ch = world.CreateCharacter();
        var multi = world.CreateItem();

        engine.Begin(ch, multi);
        engine.Build(ch, 0x0064, 2, 3);
        engine.Commit(ch);

        var tiles1 = engine.GetCommittedTiles(multi);
        Assert.Single(tiles1);
        // Same revision → same cached instance.
        Assert.Same(tiles1, engine.GetCommittedTiles(multi));

        engine.Begin(ch, multi);
        engine.Build(ch, 0x0066, 4, 4);
        engine.Commit(ch); // revision bump invalidates the cache

        var tiles2 = engine.GetCommittedTiles(multi);
        Assert.Equal(2, tiles2.Count);
    }
}
