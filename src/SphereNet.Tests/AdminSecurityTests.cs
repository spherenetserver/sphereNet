using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;
using SphereNet.Server.Admin;

namespace SphereNet.Tests;

public class AdminSecurityTests
{
    private static TelnetConsole CreateTelnet(string adminPassword)
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var config = new SphereConfig { AdminPassword = adminPassword };
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var accounts = new AccountManager(loggerFactory);
        return new TelnetConsole(world, accounts, config, () => 0,
            loggerFactory.CreateLogger<TelnetConsole>(), loggerFactory);
    }

    [Fact]
    public void TelnetConsole_Start_RejectsEmptyAdminPassword()
    {
        using var telnet = CreateTelnet("");

        Assert.False(telnet.Start(0));
    }

    [Fact]
    public void TelnetConsole_Start_AllowsConfiguredAdminPassword()
    {
        using var telnet = CreateTelnet("change-me");

        Assert.True(telnet.Start(0));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("change-me", true)]
    public void AdminHostPolicy_CanStartTelnet_RequiresConfiguredPassword(string password, bool expected)
    {
        Assert.Equal(expected, AdminHostPolicy.CanStartTelnet(new SphereConfig { AdminPassword = password }));
    }
}
