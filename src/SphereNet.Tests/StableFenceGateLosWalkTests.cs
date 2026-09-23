using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Stable fence / wooden gate field report (Britain stable, 1292,1772 map 0):
/// a fighter hit a creature through a closed i_gate_wood, creatures walked
/// through the 11-high fence statics, and INFO on a static said nothing.
///
/// Line of sight follows Source-X CChar::CanSeeLOS with ADVANCEDLOS disabled
/// (the reference default): a tile-by-tile walk at the viewer's feet in which
/// a blocking tile or a closed door in the lower half of the body stops the
/// sight; a diagonal passes when either orthogonal neighbour is open.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StableFenceGateLosWalkTests
{
    private const ushort FenceTile = 0x0837;     // wooden fence: Wall|Impassable, h11
    private const ushort GateClosed = 0x0839;    // wooden gate, slot 0 (closed art)
    private const ushort GateOpen = 0x083A;      // wooden gate, slot 1 (open art)

    private readonly ITestOutputHelper _out;
    public StableFenceGateLosWalkTests(ITestOutputHelper o) => _out = o;

    // ---------------------------------------------------------------
    // Synthetic fixtures (CI-safe)
    // ---------------------------------------------------------------

    private static (GameWorld World, MapDataManager Map) MakeSyntheticWorld()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(FenceTile, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 11, Name = "wooden fence" });
        md.SetSyntheticItemTile(GateClosed, new ItemTileData
        { Flags = TileFlag.Impassable | TileFlag.Door, Height = 11, Name = "wooden gate" });
        md.SetSyntheticItemTile(GateOpen, new ItemTileData
        { Flags = TileFlag.Impassable | TileFlag.Door, Height = 11, Name = "wooden gate" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, md);
    }

    private static Character MakeFighter(GameWorld world, short x, short y, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = ch.Dex = ch.Int = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.MaxStam = ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static Item PlaceGate(GameWorld world, ushort id, short x, short y)
    {
        var gate = world.CreateItem();
        gate.BaseId = id;
        gate.ItemType = ItemType.Door;
        world.PlaceItem(gate, new Point3D(x, y, 0, 0));
        return gate;
    }

    [Fact]
    public void Los_ClosedGateItemBetween_Blocks_OpenGateDoesNot()
    {
        var (world, _) = MakeSyntheticWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(102, 100, 0, 0);
        Assert.True(world.CanSeeLOS(from, to));

        // An 11-high closed gate sits below the old eye-height ray (z+16) but in
        // the walker's body: CAN_I_DOOR stops the legacy walk.
        var gate = PlaceGate(world, GateClosed, 101, 100);
        Assert.False(world.CanSeeLOS(from, to));

        // The open art of the same gate is no door at all (GetItemSpecificFlags).
        gate.BaseId = GateOpen;
        Assert.True(world.CanSeeLOS(from, to));
    }

    [Fact]
    public void Los_FenceStaticBetween_Blocks()
    {
        var (world, md) = MakeSyntheticWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(102, 100, 0, 0);
        md.AddSyntheticStatic(0, 101, 100, FenceTile, 0);
        Assert.False(world.CanSeeLOS(from, to));
    }

    [Fact]
    public void Los_AdjacentDiagonal_BlockedOnlyWhenBothOrthogonalsBlock()
    {
        var (world, md) = MakeSyntheticWorld();
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(101, 101, 0, 0);

        md.AddSyntheticStatic(0, 101, 100, FenceTile, 0);   // east neighbour
        Assert.True(world.CanSeeLOS(from, to));             // south still open

        md.AddSyntheticStatic(0, 100, 101, FenceTile, 0);   // south neighbour too
        Assert.False(world.CanSeeLOS(from, to));
    }

    [Fact]
    public void Los_AdvancedLos_UsesEyeHeightRay_WhichClearsLowGate()
    {
        var (world, _) = MakeSyntheticWorld();
        PlaceGate(world, GateClosed, 101, 100);
        world.AdvancedLos = 0x03;
        // CanSeeLOS_New casts at eye height; an 11-high gate stays under it.
        Assert.True(world.CanSeeLOS(new Point3D(100, 100, 0, 0), new Point3D(102, 100, 0, 0)));
    }

    [Fact]
    public void Melee_ThroughClosedGate_HoldsSwing_OpenGateAllows()
    {
        var (world, _) = MakeSyntheticWorld();
        var attacker = MakeFighter(world, 100, 100);
        var target = MakeFighter(world, 102, 100, player: false);
        attacker.Direction = Direction.East;
        var gate = PlaceGate(world, GateClosed, 101, 100);

        // Reach 2 isolates the LoS verdict from the weapon range.
        var blocked = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, effectiveRange: (1, 2));
        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, blocked.Result);
        Assert.False(CombatHelper.InWeaponReachAndLos(world, attacker, target, null,
            PrivLevel.Player, effectiveRange: (1, 2)));

        // Combat LoS binds staff as well (bCombatCheck).
        var gm = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.GM, Environment.TickCount64, effectiveRange: (1, 2));
        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, gm.Result);

        gate.BaseId = GateOpen;
        var open = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, effectiveRange: (1, 2));
        Assert.Equal(CombatHelper.SwingPrepResult.Ready, open.Result);
    }

    [Fact]
    public void Info_OnStaticAndGround_ReportsTileLine()
    {
        var (world, md) = MakeSyntheticWorld();
        md.SetSyntheticLandTile(3, new LandTileData { Flags = TileFlag.None, Name = "grass" });
        var pt = new Point3D(101, 100, 0, 0);

        string staticLine = ClientTargetingHandler.FormatStaticInfo(world, pt, FenceTile);
        Assert.StartsWith("[Static z=0, 0837=", staticLine);
        Assert.Contains("TERRAIN=03", staticLine);
        Assert.Contains("TYPE=t_grass", staticLine);

        string ground = ClientTargetingHandler.FormatStaticInfo(world, pt, 0);
        Assert.StartsWith("[No static tile], TERRAIN=03", ground);
    }

    // ---------------------------------------------------------------
    // Real map data: the Britain stable (skips without the muls)
    // ---------------------------------------------------------------

    private static string? FindMulDir()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("SPHERENET_MUL"),
            @"C:\sphereNetServer\mul",
        ];
        foreach (var dir in candidates)
            if (dir != null && File.Exists(Path.Combine(dir, "tiledata.mul")) &&
                File.Exists(Path.Combine(dir, "statics0.mul")))
                return dir;
        return null;
    }

    private static GameWorld? MakeRealWorld()
    {
        string? mul = FindMulDir();
        if (mul == null) return null;
        var md = new MapDataManager(mul);
        md.Load();
        md.InitMap(0, 7168, 4096);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static bool CanNpcMoveTo(NpcAI ai, Character npc, Point3D pos)
    {
        var m = typeof(NpcAI).GetMethod("CanNpcMoveTo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)m.Invoke(ai, [npc, pos, true])!;
    }

    [Fact]
    public void RealStable_NpcCannotStepIntoFenceOrCutItsCorner()
    {
        if (MakeRealWorld() is not { } world)
        {
            _out.WriteLine("mul data not available - skipped");
            return;
        }
        var ai = new NpcAI(world, new SphereConfig());
        var npc = MakeFighter(world, 1292, 1772, player: false);
        world.MoveCharacter(npc, new Point3D(1292, 1772, 10, 0));

        // West is the 0x0837 fence (z10, h11): not a step.
        Assert.False(CanNpcMoveTo(ai, npc, new Point3D(1291, 1772, 10, 0)));
        // South along the aisle is open ground.
        Assert.True(CanNpcMoveTo(ai, npc, new Point3D(1292, 1773, 10, 0)));
        // South-west to the gate gap (no gate item here): the west neighbour is
        // fence, so the corner may not be cut (CheckValidMove needs both sides).
        Assert.False(CanNpcMoveTo(ai, npc, new Point3D(1291, 1773, 10, 0)));

        // Straight into the gap is fine while the gate is absent, and refused once
        // a closed gate stands in it.
        world.MoveCharacter(npc, new Point3D(1292, 1773, 10, 0));
        Assert.True(CanNpcMoveTo(ai, npc, new Point3D(1291, 1773, 10, 0)));
        var gate = world.CreateItem();
        gate.BaseId = 0x0843;   // the saved stable gate art (Impassable|Door, h11)
        gate.ItemType = ItemType.Door;
        world.PlaceItem(gate, new Point3D(1291, 1773, 10, 0));
        Assert.False(CanNpcMoveTo(ai, npc, new Point3D(1291, 1773, 10, 0)));
    }

    [Fact]
    public void RealStable_MeleeThroughFenceOrClosedGate_IsBlocked()
    {
        if (MakeRealWorld() is not { } world)
        {
            _out.WriteLine("mul data not available - skipped");
            return;
        }

        // Stall side (1290,y) to aisle (1292,y) across the fence at 1291,1772.
        Assert.False(world.CanSeeLOS(new Point3D(1290, 1772, 10, 0), new Point3D(1292, 1772, 10, 0)));

        // Across the gate gap at 1291,1773: open without the gate item, shut with it.
        var a = new Point3D(1290, 1773, 10, 0);
        var b = new Point3D(1292, 1773, 10, 0);
        Assert.True(world.CanSeeLOS(a, b));
        var gate = world.CreateItem();
        gate.BaseId = 0x0843;
        gate.ItemType = ItemType.Door;
        world.PlaceItem(gate, new Point3D(1291, 1773, 10, 0));
        Assert.False(world.CanSeeLOS(a, b));

        var attacker = MakeFighter(world, 1290, 1773);
        var target = MakeFighter(world, 1292, 1773, player: false);
        world.MoveCharacter(attacker, a);
        world.MoveCharacter(target, b);
        attacker.Direction = Direction.East;
        var prep = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, effectiveRange: (1, 2));
        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, prep.Result);
    }
}
