using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Variables;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// TAG/VAR values are written the way Source-X CVarDefMap::r_WritePrefix writes them
/// (CVarDefMap.cpp:672): a number var bare, a string var inside quotes. Whether a
/// value is a number or a string is CVarDefMap::SetStr's decision (CVarDefMap.cpp:467):
/// a quoted value is always a string, an unquoted one is a number when
/// IsSimpleNumberString accepts it. A number goes out in Sphere hex
/// (CVarDefContNum::GetValStr). The load side strips the quote pair the way
/// GetArgStr does and remembers it, so a Source-X string var survives a load and a
/// save unchanged.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TagSaveQuotingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spn_tagq_{Guid.NewGuid():N}");
    private readonly ILoggerFactory _lf = LoggerFactory.Create(_ => { });

    public TagSaveQuotingTests() => Directory.CreateDirectory(_dir);

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

    private static void RunScript(IScriptObj target, params (string Key, string Arg)[] lines)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var body = lines.Select(l => new ScriptKey(l.Key, l.Arg)).ToList();
        stack.Interpreter.Execute(body, target, null, new TriggerArgs(), new ScriptScope());
    }

    private string SaveAndRead(GameWorld world, string sub)
    {
        string dir = Path.Combine(_dir, sub);
        var saver = new WorldSaver(_lf) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));
        // Items and characters go to separate files; read every one of them.
        return string.Concat(Directory.GetFiles(dir, "*.scp").Order().Select(File.ReadAllText));
    }

    private static string[] TagLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(l => l.StartsWith("TAG.", StringComparison.Ordinal))
            .ToArray();

    // ---- script assignments -------------------------------------------------

    [Fact]
    public void ScriptAssignmentsSaveAsSourceXWritesThem()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(1000, 1000, 0, 0));

        RunScript(item,
            ("TAG.Num", "5"),                // number var -> hex
            ("TAG.Ten", "10"),               // decimal 10 -> 0A
            ("TAG.Hex", "0A"),               // already Sphere hex
            ("TAG.Neg", "-1"),               // 32-bit two's complement
            ("TAG.Zero", "0"),               // zero is "00"
            ("TAG.QuotedNum", "\"5\""),      // quoted -> string var
            ("TAG.Text", "abc"),             // not a number -> string var
            ("TAG.Spaces", "hello world"),
            ("TAG.Commas", "1,2,3"),
            ("TAG.Inner", "\"a \"q\", b\""), // GetArgStr cuts at the LAST quote
            ("TAG.Empty", "\"\""),           // quoted empty -> empty string var
            ("TAG.Gone", "7"),
            ("TAG.Gone", ""),                // unquoted empty removes
            ("TAG0.Dropped", "0"));          // TAG0 deletes a zero number

        string[] lines = TagLines(SaveAndRead(world, "a"));

        Assert.Contains("TAG.Num=05", lines);
        Assert.Contains("TAG.Ten=0A", lines);
        Assert.Contains("TAG.Hex=0A", lines);
        Assert.Contains("TAG.Neg=0FFFFFFFF", lines);
        Assert.Contains("TAG.Zero=00", lines);
        Assert.Contains("TAG.QuotedNum=\"5\"", lines);
        Assert.Contains("TAG.Text=\"abc\"", lines);
        Assert.Contains("TAG.Spaces=\"hello world\"", lines);
        Assert.Contains("TAG.Commas=\"1,2,3\"", lines);
        Assert.Contains("TAG.Inner=\"a \"q\", b\"", lines);
        Assert.Contains("TAG.Empty=\"\"", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("TAG.Gone", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("TAG.Dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void AScriptReadsTheTextItWroteUntilTheSave()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        RunScript(item, ("TAG.Num", "10"), ("TAG.QuotedNum", "\"5\""), ("TAG.Empty", "\"\""));

        Assert.True(item.TryGetProperty("TAG.Num", out string num));
        Assert.Equal("10", num);
        Assert.True(item.TryGetProperty("TAG.QuotedNum", out string q));
        Assert.Equal("5", q);
        Assert.True(item.Tags.Has("Empty"));
        Assert.Equal("", item.Tags.Get("Empty"));
    }

    // ---- engine-set values ----------------------------------------------------

    [Fact]
    public void EngineSetValuesKeepTheirTextAcrossASave()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        ch.SetTag("COUNT", "123");             // a plain number: reads back the same
        ch.SetTag("UIDREF", "04000123");       // Sphere hex text
        ch.SetTag("STAMP", "2026-10-01");      // expression-shaped text stays text
        ch.SetTag("LABEL", "a=b=c");

        string[] lines = TagLines(SaveAndRead(world, "b"));
        Assert.Contains("TAG.COUNT=123", lines);
        Assert.Contains("TAG.UIDREF=04000123", lines);
        Assert.Contains("TAG.STAMP=\"2026-10-01\"", lines);
        Assert.Contains("TAG.LABEL=\"a=b=c\"", lines);

        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, Path.Combine(_dir, "b"));
        var loaded = dst.FindChar(ch.Uid)!;
        Assert.Equal("123", loaded.Tags.Get("COUNT"));
        Assert.Equal("04000123", loaded.Tags.Get("UIDREF"));
        Assert.Equal("2026-10-01", loaded.Tags.Get("STAMP"));
        Assert.Equal("a=b=c", loaded.Tags.Get("LABEL"));
    }

    // ---- round trip -----------------------------------------------------------

    [Fact]
    public void ASavedWorldLoadsAndSavesBackIdentically()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(1000, 1000, 0, 0));
        RunScript(item,
            ("TAG.Num", "10"), ("TAG.QuotedNum", "\"5\""), ("TAG.Text", "hello world"),
            ("TAG.Empty", "\"\""), ("TAG.Inner", "\"x \"y\" z\""), ("TAG.Neg", "-5"));

        string[] first = TagLines(SaveAndRead(world, "c1"));

        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, Path.Combine(_dir, "c1"));
        var loaded = dst.FindItem(item.Uid)!;
        Assert.Equal("0A", loaded.Tags.Get("Num"));
        Assert.Equal("5", loaded.Tags.Get("QuotedNum"));
        Assert.Equal(VarSaveForm.Quoted, loaded.Tags.GetSaveForm("QuotedNum"));
        Assert.Equal("hello world", loaded.Tags.Get("Text"));
        Assert.Equal("", loaded.Tags.Get("Empty"));
        Assert.Equal("x \"y\" z", loaded.Tags.Get("Inner"));
        Assert.Equal("0FFFFFFFB", loaded.Tags.Get("Neg"));

        string[] second = TagLines(SaveAndRead(dst, "c2"));
        Assert.Equal(first, second);
    }

    [Fact]
    public void ASourceXWrittenRecordLoadsAndSavesBackUnchanged()
    {
        string src = Path.Combine(_dir, "sx");
        Directory.CreateDirectory(src);
        string[] tagLines =
        [
            "TAG.HexNum=0A",
            "TAG.BigHex=0400ABCD",
            "TAG.QuotedNum=\"5\"",
            "TAG.Name=\"Lord British\"",
            "TAG.Empty=\"\"",
            "TAG.Special=\"a, b; c=d\"",
            "TAG.Braces=\"{1 2}\"",
        ];
        File.WriteAllText(Path.Combine(src, "sphereworld.scp"),
            "[WORLDITEM 0eed]\r\nSERIAL=040000201\r\nP=1000,1000,0\r\n" +
            string.Join("\r\n", tagLines) + "\r\n\r\n[EOF]\r\n");

        var world = MakeWorld();
        new WorldLoader(_lf).Load(world, src);
        var item = world.GetAllObjects().OfType<Item>().Single();

        Assert.Equal("0A", item.Tags.Get("HexNum"));
        Assert.Equal("0400ABCD", item.Tags.Get("BigHex"));
        Assert.Equal("5", item.Tags.Get("QuotedNum"));
        Assert.Equal("Lord British", item.Tags.Get("Name"));
        Assert.Equal("", item.Tags.Get("Empty"));
        Assert.Equal("a, b; c=d", item.Tags.Get("Special"));
        Assert.Equal("{1 2}", item.Tags.Get("Braces")); // read as text, not rolled
        Assert.True(item.TryGetProperty("TAG.Name", out string name));
        Assert.Equal("Lord British", name);

        string[] written = TagLines(SaveAndRead(world, "sx_out"));
        foreach (string line in tagLines)
            Assert.Contains(line, written);
    }

    [Fact]
    public void AnOldUnquotedSphereNetSaveStillLoads()
    {
        string src = Path.Combine(_dir, "old");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "sphereworld.scp"),
            "[WORLDITEM]\r\nSERIAL=040000202\r\nID=0EED\r\nP=1000,1000,0\r\n" +
            "TAG.Count=123\r\nTAG.Label=hello world\r\nTAG.Formula=a=b=c\r\n\r\n[EOF]\r\n");

        var world = MakeWorld();
        new WorldLoader(_lf).Load(world, src);
        var item = world.GetAllObjects().OfType<Item>().Single();
        Assert.Equal("123", item.Tags.Get("Count"));
        Assert.Equal("hello world", item.Tags.Get("Label"));
        Assert.Equal("a=b=c", item.Tags.Get("Formula"));

        string[] written = TagLines(SaveAndRead(world, "old_out"));
        Assert.Contains("TAG.Count=123", written);
        Assert.Contains("TAG.Label=\"hello world\"", written);
        Assert.Contains("TAG.Formula=\"a=b=c\"", written);
    }

    [Fact]
    public void TheBinaryFormatCarriesTheSameDistinction()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(1000, 1000, 0, 0));
        RunScript(item, ("TAG.QuotedNum", "\"5\""), ("TAG.Empty", "\"\""), ("TAG.Text", "abc"));

        string dir = Path.Combine(_dir, "bin");
        var saver = new WorldSaver(_lf) { Format = SaveFormat.Binary, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));

        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, dir);
        var loaded = dst.FindItem(item.Uid)!;
        Assert.Equal("\"5\"", loaded.Tags.GetSaveText("QuotedNum"));
        Assert.Equal("\"\"", loaded.Tags.GetSaveText("Empty"));
        Assert.Equal("abc", loaded.Tags.Get("Text"));
    }

    // ---- server-wide VARs and LISTs ------------------------------------------

    [Fact]
    public void GlobalVarsAndListElementsAreQuotedWhenTheyAreStrings()
    {
        var world = MakeWorld();
        world.SetGlobalVar("V_NUM", "042");
        world.SetGlobalVar("V_TEXT", "some text");
        var list = world.GetOrCreateList("l_mixed");
        list.Add("07");
        list.Add("red apple");

        string dir = Path.Combine(_dir, "g");
        var saver = new WorldSaver(_lf) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));
        string dataFile = Directory.GetFiles(dir, "*.scp")
            .First(f => File.ReadAllText(f).Contains("[GLOBALS]"));
        string text = File.ReadAllText(dataFile);
        Assert.Contains("V_NUM=042", text);
        Assert.Contains("V_TEXT=\"some text\"", text);
        Assert.Contains("ELEM=07", text);
        Assert.Contains("ELEM=\"red apple\"", text);

        var dst = MakeWorld();
        new WorldLoader(_lf).Load(dst, dir);
        Assert.Equal("042", dst.GetGlobalVar("V_NUM"));
        Assert.Equal("some text", dst.GetGlobalVar("V_TEXT"));
        Assert.Equal(["07", "red apple"], dst.GetList("l_mixed")!);
    }

    // ---- accounts -------------------------------------------------------------

    [Fact]
    public void AccountTagsSaveAndLoadAsSourceXWritesThem()
    {
        var accounts = new AccountManager(_lf);
        var acc = accounts.CreateAccount("quoter", "pw")!;
        acc.TrySetProperty("TAG.Visits", "10");
        using (ScriptArgQuoting.Enter("5"))
            acc.TrySetProperty("TAG.Pin", "5");
        acc.TrySetProperty("TAG.Motto", "carpe diem");

        string dir = Path.Combine(_dir, "acc");
        Directory.CreateDirectory(dir);
        AccountPersistence.Save(accounts, dir, SaveFormat.Text);
        string text = File.ReadAllText(Path.Combine(dir, "sphereaccu.scp"));
        Assert.Contains("TAG.Visits=0A", text);
        Assert.Contains("TAG.Pin=\"5\"", text);
        Assert.Contains("TAG.Motto=\"carpe diem\"", text);

        var loaded = new AccountManager(_lf);
        Assert.Equal(1, AccountPersistence.Load(loaded, dir));
        var back = loaded.FindAccount("quoter")!;
        Assert.Equal("0A", back.Tags.Get("Visits"));
        Assert.Equal("5", back.Tags.Get("Pin"));
        Assert.Equal("carpe diem", back.Tags.Get("Motto"));
        Assert.Equal("\"5\"", back.Tags.GetSaveText("Pin"));
    }

    [Fact]
    public void ASourceXAccountFileLoadsItsTags()
    {
        string dir = Path.Combine(_dir, "sxacc");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sphereaccu.scp"),
            "[veteran]\r\nPLEVEL=1\r\nPASSWORD=pw\r\nTAG.LastSeen=\"2024/01/02 10:00:00\"\r\n" +
            "TAG.Credits=064\r\nTAG.Note=\"\"\r\n\r\n[EOF]\r\n");

        var accounts = new AccountManager(_lf);
        Assert.Equal(1, AccountPersistence.Load(accounts, dir));
        var acc = accounts.FindAccount("veteran")!;
        Assert.Equal("2024/01/02 10:00:00", acc.Tags.Get("LastSeen"));
        Assert.Equal("064", acc.Tags.Get("Credits"));
        Assert.Equal("", acc.Tags.Get("Note"));

        string outDir = Path.Combine(_dir, "sxacc_out");
        Directory.CreateDirectory(outDir);
        AccountPersistence.Save(accounts, outDir, SaveFormat.Text);
        string text = File.ReadAllText(Path.Combine(outDir, "sphereaccu.scp"));
        Assert.Contains("TAG.LastSeen=\"2024/01/02 10:00:00\"", text);
        Assert.Contains("TAG.Credits=064", text);
        Assert.Contains("TAG.Note=\"\"", text);
    }

    // ---- the decision itself --------------------------------------------------

    [Theory]
    [InlineData(false, "5", "05")]
    [InlineData(false, "1+2", "03")]          // IsSimpleNumberString: evaluated
    [InlineData(false, "1.5", "\"1.5\"")]     // '.' is not a math separator
    [InlineData(false, "0ab", "0AB")]
    [InlineData(false, "1 2 3", "\"1 2 3\"")]
    [InlineData(true, "5", "\"5\"")]
    [InlineData(true, "", "\"\"")]
    public void SetStrDecidesLikeSourceX(bool quoted, string value, string expected)
    {
        var map = new VarMap();
        map.SetStr("K", quoted, value);
        Assert.Equal(expected, map.GetSaveText("K"));
        Assert.Equal(value, map.Get("K"));
    }

    [Theory]
    [InlineData("\"abc\"", "abc", true)]
    [InlineData("\"a \"b\" c\"", "a \"b\" c", true)]
    [InlineData("\"open", "open", false)]   // no closing quote: not quoted
    [InlineData("plain", "plain", false)]
    [InlineData("\"\"", "", true)]
    public void SaveValuesUnquoteLikeGetArgStr(string raw, string text, bool quoted)
    {
        Assert.Equal(text, VarMap.UnquoteSaveValue(raw, out bool q));
        Assert.Equal(quoted, q);
    }
}
