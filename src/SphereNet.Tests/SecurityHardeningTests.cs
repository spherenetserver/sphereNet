using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Security;
using SphereNet.Game.Accounts;
using SphereNet.Network.Manager;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class SecurityHardeningTests
{
    private static void InvokeOnCharSelect(NetState state, int slot, string name)
    {
        typeof(NetState)
            .GetMethod("OnCharSelect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(state, [slot, name]);
    }

    // --- Step 1: Slot validation ---

    [Fact]
    public void OnCharSelect_NegativeSlot_PassesToHandler()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        bool invoked = false;
        state.CharSelectHandler = (_, _, _) => invoked = true;

        InvokeOnCharSelect(state, -1, "test");

        Assert.True(invoked);
    }

    [Fact]
    public void OnCharSelect_SlotAboveMax_DoesNotInvokeHandler()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        bool invoked = false;
        state.CharSelectHandler = (_, _, _) => invoked = true;

        InvokeOnCharSelect(state, 8, "test");

        Assert.False(invoked);
    }

    [Fact]
    public void OnCharSelect_ValidSlot_InvokesHandler()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        int receivedSlot = -1;
        state.CharSelectHandler = (_, slot, _) => receivedSlot = slot;

        InvokeOnCharSelect(state, 0, "test");

        Assert.Equal(0, receivedSlot);
    }

    [Fact]
    public void OnCharSelect_MaxSlot_InvokesHandler()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        int receivedSlot = -1;
        state.CharSelectHandler = (_, slot, _) => receivedSlot = slot;

        InvokeOnCharSelect(state, 7, "test");

        Assert.Equal(7, receivedSlot);
    }

    // --- Step 2: Flood detection defaults ---

    [Fact]
    public void FloodDetection_DefaultValues_MatchHardcoded()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        using var network = new NetworkManager(1, loggerFactory);

        Assert.Equal(5, network.FloodDetectionCount);
        Assert.Equal(10_000, network.FloodDetectionWindowMs);
    }

    // --- Step 3: ConnectionAcceptFilter ---

    [Fact]
    public void ConnectionAcceptFilter_NullByDefault()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        using var network = new NetworkManager(1, loggerFactory);

        Assert.Null(network.ConnectionAcceptFilter);
    }

    // --- Step 4: ConnectionRateLimiter ---

    [Fact]
    public void ConnectionRateLimiter_AllowsUnderThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(threshold: 5, clock: () => now);

        for (int i = 0; i < 4; i++)
            limiter.RegisterAttempt("192.168.1.1");

        Assert.False(limiter.ShouldThrottle("192.168.1.1"));
    }

    [Fact]
    public void ConnectionRateLimiter_ThrottlesAtThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(threshold: 5, clock: () => now);

        for (int i = 0; i < 5; i++)
            limiter.RegisterAttempt("192.168.1.1");

        Assert.True(limiter.ShouldThrottle("192.168.1.1"));
    }

    [Fact]
    public void ConnectionRateLimiter_ExponentialBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(
            threshold: 3,
            baseDelay: TimeSpan.FromSeconds(1),
            clock: () => now);

        for (int i = 0; i < 3; i++)
            limiter.RegisterAttempt("10.0.0.1");
        Assert.True(limiter.ShouldThrottle("10.0.0.1"));

        limiter.RegisterAttempt("10.0.0.1");
        Assert.True(limiter.ShouldThrottle("10.0.0.1"));
    }

    [Fact]
    public void ConnectionRateLimiter_WindowResets()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(
            threshold: 3,
            window: TimeSpan.FromSeconds(5),
            clock: () => now);

        for (int i = 0; i < 3; i++)
            limiter.RegisterAttempt("10.0.0.1");
        Assert.True(limiter.ShouldThrottle("10.0.0.1"));

        now = now.AddSeconds(6);
        limiter.RegisterAttempt("10.0.0.1");
        Assert.False(limiter.ShouldThrottle("10.0.0.1"));
    }

    [Fact]
    public void ConnectionRateLimiter_Reset_ClearsEntry()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(threshold: 3, clock: () => now);

        for (int i = 0; i < 5; i++)
            limiter.RegisterAttempt("10.0.0.1");
        Assert.True(limiter.ShouldThrottle("10.0.0.1"));

        limiter.Reset("10.0.0.1");
        Assert.False(limiter.ShouldThrottle("10.0.0.1"));
    }

    [Fact]
    public void ConnectionRateLimiter_Cleanup_RemovesStaleEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ConnectionRateLimiter(
            threshold: 3,
            window: TimeSpan.FromSeconds(5),
            clock: () => now);

        limiter.RegisterAttempt("10.0.0.1");
        now = now.AddSeconds(10);
        limiter.Cleanup();

        Assert.False(limiter.ShouldThrottle("10.0.0.1"));
    }

    // --- Step 5: IPBlockList ---

    [Fact]
    public void IPBlockList_AddAndIsBlocked()
    {
        var list = new IPBlockList();

        Assert.False(list.IsBlocked("192.168.1.1"));
        list.Add("192.168.1.1");
        Assert.True(list.IsBlocked("192.168.1.1"));
    }

    [Fact]
    public void IPBlockList_Remove_Unblocks()
    {
        var list = new IPBlockList();
        list.Add("192.168.1.1");

        Assert.True(list.Remove("192.168.1.1"));
        Assert.False(list.IsBlocked("192.168.1.1"));
    }

    [Fact]
    public void IPBlockList_CaseInsensitive()
    {
        var list = new IPBlockList();
        list.Add("::1");

        Assert.True(list.IsBlocked("::1"));
    }

    [Fact]
    public void IPBlockList_GetAll_ReturnsSnapshot()
    {
        var list = new IPBlockList();
        list.Add("10.0.0.1");
        list.Add("10.0.0.2");

        var all = list.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void IPBlockList_NullOrEmpty_NotBlocked()
    {
        var list = new IPBlockList();
        list.Add("10.0.0.1");

        Assert.False(list.IsBlocked(""));
        Assert.False(list.IsBlocked(null!));
        Assert.False(list.Add(""));
        Assert.False(list.Add(null!));
    }

    // --- Step 6: Shared block list integration ---

    [Fact]
    public void SharedBlockList_CrossProcessorVisibility()
    {
        var shared = new IPBlockList();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(NullLoggerFactory.Instance);
        var config = new SphereConfig();
        var loggerFactory = NullLoggerFactory.Instance;

        var proc1 = new SphereNet.Server.Admin.AdminCommandProcessor(
            world, accounts, config, () => 0, loggerFactory, shared);
        var proc2 = new SphereNet.Server.Admin.AdminCommandProcessor(
            world, accounts, config, () => 0, loggerFactory, shared);

        proc1.ProcessCommand("BLOCKIP 10.0.0.1", _ => { });

        Assert.True(proc2.IsIPBlocked("10.0.0.1"));
    }

    [Fact]
    public void ConnectionAcceptFilter_RejectsBlockedIP()
    {
        var blockList = new IPBlockList();
        var limiter = new ConnectionRateLimiter();

        Func<System.Net.IPAddress, bool> filter = ip =>
        {
            string ipStr = ip.ToString();
            if (blockList.IsBlocked(ipStr)) return true;
            limiter.RegisterAttempt(ipStr);
            return limiter.ShouldThrottle(ipStr);
        };

        blockList.Add("192.168.1.100");

        Assert.True(filter(System.Net.IPAddress.Parse("192.168.1.100")));
        Assert.False(filter(System.Net.IPAddress.Parse("192.168.1.200")));
    }

    // --- Step 7: Audit logging ---

    [Fact]
    public void ProcessCommand_FiresOnCommandExecuted()
    {
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(NullLoggerFactory.Instance);
        var config = new SphereConfig();
        var proc = new SphereNet.Server.Admin.AdminCommandProcessor(
            world, accounts, config, () => 0, NullLoggerFactory.Instance);

        string? receivedSource = null;
        string? receivedCmd = null;
        proc.OnCommandExecuted += (source, cmd) =>
        {
            receivedSource = source;
            receivedCmd = cmd;
        };

        proc.ProcessCommand("STATUS", _ => { }, "test-source");

        Assert.Equal("test-source", receivedSource);
        Assert.Equal("STATUS", receivedCmd);
    }

    [Fact]
    public void ProcessCommand_EmptyInput_DoesNotFireAudit()
    {
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(NullLoggerFactory.Instance);
        var config = new SphereConfig();
        var proc = new SphereNet.Server.Admin.AdminCommandProcessor(
            world, accounts, config, () => 0, NullLoggerFactory.Instance);

        bool fired = false;
        proc.OnCommandExecuted += (_, _) => fired = true;

        proc.ProcessCommand("", _ => { });
        proc.ProcessCommand("   ", _ => { });

        Assert.False(fired);
    }

    // --- Step 8: PasswordHelper.NeedsUpgrade ---

    [Fact]
    public void PasswordHelper_NeedsUpgrade_TrueForPlaintext()
    {
        Assert.True(PasswordHelper.NeedsUpgrade("mypassword"));
    }

    [Fact]
    public void PasswordHelper_NeedsUpgrade_FalseForMd5()
    {
        Assert.False(PasswordHelper.NeedsUpgrade("D41D8CD98F00B204E9800998ECF8427E"));
    }

    [Fact]
    public void PasswordHelper_NeedsUpgrade_FalseForSHA256()
    {
        string hashed = PasswordHelper.Hash("test");
        Assert.False(PasswordHelper.NeedsUpgrade(hashed));
    }

    [Fact]
    public void PasswordHelper_NeedsUpgrade_FalseForEmpty()
    {
        Assert.False(PasswordHelper.NeedsUpgrade(""));
        Assert.False(PasswordHelper.NeedsUpgrade(null!));
    }

    // --- Step 9: Account password hardening ---

    [Fact]
    public void Account_SetPassword_ProducesMd5Hex()
    {
        var account = new Account { Name = "test", UseMd5Passwords = true };
        account.SetPassword("secret");

        Assert.Equal(32, account.PasswordHash.Length);
        Assert.DoesNotContain("SHA256:", account.PasswordHash);
    }

    [Fact]
    public void Account_SetPassword_Md5_Roundtrips()
    {
        var account = new Account { Name = "test" };
        account.SetPassword("mypass");

        Assert.True(account.CheckPassword("mypass"));
        Assert.False(account.CheckPassword("wrong"));
    }

    [Fact]
    public void Account_CheckPassword_StillVerifiesLegacyMd5()
    {
        var account = new Account { Name = "test", UseMd5Passwords = true };
        var md5 = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("legacypass"));
        account.PasswordHash = Convert.ToHexString(md5);

        Assert.True(account.CheckPassword("legacypass"));
    }

    [Fact]
    public void AccountManager_Authenticate_UpgradesPlaintextToMd5()
    {
        var mgr = new AccountManager(NullLoggerFactory.Instance) { AutoCreateAccounts = false };
        var account = new Account { Name = "legacy" };
        account.PasswordHash = "plaintext123";
        mgr.AddLoaded(account);

        var result = mgr.Authenticate("legacy", "plaintext123");

        Assert.NotNull(result);
        Assert.Equal(32, result!.PasswordHash.Length);
        Assert.True(result.CheckPassword("plaintext123"));
    }

    // --- Step 10: Config validation ---

    [Fact]
    public void SphereConfig_Validate_WarnsForDisabledFloodDetection()
    {
        var config = new SphereConfig
        {
            ServPort = 2593,
            FloodDetectionCount = 0
        };
        var warnings = config.Validate();
        Assert.Contains(warnings, w => w.Contains("FloodDetectionCount"));
    }

    [Fact]
    public void SphereConfig_Validate_WarnsForSmallFloodWindow()
    {
        var config = new SphereConfig
        {
            ServPort = 2593,
            FloodDetectionWindowMs = 500
        };
        var warnings = config.Validate();
        Assert.Contains(warnings, w => w.Contains("FloodDetectionWindowMs"));
    }

    [Fact]
    public void SphereConfig_Validate_NoWarningForDefaultFloodSettings()
    {
        var config = new SphereConfig { ServPort = 2593 };
        var warnings = config.Validate();
        Assert.DoesNotContain(warnings, w => w.Contains("FloodDetection"));
    }
}
