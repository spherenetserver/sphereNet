using Microsoft.Extensions.Logging;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A [MoonGates] line is a point with a label, not a label with a point.
///
/// The packs write "1336,1997,5,0=mg_britain" - eight per facet - and upstream reads
/// the section with ReadKey, taking GetKey() as the point and ignoring what follows
/// (CServerConfig.cpp:4080). This read the point from the other side of the '=', so
/// every line parsed as (0,0) and was dropped: the list was always empty while the log
/// reported it had loaded them, which is the shape of thing a log line is supposed to
/// catch and instead concealed.
///
/// Nothing consumes the list yet - a moongate here teleports to its own MOREP rather
/// than rotating with the moon phase as upstream's does (CCharUse.cpp:207) - so this
/// corrects a value rather than changing behaviour. It is the phase model that would
/// need the list, and that is a gameplay decision, not a parse.
/// </summary>
public sealed class MoongateSectionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_mg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ResourceHolder Load(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "m.scp");
        File.WriteAllLines(file, lines);

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        res.LoadResourceFile(file);
        return res;
    }

    /// <summary>The shape the shard's own map files are written in.</summary>
    [Fact]
    public void ThePointIsTheKeyAndTheLabelFollowsIt()
    {
        var res = Load("[MoonGates]",
            "1336,1997,5,0=mg_britain",
            "4467,1283,5,0=mg_moonglow",
            "1828,2948,-20,0=mg_trinsic");

        Assert.Equal(3, res.Moongates.Count);

        var britain = res.Moongates[0];
        Assert.Equal("mg_britain", britain.Name);
        Assert.Equal(1336, britain.Point.X);
        Assert.Equal(1997, britain.Point.Y);
        Assert.Equal(5, britain.Point.Z);

        // A negative height is a real coordinate, not a parse failure.
        Assert.Equal(-20, res.Moongates[2].Point.Z);
    }

    /// <summary>A bare point with no label is a gate too.</summary>
    [Fact]
    public void ALabelIsOptional()
    {
        var res = Load("[MoonGates]", "771,752,5,0");

        var gate = Assert.Single(res.Moongates);
        Assert.Equal(771, gate.Point.X);
        Assert.Equal(752, gate.Point.Y);
    }

    /// <summary>A line that names no point is skipped rather than landing a gate at
    /// the origin.</summary>
    [Fact]
    public void ALineWithNoPointIsSkipped()
    {
        var res = Load("[MoonGates]", "mg_britain=1336,1997,5,0", "nonsense");

        Assert.Empty(res.Moongates);
    }
}
