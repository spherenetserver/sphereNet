using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// An account snapshot staged beside a background world save is published only once
/// the world write commits. Between the two, ordinary account saves (a password, a
/// ban, a new account) still run. The staged snapshot is a transaction of its own:
/// nothing else may consume or delete its file, and publishing it must never put
/// back an account state older than one already on disk.
/// </summary>
public sealed class AccountSaveTransactionTests : IDisposable
{
    private readonly string _dir;

    public AccountSaveTransactionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_acctx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static AccountManager NewManager() => new(LoggerFactory.Create(_ => { }));

    private static AccountManager WithAccounts(params string[] names)
    {
        var m = NewManager();
        foreach (string n in names) m.CreateAccount(n, "pw");
        return m;
    }

    [Fact]
    public void AStagedSnapshotSurvivesANormalAccountSaveBeforeItIsPublished()
    {
        var accounts = WithAccounts("veteran");
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);

        // A background world save (generation 200) stages the accounts...
        var staged = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 200);

        // ...and while its world is still being written, an account is created and
        // saved the normal way, stamped with the world that is on disk.
        accounts.CreateAccount("newcomer", "pw");
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);

        // The world write commits; its staged accounts are published.
        AccountPersistence.Commit(staged);

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);
        Assert.Equal(200, result.Generation);
        Assert.NotNull(loaded.FindAccount("veteran"));
        Assert.NotNull(loaded.FindAccount("newcomer"));
    }

    [Fact]
    public void PublishingAnOlderStagedSnapshotDoesNotUndoANewerSavedChange()
    {
        var accounts = WithAccounts("veteran");
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);

        var staged = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 200);

        // The password changes after the snapshot was rendered, and that change is
        // saved. The staged file still holds the old password.
        accounts.SetAccountPassword("veteran", "rotated");
        accounts.SetAccountBlocked("veteran", true);
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);

        AccountPersistence.Commit(staged);

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);
        var veteran = loaded.FindAccount("veteran")!;
        Assert.True(veteran.CheckPassword("rotated"));
        Assert.False(veteran.CheckPassword("pw"));
        Assert.True(veteran.IsBanned);
        // The generation the published file names is the world it was staged for.
        Assert.Equal(200, result.Generation);
    }

    [Fact]
    public void DiscardingOneStagedSnapshotLeavesAnotherOnesFileAlone()
    {
        var accounts = WithAccounts("veteran");
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);

        var failed = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 200);
        accounts.CreateAccount("newcomer", "pw");
        var other = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 300);

        // The world write the first snapshot belonged to failed.
        AccountPersistence.Discard(failed);

        Assert.Equal(2, AccountPersistence.Commit(other));
        var loaded = NewManager();
        Assert.Equal(300, AccountPersistence.LoadSnapshot(loaded, _dir).Generation);
        Assert.NotNull(loaded.FindAccount("newcomer"));
    }

    [Fact]
    public void AWorldWriterTmpSweepInASharedDirectoryDoesNotTakeTheStagedAccountFile()
    {
        // AccountDir and WorldSaveDir may be the same directory. A failed world write
        // removes every *.tmp there; an account transaction in flight must not be one.
        var accounts = WithAccounts("veteran", "newcomer");
        var staged = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 200);

        foreach (string tmp in Directory.GetFiles(_dir, "*.tmp"))
            File.Delete(tmp);

        Assert.Equal(2, AccountPersistence.Commit(staged));
        Assert.Equal(200, AccountPersistence.LoadSnapshot(NewManager(), _dir).Generation);
    }

    [Fact]
    public void ADiscardedStagedSnapshotLeavesNoFileBehind()
    {
        var accounts = WithAccounts("veteran");
        AccountPersistence.Save(accounts, _dir, SaveFormat.Text, null, generation: 100);
        var before = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        var staged = AccountPersistence.Stage(accounts, _dir, SaveFormat.Text, null, generation: 200);
        AccountPersistence.Discard(staged);

        var after = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(before, after);
    }
}
