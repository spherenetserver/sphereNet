using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Character delete and login refusals answer the way upstream does
/// (Setup_Delete, CClientMsg.cpp:2943; addLoginErr, CClientLog.cpp:146).
/// The delete checked the packet's password field, which upstream never reads and
/// ClassicUO sends blank, so every delete from that client was refused; and every
/// login refusal said "incorrect password", a banned account included.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharDeleteAndLoginCodeTests
{
    private static (GameClient Client, SphereNet.Network.State.NetState State, Account Account,
        SphereNet.Game.Objects.Characters.Character Ch, GameWorld World) DeleteHarness(ILoggerFactory lf)
    {
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(30_000, 40_000));
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var account = new Account { Name = "tester" };
        account.SetPassword("pw");
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "victim";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        account.SetCharSlot(0, ch.Uid);
        TestHarness.AttachCharacter(client, null!, account);
        return (client, state, account, ch, world);
    }

    [Fact]
    public void ABlankDeletePasswordStillDeletes()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var (client, state, account, ch, world) = DeleteHarness(lf);

        client.HandleCharDelete(0, "");

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        Assert.Null(world.FindChar(ch.Uid));
        Assert.False(account.GetCharSlot(0).IsValid);
        Assert.Contains(packets, p => p.Span[0] == 0x86);
        Assert.DoesNotContain(packets, p => p.Span[0] == 0x85);
    }

    [Fact]
    public void AnEmptySlotIsBadPassAndAnOutOfRangeSlotDoesNotExist()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var (client, state, _, _, _) = DeleteHarness(lf);

        client.HandleCharDelete(1, "");
        client.HandleCharDelete(200, "");

        var codes = TestHarness.GetQueuedPackets(state).Where(p => p.Span[0] == 0x85)
            .Select(p => p.Span[1]).ToList();
        Assert.Equal(new byte[] { 0, 1 }, codes);
    }

    private static byte LoginRefusal(Action<AccountManager> setup, string name, string password)
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(40_000, 50_000));
        var accounts = new AccountManager(lf);
        setup(accounts);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        client.HandleLoginRequest(name, password);
        var denied = TestHarness.GetQueuedPackets(state).Single(p => p.Span[0] == 0x82);
        return denied.Span[1];
    }

    [Fact]
    public void LoginRefusalsSayWhy()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0x00, LoginRefusal(a => a.AutoCreateAccounts = false, "nobody" + suffix, "pw"));
        Assert.Equal(0x03, LoginRefusal(a => a.CreateAccount("wrong" + suffix, "pw"), "wrong" + suffix, "nope"));
        Assert.Equal(0x02, LoginRefusal(a => a.CreateAccount("banned" + suffix, "pw")!.IsBanned = true,
            "banned" + suffix, "pw"));
    }
}

[Collection("DefinitionLoaderSerial")]
public sealed class LoginTriesTempBanTests
{
    private static byte? Attempt(AccountManager accounts, GameWorld world, ILoggerFactory lf, int id)
    {
        var state = TestHarness.CreateActiveNetState(lf, id);
        typeof(SphereNet.Network.State.NetState)
            .GetProperty("RemoteEndPoint")!
            .SetValue(state, new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.9"), 5000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        client.HandleLoginRequest("tries_acct", "wrong");
        var denied = TestHarness.GetQueuedPackets(state).FirstOrDefault(p => p.Span[0] == 0x82);
        return denied == null ? null : denied.Span[1];
    }

    /// <summary>CLIENTLOGINMAXTRIES=2: the third attempt within 15 s is refused with
    /// "other" (upstream MaxPassTries) before the password is even looked at.</summary>
    [Fact]
    public void TheAttemptPastTheLimitIsTurnedAway()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        accounts.CreateAccount("tries_acct", "right");
        GameClient.ConfigureLoginTries(2, TimeSpan.FromMinutes(3));

        Assert.Equal((byte)3, Attempt(accounts, world, lf, 51001));
        Assert.Equal((byte)3, Attempt(accounts, world, lf, 51002));
        Assert.Equal((byte)4, Attempt(accounts, world, lf, 51003));
    }
}
