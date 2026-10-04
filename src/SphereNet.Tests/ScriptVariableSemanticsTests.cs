using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Variables;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Script variables as Source-X stores and reads them, driven through the real
/// ScriptInterpreter:
///  * CVarDefMap::SetStr (CVarDefMap.cpp:467): a quoted value is a string var kept
///    verbatim; an unquoted simple number is a number var holding the evaluated
///    value; an unquoted empty value deletes; the 0-variants drop a zero number.
///  * CVarDefContNum::GetValStr (CVarDefMap.cpp:45): a number var reads back in
///    Sphere hex unless DECIMALVARIABLES=1 (CServerConfig.cpp:68, sphere.ini:287).
///  * CVarDefCont::GetValStrZeroed (CVarDefMap.cpp:18): a missing key reads "" and
///    its 0-variant "0".
///  * CObjBase::r_WriteVal TAG (CObjBase.cpp:1553): the object's own tag, then the
///    definition's.
///  * LOCAL (CScriptTriggerArgs.cpp:242), VAR/VAR0 (CScriptObj.cpp:351) and
///    CTAG/CTAG0 (CClient.cpp:795) go through the same SetStr.
///  * NAME takes its text as written whichever reference the line goes through.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptVariableSemanticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_vars_" + Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });

    public void Dispose()
    {
        _logs.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] Defs =
    [
        "[ITEMDEF 0eed]", "DEFNAME=i_var_probe", "NAME=Probe coin",
        "TAG.FROMDEF=item_default", "TAG.DEFNUM=10", "TAG.DEFQUOTED=\"1+2\"", "",
        "[CHARDEF 0190]", "DEFNAME=c_var_probe", "NAME=Probe human", "TAG.FROMDEF=char_default", "",
    ];

    private sealed class Bench
    {
        public required ScriptRuntimeStack Stack { get; init; }
        public required GameWorld World { get; set; }
        public required Character Owner { get; init; }
        public required Item Coin { get; init; }
        public required Character Human { get; init; }
        public TriggerArgs Args => new() { Source = Owner };
    }

    private static readonly FieldInfo ProgramWorld = typeof(SphereNet.Server.Program)
        .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;

    private Bench Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "defs.scp");
        File.WriteAllLines(file, Defs);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();
        stack.Interpreter.FunctionLookup = stack.Runner.HasFunction;

        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        typeof(SphereNet.Server.Program)
            .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, stack.Resources);

        var world = UseWorld(TestHarness.CreateWorld());
        stack.Interpreter.ResolveObjectRef = (obj, head) =>
            head.StartsWith("UID.", StringComparison.OrdinalIgnoreCase)
                ? (ScriptNumber.TryParseUInt(head[4..], out uint u) ? ProgramWorldNow()?.FindObject(new Serial(u)) : null)
                : (obj as ObjBase)?.ResolveScriptRefHead(head);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.BodyId = 0x190;
        owner.Name = "Owner";
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var coin = world.CreateItem();
        coin.BaseId = 0x0EED;
        world.PlaceItem(coin, new Point3D(101, 100, 0, 0));

        var human = world.CreateCharacter();
        human.CharDefIndex = 0x190;
        human.BodyId = 0x190;
        human.Name = "Human";
        world.PlaceCharacter(human, new Point3D(102, 100, 0, 0));

        return new Bench { Stack = stack, World = world, Owner = owner, Coin = coin, Human = human };
    }

    private static GameWorld? ProgramWorldNow() => ProgramWorld.GetValue(null) as GameWorld;

    private static GameWorld UseWorld(GameWorld world)
    {
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.ResolveCharByUid = world.FindChar;
        ProgramWorld.SetValue(null, world);
        return world;
    }

    private static void Run(Bench b, IScriptObj target, ScriptScope scope, params string[] lines)
    {
        var keys = lines.Select(line =>
        {
            var key = new ScriptKey();
            key.Parse(line);
            return key;
        }).ToList();
        b.Stack.Interpreter.Execute(keys, target, null, b.Args, scope);
    }

    private static void Run(Bench b, IScriptObj target, params string[] lines) =>
        Run(b, target, new ScriptScope(), lines);

    private static string Read(Bench b, IScriptObj target, string expression, ScriptScope? scope = null) =>
        b.Stack.Interpreter.ExpandText("<" + expression + ">", target, null, b.Args, scope ?? new ScriptScope());

    private string SaveAndReload(Bench b, string sub)
    {
        string dir = Path.Combine(_dir, sub);
        var saver = new WorldSaver(_logs) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(b.World, dir));
        var dst = TestHarness.CreateWorld();
        new WorldLoader(_logs).Load(dst, dir);
        b.World = UseWorld(dst);
        return dir;
    }

    // ---- 1: a quoted TAG value is text ------------------------------------------

    [Fact]
    public void AQuotedTagKeepsItsTextThroughASaveAndReload()
    {
        var b = Build();
        Run(b, b.Coin, "TAG.QUOTED=\"1+2\"", "TAG.DATE=\"2026-10-01\"", "TAG.QTEN=\"10\"");

        Assert.Equal("1+2", Read(b, b.Coin, "TAG.QUOTED"));
        Assert.Equal("2026-10-01", Read(b, b.Coin, "TAG.DATE"));
        Assert.Equal("10", Read(b, b.Coin, "TAG.QTEN"));

        SaveAndReload(b, "q");
        var coin = b.World.FindItem(b.Coin.Uid)!;
        Assert.Equal("1+2", Read(b, coin, "TAG.QUOTED"));
        Assert.Equal("2026-10-01", Read(b, coin, "TAG.DATE"));
        Assert.Equal("10", Read(b, coin, "TAG.QTEN"));
    }

    // ---- 2: definition TAG fallback ----------------------------------------------

    [Fact]
    public void ATagReadFallsBackToTheDefinition()
    {
        var b = Build();
        Assert.Equal("item_default", Read(b, b.Coin, "TAG.FROMDEF"));
        Assert.Equal("item_default", Read(b, b.Coin, "TAG0.FROMDEF"));
        Assert.Equal("char_default", Read(b, b.Human, "TAG.FROMDEF"));
        Assert.Equal("1+2", Read(b, b.Coin, "TAG.DEFQUOTED"));
        Assert.Equal("0a", Read(b, b.Coin, "TAG.DEFNUM"));

        Run(b, b.Coin, "TAG.FROMDEF=override");
        Assert.Equal("override", Read(b, b.Coin, "TAG.FROMDEF"));

        Run(b, b.Coin, "TAG.FROMDEF=");
        Assert.Equal("item_default", Read(b, b.Coin, "TAG.FROMDEF"));
        Assert.Equal("item_default", Read(b, b.Coin, "TAG0.FROMDEF"));

        // A quoted empty override is a key of its own: it does not fall back.
        Run(b, b.Coin, "TAG.FROMDEF=\"\"");
        Assert.Equal("", Read(b, b.Coin, "TAG.FROMDEF"));
        Assert.Equal("", Read(b, b.Coin, "TAG0.FROMDEF"));
    }

    // ---- 3: missing TAG vs TAG0 ---------------------------------------------------

    [Fact]
    public void AMissingTagReadsEmptyAndItsZeroVariantZero()
    {
        var b = Build();
        Assert.Equal("", Read(b, b.Coin, "TAG.NEVERSET"));
        Assert.Equal("0", Read(b, b.Coin, "TAG0.NEVERSET"));
        Assert.Equal("", Read(b, b.Owner, "TAG.NEVERSET"));
        Assert.Equal("0", Read(b, b.Owner, "TAG0.NEVERSET"));
        Assert.Equal("", Read(b, b.Owner, "CTAG.NEVERSET"));
        Assert.Equal("0", Read(b, b.Owner, "CTAG0.NEVERSET"));
    }

    // ---- 4: number vars ---------------------------------------------------------

    [Fact]
    public void AnUnquotedNumberIsANumberVarReadInSphereHex()
    {
        var b = Build();
        var scope = new ScriptScope();
        Run(b, b.Coin, scope,
            "TAG.TEN=10", "TAG.EXPRESSION=5+3", "TAG.NUM=16", "TAG.QTEN=\"10\"",
            "LOCAL.TEN=10", "LOCAL.HEX=010", "LOCAL.EXPR=5+3",
            "VAR.SVT_TEN=10", "VAR.SVT_EXPR=5+3", "VAR.SVT_QTEN=\"10\"");
        Run(b, b.Owner, "CTAG.SVT_TEN=10", "CTAG.SVT_EXPR=5+3");

        Assert.Equal("0a", Read(b, b.Coin, "TAG.TEN"));
        Assert.Equal("08", Read(b, b.Coin, "TAG.EXPRESSION"));
        Assert.Equal("010", Read(b, b.Coin, "TAG.NUM"));
        Assert.Equal("10", Read(b, b.Coin, "TAG.QTEN"));
        Assert.Equal("0a", Read(b, b.Coin, "LOCAL.TEN", scope));
        Assert.Equal("08", Read(b, b.Coin, "LOCAL.EXPR", scope));
        Assert.Equal(16, scope.LocalVars.GetInt("HEX"));
        Assert.Equal("16", Read(b, b.Coin, "DLOCAL.HEX", scope));
        Assert.Equal("0a", Read(b, b.Coin, "VAR.SVT_TEN"));
        Assert.Equal("08", Read(b, b.Coin, "VAR.SVT_EXPR"));
        Assert.Equal("10", Read(b, b.Coin, "VAR.SVT_QTEN"));
        Assert.Equal("0a", Read(b, b.Owner, "CTAG.SVT_TEN"));
        Assert.Equal("08", Read(b, b.Owner, "CTAG.SVT_EXPR"));
        // The D prefix still reads the decimal value.
        Assert.Equal("10", Read(b, b.Coin, "DTAG.TEN"));

        // The same text after a save and a reload.
        SaveAndReload(b, "n");
        var coin = b.World.FindItem(b.Coin.Uid)!;
        Assert.Equal("0a", Read(b, coin, "TAG.TEN"));
        Assert.Equal("08", Read(b, coin, "TAG.EXPRESSION"));
        Assert.Equal("010", Read(b, coin, "TAG.NUM"));
        Assert.Equal("10", Read(b, coin, "TAG.QTEN"));
        Assert.Equal("0a", Read(b, coin, "VAR.SVT_TEN"));
        Assert.Equal("08", Read(b, coin, "VAR.SVT_EXPR"));
        Assert.Equal("10", Read(b, coin, "VAR.SVT_QTEN"));
    }

    [Fact]
    public void DecimalVariablesShowsNumberVarsInDecimal()
    {
        var b = Build();
        VarMap.DecimalVariables = true;
        var scope = new ScriptScope();
        Run(b, b.Coin, scope, "TAG.TEN=10", "TAG.EXPRESSION=5+3", "LOCAL.HEX=010", "VAR.SVD_TEN=0A");
        Run(b, b.Owner, "CTAG.SVD_TEN=10");

        Assert.Equal("10", Read(b, b.Coin, "TAG.TEN"));
        Assert.Equal("8", Read(b, b.Coin, "TAG.EXPRESSION"));
        Assert.Equal("16", Read(b, b.Coin, "LOCAL.HEX", scope));
        Assert.Equal("10", Read(b, b.Coin, "VAR.SVD_TEN"));
        Assert.Equal("10", Read(b, b.Owner, "CTAG.SVD_TEN"));

        SaveAndReload(b, "d");
        var coin = b.World.FindItem(b.Coin.Uid)!;
        Assert.Equal("10", Read(b, coin, "TAG.TEN"));
        Assert.Equal("8", Read(b, coin, "TAG.EXPRESSION"));
        Assert.Equal("10", Read(b, coin, "VAR.SVD_TEN"));
    }

    [Fact]
    public void EngineNumberTagsStayReadableByTheEngine()
    {
        var b = Build();
        Run(b, b.Coin, "TAG.TEN=10", "TAG.HEXT=0A");
        // The engine's own reads parse the stored text as a Sphere number.
        Assert.True(b.Coin.TryGetTag("TEN", out string? ten));
        Assert.True(ScriptNumber.TryParseInt(ten, out int tenValue));
        Assert.Equal(10, tenValue);
        Assert.Equal(10, b.Coin.Tags.GetInt("HEXT"));

        // An engine SetInt is a number var too: the script sees hex.
        b.Coin.Tags.SetInt("ENGINE", 255);
        Assert.Equal("0ff", Read(b, b.Coin, "TAG.ENGINE"));
        Assert.Equal(255, b.Coin.Tags.GetInt("ENGINE"));
    }

    // ---- 5: empty / zero rules on LOCAL, VAR, CTAG --------------------------------

    [Fact]
    public void LocalVarAndCtagFollowTheSetStrRules()
    {
        var b = Build();
        var scope = new ScriptScope();
        Run(b, b.Coin, scope,
            "LOCAL.EMPTY=\"\"", "LOCAL.GONE=5", "LOCAL.GONE=", "LOCAL.QZERO=\"0\"",
            "VAR.SVR_EMPTY=\"\"", "VAR.SVR_GONE=5", "VAR.SVR_GONE=",
            "VAR0.SVR_DROP=7", "VAR0.SVR_DROP=0", "VAR0.SVR_QZERO=\"0\"",
            "VAR.SVR_SPACES=\"  a  \"", "VAR.SVR_TEXT=\"1+2\"");
        Run(b, b.Owner,
            "CTAG.SVR_EMPTY=\"\"", "CTAG.SVR_GONE=5", "CTAG.SVR_GONE=",
            "CTAG0.SVR_DROP=7", "CTAG0.SVR_DROP=0", "CTAG0.SVR_QZERO=\"0\"",
            "TAG0.SVR_DROP=7", "TAG0.SVR_DROP=0", "TAG0.SVR_QZERO=\"0\"", "TAG.SVR_GONE=5", "TAG.SVR_GONE=");

        // TAG follows the same rules (the reference the other surfaces are measured against).
        Assert.False(b.Owner.Tags.Has("SVR_DROP"));
        Assert.True(b.Owner.Tags.Has("SVR_QZERO"));
        Assert.False(b.Owner.Tags.Has("SVR_GONE"));

        Assert.True(scope.LocalVars.Has("EMPTY"));
        Assert.Equal("", Read(b, b.Coin, "LOCAL.EMPTY", scope));
        Assert.False(scope.LocalVars.Has("GONE"));
        Assert.True(scope.LocalVars.Has("QZERO"));
        Assert.Equal("0", Read(b, b.Coin, "LOCAL.QZERO", scope));

        Assert.NotNull(b.World.GetGlobalVar("SVR_EMPTY"));
        Assert.Equal("", Read(b, b.Coin, "VAR.SVR_EMPTY"));
        Assert.Null(b.World.GetGlobalVar("SVR_GONE"));
        Assert.Null(b.World.GetGlobalVar("SVR_DROP"));
        Assert.Equal("0", b.World.GetGlobalVar("SVR_QZERO"));
        Assert.Equal("  a  ", Read(b, b.Coin, "VAR.SVR_SPACES"));
        Assert.Equal("1+2", Read(b, b.Coin, "VAR.SVR_TEXT"));
        Assert.Equal("", Read(b, b.Coin, "VAR.SVR_NEVER"));
        Assert.Equal("0", Read(b, b.Coin, "VAR0.SVR_NEVER"));

        Assert.True(b.Owner.CTags.Has("SVR_EMPTY"));
        Assert.False(b.Owner.CTags.Has("SVR_GONE"));
        Assert.False(b.Owner.CTags.Has("SVR_DROP"));
        Assert.True(b.Owner.CTags.Has("SVR_QZERO"));
        Assert.Equal("0", Read(b, b.Owner, "CTAG.SVR_QZERO"));
    }

    [Fact]
    public void QuotedEmptyAndQuotedSpacesVarsSurviveASave()
    {
        var b = Build();
        Run(b, b.Coin, "VAR.SVS_EMPTY=\"\"", "VAR.SVS_SPACES=\"  a  \"", "VAR.SVS_TEXT=\"1+2\"");
        SaveAndReload(b, "v");
        Assert.NotNull(b.World.GetGlobalVar("SVS_EMPTY"));
        Assert.Equal("  a  ", Read(b, b.Coin, "VAR.SVS_SPACES"));
        Assert.Equal("1+2", Read(b, b.Coin, "VAR.SVS_TEXT"));
    }

    // ---- 6: NAME through every reference path ---------------------------------------

    [Fact]
    public void NameKeepsItsTextThroughBareSrcRefAndUid()
    {
        var b = Build();
        var target = b.World.CreateItem();
        target.BaseId = 0x0EED;
        b.World.PlaceItem(target, new Point3D(103, 100, 0, 0));

        Run(b, b.Coin, "NAME=\"1+2\"");
        Assert.Equal("1+2", b.Coin.Name);

        Run(b, b.Coin, "SRC.NAME=\"1+2\"");
        Assert.Equal("1+2", b.Owner.Name);

        var scope = new ScriptScope();
        Run(b, b.Coin, scope, $"REF1=0{target.Uid.Value:X}", "REF1.NAME=\"1+2\"");
        Assert.Equal("1+2", target.Name);

        Run(b, b.Human, $"UID.0{b.Human.Uid.Value:X}.NAME=\"1+2\"");
        Assert.Equal("1+2", b.Human.Name);

        // Unquoted, NAME is text as well; a numeric key is still evaluated.
        Run(b, b.Coin, "SRC.NAME=3+4");
        Assert.Equal("3+4", b.Owner.Name);
        Run(b, b.Coin, scope, "REF1.NAME=3+4");
        Assert.Equal("3+4", target.Name);
    }
}
