using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
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
public sealed class TrySrcParityTests
{
    private sealed class ServerConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    [Theory]
    [InlineData("native")]
    [InlineData("interpreter")]
    [InlineData("client")]
    [InlineData("timer")]
    public void SourceSwitchPreservesTargetAcrossEntryPoints(string route)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var selected = world.CreateCharacter();
        var original = world.CreateCharacter();
        var item = world.CreateItem();
        using var logs = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19443);
        TestHarness.AttachCharacter(client, original);
        string payload = $"0{selected.Uid.Value:X} TAG.MARK 1";
        switch (route)
        {
            case "native": item.TryExecuteCommand("TRYSRC", payload, client); break;
            case "interpreter":
                stack.Interpreter.Execute([new ScriptKey("TRYSRC", payload)], item, client,
                    new TriggerArgs(original), new ScriptScope());
                break;
            case "client": client.TryExecuteScriptCommand(item, "TRYSRC", payload, null); break;
            case "timer":
                world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, null, new ServerConsole()).Run;
                Assert.True(item.TryExecuteCommand("TIMERF", $"0,TRYSRC {payload}", client));
                TestHarness.PumpTimerF(world, Environment.TickCount64);
                break;
        }
        Assert.True(item.TryGetTag("MARK", out var mark));
        Assert.Equal("1", mark);
        Assert.False(selected.TryGetTag("MARK", out _));
        Assert.False(original.TryGetTag("MARK", out _));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("interpreter")]
    [InlineData("client")]
    [InlineData("timer")]
    public void DialogReachesSelectedClientWithOriginalSubject(string route)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"trysrc-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[DIALOG d_trysrc_probe]\n0,0\nDTEXT 10 10 0 <NAME>\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            using var logs = LoggerFactory.Create(_ => { });
            var accounts = new AccountManager(logs);
            var originalClient = TestHarness.CreateClient(logs, world, accounts, 19444);
            var selectedClient = TestHarness.CreateClient(logs, world, accounts, 19445);
            var original = world.CreateCharacter();
            var selected = world.CreateCharacter();
            TestHarness.AttachCharacter(originalClient, original);
            TestHarness.AttachCharacter(selectedClient, selected);
            selectedClient.SetEngines(commands: new CommandHandler { Resources = stack.Resources }, triggerDispatcher: stack.Dispatcher);
            ObjBase.ResolveClientConsole = ch => ch == selected ? selectedClient : originalClient;
            var item = world.CreateItem();
            string payload = $"0{selected.Uid.Value:X} DIALOG d_trysrc_probe";
            switch (route)
            {
                case "native": Assert.True(item.TryExecuteCommand("TRYSRC", payload, originalClient)); break;
                case "interpreter":
                    stack.Interpreter.Execute([new ScriptKey("TRYSRC", payload)], item, originalClient,
                        new TriggerArgs(original), new ScriptScope());
                    break;
                case "client": Assert.True(originalClient.TryExecuteScriptCommand(item, "TRYSRC", payload, null)); break;
                case "timer":
                    world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, null, new ServerConsole()).Run;
                    Assert.True(item.TryExecuteCommand("TIMERF", $"0,TRYSRC {payload}", originalClient));
                    TestHarness.PumpTimerF(world, Environment.TickCount64);
                    break;
            }
            Assert.Empty(TestHarness.GetQueuedPackets(originalClient.NetState));
            var packet = Assert.Single(TestHarness.GetQueuedPackets(selectedClient.NetState),
                p => p.Span[0] is 0xDD or 0xB0);
            Assert.Equal(item.Uid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.Span.Slice(3, 4)));
        }
        finally { ObjBase.ResolveClientConsole = null; File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineSourceKeepsCharacterAndPrivilegeForFunction(bool expression)
    {
        var world = TestHarness.CreateWorld();
        var selected = world.CreateCharacter();
        var item = world.CreateItem();
        var previous = ObjBase.RunScriptFunction;
        bool called = false;
        try
        {
            ObjBase.RunScriptFunction = (target, verb, args, console) =>
            {
                called = true;
                Assert.Same(item, target);
                Assert.Equal("f_probe", verb);
                Assert.Equal("37", args);
                Assert.Same(selected, console!.GetSourceChar());
                Assert.Equal(selected.PrivLevel, console.GetPrivLevel());
                Assert.NotEqual(PrivLevel.Admin, console.GetPrivLevel());
                return true;
            };
            string uid = expression ? $"({selected.Uid.Value} - 1) + 1" : $"0{selected.Uid.Value:X}";
            Assert.True(item.ExecuteVerbLine("TRYSRC", $"{uid} f_probe=37", new ServerConsole()));
            Assert.True(called);
            called = false;
            Assert.False(item.ExecuteVerbLine("TRYSRC", $"{uid} DIALOG d_unavailable", new ServerConsole()));
            Assert.False(called);
        }
        finally { ObjBase.RunScriptFunction = previous; }
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("item")]
    [InlineData("missing")]
    [InlineData("deleted")]
    public void InvalidSourceRefusesWithoutFunctionFallback(string kind)
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        var deleted = world.CreateCharacter();
        world.DeleteObject(deleted);
        string uid = kind switch { "zero" => "0", "item" => $"0{item.Uid.Value:X}",
            "deleted" => $"0{deleted.Uid.Value:X}", _ => "0FFFFFF" };
        var previous = ObjBase.RunScriptFunction;
        bool called = false;
        try
        {
            ObjBase.RunScriptFunction = (_, _, _, _) => { called = true; return true; };
            Assert.False(item.ExecuteVerbLine("TRYSRC", $"{uid} TAG.MARK 1", new ServerConsole()));
            Assert.False(item.TryGetTag("MARK", out _));
            Assert.False(called);
        }
        finally { ObjBase.RunScriptFunction = previous; }
    }
}
