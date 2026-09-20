using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ScriptPhaseReferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericReferenceReadsTheExistingObjectThroughTheServer(bool arithmetic)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        var item = world.CreateItem();
        item.Name = "reference target";
        var program = typeof(SphereNet.Server.Program);
        var field = program.GetField("_world", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var resolver = program.GetMethod("HandleRefGet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, world);
            stack.Interpreter.ServerPropertyResolver = p => p.StartsWith("_REF_GET=")
                ? (string?)resolver.Invoke(null, [p[9..]]) : null;
            string value = arithmetic ? $"{item.Uid.Value - 1}+1" : item.Uid.Value.ToString();
            stack.Interpreter.Execute([
                new ScriptKey("REF100", value),
                new ScriptKey("TAG.RESULT", "<REF100.NAME>")], ch, null, new TriggerArgs(ch), new ScriptScope());
            Assert.True(ch.TryGetTag("RESULT", out var name));
            Assert.Equal(item.Name, name);
            // Source-X resolves REF assignments immediately: a removed object
            // cannot be assigned as a new nonzero reference.
            var scope = new ScriptScope();
            stack.Interpreter.Execute([new ScriptKey("REF100", value)], ch, null, new TriggerArgs(ch), scope);
            world.DeleteObject(item);
            stack.Interpreter.Execute([
                new ScriptKey("REF100", value),
                new ScriptKey("TAG.GONE", "<REF100>"),
                new ScriptKey("TAG.GONE_NAME", "<REF100.NAME>")], ch, null, new TriggerArgs(ch), scope);
            ch.TryGetTag("GONE", out var gone);
            ch.TryGetTag("GONE_NAME", out var goneName);
            Assert.Equal("0", gone);
            Assert.Equal("0", goneName);
        }
        finally { field.SetValue(null, previous); }
    }

    [Theory]
    [InlineData("1073741825", "040000001")]
    [InlineData("040000000+1", "040000001")]
    [InlineData("0", "0")]
    public void RefAssignmentEvaluatesItsNumericExpression(string expression, string expected)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var ch = TestHarness.CreateWorld().CreateCharacter();
        var scope = new ScriptScope();
        stack.Interpreter.Execute([new ScriptKey("REF100", expression)], ch, null, new TriggerArgs(ch), scope);
        Assert.Equal(expected, scope.GetRef(100));
    }

    [Fact]
    public void ReferencedFunctionKeepsTargetAndSourceSeparate()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        var item = world.CreateItem();
        using var logs = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19439);
        TestHarness.AttachCharacter(client, ch);
        stack.Interpreter.ResolveObjectRef = (_, head) => head == $"UID.0{item.Uid.Value:X}" ? item : null;
        bool called = false;
        stack.Interpreter.CallFunction = (name, target, console, args) =>
        {
            Assert.Equal("F_PROBE", name);
            Assert.Same(item, target);
            Assert.Same(client, console);
            Assert.Same(ch, args!.Source);
            called = true;
            return SphereNet.Core.Enums.TriggerResult.True;
        };
        stack.Interpreter.Execute([
            new ScriptKey("REF1", $"0{item.Uid.Value:X}"),
            new ScriptKey("REF1.F_PROBE", "17")], ch, client, new TriggerArgs(ch), new ScriptScope());
        Assert.True(called);
    }

    [Fact]
    public void ReferencedEquipKeepsTheOriginalSource()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        var item = world.CreateItem();
        using var logs = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19438);
        TestHarness.AttachCharacter(client, ch);
        stack.Interpreter.ResolveObjectRef = (_, head) => head == $"UID.0{item.Uid.Value:X}" ? item : null;
        var previous = Character.ScriptEquipItem;
        bool called = false;
        try
        {
            Character.ScriptEquipItem = (source, target) =>
            {
                Assert.Same(ch, source);
                Assert.Same(item, target);
                called = true;
                return true;
            };
            stack.Interpreter.Execute([
                new ScriptKey("REF1", $"0{item.Uid.Value:X}"),
                new ScriptKey("REF1.EQUIP", ""),
                new ScriptKey("REF1.NAME", "memory")], ch, client, new TriggerArgs(ch), new ScriptScope());
            Assert.True(called);
            Assert.Equal("memory", item.Name);
        }
        finally { Character.ScriptEquipItem = previous; }
    }
}
