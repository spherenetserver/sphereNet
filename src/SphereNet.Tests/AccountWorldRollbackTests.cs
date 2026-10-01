using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A world that comes back from a backup generation must come back with the
/// character slots of that same generation (Source-X saves sphereaccu through the
/// same backup rotation as the world, CAccounts::Account_SaveAll ->
/// CWorld::OpenScriptBackup). Account fields that do not point into the world - the
/// password, the ban, the privilege level, TAGs - keep their newest value.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AccountWorldRollbackTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;
    private readonly string _worldDir;
    private readonly string _acctDir;

    public AccountWorldRollbackTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), $"sphnet_acctrb_{Guid.NewGuid():N}");
        _worldDir = Path.Combine(_root, "save");
        _acctDir = Path.Combine(_root, "accounts");
        Directory.CreateDirectory(_worldDir);
        Directory.CreateDirectory(_acctDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static AccountManager NewManager() => new(LoggerFactory.Create(_ => { }));

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Character PlayerChar(GameWorld world, string name, string account, int x)
    {
        var ch = world.CreateCharacter();
        ch.Name = name;
        ch.IsPlayer = true;
        ch.SetTag("ACCOUNT", account);
        world.PlaceCharacter(ch, new Point3D((short)x, 1000, 0, 0));
        return ch;
    }

    private static WorldSaver Saver(int backupLevels) =>
        new(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 1, BackupLevels = backupLevels };

    /// <summary>Generation A: one character. Generation B: a second character in a
    /// new slot and a new account. After B, a normal account save changes the
    /// password, the ban flag and the privilege level. Then the current world is
    /// replaced by its .bak1 (generation A), the way an operator or a corrupt
    /// current save brings it back.</summary>
    private (long GenA, long GenB, Serial CharA, Serial CharB) WriteAThenBThenRollBack(int backupLevels)
    {
        // The world always keeps its backups; backupLevels is the account side.
        var saver = Saver(2);
        var world = World();
        var accounts = NewManager();
        accounts.CreateAccount("veteran", "pw");

        var charA = PlayerChar(world, "Old", "veteran", 1000);
        accounts.FindAccount("veteran")!.SetCharSlot(0, charA.Uid);
        accounts.FindAccount("veteran")!.LastCharUid = charA.Uid;
        accounts.FindAccount("veteran")!.Tags.Set("REWARD", "1");
        Assert.True(saver.Save(world, _worldDir));
        long genA = saver.LastGeneration;
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, genA, backupLevels);

        var charB = PlayerChar(world, "New", "veteran", 1010);
        accounts.FindAccount("veteran")!.SetCharSlot(1, charB.Uid);
        accounts.FindAccount("veteran")!.LastCharUid = charB.Uid;
        accounts.FindAccount("veteran")!.Tags.Set("REWARD", "2");
        accounts.CreateAccount("newcomer", "pw");
        Assert.True(saver.Save(world, _worldDir));
        long genB = saver.LastGeneration;
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, genB, backupLevels);

        // Normal account changes after B, saved outside a world save.
        accounts.SetAccountPassword("veteran", "rotated");
        accounts.SetAccountBlocked("newcomer", true);
        accounts.SetAccountPrivLevel("veteran", SphereNet.Core.Enums.PrivLevel.GM);
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, genB, backupLevels);

        foreach (string backup in Directory.GetFiles(_worldDir, "*.bak1"))
            File.Copy(backup, backup[..^5], overwrite: true);

        return (genA, genB, charA.Uid, charB.Uid);
    }

    private sealed record Booted(
        WorldLoader Loader, GameWorld World, AccountManager Accounts,
        AccountPersistence.AccountLoadResult Snapshot,
        AccountPersistence.AccountReconcileResult? Reconcile);

    /// <summary>Boot the way Program does: accounts first, the world second, and
    /// the accounts reconciled once the loader has picked the generation it will
    /// materialise - before it links characters into account slots.</summary>
    private Booted Boot()
    {
        var accounts = NewManager();
        var snap = AccountPersistence.LoadSnapshot(accounts, _acctDir);
        var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
        AccountPersistence.AccountReconcileResult? rec = null;
        loader.GenerationSelected = worldGen =>
            rec = AccountPersistence.ReconcileWithWorldGeneration(accounts, _acctDir, snap, worldGen);
        var world = World();
        loader.Load(world, _worldDir, accounts);
        return new Booted(loader, world, accounts, snap, rec);
    }

    [Fact]
    public void AWorldRecoveredFromABackupGetsTheCharacterSlotsOfItsOwnGeneration()
    {
        var (genA, genB, charA, charB) = WriteAThenBThenRollBack(backupLevels: 2);
        var boot = Boot();
        _out.WriteLine($"genA={genA} genB={genB} world={boot.Loader.LoadedGeneration} rec={boot.Reconcile}");

        Assert.Equal(genA, boot.Loader.LoadedGeneration);
        Assert.Equal(genB, boot.Snapshot.Generation);
        Assert.Equal(AccountPersistence.AccountReconcileOutcome.Restored, boot.Reconcile!.Outcome);

        var veteran = boot.Accounts.FindAccount("veteran")!;
        // The slot created in B names a character world A has never held.
        Assert.Equal(charA, veteran.GetCharSlot(0));
        Assert.False(veteran.GetCharSlot(1).IsValid);
        Assert.Equal(1, Enumerable.Range(0, 7).Count(i => veteran.GetCharSlot(i).IsValid));
        Assert.Null(boot.World.FindChar(charB));
        Assert.Equal(charA, veteran.LastCharUid);
        // TAGs are account data a script or an administrator may have set: they keep
        // their newest value, and the account is named so it can be reviewed.
        Assert.Equal("2", veteran.Tags.Get("REWARD"));
        Assert.Contains("veteran", boot.Reconcile.AccountsWithNewerTags);
        Assert.Contains("newcomer", boot.Reconcile.AccountsCreatedSince);

        // Changes that do not point into the world keep their newest value.
        Assert.True(veteran.CheckPassword("rotated"));
        Assert.Equal(SphereNet.Core.Enums.PrivLevel.GM, veteran.PrivLevel);
        var newcomer = boot.Accounts.FindAccount("newcomer");
        Assert.NotNull(newcomer);
        Assert.True(newcomer!.IsBanned);
        Assert.DoesNotContain(Enumerable.Range(0, 7), i => newcomer.GetCharSlot(i).IsValid);

        // Nothing in the account slots points outside the world that loaded.
        Assert.DoesNotContain(
            SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(boot.World, boot.Accounts),
            a => a.Kind == SphereNet.Game.Diagnostics.WorldInvariantAuditor.Kind.AccountCharSlotMissing);

        // The B account file is not thrown away by the recovery: the next save
        // rotates it into the backup chain under its own generation.
        AccountPersistence.Save(boot.Accounts, _acctDir, SaveFormat.Text, null, genA, backupLevels: 2);
        Assert.NotNull(AccountPersistence.FindSnapshotForGeneration(_acctDir, genB));
    }

    [Fact]
    public void AMatchingWorldAndAccountFileAreLeftAlone()
    {
        var saver = Saver(2);
        var world = World();
        var accounts = NewManager();
        accounts.CreateAccount("veteran", "pw");
        var ch = PlayerChar(world, "Old", "veteran", 1000);
        accounts.FindAccount("veteran")!.SetCharSlot(0, ch.Uid);
        Assert.True(saver.Save(world, _worldDir));
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, saver.LastGeneration, 2);

        var boot = Boot();
        Assert.Equal(AccountPersistence.AccountReconcileOutcome.NotNeeded, boot.Reconcile!.Outcome);
        Assert.Equal(ch.Uid, boot.Accounts.FindAccount("veteran")!.GetCharSlot(0));
    }

    [Fact]
    public void WithoutAMatchingAccountSnapshotTheMismatchIsKeptAndThePreRecoveryFileIsPreserved()
    {
        // No account backup chain (an account directory written before account
        // backups existed): the generation the world rolled back to has no account
        // snapshot to restore.
        var (genA, genB, _, charB) = WriteAThenBThenRollBack(backupLevels: 0);
        var boot = Boot();

        Assert.Equal(genA, boot.Loader.LoadedGeneration);
        Assert.Equal(AccountPersistence.AccountReconcileOutcome.NoSnapshot, boot.Reconcile!.Outcome);
        Assert.Equal(genB, boot.Reconcile.AccountGeneration);

        // Nothing is guessed: the slots stay as the newest file has them, and the
        // boot audit names the one that points outside this world.
        Assert.Equal(charB, boot.Accounts.FindAccount("veteran")!.GetCharSlot(1));
        Assert.Contains(
            SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(boot.World, boot.Accounts),
            a => a.Kind == SphereNet.Game.Diagnostics.WorldInvariantAuditor.Kind.AccountCharSlotMissing);

        // The pre-recovery account file is kept aside, outside every rotation, so the
        // operator's choice stays open after the next save overwrites the live file.
        Assert.NotNull(boot.Reconcile.PreservedPath);
        Assert.True(File.Exists(boot.Reconcile.PreservedPath));
        Assert.Equal(genB, AccountPersistence.ReadGenerationStamp(boot.Reconcile.PreservedPath!));

        // The next save does not normalise the mismatch away: the marker travels in
        // the file and the following boot still reports it.
        AccountPersistence.Save(boot.Accounts, _acctDir, SaveFormat.Text, null, genA,
            backupLevels: 0, unresolvedGeneration: genB);
        var reloaded = AccountPersistence.LoadSnapshot(NewManager(), _acctDir);
        Assert.Equal(genA, reloaded.Generation);
        Assert.Equal(genB, reloaded.UnresolvedGeneration);
    }

    [Fact]
    public void AnUnresolvedMismatchMarkerIsCarriedByEveryLaterSave()
    {
        var accounts = NewManager();
        accounts.CreateAccount("veteran", "pw");
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, generation: 500,
            backupLevels: 0, unresolvedGeneration: 900);

        var loaded = AccountPersistence.LoadSnapshot(NewManager(), _acctDir);
        Assert.Equal(500, loaded.Generation);
        Assert.Equal(900, loaded.UnresolvedGeneration);

        // A save without the marker (the operator resolved it) drops it.
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, generation: 500);
        Assert.Null(AccountPersistence.LoadSnapshot(NewManager(), _acctDir).UnresolvedGeneration);
    }

    [Fact]
    public void AccountBackupsRotateOncePerWorldGeneration()
    {
        var accounts = NewManager();
        accounts.CreateAccount("veteran", "pw");
        string live = Path.Combine(_acctDir, "sphereaccu.scp");

        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 1, backupLevels: 2);
        // Normal account saves inside one world generation overwrite in place.
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 1, backupLevels: 2);
        Assert.False(File.Exists(live + ".bak1"));

        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 2, backupLevels: 2);
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 3, backupLevels: 2);
        Assert.Equal(2, AccountPersistence.ReadGenerationStamp(live + ".bak1"));
        Assert.Equal(1, AccountPersistence.ReadGenerationStamp(live + ".bak2"));

        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 4, backupLevels: 2);
        Assert.Equal(3, AccountPersistence.ReadGenerationStamp(live + ".bak1"));
        Assert.Equal(2, AccountPersistence.ReadGenerationStamp(live + ".bak2"));
        Assert.False(File.Exists(live + ".bak3"));
        Assert.Null(AccountPersistence.FindSnapshotForGeneration(_acctDir, 1));
        Assert.Equal(live + ".bak2", AccountPersistence.FindSnapshotForGeneration(_acctDir, 2));
    }

    [Fact]
    public void AFormatChangeKeepsThePreviousGenerationFindable()
    {
        var accounts = NewManager();
        accounts.CreateAccount("veteran", "pw");
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Text, null, 1, backupLevels: 2);
        AccountPersistence.Save(accounts, _acctDir, SaveFormat.Binary, null, 2, backupLevels: 2);

        string? found = AccountPersistence.FindSnapshotForGeneration(_acctDir, 1);
        Assert.NotNull(found);
        Assert.Equal(1, AccountPersistence.ReadGenerationStamp(found!));
        // The live snapshot is the new format, and only it is live.
        Assert.Equal(2, AccountPersistence.LoadSnapshot(NewManager(), _acctDir).Generation);
        Assert.False(File.Exists(Path.Combine(_acctDir, "sphereaccu.scp")));
    }
}
