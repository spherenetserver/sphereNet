using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Scripting.Expressions;

namespace SphereNet.Tests;

/// <summary>
/// Expression intrinsics and SERV read-back answered the way the upstream engine
/// answers them - the argument-count rules, number formats and edge cases of
/// CExpression::GetSingle's intrinsic table (CExpression.cpp:826), the float table
/// (CFloatMath.cpp:254) and CServerConfig/CServerDef::r_WriteVal.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServerExpressionReadbackParityTests
{
    private static long? Ask(string expr)
        => new ExpressionParser().TryEvaluate(expr, out long v) ? v : null;

    private static string Float(string call)
        => new ExpressionParser().EvaluateStr($"<FLOATVAL {call}>");

    // --- integer intrinsics ---

    /// <summary>INTRINSIC_MAX/MIN: fewer than two arguments is 0, not the argument.</summary>
    [Fact]
    public void MaxAndMinNeedTwoArguments()
    {
        Assert.Equal(0L, Ask("MAX(5)"));
        Assert.Equal(0L, Ask("MIN(5)"));
        Assert.Equal(9L, Ask("MAX(3,9)"));
    }

    /// <summary>INTRINSIC_SQRT: a negative argument is the real part of the complex
    /// root, 0.</summary>
    [Fact]
    public void SqrtOfANegativeIsZero() => Assert.Equal(0L, Ask("SQRT(-4)"));

    /// <summary>INTRINSIC_LOGARITHM reads the base with GetVal, so a leading zero is
    /// hex; "e" and "pi" name their bases; a base of 0 or less answers 0.</summary>
    [Fact]
    public void LogarithmBaseIsASphereNumber()
    {
        Assert.Equal(8L, Ask("LOGARITHM(256,2)"));
        Assert.Equal(3L, Ask("LOGARITHM(4096,010)"));  // base 16
        Assert.Equal(4L, Ask("LOGARITHM(100,e)"));     // ln 100 = 4.6
        Assert.Equal(0L, Ask("LOGARITHM(100,0)"));
        Assert.Equal(2L, Ask("LOGARITHM(100)"));
    }

    /// <summary>INTRINSIC_STRCMP/STRCMPI: one argument compares unequal (1); strcmpi
    /// folds to lower case, so '_' sorts before letters.</summary>
    [Fact]
    public void StrCmpArgumentRulesAndLowerCaseFold()
    {
        Assert.Equal(1L, Ask("STRCMP(abc)"));
        Assert.Equal(1L, Ask("STRCMPI(abc)"));
        Assert.Equal(-1L, Ask("STRCMP(a,b)"));
        Assert.Equal(1L, Ask("STRCMP(b,a)"));
        Assert.Equal(0L, Ask("STRCMPI(ABC,abc)"));
        Assert.Equal(-1L, Ask("STRCMPI(_,A)"));
    }

    /// <summary>Str_IndexOf never clamps: a negative offset, one past the end and an
    /// empty search are all -1; the offset is a Sphere number.</summary>
    [Fact]
    public void StrIndexOfFollowsStrIndexOf()
    {
        Assert.Equal(2L, Ask("STRINDEXOF(hello,l)"));
        Assert.Equal(-1L, Ask("STRINDEXOF(hello,l,-1)"));
        Assert.Equal(-1L, Ask("STRINDEXOF(hello,l,5)"));
        Assert.Equal(-1L, Ask("STRINDEXOF(hello,,0)"));
        Assert.Equal(4L, Ask("STRINDEXOF(hello,o,03)"));
        Assert.Equal(3L, Ask("STRINDEXOF(hello,l,1+2)"));
    }

    /// <summary>Str_Match: '[...]' sets and ranges, '!'/'^' inversion, case-blind,
    /// and an empty text matches only a pattern that is exactly "*".</summary>
    [Theory]
    [InlineData("[a-c]at", "bat", 1)]
    [InlineData("[a-c]at", "dat", 0)]
    [InlineData("[!a]bc", "xbc", 1)]
    [InlineData("[^x]bc", "xbc", 0)]
    [InlineData("H*O", "hello", 1)]
    [InlineData("h?llo", "hello", 1)]
    [InlineData("*", "", 1)]
    [InlineData("**", "", 0)]
    [InlineData("*[0-9]", "abc7", 1)]
    [InlineData("*[0-9]", "abc", 0)]
    public void StrMatchFollowsStrMatch(string pattern, string text, long want)
        => Assert.Equal(want, Ask($"STRMATCH({pattern},{text})"));

    /// <summary>The space form of ISNUMBER takes the intrinsic's test, not a strict
    /// decimal parse.</summary>
    [Fact]
    public void IsNumberSpaceFormFollowsTheIntrinsic()
    {
        var p = new ExpressionParser();
        Assert.Equal("1", p.EvaluateStr("<ISNUMBER 0ff>"));
        Assert.Equal("1", p.EvaluateStr("<ISNUMBER abc12>"));
        Assert.Equal("0", p.EvaluateStr("<ISNUMBER abc>"));
    }

    /// <summary>INTRINSIC_QVAL: fewer than three arguments is 0.</summary>
    [Fact]
    public void QvalNeedsThreeArguments() => Assert.Equal(0L, Ask("QVAL(1,2)"));

    // --- float intrinsics ---

    /// <summary>The float evaluator's trigonometry is in degrees (CFloatMath.cpp);
    /// FLOATVAL prints six decimals.</summary>
    [Fact]
    public void FloatTrigonometryIsInDegrees()
    {
        Assert.Equal("1.000000", Float("SIN(90)"));
        Assert.Equal("1.000000", Float("COS(0)"));
        Assert.Equal("45.000000", Float("ARCTAN(1)"));
        Assert.Equal("90.000000", Float("ARCSIN(1)"));
    }

    [Fact]
    public void FloatEdgeCasesFollowUpstream()
    {
        Assert.Equal("0.000000", Float("SQRT(-4)"));
        Assert.Equal("0.000000", Float("MAX(3)"));
        Assert.Equal("3.000000", Float("LOGARITHM(8,2)"));
        Assert.StartsWith("4.6", Float("LOGARITHM(100,e)"));
        Assert.Equal("0.000000", Float("LOGARITHM(100,0)"));
    }

    // --- SERV read-back ---

    private static string Serv(string property)
    {
        var method = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [property]) ?? "";
    }

    private static void WithConfig(SphereConfig cfg, Action body)
    {
        var field = typeof(SphereNet.Server.Program)
            .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        field.SetValue(null, cfg);
        try { body(); }
        finally { field.SetValue(null, previous); }
    }

    /// <summary>The CServerConfig table keys that had a setting here but no read-back.</summary>
    [Fact]
    public void ConfigTableKeysReadBack()
    {
        var cfg = new SphereConfig
        {
            MaxFame = 12000, LightNight = 20, VendorMarkup = 25, ReagentsRequired = false,
            EventsPlayer = "e_player", SavePeriodMinutes = 20, NpcAi = 0x0C41,
            HitpointPercentOnRez = 30, SpeedScaleFactor = 80000, TrainSkillPercent = 40,
        };
        WithConfig(cfg, () =>
        {
            Assert.Equal("12000", Serv("MAXFAME"));
            Assert.Equal("20", Serv("LIGHTNIGHT"));
            Assert.Equal("25", Serv("VENDORMARKUP"));
            Assert.Equal("0", Serv("REAGENTSREQUIRED"));
            Assert.Equal("e_player", Serv("EVENTSPLAYER"));
            Assert.Equal("20", Serv("SAVEPERIOD"));
            Assert.Equal("3137", Serv("NPCAI"));
            Assert.Equal("30", Serv("HITPOINTPERCENTONREZ"));
            Assert.Equal("80000", Serv("SPEEDSCALEFACTOR"));
            Assert.Equal("40", Serv("NPCTRAINPERCENT"));
        });
    }

    /// <summary>The keys whose upstream read-back differs from the ini value.</summary>
    [Fact]
    public void ConfigKeysWithUpstreamConversions()
    {
        var cfg = new SphereConfig
        {
            MurderDecayTime = 28800, WalkBuffer = 75, ColorHidden = 0x3E9,
            OptionFlags = 0x10, Experimental = 0, ChatFlags = 0x10, CommandPrefix = ".",
            ClientLoginTempBanMinutes = 3, ServerTickMs = 100, AccApp = 2,
            ServName = "Shard", Url = "shard.example",
        };
        WithConfig(cfg, () =>
        {
            Assert.Equal("480", Serv("MURDERDECAYTIME"));     // seconds in, minutes out
            Assert.Equal("7500", Serv("WALKBUFFER"));         // tenths in, ms out
            Assert.Equal("03E9", Serv("COLORHIDDEN"));        // FormatHex
            Assert.Equal("010", Serv("OPTIONFLAGS"));
            Assert.Equal("00", Serv("EXPERIMENTAL"));
            Assert.Equal("010", Serv("CHATFLAGS"));
            Assert.Equal("46", Serv("COMMANDPREFIX"));        // ELEM_BYTE
            Assert.Equal("180000", Serv("CLIENTLOGINTEMPBAN"));
            Assert.Equal("10", Serv("TICKPERIOD"));           // TICKS_PER_SEC
            Assert.Equal("2", Serv("ACCAPP"));
            Assert.Equal("FREE", Serv("ACCAPPS"));
            Assert.Equal("<a href=\"https://shard.example\">Shard</a>", Serv("URLLINK"));
        });
    }

    private static void WithWorld(SphereNet.Game.World.GameWorld world, Action body)
    {
        var field = typeof(SphereNet.Server.Program)
            .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        field.SetValue(null, world);
        try { body(); }
        finally { field.SetValue(null, previous); }
    }

    /// <summary>LASTNEWITEM is an object reference (CWorld::r_GetRef): the uid in hex,
    /// 0 when there is none, and a trailing key read off the object.</summary>
    [Fact]
    public void LastNewItemIsAReference()
    {
        var world = TestHarness.CreateWorld();
        WithWorld(world, () =>
        {
            Assert.Equal("0", Serv("LASTNEWITEM"));
            var item = world.CreateItem();
            item.Name = "probe";
            world.LastNewItem = item.Uid;
            Assert.Equal($"0{item.Uid.Value:X}", Serv("LASTNEWITEM"));
            Assert.Equal("probe", Serv("LASTNEWITEM.NAME"));
        });
    }

    /// <summary>SERV.ROOM(&lt;name&gt;).&lt;key&gt; and SERV.ROOM.&lt;name&gt;.&lt;key&gt; reach
    /// the room resource; a bare reference answers 1.</summary>
    [Fact]
    public void RoomReferenceFormsReachTheRoom()
    {
        var world = TestHarness.CreateWorld();
        var room = new SphereNet.Game.World.Regions.Room { Name = "Throne Room", MapIndex = 1 };
        world.AddRoom(room);
        WithWorld(world, () =>
        {
            Assert.Equal("1", Serv("ROOM(Throne Room).MAP"));
            Assert.Equal("Throne Room", Serv("ROOM.Throne Room.NAME"));
            Assert.Equal("1", Serv("ROOM(Throne Room)"));
            Assert.Equal("0", Serv("ROOM(Nowhere).NAME"));
        });
    }

    /// <summary>SERV.MULTIS.COUNT counts houses, custom houses and ships.</summary>
    [Fact]
    public void MultisCountCountsMultiTypes()
    {
        var world = TestHarness.CreateWorld();
        world.CreateItem().ItemType = SphereNet.Core.Enums.ItemType.Multi;
        world.CreateItem().ItemType = SphereNet.Core.Enums.ItemType.Ship;
        world.CreateItem().ItemType = SphereNet.Core.Enums.ItemType.Container;
        WithWorld(world, () => Assert.Equal("2", Serv("MULTIS.COUNT")));
    }

    /// <summary>RTIME with no format uses CSTime::Format's default, whatever the
    /// host culture.</summary>
    [Fact]
    public void RtimeUsesTheUpstreamDefaultFormat()
    {
        string got = Serv("RTIME");
        Assert.Matches(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}$", got);
    }

    /// <summary>RTICKS.FORMAT: a timestamp alone takes the default format, the
    /// separator may be a space, and text without '%' passes through as strftime
    /// passes it.</summary>
    [Fact]
    public void RticksFormatFollowsUpstreamArguments()
    {
        long ts = 1_700_000_000;
        var local = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
        Assert.Equal(local.ToString("yyyy'/'MM'/'dd HH':'mm':'ss", System.Globalization.CultureInfo.InvariantCulture),
            Serv($"RTICKS.FORMAT {ts}"));
        Assert.Equal(local.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            Serv($"RTICKS.FORMAT {ts} %Y"));
        Assert.Equal("hello", Serv($"RTICKS.FORMAT {ts},hello"));
        Assert.Equal("", Serv("RTIME.FORMAT"));
    }

    /// <summary>RTICKS.FROMTIME takes six values as LOCAL time (mktime), so it round
    /// trips through RTICKS.FORMAT, and carries an out-of-range month over.</summary>
    [Fact]
    public void RticksFromTimeIsLocalAndRoundTrips()
    {
        string ticks = Serv("RTICKS.FROMTIME 2024,3,15,10,30,0");
        Assert.Equal("2024-03-15 10:30", Serv($"RTICKS.FORMAT {ticks},%Y-%m-%d %H:%M"));
        string carried = Serv("RTICKS.FROMTIME 2023,13,1,0,0,0");
        Assert.Equal("2024-01-01", Serv($"RTICKS.FORMAT {carried},%Y-%m-%d"));
        Assert.Equal("0", Serv("RTICKS.FROMTIME 2024,3,15"));
    }
}
