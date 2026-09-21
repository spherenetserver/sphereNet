using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogMutableArgumentsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ButtonArgumentsStartEmptyAndReflectScriptWrites(bool mutateInPrebutton, bool close)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-mutable-{Guid.NewGuid():N}.scp");
        try
        {
            const string mutate = "ARGS=alpha,beta\nARGN=42\n";
            File.WriteAllText(path, "[DIALOG d_mutable]\n0,0\nDTEXT 1 1 0 Test\n[DIALOG d_mutable PREBUTTON]\n" +
                "TAG.INIT_N=<ARGN>\nTAG.INIT_COUNT=<ARGV>\nTAG.INIT_RAW=A<ARGS>B\n" +
                (mutateInPrebutton ? mutate : "") +
                "[DIALOG d_mutable BUTTON]\nON=7\n" + (mutateInPrebutton ? "" : mutate) +
                "TAG.N=<ARGN>\nTAG.N1=<ARGN1>\nTAG.COUNT=<ARGV>\nTAG.RAW=<ARGS>\nTAG.FIRST=<ARGV[0]>\nTAG.CHECK=<ARGCHK[9]>\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19512);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_mutable", 0, player));
            if (close) Assert.True(client.CloseScriptDialog("d_mutable", 7));
            else client.HandleGumpResponse(player.Uid.Value, client.Gumps.OpenScriptDialogs["d_mutable"], 7, [9], []);
            void Tag(string key, string expected)
            {
                Assert.True(player.TryGetTag(key, out var actual));
                Assert.Equal(expected, actual);
            }
            Tag("INIT_N", "7");
            Tag("INIT_COUNT", "0");
            Tag("INIT_RAW", "AB");
            Tag("N", "42");
            Tag("N1", "42");
            Tag("COUNT", "2");
            Tag("RAW", "alpha,beta");
            Tag("FIRST", "alpha");
            Tag("CHECK", close ? "0" : "1");
        }
        finally { File.Delete(path); }
    }
}
