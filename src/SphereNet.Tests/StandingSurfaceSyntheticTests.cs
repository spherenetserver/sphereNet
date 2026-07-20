using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Multi;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The audit design's synthetic scenario matrix for the unified
/// standing-surface resolver, on the in-memory map fixture (no muls):
/// dynamic addon floors, multi house floors, ship decks over water,
/// two stories at the same X/Y, low ceilings, impassable+surface
/// furniture, and committed custom-house design tiles.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class StandingSurfaceSyntheticTests
{
    private const ushort FloorTile = 0x0500;   // synthetic Surface, h=0
    private const ushort DeckTile = 0x0501;    // synthetic Surface, h=0 (ship deck)
    private const ushort CeilingTile = 0x0502; // synthetic Impassable slab
    private const ushort CounterTile = 0x0503; // Impassable+Surface furniture
    private const ushort AddonTile = 0x0504;   // dynamic addon floor, h=2
    private const ushort WaterLand = 0x00A8;   // synthetic wet land

    private static (GameWorld world, WalkCheck walker, MapDataManager map) Setup()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(FloorTile, new ItemTileData { Flags = TileFlag.Surface });
        map.SetSyntheticItemTile(DeckTile, new ItemTileData { Flags = TileFlag.Surface });
        map.SetSyntheticItemTile(CeilingTile, new ItemTileData { Flags = TileFlag.Impassable, Height = 2 });
        map.SetSyntheticItemTile(CounterTile, new ItemTileData
        { Flags = TileFlag.Impassable | TileFlag.Surface, Height = 6 });
        map.SetSyntheticItemTile(AddonTile, new ItemTileData { Flags = TileFlag.Surface, Height = 2 });
        map.SetSyntheticLandTile(WaterLand, new LandTileData
        { Flags = TileFlag.Impassable | TileFlag.Wet, Name = "water" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, new WalkCheck(world), map);
    }

    private static Character Mover(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        return ch;
    }

    [Fact]
    public void DynamicAddonFloor_SettleSeatsOnTop()
    {
        var (world, walker, _) = Setup();
        var mover = Mover(world);

        var addon = world.CreateItem();
        addon.BaseId = AddonTile;
        addon.SetAttr(ObjAttributes.Move_Never); // anchored → movement geometry
        world.PlaceItem(addon, new Point3D(50, 50, 3, 0));

        // Surface top = 3 + height 2 = 5; a nearby reference must seat there,
        // not on the land at 0 (GetEffectiveZ never saw dynamic items at all).
        var stand = walker.ResolveStandingSurface(mover, 0, 50, 50, 5,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(stand.Found);
        Assert.Equal(5, stand.Z);

        // The addon spans 3..5 over the land: only 3 units of clearance
        // remain underneath, so even a ground reference seats ON TOP —
        // there is no headroom for a person under the platform.
        var ground = walker.ResolveStandingSurface(mover, 0, 50, 50, 0,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(ground.Found);
        Assert.Equal(5, ground.Z);
    }

    [Fact]
    public void MultiHouseFloor_SettleSeatsTheCorrectStory()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        // A one-tile "house" multi: ground floor at +0 handled by land,
        // upper story floor tiles at +20 (both visible components).
        map.SetSyntheticMulti(0x0071, new MultiDef(0x0071,
        [
            new MultiComponent { TileId = FloorTile, XOffset = 0, YOffset = 0, ZOffset = 7, Flags = 1 },
            new MultiComponent { TileId = FloorTile, XOffset = 0, YOffset = 0, ZOffset = 27, Flags = 1 },
        ]));
        var house = world.CreateItem();
        house.BaseId = 0x0071;
        house.ItemType = ItemType.Multi;
        world.PlaceItem(house, new Point3D(80, 80, 0, 0));

        // Two-story seat: the reference Z decides the floor (audit scenario
        // "iki katli evde referans Z sayesinde dogru kata oturur").
        var first = walker.ResolveStandingSurface(mover, 0, 80, 80, 7,
            WalkCheck.StandingPolicy.Settle);
        var second = walker.ResolveStandingSurface(mover, 0, 80, 80, 27,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(first.Found && second.Found);
        Assert.Equal(7, first.Z);
        Assert.Equal(27, second.Z);
    }

    [Fact]
    public void ShipDeck_SettleSeatsOnDeck_NeverInTheWater()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        // Water land at the ship tile: the land is a blocked (wet) surface,
        // so the ONLY standable geometry is the deck component at +3.
        map.SetSyntheticMulti(0x4000, new MultiDef(0x4000,
        [
            new MultiComponent { TileId = DeckTile, XOffset = 0, YOffset = 0, ZOffset = 3, Flags = 1 },
        ]));
        // Rewrite the land under the ship tile to water via a synthetic map
        // on a second map index (land tile id is per-map in the fixture).
        map.AddSyntheticMap(1, 256, 256, landZ: -5, landTile: WaterLand);
        world.InitMap(1, 256, 256);

        var ship = world.CreateItem();
        ship.BaseId = 0x4000;
        ship.ItemType = ItemType.Ship;
        world.PlaceItem(ship, new Point3D(100, 100, -2, 1));

        var deck = walker.ResolveStandingSurface(mover, 1, 100, 100, 1,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(deck.Found, "no deck surface found over water");
        Assert.Equal(1, deck.Z); // multi Z -2 + deck offset 3

        // Even a riverbed reference may never seat IN the water: the wet
        // land is not a candidate, the deck is the nearest surface.
        var below = walker.ResolveStandingSurface(mover, 1, 100, 100, -5,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(below.Found);
        Assert.Equal(1, below.Z);
    }

    [Fact]
    public void LowCeiling_BlocksTheSeat_ButNotTheBypassFallback()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        // Floor at 0 (land) with an impassable slab at +10: less than a
        // person height (16) of clearance — nothing standable here.
        map.AddSyntheticStatic(0, 60, 60, CeilingTile, 10);

        var settle = walker.ResolveStandingSurface(mover, 0, 60, 60, 0,
            WalkCheck.StandingPolicy.Settle);
        Assert.False(settle.Found, "settled under a low ceiling");

        // The GM bypass still follows the nearest surface (no headroom).
        var bypass = walker.ResolveStandingSurface(mover, 0, 60, 60, 0,
            WalkCheck.StandingPolicy.IgnoreCollision);
        Assert.True(bypass.Found);
        Assert.False(bypass.HasHeadroom);
        Assert.Equal(0, bypass.Z);
    }

    [Fact]
    public void ImpassableSurfaceFurniture_IsNeverASeat()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        // A counter/table: Impassable+Surface, top at 6. The walk path
        // treats it as a blocker, never a floor — the resolver must agree
        // and never seat the character ON the counter top.
        map.AddSyntheticStatic(0, 70, 70, CounterTile, 0);

        var stand = walker.ResolveStandingSurface(mover, 0, 70, 70, 6,
            WalkCheck.StandingPolicy.Settle);
        if (stand.Found)
            Assert.NotEqual(6, stand.Z); // top of the counter is not a seat
    }

    [Fact]
    public void CustomHouseFloor_DesignTilesAreSeats()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        var foundation = world.CreateItem();
        foundation.BaseId = 0x0072;
        foundation.ItemType = ItemType.MultiCustom;
        world.PlaceItem(foundation, new Point3D(90, 90, 0, 0));

        // Committed design tiles are not real items — they reach the walk
        // geometry through the ResolveCustomDesign hook.
        WalkCheck.ResolveCustomDesign = multi =>
            multi == foundation
                ? new[] { new HouseDesignTile(FloorTile, 0, 0, 7) }
                : [];
        try
        {
            var floor = walker.ResolveStandingSurface(mover, 0, 90, 90, 7,
                WalkCheck.StandingPolicy.Settle);
            Assert.True(floor.Found, "custom-house design floor not seen");
            Assert.Equal(7, floor.Z);
        }
        finally
        {
            WalkCheck.ResolveCustomDesign = null;
        }
    }

    [Fact]
    public void EndToEnd_UpperStoryWalk_ThenDclickFacing_0x20CarriesTheFloorZ()
    {
        // The full drift chain, end to end (audit ask): a real
        // MovementEngine.TryMove step on an upper story, then a double-click
        // that turns the character — the self 0x20 the turn sends must carry
        // the story's Z, not a terrain-derived one.
        var (world, _, map) = Setup();
        map.AddSyntheticStatic(0, 40, 40, FloorTile, 20);
        map.AddSyntheticStatic(0, 41, 40, FloorTile, 20);
        map.AddSyntheticStatic(0, 42, 40, FloorTile, 20);

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 17001);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190;
        world.PlaceCharacter(player, new Point3D(41, 40, 20, 0));
        TestHarness.AttachCharacter(client, player);

        var movement = new SphereNet.Game.Movement.MovementEngine(world);
        Assert.True(movement.TryMove(player, Direction.East, running: false, sequence: 0),
            "the upper-story step was rejected");
        Assert.Equal(20, player.Z); // the walk kept the story

        // A dclick target south of the player forces a facing change,
        // which sends the self 0x20 redraw.
        var lamp = world.CreateItem();
        lamp.BaseId = AddonTile;
        world.PlaceItem(lamp, new Point3D(42, 41, 20, 0));
        client.HandleDoubleClick(lamp.Uid.Value);

        var draw = TestHarness.GetQueuedPackets(client.NetState)
            .LastOrDefault(p => p.Span.Length >= 19 && p.Span[0] == 0x20 &&
                ReadU32(p, 1) == player.Uid.Value);
        Assert.NotNull(draw);
        Assert.Equal(20, (sbyte)draw!.Span[18]); // 0x20 layout: Z is the final byte
    }

    private static uint ReadU32(SphereNet.Network.Packets.PacketBuffer p, int offset) =>
        (uint)((p.Span[offset] << 24) | (p.Span[offset + 1] << 16) |
               (p.Span[offset + 2] << 8) | p.Span[offset + 3]);

    [Fact]
    public void TwoStories_SameTile_WalkAndSettleAgreePerFloor()
    {
        var (world, walker, map) = Setup();
        var mover = Mover(world);

        // Static floors at 0 (land) and +20 with full headroom between.
        map.AddSyntheticStatic(0, 40, 40, FloorTile, 20);
        map.AddSyntheticStatic(0, 41, 40, FloorTile, 20);

        // Walking along the upper story keeps the upper Z...
        var from = new Point3D(40, 40, 20, 0);
        world.PlaceCharacter(mover, from);
        Assert.True(walker.CheckMovement(mover, from, Direction.East, out int stepZ));
        Assert.Equal(20, stepZ);

        // ...and Settle reproduces exactly the walk's choice per reference.
        var upper = walker.ResolveStandingSurface(mover, 0, 41, 40, 20,
            WalkCheck.StandingPolicy.Settle);
        var lower = walker.ResolveStandingSurface(mover, 0, 41, 40, 0,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(upper.Found && lower.Found);
        Assert.Equal(20, upper.Z);
        Assert.Equal(0, lower.Z);
    }
}
