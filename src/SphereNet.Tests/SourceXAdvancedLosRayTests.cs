using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// ADVANCEDLOS against Source-X CChar::CanSeeLOS_New (CCharLOS.cpp:112-656) and the
/// CANSEELOS / CANSEELOSFLAG script reads (CObjBase.cpp:1109-1150): the eye-height
/// 3D ray, the occluder rules for statics / world items / multi components, the 13
/// LOS_* flags (CChar.h:424-436), the player/NPC ADVANCEDLOS bit, and LOS_FISHING.
/// Each case is a counter-example the previous implementation got wrong.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXAdvancedLosRayTests
{
    private const ushort ArtA = 0x0080;
    private const ushort ArtB = 0x0081;
    private const ushort DirtLand = 0x0003;

    private static GameWorld World(int advanced, ushort landTile = DirtLand)
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: landTile);
        var w = new GameWorld(NullLoggerFactory.Instance);
        w.InitMap(0, 256, 256);
        w.MapData = md;
        w.AdvancedLos = advanced;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Character Char(GameWorld w, short x = 100, short y = 100, sbyte z = 0, bool player = true)
    {
        var c = w.CreateCharacter();
        c.IsPlayer = player;
        c.BodyId = 0x190;
        w.PlaceCharacter(c, new Point3D(x, y, z, 0));
        return c;
    }

    /// <summary>An art id with tiledata and a scripted ITEMDEF (HEIGHT=, CAN bits).</summary>
    private static void Art(GameWorld w, ushort id, TileFlag flags, byte height,
        CanFlags can = CanFlags.None, byte? defHeight = null)
    {
        w.MapData!.SetSyntheticItemTile(id, new ItemTileData { Flags = flags, Height = height, Name = "probe" });
        DefinitionLoader.SetItemDef(id, new ItemDef(ResourceId.Invalid)
        { DispIndex = id, Can = can, Height = defHeight ?? height });
    }

    private static void Static(GameWorld w, short x, ushort id, sbyte z = 0) =>
        w.MapData!.AddSyntheticStatic(0, x, 100, id, z);

    private static Item ItemAt(GameWorld w, ushort id, short x = 103, sbyte z = 0)
    {
        var i = w.CreateItem();
        i.BaseId = id;
        w.PlaceItem(i, new Point3D(x, 100, z, 0));
        return i;
    }

    private static string Read(Character c, string key)
    {
        Assert.True(c.TryGetProperty(key, out string value));
        return value;
    }

    // ---------------------------------------------------------------- #12 the ray

    [Fact]
    public void Ray_RunsFromTheViewersEyesDownToThePointsOwnZ()
    {
        // Eyes at 0 + 16 - 1 = 15 (GetHeightMount(true), CCharLOS.cpp:132); the ray
        // descends to the point's Z 0 (:139), crossing x=103 near z 7, inside a
        // 0..10 wall. The old ray aimed at z+16 at both ends and passed over it.
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 10);
        Static(w, 103, ArtA);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Ray_UsesTheViewersOwnHeight()
    {
        // HEIGHT=40: eyes at 39; the first tile (x=101) is crossed near z 33, above a
        // 0..20 wall. A fixed 16-high body was blocked by it.
        var w = World(1);
        var c = Char(w);
        c.HeightOverride = 40;
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        Static(w, 101, ArtA);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));

        c.HeightOverride = 0; // default 16: eyes at 15, inside the wall
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Ray_AimsAtACharacterTargetsEyes()
    {
        // CanSeeLOS(pObj) raises a character target by ITS GetHeightMount(true)
        // (CCharLOS.cpp:678-683): a 40-high target's eyes at 39 lift the ray over a
        // 0..16 wall at x=103; a 16-high target's eyes at 15 keep it inside.
        var w = World(1);
        var c = Char(w);
        var tall = Char(w, 106);
        tall.HeightOverride = 40;
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 16);
        Static(w, 103, ArtA);
        Assert.True(w.CanSeeLOSFor(c, tall));
        tall.HeightOverride = 0;
        Assert.False(w.CanSeeLOSFor(c, tall));
    }

    [Fact]
    public void Ray_SameXyDifferentZ_IsTestedLikeAnyOtherPath()
    {
        // No same-X/Y shortcut: from eyes at 15 straight up to z40, a 20..25 floor in
        // between occludes (CCharLOS.cpp:129 only exempts the identical point).
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Surface, 5);
        w.MapData!.AddSyntheticStatic(0, 100, 100, ArtA, 20);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,100,100,40,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,100,100,0,0")); // the point I stand on
    }

    // ------------------------------------------------------------ #13 occluders

    [Fact]
    public void Occluder_PlatformSurfaceBlocksWhereTheRayCrossesIt()
    {
        // UFLAG2_PLATFORM is one of the occluding tile flags (CCharLOS.cpp:400): the
        // ray crosses x=103 near z 7, inside a 4..9 platform.
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Surface, 5);
        Static(w, 103, ArtA, 4);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Occluder_BlockLosBlocksAtAnyHeight_BlockLosHeightOnlyInItsSpan()
    {
        // CAN_I_BLOCKLOS blocks regardless of Z (CCharLOS.cpp:457-461); the
        // CAN_I_BLOCKLOS_HEIGHT item blocks only inside its own z..z+height (:506).
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.None, 1, CanFlags.I_BlockLOS);
        var low = ItemAt(w, ArtA, z: -50);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));

        low.Delete();
        Art(w, ArtB, TileFlag.None, 20, CanFlags.I_BlockLOSHeight);
        var tall = ItemAt(w, ArtB, z: -50);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        tall.Delete();
        ItemAt(w, ArtB, z: 0);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Occluder_ItemdefHeightReplacesTheTiledataHeight()
    {
        // pItemDef->GetHeight() (CCharLOS.cpp:474): HEIGHT=20 on a wall whose
        // tiledata says 1 makes it 0..20, which the ray at z ~7 crosses.
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall, 1, defHeight: 20);
        ItemAt(w, ArtA);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Occluder_WorldItemIsMeasuredByTheArtItIsDisplayedAs()
    {
        // A changed DISPID: the displayed art's definition gives the flags and
        // height (CCharLOS.cpp:476-491).
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.None, 0);
        Art(w, ArtB, TileFlag.Wall | TileFlag.Impassable, 20);
        var item = ItemAt(w, ArtA);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.True(item.TrySetProperty("DISPID", "081"));
        Assert.Equal((ushort)ArtB, item.DispIdFull);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    [Fact]
    public void Occluder_RoofOnlyTileIsNotAnOccluder()
    {
        // The occluding tile flags are WALL, BLOCK and PLATFORM (CCharLOS.cpp:400);
        // a tile that is only a roof is looked through.
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Roof, 10);
        Static(w, 103, ArtA);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
    }

    // ---------------------------------------------------------------- #14 windows

    [Fact]
    public void Window_BlocksUnlessLosNbWindows()
    {
        // !((qwTFlags & UFLAG2_WINDOW) && (flags & LOS_NB_WINDOWS)) (CCharLOS.cpp:400).
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Window, 20);
        Static(w, 103, ArtA);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0100,106,100,0,0"));
    }

    // ---------------------------------------------------------- #15 the LOS flags

    [Fact]
    public void Flags_NbStaticAndNbDynamicSkipTheirPass()
    {
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        Static(w, 103, ArtA);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 020,106,100,0,0"));   // LOS_NB_STATIC
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 040,106,100,0,0"));   // the static still blocks

        var w2 = World(1);
        var c2 = Char(w2);
        Art(w2, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        ItemAt(w2, ArtA);
        Assert.Equal("0", Read(c2, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("1", Read(c2, "CANSEELOSFLAG 040,106,100,0,0"));  // LOS_NB_DYNAMIC
    }

    [Fact]
    public void Flags_NbTerrainLetsTheRayThroughTheGround()
    {
        // A point 20 under the ground: the ray meets the z0 terrain at x=103
        // (CCharLOS.cpp:316) unless LOS_NB_TERRAIN.
        var w = World(1);
        var c = Char(w);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,-20,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 010,106,100,-20,0"));
    }

    [Fact]
    public void Flags_NbMultiSkipsMultiComponents()
    {
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        var house = w.CreateItem();
        house.ItemType = ItemType.MultiCustom;
        w.PlaceItem(house, new Point3D(100, 100, 0, 0));
        try
        {
            WalkCheck.ResolveCustomDesign = m => m == house
                ? new List<HouseDesignTile> { new(ArtA, 3, 0, 0) }
                : (IReadOnlyList<HouseDesignTile>)System.Array.Empty<HouseDesignTile>();
            Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
            Assert.Equal("1", Read(c, "CANSEELOSFLAG 080,106,100,0,0"));  // LOS_NB_MULTI
        }
        finally
        {
            WalkCheck.ResolveCustomDesign = null;
        }
    }

    [Fact]
    public void Flags_RegionBoundFlags()
    {
        var w = World(1);
        var c = Char(w);
        var area = new Region { Name = "probe area" };
        area.AddRect(90, 90, 103, 110);
        w.AddRegion(area);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        Static(w, 102, ArtA);

        // LOS_NB_LOCAL_STATIC: the wall stands in my own region (CCharLOS.cpp:336).
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 02,106,100,0,0"));
        // LOS_NO_OTHER_REGION: the ray leaves it past x=103 (:239).
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0202,106,100,0,0"));
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0202,103,100,0,0"));

        // LOS_NC_MULTI: crossing a house region that is not mine (:246).
        var house = new Region { Name = "probe house", Flags = RegionFlag.House };
        house.AddRect(104, 99, 104, 101);
        w.AddRegion(house);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 02,106,100,0,0"));
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0402,106,100,0,0"));
    }

    [Fact]
    public void Flags_NcWaterMarksWaterTerrainAsANullTile()
    {
        // Upstream's LOS_NC_WATER sets bNullTerrain on a water terrain tile
        // (CCharLOS.cpp:322-324), and a null tile with no item on it blocks (:643).
        var w = World(1, landTile: 0x00A8);
        w.MapData!.SetSyntheticLandTile(0x00A8, new LandTileData { Flags = TileFlag.Wet, Name = "water" });
        var c = Char(w);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 01000,106,100,0,0"));
    }

    // ------------------------------------------------- #16 ADVANCEDLOS bit choice

    [Fact]
    public void Script_UsesTheViewersOwnAdvancedLosBit()
    {
        // ADVANCEDLOS=2 covers NPCs only: a player takes the legacy walk, which
        // fails on a 30 Z gap (CCharLOS.cpp:93) - for every character LOS read.
        var w = World(2);
        var c = Char(w);
        var target = Char(w, 106, 100, 30);
        Assert.False(w.CanSeeLOSFor(c, c.Position, target.Position));
        Assert.Equal("0", Read(c, $"CANSEELOS 0{target.Uid.Value:X}"));
        Assert.Equal("0", Read(c, $"CANSEELOSFLAG 0,0{target.Uid.Value:X}"));

        // The NPC bit covers an NPC viewer: the eye-height ray clears the gap.
        var npc = Char(w, 100, 102, 0, player: false);
        Assert.Equal("1", Read(npc, $"CANSEELOS 0{target.Uid.Value:X}"));
    }

    // ------------------------------------------- #18 CANSEELOS forms and limits

    [Fact]
    public void Script_CanSeeLosTakesAPointOrAUid_BoundedByViewRange()
    {
        // GetRegionPoint first (CObjBase.cpp:1131): x,y,z,m is a point.
        var w = World(0);
        var c = Char(w);
        Assert.Equal("1", Read(c, "CANSEELOS 106,100,0,0"));

        // The UID form is bounded by the viewer's visual range (:1143).
        var far = Char(w, 130);
        var near = Char(w, 106);
        Assert.Equal("0", Read(c, $"CANSEELOS 0{far.Uid.Value:X}"));
        Assert.Equal("1", Read(c, $"CANSEELOS 0{near.Uid.Value:X}"));
    }

    [Fact]
    public void Script_AdvancedRangeIsTheRoundedStraightLineDistance()
    {
        // APPROX(dist2d) > iMaxDist (CCharLOS.cpp:148): (18,18) away is 25 tiles for
        // the ray, 18 for the legacy walk's square distance.
        var adv = World(1);
        var a = Char(adv);
        Assert.Equal("0", Read(a, "CANSEELOSFLAG 0,118,118,0,0"));
        Assert.Equal("1", Read(a, "CANSEELOSFLAG 0,113,112,0,0")); // 17.7 -> 18

        var legacy = World(0);
        var l = Char(legacy);
        Assert.Equal("1", Read(l, "CANSEELOSFLAG 0,118,118,0,0"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Script_ActiveGmSeesThroughWalls_CombatLosDoesNot(int advanced)
    {
        // CanSeeLOS without bCombatCheck passes an active GM (CCharLOS.cpp:25/117).
        var w = World(advanced);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Wall | TileFlag.Impassable, 20);
        Static(w, 103, ArtA);
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        c.PrivLevel = PrivLevel.GM;
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("1", Read(c, "CANSEELOS 106,100,0,0"));

        // A combat LOS check never takes the pass.
        var target = Char(w, 106);
        Assert.False(w.CanSeeLOSFor(c, target));
        Assert.False(w.CanSeeLOSFor(c, c.Position, target.Position));
    }

    // ------------------------------------------------------------ #19 LOS_FISHING

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Fishing_UntypedTerrainIsFishable_LegacyIgnoresTheFlag(int advanced)
    {
        // GetTerrainItemType answers IT_NORMAL for a tile no TYPEDEF claims
        // (CWorldMap.cpp:209-215), and IT_NORMAL passes the fishing test (:275).
        var w = World(advanced);
        var c = Char(w);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0800,106,100,0,0"));
    }

    [Fact]
    public void Fishing_TerrainTypedOtherThanWaterBlocksFromTwoTilesOut()
    {
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_losfish_{System.Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(file, """
                [TYPEDEFS]
                t_rock 45
                t_water 30

                [TYPEDEF t_rock]
                TERRAIN = 0003 0003

                [TYPEDEF t_water]
                TERRAIN = 00a8 00ab
                """);
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
            { ScpBaseDir = Path.GetDirectoryName(file) ?? "" };
            resources.LoadResourceFile(file);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            Assert.Equal(ItemType.Rock, NaturalResourceTiles.TerrainItemType(DirtLand));
            Assert.Equal(ItemType.Water, NaturalResourceTiles.TerrainItemType(0x00A9));
            Assert.Equal(ItemType.Normal, NaturalResourceTiles.TerrainItemType(0x0010));

            var rock = World(1);
            var c = Char(rock);
            Assert.Equal("0", Read(c, "CANSEELOSFLAG 0800,106,100,0,0"));
            Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
            Assert.Equal("1", Read(c, "CANSEELOSFLAG 0800,101,100,0,0")); // within 1 tile

            var water = World(1, landTile: 0x00A9);
            var c2 = Char(water);
            Assert.Equal("1", Read(c2, "CANSEELOSFLAG 0800,106,100,0,0"));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void Fishing_SolidNonWaterThingBlocksEvenBelowTheRay()
    {
        // From two tiles out, anything with DOOR/PLATFORM/BLOCK/... CAN that is not
        // IT_WATER stops a fishing ray wherever its Z is (CCharLOS.cpp:367-373).
        var w = World(1);
        var c = Char(w);
        Art(w, ArtA, TileFlag.Impassable, 5);
        Static(w, 103, ArtA, -50);
        Assert.Equal("1", Read(c, "CANSEELOSFLAG 0,106,100,0,0"));
        Assert.Equal("0", Read(c, "CANSEELOSFLAG 0800,106,100,0,0"));
    }
}
