using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

// Wave M2: CI-stable z-rule assertions on a SYNTHETIC map. The real-MUL stair
// diagnostics self-skip without C:\mortechUO\mul, so none of the climb/ledge
// rules were asserted in CI. The MapDataManager synthetic fixture builds a
// tiny in-memory map with hand-placed statics + tiledata, no files needed.
public class SyntheticMapZRuleTests
{
    private const ushort StepTile = 0x1000;   // stairs: Surface|Bridge, height 5
    private const ushort LedgeTile = 0x1001;  // platform: Surface, height 20
    private const ushort WallTile = 0x1002;   // wall: Impassable, height 20

    private static (GameWorld World, WalkCheck Walk, Character Ch) Setup()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0);
        map.SetSyntheticItemTile(StepTile, new ItemTileData
        { Flags = TileFlag.Surface | TileFlag.Bridge, Height = 5, Name = "stairs" });
        map.SetSyntheticItemTile(LedgeTile, new ItemTileData
        { Flags = TileFlag.Surface, Height = 20, Name = "ledge" });
        map.SetSyntheticItemTile(WallTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 20, Name = "wall" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50; ch.Dex = 50;
        ch.Hits = ch.MaxHits = 50; ch.Stam = ch.MaxStam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 101, 0, 0));
        return (world, new WalkCheck(world), ch);
    }

    private static MapDataManager Md(GameWorld w) => (MapDataManager)w.MapData!;

    [Fact]
    public void Stairs_ClimbStepByStep_UsingBridgeHalfHeight()
    {
        var (world, walk, ch) = Setup();
        // A north staircase: z0 step at y=100, z5 step at y=99.
        Md(world).AddSyntheticStatic(0, 100, 100, StepTile, 0);
        Md(world).AddSyntheticStatic(0, 100, 99, StepTile, 5);

        Assert.True(walk.CheckMovementDetailed(ch, ch.Position, Core.Enums.Direction.North, out int z1, out _));
        world.MoveCharacter(ch, new Point3D(100, 100, (sbyte)z1, 0));
        Assert.True(z1 > 0); // stepped up onto the stair surface

        Assert.True(walk.CheckMovementDetailed(ch, ch.Position, Core.Enums.Direction.North, out int z2, out _));
        Assert.True(z2 > z1); // second step climbs further
    }

    [Fact]
    public void TallLedge_IsRejected_ButDescentWithinLimitIsAllowed()
    {
        var (world, walk, ch) = Setup();
        // A 20-high flat-top platform directly north — no stairs, no climb.
        Md(world).AddSyntheticStatic(0, 100, 100, LedgeTile, 0);
        bool ok = walk.CheckMovementDetailed(ch, ch.Position, Core.Enums.Direction.North, out int z, out _);
        Assert.False(ok && z >= 20); // may not pop onto the ledge top

        // Standing ON the ledge, stepping off (drop 20 ≤ MaxDescendZ 25) is fine.
        world.MoveCharacter(ch, new Point3D(100, 100, 20, 0));
        Assert.True(walk.CheckMovementDetailed(ch, ch.Position, Core.Enums.Direction.North, out int zDown, out _));
        Assert.True(zDown <= 0);
    }

    [Fact]
    public void ImpassableWall_BlocksTheStep()
    {
        var (world, walk, ch) = Setup();
        Md(world).AddSyntheticStatic(0, 100, 100, WallTile, 0);
        Assert.False(walk.CheckMovementDetailed(ch, ch.Position, Core.Enums.Direction.North, out _, out _));
    }
}
