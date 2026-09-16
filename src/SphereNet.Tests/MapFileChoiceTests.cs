using SphereNet.MapData;

namespace SphereNet.Tests;

/// <summary>
/// Which terrain file the server reads has to be the one the CLIENT reads, or the
/// two disagree about the ground with nothing reporting it. ClassicUO takes the
/// UOP terrain only when the folder is a UOP installation - a MainMisc.uop beside
/// the rest (UOFileManager:29) - and otherwise reads map{N}.mul even with the .uop
/// file present (MapLoader.cs:147-166).
/// </summary>
public sealed class MapFileChoiceTests
{
    private static string Folder(params string[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_mulchoice_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (string f in files)
            File.WriteAllBytes(Path.Combine(dir, f), new byte[196]);
        return dir;
    }

    private static string? ChosenTerrain(string dir, int width = 8, int height = 8)
    {
        var md = new MapDataManager(dir);
        string? chosen = null;
        // Statics files notify through the same event; only the terrain choice is
        // under test here.
        md.OnMapFileLoaded += (_, path) =>
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("map", StringComparison.OrdinalIgnoreCase))
                chosen = name;
        };
        try { md.InitMap(0, width, height); }
        catch (FileNotFoundException) { return null; }
        catch (InvalidDataException)
        {
            // The placeholder .uop is not a real container, so the reader rejects
            // it - but reaching that reader at all IS the choice under test, and it
            // is made before any byte is parsed.
            return $"map{0}LegacyMUL.uop";
        }
        return chosen;
    }

    /// <summary>The readers keep the terrain file mapped, so a failed delete is the
    /// file still being open - not a test result. The folder is under the system temp
    /// either way.</summary>
    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void WithoutMainMiscTheMulIsRead_EvenThoughTheUopIsThere()
    {
        // The shape that produced the bug: a shard's own legacy map copied in next
        // to a client install's UOP. The client reads the MUL here.
        string dir = Folder("map0.mul", "map0LegacyMUL.uop", "staidx0.mul", "statics0.mul");
        Assert.Equal("map0.mul", ChosenTerrain(dir));
        Cleanup(dir);
    }

    [Fact]
    public void WithMainMiscTheUopIsRead()
    {
        string dir = Folder("MainMisc.uop", "map0.mul", "map0LegacyMUL.uop",
                            "staidx0.mul", "statics0.mul");
        Assert.Equal("map0LegacyMUL.uop", ChosenTerrain(dir));
        Cleanup(dir);
    }

    [Fact]
    public void TheMulIsStillReadWhenItIsTheOnlyTerrainFile()
    {
        string dir = Folder("map0.mul", "staidx0.mul", "statics0.mul");
        Assert.Equal("map0.mul", ChosenTerrain(dir));
        Cleanup(dir);
    }
}
