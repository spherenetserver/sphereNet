using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogCloseArgumentTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData(" 16", 16)]
    [InlineData(",16", 16)]
    [InlineData(" 010", 16)]
    [InlineData("\t010", 16)]
    [InlineData(", (8 + 8)", 16)]
    public void CloseUsesSameButtonForPacketAndScript(string suffix, int button)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-close-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, "[DIALOG d_close_probe]\n0,0\nDTEXT 10 10 0 Test\n[DIALOG d_close_probe BUTTON]\nON=0\nSRC.TAG.CLOSED=0\nON=16\nSRC.TAG.CLOSED=16\n");
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19503);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(player.TryExecuteCommand("DIALOG", "d_close_probe", client));
            Assert.True(player.TryExecuteCommand("DIALOGCLOSE", "d_close_probe" + suffix, client));
            Assert.False(client.IsScriptDialogOpen("d_close_probe"));
            Assert.True(player.TryGetTag("CLOSED", out var value));
            Assert.Equal(button.ToString(), value);
            var packet = Assert.Single(TestHarness.GetQueuedPackets(client.NetState),
                p => p.Span[0] == 0xBF && p.Length == 13 && p.Span[4] == 4);
            Assert.Equal((uint)button, BinaryPrimitives.ReadUInt32BigEndian(packet.Span.Slice(9, 4)));
        }
        finally { File.Delete(path); }
    }
}
