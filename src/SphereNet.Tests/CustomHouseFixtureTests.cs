using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Source-X custom-house fixture semantics (CItemMultiCustom::CommitChanges):
/// commit materializes door/container design tiles as real items and the
/// design copies become invisible — excluded from the committed 0xD8 stream
/// and from walk/LOS geometry, so a door exists exactly once.
/// </summary>
public sealed class CustomHouseFixtureTests
{
    private const ushort DoorTile = 0x0600;  // synthetic Door+Impassable
    private const ushort FloorTile = 0x0604; // synthetic Surface
    private const ushort WallTile = 0x0608;  // synthetic Wall+Impassable

    private static (GameWorld World, CustomHousingEngine Engine, Character Ch,
        SphereNet.Game.Objects.Items.Item Multi) Setup()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(DoorTile, new ItemTileData
        { Flags = TileFlag.Door | TileFlag.Impassable, Height = 20, Name = "door" });
        md.SetSyntheticItemTile(FloorTile, new ItemTileData
        { Flags = TileFlag.Surface, Name = "floor" });
        md.SetSyntheticItemTile(WallTile, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });

        var world = new GameWorld(NullLoggerFactory.Instance);
        world.InitMap(0, 256, 256);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var engine = new CustomHousingEngine(world, new HousingEngine(world, new MultiRegistry()));
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.GM; // bypass any whitelist another test registered
        var multi = world.CreateItem();
        multi.ItemType = ItemType.MultiCustom;
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        engine.Begin(ch, multi);
        return (world, engine, ch, multi);
    }

    [Fact]
    public void Commit_DoorTile_MaterializesRealItem_AndHidesDesignCopy()
    {
        var (world, engine, ch, multi) = Setup();
        Assert.True(engine.Build(ch, FloorTile, 1, 1));
        Assert.True(engine.Build(ch, DoorTile, 2, 1));
        engine.Commit(ch);

        // Exactly one real door item, tagged as this house's fixture.
        var fixtures = world.GetAllObjects().OfType<SphereNet.Game.Objects.Items.Item>()
            .Where(i => i.BaseId == DoorTile && !i.IsDeleted).ToList();
        var door = Assert.Single(fixtures);
        Assert.Equal(ItemType.Door, door.ItemType);
        Assert.True(door.TryGetTag("FIXTURE", out string? owner));
        Assert.Equal(multi.Uid.Value.ToString(), owner);
        Assert.Equal(102, door.X); // multi 100 + offset 2
        Assert.Equal(101, door.Y);

        // The committed design still stores the door tile (revision history /
        // re-edit), but marked invisible; the floor stays visible.
        var design = engine.GetCommittedDesign(multi);
        Assert.Contains(design.Tiles, t => t.TileId == DoorTile && !t.Visible);
        Assert.Contains(design.Tiles, t => t.TileId == FloorTile && t.Visible);

        // The committed 0xD8 stream (visible tiles only) carries no door.
        var sent = design.Tiles.Where(t => t.Visible).ToList();
        Assert.DoesNotContain(sent, t => t.TileId == DoorTile);
        Assert.Single(sent);
    }

    [Fact]
    public void Recommit_ReplacesPreviousDoorFixture()
    {
        var (world, engine, ch, multi) = Setup();
        Assert.True(engine.Build(ch, DoorTile, 2, 1));
        engine.Commit(ch);

        engine.Begin(ch, multi);
        engine.Commit(ch); // same design again

        var fixtures = world.GetAllObjects().OfType<SphereNet.Game.Objects.Items.Item>()
            .Where(i => i.BaseId == DoorTile && !i.IsDeleted).ToList();
        Assert.Single(fixtures); // old fixture torn down, not stacked
    }

    [Fact]
    public void LegacyCommittedDesign_WithoutFixtureItems_KeepsDoorVisible()
    {
        // A design committed before fixture materialization existed: door
        // tile in the tags, no COMMIT_FIXTURES tag, no real door item. The
        // virtual door must stay visible or the house loses its door.
        var (world, engine, _, _) = Setup();
        var legacyMulti = world.CreateItem();
        legacyMulti.Tags.Set("DESIGN_0", $"0x{DoorTile:X},2,1,7,0");
        legacyMulti.Tags.Set(HouseDesign.RevisionTag, "5");

        var design = engine.GetCommittedDesign(legacyMulti);
        var tile = Assert.Single(design.Tiles);
        Assert.True(tile.Visible);
    }

    [Fact]
    public void CanSeeLOS_InvisibleFixtureTile_DoesNotBlockRay()
    {
        var (world, _, _, multi) = Setup();
        var from = new Point3D(100, 101, 0, 0);
        var to = new Point3D(106, 101, 0, 0);

        try
        {
            // Visible design wall on the ray blocks; the same tile marked
            // invisible (a materialized fixture) must not.
            WalkCheck.ResolveCustomDesign = m => m == multi
                ? [new HouseDesignTile(WallTile, 3, 1, 0)]
                : (IReadOnlyList<HouseDesignTile>)[];
            Assert.False(world.CanSeeLOS(from, to));

            WalkCheck.ResolveCustomDesign = m => m == multi
                ? [new HouseDesignTile(WallTile, 3, 1, 0, Visible: false)]
                : (IReadOnlyList<HouseDesignTile>)[];
            Assert.True(world.CanSeeLOS(from, to));
        }
        finally
        {
            WalkCheck.ResolveCustomDesign = null;
        }
    }

    [Fact]
    public void WalkGeometry_InvisibleFixtureTile_ContributesNoSurface()
    {
        var (world, _, ch, multi) = Setup();
        ch.IsPlayer = true;
        var walker = new WalkCheck(world);

        try
        {
            WalkCheck.ResolveCustomDesign = m => m == multi
                ? [new HouseDesignTile(FloorTile, 0, 0, 7)]
                : (IReadOnlyList<HouseDesignTile>)[];
            var visible = walker.ResolveStandingSurface(ch, 0, 100, 100, 7,
                WalkCheck.StandingPolicy.Settle);
            Assert.True(visible.Found);
            Assert.Equal(7, visible.Z);

            WalkCheck.ResolveCustomDesign = m => m == multi
                ? [new HouseDesignTile(FloorTile, 0, 0, 7, Visible: false)]
                : (IReadOnlyList<HouseDesignTile>)[];
            var hidden = walker.ResolveStandingSurface(ch, 0, 100, 100, 7,
                WalkCheck.StandingPolicy.Settle);
            // The invisible design copy adds no geometry — only the land
            // at Z 0 remains.
            Assert.True(hidden.Found);
            Assert.Equal(0, hidden.Z);
        }
        finally
        {
            WalkCheck.ResolveCustomDesign = null;
        }
    }
}
