using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using TriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TrySrvParityTests
{
    private sealed class PlayerConsole(IScriptObj character) : ITextConsole
    {
        public int ClientCommands;
        public PrivLevel GetPrivLevel() => PrivLevel.Player;
        public string GetName() => "player";
        public IScriptObj GetSourceChar() => character;
        public void SysMessage(string text) { }
        public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
        { ClientCommands++; return true; }
    }

    [Theory]
    [InlineData("native", "NAME=renamed")]
    [InlineData("interpreter", "NAME=renamed")]
    [InlineData("timer", "NAME=renamed")]
    [InlineData("native", "f_srv_probe=37")]
    [InlineData("interpreter", "f_srv_probe=37")]
    [InlineData("timer", "f_srv_probe=37")]
    public void ServerCallReachesPropertiesAndFunctionsWithoutPlayerContext(string route, string payload)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"trysrv-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[FUNCTION f_srv_probe]\nTAG.SOURCE=<SRC>\nTAG.VALUE=<ARGN1>\nRETURN 1\n");
        var previous = ObjBase.RunScriptFunction;
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            var character = world.CreateCharacter();
            var console = new PlayerConsole(character);
            var item = world.CreateItem();
            Assert.True(character.Equip(item, (Layer)30));
            ObjBase.RunScriptFunction = (target, verb, raw, source) =>
            {
                Assert.Equal(PrivLevel.Owner, source!.GetPrivLevel());
                Assert.Null(source.GetSourceChar());
                var args = new TriggerArgs { Source = source.GetSourceChar() };
                args.InitFromRaw(raw);
                return stack.Runner.TryRunFunction(verb, target, source, args, out _);
            };
            switch (route)
            {
                case "native": item.TryExecuteCommand("TRYSRV", payload, console); break;
                case "interpreter":
                    stack.Interpreter.Execute([new ScriptKey("TRYSRV", payload), new ScriptKey("TAG.AFTER", "<SRC>")], item, console,
                        new TriggerArgs(character), new ScriptScope());
                    Assert.True(item.TryGetTag("AFTER", out var after));
                    Assert.True(character.TryGetProperty("UID", out var uid));
                    Assert.Equal(uid, after);
                    break;
                case "timer":
                    world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, _ => console, console).Run;
                    Assert.True(item.TryExecuteCommand("TIMERF", $"0,TRYSRV {payload}", console));
                    TestHarness.PumpTimerF(world, Environment.TickCount64);
                    break;
            }
            Assert.Equal(0, console.ClientCommands);
            if (payload.StartsWith("NAME")) Assert.Equal("renamed", item.Name);
            else
            {
                Assert.True(item.TryGetTag("VALUE", out var value));
                Assert.Equal("37", value);
                Assert.True(item.TryGetTag("SOURCE", out var source));
                Assert.Equal("0", source);
            }
        }
        finally { ObjBase.RunScriptFunction = previous; File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerDialogNeverFallsBackToOriginalPlayerOrTargetClient(bool throughFunction)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"trysrv-dialog-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[FUNCTION f_srv_dialog]\nTAG.RAN=1\nDIALOG d_probe\nRETURN 1\n");
        try
        {
            stack.Resources.LoadResourceFile(path);
            var character = TestHarness.CreateWorld().CreateCharacter();
            var console = new PlayerConsole(character);
            var bridges = new List<string>();
            stack.Interpreter.ServerPropertyResolver = key => { bridges.Add(key); return null; };
            stack.Interpreter.Execute([new ScriptKey("TRYSRV", throughFunction ? "f_srv_dialog" : "DIALOG d_probe")],
                character, console, new TriggerArgs(character), new ScriptScope());
            Assert.Equal(0, console.ClientCommands);
            Assert.DoesNotContain(bridges, key => key.StartsWith("_REF_EXEC="));
            if (throughFunction) Assert.True(character.TryGetTag("RAN", out _));
        }
        finally { File.Delete(path); }
    }
}
