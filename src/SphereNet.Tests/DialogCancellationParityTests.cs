using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogCancellationParityTests
{
    [Theory]
    [InlineData("DIALOG", "missing_dialog")]
    [InlineData("SDIALOG", "missing_dialog")]
    [InlineData("DIALOG", "")]
    public void MissingDialogDoesNotSendPlaceholder(string verb, string name)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19501);
        var player = world.CreateCharacter();
        TestHarness.AttachCharacter(client, player);
        Assert.False(client.TryExecuteScriptCommand(player, verb, name, null));
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] is 0xDD or 0xB0);
    }

    [Theory]
    [InlineData(true, "d_cancel", 1, "IF 1", "ENDIF")]
    [InlineData(false, "d_cancel", 1, "IF 1", "ENDIF")]
    [InlineData(true, "d_helppage", 1, "FOR 2", "ENDFOR")]
    [InlineData(false, "d_helppage", 1, "FOR 2", "ENDFOR")]
    [InlineData(true, "d_cancel", 1, "WHILE 1", "ENDWHILE")]
    [InlineData(false, "d_cancel", 1, "WHILE 1", "ENDWHILE")]
    [InlineData(true, "d_cancel", 0, "IF 1", "ENDIF")]
    [InlineData(false, "d_cancel", 0, "IF 1", "ENDIF")]
    [InlineData(true, "d_cancel", 2, "FOR 2", "ENDFOR")]
    [InlineData(false, "d_cancel", 2, "FOR 2", "ENDFOR")]
    public void LayoutReturnStopsExecutionAndOnlyOneCancels(bool interpreter, string name, int result, string begin, string end)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-cancel-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DIALOG {name}]\n0,0\nSRC.CTAG.BEFORE=1\nDTEXT 10 10 0 Before\n{begin}\nRETURN {result}\n{end}\nSRC.CTAG.AFTER=1\nDTEXT 10 30 0 After\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19502);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources },
                triggerDispatcher: interpreter ? stack.Dispatcher : null);
            Assert.Equal(result != 1, client.TryExecuteScriptCommand(player, "SDIALOG", name, null));
            Assert.True(player.TryGetProperty("CTAG.BEFORE", out var before));
            Assert.Equal("1", before);
            player.TryGetProperty("CTAG.AFTER", out var after);
            Assert.NotEqual("1", after);
            Assert.Equal(result != 1, client.IsScriptDialogOpen(name));
            Assert.Equal(result == 1 ? 0 : 1, TestHarness.GetQueuedPackets(client.NetState).Count(p => p.Span[0] is 0xDD or 0xB0));
        }
        finally { File.Delete(path); }
    }
}
