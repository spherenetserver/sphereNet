using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.MapData;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// P.TERRAIN answers with the land tile under the object, written as hex.
///
/// Upstream writes the tile index with FormatHex (PT_TERRAIN, CPointBase.cpp:827), and
/// the shard compares it against tile ids directly - its water test asks whether the
/// ground underfoot is 05f, 055, 059, 064 or 0AA.
///
/// The value was already answered, but only spelled bare: &lt;TERRAIN&gt; worked and
/// &lt;P.TERRAIN&gt;, which is what the packs actually write, fell through. So every one
/// of those comparisons was against nothing and the water test could only be false.
/// </summary>
public sealed class PointTerrainReadTests
{
    /// <summary>A character standing on a chosen land tile. 0x5F is one of the water
    /// tiles the shard's own test names.</summary>
    private static (SphereNet.Game.Objects.Characters.Character Ch,
                    SphereNet.Game.World.GameWorld World) StandingOn(ushort landTile)
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: -3, landTile: landTile);

        var world = new SphereNet.Game.World.GameWorld(lf);
        world.InitMap(0, 256, 256);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (ch, world);
    }

    /// <summary>It answers, and with the tile the map actually reports - read from the
    /// same place so the test holds whether this machine has the shard's maps or the
    /// empty stand-in.</summary>
    [Fact]
    public void ItAnswersWithTheTileUnderfoot()
    {
        var (ch, _) = StandingOn(0x5F);

        Assert.True(ch.TryGetProperty("P.TERRAIN", out string value), "P.TERRAIN did not answer");
        Assert.Equal("05F", value);
    }

    /// <summary>The answer is hex, which is what makes the shard's comparisons work:
    /// read back as a Sphere number it is the tile id again, because of the leading
    /// zero.</summary>
    [Fact]
    public void TheAnswerReadsBackAsTheSameTile()
    {
        var (ch, _) = StandingOn(0xAA);

        ch.TryGetProperty("P.TERRAIN", out string value);
        Assert.Equal(0xAA, SphereNet.Scripting.Definitions.ValueCurve.ParseSphereNumber(value));
    }

    /// <summary>And the suffixed form still answers what it did.</summary>
    [Fact]
    public void TheHeightSuffixStillAnswers()
    {
        var (ch, _) = StandingOn(0x5F);

        Assert.True(ch.TryGetProperty("TERRAIN.Z", out string value));
        Assert.Equal("-3", value);
    }
}
