using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogAccessorSyntaxTests
{
    [Theory]
    [InlineData("ARGTXT[3]", "value")]
    [InlineData("ARGTXT.3", "value")]
    [InlineData("ARGTXT(3)", "value")]
    [InlineData("ARGTXT 3", "value")]
    [InlineData("ARGTXT.(1+2)", "value")]
    [InlineData("ARGTXT[text_id]", "value")]
    [InlineData("ARGTXT.3+1", "value")]
    [InlineData("ARGCHK.9", "1")]
    [InlineData("ARGCHK(8+1)", "1")]
    [InlineData("ARGCHK[-1]", "1")]
    [InlineData("ARGCHK[0FFFFFFFF]", "1")]
    [InlineData("ARGTXT[65539]", "")]
    public void AccessorReadsSingleSphereOperand(string accessor, string expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-accessor-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DEFNAME args_test]\ntext_id=3\n[DIALOG d_accessor]\n0,0\nDTEXT 1 1 0 Test\n[DIALOG d_accessor BUTTON]\nON=7\nTAG.RESULT=A<{accessor}>B\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19507);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_accessor", 0, player));
            client.HandleGumpResponse(player.Uid.Value, client.Gumps.OpenScriptDialogs["d_accessor"], 7,
                [9, uint.MaxValue], [(3, "value")]);
            Assert.True(player.TryGetTag("RESULT", out var actual));
            Assert.Equal("A" + expected + "B", actual);
        }
        finally { File.Delete(path); }
    }
}
