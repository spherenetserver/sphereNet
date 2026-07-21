using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 07 — binary save record atomicity (C6) and save-failure propagation (H5).
///
/// BinarySaveWriter.EndRecord used to write the section header and property count
/// BEFORE validating each property's length, so an oversized field threw
/// mid-record — leaving a truncated record whose promised count no longer matched
/// what followed, which misaligned every subsequent record and made the whole
/// shard unreadable (and then unbootable). And WorldSaver swallowed per-record
/// write errors and committed the shard anyway, silently losing the entity.
///
/// Now: EndRecord validates the entire record up front and writes atomically
/// (all-or-nothing), and a record failure fails the whole save — WritePrepared
/// cleans up the .tmp files, leaves the previous committed save untouched, and
/// returns false so no "save complete" is reported.
/// </summary>
public sealed class BinarySaveAtomicityTests
{
    private static List<(string Section, List<(string Key, string Value)> Props)> ReadAll(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinarySaveReader(ms);
        var recs = new List<(string, List<(string, string)>)>();
        while (r.NextRecord(out var section))
        {
            var props = new List<(string, string)>();
            while (r.NextProperty(out var k, out var v))
                props.Add((k, v));
            recs.Add((section, props));
        }
        return recs;
    }

    [Fact]
    public void EndRecord_ExactMaxValue_IsAccepted_AndRoundTrips()
    {
        using var ms = new MemoryStream();
        var w = new BinarySaveWriter(ms, ownsStream: false);
        string big = new('a', 65535); // exactly the ushort ceiling
        w.BeginRecord("WORLDITEM");
        w.WriteProperty("BIG", big);
        Assert.Null(Record.Exception(() => w.EndRecord()));
        w.Dispose();

        var recs = ReadAll(ms.ToArray());
        var rec = Assert.Single(recs);
        Assert.Equal("WORLDITEM", rec.Section);
        Assert.Equal("BIG", rec.Props[0].Key);
        Assert.Equal(65535, rec.Props[0].Value.Length);
    }

    [Fact]
    public void EndRecord_OversizedValue_Throws_WithoutWritingAnyByte()
    {
        using var ms = new MemoryStream();
        var w = new BinarySaveWriter(ms, ownsStream: false);

        w.BeginRecord("A");
        w.WriteProperty("K", "v");
        w.EndRecord();
        long afterGood = ms.Position;

        // The oversized record must throw and leave the stream exactly where the
        // previous good record ended — no partial section/count bytes.
        w.BeginRecord("B");
        w.WriteProperty("BIG", new string('a', 65536));
        Assert.IsType<InvalidDataException>(Record.Exception(() => w.EndRecord()));
        Assert.Equal(afterGood, ms.Position);

        // The writer recovers: a later record still produces a valid stream with
        // the good records present and the rejected one absent.
        w.BeginRecord("C");
        w.WriteProperty("K2", "v2");
        w.EndRecord();
        w.Dispose();

        var recs = ReadAll(ms.ToArray());
        Assert.Equal(2, recs.Count);
        Assert.Equal("A", recs[0].Section);
        Assert.Equal("C", recs[1].Section);
    }

    [Fact]
    public void EndRecord_ValueLimit_IsMeasuredInUtf8BytesNotChars()
    {
        // 'ç' (U+00E7) encodes to 2 UTF-8 bytes.
        using var over = new MemoryStream();
        var w1 = new BinarySaveWriter(over, ownsStream: false);
        w1.BeginRecord("A");
        w1.WriteProperty("K", new string('ç', 32768)); // 65536 bytes > limit
        Assert.IsType<InvalidDataException>(Record.Exception(() => w1.EndRecord()));

        using var ok = new MemoryStream();
        var w2 = new BinarySaveWriter(ok, ownsStream: false);
        w2.BeginRecord("A");
        w2.WriteProperty("K", new string('ç', 32767)); // 65534 bytes <= limit
        Assert.Null(Record.Exception(() => w2.EndRecord()));
    }

    // ---- WorldSaver: a failing record fails the save and keeps the prior one ----

    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Dictionary<string, byte[]> SnapshotCommitted(string dir) =>
        Directory.GetFiles(dir)
            .Where(f => !f.EndsWith(".tmp"))
            .ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes);

    [Fact]
    public void Save_RecordThatFailsToEncode_FailsSave_KeepsPreviousSave_NoTempLeft()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_atomicsave_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.Binary,
                ShardCount = 0,
            };
            var world = MakeWorld();
            var good = world.CreateItem();
            good.BaseId = 0x0EED;
            world.PlaceItem(good, new Point3D(1000, 1000, 0, 0));

            // First save commits a valid generation.
            Assert.True(saver.Save(world, dir));
            var committed = SnapshotCommitted(dir);
            Assert.NotEmpty(committed);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            // A second item whose NAME exceeds the 65535-byte binary field makes
            // its record fail to encode.
            var bad = world.CreateItem();
            bad.BaseId = 0x0EED;
            bad.Name = new string('x', 70000);
            world.PlaceItem(bad, new Point3D(1001, 1000, 0, 0));

            Assert.False(saver.Save(world, dir)); // save reports failure

            // Every previously committed file is byte-identical, and no half-written
            // .tmp was left behind.
            var after = SnapshotCommitted(dir);
            Assert.Equal(committed.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
            foreach (var kv in committed)
                Assert.Equal(kv.Value, after[kv.Key]);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
