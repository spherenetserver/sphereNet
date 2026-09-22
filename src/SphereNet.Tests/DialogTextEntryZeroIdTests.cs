using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Reproduction for the live GM-page box: a single text entry whose id is 0, read
/// back as ARGTXT[0] and handed to a script FUNCTION.
///
/// The pack writes its only entry as DTEXTENTRYLIMITED ... 0 0 400 (colour 0, id 0)
/// and the button handler opens with IF (&lt;ISBLANK &lt;ARGTXT[0]&gt;&gt;). The client
/// sent the text — one entry, id 0, sixteen characters — and the handler still took
/// the blank branch and reopened the page.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DialogTextEntryZeroIdTests(ITestOutputHelper output)
{
    private string RunButton(string script, string body,
        (ushort Id, string Text)[] entries, uint[]? switches = null)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-zeroid-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path,
                script +
                "[DIALOG d_zeroid]\n0,0\nDTEXTENTRYLIMITED 15 15 220 100 0 0 400\n" +
                "[DIALOG d_zeroid BUTTON]\nON=21\n" + body + "\n");
            stack.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19541);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources },
                triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_zeroid", 0, player));
            client.HandleGumpResponse(player.Uid.Value,
                client.Gumps.OpenScriptDialogs["d_zeroid"], 21, switches ?? [], entries);
            return player.TryGetTag("RESULT", out var actual) ? actual ?? "" : "<no tag>";
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ATextEntryWithIdZeroIsReadBack()
    {
        string result = RunButton("", "TAG.RESULT=A<ARGTXT[0]>B", [(0, "asdas dasdasdas")]);
        output.WriteLine($"ARGTXT[0] direct -> '{result}'");
        Assert.Equal("Aasdas dasdasdasB", result);
    }

    [Fact]
    public void TheLiveGmPageGateAgreesWithWhatWasTyped()
    {
        // The shape of the pack's own IsBlank, reduced to the branch that decided
        // every GM page: ASC answers a SPACE-SEPARATED list of hex codes for the
        // whole string ("68 65 6C 6C 6F" for "hello", which is what upstream's ASC
        // answers too), and the function then asks whether that, read as a number,
        // is zero.
        //
        // <dX> has to turn its value into a NUMBER for that question to mean
        // anything. While it passed text through, the condition became
        // "68 65 6C 6C 6F == 0": the evaluator read the leading 68, stopped at the
        // next token the way Sphere's always has, and never reached the "== 0" — so
        // a condition meant to detect an EMPTY box was true for every non-empty one,
        // and the page was thrown away with "please describe your problem".
        const string fn =
            "[FUNCTION f_isblank]\nLOCAL.ASC <ASC <ARGS>>\n" +
            "IF !(<EVAL STRLEN(<ARGS>)>)\nRETURN 1\n" +
            "ELSEIF (<dLOCAL.ASC> == 0)\nRETURN 1\nENDIF\nRETURN 0\n";
        foreach (string typed in new[] { "asdas dasdasdas", "hello", "" })
        {
            string result = RunButton(fn,
                "TAG.RESULT=<QVAL (<f_isblank <ARGTXT[0]>>)?blank:sent>", [(0, typed)]);
            output.WriteLine($"typed '{typed}' -> {result}");
            Assert.Equal(typed.Length == 0 ? "blank" : "sent", result);
        }
    }

    [Fact]
    public void AnEmptyBoxStillReadsBlank()
    {
        string result = RunButton("", "TAG.RESULT=A<ARGTXT[0]>B", [(0, "")]);
        output.WriteLine($"empty entry -> '{result}'");
        Assert.Equal("AB", result);
    }

    [Fact]
    public void NoEntryAtAllReadsBlank()
    {
        string result = RunButton("", "TAG.RESULT=A<ARGTXT[0]>B", []);
        output.WriteLine($"no entries -> '{result}'");
        Assert.Equal("AB", result);
    }
}
