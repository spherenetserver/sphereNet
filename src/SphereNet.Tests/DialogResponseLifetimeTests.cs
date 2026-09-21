using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogResponseLifetimeTests
{
    [Theory]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 1, false, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 5000, false, false)]
    [InlineData(true, 7, true, false)]
    [InlineData(true, 7, false, false)]
    [InlineData(true, 7, false, true)]
    public void ResponseOnlyRunsMatchingScriptOnLivingSubject(bool hasButtons, int button, bool deleted, bool reopen)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-response-{Guid.NewGuid():N}.scp");
        try
        {
            string script = "[DIALOG d_response_probe]\n0,0\nDTEXT 10 10 0 Test\n";
            if (hasButtons)
                script += "[DIALOG d_response_probe PREBUTTON]\nTAG.PRE=1\n[DIALOG d_response_probe BUTTON]\nON=7\nTAG.BUTTON=1\nSRC.TAG.RAN=1\n" +
                    (reopen ? "DIALOG d_response_probe\n" : "");
            File.WriteAllText(path, script);
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19505);
            var player = world.CreateCharacter();
            var subject = world.CreateItem();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_response_probe", 0, subject));
            uint uid = subject.Uid.Value;
            if (deleted) world.DeleteObject(subject);
            client.HandleGumpResponse(uid, client.Gumps.OpenScriptDialogs["d_response_probe"], (uint)button, [], []);
            bool runs = hasButtons && button == 7 && !deleted;
            Assert.Equal(runs, player.TryGetTag("RAN", out _));
            Assert.False(player.TryGetTag("BUTTON", out _));
            Assert.False(player.TryGetTag("PRE", out _));
            if (!deleted)
            {
                Assert.Equal(runs, subject.TryGetTag("BUTTON", out _));
                Assert.Equal(runs, subject.TryGetTag("PRE", out _));
            }
            Assert.Equal(reopen, client.IsScriptDialogOpen("d_response_probe"));
            Assert.Equal(reopen ? 2 : 1, TestHarness.GetQueuedPackets(client.NetState).Count(p => p.Span[0] is 0xDD or 0xB0));
        }
        finally { File.Delete(path); }
    }
}
