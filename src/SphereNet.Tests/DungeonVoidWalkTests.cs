using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>Real-map check (skips without the live muls): no step from walkable
/// ground lands on the TERRAIN_NULL black of Destard's first level.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DungeonVoidWalkTests(ITestOutputHelper output)
{
    [Fact]
    public void NoStepEntersTheBlackInDestard()
    {
        const string mul = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables", !File.Exists(Path.Combine(mul, "tiledata.mul")))) return;

        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var walker = world.CreateCharacter();

        int voids = 0, entered = 0;
        for (int x = 5120; x < 5200; x++)
        for (int y = 770; y < 850; y++)
        {
            if (map.GetTerrainTile(0, x, y).TileId != SphereNet.Game.Movement.WalkCheck.TerrainNull ||
                map.GetStatics(0, x, y).Length != 0)
                continue;
            voids++;
            for (int d = 0; d < 8; d++)
            {
                int dx = d is 1 or 2 or 3 ? 1 : d is 5 or 6 or 7 ? -1 : 0;
                int dy = d is 0 or 1 or 7 ? -1 : d is 3 or 4 or 5 ? 1 : 0;
                var stand = world.Standing.ResolveStandingSurface(walker, 0, x - dx, y - dy, 0,
                    SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
                if (!stand.Found) continue;
                var from = new Point3D((short)(x - dx), (short)(y - dy), (sbyte)stand.Z, 0);
                walker.Position = from;
                if (world.Standing.CheckMovement(walker, from, (Direction)d, out _))
                    entered++;
            }
        }
        output.WriteLine($"void cells={voids} entered={entered}");
        Assert.True(voids > 0);
        Assert.Equal(0, entered);
    }
}
