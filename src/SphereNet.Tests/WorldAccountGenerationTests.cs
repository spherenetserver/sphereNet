using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The accounts and the world are one save (review work item D01).
///
/// They were not published as one. In background mode the account file was written
/// the moment the world snapshot was captured, while the shards were still being
/// encoded on the writer thread — so a world write that then failed published the NEW
/// accounts beside the PREVIOUS world. Recovery has the same shape from the other end:
/// a world that comes back from a .bakN is older than the account file, which is a
/// single file nobody rotates.
///
/// Either way the damage is quiet. An account slot names a character the world does
/// not hold: the player sees a "?" in the character list, the slot still counts
/// against the seven, and nothing in the log ever said so.
///
/// Three things are asserted here: an account snapshot is published only when the
/// world write commits, the file names the generation it belongs to so a mismatch can
/// be seen at all, and a slot pointing into nothing is reported by the boot audit.
/// </summary>
public sealed class WorldAccountGenerationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public WorldAccountGenerationTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_accgen_{Guid.NewGuid():N}");
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

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static GameWorld Populated(int items)
    {
        var w = World();
        var ch = w.CreateCharacter();
        ch.Name = "Owner";
        w.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        for (int i = 0; i < items; i++)
        {
            var it = w.CreateItem();
            it.BaseId = 0x0EED;
            w.PlaceItem(it, new Point3D((short)(1001 + i), 1000, 0, 0));
        }
        return w;
    }

    private static WorldSaver Saver(int backupLevels = 0) =>
        new(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 1, BackupLevels = backupLevels };

    // ---- publishing ------------------------------------------------------

    [Fact]
    public void AnAccountSnapshotStagedForAFailedWorldWriteIsNotPublished()
    {
        // The generation on disk: one account, and the world it belongs to.
        AccountPersistence.Save(WithAccounts("veteran"), _dir, SaveFormat.Text, null, generation: 111);

        // The next save captures a second account — and then its world write fails.
        var staged = AccountPersistence.Stage(
            WithAccounts("veteran", "newcomer"), _dir, SaveFormat.Text, null, generation: 222);
        AccountPersistence.Discard(staged);

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);

        // Publishing it anyway is the defect: "newcomer" would hold a character slot
        // in a world that was never written, and the world on disk has never heard of
        // the character behind it.
        _out.WriteLine($"accounts={result.Count} generation={result.Generation}");
        Assert.Equal(1, result.Count);
        Assert.Equal(111, result.Generation);
        Assert.Null(loaded.FindAccount("newcomer"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AStagedSnapshotBecomesLiveWhenItIsCommitted()
    {
        // The control for the test above: every assertion there would pass just as
        // well if staging had stopped writing anything at all.
        AccountPersistence.Save(WithAccounts("veteran"), _dir, SaveFormat.Text, null, generation: 111);

        var staged = AccountPersistence.Stage(
            WithAccounts("veteran", "newcomer"), _dir, SaveFormat.Text, null, generation: 222);
        Assert.Equal(2, AccountPersistence.Commit(staged));

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);
        Assert.Equal(2, result.Count);
        Assert.Equal(222, result.Generation);
        Assert.NotNull(loaded.FindAccount("newcomer"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ---- the stamp -------------------------------------------------------

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void TheAccountFileNamesTheGenerationItWasWrittenFor(SaveFormat fmt)
    {
        const long gen = 0x0123456789ABCDL;
        Assert.Equal(2, AccountPersistence.Save(
            WithAccounts("alpha", "beta"), _dir, fmt, null, generation: gen));

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);

        // Every format goes through the same writer, but the stamp is a record like
        // any other, and a format that dropped it would leave the mismatch check
        // permanently blind rather than failing loudly.
        Assert.Equal(gen, result.Generation);
        Assert.Equal(2, result.Count);
        Assert.NotNull(loaded.FindAccount("alpha"));
        Assert.NotNull(loaded.FindAccount("beta"));
    }

    [Fact]
    public void TheStampIsNotReadBackAsAnAccount()
    {
        AccountPersistence.Save(WithAccounts("alpha"), _dir, SaveFormat.Text, null, generation: 999);

        var loaded = NewManager();
        Assert.Equal(1, AccountPersistence.Load(loaded, _dir));

        // The reader treats any non-reserved section as an account name, so a stamp
        // record it did not know about would arrive as a passwordless account called
        // SAVEID — one anybody could log into.
        Assert.Null(loaded.FindAccount("SAVEID"));
        Assert.Single(loaded.GetAllAccounts());
    }

    [Fact]
    public void AnUnstampedAccountFileStillLoads()
    {
        // What every account file written before the stamp existed looks like. It
        // cannot be checked against the world, and that has to mean "unknown" rather
        // than "mismatch": treating it as a mismatch would fire on every shard the
        // first time it ran this build.
        Assert.Equal(1, AccountPersistence.Save(WithAccounts("veteran"), _dir, SaveFormat.Text));

        var loaded = NewManager();
        var result = AccountPersistence.LoadSnapshot(loaded, _dir);
        Assert.Null(result.Generation);
        Assert.Equal(1, result.Count);
        Assert.NotNull(loaded.FindAccount("veteran"));
    }

    // ---- the mismatch a rollback leaves ----------------------------------

    [Fact]
    public void AWorldRecoveredFromABackupReportsTheGenerationItActuallyLoaded()
    {
        var saver = Saver(backupLevels: 2);

        Assert.True(saver.Save(Populated(4), _dir));
        long firstGen = saver.LastGeneration;

        // The accounts are published with the second save, as they are on a shard
        // that is running normally.
        Assert.True(saver.Save(Populated(6), _dir));
        long secondGen = saver.LastGeneration;
        Assert.NotEqual(firstGen, secondGen);
        AccountPersistence.Save(WithAccounts("veteran"), _dir, SaveFormat.Text, null, secondGen);

        // Something goes wrong with the second save and the operator does what the
        // loader's own error message tells them to: copy a matching set of .bak1 files
        // over the current ones. The world is now a generation behind; the account
        // file, which is one file and is never rotated, is not.
        foreach (string backup in Directory.GetFiles(_dir, "*.bak1"))
            File.Copy(backup, backup[..^5], overwrite: true);

        var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
        var (items, _) = loader.Load(World(), _dir);
        _out.WriteLine($"items={items} worldGen={loader.LoadedGeneration} firstGen={firstGen} secondGen={secondGen}");

        // Four items: the older generation. The world is a save behind the account
        // file, and the only way anything downstream can notice is that the world
        // names the generation it loaded.
        Assert.Equal(4, items);
        Assert.Equal(firstGen, loader.LoadedGeneration);
        Assert.Equal(secondGen, AccountPersistence.LoadSnapshot(NewManager(), _dir).Generation);
        Assert.NotEqual(loader.LoadedGeneration, AccountPersistence.LoadSnapshot(NewManager(), _dir).Generation);
    }

    [Fact]
    public void AWorldAndAccountFileFromTheSameSaveAgree()
    {
        // The control: the check must not fire on a shard where nothing went wrong.
        var saver = Saver(backupLevels: 2);
        Assert.True(saver.Save(Populated(4), _dir));
        AccountPersistence.Save(WithAccounts("veteran"), _dir, SaveFormat.Text, null, saver.LastGeneration);

        var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
        loader.Load(World(), _dir);

        Assert.Equal(loader.LoadedGeneration, AccountPersistence.LoadSnapshot(NewManager(), _dir).Generation);
    }

    // ---- what the mismatch does to an account ----------------------------

    [Fact]
    public void AnAccountSlotNamingAMissingCharacterIsReported()
    {
        var world = World();
        var ch = world.CreateCharacter();
        ch.Name = "Alive";
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        var accounts = WithAccounts("veteran");
        var acc = accounts.FindAccount("veteran")!;
        acc.SetCharSlot(0, ch.Uid);
        // Slot 1 is what a rolled-back world leaves behind: a character created in a
        // save the world no longer has.
        acc.SetCharSlot(1, new Serial(0x0BADF00D));

        var anomalies = SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(world, accounts);
        foreach (var a in anomalies) _out.WriteLine(a.ToString());

        var missing = Assert.Single(anomalies,
            a => a.Kind == SphereNet.Game.Diagnostics.WorldInvariantAuditor.Kind.AccountCharSlotMissing);
        Assert.Equal(0x0BADF00Du, missing.Uid);
        Assert.Contains("veteran", missing.Detail);
        Assert.Contains("slot 1", missing.Detail);
    }

    [Fact]
    public void AnAccountWhoseCharactersAllExistIsNotReported()
    {
        var world = World();
        var ch = world.CreateCharacter();
        ch.Name = "Alive";
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        var accounts = WithAccounts("veteran");
        accounts.FindAccount("veteran")!.SetCharSlot(0, ch.Uid);

        // The audit runs at every boot; one false positive per account would bury the
        // real ones.
        Assert.DoesNotContain(
            SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(world, accounts),
            a => a.Kind == SphereNet.Game.Diagnostics.WorldInvariantAuditor.Kind.AccountCharSlotMissing);
    }
}
