using System.IO;
using SphereNet.Core.Configuration;
using Xunit;

namespace SphereNet.Tests;

/// <summary>sphere.ini keys read in the unit and form Source-X reads them.</summary>
public sealed class IniUnitParityTests
{
    private static SphereConfig Load(string body)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[SPHERE]\n" + body);
            var parser = new IniParser();
            parser.Load(path);
            var cfg = new SphereConfig();
            cfg.LoadFromIni(parser);
            return cfg;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Defaults_FollowSourceX()
    {
        var cfg = new SphereConfig();
        Assert.False(cfg.Md5Passwords);               // m_fMd5Passwords = false
        Assert.Equal(10, cfg.CombatArcheryMovementDelay); // tenths
        Assert.Equal(200, cfg.MaxBaseSkill);
        Assert.Equal(2, cfg.UseHttpMode);
        Assert.True(cfg.UseHttp);
    }

    [Fact]
    public void UseHttp2_IsOn_And0_IsOff()
    {
        Assert.True(Load("UseHttp=2\n").UseHttp);
        Assert.False(Load("UseHttp=0\n").UseHttp);
    }

    [Fact]
    public void TickPeriod_IsTicksPerSecond()
    {
        Assert.Equal(100, Load("TICKPERIOD=10\n").ServerTickMs);
        Assert.Equal(50, Load("TICKPERIOD=20\n").ServerTickMs);
        Assert.Equal(80, Load("TICKPERIOD=10\nServerTickMs=80\n").ServerTickMs);
    }

    [Fact]
    public void SentryDsn_IsWrittenWithoutItsScheme()
    {
        Assert.Equal("https://key@host/1", Load("SentryDsn=key@host/1\n").SentryDsn);
        Assert.Equal("", Load("SentryDsn=https://key@host/1\n").SentryDsn); // cut to "https:"
        Assert.Equal("", Load("ServName=x\n").SentryDsn);
    }
}
