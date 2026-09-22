using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CAccount::OnLogin / Setup_Play / OnLogout bookkeeping. Admin dialogs
/// read ACCOUNT.FIRSTIP, FIRSTCONNECTDATE, LASTCONNECTDATE and TOTALCONNECTTIME;
/// none of them was ever written at runtime, so every account showed blanks and
/// a connect time of zero.
/// </summary>
public sealed class AccountConnectRecordTests
{
    private static Account NewAccount() =>
        new AccountManager(NullLoggerFactory.Instance).CreateAccount("rec", "pw")!;

    private static string Get(Account a, string key)
    {
        Assert.True(a.TryGetProperty(key, out string v));
        return v;
    }

    [Fact]
    public void FirstLoginStampsFirstAndLastIp()
    {
        var a = NewAccount();
        a.RecordLogin("10.0.0.1");
        Assert.Equal("10.0.0.1", Get(a, "FIRSTIP"));
        Assert.Equal("10.0.0.1", Get(a, "LASTIP"));
        Assert.Matches(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}$", Get(a, "FIRSTCONNECTDATE"));
    }

    [Fact]
    public void OnceTimeIsRecordedTheFirstIpStays()
    {
        var a = NewAccount();
        a.RecordLogin("10.0.0.1");
        a.RecordLogout(TimeSpan.FromMinutes(42));
        a.RecordLogin("10.0.0.2");
        Assert.Equal("10.0.0.1", Get(a, "FIRSTIP"));
        Assert.Equal("10.0.0.2", Get(a, "LASTIP"));
    }

    [Fact]
    public void LogoutAccumulatesMinutes()
    {
        var a = NewAccount();
        a.RecordLogout(TimeSpan.FromMinutes(30));
        a.RecordLogout(TimeSpan.FromMinutes(12.9));
        Assert.Equal("12", Get(a, "LASTCONNECTTIME"));
        Assert.Equal("42", Get(a, "TOTALCONNECTTIME"));
    }

    [Fact]
    public void CharacterEnterMovesThePreviousDateToLastLogged()
    {
        var a = NewAccount();
        Assert.Equal("", Get(a, "LASTCONNECTDATE"));
        a.RecordCharacterEnter();
        string first = Get(a, "LASTCONNECTDATE");
        Assert.Matches(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}$", first);
        a.RecordCharacterEnter();
        Assert.Equal(first, a.Tags.Get("LastLogged"));
    }
}
