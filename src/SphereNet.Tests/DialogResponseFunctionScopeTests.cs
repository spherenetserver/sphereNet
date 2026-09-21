using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using SphereNet.Scripting.Execution;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogResponseFunctionScopeTests
{
    [Theory]
    [InlineData(false, "42,5,6", "42/5/6")]
    [InlineData(true, "42,5,6", "42/5/6")]
    [InlineData(false, "", "0/0/0")]
    [InlineData(true, "", "0/0/0")]
    public void ExpressionFunctionInitializesItsOwnArguments(bool scoped, string raw, string expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"function-args-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[FUNCTION f_own_args]\nRETURN <ARGN1>/<ARGN2>/<ARGN3>\n");
            stack.Resources.LoadResourceFile(path);
            var player = TestHarness.CreateWorld().CreateCharacter();
            var caller = new TriggerArgs(player, 7, 8) { Number3 = 9 };
            string value;
            bool found = scoped
                ? stack.Runner.TryEvaluateFunction("f_own_args", raw, player, null, caller, new ScriptScope(), out value)
                : stack.Runner.TryEvaluateFunction("f_own_args", raw, player, null, caller, out value);
            Assert.True(found);
            Assert.Equal(expected, value);
            Assert.Equal(7, caller.Number1);
            Assert.Equal(8, caller.Number2);
            Assert.Equal(9, caller.Number3);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("f_response_scope 42", false, "42")]
    [InlineData("CALL f_response_scope", true, "7")]
    [InlineData("CALL f_response_scope 42", true, "42")]
    [InlineData("TAG.RESULT=<f_response_scope 42>", false, "42")]
    public void OnlyCallSharesTheResponseArgumentObject(string invocation, bool sharesResponse, string number)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-response-scope-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DIALOG d_scope]\n0,0\nDTEXT 1 1 0 Test\n[DIALOG d_scope BUTTON]\nON=7\n{invocation}\nTAG.AFTER=A<ARGTXT[3]>B\nTAG.AFTER_N=<ARGN>\n[FUNCTION f_response_scope]\nTAG.INNER=A<ARGTXT[3]>B\nTAG.CHECK=A<ARGCHK[9]>B\nTAG.NUMBER=<ARGN>\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19524);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_scope", 0, player));
            client.HandleGumpResponse(player.Uid.Value, client.Gumps.OpenScriptDialogs["d_scope"], 7, [9], [(3, "reply")]);
            void Tag(string key, string expected)
            {
                Assert.True(player.TryGetTag(key, out string? value));
                Assert.Equal(expected, value);
            }
            Tag("INNER", sharesResponse ? "AreplyB" : "AB");
            Tag("CHECK", sharesResponse ? "A1B" : "AB");
            Tag("NUMBER", number);
            Tag("AFTER", "AreplyB");
            Tag("AFTER_N", "7");
        }
        finally { File.Delete(path); }
    }
}
