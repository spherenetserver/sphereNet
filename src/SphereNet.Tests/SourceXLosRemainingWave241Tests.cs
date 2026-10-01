using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Remaining Source-X CanSeeLOS_New passes: LOS_NB_MULTI (placed house/ship walls
/// and committed custom-house design tiles occlude sight) and LOS_FISHING (two or
/// more tiles from the caster the terrain must be water or untyped).
/// </summary>
public sealed class SourceXLosRemainingWave241Tests
{
    private const ushort WallGraphic = 0x0080;
    private const ushort WaterLand = 0x00A8;
    private const ushort DirtLand = 0x0003;

    private static GameWorld MakeWorld()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: DirtLand);
        md.SetSyntheticItemTile(WallGraphic, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });

        var world = new GameWorld(NullLoggerFactory.Instance);
        world.InitMap(0, 512, 512);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanSeeLOS_CustomHouseDesignWall_BlocksRay()
    {
        var world = MakeWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(106, 100, 0, 0);

        Assert.True(world.CanSeeLOS(from, to)); // open ground

        // A custom house whose committed design places a wall tile on the ray's
        // midpoint (offset 3,0 from the house origin at 100,100 → 103,100).
        var house = world.CreateItem();
        house.ItemType = ItemType.MultiCustom;
        world.PlaceItem(house, new Point3D(100, 100, 0, 0));
        WalkCheck.ResolveCustomDesign = m => m == house
            ? new List<HouseDesignTile> { new(WallGraphic, 3, 0, 0) }
            : (IReadOnlyList<HouseDesignTile>)System.Array.Empty<HouseDesignTile>();

        Assert.False(world.CanSeeLOS(from, to));
    }

    [Fact]
    public void CanSeeLOS_Fishing_WaterAndUntypedTerrainAreFishable()
    {
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(106, 100, 0, 0);

        var water = MakeFishingWorld(wet: true);
        water.AdvancedLos = 0x03;
        Assert.True(water.CanSeeLOS(from, to, LosFlags.Fishing)); // all water → clear

        // The fishing terrain test is by TYPE, not by the tiledata wet flag: a land
        // tile no [TYPEDEF] TERRAIN= claims is IT_NORMAL (CWorldMap.cpp:209-215) and
        // IT_NORMAL is fishable like IT_WATER (CCharLOS.cpp:275). A typed non-water
        // tile blocking is SourceXAdvancedLosRayTests' fishing case.
        var land = MakeFishingWorld(wet: false);
        land.AdvancedLos = 0x03;
        Assert.True(land.CanSeeLOS(from, to, LosFlags.Fishing));
        Assert.True(land.CanSeeLOS(from, to));
    }

    private static GameWorld MakeFishingWorld(bool wet)
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: wet ? WaterLand : DirtLand);
        md.SetSyntheticLandTile(WaterLand, new LandTileData
        { Flags = TileFlag.Wet | TileFlag.Impassable, Name = "water" });
        md.SetSyntheticLandTile(DirtLand, new LandTileData { Flags = TileFlag.None, Name = "dirt" });

        var world = new GameWorld(NullLoggerFactory.Instance);
        world.InitMap(0, 512, 512);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }
}
