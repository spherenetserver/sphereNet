using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogLayoutArgumentsTests
{
    [Theory]
    [InlineData("DIALOG", ",2,alpha,beta", "alpha,beta", "alpha", 2)]
    [InlineData("SDIALOG", ",2,alpha,beta", "alpha,beta", "alpha", 2)]
    [InlineData("DIALOG", " (1 + 1) alpha,beta", "alpha,beta", "alpha", 2)]
    [InlineData("DIALOG", ",2,42,99", "42,99", "42", 2)]
    [InlineData("DIALOG", ",2", "", "", 2)]
    [InlineData("DIALOG", "", "", "", 0)]
    public void ThirdArgumentReachesLayoutWithoutReplacingPage(string verb, string suffix, string raw, string first, int page)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-layout-args-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[DIALOG d_layout_args]\n0,0\nTAG.RAW=A<ARGS>B\nTAG.FIRST=A<ARGV[0]>B\nTAG.PAGE=<ARGN1>\nTAG.SUBJECT=<ARGO.UID>\nDTEXT 1 1 0 Test\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19509);
            var player = world.CreateCharacter();
            var subject = world.CreateItem();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(subject.TryExecuteCommand(verb, "d_layout_args" + suffix, client));
            Assert.True(subject.TryGetTag("RAW", out var actual));
            Assert.Equal("A" + raw + "B", actual);
            Assert.True(subject.TryGetTag("FIRST", out actual));
            Assert.Equal("A" + first + "B", actual);
            Assert.True(subject.TryGetTag("PAGE", out actual));
            Assert.Equal(page.ToString(), actual);
            Assert.True(subject.TryGetTag("SUBJECT", out actual));
            Assert.True(subject.TryGetProperty("UID", out var uid));
            Assert.Equal(uid, actual);
        }
        finally { File.Delete(path); }
    }
}
