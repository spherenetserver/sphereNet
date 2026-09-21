using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogResponseArgumentTests
{
    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(1, 2, 9)]
    [InlineData(2, 2, -1)]
    [InlineData(3, 3, 9)]
    public void ResponseCountsAndFirstIdsPreservePacketOrder(int scenario, int count, int first)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-args-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, """
                [DIALOG d_args_probe]
                0,0
                DTEXT 10 10 0 Test
                [DIALOG d_args_probe BUTTON]
                ON=7
                TAG.COUNT=<ARGCHK>
                TAG.FIRST=<ARGCHKID>
                TAG.TEXTCOUNT=<ARGTXT>
                TAG.TEXT=A<ARGTXT[3]>B
                TAG.OUTSIDE=A<ARGTXT[65539]>B
                TAG.CHECK=<ARGCHK[9]>
                """);
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19506);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(client.TryShowScriptDialog("d_args_probe", 0, player));
            uint[] switches = scenario switch { 0 => [], 1 => [9, 2], 2 => [0, 9], _ => [9, 2, 9] };
            client.HandleGumpResponse(player.Uid.Value, client.Gumps.OpenScriptDialogs["d_args_probe"], 7,
                switches, scenario == 0 ? [] : [(3, "first"), (3, "second")]);
            void Tag(string key, string expected)
            {
                Assert.True(player.TryGetTag(key, out var actual));
                Assert.Equal(expected, actual);
            }
            Tag("COUNT", count.ToString());
            Tag("FIRST", first.ToString());
            Tag("TEXTCOUNT", scenario == 0 ? "0" : "2");
            Tag("TEXT", scenario == 0 ? "AB" : "AfirstB");
            Tag("OUTSIDE", "AB");
            Tag("CHECK", scenario == 0 ? "0" : "1");
        }
        finally { File.Delete(path); }
    }
}
