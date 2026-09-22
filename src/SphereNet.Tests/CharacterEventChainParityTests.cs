using SphereNet.Game.Objects.Characters;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class CharacterEventChainParityTests
{
    private static ScriptRuntimeStack Load(string script)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"char-chain-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        return stack;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameEventAcrossCharacterStagesRunsOncePerDispatch(bool player)
    {
        var stack = Load("""
            [EVENTS e_count]
            ON=@Probe
            TAG.count=<EVAL <TAG0.count>+1>
            RETURN 0
            [CHARDEF 0190]
            DEFNAME=c_chain
            TEVENTS=e_count
            """);
        var ch = TestHarness.CreateWorld().CreateCharacter();
        ch.BodyId = 0x190;
        ch.CharDefIndex = 0x190;
        ch.IsPlayer = player;
        var eventId = stack.Resources.ResolveDefName("e_count");
        ch.Events.Add(eventId);
        (player ? stack.Dispatcher.GlobalPlayerEvents : stack.Dispatcher.GlobalPetEvents).Add(eventId);
        for (int count = 1; count <= 2; count++)
        {
            stack.Dispatcher.FireCharTriggerByName(ch, "Probe", new GameArgs());
            ch.TryGetProperty("TAG.count", out var value);
            Assert.Equal(count.ToString(), value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterDefinitionStagesApplyOnlyToNpcs(bool player)
    {
        var stack = Load("""
            [EVENTS e_type]
            ON=@Probe
            TAG.type=1
            RETURN 0
            [CHARDEF 0190]
            DEFNAME=c_chain
            TEVENTS=e_type
            ON=@Probe
            TAG.body=1
            RETURN 0
            """);
        var ch = TestHarness.CreateWorld().CreateCharacter();
        ch.BodyId = 0x190;
        ch.CharDefIndex = 0x190;
        ch.IsPlayer = player;
        stack.Dispatcher.FireCharTriggerByName(ch, "Probe", new GameArgs());
        ch.TryGetProperty("TAG0.type", out var type);
        ch.TryGetProperty("TAG0.body", out var body);
        Assert.Equal(player ? "0" : "1", type);
        Assert.Equal(player ? "0" : "1", body);
    }

    [Fact]
    public void RemovingLaterEventDoesNotExecuteCurrentCharacterEventAgain()
    {
        var stack = Load("""
            [EVENTS e_first]
            ON=@Probe
            TAG.count=<EVAL <TAG0.count>+1>
            EVENTS=-e_second
            RETURN 0
            [EVENTS e_second]
            ON=@Probe
            TAG.removed=1
            RETURN 0
            [EVENTS e_last]
            ON=@Probe
            TAG.last=1
            RETURN 0
            """);
        var ch = TestHarness.CreateWorld().CreateCharacter();
        foreach (string name in new[] { "e_first", "e_second", "e_last" })
            ch.Events.Add(stack.Resources.ResolveDefName(name));
        stack.Dispatcher.FireCharTriggerByName(ch, "Probe", new GameArgs());
        ch.TryGetProperty("TAG.count", out var count);
        ch.TryGetProperty("TAG0.removed", out var removed);
        ch.TryGetProperty("TAG.last", out var last);
        Assert.Equal("1", count);
        Assert.Equal("0", removed);
        Assert.Equal("1", last);
    }
}
