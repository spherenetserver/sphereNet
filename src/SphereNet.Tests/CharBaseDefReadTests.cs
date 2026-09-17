using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A creature answers for the keys its CHARDEF declares.
///
/// Upstream reads them off the CCharBase behind the CChar (CCharBase::r_WriteVal), so
/// &lt;SOUNDDIE&gt; or &lt;MOVERATE&gt; on a creature answers what its definition said.
/// Here they answered nothing at all: a script asking one got an empty string, which
/// an IF reads as zero, so a line branching on a creature's death sound or its icon
/// took the wrong branch every time.
///
/// MAXFOOD is the same question from the other side. The engine's own MaxFood resolves
/// instance, then definition, then the classic 60; the script read went straight to
/// the raw tag and answered 0. Upstream answers Stat_GetMaxAdjusted(STAT_FOOD), the
/// number the engine works from (CChar.cpp:3184), and the shipped food dialog carries
/// a SERV.CHARDEF lookup to work around the difference.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharBaseDefReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_bd_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Character FromDef(params string[] defLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        var lines = new List<string> { "[CHARDEF c_probe_beast]", "ID=0190", "NAME=probe beast" };
        lines.AddRange(defLines);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(ch, "c_probe_beast", resources));
        world.PlaceCharacter(ch, new Point3D(120, 120, 0, 0));
        return ch;
    }

    private static string Read(Character ch, string key) =>
        ch.TryGetProperty(key, out string v) ? v : "<unanswered>";

    [Fact]
    public void TheSoundsComeFromTheDefinition()
    {
        var ch = FromDef("SOUNDDIE=056", "SOUNDGETHIT=057", "SOUNDHIT=058",
                         "SOUNDIDLE=059", "SOUNDNOTICE=05a");

        Assert.Equal("056", Read(ch, "SOUNDDIE"));
        Assert.Equal("057", Read(ch, "SOUNDGETHIT"));
        Assert.Equal("058", Read(ch, "SOUNDHIT"));
        Assert.Equal("059", Read(ch, "SOUNDIDLE"));
        Assert.Equal("05A", Read(ch, "SOUNDNOTICE"));
    }

    [Fact]
    public void ThePacingAndWageComeFromTheDefinition()
    {
        var ch = FromDef("MOVERATE=40", "HIREDAYWAGE=70", "ICON=i_pet_horse_brown_dk");

        Assert.Equal("40", Read(ch, "MOVERATE"));
        Assert.Equal("70", Read(ch, "HIREDAYWAGE"));
        Assert.Equal("i_pet_horse_brown_dk", Read(ch, "ICON"));
    }

    /// <summary>The era-display family, which the reference pack reads off a creature
    /// while building it.</summary>
    [Fact]
    public void TheEraDisplayKeysComeFromTheDefinition()
    {
        var ch = FromDef("RESLEVEL=3", "RESDISPDNHUE=0482", "RESDISPDNID=c_probe_beast",
                         "ANIM=03f8c7f");

        Assert.Equal("3", Read(ch, "RESLEVEL"));
        Assert.Equal("0482", Read(ch, "RESDISPDNHUE"));
        Assert.Equal("c_probe_beast", Read(ch, "RESDISPDNID"));
        Assert.Equal("03F8C7F", Read(ch, "ANIM"));
    }

    /// <summary>A creature with no definition behind it answers rather than throwing -
    /// that is most of what a test harness builds.</summary>
    [Fact]
    public void ACreatureWithNoDefinitionAnswersZero()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var bare = world.CreateCharacter();
        world.PlaceCharacter(bare, new Point3D(100, 100, 0, 0));

        Assert.Equal("0", Read(bare, "SOUNDDIE"));
        Assert.Equal("0", Read(bare, "MOVERATE"));
    }

    /// <summary>MAXFOOD reads the ceiling the engine works from, at every level of the
    /// instance-definition-default chain.</summary>
    [Fact]
    public void MaxFoodReadsTheEffectiveCeiling()
    {
        var declared = FromDef("MAXFOOD=35");
        Assert.Equal("35", Read(declared, "MAXFOOD"));
        Assert.Equal(declared.MaxFood.ToString(), Read(declared, "MAXFOOD"));

        // An instance value wins over the definition's.
        Assert.True(declared.TrySetProperty("MAXFOOD", "12"));
        Assert.Equal("12", Read(declared, "MAXFOOD"));
    }
}
