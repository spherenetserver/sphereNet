using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A world generation is loaded whole or not at all, and a save publishes a whole
/// generation or leaves the previous one recoverable.
///
/// Five holes, each one a way to boot a world that is missing part of itself while a
/// good copy sat on disk:
/// <list type="bullet">
/// <item>a file cut off exactly between two records parsed as a shorter, valid file -
/// nothing checked that it ended with its end marker, which upstream requires of every
/// world file (CWorld::LoadFile: "No [EOF] marker ... is corrupt"); and the manifest
/// recorded the sizes of the files it was REPLACING, so it could not catch it either;</item>
/// <item>a stamped generation that had lost a whole file family (world, chars or server
/// data) loaded as if the family had simply been empty;</item>
/// <item>the backup search stopped at the first missing <c>.bakN</c> level, so a gap hid
/// every older backup;</item>
/// <item>publishing rotated and promoted one file at a time, so an interruption after the
/// first file left a <c>.bak1</c> holding only that file, and it loaded as a world with no
/// characters;</item>
/// <item>a backup copy that failed was swallowed and the save reported success.</item>
/// </list>
/// </summary>
public sealed class SaveGenerationDurabilityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public SaveGenerationDurabilityTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_durable_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- helpers ---------------------------------------------------------

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    /// <summary>A world of <paramref name="items"/> loose items, every one tagged with
    /// the generation name, and <paramref name="chars"/> characters.</summary>
    private static GameWorld Populated(int items, int chars, string gen)
    {
        var w = World();
        for (int c = 0; c < chars; c++)
        {
            var ch = w.CreateCharacter();
            ch.Name = $"Owner{c}";
            w.PlaceCharacter(ch, new Point3D((short)(900 + c), 1000, 0, 0));
        }
        for (int i = 0; i < items; i++)
        {
            var it = w.CreateItem();
            it.BaseId = 0x0EED;
            it.SetTag("GEN", gen);
            w.PlaceItem(it, new Point3D((short)(1001 + i), 1000, 0, 0));
        }
        return w;
    }

    private static WorldSaver Saver(SaveFormat fmt, int shards, int backups) =>
        new(LoggerFactory.Create(_ => { }))
        { Format = fmt, ShardCount = shards, BackupLevels = backups };

    private readonly record struct Loaded(int Items, int Chars, string? Gen);

    private Loaded Load()
    {
        var w = World();
        var (items, chars) = new WorldLoader(LoggerFactory.Create(_ => { })).Load(w, _dir);
        var gens = w.GetAllObjects().OfType<Item>().Where(i => !i.IsDeleted)
            .Select(i => i.TryGetTag("GEN", out var g) ? g : null)
            .Where(g => g != null).Distinct().ToList();
        Assert.True(gens.Count <= 1, "items of two generations were loaded together: " + string.Join(",", gens));
        return new Loaded(items, chars, gens.FirstOrDefault());
    }

    private string[] LiveFiles(string prefix) =>
        Directory.GetFiles(_dir, prefix + "*")
            .Where(f => !f.Contains(".bak", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private string Listing() => string.Join(", ",
        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    /// <summary>Byte offsets at which a whole record (or the end marker) starts: cutting
    /// the file at one of them leaves only complete records and no end marker.</summary>
    private static List<int> RecordBoundaries(byte[] bytes, bool binary)
    {
        var cuts = new List<int>();
        if (binary)
        {
            int p = 8;
            while (p < bytes.Length)
            {
                cuts.Add(p);
                int sectionLen = bytes[p];
                if (sectionLen == 0) break;          // the terminator
                p += 1 + sectionLen;
                uint props = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p));
                p += 4;
                for (uint i = 0; i < props; i++)
                {
                    int keyLen = bytes[p];
                    p += 1 + keyLen;
                    int valLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p));
                    p += 2 + valLen;
                }
            }
            return cuts;
        }

        int lineStart = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            string line = Encoding.UTF8.GetString(bytes, lineStart, i - lineStart).Trim().TrimStart('﻿');
            if (line.StartsWith("[", StringComparison.Ordinal))
                cuts.Add(lineStart);
            lineStart = i + 1;
        }
        return cuts;
    }

    // ---- S01: an end marker is required, and the manifest sizes are real --------

    [Theory]
    [InlineData(SaveFormat.Text, 0)]
    [InlineData(SaveFormat.Text, 2)]
    [InlineData(SaveFormat.Binary, 0)]
    [InlineData(SaveFormat.Binary, 2)]
    public void AFileCutAtAnyRecordBoundaryFallsBackToTheWholeBackup(SaveFormat fmt, int shards)
    {
        Assert.True(Saver(fmt, shards, backups: 1).Save(Populated(3, 1, "A"), _dir));
        Assert.True(Saver(fmt, shards, backups: 1).Save(Populated(8, 1, "B"), _dir));
        Assert.Equal(new Loaded(8, 1, "B"), Load());

        bool binary = fmt == SaveFormat.Binary;
        int checkedCuts = 0;
        foreach (string victim in LiveFiles("sphereworld"))
        {
            byte[] original = File.ReadAllBytes(victim);
            foreach (int cut in RecordBoundaries(original, binary))
            {
                File.WriteAllBytes(victim, original.AsSpan(0, cut).ToArray());
                var loaded = Load();
                _out.WriteLine($"{Path.GetFileName(victim)} cut at {cut}/{original.Length} -> {loaded}");
                Assert.Equal(new Loaded(3, 1, "A"), loaded);
                checkedCuts++;
            }
            File.WriteAllBytes(victim, original);
        }
        Assert.True(checkedCuts >= 3, $"expected several record boundaries, checked {checkedCuts}");
        Assert.Equal(new Loaded(8, 1, "B"), Load());
    }

    [Fact]
    public void ServerDataWithoutItsEndMarkerIsNotAccepted()
    {
        Assert.True(Saver(SaveFormat.Text, 0, backups: 1).Save(Populated(3, 1, "A"), _dir));
        Assert.True(Saver(SaveFormat.Text, 0, backups: 1).Save(Populated(5, 1, "B"), _dir));

        string data = Path.Combine(_dir, "spheredata.scp");
        string text = File.ReadAllText(data);
        int eof = text.LastIndexOf("[EOF]", StringComparison.Ordinal);
        Assert.True(eof > 0);
        File.WriteAllText(data, text[..eof]);

        Assert.Equal(new Loaded(3, 1, "A"), Load());
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void TheManifestRecordsTheSizesOfTheShardsItPublishes(SaveFormat fmt)
    {
        for (int round = 0; round < 2; round++)
        {
            Assert.True(Saver(fmt, shards: 2, backups: 1).Save(Populated(round == 0 ? 6 : 2, 1, "G" + round), _dir));
            foreach (string baseName in new[] { "sphereworld", "spherechars" })
            {
                var manifest = ShardManifest.TryLoad(Path.Combine(_dir, baseName + ".manifest"));
                Assert.NotNull(manifest);
                Assert.Equal(2, manifest!.Files.Count);
                foreach (string f in manifest.Files)
                {
                    long actual = new FileInfo(Path.Combine(_dir, f)).Length;
                    _out.WriteLine($"round {round} {f}: manifest={manifest.FileSizes.GetValueOrDefault(f)} disk={actual}");
                    Assert.Equal(actual, manifest.FileSizes[f]);
                }
            }
        }
    }

    [Fact]
    public void AShardWhoseSizeDisagreesWithItsManifestFallsBack()
    {
        Assert.True(Saver(SaveFormat.Text, 2, backups: 1).Save(Populated(3, 1, "A"), _dir));
        Assert.True(Saver(SaveFormat.Text, 2, backups: 1).Save(Populated(8, 1, "B"), _dir));

        // Still a parseable file that ends in [EOF] - only the size gives it away.
        string victim = LiveFiles("sphereworld")[0];
        File.AppendAllText(victim, "// appended after the save\r\n");

        Assert.Equal(new Loaded(3, 1, "A"), Load());
    }

    [Fact]
    public void AnOldManifestWithMeasuredSizesStillLoads()
    {
        // Manifests written before the sizes were exact carried the size of the file
        // being replaced. They cannot be held to it.
        Assert.True(Saver(SaveFormat.Text, 2, backups: 0).Save(Populated(6, 1, "A"), _dir));
        foreach (string baseName in new[] { "sphereworld", "spherechars" })
        {
            string path = Path.Combine(_dir, baseName + ".manifest");
            var lines = File.ReadAllLines(path)
                .Where(l => !l.StartsWith("VERSION=", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase) ? "SIZE=0" : l);
            File.WriteAllLines(path, lines);
        }

        Assert.Equal(new Loaded(6, 1, "A"), Load());
    }

    // ---- S02: a stamped generation must have every family ------------------------

    [Theory]
    [InlineData("sphereworld", 0)]
    [InlineData("spherechars", 0)]
    [InlineData("spheredata", 0)]
    [InlineData("sphereworld", 2)]
    [InlineData("spherechars", 2)]
    public void AGenerationMissingAWholeFamilyFallsBackToTheWholeBackup(string family, int shards)
    {
        Assert.True(Saver(SaveFormat.Text, shards, backups: 1).Save(Populated(2, 1, "A"), _dir));
        Assert.True(Saver(SaveFormat.Text, shards, backups: 1).Save(Populated(3, 2, "B"), _dir));

        foreach (string f in Directory.GetFiles(_dir, family + "*"))
            if (!f.Contains(".bak", StringComparison.OrdinalIgnoreCase))
                File.Delete(f);

        var loaded = Load();
        _out.WriteLine($"without {family} -> {loaded}");
        Assert.Equal(new Loaded(2, 1, "A"), loaded);
    }

    [Fact]
    public void AGenerationMissingAFamilyWithNoBackupIsRefused()
    {
        Assert.True(Saver(SaveFormat.Text, 0, backups: 0).Save(Populated(3, 2, "A"), _dir));
        File.Delete(Path.Combine(_dir, "spherechars.scp"));

        Assert.IsType<InvalidDataException>(Record.Exception(() => Load()));
    }

    [Theory]
    [InlineData(SaveFormat.Text, 0)]
    [InlineData(SaveFormat.BinaryGz, 2)]
    public void AGenuinelyEmptyWorldStillSavesAndLoads(SaveFormat fmt, int shards)
    {
        Assert.True(Saver(fmt, shards, backups: 1).Save(World(), _dir));
        Assert.True(Saver(fmt, shards, backups: 1).Save(World(), _dir));
        Assert.NotEmpty(LiveFiles("sphereworld"));
        Assert.NotEmpty(LiveFiles("spherechars"));

        Assert.Equal(new Loaded(0, 0, null), Load());
    }

    [Fact]
    public void AClassicMixedSingleFileWithoutEndMarkerStillLoads()
    {
        // Classic saves carry no stamp. One file may hold items AND characters, and
        // nothing else may be beside it; and a hand-cut file without [EOF] loaded
        // before this change, so it still does.
        Assert.True(Saver(SaveFormat.Text, 0, backups: 0).Save(Populated(4, 1, "A"), _dir));
        string world = Path.Combine(_dir, "sphereworld.scp");
        string chars = Path.Combine(_dir, "spherechars.scp");
        string combined = StripStampAndEof(File.ReadAllText(world)) + StripStampAndEof(File.ReadAllText(chars));
        File.WriteAllText(world, combined);
        File.Delete(chars);
        File.Delete(Path.Combine(_dir, "spheredata.scp"));

        Assert.Equal(new Loaded(4, 1, "A"), Load());
    }

    private static string StripStampAndEof(string text)
    {
        var kept = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Equals("[EOF]", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Equals("[SAVEID]", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < lines.Length && lines[i + 1].Contains('=')) i++;
                continue;
            }
            kept.Add(lines[i]);
        }
        return string.Join("\r\n", kept) + "\r\n";
    }

    // ---- S03: a gap in the backup chain does not hide older backups --------------

    [Theory]
    [InlineData("present")]
    [InlineData("missing")]
    [InlineData("inconsistent")]
    public void AMissingBak1DoesNotHideAGoodBak2(string current)
    {
        Assert.True(Saver(SaveFormat.Text, 0, backups: 2).Save(Populated(3, 1, "A"), _dir));
        Assert.True(Saver(SaveFormat.Text, 0, backups: 2).Save(Populated(4, 1, "B"), _dir));
        Assert.True(Saver(SaveFormat.Text, 0, backups: 2).Save(Populated(5, 1, "C"), _dir));
        Assert.True(File.Exists(Path.Combine(_dir, "sphereworld.scp.bak2")));

        foreach (string f in Directory.GetFiles(_dir, "*.bak1"))
            File.Delete(f);

        switch (current)
        {
            case "missing":
                foreach (string f in Directory.GetFiles(_dir))
                    if (!f.Contains(".bak", StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
                break;
            case "inconsistent":
                File.Copy(Path.Combine(_dir, "spherechars.scp.bak2"), Path.Combine(_dir, "spherechars.scp"), overwrite: true);
                break;
        }

        var loaded = Load();
        _out.WriteLine($"current {current}, no .bak1 -> {loaded}");
        Assert.Equal(current == "present" ? new Loaded(5, 1, "C") : new Loaded(3, 1, "A"), loaded);
    }

    // ---- S04: an interrupted publish leaves a whole generation -------------------

    private sealed class InterruptedPublish(string step) : Exception($"interrupted after {step}");

    [Theory]
    [InlineData(SaveFormat.Text, 0, SaveFormat.Text, 0, 1)]
    [InlineData(SaveFormat.Text, 0, SaveFormat.Text, 0, 0)]
    [InlineData(SaveFormat.Text, 2, SaveFormat.Text, 2, 2)]
    [InlineData(SaveFormat.BinaryGz, 3, SaveFormat.BinaryGz, 3, 1)]
    [InlineData(SaveFormat.Text, 0, SaveFormat.Binary, 2, 1)]
    [InlineData(SaveFormat.Binary, 2, SaveFormat.Text, 0, 1)]
    public void AnInterruptionAfterAnyPublishStepLeavesAWholeGeneration(
        SaveFormat fmtA, int shardsA, SaveFormat fmtB, int shardsB, int backups)
    {
        var a = new Loaded(3, 1, "A");
        var b = new Loaded(4, 2, "B");
        int steps = 0;
        for (int stop = 0; ; stop++)
        {
            foreach (string f in Directory.GetFileSystemEntries(_dir))
                File.Delete(f);
            Assert.True(Saver(fmtA, shardsA, backups).Save(Populated(3, 1, "A"), _dir));

            int seen = 0;
            string? interruptedAt = null;
            var saver = Saver(fmtB, shardsB, backups);
            saver.PublishStepHook = step =>
            {
                if (seen++ == stop)
                {
                    interruptedAt = step;
                    throw new InterruptedPublish(step);
                }
            };
            bool ok = saver.Save(Populated(4, 2, "B"), _dir);

            var loaded = Load();
            _out.WriteLine($"stop {stop} ({interruptedAt ?? "completed"}): {loaded} | {Listing()}");
            Assert.True(loaded == a || loaded == b,
                $"interrupted after '{interruptedAt}' the load produced {loaded}, neither generation A nor B");

            if (interruptedAt == null)
            {
                Assert.True(ok);
                Assert.Equal(b, loaded);
                break;
            }
            Assert.False(ok);
            steps++;
            Assert.True(stop < 200, "publish never completed");
        }
        Assert.True(steps >= 3, $"expected several publish steps, saw {steps}");
    }

    // ---- S05: a failed backup is reported and never loaded as a generation ---------

    private sealed class Capture : ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add((level, formatter(state, ex)));
            }
        }
    }

    [Fact]
    public void AFailedBackupCopyIsReportedAndNeverLoadedAsAGeneration()
    {
        var capture = new Capture();
        var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        WorldSaver NewSaver() => new(lf) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 1 };

        Assert.True(NewSaver().Save(Populated(3, 1, "A"), _dir));
        // A directory where the world backup must go makes the copy fail.
        Directory.CreateDirectory(Path.Combine(_dir, "sphereworld.scp.bak1"));

        var saver = NewSaver();
        bool ok = saver.Save(Populated(4, 2, "B"), _dir);
        _out.WriteLine($"save returned {ok}, backup error: {saver.LastBackupError ?? "<none>"} | {Listing()}");

        // The world itself was written...
        Assert.True(ok);
        Assert.Equal(new Loaded(4, 2, "B"), Load());
        // ...but the backup was not, and the save says so.
        Assert.NotNull(saver.LastBackupError);
        Assert.Contains("sphereworld.scp", saver.LastBackupError);
        Assert.Contains(capture.Entries, e => e.Level >= LogLevel.Error && e.Text.Contains("sphereworld.scp"));

        // Losing the current generation must not boot the half backup (A's characters
        // without A's world).
        foreach (string f in Directory.GetFiles(_dir))
            if (!f.Contains(".bak", StringComparison.OrdinalIgnoreCase))
                File.Delete(f);
        var ex = Record.Exception(() => Load());
        if (ex == null)
        {
            var loaded = Load();
            _out.WriteLine($"after losing current -> {loaded}");
            Assert.Equal(new Loaded(0, 0, null), loaded);
        }
        else
        {
            Assert.IsType<InvalidDataException>(ex);
        }
    }

    [Fact]
    public void ASuccessfulSaveReportsNoBackupError()
    {
        var saver = Saver(SaveFormat.Text, 0, backups: 2);
        Assert.True(saver.Save(Populated(1, 1, "A"), _dir));
        Assert.True(saver.Save(Populated(1, 1, "B"), _dir));
        Assert.Null(saver.LastBackupError);
    }
}
