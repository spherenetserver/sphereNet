using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A spawned creature is placed MoveNear(gem, rand(MOREZ)+1): somewhere
/// walkable within that distance of the gem (CCSpawn.cpp:433), not on the gem
/// itself. Born on the gem, every creature of a spawn line stood on one tile.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnScatterTests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_scatter
        TYPE=t_spawn_char

        [CHARDEF c_scatter_probe]
        DEFNAME=c_scatter_probe
        ID=0x190
        NAME=scatter probe
        CAN=0x04

        [EOF]
        """;

    private static (GameWorld World, Item Stone) Build(int range, int count, ushort landTile = 3)
    {
        var lf = LoggerFactory.Create(_ => { });
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_scatter_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Script);
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = Path.GetDirectoryName(file) ?? "" };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: landTile);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_scatter_probe");
        stone.Amount = (ushort)count;
        stone.InitializeSpawnComponent(world, res);
        stone.SpawnChar!.SpawnRange = range;
        return (world, stone);
    }

    [Fact]
    public void ChildrenSpreadWithinMoreZ()
    {
        var (world, stone) = Build(range: 5, count: 20);

        stone.SpawnChar!.RespawnNow();

        var spots = stone.SpawnChar.SpawnedUids.Select(u => world.FindChar(u)!.Position).ToList();
        Assert.Equal(20, spots.Count);
        Assert.All(spots, p => Assert.True(Math.Max(Math.Abs(p.X - 100), Math.Abs(p.Y - 100)) <= 5, $"{p} is outside MOREZ"));
        Assert.True(spots.Select(p => (p.X, p.Y)).Distinct().Count() > 1, "every child landed on one tile");
    }

    /// <summary>A cave's black void (land 0x1AE-0x1B5, 0x1DB) carries no tile flags,
    /// so the old IsPassable test took it for open ground and children were born in
    /// it, out of reach and unable to walk out. Upstream asks whether the child can
    /// stand there (CanMoveWalkTo); on void nothing can, so it goes on the gem.</summary>
    [Fact]
    public void NoChildIsBornInTheVoid()
    {
        var (world, stone) = Build(range: 5, count: 10, landTile: 0x01AF);

        stone.SpawnChar!.RespawnNow();

        var spots = stone.SpawnChar.SpawnedUids.Select(u => world.FindChar(u)!.Position).ToList();
        Assert.NotEmpty(spots);
        Assert.All(spots, p => Assert.Equal((100, 100), (p.X, p.Y)));
    }

    [Fact]
    public void MorePSetOnALiveSpawnerIsItsSpawnDistance()
    {
        // The worldgen sets MOREP after TYPE; only storing it left the spawn range 0
        // and every creature beside the gem until the next restart.
        var (_, stone) = Build(range: 0, count: 1);

        Assert.True(stone.TrySetProperty("MOREP", "5,10,15"));
        Assert.Equal(15, stone.SpawnChar!.SpawnRange);

        Assert.True(stone.TrySetProperty("MOREZ", "30"));
        Assert.Equal(30, stone.SpawnChar.SpawnRange);
    }

    [Fact]
    public void AForcedFillRunsTheTimerTriggerBeforeEachChild()
    {
        var (_, stone) = Build(range: 3, count: 4);
        int fired = 0;
        var prev = Item.OnTimerExpired;
        Item.OnTimerExpired = it => { if (it == stone) fired++; return SphereNet.Core.Enums.TriggerResult.Default; };
        try
        {
            stone.SpawnChar!.RespawnNow();
            Assert.Equal(4, stone.SpawnChar.CurrentCount);
            Assert.Equal(4, fired);
        }
        finally
        {
            Item.OnTimerExpired = prev;
        }
    }

    [Fact]
    public void ATimerTriggerReturningOneStopsTheFill()
    {
        var (_, stone) = Build(range: 3, count: 4);
        var prev = Item.OnTimerExpired;
        Item.OnTimerExpired = _ => SphereNet.Core.Enums.TriggerResult.True;
        try
        {
            stone.SpawnChar!.RespawnNow();
            Assert.Equal(0, stone.SpawnChar.CurrentCount);
        }
        finally
        {
            Item.OnTimerExpired = prev;
        }
    }

    [Fact]
    public void ZeroMoreZKeepsChildrenBesideTheGem()
    {
        var (world, stone) = Build(range: 0, count: 10);

        stone.SpawnChar!.RespawnNow();

        Assert.All(stone.SpawnChar.SpawnedUids.Select(u => world.FindChar(u)!.Position),
            p => Assert.True(Math.Max(Math.Abs(p.X - 100), Math.Abs(p.Y - 100)) <= 1, $"{p} is more than 1 from the gem"));
    }
}
