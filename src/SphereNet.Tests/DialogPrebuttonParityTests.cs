using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogPrebuttonParityTests
{
    [Theory]
    [InlineData(0, 7, true)]
    [InlineData(1, 7, false)]
    [InlineData(2, 7, true)]
    [InlineData(1, 99, false)]
    [InlineData(1, 7, false, true)]
    public void PrebuttonRunsBeforeMatchedHandlerWithSharedContext(int result, int button, bool runs, bool close = false)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-prebutton-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"""
                [DIALOG d_pre_probe]
                0,0
                DTEXT 10 10 0 Test
                [DIALOG d_pre_probe PREBUTTON]
                TAG.PRE=<ARGN1>
                SRC.TAG.TEXT=<ARGTXT[3]>
                LOCAL.probe=42
                RETURN {result}
                TAG.UNREACHABLE=1
                [DIALOG d_pre_probe BUTTON]
                ON=7
                TAG.BUTTON=<LOCAL.probe>
                SRC.TAG.BUTTON=1
                """);
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19504);
            var player = world.CreateCharacter();
            var subject = world.CreateItem();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_pre_probe", 0, subject));
            if (close) Assert.True(client.CloseScriptDialog("d_pre_probe", button));
            else client.HandleGumpResponse(subject.Uid.Value, client.Gumps.OpenScriptDialogs["d_pre_probe"], (uint)button,
                    [], [(3, "entered text")]);
            Assert.Equal(button == 7, subject.TryGetTag("PRE", out var pre));
            if (button == 7)
            {
                Assert.Equal("7", pre);
                Assert.Equal(!close, player.TryGetTag("TEXT", out var text));
                Assert.Equal(close ? "" : "entered text", text ?? "");
                Assert.False(client.IsScriptDialogOpen("d_pre_probe"));
            }
            Assert.False(subject.TryGetTag("UNREACHABLE", out _));
            Assert.Equal(runs, subject.TryGetTag("BUTTON", out var value));
            if (runs) Assert.Equal("42", value);
            Assert.Equal(runs, player.TryGetTag("BUTTON", out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResyncRemovesDeletedPrebutton(bool full)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-pre-reload-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[DIALOG d_reload PREBUTTON]\nRETURN 1\n");
            stack.Resources.LoadResourceFile(path);
            Assert.True(stack.Resources.TryGetDialogPrebutton("D_RELOAD", out _));
            File.WriteAllText(path, "[DIALOG d_reload]\n0,0\nDTEXT 1 1 0 Updated\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(1, full ? stack.Resources.ResyncAll() : stack.Resources.Resync());
            Assert.False(stack.Resources.TryGetDialogPrebutton("d_reload", out _));
            Assert.True(stack.Resources.TryGetDialogLayout("d_reload", out _));
        }
        finally { File.Delete(path); }
    }
}
