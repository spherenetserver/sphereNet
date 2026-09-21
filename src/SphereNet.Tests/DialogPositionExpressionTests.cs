using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class DialogPositionExpressionTests
{
    [Theory]
    [InlineData("10,20", 10, 20)]
    [InlineData("010,020", 16, 32)]
    [InlineData("position_x,position_y", 16, 32)]
    [InlineData("(10 + 5),[20 + 6]", 15, 26)]
    [InlineData("10+5 20+6", 15, 26)]
    [InlineData("010 = 020", 16, 32)]
    [InlineData("010", 16, 0)]
    [InlineData(",020", 0, 32)]
    [InlineData("<TAG.X>,<ARGN1>", 16, 2)]
    [InlineData("TAG.HEADER_EXECUTED=1", 0, 1)]
    public void PositionUsesSphereExpressionsAndIsConsumedBeforeLayout(string position, int x, int y)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-position-{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[DEFNAME position_test]\nposition_x=010\nposition_y=020\n[DIALOG d_position_test]\n{position}\nTAG.LAYOUT_EXECUTED=1\nPAGE 0\nDTEXT 1 1 0 Position\n");
            stack.Resources.LoadResourceFile(path);
            using var logs = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19520);
            var player = world.CreateCharacter();
            var subject = world.CreateItem();
            subject.SetTag("X", "16");
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            Assert.True(subject.TryExecuteCommand("DIALOG", "d_position_test,2", client));
            byte[] packet = TestHarness.GetQueuedPackets(client.NetState).Last(p => p.Span[0] is 0xDD or 0xB0).Span.ToArray();
            Assert.Equal(x, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(11, 4)));
            Assert.Equal(y, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(15, 4)));
            Assert.False(subject.TryGetTag("HEADER_EXECUTED", out _));
            Assert.True(subject.TryGetTag("LAYOUT_EXECUTED", out string? value));
            Assert.Equal("1", value);
        }
        finally { File.Delete(path); }
    }
}
