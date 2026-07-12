using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CanSeeLOS_New LOS_NB_DYNAMIC pass: an item placed in the world at
/// runtime occludes line of sight the same way a MUL static does, while a
/// window graphic stays see-through (LOS_NB_WINDOWS default).
/// </summary>
public sealed class SourceXLosDynamicWave240Tests
{
    private const ushort WallGraphic = 0x0080;
    private const ushort WindowGraphic = 0x0081;

    private static GameWorld MakeWorld()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(WallGraphic, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });
        md.SetSyntheticItemTile(WindowGraphic, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Window, Height = 20, Name = "window" });

        var world = new GameWorld(NullLoggerFactory.Instance);
        world.InitMap(0, 512, 512);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanSeeLOS_DynamicWallItem_BlocksRay_WindowDoesNot()
    {
        var world = MakeWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(106, 100, 0, 0);

        // Open ground → clear line of sight.
        Assert.True(world.CanSeeLOS(from, to));

        // A wall item dropped on the midpoint tile occludes the ray.
        var blocker = world.CreateItem();
        blocker.BaseId = WallGraphic;
        world.PlaceItem(blocker, new Point3D(103, 100, 0, 0));
        Assert.False(world.CanSeeLOS(from, to));

        // The same tile with a window graphic is see-through again.
        blocker.BaseId = WindowGraphic;
        Assert.True(world.CanSeeLOS(from, to));
    }

    [Fact]
    public void CanSeeLOS_DynamicItem_OnlyBlocksWhenZSpanCoversRay()
    {
        var world = MakeWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(106, 100, 0, 0);

        // A wall item far below the eye-level ray (deep negative Z) does not block.
        var lowBlocker = world.CreateItem();
        lowBlocker.BaseId = WallGraphic;
        world.PlaceItem(lowBlocker, new Point3D(103, 100, -60, 0));
        Assert.True(world.CanSeeLOS(from, to));
    }
}
