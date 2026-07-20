using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// Source-X runs a DIALOG layout as a normal script with the dialog def as the
/// target: a body line whose command is not a built-in gump keyword is looked up
/// in the FUNCTION table and called, so the function's own gump verbs (RESIZEPIC,
/// DHTMLGUMP, ...) append to the control list (CDialogDef::r_Verb). SphereNet had
/// wrongly registered RESIZE as a built-in render keyword, shadowing the pack's
/// [FUNCTION RESIZE] so it was never called and the raw line was misparsed.
/// </summary>
public sealed class DialogFunctionCallTests
{
    // Mirrors ClientDialogHandler.DialogRenderCommands / Source-X sm_szLoadKeys.
    private static readonly HashSet<string> GumpVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUTTON", "BUTTONTILEART", "CHECKBOX", "CHECKERTRANS", "CROPPEDTEXT",
        "DCROPPEDTEXT", "DHTMLGUMP", "DORIGIN", "DTEXT", "DTEXTENTRY",
        "DTEXTENTRYLIMITED", "GROUP", "GUMPIC", "GUMPPIC", "GUMPPICTILED",
        "HTMLGUMP", "ITEMPROPERTY", "NOCLOSE", "NODISPOSE", "NOMOVE", "PAGE",
        "PICINPIC", "RADIO", "RESIZEPIC", "TEXT", "TEXTENTRY",
        "TEXTENTRYLIMITED", "TILEPIC", "TILEPICHUE", "TOOLTIP", "XMFHTMLGUMP",
        "XMFHTMLGUMPCOLOR", "XMFHTMLTOK"
    };

    /// <summary>Captures gump verbs like the real DialogRenderTarget.</summary>
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
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dlgfn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, scriptText);
        resources.LoadResourceFile(tmp);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var result)
                ? result : TriggerResult.Default;
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var result)
                ? result : TriggerResult.Default;
        interpreter.ResolveFunctionExpression = (name, rawArgs, target, source, args) =>
            runner.TryEvaluateFunction(name, rawArgs, target, source, args, out var value)
                ? value : null;
        return new TriggerDispatcher { Resources = resources, Runner = runner };
    }

    [Fact]
    public void ResizeFunctionCall_InDialogBody_ExpandsToGumpControls()
    {
        // The real pack RESIZE helper: outer + inner border, optional centered title.
        var dispatcher = Dispatcher("""
            [FUNCTION RESIZE]
            RESIZEPIC <EVAL <argv[0]>> <EVAL <argv[1]>> 9200 <argv[2]> <argv[3]>
            RESIZEPIC <EVAL <argv[0]>+10> <EVAL <argv[1]>+10> 3000 <EVAL <argv[2]>-20> <EVAL <argv[3]>-20>
            IF !(<ISEMPTY <ARGV[4]>>)
                DHTMLGUMP <eval <argv[0]>+20> <EVAL <argv[1]>+20> <EVAL <argv[2]>-40> 25 1 0 <ARGV[4]>
            ENDIF
            """);

        var interpreter = dispatcher.Runner!.Interpreter;
        var subject = new Character();
        var capture = new CaptureTarget(subject);
        var scope = new ScriptScope { TriggerName = "DIALOG:d_test", MaxLoopIterations = 500 };
        var args = new ExecTriggerArgs(subject, 0, 0, "0");

        var body = new[] { new ScriptKey("RESIZE", "0,0,960,400,Admin Panel") };
        interpreter.Execute(body, capture, null, args, scope);

        // The FUNCTION must have been dispatched and expanded into gump controls
        // (before the fix, RESIZE was swallowed as a built-in and nothing expanded).
        Assert.Equal(3, capture.Output.Count);
        Assert.Equal("RESIZEPIC", capture.Output[0].Key);
        Assert.Equal("0 0 9200 960 400", CollapseWs(capture.Output[0].Arg));
        Assert.Equal("RESIZEPIC", capture.Output[1].Key);
        Assert.Equal("10 10 3000 940 380", CollapseWs(capture.Output[1].Arg));

        // The title (argv[4]) drives a centered DHTMLGUMP; Source-X ARGV keeps the
        // whole comma-field, so the multi-word title must survive intact.
        Assert.Equal("DHTMLGUMP", capture.Output[2].Key);
        Assert.EndsWith("Admin Panel", capture.Output[2].Arg.TrimEnd());
    }

    [Fact]
    public void ResizeFunctionCall_WithLeadingEmptyArgs_KeepsIndexAlignment()
    {
        // The pin dialog calls "RESIZE ,,230,90,Pin Giris" — the empty x/y fields
        // must be preserved so width/height/title stay on ARGV[2..4] (Source-X keeps
        // empty comma fields; dropping them would shift every later index).
        var dispatcher = Dispatcher("""
            [FUNCTION RESIZE]
            RESIZEPIC <EVAL <argv[0]>> <EVAL <argv[1]>> 9200 <argv[2]> <argv[3]>
            IF !(<ISEMPTY <ARGV[4]>>)
                DHTMLGUMP <eval <argv[0]>+20> <EVAL <argv[1]>+20> <EVAL <argv[2]>-40> 25 1 0 <ARGV[4]>
            ENDIF
            """);

        var interpreter = dispatcher.Runner!.Interpreter;
        var capture = new CaptureTarget(new Character());
        var scope = new ScriptScope { TriggerName = "DIALOG:d_pin", MaxLoopIterations = 500 };
        var args = new ExecTriggerArgs(capture, 0, 0, "0");

        var body = new[] { new ScriptKey("RESIZE", ",,230,90,Pin Giris") };
        interpreter.Execute(body, capture, null, args, scope);

        Assert.Equal(2, capture.Output.Count);
        Assert.Equal("0 0 9200 230 90", CollapseWs(capture.Output[0].Arg)); // empty x,y -> 0
        Assert.EndsWith("Pin Giris", capture.Output[1].Arg.TrimEnd());
    }

    private static string CollapseWs(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
