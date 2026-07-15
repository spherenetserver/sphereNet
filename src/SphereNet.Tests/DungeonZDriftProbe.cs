using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Field probe for the "[z_drift] dclick-facing at 5146,993 map 0: stored Z=10,
/// standing Z=1" report — dumps the tile's land/static inventory from the REAL
/// mul data and replays WalkCheck steps into the tile from each neighbor to
/// see which surface the walk path climbs onto (the server ends 9z above the
/// floor the client renders). Skips cleanly when the mul data is absent.
/// </summary>
public sealed class DungeonZDriftProbe
{
    private const string MulDir = @"C:\sphereNetServer\mul";
    private readonly ITestOutputHelper _out;
    public DungeonZDriftProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Dump_5146_993_AndWalkInFromNeighbors()
    {
        if (!File.Exists(Path.Combine(MulDir, "tiledata.mul")))
        {
            _out.WriteLine("mul data not available");
            return;
        }

        var map = new MapDataManager(MulDir);
        map.Load();
        map.InitMap(0, 7168, 4096);

        const short tx = 5146, ty = 993;
        for (short y = (short)(ty - 1); y <= ty + 1; y++)
        for (short x = (short)(tx - 1); x <= tx + 1; x++)
        {
            var land = map.GetTerrainTile(0, x, y);
            map.GetAverageZ(0, x, y, out int lo, out int center, out int top);
            _out.WriteLine($"({x},{y}) land tile=0x{land.TileId:X4} z={land.Z} avg(lo={lo},c={center},top={top})");
            foreach (var s in map.GetStatics(0, x, y))
            {
                var d = map.GetItemTileData(s.TileId);
                _out.WriteLine($"   static 0x{s.TileId:X4} z={s.Z} h={d.Height}/{d.CalcHeight} flags={d.Flags}");
            }
        }

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var walker = new WalkCheck(world);

        var mover = world.CreateCharacter();
        mover.IsPlayer = true;

        // Step INTO 5146,993 from each cardinal neighbor at both plausible
        // start heights (client floor 1 and drifted 10).
        (short dx, short dy, SphereNet.Core.Enums.Direction d)[] steps =
        [
            (-1, 0, SphereNet.Core.Enums.Direction.East),
            (1, 0, SphereNet.Core.Enums.Direction.West),
            (0, -1, SphereNet.Core.Enums.Direction.South),
            (0, 1, SphereNet.Core.Enums.Direction.North),
        ];
        foreach (sbyte startZ in (sbyte[])[1, 10])
        foreach (var (dx, dy, d) in steps)
        {
            var from = new Point3D((short)(tx + dx), (short)(ty + dy), startZ, 0);
            world.PlaceCharacter(mover, from);
            bool ok = walker.CheckMovement(mover, from, d, out int newZ);
            _out.WriteLine($"step {d} from ({from.X},{from.Y},z{startZ}) -> ok={ok} newZ={newZ}");
        }

        _out.WriteLine($"GetEffectiveZ(cur=10) = {map.GetEffectiveZ(0, tx, ty, 10)}");
        _out.WriteLine($"GetEffectiveZ(cur=1)  = {map.GetEffectiveZ(0, tx, ty, 1)}");
    }
}
