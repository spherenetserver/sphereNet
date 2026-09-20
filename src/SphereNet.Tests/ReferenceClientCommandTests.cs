using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Scripting;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ReferenceClientCommandTests
{
    private sealed class ServerConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    [Theory]
    [InlineData("client")]
    [InlineData("server")]
    [InlineData("none")]
    public void InterpreterDialogUsesSourceClientRatherThanTargetClient(string sourceKind)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"dialog-source-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[DIALOG d_source_probe]\n0,0\nDTEXT 10 10 0 <NAME>\n[FUNCTION DIALOG]\nTAG.SHADOW=1\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var accounts = new AccountManager(logs);
            var sourceClient = TestHarness.CreateClient(logs, world, accounts, 19446);
            var targetClient = TestHarness.CreateClient(logs, world, accounts, 19447);
            var source = world.CreateCharacter();
            var target = world.CreateCharacter();
            TestHarness.AttachCharacter(sourceClient, source);
            TestHarness.AttachCharacter(targetClient, target);
            sourceClient.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            ITextConsole? console = sourceKind switch { "client" => sourceClient, "server" => ScriptServerConsole.Instance, _ => null };
            stack.Interpreter.Execute([new SphereNet.Scripting.Parsing.ScriptKey("DIALOG", "d_source_probe")],
                target, console, null, new SphereNet.Scripting.Execution.ScriptScope());
            Assert.False(target.TryGetTag("SHADOW", out _));
            Assert.Empty(TestHarness.GetQueuedPackets(targetClient.NetState));
            var packets = TestHarness.GetQueuedPackets(sourceClient.NetState).Where(p => p.Span[0] is 0xDD or 0xB0).ToArray();
            if (sourceKind == "client")
                Assert.Equal(target.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(Assert.Single(packets).Span.Slice(3, 4)));
            else Assert.Empty(packets);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("DIALOG", "online")]
    [InlineData("SDIALOG", "online")]
    [InlineData("DIALOG", "offline")]
    [InlineData("SDIALOG", "offline")]
    [InlineData("DIALOG", "ground")]
    [InlineData("SDIALOG", "ground")]
    [InlineData("DIALOGCLOSE", "online")]
    [InlineData("DIALOGCLOSE", "offline")]
    [InlineData("DIALOGCLOSE", "ground")]
    public void DelayedDialogUsesCurrentOwnerClientAndNeverFallsThroughToFunction(string verb, string location)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"timer-dialog-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[DIALOG d_timer_probe]\n0,0\nDTEXT 10 10 0 <NAME>\n[FUNCTION DIALOG]\nTAG.SHADOW=1\n[FUNCTION SDIALOG]\nTAG.SHADOW=1\n[FUNCTION DIALOGCLOSE]\nTAG.SHADOW=1\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19442);
            var owner = world.CreateCharacter();
            TestHarness.AttachCharacter(client, owner);
            client.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            var item = world.CreateItem();
            item.Name = "timer subject";
            // Scheduled on the ground, carried before execution: SRC must be
            // selected when the timer fires, not captured when it is created.
            Assert.True(item.TryExecuteCommand("TIMERF", $"0,{verb} d_timer_probe", new ServerConsole()));
            if (location != "ground") Assert.True(owner.Equip(item, (Layer)30));
            var dispatcher = new DelayedCallDispatcher(() => stack.Runner,
                character => location == "online" && character == owner ? client : null, new ServerConsole());

            if (verb == "DIALOGCLOSE" && location == "online")
            {
                Assert.True(item.TryExecuteCommand("DIALOG", "d_timer_probe", client));
                Assert.True(client.IsScriptDialogOpen("d_timer_probe"));
            }
            world.TimerFExpired = dispatcher.Run;
            TestHarness.PumpTimerF(world, Environment.TickCount64);

            Assert.False(item.TryGetTag("SHADOW", out _));
            var packets = TestHarness.GetQueuedPackets(client.NetState)
                .Where(p => p.Span[0] is 0xDD or 0xB0).ToArray();
            if (location == "online")
                Assert.Equal(item.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(Assert.Single(packets).Span.Slice(3, 4)));
            else
                Assert.Empty(packets);
            if (verb == "DIALOGCLOSE") Assert.False(client.IsScriptDialogOpen("d_timer_probe"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("LINK", true)]
    [InlineData("CONT", true)]
    [InlineData("ACT", true)]
    [InlineData("LINK", false)]
    [InlineData("CONT", false)]
    [InlineData("ACT", false)]
    public void ChainedDialogUsesSourceClientAndReferencedSubject(string head, bool valid)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"ref-dialog-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[DIALOG d_ref_probe]\n0,0\nDTEXT 10 10 0 <NAME>\n[DIALOG d_ref_probe BUTTON]\nON=0\nTAG.CLOSED=1\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var accounts = new AccountManager(logs);
            var sourceClient = TestHarness.CreateClient(logs, world, accounts, 19440);
            var subjectClient = TestHarness.CreateClient(logs, world, accounts, 19441);
            var source = world.CreateCharacter();
            var subject = world.CreateCharacter();
            subject.Name = "dialog subject";
            TestHarness.AttachCharacter(sourceClient, source);
            TestHarness.AttachCharacter(subjectClient, subject);
            sourceClient.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            var carrier = world.CreateItem();
            ObjBase target = head == "ACT" ? source : carrier;
            if (valid)
            {
                if (head == "LINK") carrier.Link = subject.Uid;
                else if (head == "CONT") Assert.True(subject.Equip(carrier, (Layer)30));
                else source.TrySetProperty("ACT", $"0{subject.Uid.Value:X}");
            }
            target.TryExecuteCommand($"{head}.DIALOG", "d_ref_probe", sourceClient);
            var packets = TestHarness.GetQueuedPackets(sourceClient.NetState)
                .Where(p => p.Span[0] is 0xDD or 0xB0).ToArray();
            Assert.Empty(TestHarness.GetQueuedPackets(subjectClient.NetState));
            if (!valid) Assert.Empty(packets);
            else
            {
                var packet = Assert.Single(packets);
                Assert.Equal(subject.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(packet.Span.Slice(3, 4)));
                Assert.True(sourceClient.CloseScriptDialog("d_ref_probe"));
                Assert.True(subject.TryGetTag("CLOSED", out var closed));
                Assert.Equal("1", closed);
                Assert.False(source.TryGetTag("CLOSED", out _));
            }
        }
        finally { File.Delete(path); }
    }
}
