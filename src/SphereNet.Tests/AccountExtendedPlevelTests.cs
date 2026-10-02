using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Formats;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Sphere 56T custom-version compatibility: account files from that version write
/// levels above Owner by name (PLEVEL=Founder / PLEVEL=Root). They used to fail the name
/// parse and leave a staff account at Player.</summary>
public sealed class AccountExtendedPlevelTests
{
    private static AccountManager NewManager() => new(LoggerFactory.Create(b => { }));

    private static string WriteAccountFile(string dir, string plevel)
    {
        string path = Path.Combine(dir, "sphereaccu.scp");
        File.WriteAllText(path,
            "[staffer]\r\nPLEVEL=" + plevel + "\r\nPASSWORD=pw\r\n\r\n[EOF]\r\n");
        return path;
    }

    [Theory]
    [InlineData("Root")]
    [InlineData("Founder")]
    [InlineData("9")]
    public void AnExtendedLevelLoadsAsOwnerAndIsWrittenBack(string plevel)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_plevel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            WriteAccountFile(tmp, plevel);
            var accounts = NewManager();
            Assert.Equal(1, AccountPersistence.Load(accounts, tmp));
            var acc = accounts.FindAccount("staffer")!;
            Assert.Equal(PrivLevel.Owner, acc.PrivLevel);

            string outDir = Path.Combine(tmp, "out");
            Directory.CreateDirectory(outDir);
            AccountPersistence.Save(accounts, outDir, SaveFormat.Text);
            string text = string.Join("\n", Directory.GetFiles(outDir, "*.scp").Select(File.ReadAllText));
            Assert.Contains("PLEVEL=" + plevel, text);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LoweringTheLevelDropsTheExtendedName()
    {
        var acc = new Account { Name = "staffer", PrivLevel = PrivLevel.Owner, ExtendedPlevelName = "Root" };
        acc.PrivLevel = PrivLevel.GM;
        Assert.Null(acc.ExtendedPlevelName);
    }
}
