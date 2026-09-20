using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class NewItemEquipTests
{
    [Theory]
    [InlineData("")]
    [InlineData("<SRC>")]
    [InlineData("0deadbeef")]
    public void NewEquipPreservesSourceAndReferenceForFollowingProperties(string argument)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var world = TestHarness.CreateWorld();
        var wearer = world.CreateCharacter();
        var memory = world.CreateItem();
        memory.EquipLayer = (Layer)30;
        using var logs = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19437);
        TestHarness.AttachCharacter(client, wearer);
        stack.Interpreter.ResolveObjectRef = (_, head) => head == "NEW" ? memory : null;
        bool equipped = false;
        var previous = Character.ScriptEquipItem;
        try
        {
            Character.ScriptEquipItem = (ch, item) =>
            {
                Assert.Same(wearer, ch);
                Assert.Same(memory, item);
                equipped = ch.Equip(item, item.EquipLayer);
                // The @Equip initialization used by the stuck memory.
                stack.Interpreter.Execute([
                    new ScriptKey("MORE1", "15"), new ScriptKey("MORE2", "1"),
                    new ScriptKey("TIMER", "1")], item, client, new TriggerArgs(ch), new ScriptScope());
                return equipped;
            };
            stack.Interpreter.Execute([
                new ScriptKey("NEW.MOREP", "1496,1629,10,0"),
                new ScriptKey("NEW.EQUIP", argument),
                new ScriptKey("NEW.COLOR", "044")], wearer, client, new TriggerArgs(wearer), new ScriptScope());
            Assert.True(equipped);
            Assert.Same(memory, wearer.GetEquippedItem((Layer)30));
            Assert.Equal((ushort)0x44, memory.Hue.Value);
            Assert.True(memory.TryGetProperty("MORE2", out var stage));
            Assert.Equal("01", stage);
            Assert.True(memory.TryGetProperty("MOREP", out var point));
            Assert.Equal("1496,1629,10,0", point);
            // Source-X refuses item EQUIP without a source character.
            Assert.False(memory.TryExecuteCommand("EQUIP", "", new ServerConsole()));
        }
        finally { Character.ScriptEquipItem = previous; }
    }

    private sealed class ServerConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }
}
