using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class NestedDialogContextTests
{
    [Theory]
    [InlineData("DIALOG", "normal")]
    [InlineData("DIALOG", "cancel")]
    [InlineData("DIALOG", "missing")]
    [InlineData("SDIALOG", "normal")]
    [InlineData("SDIALOG", "cancel")]
    [InlineData("SDIALOG", "missing")]
    public void OpeningFromButtonUsesNewLayoutContextThenRestoresResponse(string verb, string mode)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"nested-dialog-{Guid.NewGuid():N}.scp");
        try
        {
            string child = mode == "missing" ? "d_missing" : "d_child";
            File.WriteAllText(path, $"""
                [DIALOG d_parent]
                0,0
                DTEXT 1 1 0 Parent
                [DIALOG d_parent BUTTON]
                ON=7
                {verb} {child},2,alpha,beta
                TAG.PARENT_N=<ARGN>
                TAG.PARENT_TEXT=<ARGTXT[3]>
                TAG.PARENT_CHECK=<ARGCHK[9]>
                [DIALOG d_child]
                0,0
                TAG.CHILD_N=<ARGN>
                TAG.CHILD_N1=<ARGN1>
                TAG.CHILD_RAW=<ARGS>
                TAG.CHILD_FIRST=<ARGV[0]>
                DTEXT 1 1 0 Child
                RETURN {(mode == "cancel" ? 1 : 0)}
                """);
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19511);
            var player = world.CreateCharacter();
            var subject = world.CreateItem();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_parent", 0, subject));
            client.HandleGumpResponse(subject.Uid.Value, client.Gumps.OpenScriptDialogs["d_parent"], 7, [9], [(3, "original")]);
            void Tag(string key, string expected)
            {
                Assert.True(subject.TryGetTag(key, out var actual));
                Assert.Equal(expected, actual);
            }
            Tag("PARENT_N", "7");
            Tag("PARENT_TEXT", "original");
            Tag("PARENT_CHECK", "1");
            if (mode != "missing")
            {
                Tag("CHILD_N", "2");
                Tag("CHILD_N1", "2");
                Tag("CHILD_RAW", "alpha,beta");
                Tag("CHILD_FIRST", "alpha");
            }
            Assert.Equal(mode == "normal", client.IsScriptDialogOpen("d_child"));
            Assert.Equal(mode == "normal" ? 2 : 1, TestHarness.GetQueuedPackets(client.NetState).Count(p => p.Span[0] is 0xDD or 0xB0));
        }
        finally { File.Delete(path); }
    }
}
