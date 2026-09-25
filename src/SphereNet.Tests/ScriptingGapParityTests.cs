using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Script-surface pieces that answered nothing or were refused: the STRTOKEN /
/// STRRANDRANGE / STRFIRSTCAP / LISTCOL string functions (CScriptObj_functions.tbl),
/// the BREATH / BONUSSKILL / MODAC / RARITY / SELFREPAIR / DUPEITEM / RESDISPDNHUE
/// keys, and the GARBAGE / SHRINKMEM / CALCCRYPT server verbs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptingGapParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_gap_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private void LoadDefs(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllLines(file, lines);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    // ---------------- STRTOKEN ----------------

    [Theory]
    [InlineData("<STRTOKEN \"a,b,c\",2,\",\">", "b")]
    [InlineData("<STRTOKEN \"a,b,c\",0,\",\">", "3")]
    [InlineData("<STRTOKEN \"a,b,c,d\",2-3,\",\">", "b,c")]
    [InlineData("<STRTOKEN \"a,b,c,d\",2-0,\",\">", "b,c,d")]   // end 0 = to the end
    [InlineData("<STRTOKEN \"a,b,c,d\",3-9,\",\">", "c,d")]     // end past count = to the end
    [InlineData("<STRTOKEN \"a b c\",3,\" \">", "c")]
    [InlineData("<STRTOKEN \"a|b|c\",1,\"|x\">", "a")]         // only the first separator char
    [InlineData("<STRTOKEN \"a,b\",5,\",\">", "")]              // past the count: failed read
    public void StrToken_MatchesUpstream(string expr, string expected)
    {
        var parser = new ExpressionParser();
        Assert.Equal(expected, parser.ResolveAngleBrackets(expr));
    }

    [Fact]
    public void StrToken_TokenizerKeepsBracketsAndQuotesTogether()
    {
        // Str_ParseCmdsAdv does not split inside () or quotes.
        var parts = ExpressionParser.ParseCmdsAdv("f(a,b),\"x,y\",z", 255, ",");
        Assert.Equal(["f(a,b)", "\"x,y\"", "z"], parts);
    }

    // ---------------- STRRANDRANGE ----------------

    [Fact]
    public void StrRandRange_PicksOnlyWeightedValues()
    {
        var parser = new ExpressionParser();
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
            seen.Add(parser.ResolveAngleBrackets("<STRRANDRANGE sword 1 axe 1 bow 0>"));
        Assert.Subset(new HashSet<string> { "sword", "axe" }, seen);
        Assert.DoesNotContain("bow", seen);
    }

    [Fact]
    public void StrRandRange_KeepsQuotesAndRejectsBadLists()
    {
        var parser = new ExpressionParser();
        Assert.Equal("\"weapon_sword\"",
            parser.ResolveAngleBrackets("<STRRANDRANGE \"weapon_sword\" 1 \"armor_leather\" 0>"));
        Assert.Equal("\"weapon_sword\"", parser.StrRandRange("\"weapon_sword\" 5"));
        // Odd element count and non-numeric weights are invalid (CExpression.cpp:2102/2122).
        Assert.Equal("", parser.StrRandRange("a 1 b"));
        Assert.Equal("", parser.StrRandRange("a x b y"));
        // A lone value is copied one character short (CExpression.cpp:2098).
        Assert.Equal("ab", parser.StrRandRange("abc"));
    }

    // ---------------- STRFIRSTCAP / LISTCOL ----------------

    [Fact]
    public void StrFirstCap_ResolvesEmptyLikeUpstream()
    {
        // In the table but handled by no case: StringFunction leaves the result empty.
        var parser = new ExpressionParser();
        Assert.Equal("[]", parser.ResolveAngleBrackets("[<STRFIRSTCAP hello>]"));
    }

    [Fact]
    public void ListCol_AlternatesWithTheWebListIndex()
    {
        var parser = new ExpressionParser();
        ExpressionParser.WebListIndex = 0;
        Assert.Equal("", parser.ResolveAngleBrackets("<LISTCOL>"));
        ExpressionParser.WebListIndex = 1;
        Assert.Equal("bgcolor=\"#E8E8E8\"", parser.ResolveAngleBrackets("<LISTCOL>"));
        ExpressionParser.WebListIndex = 2;
        Assert.Equal("", parser.ResolveAngleBrackets("<LISTCOL>"));
    }

    // ---------------- BREATH ----------------

    [Fact]
    public void Breath_DottedKeysStoreAndReadBack()
    {
        var world = MakeWorld();
        var npc = world.CreateCharacter();

        Assert.False(npc.TrySetProperty("BREATH", "1"));        // bare BREATH is refused
        Assert.True(npc.TrySetProperty("BREATH.HUE", "0480"));
        Assert.True(npc.TrySetProperty("BREATH.DAM", "25"));
        Assert.True(npc.TrySetProperty("BREATH.MAXDIST", "6"));

        Assert.True(npc.TryGetProperty("BREATH.HUE", out string hue));
        Assert.Equal("0480", hue);                               // hex read-back
        Assert.True(npc.TryGetProperty("BREATH.DAM", out string dam));
        Assert.Equal("25", dam);                                 // decimal read-back
        Assert.True(npc.TryGetProperty("BREATH.ANIM", out string anim));
        Assert.Equal("00", anim);                                // unset reads 0

        var look = SphereNet.Game.AI.NpcAI.ResolveBreath(npc);
        Assert.Equal(0x480, look.Hue);
    }

    [Fact]
    public void Breath_ZeroDamageFallsBackToTheStrengthDefault()
    {
        var world = MakeWorld();
        var npc = world.CreateCharacter();
        npc.Str = 200;
        npc.TrySetProperty("BREATH.DAM", "0");
        var m = typeof(SphereNet.Game.AI.NpcAI).GetMethod("GetBreathDamage",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(200 * 5 / 100, (int)m.Invoke(null, [npc])!);
        npc.TrySetProperty("BREATH.DAM", "40");
        Assert.Equal(40, (int)m.Invoke(null, [npc])!);
    }

    // ---------------- MODAC / MODAR ----------------

    [Fact]
    public void ModAc_IsTheModArAliasOnCharacters()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TrySetProperty("MODAC", "7"));
        Assert.Equal(7, ch.ModAr);
        Assert.True(ch.TryGetProperty("MODAR", out string v));
        Assert.Equal("7", v);
    }

    [Fact]
    public void ItemModAr_ShiftsArmorAndWeaponRatings()
    {
        var world = MakeWorld();
        var armor = world.CreateItem();
        armor.TrySetProperty("ARMOR", "10");
        int baseDef = armor.GetArmorDefense();
        Assert.True(armor.TrySetProperty("MODAC", "5"));
        Assert.Equal(baseDef + 5, armor.GetArmorDefense());
        armor.TrySetProperty("MODAR", "-999");
        Assert.Equal(0, armor.GetArmorDefense());               // floored at 0

        var attacker = world.CreateCharacter();
        attacker.Str = 0;
        var weapon = world.CreateItem();
        weapon.TrySetProperty("DAM", "5,10");
        var before = CombatEngine.CalcWeaponDamage(attacker, weapon);
        weapon.TrySetProperty("MODAR", "3");
        var after = CombatEngine.CalcWeaponDamage(attacker, weapon);
        Assert.Equal(before.Min + 3, after.Min);
        Assert.Equal(before.Max + 3, after.Max);
        Assert.True(weapon.TryGetProperty("MODAC", out string mv));
        Assert.Equal("3", mv);
    }

    // ---------------- BONUSSKILL / RARITY / SELFREPAIR / DUPEITEM ----------------

    [Fact]
    public void ItemBaseDefKeys_ReadBackAndFallBackToTheDefinition()
    {
        LoadDefs("[ITEMDEF 01f03]", "DEFNAME=i_probe_robe", "TYPE=t_clothing",
            "BONUSSKILL2=Magery", "BONUSSKILL2AMT=50", "RARITY=4");
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x1F03;

        Assert.True(item.TryGetProperty("BONUSSKILL1", out string b1));
        Assert.Equal("", b1);
        Assert.True(item.TryGetProperty("BONUSSKILL1AMT", out string a1));
        Assert.Equal("0", a1);
        Assert.True(item.TryGetProperty("BONUSSKILL2", out string b2));
        Assert.Equal("Magery", b2);                              // from the ITEMDEF
        Assert.True(item.TryGetProperty("RARITY", out string r0));
        Assert.Equal("4", r0);

        Assert.True(item.TrySetProperty("BonusSkill1", "Archery"));
        Assert.True(item.TrySetProperty("BonusSkill1Amt", "10.0")); // 100, skill notation
        Assert.True(item.TrySetProperty("Rarity", "1"));
        Assert.True(item.TrySetProperty("SelfRepair", "3"));
        item.TryGetProperty("BONUSSKILL1", out b1);
        item.TryGetProperty("BONUSSKILL1AMT", out a1);
        item.TryGetProperty("RARITY", out string r1);
        item.TryGetProperty("SELFREPAIR", out string sr);
        Assert.Equal(("Archery", "100", "1", "3"), (b1, a1, r1, sr));

        // A zero is STORED on the instance (SetDefNum(..., false), CItem.cpp:3170), so it
        // overrides the definition rather than letting it show through again.
        item.TrySetProperty("RARITY", "0");
        item.TryGetProperty("RARITY", out string r2);
        Assert.Equal("0", r2);
    }

    [Fact]
    public void DupeItem_IsReadOnly()
    {
        var world = MakeWorld();
        var item = world.CreateItem();
        item.BaseId = 0x03C9;
        Assert.True(item.TryGetProperty("DUPEITEM", out string same));
        Assert.Equal("0", same);
        item.TrySetProperty("DISPID", "03BE");
        Assert.True(item.TryGetProperty("DUPEITEM", out string dupe));
        Assert.Equal("03C9", dupe);
        Assert.False(item.TrySetProperty("DUPEITEM", "03be"));
    }

    [Fact]
    public void ItemBaseDefKeysAndModAr_RoundTripThroughASave()
    {
        string dir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(dir);
        var lf = LoggerFactory.Create(_ => { });
        var src = MakeWorld();
        var item = src.CreateItem();
        item.BaseId = 0x0EED;
        src.PlaceItem(item, new Point3D(1001, 1000, 0, 0));
        item.TrySetProperty("BONUSSKILL3", "Fencing");
        item.TrySetProperty("BONUSSKILL3AMT", "150");
        item.TrySetProperty("SELFREPAIR", "2");
        item.TrySetProperty("MODAR", "4");
        Assert.Equal(4, item.CreateDupe(src).ModAr);

        Assert.True(new WorldSaver(lf).Save(src, dir));
        var dst = MakeWorld();
        new WorldLoader(lf).Load(dst, dir);
        var loaded = dst.FindItem(item.Uid)!;
        loaded.TryGetProperty("BONUSSKILL3", out string b3);
        loaded.TryGetProperty("BONUSSKILL3AMT", out string a3);
        loaded.TryGetProperty("SELFREPAIR", out string sr);
        Assert.Equal(("Fencing", "150", "2", 4), (b3, a3, sr, loaded.ModAr));
    }

    // ---------------- RESDISPDNHUE ----------------

    [Fact]
    public void ResDispDn_ReplacesBodyAndHueBelowTheResLevel()
    {
        LoadDefs("[CHARDEF 0101]", "DEFNAME=c_probe_beast", "NAME=beast",
            "RESLEVEL=3", "RESDISPDNID=0190", "RESDISPDNHUE=0482");
        var world = MakeWorld();
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);
        var account = accounts.CreateAccount("viewer", "pw")!;
        var client = TestHarness.CreateClient(lf, world, accounts, 7101);
        var viewer = world.CreateCharacter();
        viewer.IsPlayer = true;
        viewer.BodyId = 0x190;
        world.PlaceCharacter(viewer, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, viewer, account);

        var beast = world.CreateCharacter();
        beast.CharDefIndex = 0x101;
        beast.BodyId = 0x101;
        beast.Hue = new Color(0x0021);
        world.PlaceCharacter(beast, new Point3D(101, 100, 0, 0));

        var adjust = typeof(SphereNet.Game.Clients.GameClient).GetMethod("AdjustCharViewForViewer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        account.ResDisp = 2;
        var (body, hue) = ((ushort, Color))adjust.Invoke(client, [beast])!;
        Assert.Equal((ushort)0x190, body);
        Assert.Equal((ushort)0x0482, hue.Value);

        account.ResDisp = 3;
        (body, hue) = ((ushort, Color))adjust.Invoke(client, [beast])!;
        Assert.Equal((ushort)0x101, body);
        Assert.Equal((ushort)0x0021, hue.Value);
    }

    // ---------------- SERV.GARBAGE / SHRINKMEM / CALCCRYPT ----------------

    private static SphereNet.Server.Admin.AdminCommandProcessor MakeProcessor() =>
        new(TestHarness.CreateWorld(), new AccountManager(NullLoggerFactory.Instance),
            new SphereConfig(), () => 0, NullLoggerFactory.Instance);

    [Fact]
    public void Console_CalcCrypt_AcceptsCommaOrSpaceSeparatedArgs()
    {
        var lines = new List<string>();
        MakeProcessor().ProcessCommand("CALCCRYPT 7.0.20", lines.Add);
        MakeProcessor().ProcessCommand("CALCCRYPT 7.0.20 0", lines.Add);
        MakeProcessor().ProcessCommand("CALCCRYPT 7.0.20,0", lines.Add);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("7002000 0", lines[0]);
        Assert.Contains("// 7.0.20", lines[0]);
        Assert.Equal(lines[0], lines[1]);
        Assert.Equal(lines[0], lines[2]);
    }

    [Fact]
    public void Console_ShrinkMem_AnswersUpstreamsLine()
    {
        var lines = new List<string>();
        MakeProcessor().ProcessCommand("SHRINKMEM", lines.Add);
        Assert.Single(lines);
        Assert.Equal(OperatingSystem.IsWindows()
            ? "Memory shrinked succesfully."
            : "Command not avaible on *NIX os.", lines[0]);
    }

    [Fact]
    public void Garbage_IsRefusedWhileTheWorldIsSaving()
    {
        var field = typeof(SphereNet.Server.Program)
            .GetField("_backgroundSaveTask", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var pending = new TaskCompletionSource<bool>();
        try
        {
            field.SetValue(null, pending.Task);
            var lines = new List<string>();
            MakeProcessor().ProcessCommand("GARBAGE", lines.Add);
            Assert.Equal(["Not allowed during world save and/or resync pause"], lines);

            var resolve = typeof(SphereNet.Server.Program)
                .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal("0", (string?)resolve.Invoke(null, ["_GARBAGE=0"]));

            field.SetValue(null, null);
            lines.Clear();
            MakeProcessor().ProcessCommand("GARBAGE", lines.Add);
            Assert.Contains(lines, l => l.StartsWith("World sweep:"));
        }
        finally
        {
            pending.TrySetResult(true);
            field.SetValue(null, previous);
        }
    }

    [Fact]
    public void ScriptServVerbs_CarryTheCallerToTheHost()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var sent = new List<string>();
        stack.Interpreter.ServerPropertyResolver = p => { sent.Add(p); return "1"; };
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.TryGetProperty("UID", out string uid);

        stack.Interpreter.Execute(
            [new ScriptKey("SERV.GARBAGE", ""), new ScriptKey("SERV.SHRINKMEM", "")],
            ch, null, new TriggerArgs { Source = ch }, new ScriptScope());

        Assert.Contains($"_GARBAGE={uid}", sent);
        Assert.Contains($"_SHRINKMEM={uid}", sent);
    }
}
