using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>What a scripted treasure map leans on (Scripts-X f_treasure_map.scp): a
/// point's REGION, P.TYPE from the pack's TERRAIN typedefs, DISTANCE to a point,
/// the OP= rewrites, .x&lt;function&gt; and SHOW.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TreasureMapScriptParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_tmp_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private void Load(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, lines);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    /// <summary>P.TYPE is the TYPEDEF whose TERRAIN= ranges take in the land tile, by
    /// the pack's name for a number it gave (t_forest 500), t_normal when none does
    /// (CWorldMap::GetTerrainItemTypeDef). It was guessed from the tile's name.</summary>
    [Fact]
    public void TerrainTypeComesFromThePacksTerrainRanges()
    {
        Load("[TYPEDEFS]", "t_grass 97", "t_forest 500",
             "[TYPEDEF t_forest]", "TERRAIN = 0c4 0c7",
             "[TYPEDEF t_grass]", "TERRAIN = 03 06");

        Assert.Equal("t_forest", ObjBase.TerrainTypeName(0x0C5));
        Assert.Equal("t_grass", ObjBase.TerrainTypeName(0x04));
        Assert.Equal("t_normal", ObjBase.TerrainTypeName(0x200));
    }

    /// <summary>DISTANCE x,y is the distance to that point (OC_DISTANCE ->
    /// GetRegionPoint, CObjBase.cpp:1270-1279); the x was read as a uid.</summary>
    [Fact]
    public void DistanceToAPoint()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(ch.TryGetProperty("DISTANCE 130,110", out string d));
        Assert.Equal("30", d);
    }

    /// <summary>A point's REGION alone is 1 or 0 (PT_REGION, CPointBase.cpp:752-757);
    /// it was the area's name, which IF !(<SERV.MAP(...).REGION>) read as 0.</summary>
    [Fact]
    public void APointsRegionIsOneOrZero()
    {
        var world = TestHarness.CreateWorld();
        var area = new Region { Name = "Somewhere", MapIndex = 0 };
        area.AddRect(0, 0, 200, 200);
        world.AddRegion(area);

        Assert.True(ObjBase.TryGetPointKey("REGION", new Point3D(50, 50, 0, 0), out string inside));
        Assert.Equal("1", inside);
        Assert.True(ObjBase.TryGetPointKey("REGION", new Point3D(900, 900, 0, 0), out string outside));
        Assert.Equal("0", outside);
        Assert.True(ObjBase.TryGetPointKey("REGION.NAME", new Point3D(50, 50, 0, 0), out string name));
        Assert.Equal("Somewhere", name);
    }

    /// <summary>The OP= rewrites (CScript::ReadKeyParse, CScript.cpp:744-768): .= drops
    /// the quotes, and the argument of the others is one bracketed operand.</summary>
    [Fact]
    public void CompoundAssignmentsAsReadKeyParseWritesThem()
    {
        var concat = new ScriptKey();
        concat.Parse("NAME .= \" (Guardian)\"");
        Assert.Equal("NAME", concat.Key);
        Assert.Equal("<NAME> (Guardian)", concat.Arg);

        var times = new ScriptKey();
        times.Parse("LOCAL.X *= 2+3");
        Assert.Equal("<EVAL (<LOCAL.X> * (2+3))>", times.Arg);
    }

    /// <summary>.x&lt;function&gt; runs the script function on the picked object, with
    /// the staff member as SRC (CObjBase::r_Verb); it was never tried.</summary>
    [Fact]
    public void AnXVerbRunsAFunctionOnTheObject()
    {
        var world = TestHarness.CreateWorld();
        var gm = world.CreateCharacter();
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        ObjBase? ranOn = null;
        string? src = null;
        var saved = ObjBase.RunScriptFunction;
        ObjBase.RunScriptFunction = (obj, name, args, console) =>
        {
            if (!name.Equals("TreasureMap_SetRandomLocation", StringComparison.OrdinalIgnoreCase)) return false;
            ranOn = obj;
            src = console?.GetName();
            return true;
        };
        try
        {
            gm.Name = "Staff";
            Assert.True(new CommandHandler().ExecuteVerbForTarget(gm, "TreasureMap_SetRandomLocation", "", item));
            Assert.Same(item, ranOn);
            Assert.Equal("Staff", src);
        }
        finally { ObjBase.RunScriptFunction = saved; }
    }

    /// <summary>SHOW &lt;key&gt; says any value of the object (CScriptObj.cpp:1469-1478);
    /// only EVENTS was answered.</summary>
    [Fact]
    public void ShowSaysAnyValue()
    {
        var world = TestHarness.CreateWorld();
        var gm = world.CreateCharacter();
        gm.Name = "Staff";
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.MoreP = new Point3D(1036, 1444, 0, 0);
        item.Name = "map";
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        var speech = new CommandHandler();
        speech.RegisterDefaults(world);
        var said = new List<string>();
        speech.OnSysMessage += (_, text) => said.Add(text);

        Assert.True(speech.ExecuteShowForTarget(gm, "morep", item.Uid.Value));
        Assert.Equal("'morep' for 'map' is '1036,1444,0,0'", said.Single());
    }
}
