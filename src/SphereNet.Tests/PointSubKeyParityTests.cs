using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// &lt;OBJ.P.key&gt; is a question put to the object's point.
///
/// Upstream answers OC_P with GetUnkPoint().r_WriteVal(key + 2) (CObjBase.cpp:
/// 1548-1551), so every key a point has - STATICS, STATICS.n.ID, REGION.x, ROOM,
/// SECTOR, ISNEARTYPE - is readable through P. on any object, the same way TARGP.
/// reaches the picked point. Only the coordinates, P.TYPE and P.TERRAIN were
/// answered here; anything else read nothing.
///
/// The Sphere 56T custom-version packs gate their pickaxe on it: the TYPEDEF of
/// t_weapon_mace_pick refuses @TargOn_Ground with "IF !(&lt;SRC.P.STATICS&gt;) ...
/// RETURN 1" (the miner must stand on statics, and the picked spot must hold
/// statics). With P.STATICS unread the gate refused at every spot, so mining
/// never started anywhere.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PointSubKeyParityTests : IDisposable
{
    private const ushort RockTile = 0x053B;
    private const ushort FloorTile = 0x0519;
    private const string ExternalPackDefault = @"C:\56T\scripts";

    private readonly ITestOutputHelper _out;
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_pkey_{Guid.NewGuid():N}.scp");

    public PointSubKeyParityTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static (GameWorld World, MapDataManager Map) SyntheticWorld()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = TestHarness.CreateWorld();
        world.MapData = map;
        return (world, map);
    }

    [Fact]
    public void PointStaticsCountsWhatLiesUnderTheObject()
    {
        var (world, map) = SyntheticWorld();
        map.AddSyntheticStatic(0, 100, 100, FloorTile, 0);
        map.AddSyntheticStatic(0, 100, 100, RockTile, 0);

        var onStatics = world.CreateCharacter();
        world.PlaceCharacter(onStatics, new Point3D(100, 100, 0, 0));
        var onBareLand = world.CreateCharacter();
        world.PlaceCharacter(onBareLand, new Point3D(101, 100, 0, 0));

        Assert.True(onStatics.TryGetProperty("P.STATICS", out string count));
        Assert.Equal("2", count);
        Assert.True(onStatics.TryGetProperty("p.statics.1.id", out string id));
        Assert.Equal($"0{RockTile:x}", id);
        Assert.True(onBareLand.TryGetProperty("P.STATICS", out string none));
        Assert.Equal("0", none);

        // The coordinates and the existing point keys keep their meaning.
        Assert.True(onStatics.TryGetProperty("P.X", out string x));
        Assert.Equal("100", x);
        Assert.True(onStatics.TryGetProperty("P.TERRAIN", out string terrain));
        Assert.Equal("03", terrain);
    }

    [Fact]
    public void PointRegionNamesTheAreaTheObjectStandsIn()
    {
        var (world, _) = SyntheticWorld();
        var area = new SphereNet.Game.World.Regions.Region { Name = "Test Quarry", MapIndex = 0 };
        area.AddRect(90, 90, 110, 110);
        world.AddRegion(area);

        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(105, 105, 0, 0));

        Assert.True(ch.TryGetProperty("P.REGION.NAME", out string chArea));
        Assert.Equal("Test Quarry", chArea);
        Assert.True(item.TryGetProperty("P.REGION.NAME", out string itemArea));
        Assert.Equal("Test Quarry", itemArea);
    }

    /// <summary>The pack's gate, as written: both points must hold statics.</summary>
    [Fact]
    public void APickaxeGateOnThePointUnderfootLetsTheSwingThrough()
    {
        File.WriteAllText(_defFile, """
            [ITEMDEF 0e85]
            DEFNAME=i_pickaxe
            TYPE=t_weapon_mace_pick

            [TYPEDEF t_weapon_mace_pick]
            ON=@TargOn_Ground
            IF !(<SRC.P.STATICS>)
                RETURN 1
            ENDIF
            IF !(<SRC.TARGP.STATICS>)
                RETURN 1
            ENDIF
            """);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var outcome = SwingAtThreeSpots(resources, lf);
        Assert.Equal(TriggerResult.Default, outcome.BothOnStatics);
        Assert.Equal(TriggerResult.True, outcome.TargetBare);
        Assert.Equal(TriggerResult.True, outcome.MinerOnBareLand);
    }

    /// <summary>The same gate read from a real Sphere 56T custom-version pack, loaded
    /// in its own [RESOURCES] order. The map is synthetic: only the statics count.</summary>
    [Fact]
    public void TheExternalPackPickaxeGateAcceptsAMinerOnStatics()
    {
        string scripts = Environment.GetEnvironmentVariable("SPHERENET_56T_SCRIPTS") ?? ExternalPackDefault;
        if (Gate.Missing(_out, "external script pack", !Directory.Exists(scripts))) return;

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var outcome = SwingAtThreeSpots(resources, lf);
        _out.WriteLine($"both={outcome.BothOnStatics} bareTarget={outcome.TargetBare} bareMiner={outcome.MinerOnBareLand}");
        Assert.Equal(TriggerResult.Default, outcome.BothOnStatics);
        // The pack's gate is live: a spot with no statics is still refused.
        Assert.Equal(TriggerResult.True, outcome.TargetBare);
    }

    private static (TriggerResult BothOnStatics, TriggerResult TargetBare, TriggerResult MinerOnBareLand)
        SwingAtThreeSpots(ResourceHolder resources, ILoggerFactory lf)
    {
        var (world, map) = SyntheticWorld();
        // A cave: floor statics under the miner's two spots, a rock static to dig at.
        map.AddSyntheticStatic(0, 100, 100, FloorTile, 0);
        map.AddSyntheticStatic(0, 101, 100, RockTile, 0);
        map.AddSyntheticStatic(0, 300, 300, FloorTile, 0);

        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(
            interpreter, resources, lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var miner = world.CreateCharacter();
        miner.IsPlayer = true;
        var pick = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(pick, 0x0E85));
        Assert.Equal(ItemType.WeaponMacePick, pick.ItemType);
        miner.Equip(pick, Layer.OneHanded);

        TriggerResult Swing(Point3D stand, Point3D at)
        {
            world.MoveCharacter(miner, stand);
            miner.SetTag("TARGP", $"{at.X},{at.Y},{at.Z},{at.Map}");
            return dispatcher.FireItemTrigger(pick, ItemTrigger.TargOnGround, new TriggerArgs
            {
                CharSrc = miner,
                ItemSrc = pick,
                N1 = RockTile,
            });
        }

        world.PlaceCharacter(miner, new Point3D(100, 100, 0, 0));
        var both = Swing(new Point3D(100, 100, 0, 0), new Point3D(101, 100, 0, 0));
        var bareTarget = Swing(new Point3D(300, 300, 0, 0), new Point3D(301, 300, 0, 0));
        var bareMiner = Swing(new Point3D(102, 100, 0, 0), new Point3D(101, 100, 0, 0));
        return (both, bareTarget, bareMiner);
    }
}
