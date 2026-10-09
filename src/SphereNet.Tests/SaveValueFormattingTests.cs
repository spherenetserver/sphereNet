using System.Globalization;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Variables;

namespace SphereNet.Tests;

/// <summary>
/// The save capture formats values into a buffer instead of building a string per
/// property (the strings were most of what the capture allocated, and the capture holds
/// the world still). Every buffer path here has a string path it replaced or still sits
/// beside, and the two must produce the same text: a save that changes shape because of
/// how it was formatted is a save that no longer loads the way it did.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SaveValueFormattingTests
{
    private static string Format(ISpanFormattable value)
    {
        Span<char> buffer = stackalloc char[64];
        Assert.True(value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture));
        return new string(buffer[..written]);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1234, 567, -5, 1)]
    [InlineData(-1, -32768, -128, 255)]
    [InlineData(32767, 32767, 127, 0)]
    public void APointFormatsIntoABufferAsItsToStringReads(short x, short y, sbyte z, byte map)
    {
        var p = new Point3D(x, y, z, map);
        Assert.Equal(p.ToString(), Format(p));
        Assert.Equal(p.ToString(), $"{p}");
    }

    /// <summary>A buffer that is too small is refused at every length short of the
    /// text, never overrun or half-reported as written.</summary>
    [Fact]
    public void APointRefusesEveryBufferShorterThanItsText()
    {
        var p = new Point3D(-1234, 32767, -128, 255);
        string expected = p.ToString();
        for (int size = 0; size < expected.Length; size++)
        {
            var buffer = new char[size];
            Assert.False(p.TryFormat(buffer, out int written, default, null), $"size {size}");
            Assert.Equal(0, written);
        }
        var exact = new char[expected.Length];
        Assert.True(p.TryFormat(exact, out int n, default, null));
        Assert.Equal(expected, new string(exact, 0, n));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANumberFormatsIntoABufferAsFormatNumberReads(bool decimalVariables)
    {
        bool saved = VarMap.DecimalVariables;
        VarMap.DecimalVariables = decimalVariables;
        try
        {
            long[] values = [0, 1, -1, 10, 255, int.MaxValue, int.MinValue, uint.MaxValue,
                (long)uint.MaxValue + 1, long.MaxValue, long.MinValue];
            Span<char> buffer = stackalloc char[24];
            foreach (long v in values)
            {
                Assert.True(VarMap.TryFormatNumber(v, buffer, out int written));
                Assert.Equal(VarMap.FormatNumber(v), new string(buffer[..written]));
            }
        }
        finally
        {
            VarMap.DecimalVariables = saved;
        }
    }

    /// <summary>The parts a writer formats a TAG line from rebuild exactly the text
    /// GetSaveText gives, for every way a value can be stored.</summary>
    [Fact]
    public void TheStoredPartsOfATagRebuildItsSaveText()
    {
        var map = new VarMap();
        map.Set("plaintext", "hello world");
        map.Set("numerictext", "123");
        map.SetStr("quoted", true, "say \"hi\"");
        map.SetStr("quotedempty", true, "");
        map.SetStr("scriptnumber", false, "0400abcd");
        map.SetStr("arith", false, "1+2");
        map.SetInt("setint", 42);
        map.SetInt("negative", -7);
        map.LoadValue("loadedliteral", "0A");
        map.LoadValue("loadedquoted", "\"x y\"");
        map.LoadValue("loadedarith", "2*3");

        var keys = new List<string>();
        map.CopyKeysTo(keys);
        Assert.Equal(map.Count, keys.Count);
        foreach (string key in keys)
        {
            string expected = map.GetSaveText(key)!;
            string rebuilt;
            if (map.TryGetStoredSaveText(key, out string text, out bool quoted))
                rebuilt = quoted ? "\"" + text + "\"" : text;
            else if (map.TryGetSaveNumber(key, out long number))
                rebuilt = VarMap.FormatNumber(number);
            else
                rebuilt = expected; // the computed fallback the writer still asks for
            Assert.Equal(expected, rebuilt);
        }
    }

    [Fact]
    public void TheValueBuilderFormatsAsInvariantInterpolationDoes()
    {
        uint uid = 0x4000123A;
        var guid = Guid.Parse("01a12267-06e1-7674-b861-c0b2950bd99f");
        var p = new Point3D(-5, 4000, -3, 1);
        string longText = new('x', 1000);

        Assert.Equal($"0{uid:x8}", Build($"0{uid:x8}"));
        Assert.Equal(guid.ToString("D"), Build($"{guid:D}"));
        Assert.Equal(p.ToString(), Build($"{p}"));
        Assert.Equal("-1,2,-3", Build($"{(short)-1},{(byte)2},{(sbyte)-3}"));
        Assert.Equal($"0{0xFFFFFFFFFFUL:x}", Build($"0{0xFFFFFFFFFFUL:x}"));
        Assert.Equal("\"" + longText + "\"", Build($"\"{longText}\""));
        // Many formatted values past the first buffer, so it has to grow mid-number.
        string many = string.Concat(Enumerable.Range(0, 200).Select(i => (i * 7919L).ToString(CultureInfo.InvariantCulture) + ","));
        Assert.Equal(many, BuildMany(200));
    }

    private sealed class CapturingWriter : ISaveWriter
    {
        public string? Last;
        public long WrittenBytes => 0;
        public void BeginRecord(string section) { }
        public void WriteProperty(string key, string value) => Last = value;
        public void EndRecord() { }
        public void WriteHeaderComment(string line) { }
        public void Flush() { }
        public void Dispose() { }
    }

    private static string Build(ref SaveValueBuilder value)
    {
        ISaveWriter w = new CapturingWriter();
        w.WriteProperty("K", ref value);
        return ((CapturingWriter)w).Last!;
    }

    private static string Build(string _) => throw new InvalidOperationException("handler overload not chosen");

    private static string BuildMany(int count)
    {
        var builder = new SaveValueBuilder(0, count);
        for (int i = 0; i < count; i++)
        {
            builder.AppendFormatted(i * 7919L);
            builder.AppendLiteral(",");
        }
        ISaveWriter w = new CapturingWriter();
        w.WriteProperty("K", ref builder);
        return ((CapturingWriter)w).Last!;
    }

    /// <summary>One record writer per path: the packed snapshot writer formats into its
    /// buffer, a plain writer receives strings through the interface defaults. Both are
    /// fed by the same record code, so their properties must read the same.</summary>
    private sealed class StringRecordWriter : ISaveWriter
    {
        public readonly List<(string Key, string Value)> Props = [];
        public string Section = "";
        public long WrittenBytes => 0;
        public void BeginRecord(string section) { Section = section; Props.Clear(); }
        public void WriteProperty(string key, string value) => Props.Add((key, value));
        public void EndRecord() { }
        public void WriteHeaderComment(string line) { }
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public void ThePackedCaptureAndTheStringPathWriteTheSameRecords()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.Amount = 3;
        pack.Hue = new Color(0x0481);
        pack.MoreP = new Point3D(-10, 20, -5, 1);
        pack.SetTag("plain", "hello");
        pack.Tags.SetStr("quoted", true, "a \"b\"");
        pack.Tags.SetInt("counter", -42);
        pack.Tags.LoadValue("literal", "0400abcd");
        pack.SetTag("SPELL_CASTING", "1"); // ephemeral: stripped, never written
        world.PlaceItem(pack, new Point3D(1000, 2000, 5, 0));

        var ch = world.CreateCharacter();
        ch.BodyId = 0x190;
        ch.Name = "Ayşe";
        ch.Tags.SetInt("score", 1234);
        ch.SetTag("note", "ünlü şövalye");
        ch.SetStatLock(1, 2);
        world.PlaceCharacter(ch, new Point3D(-1, 300, -20, 2));

        var saver = new WorldSaver(LoggerFactory.Create(_ => { }));
        long now = Environment.TickCount64;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var writeItem = typeof(WorldSaver).GetMethod("WriteItem", flags)!
            .CreateDelegate<Action<WorldSaver, ISaveWriter, Item, long>>();
        var writeChar = typeof(WorldSaver).GetMethod("WriteChar", flags)!
            .CreateDelegate<Action<WorldSaver, ISaveWriter, Character, long>>();

        var stringItem = new StringRecordWriter();
        writeItem(saver, stringItem, pack, now);
        var stringChar = new StringRecordWriter();
        writeChar(saver, stringChar, ch, now);
        var itemProps = stringItem.Props.ToList();
        var charProps = stringChar.Props.ToList();

        var prepared = saver.Prepare(world);
        var itemRecord = prepared.Snapshot.Items.Single(r => r.Uid == pack.Uid.Value);
        var charRecord = prepared.Snapshot.Characters.Single(r => r.Uid == ch.Uid.Value);

        Assert.Equal(stringItem.Section, itemRecord.Section);
        Assert.Equal(itemProps, itemRecord.Properties.ToList());
        Assert.Equal(stringChar.Section, charRecord.Section);
        Assert.Equal(charProps, charRecord.Properties.ToList());

        Assert.Contains(("TAG.quoted", "\"a \"b\"\""), itemProps);
        Assert.Contains(("TAG.literal", "0400abcd"), itemProps);
        Assert.Contains(("MOREP", "-10,20,-5,1"), itemProps);
        Assert.DoesNotContain(itemProps, p => p.Key.Contains("SPELL_CASTING"));
        Assert.False(pack.TryGetTag("SPELL_CASTING", out _));
        Assert.Contains(("NAME", "Ayşe"), charProps);
        Assert.Contains(("StatLock[1]", "2"), charProps);
    }
}
