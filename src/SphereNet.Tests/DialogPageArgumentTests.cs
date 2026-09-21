using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogPageArgumentTests
{
    [Theory]
    [InlineData("DIALOG", "", 0)]
    [InlineData("SDIALOG", "", 0)]
    [InlineData("DIALOG", ",page_id", 16)]
    [InlineData("SDIALOG", ",page_id", 16)]
    [InlineData("DIALOG", "\t010", 16)]
    [InlineData("DIALOG", ",8+8", 16)]
    [InlineData("DIALOG", ", (8 + 8),unused", 16)]
    [InlineData("DIALOG", ",2,unused", 2)]
    [InlineData("DIALOG", " 2 unused", 2)]
    public void PageIsASphereExpressionAndDefaultsToZero(string verb, string suffix, int page)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-page-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[DEFNAME page_test]\npage_id=16\n[DIALOG d_page_probe]\n0,0\nTAG.PAGE=<ARGN1>\nDTEXT 1 1 0 Test\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19508);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(player.TryExecuteCommand(verb, "d_page_probe" + suffix, client));
            Assert.True(player.TryGetTag("PAGE", out var actual));
            Assert.Equal(page.ToString(), actual);
        }
        finally { File.Delete(path); }
    }
}
