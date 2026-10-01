using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Variables;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A TAG keeps its value, its type (Source-X number var or string var) and its
/// exact text across a save and a reload in every save format, and a legacy
/// Source-X TAG line loads the way CObjBase::r_LoadVal -> CVarDefMap::SetStr reads
/// it (CObjBase.cpp:1788, CVarDefMap.cpp:467). The text writer refuses a line its
/// own reader would reject instead of publishing a save that cannot load.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TagSaveFormatRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_tagrt_{Guid.NewGuid():N}");
    private readonly ILoggerFactory _lf = LoggerFactory.Create(_ => { });

    /// <summary>TextSaveReader.MaxLineLength: the longest physical line it reads.</summary>
    private const int MaxLine = 8 * 1024 * 1024;

    public TagSaveFormatRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Item PlaceItem(GameWorld world)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(1000, 1000, 0, 0));
        return item;
    }

    private Item SaveAndReload(GameWorld world, Item item, SaveFormat format, string sub)
    {
        string dir = Path.Combine(_dir, sub);
        var saver = new WorldSaver(_lf) { Format = format, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));
        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, dir);
        return dst.FindItem(item.Uid)!;
    }

    public static TheoryData<SaveFormat> AllFormats => new()
    {
        SaveFormat.Text, SaveFormat.TextGz, SaveFormat.Binary, SaveFormat.BinaryGz,
    };

    // ---- S09: the number / string distinction ------------------------------

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void NumberAndStringTagsKeepTheirTypeInEveryFormat(SaveFormat format)
    {
        var world = MakeWorld();
        var item = PlaceItem(world);
        item.Tags.SetInt("INT42", 42);
        item.Tags.SetInt("INTNEG", -7);
        item.Tags.SetInt("INTZERO", 0);
        item.Tags.SetStr("STR42", quoted: true, "42");
        item.Tags.SetStr("STRZERO", quoted: true, "0");
        item.Tags.SetStr("EMPTY", quoted: true, "");
        item.Tags.SetStr("HEX", quoted: false, "0A");    // a script's TAG.HEX=0A
        item.Tags.Set("QUOTES", "say \"hi\" now");
        item.Tags.Set("LEADQ", "\"lead");
        item.Tags.Set("TRAILQ", "trail\"");
        item.Tags.Set("ONLYQ", "\"");

        var loaded = SaveAndReload(world, item, format, $"type_{format}");

        Assert.True(loaded.Tags.IsInteger("INT42"));
        Assert.Equal(42, loaded.Tags.GetInt("INT42"));
        Assert.True(loaded.Tags.IsInteger("INTNEG"));
        Assert.Equal(-7, (int)loaded.Tags.GetInt("INTNEG"));
        Assert.True(loaded.Tags.Has("INTZERO"));
        Assert.True(loaded.Tags.IsInteger("INTZERO"));
        Assert.Equal(0, loaded.Tags.GetInt("INTZERO", -1));

        Assert.False(loaded.Tags.IsInteger("STR42"));
        Assert.Equal("42", loaded.Tags.Get("STR42"));
        Assert.Equal(VarSaveForm.Quoted, loaded.Tags.GetSaveForm("STR42"));
        Assert.Equal("0", loaded.Tags.Get("STRZERO"));
        Assert.Equal(VarSaveForm.Quoted, loaded.Tags.GetSaveForm("STRZERO"));
        Assert.True(loaded.Tags.Has("EMPTY"));
        Assert.Equal("", loaded.Tags.Get("EMPTY"));

        Assert.True(loaded.Tags.IsInteger("HEX"));
        Assert.Equal(10, loaded.Tags.GetInt("HEX"));

        Assert.Equal("say \"hi\" now", loaded.Tags.Get("QUOTES"));
        Assert.Equal("\"lead", loaded.Tags.Get("LEADQ"));
        Assert.Equal("trail\"", loaded.Tags.Get("TRAILQ"));
        Assert.Equal("\"", loaded.Tags.Get("ONLYQ"));

        // Every value goes back out as it came in.
        foreach (var entry in item.Tags.GetAllEntries())
            Assert.Equal(item.Tags.GetSaveText(entry.Key), loaded.Tags.GetSaveText(entry.Key));
    }

    [Fact]
    public void AScriptNumberTagIsReadAsANumberByEngineCode()
    {
        var map = new VarMap();
        map.SetStr("HEX", quoted: false, "0A");
        Assert.Equal(10, map.GetInt("HEX"));
        Assert.Equal("0A", map.Get("HEX"));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void ALegacySourceXTagLineLoadsAsSetStrReadsIt(SaveFormat format)
    {
        string src = Path.Combine(_dir, $"legacy_{format}");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "sphereworld.scp"),
            "[WORLDITEM 0eed]\r\nSERIAL=040000301\r\nP=1000,1000,0\r\n" +
            "TAG.QUOTED=\"hello\"\r\n" +
            "TAG.NUMERIC=42\r\n" +
            "TAG.HEXNUM=0400ABCD\r\n" +
            "TAG.QNUM=\"42\"\r\n" +
            "TAG.ZERO=0\r\n" +
            "TAG0.ZERODROP=0\r\n" +
            "TAG0.KEPT=05\r\n" +
            "TAG.BLANK=\r\n" +
            "TAG.QBLANK=\"\"\r\n" +
            "TAG.LOOSE=   spaced  value   \r\n" +
            "TAG.QSPACE=\"  inner  \"\r\n" +
            "\r\n[EOF]\r\n");

        var world = MakeWorld();
        new WorldLoader(_lf).Load(world, src);
        var item = world.GetAllObjects().OfType<Item>().Single();

        void Check(Item it)
        {
            Assert.Equal("hello", it.Tags.Get("QUOTED"));
            Assert.False(it.Tags.IsInteger("QUOTED"));
            Assert.True(it.Tags.IsInteger("NUMERIC"));
            Assert.Equal(42, it.Tags.GetInt("NUMERIC"));
            Assert.True(it.Tags.IsInteger("HEXNUM"));
            Assert.Equal(0x0400ABCD, it.Tags.GetInt("HEXNUM"));
            Assert.False(it.Tags.IsInteger("QNUM"));
            Assert.Equal("42", it.Tags.Get("QNUM"));
            Assert.True(it.Tags.Has("ZERO"));
            Assert.True(it.Tags.IsInteger("ZERO"));
            Assert.False(it.Tags.Has("ZERODROP"));
            Assert.Equal(5, it.Tags.GetInt("KEPT"));
            Assert.False(it.Tags.Has("BLANK"));
            Assert.True(it.Tags.Has("QBLANK"));
            Assert.Equal("", it.Tags.Get("QBLANK"));
            Assert.Equal("spaced  value", it.Tags.Get("LOOSE")); // legacy whitespace tolerance
            Assert.Equal("  inner  ", it.Tags.Get("QSPACE"));
        }

        Check(item);
        // And the loaded state survives this engine's own save in the format.
        Check(SaveAndReload(world, item, format, $"legacy_out_{format}"));
    }

    // ---- S10: text is kept exactly ----------------------------------------

    public static TheoryData<SaveFormat, string> WhitespaceValues()
    {
        string[] values =
        [
            "  value  ",
            "   ",
            "\t",
            "a\tb\t",
            " lead",
            "trail ",
            "line1\r\nline2  ",
            "line1\n  ",
            "\n",
            "\u0001starts with the sentinel",
            "\u0001",
            "back\\slash\\n not a newline\\",
            "mixed \\n and\nreal  ",
            "\"  quoted inside  \"",
        ];
        var data = new TheoryData<SaveFormat, string>();
        foreach (var f in new[] { SaveFormat.Text, SaveFormat.TextGz, SaveFormat.Binary, SaveFormat.BinaryGz })
            foreach (string v in values)
                data.Add(f, v);
        return data;
    }

    [Theory]
    [MemberData(nameof(WhitespaceValues))]
    public void TagTextRoundTripsExactly(SaveFormat format, string value)
    {
        var world = MakeWorld();
        var item = PlaceItem(world);
        item.Tags.Set("AUTO", value);
        item.Tags.SetStr("QUOTED", quoted: true, value);

        var loaded = SaveAndReload(world, item, format, $"ws_{format}_{Guid.NewGuid():N}");

        Assert.Equal(value, loaded.Tags.Get("AUTO"));
        Assert.Equal(value, loaded.Tags.Get("QUOTED"));
        Assert.Equal(VarSaveForm.Quoted, loaded.Tags.GetSaveForm("QUOTED"));
    }

    [Theory]
    [InlineData("line1\r\nline2  ")]
    [InlineData("trailing tab\n\t")]
    [InlineData("\u0001  ")]
    public void AMultiLinePropertyKeepsItsTrailingWhitespace(string value)
    {
        using var ms = new MemoryStream();
        using (var w = new TextSaveWriter(ms, ownsStream: false))
        {
            w.BeginRecord("WORLDITEM");
            w.WriteProperty("NAME", value);
        }
        ms.Position = 0;
        using var r = new TextSaveReader(ms);
        Assert.True(r.NextRecord(out _));
        Assert.True(r.NextProperty(out string key, out string read));
        Assert.Equal("NAME", key);
        Assert.Equal(value, read);
    }

    // ---- S08: the writer keeps the reader's line limit -----------------------

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    public void ALineAtTheReaderLimitSavesAndReloads(SaveFormat format)
    {
        // "NAME=" + sentinel + k escaped newlines (2 chars each) + filler, exactly
        // the reader's limit once encoded although the raw name is far shorter.
        const int newlines = 1000;
        int filler = MaxLine - "NAME=".Length - 1 - (2 * newlines);
        string name = new string('\n', newlines) + new string('a', filler);

        var world = MakeWorld();
        var item = PlaceItem(world);
        item.Name = name;
        var loaded = SaveAndReload(world, item, format, $"limit_ok_{format}");
        Assert.Equal(name, loaded.Name);
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    public void ALineOverTheReaderLimitFailsTheSave(SaveFormat format)
    {
        const int newlines = 1000;
        int filler = MaxLine - "NAME=".Length - 1 - (2 * newlines) + 1;
        string name = new string('\n', newlines) + new string('a', filler);
        Assert.True(name.Length < MaxLine); // only the escapes push it over

        var world = MakeWorld();
        var item = PlaceItem(world);
        item.Name = "first";
        string dir = Path.Combine(_dir, $"limit_over_{format}");
        var saver = new WorldSaver(_lf) { Format = format, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));

        item.Name = name;
        Assert.False(saver.Save(world, dir));

        // The previous generation is still the one on disk, and it loads.
        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, dir);
        Assert.Equal("first", dst.FindItem(item.Uid)!.Name);
    }

    [Fact]
    public void TheWriterRejectsAnOverlongLineBeforeWritingIt()
    {
        using var ms = new MemoryStream();
        using var w = new TextSaveWriter(ms, ownsStream: false);
        w.BeginRecord("WORLDITEM");
        string atLimit = new string('x', MaxLine - "K=".Length);
        w.WriteProperty("K", atLimit);
        Assert.Throws<InvalidDataException>(() => w.WriteProperty("K", atLimit + "x"));
        // A line break costs the sentinel and a two-char escape: one char shorter
        // raw than the line above, two longer once encoded.
        Assert.Throws<InvalidDataException>(() => w.WriteProperty("K", "\n" + atLimit[2..]));
        // A backslash alone is written verbatim, so the same length still fits.
        w.WriteProperty("K", "\\" + atLimit[1..]);
    }
}
