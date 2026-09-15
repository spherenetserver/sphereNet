using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using System.Linq;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// What a bare &lt;ARGV&gt; means.
///
/// Upstream answers the argument COUNT for it: CScriptTriggerArgs::r_WriteVal formats
/// the quantity when nothing follows the keyword (CScriptTriggerArgs.cpp:511), and the
/// indexed form &lt;ARGV[n]&gt; runs n through Exp_GetUSingle (:518) - so the index is an
/// expression, not a literal.
///
/// Here a bare &lt;ARGV&gt; returned argument ZERO and the index was parsed as a plain
/// integer. Both matter because a script pack's list helpers are built on exactly these
/// two: `[FUNCTION ARRAYCOUNT] RETURN &lt;EVAL &lt;ARGV&gt;&gt;` counts a comma list, and
/// `[FUNCTION ARRAY]` reads its last field with `&lt;ARGV[&lt;EVAL &lt;ARGV&gt; - 1&gt;]&gt;`
/// before indexing with an expression of its own. With the count coming back as the
/// first element, a dialog looping `FOR 1 &lt;ARRAYCOUNT &lt;LOCAL.list&gt;&gt;` became
/// `FOR 1 0` - which counts DOWN - and drew two blank rows.
/// </summary>
public sealed class ScriptArgvCountTests
{
    private readonly ITestOutputHelper _out;
    public ScriptArgvCountTests(ITestOutputHelper o) => _out = o;

    private static readonly HashSet<string> GumpVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUTTON", "DORIGIN", "DTEXT", "RESIZEPIC", "DHTMLGUMP", "RESIZE"
    };

    private sealed class CaptureTarget : IScriptObj
    {
        private readonly Character _subject;
        public readonly List<ScriptKey> Output = [];
        public CaptureTarget(Character subject) => _subject = subject;
        public string GetName() => _subject.GetName();
        public bool TryGetProperty(string key, out string value) => _subject.TryGetProperty(key, out value);
        public bool TrySetProperty(string key, string value) => _subject.TrySetProperty(key, value);
        public TriggerResult OnTrigger(int t, IScriptObj? s, ITriggerArgs? a) => _subject.OnTrigger(t, s, a);
        public bool TryExecuteCommand(string key, string args, ITextConsole source)
        {
            if (GumpVerbs.Contains(key)) { Output.Add(new ScriptKey(key, args)); return true; }
            return _subject.TryExecuteCommand(key, args, source);
        }
    }

    private static TriggerDispatcher Dispatcher(string scriptText)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dlglist_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, scriptText);
        resources.LoadResourceFile(tmp);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var result) ? result : TriggerResult.Default;
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var result) ? result : TriggerResult.Default;
        interpreter.ResolveFunctionExpression = (name, rawArgs, target, source, args) =>
            runner.TryEvaluateFunction(name, rawArgs, target, source, args, out var value) ? value : null;
        return new TriggerDispatcher { Resources = resources, Runner = runner };
    }

    [Fact]
    public void ADialogThatLoopsOverAListDrawsEveryEntry()
    {
        // The body written as a script, so the .scp parser produces the keys - a
        // hand-built ScriptKey list would not exercise how "LOCAL.X = <...>" is split.
        var dispatcher = Dispatcher("""
            [FUNCTION ARRAY]
            LOCAL.TEMP = <ARGV[<EVAL <ARGV> - 1>]> -1
            RETURN <ARGV[<DLOCAL.TEMP>]>

            [FUNCTION ARRAYCOUNT]
            RETURN <EVAL <ARGV>>

            [FUNCTION D_HAZIRLA_BODY]
            LOCAL.BODYLIST C_MAN_GM,C_MAN,C_WOMAN,C_ELF_MALE,C_ELF_FEMALE
            DORIGIN 20 30
            FOR 1 <ARRAYCOUNT <LOCAL.BODYLIST>>
                LOCAL.BODY = <ARRAY <LOCAL.BODYLIST>,<DLOCAL._FOR>>
                BUTTON +0 *20 0845 0846 1 0 <dlocal._for>
                DTEXT +20 +0 1000 <LOCAL.BODY>
            ENDFOR
            """);

        var capture = new CaptureTarget(new Character());
        var args = new ExecTriggerArgs(capture, 0, 0, "0");
        dispatcher.Runner!.TryRunFunction("D_HAZIRLA_BODY", capture, null, args, out _);

        _out.WriteLine($"{capture.Output.Count} control(s):");
        foreach (var k in capture.Output)
            _out.WriteLine($"  {k.Key} | {k.Arg}");

        int buttons = capture.Output.Count(k => k.Key.Equals("BUTTON", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, buttons);

        // Every row names its own entry: the ARRAY helper reads the list by index,
        // and its index arithmetic runs through ARGV twice.
        var labels = capture.Output
            .Where(k => k.Key.Equals("DTEXT", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Arg.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
            .ToArray();
        Assert.Equal(
            new[] { "C_MAN_GM", "C_MAN", "C_WOMAN", "C_ELF_MALE", "C_ELF_FEMALE" },
            labels);
    }

    /// <summary>The two rules on their own, so a failure says which one broke.</summary>
    [Theory]
    [InlineData("<ARGV>", "3")]                 // the count, not argument zero
    [InlineData("<ARGV[0]>", "a")]
    [InlineData("<ARGV[2]>", "c")]
    [InlineData("<ARGV[<EVAL 1+1>]>", "c")]     // the index is an expression
    [InlineData("<ARGV[3 -1]>", "c")]           // ...including this shape, which the
    [InlineData("<ARGV[9]>", "")]               //    pack's ARRAY helper produces
    public void ArgvAnswersTheCountAndTheIndexedField(string expr, string expected)
    {
        var dispatcher = Dispatcher($$"""
            [FUNCTION PROBE]
            DTEXT {{expr}}
            """);
        var capture = new CaptureTarget(new Character());
        var args = new ExecTriggerArgs(capture, 0, 0, "a,b,c");
        dispatcher.Runner!.TryRunFunction("PROBE", capture, null, args, out _);

        string got = capture.Output.Count > 0 ? capture.Output[0].Arg.Trim() : "";
        _out.WriteLine($"{expr} -> '{got}'");
        Assert.Equal(expected, got);
    }

    [Fact]
    public void NoArgumentsAtAllCountsZero()
    {
        var dispatcher = Dispatcher("""
            [FUNCTION PROBE]
            DTEXT [<ARGV>]
            """);
        var capture = new CaptureTarget(new Character());
        dispatcher.Runner!.TryRunFunction("PROBE", capture, null,
            new ExecTriggerArgs(capture, 0, 0, ""), out _);

        Assert.Equal("[0]", capture.Output[0].Arg.Trim());
    }
}
