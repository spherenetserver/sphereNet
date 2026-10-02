using System.Reflection;
using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// SERV.&lt;rest&gt; written as a STATEMENT follows CServer::r_Verb (CServer.cpp:1780):
/// the server's verb table, then a [FUNCTION] of that name run on the server, then
/// ACCOUNT.&lt;name&gt;.&lt;key&gt;, then CScriptObj::r_Verb - which is where VAR/VAR0/LIST
/// (CScriptObj.cpp:351/361) and the NEW/OBJ/UID references (CScriptObj.cpp:1305) live.
///
/// Only the bare spellings were recognised: SERV.VAR0.x=, SERV.LIST.x.add,
/// SERV.f_x, SERV.NEW.prop= and SERV.ACCOUNT all fell to "SERV verb not implemented"
/// and did nothing, with a client attached or without one. The bare Source-X forms
/// are asserted alongside, so the prefix only adds a spelling and changes nothing
/// the Source-X packs already rely on.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ServStatementParityTests : IDisposable
{
    private const string Nl = "\r\n";
    private static readonly Type P = typeof(SphereNet.Server.Program);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_servst_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, object?> _savedStatics = new();

    public ServStatementParityTests()
    {
        foreach (string name in new[] { "_resources", "_world", "_accounts", "_config", "_triggerRunner" })
            _savedStatics[name] = Field(name).GetValue(null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedStatics)
            Field(name).SetValue(null, value);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static FieldInfo Field(string name) =>
        P.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private sealed record Bench(ScriptRuntimeStack Stack, GameWorld World, Character Ch, AccountManager Accounts);

    /// <summary>Load the script and wire the host the way the server does: the SERV
    /// resolver, the function lookup, the NEW reference and the server object.</summary>
    private Bench Build(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "t.scp");
        File.WriteAllText(file, script);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Field("_resources").SetValue(null, stack.Resources);
        Field("_world").SetValue(null, world);
        Field("_triggerRunner").SetValue(null, stack.Runner);
        var accounts = new AccountManager(TestHarness.CreateLoggerFactory());
        Field("_accounts").SetValue(null, accounts);

        var resolve = P.GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        var interp = stack.Interpreter;
        interp.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        interp.FunctionLookup = stack.Runner.HasFunction;
        interp.ServerObject = (IScriptObj)Field("_serverHookContext").GetValue(null)!;
        interp.ResolveObjectRef = (obj, head) =>
            head.Equals("NEW", StringComparison.OrdinalIgnoreCase) ? world.FindObject(world.LastNewObject)
            : obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x190;
        ch.Name = "Prober";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return new Bench(stack, world, ch, accounts);
    }

    private static void Run(Bench b, string function, ITextConsole? source = null, ObjBase? argo = null)
    {
        var args = new TriggerArgs { Source = b.Ch, Object1 = argo };
        Assert.True(b.Stack.Runner.TryRunFunction(function, b.Ch, source, args, out _), $"{function} not found");
    }

    private static string Read(Bench b, string expr, ObjBase? argo = null)
    {
        b.Ch.Tags.Remove("OUT");
        b.Stack.Interpreter.Execute([new ScriptKey("TAG.OUT", expr)], b.Ch, null,
            new TriggerArgs { Source = b.Ch, Object1 = argo }, new ScriptScope());
        return b.Ch.Tags.Get("OUT") ?? "";
    }

    private sealed class Console(PrivLevel level) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => level;
        public void SysMessage(string text) { }
        public string GetName() => "console";
    }

    // ---- items 1 / 2: VAR, VAR0, LIST through SERV ---------------------------------

    [Fact]
    public void ServVarAndVar0WritesReachTheGlobalStore()
    {
        var b = Build(
            "[FUNCTION f_t]" + Nl +
            "SERV.VAR0.psv=5" + Nl +
            "serv.var.psv2=abc" + Nl +
            "VAR0.bare=7" + Nl);
        Run(b, "f_t");

        Assert.Equal("5", Read(b, "<EVAL <VAR0.psv>>"));
        Assert.Equal("5", Read(b, "<EVAL <SERV.VAR0.psv>>"));
        Assert.Equal("abc", Read(b, "<SERV.VAR.psv2>"));
        // The Source-X bare spelling, unchanged, reads back through either form.
        Assert.Equal("7", Read(b, "<EVAL <SERV.VAR0.bare>>"));
    }

    [Fact]
    public void ServVarWritesWorkWithoutAnyConsole()
    {
        var b = Build("[FUNCTION f_t]" + Nl + "serv.var0.nocon=9" + Nl);
        var args = new TriggerArgs();
        Assert.True(b.Stack.Runner.TryRunFunction("f_t", (IScriptObj)Field("_serverHookContext").GetValue(null)!,
            null, args, out _));
        Assert.Equal("9", Read(b, "<EVAL <VAR0.nocon>>"));
    }

    [Fact]
    public void ServListVerbsMutateTheGlobalList()
    {
        var b = Build(
            "[FUNCTION f_t]" + Nl +
            "serv.list.pl.clear" + Nl +
            "serv.list.pl.add 5" + Nl +
            "serv.list.pl.add 6" + Nl +
            "serv.list.pl.add 7" + Nl +
            "serv.list.pl.0.remove" + Nl +
            "LIST.bare.add 1" + Nl);
        Run(b, "f_t");

        Assert.Equal("2", Read(b, "<SERV.LIST.pl.COUNT>"));
        Assert.Equal("6", Read(b, "<SERV.LIST.pl.0>"));
        // Bare reads of the global list answer too, and the bare write is unchanged.
        Assert.Equal("2", Read(b, "<LIST.pl.COUNT>"));
        Assert.Equal("1", Read(b, "<SERV.LIST.bare.COUNT>"));
    }

    // ---- item 3: SERV.<function> --------------------------------------------------

    [Fact]
    public void ServFunctionRunsOnTheServerWithTheLinesArguments()
    {
        var b = Build(
            "[FUNCTION f_caller]" + Nl +
            "serv.f_target 7,8" + Nl +
            "[FUNCTION f_target]" + Nl +
            "VAR0.ran=<ARGN1>" + Nl +
            "VAR.args=<ARGS>" + Nl +
            "VAR.who=<NAME>" + Nl +
            "VAR.src=<SRC.NAME>" + Nl);
        Run(b, "f_caller");

        Assert.Equal("7", Read(b, "<EVAL <VAR0.ran>>"));
        Assert.Equal("7,8", Read(b, "<VAR.args>"));
        // r_Call on g_Serv: the function's own object is the server, SRC is kept.
        Assert.Equal("SERVER", Read(b, "<VAR.who>"));
        Assert.Equal("Prober", Read(b, "<VAR.src>"));
    }

    // ---- item 19: SERV.NEW.<prop>= ------------------------------------------------

    [Fact]
    public void ServNewPropertyWritesReachTheNewObject()
    {
        var b = Build(
            "[ITEMDEF 0eed]" + Nl + "DEFNAME=i_gold" + Nl + "TYPE=t_gold" + Nl +
            "[FUNCTION f_t]" + Nl +
            "serv.newitem i_gold" + Nl +
            "serv.new.amount=3" + Nl +
            "serv.new.tag.ptag=9" + Nl +
            "SERV.NEW.COLOR 021" + Nl);
        Run(b, "f_t");

        var item = b.World.FindItem(b.World.LastNewItem);
        Assert.NotNull(item);
        Assert.Equal(3, (int)item!.Amount);
        Assert.Equal("9", item.TagValue("PTAG"));
        Assert.Equal(0x21, item.Hue.Value);
    }

    // ---- item 20: SERV.ACCOUNT ----------------------------------------------------

    [Fact]
    public void ServAccountVerbFormLoadsKeysOnTheNamedAccount()
    {
        var b = Build(
            "[FUNCTION f_t]" + Nl +
            "SERV.ACCOUNT bob TAG0.X=5" + Nl +
            "serv.account bob plevel 2" + Nl +
            "SERV.ACCOUNT ADD carl secret" + Nl);
        var bob = b.Accounts.CreateAccount("bob", "pw")!;

        Run(b, "f_t");   // no console: the server's own privilege

        Assert.Equal("5", bob.Tags.Get("X"));
        Assert.Equal(PrivLevel.Counsel, bob.PrivLevel);
        Assert.NotNull(b.Accounts.FindAccount("carl"));
    }

    [Fact]
    public void ServAccountVerbFormNeedsAnAdminCaller()
    {
        var b = Build("[FUNCTION f_t]" + Nl + "SERV.ACCOUNT bob PLEVEL 4" + Nl);
        var bob = b.Accounts.CreateAccount("bob", "pw")!;

        Run(b, "f_t", new Console(PrivLevel.GM));
        Assert.Equal(PrivLevel.Player, bob.PrivLevel);

        Run(b, "f_t", new Console(PrivLevel.Admin));
        Assert.Equal(PrivLevel.GM, bob.PrivLevel);
    }

    [Fact]
    public void ServAccountDottedFormLoadsTheKey()
    {
        var b = Build(
            "[FUNCTION f_t]" + Nl +
            "SERV.ACCOUNT.<ARGS>.TAG.Y=hello" + Nl +
            "SERV.ACCOUNT.bob.TAG0.Z 4" + Nl);
        var bob = b.Accounts.CreateAccount("bob", "pw")!;

        var args = new TriggerArgs { Source = b.Ch };
        args.InitFromRaw("bob");
        // The dotted form is r_LoadVal with no privilege test, even for a player.
        Assert.True(b.Stack.Runner.TryRunFunction("f_t", b.Ch, new Console(PrivLevel.Player), args, out _));

        Assert.Equal("hello", bob.Tags.Get("Y"));
        Assert.Equal("4", bob.Tags.Get("Z"));
        Assert.Equal("hello", Read(b, "<SERV.ACCOUNT.bob.TAG.Y>"));
    }

    // ---- P3: SERV.RESYNC and the WRITEFILE shadow -----------------------------------

    [Fact]
    public void ServResyncIsDeferredToTheMainLoop()
    {
        var b = Build("[FUNCTION f_t]" + Nl + "SERV.RESYNC" + Nl);
        var queue = (System.Collections.Concurrent.ConcurrentQueue<Action>)Field("_mainLoopActions").GetValue(null)!;
        int before = queue.Count;

        Run(b, "f_t");

        Assert.Equal(before + 1, queue.Count);
        // Leave the queue as it was; the resync itself is not this test's business.
        var drained = new List<Action>();
        while (queue.TryDequeue(out var a)) drained.Add(a);
        foreach (var a in drained.Take(before)) queue.Enqueue(a);
    }

    [Fact]
    public void APackWritefileFunctionWinsOverTheBuiltIn()
    {
        var b = Build(
            "[FUNCTION WRITEFILE]" + Nl +
            "VAR.wf=<ARGS>" + Nl +
            "[FUNCTION f_t]" + Nl +
            "serv.writefile logs/x.txt hello" + Nl +
            "[FUNCTION f_bare]" + Nl +
            "WRITEFILE logs/y.txt bye" + Nl);

        Run(b, "f_t");
        Assert.Equal("logs/x.txt hello", Read(b, "<VAR.wf>"));
        Run(b, "f_bare");
        Assert.Equal("logs/y.txt bye", Read(b, "<VAR.wf>"));
    }

    // ---- P3: <SERV.f_x> reads ------------------------------------------------------

    [Fact]
    public void AServFunctionReadCallsTheFunctionInsteadOfAnsweringItsIndex()
    {
        var b = Build(
            "[FUNCTION f_probe_ret]" + Nl + "RETURN 42" + Nl +
            "[FUNCTION f_probe_twice]" + Nl + "RETURN <EVAL <ARGN1>*2>" + Nl);

        Assert.Equal("42", Read(b, "<SERV.f_probe_ret>"));
        Assert.Equal("10", Read(b, "<SERV.f_probe_twice 5>"));
        // The server's own keys are still the server's.
        Assert.Equal("Prober", Read(b, "<SRC.NAME>"));
    }

    /// <summary>A [FUNCTION SERV.name] - the whole token - is looked up before the SERV
    /// reference is followed (CObjBase.cpp:974). With the server asked first, DAYNAME
    /// was read as the d-prefixed &lt;dAYNAME&gt; and answered "0".</summary>
    [Fact]
    public void AWholeTokenServFunctionAnswersBeforeTheServerKeys()
    {
        var b = Build(
            "[FUNCTION SERV.DAYNAME]" + Nl + "RETURN Thursday" + Nl +
            "[FUNCTION SERV.ECHO]" + Nl + "RETURN <ARGS>" + Nl);

        Assert.Equal("Thursday", Read(b, "<SERV.DAYNAME>"));
        Assert.Equal("a b", Read(b, "<SERV.ECHO a b>"));
        // A real server key keeps its own answer.
        Assert.Equal(Read(b, "<SERV.RTIME.FORMAT %Y>"), DateTime.Now.Year.ToString());
    }

    // ---- item 31: SERV.RTIMESQL ----------------------------------------------------

    [Fact]
    public void RtimeSqlIsTheLocalTimeAsAnSqlDatetime()
    {
        var b = Build("[FUNCTION f_unused]" + Nl + "RETURN 1" + Nl);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"), Read(b, "<SERV.RTIMESQL>"));
    }

    /// <summary>A Source-X pack answers RTIMESQL with its own [FUNCTION]; that one
    /// still wins over the custom-version built-in.</summary>
    [Fact]
    public void APackRtimeSqlFunctionWinsOverTheBuiltIn()
    {
        var b = Build("[FUNCTION RTIMESQL]" + Nl + "RETURN from_pack" + Nl);
        Assert.Equal("from_pack", Read(b, "<SERV.RTIMESQL>"));
    }

    // ---- item 17: SERV.MYSQL -------------------------------------------------------

    [Fact]
    public void ServMySqlIsTheIniSwitch()
    {
        var b = Build("[FUNCTION f_unused]" + Nl + "RETURN 1" + Nl);
        Field("_config").SetValue(null, new SphereNet.Core.Configuration.SphereConfig { MySQL = 1 });
        Assert.Equal("1", Read(b, "<SERV.MYSQL>"));
        Field("_config").SetValue(null, new SphereNet.Core.Configuration.SphereConfig { MySQL = 0 });
        Assert.Equal("0", Read(b, "<SERV.MYSQL>"));
    }

    // ---- item 21: SERV.ITEMDEF.x.ID ------------------------------------------------

    [Fact]
    public void ANumericItemdefWithoutIdAnswersItsOwnGraphic()
    {
        var b = Build(
            "[ITEMDEF 0f51]" + Nl + "DEFNAME=i_probe_dagger" + Nl + "NAME=dagger" + Nl +
            "[ITEMDEF i_probe_named]" + Nl + "ID=0f52" + Nl +
            "[ITEMDEF i_probe_noid]" + Nl + "NAME=nothing" + Nl);

        Assert.Equal("0F51", Read(b, "<SERV.ITEMDEF.i_probe_dagger.ID>"));
        Assert.Equal("0F52", Read(b, "<SERV.ITEMDEF.i_probe_named.ID>"));
        Assert.Equal("00", Read(b, "<SERV.ITEMDEF.i_probe_noid.ID>"));
    }

    // ---- item 22: TEVENTS on an instance -------------------------------------------

    [Fact]
    public void AnInstanceReadsTeventsFromItsDefinition()
    {
        var b = Build(
            "[EVENTS e_probe_tev]" + Nl + "ON=@Click" + Nl + "RETURN 0" + Nl +
            "[ITEMDEF 0f51]" + Nl + "DEFNAME=i_probe_tev" + Nl + "TEVENTS=e_probe_tev" + Nl);

        var item = b.World.CreateItem();
        item.BaseId = 0x0F51;
        b.World.PlaceItem(item, new Point3D(101, 100, 0, 0));

        Assert.Equal("e_probe_tev", Read(b, "<ARGO.TEVENTS>", item));
        Assert.Equal("1", Read(b, "<QVAL (<ARGO.TEVENTS> == e_probe_tev) ? 1 : 0>", item));
    }

    // ---- P3: LOOKUPSKILL with KEY renames ------------------------------------------

    [Fact]
    public void LookupSkillFindsAPackRenamedSkill()
    {
        var b = Build(
            "[SKILL 19]" + Nl + "DEFNAME=Skill_Farming" + Nl + "KEY=Farming" + Nl +
            "[NEWBIE Farming]" + Nl + "ITEM=i_gold" + Nl);

        Assert.Equal("19", Read(b, "<SERV.LOOKUPSKILL Farming>"));
        // The Source-X names keep answering.
        Assert.Equal("0", Read(b, "<SERV.LOOKUPSKILL Alchemy>"));
        Assert.Equal("-1", Read(b, "<SERV.LOOKUPSKILL NoSuchSkillAnywhere>"));
    }

    // ---- P3: CLIENTIS3D on a character ---------------------------------------------

    [Fact]
    public void ClientIs3DIsAnsweredOnTheCharacter()
    {
        var b = Build("[FUNCTION f_unused]" + Nl + "RETURN 1" + Nl);
        var saved = Character.ResolveClientInfo;
        try
        {
            Character.ResolveClientInfo = _ => (0, Character.ClientType.Classic3D);
            Assert.Equal("1", Read(b, "<CLIENTIS3D>"));
            Character.ResolveClientInfo = _ => (0, Character.ClientType.ClassicWindows);
            Assert.Equal("0", Read(b, "<CLIENTIS3D>"));
        }
        finally { Character.ResolveClientInfo = saved; }
    }
}

/// <summary>Expression-level fixes that need no host: FLOATVAL number reading,
/// the call form of STRTOUPPER/STRTOLOWER and MULDIV's blank separators.</summary>
public sealed class ScriptExpressionParityWaveTests
{
    private static string E(string expr) => new ExpressionParser().EvaluateStr(expr);

    /// <summary>strtod (CFloatMath.cpp:197-218): a leading zero is decimal.</summary>
    [Theory]
    [InlineData("<FLOATVAL 1+0.5>", "1.500000")]
    [InlineData("<FLOATVAL 0.5*2>", "1.000000")]
    [InlineData("<FLOATVAL 0.25>", "0.250000")]
    [InlineData("<FLOATVAL 010>", "10.000000")]
    [InlineData("<FLOATVAL 1+<FLOATVAL 500/1000>>", "1.500000")]
    // Unchanged forms.
    [InlineData("<FLOATVAL (1.5+4)/2>", "2.750000")]
    [InlineData("<FLOATVAL 5/2>", "2.500000")]
    [InlineData("<FLOATVAL 0ff>", "255.000000")]
    public void FloatValReadsLeadingZeroDecimals(string expr, string expected)
        => Assert.Equal(expected, E(expr));

    [Theory]
    [InlineData("<STRTOUPPER(abc)>", "ABC")]
    [InlineData("<STRTOLOWER(ABC)>", "abc")]
    [InlineData("<STRTOUPPER abc>", "ABC")]
    [InlineData("<STRTOLOWER ABC>", "abc")]
    public void StrToUpperAndLowerAcceptTheCallForm(string expr, string expected)
        => Assert.Equal(expected, E(expr));

    [Theory]
    [InlineData("<MULDIV 10 3 2>", "15")]
    [InlineData("<MULDIV 10,3,2>", "15")]
    [InlineData("<MULDIV(10,3,2)>", "15")]
    [InlineData("<MULDIV 10, 3 2>", "15")]
    public void MulDivTakesBlankSeparatedArguments(string expr, string expected)
        => Assert.Equal(expected, E(expr));
}
