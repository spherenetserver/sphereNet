using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Scripting;
using SphereNet.Game.Speech;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using TriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SingleDialogParityTests
{
    [Theory]
    [InlineData("native", "SDIALOG", 1)]
    [InlineData("interpreter", "SDIALOG", 1)]
    [InlineData("timer", "SDIALOG", 1)]
    [InlineData("native", "DIALOG", 2)]
    [InlineData("interpreter", "DIALOG", 2)]
    [InlineData("timer", "DIALOG", 2)]
    public void SingleDialogDoesNotRenderAgainOrReplaceSubject(string route, string verb, int expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteFixture();
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19448);
            var player = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            ObjBase.ResolveClientConsole = ch => ch == player ? client : null;
            world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, null, ScriptServerConsole.Instance).Run;
            var first = world.CreateItem();
            var second = world.CreateItem();
            void Open(ObjBase subject, string dialog)
            {
                if (route == "native") subject.TryExecuteCommand(verb, dialog, client);
                else if (route == "interpreter")
                    stack.Interpreter.Execute([new ScriptKey(verb, dialog)], subject, client,
                        new TriggerArgs(player), new ScriptScope());
                else
                {
                    subject.TryExecuteCommand("TIMERF", $"0,TRYSRC 0{player.Uid.Value:X} {verb} {dialog}", client);
                    TestHarness.PumpTimerF(world, Environment.TickCount64);
                }
            }
            Open(first, "d_single_probe");
            // Case and page changes must not bypass the single-dialog guard.
            Open(second, "D_SINGLE_PROBE, 2");
            var packets = TestHarness.GetQueuedPackets(client.NetState).Where(p => p.Span[0] is 0xDD or 0xB0).ToArray();
            Assert.Equal(expected, packets.Length);
            Assert.True(first.TryGetTag("RENDERED", out _));
            Assert.Equal(verb == "DIALOG", second.TryGetTag("RENDERED", out _));
            Assert.True(client.CloseScriptDialog("d_single_probe"));
            Assert.Equal(verb == "SDIALOG", first.TryGetTag("CLOSED", out _));
            Assert.Equal(verb == "DIALOG", second.TryGetTag("CLOSED", out _));
        }
        finally { ObjBase.ResolveClientConsole = null; File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedSingleDialogCanReopenAndOtherClientIsIndependent(bool clientResponse)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteFixture();
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var accounts = new AccountManager(logs);
            var client = TestHarness.CreateClient(logs, world, accounts, 19449);
            var otherClient = TestHarness.CreateClient(logs, world, accounts, 19450);
            var player = world.CreateCharacter();
            var other = world.CreateCharacter();
            TestHarness.AttachCharacter(client, player);
            TestHarness.AttachCharacter(otherClient, other);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            otherClient.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            player.TryExecuteCommand("SDIALOG", "d_single_probe", client);
            other.TryExecuteCommand("SDIALOG", "d_single_probe", otherClient);
            Assert.Single(TestHarness.GetQueuedPackets(otherClient.NetState), p => p.Span[0] is 0xDD or 0xB0);
            if (clientResponse)
            {
                var packet = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] is 0xDD or 0xB0);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(packet.Span.Slice(7, 4));
                client.HandleGumpResponse(player.Uid.Value, id, 0, [], []);
            }
            else Assert.True(client.CloseScriptDialog("d_single_probe"));
            Assert.False(client.IsScriptDialogOpen("d_single_probe"));
            Assert.True(otherClient.IsScriptDialogOpen("d_single_probe"));
            player.TryExecuteCommand("SDIALOG", "d_single_probe", client);
            Assert.True(client.IsScriptDialogOpen("d_single_probe"));
            Assert.Equal(2, TestHarness.GetQueuedPackets(client.NetState).Count(p => p.Span[0] is 0xDD or 0xB0));
        }
        finally { File.Delete(path); }
    }

    private static string WriteFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"single-dialog-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[DIALOG d_single_probe]\n0,0\nTAG.RENDERED=1\nDTEXT 10 10 0 <NAME>\n[DIALOG d_single_probe BUTTON]\nON=0\nTAG.CLOSED=1\n");
        return path;
    }
}
