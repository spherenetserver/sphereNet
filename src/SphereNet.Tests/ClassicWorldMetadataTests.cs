using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ClassicWorldMetadataTests : IDisposable
{
    private readonly DirectoryInfo _save = Directory.CreateTempSubdirectory("sphnet_classic_metadata_");
    private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });

    public void Dispose()
    {
        _logs.Dispose();
        _save.Delete(recursive: true);
    }

    private void Write(string family, string content) =>
        File.WriteAllText(Path.Combine(_save.FullName, $"sphere{family}.scp"), content);

    private GameWorld Load()
    {
        var world = new GameWorld(_logs);
        world.InitMap(0, 256, 256);
        world.InitMap(1, 256, 256);
        new WorldLoader(_logs).Load(world, _save.FullName);
        return world;
    }

    [Theory]
    [InlineData("TIME=123\nTIMEHIRES=72899996038", 72_899_996_038L)]
    [InlineData("TIME=123", 123_000L)]
    public void ClassicClockIsReadFromTheFileHeader(string clockHeader, long expectedMilliseconds)
    {
        Write("world", "[EOF]\n");
        Write("data", $"TITLE=Sphere World Script\nVERSION=110\n{clockHeader}\nSAVECOUNT=7\n[EOF]\n");

        Assert.Equal(expectedMilliseconds, Load().GameClockMs);
    }

    [Fact]
    public void AnOlderWorldOnlySaveRestoresItsClockInTenths()
    {
        Write("world", "TITLE=Sphere World Script\nVERSION=0.56d\nTIME=123\n[EOF]\n");

        Assert.Equal(12_300L, Load().GameClockMs);
    }

    [Fact]
    public void ClassicSectorsAndGmPagesSurviveTheFirstSphereNetSave()
    {
        Write("world", """
            [SECTOR 128,192,0,1]
            LIGHT=0
            RAINCHANCE=0
            COLDCHANCE=20
            SEASON=3
            [SECTOR 64,128,0,1]
            FLAGS=01
            [EOF]
            """);
        Write("data", """
            [GMPAGE legacy_account]
            CHARUID=014ecb
            P=144,150,5,1
            REASON=I need help
            TIME=021d
            [EOF]
            """);

        GmPage.NowSeconds = () => 1_000_000;
        try
        {
            var world = Load();
            CheckMetadata(world);

            Assert.True(new WorldSaver(_logs).Save(world, _save.FullName));
            CheckMetadata(Load());
        }
        finally
        {
            GmPage.NowSeconds = null;
        }
    }

    private static void CheckMetadata(GameWorld world)
    {
        var sector = Assert.IsType<Sector>(world.GetSector(new Point3D(128, 192, 0, 1)));
        Assert.True(sector.IsLightOverridden);
        Assert.Equal(0, sector.Light);
        Assert.Equal(0, sector.RainChance);
        Assert.Equal(20, sector.ColdChance);
        Assert.Equal(3, sector.Season);
        Assert.Equal(SectorFlag.NoSleep, world.GetSector(new Point3D(64, 128, 0, 1))!.Flags);

        var page = Assert.Single(world.GmPages);
        Assert.Equal("legacy_account", page.Account);
        Assert.Equal(new Serial(0x14ECB), page.CharUid);
        Assert.Equal(new Point3D(144, 150, 5, 1), page.Position);
        Assert.Equal("I need help", page.Reason);
        Assert.Equal(0x21DL, page.AgeSeconds);
    }
}
