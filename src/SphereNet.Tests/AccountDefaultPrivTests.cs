using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;

namespace SphereNet.Tests;

/// <summary>
/// A new account is a PLAYER, not a guest.
///
/// Upstream decides it by name and nothing else: a new CAccount is PLEVEL_Player unless
/// its name begins with GUEST or it was created as one, in which case PLEVEL_Guest
/// (CAccount.cpp:593).
///
/// Everything here defaulted to Guest, which is a level below the one the command
/// surface and the shipped scripts are written against - so an ordinary player was
/// refused things every ordinary player can do, for no reason anyone would find in a
/// script.
/// </summary>
public sealed class AccountDefaultPrivTests
{
    private static AccountManager Manager()
    {
        var lf = LoggerFactory.Create(_ => { });
        return new AccountManager(lf);
    }

    [Fact]
    public void ANewAccountIsAPlayer()
    {
        var mgr = Manager();
        var acc = mgr.CreateAccount("someone", "pw");
        Assert.NotNull(acc);
        Assert.Equal(PrivLevel.Player, acc!.PrivLevel);
    }

    /// <summary>The one case upstream keeps as a guest.</summary>
    [Theory]
    [InlineData("guest")]
    [InlineData("GUEST")]
    [InlineData("Guest17")]
    public void AGuestNamedAccountIsAGuest(string name)
    {
        var mgr = Manager();
        var acc = mgr.CreateAccount(name, "pw");
        Assert.NotNull(acc);
        Assert.Equal(PrivLevel.Guest, acc!.PrivLevel);
    }

    /// <summary>A bare account object carries the same default, so an account built
    /// anywhere else does not quietly come out a level lower.</summary>
    [Fact]
    public void ABareAccountCarriesTheSameDefault()
        => Assert.Equal(PrivLevel.Player, new Account().PrivLevel);

    /// <summary>The shard can still ask for something else, and a name-based guest
    /// stays a guest whatever the default is.</summary>
    [Fact]
    public void TheConfiguredDefaultStillWins()
    {
        var mgr = Manager();
        mgr.DefaultPrivLevel = PrivLevel.Counsel;

        Assert.Equal(PrivLevel.Counsel, mgr.CreateAccount("staffer", "pw")!.PrivLevel);
        Assert.Equal(PrivLevel.Guest, mgr.CreateAccount("guest_visitor", "pw")!.PrivLevel);
    }
}
