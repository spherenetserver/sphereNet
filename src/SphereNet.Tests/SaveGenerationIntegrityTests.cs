using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A save generation is consistent only if EVERY file in it comes from the same save
/// (review finding B2).
///
/// The check existed and looked at three files: <c>ItemPaths[0]</c>,
/// <c>CharPaths[0]</c>, <c>DataPaths[0]</c>. A sharded save has more than one of each,
/// so on a two-shard shard layout the SECOND world shard was never compared with
/// anything — an older copy of it could be dropped in and the loader would accept the
/// mixture and build a world out of two different points in time. The reproduction in
/// the review returned (8, 0): eight items from the stale shard, and a character file
/// that no longer described them.
///
/// The second half was quieter. The loop abandoned verification entirely at the first
/// UNSTAMPED file — <c>id == null → return true</c> — so one legacy or truncated shard
/// switched the check off for every other file in the generation, including the ones
/// that were stamped and did disagree.
/// </summary>
public sealed class SaveGenerationIntegrityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public SaveGenerationIntegrityTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_gen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static WorldSaver Saver(int shards, int backupLevels = 0) =>
        new(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = shards, BackupLevels = backupLevels };

    /// <summary>A world with <paramref name="items"/> loose items and one character.</summary>
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

    private string[] WorldShards() =>
        Directory.GetFiles(_dir, "sphereworld*.scp")
            .Where(f => !f.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private (int Items, int Chars) LoadOrThrow()
    {
        var dst = World();
        return new WorldLoader(LoggerFactory.Create(_ => { })).Load(dst, _dir);
    }

    // ---- the finding -----------------------------------------------------

    [Fact]
    public void AStaleSecondShardIsNotAccepted()
    {
        // Generation A: eight items across two shards.
        Assert.True(Saver(shards: 2).Save(Populated(8), _dir));
        var shards = WorldShards();
        Assert.True(shards.Length >= 2, $"expected a sharded save, got {shards.Length} world file(s)");
        string staleSecond = File.ReadAllText(shards[1]);

        // Generation B: a different world, saved over it.
        Assert.True(Saver(shards: 2).Save(Populated(3), _dir));

        // Put generation A's SECOND shard back. Its first shard, its characters and
        // the server data all belong to B.
        File.WriteAllText(shards[1], staleSecond);

        var ex = Record.Exception(() => LoadOrThrow());
        _out.WriteLine($"load of the mixed generation: {ex?.GetType().Name ?? "accepted"}");

        // Accepting this builds a world out of two different points in time: items
        // whose container, owner and equip links name objects the rest of the
        // generation never had. Refusing is the only safe answer - there is a backup
        // to fall back to, and none here, so the load must say so rather than invent a
        // world.
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void AStaleShardIsNotAcceptedOnTheCharacterSideEither()
    {
        Assert.True(Saver(shards: 2).Save(Populated(8), _dir));
        var charShards = Directory.GetFiles(_dir, "spherechars*.scp")
            .Where(f => !f.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(charShards.Length >= 2);
        string stale = File.ReadAllText(charShards[1]);

        Assert.True(Saver(shards: 2).Save(Populated(3), _dir));
        File.WriteAllText(charShards[1], stale);

        // Same rule, other base. The original check happened to look at index 0 of
        // each list, so which base was tampered with decided whether it was noticed.
        Assert.IsType<InvalidDataException>(Record.Exception(() => LoadOrThrow()));
    }

    [Fact]
    public void OneUnstampedShardDoesNotSwitchTheCheckOffForTheRest()
    {
        Assert.True(Saver(shards: 2).Save(Populated(8), _dir));
        var shards = WorldShards();
        string staleSecond = File.ReadAllText(shards[1]);

        Assert.True(Saver(shards: 2).Save(Populated(3), _dir));

        // One shard loses its stamp - a legacy file, a truncated write, a hand edit -
        // and another is stale. The unstamped one cannot be verified; that is no
        // reason to stop verifying the one that CAN be.
        File.WriteAllText(shards[0], StripStamp(File.ReadAllText(shards[0])));
        File.WriteAllText(shards[1], staleSecond);

        var ex = Record.Exception(() => LoadOrThrow());
        _out.WriteLine($"unstamped + stale: {ex?.GetType().Name ?? "accepted"}");
        Assert.IsType<InvalidDataException>(ex);
    }

    // ---- what must keep working -----------------------------------------

    [Fact]
    public void AnUntamperedShardedSaveStillLoads()
    {
        var src = Populated(8);
        Assert.True(Saver(shards: 2).Save(src, _dir));

        var (items, chars) = LoadOrThrow();
        _out.WriteLine($"clean two-shard save -> items={items} chars={chars}");

        Assert.Equal(8, items);
        Assert.Equal(1, chars);
    }

    [Fact]
    public void ASaveWithNoStampsAtAllIsStillAccepted()
    {
        Assert.True(Saver(shards: 2).Save(Populated(4), _dir));
        foreach (string f in WorldShards().Concat(
                     Directory.GetFiles(_dir, "spherechars*.scp")
                         .Where(x => !x.Contains(".bak", StringComparison.OrdinalIgnoreCase))))
            File.WriteAllText(f, StripStamp(File.ReadAllText(f)));

        var (items, _) = LoadOrThrow();
        _out.WriteLine($"unstamped save -> items={items}");

        // A classic Sphere save carries no generation stamp at all. Refusing those
        // would turn a compatibility guarantee into a startup failure; the check can
        // only reject files that actively DISAGREE.
        Assert.Equal(4, items);
    }

    [Fact]
    public void AMixedGenerationFallsBackToAConsistentBackup()
    {
        // With a backup to fall back to, the answer is not an exception - it is the
        // previous generation, whole.
        Assert.True(Saver(shards: 2, backupLevels: 1).Save(Populated(8), _dir));
        Assert.True(Saver(shards: 2, backupLevels: 1).Save(Populated(3), _dir));

        var shards = WorldShards();
        string fromBackup = File.ReadAllText(
            Path.Combine(_dir, Path.GetFileName(shards[1]) + ".bak1"));
        File.WriteAllText(shards[1], fromBackup);

        var (items, chars) = LoadOrThrow();
        _out.WriteLine($"mixed current, good backup -> items={items} chars={chars}");

        Assert.Equal(8, items);
        Assert.Equal(1, chars);
    }

    // ---- B1: an incomplete generation must not read as an empty world ----

    [Fact]
    public void AMigrationWhoseNewWorldFileIsLostDoesNotBootAnEmptyWorld()
    {
        // The review's setup: a Text save, then a Binary one, backups on. The world
        // and character files change extension; spheredata keeps its name.
        var saverA = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 2 };
        Assert.True(saverA.Save(Populated(8), _dir));

        var saverB = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Binary, ShardCount = 0, BackupLevels = 2 };
        Assert.True(saverB.Save(Populated(8), _dir));

        _out.WriteLine("after migration: " + string.Join(", ",
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x)));

        // The new world file is lost - a bad sector, a truncated write, a hand-edit.
        foreach (string f in Directory.GetFiles(_dir, "sphereworld*.sbin"))
            File.Delete(f);
        foreach (string f in Directory.GetFiles(_dir, "spherechars*.sbin"))
            File.Delete(f);

        var (items, chars) = LoadOrThrow();
        _out.WriteLine($"after losing the migrated world files -> items={items} chars={chars}");

        // Two things had to be true for this to recover. The migration has to KEEP the
        // superseded generation - backups rotate by file name, so nothing rotated the
        // .scp world into the .sbin chain and the old files were simply deleted,
        // leaving nothing to fall back to. And the loader has to refuse what remains
        // when the new file is gone: server data with no world or character file is
        // not an empty world, it is the residue of a publish that did not finish.
        // Accepting it returns (0, 0), and the next save writes that emptiness over
        // the backups - which is how a recoverable incident becomes a permanent one.
        Assert.Equal(8, items);
        Assert.Equal(1, chars);
    }

    [Fact]
    public void AFormatMigrationKeepsThePreviousGenerationAsABackup()
    {
        var text = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 2 };
        Assert.True(text.Save(Populated(6), _dir));

        var binary = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Binary, ShardCount = 0, BackupLevels = 2 };
        Assert.True(binary.Save(Populated(6), _dir));

        // The old generation lives on under its OWN extension's backup chain, because
        // that is the name it had. The loader probes both.
        Assert.True(File.Exists(Path.Combine(_dir, "sphereworld.scp.bak1")),
            "the superseded world file was deleted instead of retired");
        Assert.True(File.Exists(Path.Combine(_dir, "spherechars.scp.bak1")),
            "the superseded character file was deleted instead of retired");
    }

    [Fact]
    public void WithBackupsOffAMigrationStillCleansUpAfterItself()
    {
        var text = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(text.Save(Populated(6), _dir));

        var binary = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Binary, ShardCount = 0, BackupLevels = 0 };
        Assert.True(binary.Save(Populated(6), _dir));

        _out.WriteLine("backups off: " + string.Join(", ",
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x)));

        // A shard that asked for no backups gets none - retiring the old generation
        // would be keeping a backup it turned off. The stale file still goes, so the
        // old format cannot shadow the new one.
        Assert.False(File.Exists(Path.Combine(_dir, "sphereworld.scp")));
        Assert.False(File.Exists(Path.Combine(_dir, "sphereworld.scp.bak1")));
        Assert.True(File.Exists(Path.Combine(_dir, "sphereworld.sbin")));
    }

    [Fact]
    public void AGenerationOfServerDataAloneIsNotAWorld()
    {
        Assert.True(Saver(shards: 0).Save(Populated(5), _dir));
        foreach (string f in Directory.GetFiles(_dir, "sphereworld*")
                     .Concat(Directory.GetFiles(_dir, "spherechars*")))
            File.Delete(f);

        var ex = Record.Exception(() => LoadOrThrow());
        _out.WriteLine($"data-only generation: {ex?.GetType().Name ?? "accepted"}");

        // The same shape without the migration: a fresh shard has NO files at all,
        // and that is the only thing an empty world may be built from.
        Assert.NotNull(ex);
    }

    [Fact]
    public void AnEmptyDirectoryIsStillAFreshStart()
    {
        var (items, chars) = LoadOrThrow();
        _out.WriteLine($"empty directory -> items={items} chars={chars}");

        // The control. Refusing here would turn a first boot into a startup failure.
        Assert.Equal(0, items);
        Assert.Equal(0, chars);
    }

    private static string StripStamp(string text)
    {
        var kept = new System.Collections.Generic.List<string>();
        bool inStamp = false;
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal))
                inStamp = t.StartsWith("[SAVEID", StringComparison.OrdinalIgnoreCase);
            if (inStamp) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }
}
