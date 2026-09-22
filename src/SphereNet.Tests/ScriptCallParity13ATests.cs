using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The script call chain: CALL, TRY and ARGN assignment (review 13A).
///
/// Source-X Execute_Call (CScriptObj.cpp:1505) resolves an object reference on the
/// argument first, then either passes the caller's args object through untouched (no
/// argument) or re-Inits it from the new argument and restores it afterwards. Either
/// way it is the SAME CScriptTriggerArgs, and the LOCAL pool lives on that object, so
/// CALL shares locals while an ordinary function verb line - which builds a fresh args
/// object (CObjBase.cpp:2138) - does not. TRY hands the rest of the line to the
/// target's full r_Verb (CObjBase.cpp:2899), and an ARGN assignment goes through the
/// expression parser (CScriptTriggerArgs.cpp:313).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptCallParity13ATests
{
    private sealed class AdminConsole(Character? sourceChar = null) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
        public IScriptObj? GetSourceChar() => sourceChar;
    }

    private sealed class Harness : IDisposable
    {
        public ScriptInterpreter Interpreter { get; }
        public TriggerRunner Runner { get; }
        private readonly string _path;

        public Harness(string scriptText)
        {
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            _path = Path.Combine(Path.GetTempPath(), $"sphnet_13a_{Guid.NewGuid():N}.scp");
            File.WriteAllText(_path, scriptText);
            resources.LoadResourceFile(_path);

            Interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
            Runner = new TriggerRunner(Interpreter, resources, lf.CreateLogger<TriggerRunner>());
            Interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
                Runner.TryRunFunction(name, target, source, args, scope, out var r) ? r : TriggerResult.Default;
            Interpreter.CallFunction = (name, target, source, args) =>
                Runner.TryRunFunction(name, target, source, args, out var r) ? r : TriggerResult.Default;
            Interpreter.FunctionLookup = Runner.HasFunction;
            Interpreter.ResolveObjectRef = (obj, head) => obj is ObjBase o ? o.ResolveRefHead(head) : null;
        }

        public void Dispose() => File.Delete(_path);
    }

    private static Item GroundItem(SphereNet.Game.World.GameWorld world, int x = 100)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D((short)x, 100, 0, 0));
        return item;
    }

    [Theory]
    [InlineData("ARGS=010,2,-3", "010,2,-3", 16, 2, -3)]
    [InlineData("ARGS hello,5", "hello,5", 0, 0, 0)]
    [InlineData("ARGS", "", 0, 0, 0)]
    [InlineData("ARGS=", "", 0, 0, 0)]
    [InlineData("ARGS=\"12,3,4\"", "12,3,4", 12, 3, 4)]
    public void ArgsAssignmentReinitializesNumbersAndArgoWithoutClearingScope(
        string statement, string text, long n1, long n2, long n3)
    {
        using var h = new Harness($"""
            [FUNCTION f_parent]
            LOCAL.keep=42
            REF1=<UID>
            {statement}
            TAG.keep=<LOCAL.keep>
            TAG.ref=<REF1>
            TAG.argv=<ARGV[0]>
            RETURN 1
            """);
        var world = TestHarness.CreateWorld(); var item = GroundItem(world);
        var args = new TriggerArgs { Source = item, Object1 = item, Number1 = 91, Number2 = 92, Number3 = 93, ArgString = "old,values" };
        Assert.Equal("old", args.GetArgv()[0]);
        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));
        Assert.Equal(text, args.ArgString); Assert.Equal(n1, args.Number1);
        Assert.Equal(n2, args.Number2); Assert.Equal(n3, args.Number3); Assert.Null(args.Object1);
        Assert.Same(item, args.Source);
        item.TryGetProperty("TAG.keep", out var keep); Assert.Equal("42", keep);
        item.TryGetProperty("TAG.ref", out var reference); Assert.Equal($"0{item.Uid.Value:X}", reference);
        Assert.Equal(text.Length == 0 ? Array.Empty<string>() : text.Split(','), args.GetArgv());
    }

    [Theory]
    [InlineData("\"a,b\",c", new[] { "a,b", "c" })]
    [InlineData("x,\"y,z\"", new[] { "x", "y,z" })]
    [InlineData("a,", new[] { "a" })]
    [InlineData(",,a", new[] { "", "", "a" })]
    [InlineData("  a  , b", new[] { "a  ", "b" })]
    [InlineData("pre\"a,b\"post,c", new[] { "pre\"a,b\"post", "c" })]
    public void ArgvPreservesSourceXQuotedFieldsAndSeparatorBoundaries(string raw, string[] expected)
    {
        var args = new TriggerArgs { ArgString = raw };
        Assert.Equal(expected, args.GetArgv());
        Assert.Equal(raw, args.ArgString);
    }

    [Theory]
    [InlineData("CALL f_child", false)]
    [InlineData("CALL f_child 5", true)]
    public void ArgsReinitializationInsideCallFollowsCallerRestorationRules(string call, bool restored)
    {
        using var h = new Harness($"""
            [FUNCTION f_child]
            ARGS=17,18,19
            TAG.child=<ARGN1>,<ARGN2>,<ARGN3>,<ARGO>
            RETURN 1
            [FUNCTION f_parent]
            {call}
            RETURN 1
            """);
        var world = TestHarness.CreateWorld(); var item = GroundItem(world);
        var args = new TriggerArgs { Source = item, Object1 = item, Number1 = 1, Number2 = 2, Number3 = 3, ArgString = "old" };
        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));
        item.TryGetProperty("TAG.child", out var child); Assert.Equal("17,18,19,0", child);
        Assert.Equal(restored ? "old" : "17,18,19", args.ArgString);
        Assert.Equal(restored ? 1 : 17, args.Number1);
        Assert.Equal(restored ? 2 : 18, args.Number2);
        Assert.Equal(restored ? 3 : 19, args.Number3);
        Assert.Same(restored ? item : null, args.Object1);
    }

    [Fact]
    public void CallRestoresQuotedArgumentVectorAfterChildReinitializesIt()
    {
        using var h = new Harness("""
            [FUNCTION f_child]
            TAG.child_count=<ARGV>
            TAG.child_first=<ARGV[0]>
            ARGS=7,8
            RETURN 1
            [FUNCTION f_parent]
            TAG.before=<ARGV[0]>
            CALL f_child "a,b",c
            TAG.after=<ARGV[0]>
            RETURN 1
            """);
        var world = TestHarness.CreateWorld(); var item = GroundItem(world);
        var args = new TriggerArgs { ArgString = "\"original,field\",second" };
        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));
        item.TryGetProperty("TAG.child_count", out var count); Assert.Equal("2", count);
        item.TryGetProperty("TAG.child_first", out var child); Assert.Equal("a,b", child);
        item.TryGetProperty("TAG.before", out var before); Assert.Equal("original,field", before);
        item.TryGetProperty("TAG.after", out var after); Assert.Equal(before, after);
        Assert.Equal(new[] { "original,field", "second" }, args.GetArgv());
    }

    // ============================================================ 13A-1
    [Theory]
    [InlineData("CALL f_child", true)]
    [InlineData("CALL f_child 37", true)]
    [InlineData("f_child", false)]
    public void CallSharesReferenceAndFloatPoolsButOrdinaryFunctionsAreIsolated(string call, bool shared)
    {
        using var h = new Harness(
            "[FUNCTION f_grandchild]\nTAG.SEENREF=<REF1>\nTAG.SEENFLOAT=<FLOAT.RATE>\n" +
            "REF1=0\nFLOAT.RATE=2.5\nLOCAL.FLAG=child\nRETURN 1\n" +
            "[FUNCTION f_child]\nCALL f_grandchild\nRETURN 1\n" +
            "[FUNCTION f_parent]\nREF1=<UID>\nFLOAT.RATE=1.5\nLOCAL.FLAG=parent\n" +
            call + "\nTAG.AFTERREF=<REF1>\nTAG.AFTERFLOAT=<FLOAT.RATE>\nTAG.AFTERLOCAL=<LOCAL.FLAG>\nRETURN 1\n");
        var item = GroundItem(TestHarness.CreateWorld());
        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, new TriggerArgs(), out _));
        item.TryGetProperty("UID", out string uid);
        item.TryGetProperty("TAG.SEENREF", out string seenRef);
        item.TryGetProperty("TAG.SEENFLOAT", out string seenFloat);
        item.TryGetProperty("TAG.AFTERREF", out string afterRef);
        item.TryGetProperty("TAG.AFTERFLOAT", out string afterFloat);
        item.TryGetProperty("TAG.AFTERLOCAL", out string afterLocal);
        Assert.Equal(shared ? uid : "0", seenRef);
        Assert.Equal(shared ? "1.5" : "0.0", seenFloat);
        Assert.Equal(shared ? "0" : uid, afterRef);
        Assert.Equal(shared ? "2.5" : "1.5", afterFloat);
        Assert.Equal(shared ? "child" : "parent", afterLocal);
    }

    [Theory]
    [InlineData("CALL f_child 37", false)]
    [InlineData("CALL f_child", true)]
    public void CallWithArgumentsClearsArgoTemporarilyAndRestoresIt(string call, bool keepsArgo)
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.CHILDARGO=<ARGO>\nTAG.CHILDSRC=<SRC>\nRETURN 1\n" +
            "[FUNCTION f_parent]\n" + call + "\nTAG.AFTERARGO=<ARGO>\nRETURN 1\n");
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var argObject = GroundItem(world);
        var source = world.CreateCharacter();
        var args = new TriggerArgs(source) { Object1 = argObject };
        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(source), args, out _));
        argObject.TryGetProperty("UID", out string objectUid);
        source.TryGetProperty("UID", out string sourceUid);
        item.TryGetProperty("TAG.CHILDARGO", out string childArgo);
        item.TryGetProperty("TAG.CHILDSRC", out string childSrc);
        item.TryGetProperty("TAG.AFTERARGO", out string afterArgo);
        Assert.Equal(keepsArgo ? objectUid : "0", childArgo);
        Assert.Equal(objectUid, afterArgo);
        Assert.Equal(sourceUid, childSrc);
        Assert.Same(argObject, args.Object1);
    }

    // CALL prepares ARGN/ARGS from its own argument, and an argument-less CALL leaves
    // the caller's args alone rather than blanking them.

    [Fact]
    public void CallWithAnArgumentPreparesBothArgnAndArgs()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nTAG.ARGS=<ARGS>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("37", n1);                     // not the caller's 17
        Assert.True(item.TryGetProperty("TAG.ARGS", out string a));
        Assert.Equal("37", a);
    }

    [Fact]
    public void CallWithoutAnArgumentPassesTheCallersArgsThrough()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nTAG.ARGS=<ARGS>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("17", n1);
        Assert.True(item.TryGetProperty("TAG.ARGS", out string a));
        Assert.Equal("old", a);                     // ARGS survives, it is not cleared
    }

    [Fact]
    public void TheCallersArgsAreRestoredAfterAnArgumentedCall()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child 37\nTAG.AFTER=<ARGN1>\nTAG.AFTERARGS=<ARGS>\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.AFTER", out string after));
        Assert.Equal("17", after);
        Assert.True(item.TryGetProperty("TAG.AFTERARGS", out string afterArgs));
        Assert.Equal("old", afterArgs);
        Assert.Equal(17L, args.Number1);            // ...on the args object itself too
        Assert.Equal("old", args.ArgString);
    }

    [Fact]
    public void APlainFunctionLinePreparesArgnFromItsOwnArgument()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nf_child 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("37", n1);
    }

    // ============================================================ 13A-3
    // A reference head redirects the call before the name is looked up.

    [Theory]
    [InlineData("TOPOBJ")]
    [InlineData("CONT")]
    [InlineData("LINK")]
    public void CallResolvesAnObjectReferenceHeadBeforeTheFunctionName(string head)
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=<ARGN1>\nRETURN 1\n\n" +
            $"[FUNCTION f_parent]\nCALL {head}.f_mark 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        pack.AddItem(item);
        item.Link = owner.Uid;

        ObjBase expected = head == "CONT" ? pack : owner;

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, new TriggerArgs(), out _));

        Assert.True(expected.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("37", mark);
        Assert.False(item.TryGetProperty("TAG.MARK", out string own) && own == "37");
    }

    [Fact]
    public void CallResolvesSrcBeforeTheFunctionName()
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=1\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL SRC.f_mark\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null,
            new TriggerArgs { Source = player }, out _));

        Assert.True(player.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("1", mark);
    }

    // ============================================================ 13A-4
    // TRY runs the rest of the line through the target's ordinary verb path.

    [Fact]
    public void TryReachesAPropertyWrittenWithoutAnEquals()
    {
        using var h = new Harness("[FUNCTION f_parent]\nTRY TAG.FLAG 1\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.TryGetProperty("TAG.FLAG", out string flag));
        Assert.Equal("1", flag);
    }

    [Fact]
    public void TryReachesAScriptFunction()
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=1\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nTRY f_mark\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("1", mark);
    }

    [Fact]
    public void TryStillReachesAnEngineVerb()
    {
        using var h = new Harness("[FUNCTION f_parent]\nTRY REMOVE\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.IsDeleted);
    }

    // ============================================================ 13A-5
    // ARGN assignment is an expression, not a narrow int parse.

    [Theory]
    [InlineData("16", "16")]
    [InlineData("010", "16")]     // leading zero is hex
    [InlineData("1+1", "2")]      // arithmetic, not a parse failure
    [InlineData("<EVAL 1+1>", "2")]
    public void ArgnAssignmentGoesThroughTheExpressionParser(string assigned, string expected)
    {
        using var h = new Harness(
            $"[FUNCTION f_parent]\nARGN1={assigned}\nTAG.RESULT=<ARGN1>\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null,
            new TriggerArgs { Number1 = 17 }, out _));

        Assert.True(item.TryGetProperty("TAG.RESULT", out string result));
        Assert.Equal(expected, result);
    }
}
