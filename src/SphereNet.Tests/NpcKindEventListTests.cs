using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// Sphere 56T custom version's per-kind NPC event lists (sphere.ini EventsNpc,
/// EventsMonster, EventsAnimal, EventsMountable, EventsShop): every NPC runs the NPC
/// list, and only the kinds a list names run that list. Players run none of them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcKindEventListTests
{
    private static ScriptRuntimeStack Load()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"npc-kind-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [EVENTS e_k_npc]
            ON=@Probe
            TAG.npc=1
            [EVENTS e_k_monster]
            ON=@Probe
            TAG.monster=1
            [EVENTS e_k_animal]
            ON=@Probe
            TAG.animal=1
            [EVENTS e_k_mount]
            ON=@Probe
            TAG.mount=1
            [EVENTS e_k_shop]
            ON=@Probe
            TAG.shop=1
            """);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        var d = stack.Dispatcher;
        d.GlobalNpcEvents.Add(stack.Resources.ResolveDefName("e_k_npc"));
        d.GlobalMonsterEvents.Add(stack.Resources.ResolveDefName("e_k_monster"));
        d.GlobalAnimalEvents.Add(stack.Resources.ResolveDefName("e_k_animal"));
        d.GlobalMountableEvents.Add(stack.Resources.ResolveDefName("e_k_mount"));
        d.GlobalShopEvents.Add(stack.Resources.ResolveDefName("e_k_shop"));
        return stack;
    }

    private static string Fired(ScriptRuntimeStack stack, Character ch)
    {
        stack.Dispatcher.FireCharTriggerByName(ch, "Probe", new GameArgs());
        return string.Join(",", new[] { "npc", "monster", "animal", "mount", "shop" }
            .Where(k => ch.TagValue(k) == "1"));
    }

    [Theory]
    [InlineData(NpcBrainType.Monster, (ushort)0x0E, "npc,monster")]
    [InlineData(NpcBrainType.Dragon, (ushort)0x3B, "npc,monster")]
    [InlineData(NpcBrainType.Animal, (ushort)0xC8, "npc,animal,mount")]   // horse
    [InlineData(NpcBrainType.Vendor, (ushort)0x190, "npc,shop")]
    [InlineData(NpcBrainType.Human, (ushort)0x190, "npc")]
    public void EachNpcRunsTheListsOfItsKind(NpcBrainType brain, ushort body, string expected)
    {
        var stack = Load();
        var npc = TestHarness.CreateWorld().CreateCharacter();
        npc.NpcBrain = brain;
        npc.BodyId = body;
        Assert.Equal(expected, Fired(stack, npc));
    }

    [Fact]
    public void APlayerRunsNoNpcList()
    {
        var stack = Load();
        var player = TestHarness.CreateWorld().CreateCharacter();
        player.IsPlayer = true;
        player.NpcBrain = NpcBrainType.Monster;
        Assert.Equal("", Fired(stack, player));
    }
}
