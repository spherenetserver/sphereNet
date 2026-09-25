using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ScriptHookResyncTests : IDisposable
{
    private readonly ScriptRuntimeStack _stack = ScriptTestBootstrap.CreateRuntimeStack();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hooks-{Guid.NewGuid():N}.scp");
    private readonly Dictionary<FieldInfo, object?> _saved = new();
    private readonly NpcAI _npc;
    private readonly Character _character;

    public ScriptHookResyncTests()
    {
        var world = TestHarness.CreateWorld();
        _character = world.CreateCharacter();
        _npc = new NpcAI(world, new SphereConfig());
        SetServer("_triggerDispatcher", _stack.Dispatcher);
        SetServer("_npcAI", _npc);
        SetServer("_world", world);
        File.WriteAllText(_path, "[EVENTS e_probe]\n");
        _stack.Resources.LoadResourceFile(_path);
        Refresh();
    }

    private void SetServer(string name, object value)
    {
        var field = typeof(SphereNet.Server.Program).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        _saved.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void Refresh() => typeof(SphereNet.Server.Program)
        .GetMethod("RefreshScriptTriggerHooks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

    private void Reload(string script)
    {
        File.WriteAllText(_path, script);
        Assert.Equal(1, _stack.Resources.ResyncAll());
        ScriptTestBootstrap.LoadDefinitions(_stack.Resources);
        Refresh();
    }

    public void Dispose()
    {
        foreach (var (field, value) in _saved) field.SetValue(null, value);
        File.Delete(_path);
        _stack.LoggerFactory.Dispose();
    }

    [Theory]
    [InlineData("NotoSend", typeof(Character), "OnNotoSend")]
    [InlineData("EffectAdd", typeof(Character), "OnEffectAdd")]
    [InlineData("Reveal", typeof(Character), "OnRevealing")]
    [InlineData("SpellEffectAdd", typeof(Character), "OnSpellEffectAdd")]
    [InlineData("SpellEffectRemove", typeof(Character), "OnSpellEffectRemove")]
    [InlineData("SpellEffectTick", typeof(Character), "OnSpellEffectTick")]
    [InlineData("MemoryEquip", typeof(Character), "OnMemoryEquip")]
    [InlineData("SkillUseQuick", typeof(Character), "OnSkillUseQuickDetailed")]
    [InlineData("NPCSeeNewPlayer", typeof(Character), "OnNpcSeeNewPlayer")]
    [InlineData("SkillGain", typeof(SkillEngine), "OnSkillGainCheck")]
    [InlineData("SkillChange", typeof(Character), "OnSkillChange")]
    [InlineData("NPCLookAtChar", typeof(NpcAI), "OnNpcLookAtChar")]
    [InlineData("NPCActFight", typeof(NpcAI), "OnNpcActFight")]
    [InlineData("NPCActWander", typeof(NpcAI), "OnNpcActWander")]
    [InlineData("NPCActFollow", typeof(NpcAI), "OnNpcActFollow")]
    [InlineData("NPCActCast", typeof(NpcAI), "OnNpcActCast")]
    [InlineData("NPCLookAtItem", typeof(NpcAI), "OnNpcLookAtItem")]
    [InlineData("NPCSeeWantItem", typeof(NpcAI), "OnNpcSeeWantItem")]
    [InlineData("NPCAction", typeof(NpcAI), "OnNpcAction")]
    public void ResyncAddsAndRemovesRuntimeGates(string trigger, Type owner, string property)
    {
        var hook = owner.GetProperty(property)!;
        object? instance = owner == typeof(NpcAI) ? _npc : null;
        Assert.Null(hook.GetValue(instance));
        Reload($"[EVENTS e_probe]\nON=@{trigger}\nRETURN 1\n");
        Assert.NotNull(hook.GetValue(instance));
        Reload("[EVENTS e_probe]\n");
        Assert.Null(hook.GetValue(instance));
    }

    [Fact]
    public void ReloadedNpcHandlerUsesNewBodyAndThenStops()
    {
        Reload("[EVENTS e_probe]\nON=@NPCActWander\nTAG.visited=1\nRETURN 1\n");
        _character.Events.Add(_stack.Resources.ResolveDefName("e_probe"));
        Assert.True(_npc.OnNpcActWander!(_character));
        Reload("[EVENTS e_probe]\nON=@NPCActWander\nTAG.visited=2\nRETURN 0\n");
        Assert.False(_npc.OnNpcActWander!(_character));
        _character.TryGetProperty("TAG.visited", out var value);
        Assert.Equal("2", value);
        Reload("[EVENTS e_probe]\n");
        Assert.Null(_npc.OnNpcActWander);
    }

    [Fact]
    public void SpellResourceEffectTickWorksWithoutCharacterHook()
    {
        Reload("[SPELL 20]\nON=@EffectTick\nLOCAL.EFFECT=7\nLOCAL.CHARGES=2\nRETURN 0\n");
        Assert.False(_stack.Dispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectTick));
        Assert.NotNull(Character.OnSpellEffectTick);
        var context = new SpellEffectTickContext { SpellId = 20, Damage = 1, Charges = 4 };
        Assert.True(Character.OnSpellEffectTick!(_character, context));
        Assert.Equal(7, context.Damage);
        Assert.Equal(2, context.Charges);
    }

    // Source-X reads ARGN2 back as the level after @EffectTick (CCharSpell.cpp:2011);
    // the pack caps a lethal poison with ARGN2=3.
    [Fact]
    public void SpellResourceEffectTickReadsTheLevelBack()
    {
        Reload("[SPELL 20]\nON=@EffectTick\nIF (<ARGN2> >= 4)\nARGN2=3\nENDIF\n");
        var context = new SpellEffectTickContext { SpellId = 20, Strength = 4, Damage = 1, Charges = 4 };
        Assert.True(Character.OnSpellEffectTick!(_character, context));
        Assert.Equal(3, context.Strength);
    }

    // [SPELL 20] @EffectAdd for the poison memory (SetPoison -> LayerAdd ->
    // Spell_Effect_Add): ARGO is the memory, SRC the poisoner; RETURN 1 means no
    // poison at all.
    [Fact]
    public void PoisonMemoryRunsTheSpellEffectAddStage()
    {
        var world = (SphereNet.Game.World.GameWorld)typeof(SphereNet.Server.Program)
            .GetField("_world", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        world.PlaceCharacter(_character, new SphereNet.Core.Types.Point3D(100, 100, 0, 0));
        var poisoner = world.CreateCharacter();
        world.PlaceCharacter(poisoner, new SphereNet.Core.Types.Point3D(101, 100, 0, 0));

        Reload("[SPELL 20]\nON=@EffectAdd\nARGO.TAG.OVERRIDE.CUREPOISONCHANCE=100\nTAG.FX_SRC=<SRC.UID>\n");
        Assert.NotNull(CharacterPoisonState.OnSpellEffectAdd);
        Assert.True(_character.SetPoison(500, 5, poisoner));
        var memory = _character.GetEquippedItem(Layer.FlagPoison);
        Assert.NotNull(memory);
        Assert.True(memory!.TryGetTag("OVERRIDE.CUREPOISONCHANCE", out var chance) && chance == "100");
        Assert.True(_character.TryGetTag("FX_SRC", out var src) &&
            SphereNet.Core.Types.ScriptNumber.TryParseToken(src!, out long srcUid) &&
            (uint)srcUid == poisoner.Uid.Value);

        _character.Poison.Cure(false);
        Reload("[SPELL 20]\nON=@EffectAdd\nRETURN 1\n");
        Assert.False(_character.SetPoison(500, 5, poisoner));
        Assert.Null(_character.GetEquippedItem(Layer.FlagPoison));

        Reload("[EVENTS e_probe]\n");
        Assert.Null(CharacterPoisonState.OnSpellEffectAdd);
    }

    [Theory]
    [InlineData("Gain", "OnSkillGainCheck")]
    [InlineData("UseQuick", "OnSkillUseQuickDetailed")]
    public void SkillResourceStageEnablesItsRuntimeCallback(string stage, string property)
    {
        Reload($"[SKILL 0]\nON=@{stage}\nRETURN 1\n");
        var owner = stage == "Gain" ? typeof(SkillEngine) : typeof(Character);
        Assert.NotNull(owner.GetProperty(property)!.GetValue(null));
        if (stage == "Gain")
        {
            int chance = 1, cap = 1000;
            Assert.True(SkillEngine.OnSkillGainCheck!(_character, (SkillType)0, ref chance, ref cap));
        }
        else
        {
            int difficulty = 1;
            // RETURN 1 answers "success, no experience" (CCharSkill.cpp:578-579).
            Assert.Equal(SkillEngine.UseQuickHandledSuccess,
                Character.OnSkillUseQuickDetailed!(_character, 0, ref difficulty, 1));
        }
    }
}
