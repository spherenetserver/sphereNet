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
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The save matrix the acceptance asks for, and the hole it was hiding (review work
/// item D01).
///
/// Four formats, several shard layouts, backups on and off: the question each cell
/// answers is the same one, which is whether a generation that is not whole can be
/// told apart from one that is. The interesting cell is a world shard replaced by
/// something unreadable. It parses as zero records and carries no save stamp, its
/// stamped siblings made the generation look internally consistent, and the world came
/// up EMPTY with a good backup sitting beside it — the same ending as the original B1
/// finding, reached by a different road.
///
/// A classic Sphere save is the case that keeps this honest: it stamps neither shard,
/// and it has to keep loading. So "some stamped, some not" is only a contradiction
/// among the world/char shards, where a save either stamps all of them or none.
/// </summary>
public sealed class SaveGenerationMatrixTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public SaveGenerationMatrixTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_genmatrix_{Guid.NewGuid():N}");
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
            w.PlaceItem(it, new Point3D((short)(1001 + i % 50), (short)(1000 + i / 50), 0, 0));
        }
        return w;
    }

    private static WorldSaver Saver(SaveFormat fmt, int shards, int backups) =>
        new(LoggerFactory.Create(_ => { }))
        { Format = fmt, ShardCount = shards, BackupLevels = backups };

    private (int Items, int Chars) Load()
    {
        var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
        return loader.Load(World(), _dir);
    }

    private string[] LiveFiles(string prefix) =>
        Directory.GetFiles(_dir, prefix + "*")
            .Where(f => !f.Contains(".bak", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // ---- the matrix ------------------------------------------------------

    [Theory]
    [InlineData(SaveFormat.Text, 0)]
    [InlineData(SaveFormat.Text, 2)]
    [InlineData(SaveFormat.TextGz, 1)]
    [InlineData(SaveFormat.TextGz, 4)]
    [InlineData(SaveFormat.Binary, 0)]
    [InlineData(SaveFormat.Binary, 2)]
    [InlineData(SaveFormat.BinaryGz, 1)]
    [InlineData(SaveFormat.BinaryGz, 4)]
    public void EveryFormatAndShardLayoutRoundTripsWholeWithItsCharacter(SaveFormat fmt, int shards)
    {
        Assert.True(Saver(fmt, shards, backups: 1).Save(Populated(60), _dir));

        var (items, chars) = Load();
        _out.WriteLine($"{fmt} shards={shards}: {LiveFiles("sphereworld").Length} world file(s) " +
                       $"-> items={items} chars={chars}");

        Assert.Equal(60, items);
        Assert.Equal(1, chars);
    }

    [Theory]
    [InlineData(SaveFormat.Text, 2)]
    [InlineData(SaveFormat.BinaryGz, 2)]
    public void AShardReplacedByRubbishFallsBackInsteadOfLoadingAnEmptyWorld(SaveFormat fmt, int shards)
    {
        Assert.True(Saver(fmt, shards, backups: 2).Save(Populated(40), _dir));
        Assert.True(Saver(fmt, shards, backups: 2).Save(Populated(60), _dir));

        // One world shard is replaced by something that is not a save. It reads as zero
        // records and carries no stamp; its siblings still carry theirs.
        string victim = LiveFiles("sphereworld")[0];
        File.WriteAllText(victim, "this is not a save file\r\n");

        var (items, chars) = Load();
        _out.WriteLine($"{fmt} shards={shards}: after replacing {Path.GetFileName(victim)} " +
                       $"-> items={items} chars={chars}");

        // The previous generation, whole - not this one with a hole in it, and not an
        // empty world that the next save would write over the backups.
        Assert.Equal(40, items);
        Assert.Equal(1, chars);
    }

    [Fact]
    public void WithNoBackupToFallBackOnTheLoadRefusesRatherThanInventingAWorld()
    {
        Assert.True(Saver(SaveFormat.Text, shards: 2, backups: 0).Save(Populated(40), _dir));
        File.WriteAllText(LiveFiles("sphereworld")[0], "this is not a save file\r\n");

        var ex = Record.Exception(() => Load());
        _out.WriteLine($"no backup, one shard ruined: {ex?.GetType().Name ?? "accepted"}");

        // Booting blank here is the worst answer available: the next save writes that
        // emptiness over everything.
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void AClassicSaveWithNoStampsInEitherShardStillLoads()
    {
        // The compatibility guarantee the rule above must not break. A classic Sphere
        // save stamps neither shard; only spheredata carries a SAVECOUNT, which classic
        // saves write too - so "data stamped, shards not" is a legacy save, not a
        // mixture.
        Assert.True(Saver(SaveFormat.Text, shards: 1, backups: 0).Save(Populated(12), _dir));
        foreach (string f in LiveFiles("sphereworld").Concat(LiveFiles("spherechars")))
            File.WriteAllText(f, StripStamp(File.ReadAllText(f)));

        var (items, chars) = Load();
        _out.WriteLine($"classic-shaped save: items={items} chars={chars}");
        Assert.Equal(12, items);
        Assert.Equal(1, chars);
    }

    [Fact]
    public void BackupsOffStillLeavesTheLastGoodSaveReadable()
    {
        // BackupLevels=0 is a supported choice, not a broken one: nothing is retired,
        // so the only copy is the live one and it has to be whole.
        Assert.True(Saver(SaveFormat.Binary, shards: 0, backups: 0).Save(Populated(25), _dir));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak1"));

        var (items, chars) = Load();
        Assert.Equal(25, items);
        Assert.Equal(1, chars);
    }

    /// <summary>Remove the two-line [SAVEID] record, leaving a file shaped like a
    /// classic save.</summary>
    private static string StripStamp(string text)
    {
        var kept = new System.Collections.Generic.List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("[SAVEID]", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < lines.Length && lines[i + 1].Contains('='))
                    i++;
                continue;
            }
            kept.Add(lines[i]);
        }
        return string.Join("\r\n", kept);
    }
}
