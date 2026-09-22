using SphereNet.Core.Types;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A written point separates on comma, space OR tab.
///
/// Source-X hands " ,\t" to Str_ParseCmds for every coordinate list it reads
/// (CPointBase::Read), so a pack may write a point either way and routinely does:
/// the live map file carries
///
///     [AREADEF a_safe_zone]
///     P=1978 2080,0,0
///
/// among four hundred comma-separated neighbours. Splitting on the comma alone
/// handed "1978 2080" to the number parser, which failed, which left the region
/// with no P at all — and nothing said so. The help menu's Stuck option reads
/// SERV.AREA.a_safe_zone.P, got the 0,0,0,0 that a missing P reads back as, and
/// teleported the player to the corner of the map, where there is nowhere to walk.
/// One malformed-looking space, a player stuck at 0,0, and no error anywhere.
/// </summary>
public sealed class WrittenPointSeparatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("1978 2080,0,0", 1978, 2080, 0, 0)]      // the live AREADEF
    [InlineData("1978,2080,0,0", 1978, 2080, 0, 0)]      // the ordinary form
    [InlineData("1978 2080 0 0", 1978, 2080, 0, 0)]      // all spaces
    [InlineData("1978, 2080, 0, 0", 1978, 2080, 0, 0)]   // comma then space
    [InlineData("  1978\t2080 , 0,0  ", 1978, 2080, 0, 0)] // tabs and padding
    [InlineData("2623,466,14,0", 2623, 466, 14, 0)]      // a neighbour, unchanged
    public void EveryWrittenFormReadsAsTheSamePoint(string text, int x, int y, int z, int map)
    {
        var parts = Point3D.SplitComponents(text);
        output.WriteLine($"'{text}' -> [{string.Join("] [", parts)}]");
        Assert.Equal(4, parts.Length);
        Assert.Equal(x, int.Parse(parts[0]));
        Assert.Equal(y, int.Parse(parts[1]));
        Assert.Equal(z, int.Parse(parts[2]));
        Assert.Equal(map, int.Parse(parts[3]));
    }

    [Fact]
    public void RepeatedCommasStillLeaveAnEmptyComponent()
    {
        // Whitespace runs collapse because Str_Parse skips leading whitespace before
        // each argument; a second COMMA is a second separator and keeps its empty
        // argument. Callers rely on the difference — an empty component means "leave
        // this coordinate as it is", which is not the same as "not supplied".
        var parts = Point3D.SplitComponents("100,,5");
        output.WriteLine($"'100,,5' -> [{string.Join("] [", parts)}]");
        Assert.Equal(["100", "", "5"], parts);
    }

    [Fact]
    public void NothingWrittenIsNoComponents()
    {
        Assert.Empty(Point3D.SplitComponents(null));
        Assert.Empty(Point3D.SplitComponents("   "));
    }
}
