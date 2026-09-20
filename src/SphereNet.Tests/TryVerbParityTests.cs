using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;
using TriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TryVerbParityTests
{
    private sealed class CharacterConsole(Character source) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => source.PrivLevel;
        public string GetName() => source.Name;
        public IScriptObj GetSourceChar() => source;
        public void SysMessage(string text) { }
    }

    [Theory]
    [InlineData("native", " ", "37")]
    [InlineData("native", "=", "37")]
    [InlineData("native", ",", "37")]
    [InlineData("native", "\t", "37")]
    [InlineData("native", " ", "a=b")]
    [InlineData("interpreter", " ", "37")]
    [InlineData("interpreter", "=", "37")]
    [InlineData("interpreter", ",", "37")]
    [InlineData("interpreter", "\t", "37")]
    [InlineData("interpreter", " ", "a=b")]
    [InlineData("timer", " ", "37")]
    [InlineData("timer", "=", "37")]
    [InlineData("timer", ",", "37")]
    [InlineData("timer", "\t", "37")]
    [InlineData("timer", " ", "a=b")]
    public void TryDispatchesFunctionWithRawArgumentsAndOriginalSource(string route, string separator, string value)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"try-verb-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[FUNCTION f_try_capture]\nTAG.ARG=<ARGS>\nTAG.SOURCE=<SRC>\nRETURN 1\n");
        var previous = ObjBase.RunScriptFunction;
        try
        {
            stack.Resources.LoadResourceFile(path);
            var world = TestHarness.CreateWorld();
            var character = world.CreateCharacter();
            var item = world.CreateItem();
            Assert.True(character.Equip(item, (Layer)30));
            var console = new CharacterConsole(character);
            ObjBase.RunScriptFunction = (target, name, raw, source) =>
            {
                var args = new TriggerArgs { Source = source?.GetSourceChar() };
                args.InitFromRaw(raw);
                return stack.Runner.TryRunFunction(name, target, source, args, out _);
            };
            string payload = $"f_try_capture{separator}{value}";
            if (route == "native") item.TryExecuteCommand("TRY", payload, console);
            else if (route == "interpreter")
                stack.Interpreter.Execute([new ScriptKey("TRY", payload)], item, console,
                    new TriggerArgs(character), new ScriptScope());
            else
            {
                world.TimerFExpired = new DelayedCallDispatcher(() => stack.Runner, _ => console, ScriptServerConsole.Instance).Run;
                item.TryExecuteCommand("TIMERF", $"0,TRY {payload}", console);
                TestHarness.PumpTimerF(world, Environment.TickCount64);
            }
            Assert.True(item.TryGetTag("ARG", out var actual));
            Assert.Equal(value, actual);
            Assert.True(item.TryGetTag("SOURCE", out var sourceUid));
            Assert.True(character.TryGetProperty("UID", out var uid));
            Assert.Equal(uid, sourceUid);
        }
        finally { ObjBase.RunScriptFunction = previous; File.Delete(path); }
    }

    [Theory]
    [InlineData("UNKNOWN_COMMAND")]
    [InlineData("TRYSRC 0 NAME=wrong")]
    [InlineData("DIALOG d_unavailable")]
    public void RefusedPayloadIsSuppressedWithoutRunningTryShadow(string payload)
    {
        var item = TestHarness.CreateWorld().CreateItem();
        var previous = ObjBase.RunScriptFunction;
        var names = new List<string>();
        try
        {
            ObjBase.RunScriptFunction = (_, name, _, _) => { names.Add(name); return false; };
            Assert.True(item.ExecuteVerbLine("TRY", payload, ScriptServerConsole.Instance));
            Assert.DoesNotContain("TRY", names);
            if (payload != "UNKNOWN_COMMAND") Assert.Empty(names);
        }
        finally { ObjBase.RunScriptFunction = previous; }
    }
}
