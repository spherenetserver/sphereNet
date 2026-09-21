using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogUnsignedButtonTests
{
    [Theory]
    [InlineData(7u, false)]
    [InlineData(7u, true)]
    [InlineData(2147483647u, false)]
    [InlineData(2147483647u, true)]
    [InlineData(2147483648u, false)]
    [InlineData(2147483648u, true)]
    [InlineData(4294967295u, false)]
    [InlineData(4294967295u, true)]
    public void ResponseButtonWidensFromUnsignedPacketValue(uint buttonId, bool close)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-unsigned-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DIALOG d_unsigned]\n0,0\nDTEXT 1 1 0 Test\n[DIALOG d_unsigned PREBUTTON]\nTAG.PRE=<ARGN1>\n[DIALOG d_unsigned BUTTON]\nON={buttonId}\nTAG.N=<ARGN>\nTAG.N1=<ARGN1>\nARGN=-7\nTAG.MUTATED=<ARGN>\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19523);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_unsigned", 0, player));
            if (close) Assert.True(client.CloseScriptDialog("d_unsigned", unchecked((int)buttonId)));
            else client.HandleGumpResponse(player.Uid.Value, client.Gumps.OpenScriptDialogs["d_unsigned"], buttonId, [], []);
            foreach (string key in new[] { "PRE", "N", "N1" })
            {
                Assert.True(player.TryGetTag(key, out string? value));
                Assert.Equal(buttonId.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
            }
            Assert.True(player.TryGetTag("MUTATED", out string? mutated));
            Assert.Equal("-7", mutated);
        }
        finally { File.Delete(path); }
    }
}
