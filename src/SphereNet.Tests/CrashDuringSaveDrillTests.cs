using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The drill for a save that never finished (port plan İŞ-45 / PLAN-704).
///
/// A save FAILING and a save being KILLED are different accidents. A failure
/// throws, and the saver cleans its .tmp siblings up - BinarySaveAtomicityTests
/// and SaveTransactionalCommitTests cover that. A kill (power cut, OOM, operator
/// stopping the process mid-write) leaves the .tmp files exactly where they were,
/// with nothing renamed and no exception raised.
///
/// WorldSaver's own comment states the intended guarantee: "A crash here leaves
/// the previous generation fully intact - only stray .tmp files". Nothing verified
/// the second half of that sentence, so these drills leave the debris a kill would
/// leave and then boot.
/// </summary>
public sealed class CrashDuringSaveDrillTests
{
    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static void SaveGeneration(WorldSaver saver, string dir, string genMarker)
    {
        var w = MakeWorld();
        var item = w.CreateItem();
        item.BaseId = 0x0EED;
        item.SetTag("GEN", genMarker);
        w.PlaceItem(item, new Point3D(1500, 1500, 0, 0));
        Assert.True(saver.Save(w, dir));
    }

    private static string? LoadedGeneration(GameWorld w) =>
        w.GetAllObjects().OfType<Item>().Where(i => !i.IsDeleted)
            .Select(i => i.TryGetTag("GEN", out var g) ? g : null)
            .FirstOrDefault(g => g != null);

    private static WorldSaver NewSaver(SaveFormat fmt = SaveFormat.BinaryGz, int shards = 0) =>
        new(LoggerFactory.Create(_ => { })) { Format = fmt, ShardCount = shards, BackupLevels = 5 };

    /// <summary>Leave behind what a process killed mid-write would leave: a .tmp
    /// sibling for every committed data file, holding partial bytes. Nothing is
    /// renamed, so the committed generation is untouched.</summary>
    private static int LeaveKilledSaveDebris(string dir, byte[] partial)
    {
        int made = 0;
        foreach (var f in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(f);
            if (name.Contains(".bak") || name.EndsWith(".tmp") || name.Contains(".manifest"))
                continue;
            if (!name.Contains(".sbin") && !name.Contains(".scp"))
                continue;
            File.WriteAllBytes(f + ".tmp", partial);
            made++;
        }
        return made;
    }

    private static void WithTempDir(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_crashdrill_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- killed mid-save ---------------------------------------------------

    [Theory]
    [InlineData(SaveFormat.BinaryGz, 0)]
    [InlineData(SaveFormat.Binary, 3)]
    [InlineData(SaveFormat.Text, 0)]
    public void AKilledSaveLeavesTempFilesAndTheNextBootIgnoresThem(SaveFormat fmt, int shards)
    {
        WithTempDir(dir =>
        {
            var saver = NewSaver(fmt, shards);
            SaveGeneration(saver, dir, "A");

            // The kill: half-written temps beside an intact committed generation.
            int debris = LeaveKilledSaveDebris(dir, [0xDE, 0xAD, 0xBE, 0xEF]);
            Assert.True(debris > 0, "the drill needs debris to be meaningful");

            var booted = MakeWorld();
            var (items, _) = new WorldLoader(LoggerFactory.Create(_ => { })).Load(booted, dir);

            Assert.True(items >= 1);
            Assert.Equal("A", LoadedGeneration(booted));
        });
    }

    [Fact]
    public void TempDebrisIsNotMistakenForAGenerationWhenTheCurrentSaveIsAlsoGone()
    {
        // Worst case: killed mid-save AND the current generation lost. The loader
        // must reach the .bak1 rotation rather than treating a .tmp as data.
        WithTempDir(dir =>
        {
            var saver = NewSaver();
            SaveGeneration(saver, dir, "A");
            SaveGeneration(saver, dir, "B");   // A rotates to .bak1

            LeaveKilledSaveDebris(dir, [0x00, 0x01]);
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.Contains(".bak") || name.EndsWith(".tmp")) continue;
                File.Delete(f);                // current generation gone
            }

            var booted = MakeWorld();
            var (items, _) = new WorldLoader(LoggerFactory.Create(_ => { })).Load(booted, dir);

            Assert.True(items >= 1);
            Assert.Equal("A", LoadedGeneration(booted));
        });
    }

    [Fact]
    public void ASaveAfterAKilledOneStillCommitsAndBecomesCurrent()
    {
        // The operator restarts and the server saves again. The stale temps must
        // not block the new commit, and the new generation must win.
        WithTempDir(dir =>
        {
            var saver = NewSaver();
            SaveGeneration(saver, dir, "A");
            LeaveKilledSaveDebris(dir, [0xFF]);

            SaveGeneration(saver, dir, "B");

            var booted = MakeWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(booted, dir);
            Assert.Equal("B", LoadedGeneration(booted));
        });
    }

    // ---- the restart drill, end to end -------------------------------------

    [Fact]
    public void SaveRestartLoadThenLoseTheCurrentSaveAndRestartAgain()
    {
        // The whole drill in one sequence: run, save, restart, run, save, restart,
        // then lose the newest save and restart once more. Each "restart" is a
        // fresh world and a fresh loader, which is what a process restart is.
        WithTempDir(dir =>
        {
            var saver = NewSaver();
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));

            SaveGeneration(saver, dir, "A");
            var afterFirst = MakeWorld();
            loader.Load(afterFirst, dir);
            Assert.Equal("A", LoadedGeneration(afterFirst));

            SaveGeneration(saver, dir, "B");
            var afterSecond = MakeWorld();
            loader.Load(afterSecond, dir);
            Assert.Equal("B", LoadedGeneration(afterSecond));

            // Lose the current generation the way a bad disk would.
            foreach (var f in Directory.GetFiles(dir))
                if (!Path.GetFileName(f).Contains(".bak"))
                    File.Delete(f);

            var afterLoss = MakeWorld();
            loader.Load(afterLoss, dir);
            Assert.Equal("A", LoadedGeneration(afterLoss));
        });
    }

    [Fact]
    public void AGoodSaveLeavesNoTempFilesOfItsOwn()
    {
        // The premise of the drills above: debris only appears when a save is
        // interrupted. A clean save must not leave any, or "stray .tmp" would be
        // normal and could never signal a kill.
        WithTempDir(dir =>
        {
            SaveGeneration(NewSaver(), dir, "A");

            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        });
    }
}
