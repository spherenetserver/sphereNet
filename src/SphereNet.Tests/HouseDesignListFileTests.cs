using SphereNet.Game.Housing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The client's house-design lists feed the placement whitelist (Source-X
/// LoadValidItems): tab-separated, a type row, a name row, then data. The whitelist
/// existed but nothing ever filled it, so any graphic passed.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HouseDesignListFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sn_design_{Guid.NewGuid():N}");

    public HouseDesignListFileTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "walls.txt"),
            "int\tint\tint\n" +
            "Category\tSouth1\tEast1\n" +
            "\n" +
            "1\t100\t101\n");
        File.WriteAllText(Path.Combine(_dir, "stairs.txt"),
            "int\tint\tint\n" +
            "Category\tBlock\tMultiNorth\n" +
            "1\t1822\t1971\n");
    }

    public void Dispose()
    {
        HouseDesignValidItems.ClearValidItems();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void PiecesAndStaircasesAreRead()
    {
        Assert.Equal(2, HouseDesignValidItems.LoadFromDirectory(_dir));

        Assert.True(HouseDesignValidItems.IsValidBuildTile(100, isGm: false));
        Assert.True(HouseDesignValidItems.IsValidBuildTile(1822, isGm: false));
        Assert.False(HouseDesignValidItems.IsValidBuildTile(555, isGm: false));
        Assert.True(HouseDesignValidItems.IsValidStairMulti(1971, isGm: false));
        Assert.False(HouseDesignValidItems.IsValidStairMulti(1972, isGm: false));
        Assert.True(HouseDesignValidItems.IsValidBuildTile(555, isGm: true));
    }
}
