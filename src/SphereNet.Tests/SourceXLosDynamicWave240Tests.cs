using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CanSeeLOS_New LOS_NB_DYNAMIC pass: an item placed in the world at
/// runtime occludes line of sight the same way a MUL static does; a window
/// graphic occludes too unless the check passes LOS_NB_WINDOWS (CCharLOS.cpp:506).
/// The fixtures turn ADVANCEDLOS on so the eye-height ray is what is measured.
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
        { Flags = TileFlag.Wall | TileFlag.Window | TileFlag.Impassable, Height = 20, Name = "window" });

        var world = new GameWorld(NullLoggerFactory.Instance);
        world.InitMap(0, 512, 512);
        world.MapData = md;
        world.AdvancedLos = 0x03;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanSeeLOS_DynamicWallItem_BlocksRay_WindowOnlyWithoutNbWindows()
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

        // A window graphic blocks as well, unless the check looks past windows.
        blocker.BaseId = WindowGraphic;
        Assert.False(world.CanSeeLOS(from, to));
        Assert.True(world.CanSeeLOS(from, to, SphereNet.Core.Enums.LosFlags.NbWindows));
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
