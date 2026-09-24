using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Operational parity with Source-X: MAP0..MAP255 read the way
/// CUOMapList::Load reads them, the FREEZERESTARTTIME rule, the sphereacct.scp
/// hand-edit file and the USEMAPDIFFS patch files.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class OpsReadinessTests
{
    private static SphereConfig LoadIni(string body)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_ops_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, "[SPHERE]\n" + body);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);
            return config;
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void MapLinesPastFiveAreReadAndAMalformedLineDoesNotStopStartup()
    {
        var config = LoadIni("""
            MAP0=6144,4096,64,0,0
            MAP1=
            MAP7=junk,4096,xx,5,7
            MAP12=1001,1600,48,2,12
            """);

        Assert.Contains(config.Maps, m => m.MapSendId == 0 && m.MaxX == 6144);
        Assert.DoesNotContain(config.Maps, m => m.MapSendId == 1);            // MAP1= disables
        var m7 = Assert.Single(config.Maps, m => m.MapSendId == 7);
        Assert.Equal((1280, 4096, 5), (m7.MaxX, m7.MaxY, m7.MapReadId));        // map5's default width kept
        var m12 = Assert.Single(config.Maps, m => m.MapSendId == 12);
        Assert.Equal((2304, 1600, 64), (m12.MaxX, m12.MaxY, m12.SectorSize)); // 1001 and 48 refused
        var warnings = config.Validate();
        Assert.Contains(warnings, w => w.Contains("MAP12") && w.Contains("multiple of 8"));
        Assert.Contains(warnings, w => w.Contains("MAP12") && w.Contains("power of 2"));
    }

    [Fact]
    public void AHungLoopIsDeclaredAfterTwoChecksWithoutProgress()
    {
        var watch = new SphereNet.Server.Program.FreezeWatch();
        Assert.False(watch.Check(1));
        Assert.False(watch.Check(2));
        Assert.False(watch.Check(2));  // first miss
        Assert.True(watch.Check(2));   // second miss: hung
        Assert.False(watch.Check(3));  // moving again resets
    }

    [Fact]
    public void TheChangesFileIsMergedOnLoadAndEmptiedAfterASave()
    {
        using var lf = LoggerFactory.Create(_ => { });
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_acct_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var first = new AccountManager(lf);
            first.CreateAccount("alice", "pw1");
            AccountPersistence.Save(first, dir, SaveFormat.Text);
            File.WriteAllText(Path.Combine(dir, AccountPersistence.ChangesFileName),
                "[ACCOUNT alice]\nPLEVEL=4\n\n[ACCOUNT bob]\nPLEVEL=1\nPASSWORD=secret\n");

            var loaded = new AccountManager(lf);
            AccountPersistence.Load(loaded, dir);
            Assert.Equal(SphereNet.Core.Enums.PrivLevel.GM, loaded.FindAccount("alice")!.PrivLevel);
            Assert.NotNull(loaded.FindAccount("bob"));

            AccountPersistence.Save(loaded, dir, SaveFormat.Text);
            string changes = File.ReadAllText(Path.Combine(dir, AccountPersistence.ChangesFileName));
            Assert.DoesNotContain("[ACCOUNT", changes);
            var again = new AccountManager(lf);
            AccountPersistence.Load(again, dir);
            Assert.NotNull(again.FindAccount("bob"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void MapDiffsReplaceTerrainAndStaticBlocks()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_diff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A 16x16 map: 2x2 blocks of 196 bytes, every cell tile 3.
            var map = new byte[4 * 196];
            for (int b = 0; b < 4; b++)
                for (int c = 0; c < 64; c++)
                    map[b * 196 + 4 + c * 3] = 3;
            File.WriteAllBytes(Path.Combine(dir, "map0.mul"), map);
            var idx = new byte[4 * 12];
            for (int i = 0; i < idx.Length; i++) idx[i] = 0xFF;
            File.WriteAllBytes(Path.Combine(dir, "staidx0.mul"), idx);
            File.WriteAllBytes(Path.Combine(dir, "statics0.mul"), [0, 0, 0, 0, 0, 0, 0]);

            // Patch block 1 (blockX 0, blockY 1): terrain tile 0x1234, one static.
            File.WriteAllBytes(Path.Combine(dir, "mapdifl0.mul"), BitConverter.GetBytes(1));
            var block = new byte[196];
            for (int c = 0; c < 64; c++) { block[4 + c * 3] = 0x34; block[4 + c * 3 + 1] = 0x12; }
            File.WriteAllBytes(Path.Combine(dir, "mapdif0.mul"), block);
            File.WriteAllBytes(Path.Combine(dir, "stadifl0.mul"), BitConverter.GetBytes(1));
            var sidx = new byte[12];
            BitConverter.GetBytes(0).CopyTo(sidx, 0);
            BitConverter.GetBytes(7).CopyTo(sidx, 4);
            File.WriteAllBytes(Path.Combine(dir, "stadifi0.mul"), sidx);
            File.WriteAllBytes(Path.Combine(dir, "stadif0.mul"), [0x01, 0x10, 2, 3, 5, 0, 0]);

            using var md = new SphereNet.MapData.MapDataManager(dir) { UseMapDiffs = true };
            md.InitMap(0, 16, 16);

            Assert.Equal(3, md.GetTerrainTile(0, 1, 1).TileId);        // block 0 untouched
            Assert.Equal(0x1234, md.GetTerrainTile(0, 1, 9).TileId);   // block 1 patched
            var statics = md.GetStatics(0, 2, 11);                     // block 1, offset 2,3
            Assert.Equal(0x1001, Assert.Single(statics).TileId);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
