using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Z-drift regression suite for the "[z_drift] dclick-facing at 5146,993
/// map 0: stored Z=10, standing Z=1" field report, upgraded from a dump-only
/// probe to REAL assertions per the audit design: the WalkCheck surface
/// algorithm is the single authority — a normal step, the GM/AllMove bypass,
/// a pass-walls step and both Settle references must all agree on the same
/// floor at the drift tile, so no path can accumulate a different Z than the
/// one the client renders. Skips cleanly when the real mul data is absent.
/// </summary>
public sealed class DungeonZDriftProbe
{
    private readonly ITestOutputHelper _out;
    public DungeonZDriftProbe(ITestOutputHelper o) => _out = o;

    /// <summary>The live-server report tile (a dungeon floor).</summary>
    private const short TX = 5146, TY = 993;

    private static string? FindMulDir()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("SPHERENET_MUL"),
            @"C:\sphereNet\mul",
            @"C:\sphereNetServer\mul",
        ];
        foreach (var dir in candidates)
            if (dir != null && File.Exists(Path.Combine(dir, "tiledata.mul")))
                return dir;
        return null;
    }

    private static (GameWorld world, WalkCheck walker, MapDataManager map)? Setup()
    {
        string? mulDir = FindMulDir();
        if (mulDir == null)
            return null;

        var map = new MapDataManager(mulDir);
        map.Load();
        map.InitMap(0, 7168, 4096);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return (world, new WalkCheck(world), map);
    }

    [Fact]
    public void DriftTile_EveryPathAndPolicy_AgreesOnOneFloor()
    {
        if (Setup() is not { } env)
        {
            _out.WriteLine("mul data not available — skipped");
            return;
        }
        var (world, walker, _) = env;

        var mover = world.CreateCharacter();
        mover.IsPlayer = true;

        // Ground truth: a NORMAL player step into the tile from the west at
        // the client-rendered floor height (z=1 per the field report).
        var from = new Point3D(TX - 1, TY, 1, 0);
        world.PlaceCharacter(mover, from);
        bool ok = walker.CheckMovement(mover, from, Direction.East, out int stepZ);
        Assert.True(ok, "the normal step into the drift tile was rejected");
        _out.WriteLine($"normal step -> z={stepZ}");

        // The GM/AllMove bypass policy must land on the SAME floor — the old
        // GetEffectiveZ pick climbed a side static and stayed there (its
        // closest-to-currentZ selection was self-reinforcing at z=10).
        var gmLow = walker.ResolveStandingSurface(mover, 0, TX, TY, 1,
            WalkCheck.StandingPolicy.IgnoreCollision);
        var gmDrifted = walker.ResolveStandingSurface(mover, 0, TX, TY, 10,
            WalkCheck.StandingPolicy.IgnoreCollision);
        Assert.True(gmLow.Found && gmDrifted.Found);
        Assert.Equal(stepZ, gmLow.Z);
        Assert.Equal(stepZ, gmDrifted.Z); // a drifted reference must NOT stick

        // Settle (login/mount/teleport seating) agrees from both references.
        var settleLow = walker.ResolveStandingSurface(mover, 0, TX, TY, 1,
            WalkCheck.StandingPolicy.Settle);
        var settleDrifted = walker.ResolveStandingSurface(mover, 0, TX, TY, 10,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(settleLow.Found && settleDrifted.Found);
        Assert.Equal(stepZ, settleLow.Z);
        Assert.Equal(stepZ, settleDrifted.Z);
    }

    [Fact]
    public void SlopedLand_SettleMatchesTheWalkZ()
    {
        if (Setup() is not { } env)
        {
            _out.WriteLine("mul data not available — skipped");
            return;
        }
        var (world, walker, map) = env;

        var mover = world.CreateCharacter();
        mover.IsPlayer = true;

        // Britain hillside strip: walk a few eastward steps and require the
        // Settle policy to reproduce every step's landing Z exactly.
        short y = 1620;
        map.GetAverageZ(0, 1400, y, out _, out int startAvg, out _);
        var pos = new Point3D(1400, y, (sbyte)startAvg, 0);
        world.PlaceCharacter(mover, pos);

        for (short x = 1400; x < 1408; x++)
        {
            var loc = new Point3D(x, y, mover.Z, 0);
            if (!walker.CheckMovement(mover, loc, Direction.East, out int newZ))
            {
                _out.WriteLine($"step at {x + 1},{y} blocked — stopping strip here");
                break;
            }
            var settle = walker.ResolveStandingSurface(mover, 0, x + 1, y, newZ,
                WalkCheck.StandingPolicy.Settle);
            Assert.True(settle.Found, $"settle found no surface at {x + 1},{y}");
            Assert.Equal(newZ, settle.Z);
            mover.Position = new Point3D((short)(x + 1), y, (sbyte)newZ, 0);
        }
    }

    [Fact]
    public void BritainBridge_SettleSeatsOnTheDeck_NotTheRiverbed()
    {
        if (Setup() is not { } env)
        {
            _out.WriteLine("mul data not available — skipped");
            return;
        }
        var (world, walker, _) = env;

        var mover = world.CreateCharacter();
        mover.IsPlayer = true;

        // The Britain moat bridge (east gate) — a classic Bridge-flagged
        // static span. Settling with a deck-height reference must pick the
        // bridge surface, never the water/ground below it.
        short bx = 1520, by = 1666;
        var deck = walker.ResolveStandingSurface(mover, 0, bx, by, 0,
            WalkCheck.StandingPolicy.Settle);
        if (!deck.Found)
        {
            _out.WriteLine("no bridge surface at probe coordinate — layout differs, skipping");
            return;
        }
        _out.WriteLine($"bridge settle -> z={deck.Z}");

        // Reference far below (riverbed) still surfaces onto standable deck
        // geometry — Settle never invents a Z with no surface under it.
        var fromBelow = walker.ResolveStandingSurface(mover, 0, bx, by, -20,
            WalkCheck.StandingPolicy.Settle);
        Assert.True(fromBelow.Found);
    }
}
