using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Game.Ships;
using SphereNet.Game.Housing;
using SphereNet.Scripting.Definitions;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

/// <summary>
/// A water tile in UO is two things: the SEA FLOOR as terrain, which is ordinary
/// dry passable land, and the water itself as an impassable static above it. Real
/// data from the live shard at (1460,1881):
///
///   land 0x005E z=-15  (not wet, not impassable)
///   static 0x1799 z=-5 h=0 'water' — Background, Impassable, Wet, NoHouse
///
/// So "is this water" cannot be answered from the land tile alone. A check that
/// only refuses wet IMPASSABLE LAND accepts the sea floor as a surface and lets a
/// character stand under the sea - which is what ten units below a ship's deck
/// looks like from inside the game, and why the client then refuses every step:
/// it knows the water is impassable and will not walk a character standing there.
/// </summary>
public sealed class UnderwaterStandingTests
{
    private const ushort SeaFloor = 0x005E;   // dry land, under the water
    private const ushort WaterStatic = 0x1799;
    private const ushort DryGrass = 0x0003;

    private static (GameWorld World, WalkCheck Check) Setup()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 64, 64, landZ: -15, landTile: SeaFloor);
        md.SetSyntheticLandTile(SeaFloor, new LandTileData
        { Flags = TileFlag.None, Name = "sea floor" });
        md.SetSyntheticLandTile(DryGrass, new LandTileData
        { Flags = TileFlag.None, Name = "grass" });
        md.SetSyntheticItemTile(WaterStatic, new ItemTileData
        {
            Flags = TileFlag.Background | TileFlag.Impassable | TileFlag.Wet | TileFlag.NoHouse,
            Height = 0,
            Name = "water",
        });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 64, 64);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        // Water everywhere except the row the mover starts on, so the step under
        // test is "from a deck-height standing spot onto open water".
        for (int x = 0; x < 64; x++)
            for (int y = 0; y < 64; y++)
                md.AddSyntheticStatic(0, x, y, WaterStatic, -5);

        return (world, new WalkCheck(world));
    }

    [Fact]
    public void AStepOntoOpenWaterIsRefused()
    {
        var (world, check) = Setup();
        var ch = world.CreateCharacter();
        // Standing where a ship's deck would put them: one above the water.
        world.PlaceCharacter(ch, new Point3D(20, 20, -4, 0));

        bool ok = check.CheckMovementDetailed(ch, ch.Position, Direction.South,
            out int newZ, out var diag);

        Assert.False(ok,
            $"stepped onto water and landed at z={newZ} (reason={diag.FwdReason})");
    }

    [Fact]
    public void TheSeaFloorIsNotAStandingSurface()
    {
        // The same question asked of the seat resolver, which is what teleports,
        // logins and plank boarding go through.
        var (world, _) = Setup();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(20, 20, -4, 0));

        var seat = world.Standing.ResolveStandingSurface(
            ch, 0, 21, 20, -4, WalkCheck.StandingPolicy.Settle);

        Assert.False(seat.Found && seat.Z <= -15,
            $"seated on the sea floor at z={seat.Z}");
    }

    [Fact]
    public void TheWaterSurfaceIsTheStaticsTop_NotTheSeaBed()
    {
        // The live shard's shape: water laid as a static over a sea bed ten units
        // below. Reading the terrain height here is what moors a hull on the bottom.
        var (world, _) = Setup();
        var ships = new ShipEngine(world, new MultiRegistry(), world.MapData);

        Assert.True(ships.TryGetWaterSurfaceZ(0, 20, 20, out sbyte z));
        Assert.Equal(-5, z);
    }

    [Fact]
    public void WetTerrainAnswersWithItsOwnHeight()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 32, 32, landZ: -5, landTile: 0x00A8);
        md.SetSyntheticLandTile(0x00A8, new LandTileData
        { Flags = TileFlag.Wet | TileFlag.Impassable, Name = "water" });
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 32, 32);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var ships = new ShipEngine(world, new MultiRegistry(), md);
        Assert.True(ships.TryGetWaterSurfaceZ(0, 10, 10, out sbyte z));
        Assert.Equal(-5, z);
    }

    [Fact]
    public void DryLandHasNoWaterSurface()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 32, 32, landZ: 0, landTile: DryGrass);
        md.SetSyntheticLandTile(DryGrass, new LandTileData { Flags = TileFlag.None, Name = "grass" });
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 32, 32);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var ships = new ShipEngine(world, new MultiRegistry(), md);
        Assert.False(ships.TryGetWaterSurfaceZ(0, 10, 10, out _));
    }

    // --- Placement and restore ------------------------------------------

    private static (GameWorld World, ShipEngine Ships, MultiRegistry Reg) WaterWorld()
    {
        var (world, _) = Setup();
        var reg = new MultiRegistry();
        var def = new MultiDef { Id = 0x4000, Name = "test ship" };
        def.Components.Add(new MultiComponent
        { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = false });
        def.RecalcBounds();
        reg.Register(def);
        var ships = new ShipEngine(world, reg, world.MapData)
        { MaxShipsPerPlayer = -1, MaxShipsPerAccount = -1 };
        return (world, ships, reg);
    }

    private static Character Owner(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(2, 2, -4, 0));
        return ch;
    }

    [Fact]
    public void AShipTargetedOnTheSeaBedIsPlacedOnTheWaterInstead()
    {
        // The client reports the terrain for a water tile, so this is the Z a real
        // placement arrives with: ten units under the water it should float on.
        var (world, ships, _) = WaterWorld();
        var ship = ships.PlaceShip(Owner(world), 0x4000,
            new Point3D(20, 20, -15, 0), Direction.North);

        Assert.NotNull(ship);
        Assert.Equal(-5, ship!.MultiItem.Z);
    }

    [Fact]
    public void ARestoredShipBelowItsWaterLineIsRefloated()
    {
        // Placement is fixed, but the ships already in a save are not: without this
        // they stay under the sea until somebody redeeds them.
        var (world, ships, _) = WaterWorld();
        var ship = ships.PlaceShip(Owner(world), 0x4000,
            new Point3D(20, 20, -5, 0), Direction.North)!;

        // Put it back where the old placement left it, hull and component alike.
        Assert.True(ships.MoveDelta(ship, 0, 0, -10));
        Assert.Equal(-15, ship.MultiItem.Z);

        int raised = ships.RefloatSunkenShips();

        Assert.Equal(1, raised);
        Assert.Equal(-5, ship.MultiItem.Z);
        Assert.Equal(-5, world.FindItem(ship.Components[0])!.Z);
    }

    [Fact]
    public void AShipAtOrAboveItsWaterLineIsLeftAlone()
    {
        // Only ever upward, and never onto a ship a script deliberately raised.
        var (world, ships, _) = WaterWorld();
        var ship = ships.PlaceShip(Owner(world), 0x4000,
            new Point3D(20, 20, -5, 0), Direction.North)!;
        Assert.True(ships.MoveDelta(ship, 0, 0, 20));
        sbyte lifted = ship.MultiItem.Z;

        Assert.Equal(0, ships.RefloatSunkenShips());
        Assert.Equal(lifted, ship.MultiItem.Z);
    }

    [Fact]
    public void TheRestoreItselfRefloats_NotOnlyAManualCall()
    {
        // The correction is worth nothing unless the world load runs it: a save full
        // of sunken ships is exactly the situation it exists for.
        var (world, ships, reg) = WaterWorld();
        var ship = ships.PlaceShip(Owner(world), 0x4000,
            new Point3D(20, 20, -5, 0), Direction.North)!;
        Assert.True(ships.MoveDelta(ship, 0, 0, -10));

        var reloaded = new ShipEngine(world, reg, world.MapData)
        { MaxShipsPerPlayer = -1, MaxShipsPerAccount = -1 };
        int reported = 0;
        reloaded.OnShipRefloated = (_, lift) => reported = lift;
        reloaded.DeserializeFromWorld();

        var restored = reloaded.GetShip(ship.MultiItem.Uid);
        Assert.NotNull(restored);
        Assert.Equal(-5, restored!.MultiItem.Z);
        Assert.Equal(10, reported);
    }
}
