using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// [SPELL n] @EffectAdd against Source-X CChar::Spell_Effect_Add
/// (CCharSpell.cpp:1000-1014): ARGO = the spell memory, SRC = the caster,
/// RETURN 1 deletes the memory (no effect), RETURN 0 keeps the memory but skips
/// the engine's own effect.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellEffectAddStageTests
{
    private static (SpellEngine Engine, GameWorld World, Character Caster, Character Target, ScriptRuntimeStack Stack, string Path)
        Setup(string stageBody)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fxadd-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, "[SPELL 16]\nNAME=Strength\nON=@EffectAdd\n" + stageBody + "\n");
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Strength,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 1,
            EffectBase = 50,
            EffectScale = 50,
            DurationBase = 600,
            DurationScale = 600,
        });
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = stack.Dispatcher };

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Str = 30;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (engine, world, caster, target, stack, path);
    }

    // A spell memory lives on its wearer's memory layer, not in the world index.
    private static Item? FindMemory(Character target, uint uid) =>
        target.Memories.FirstOrDefault(m => m.Uid.Value == uid);

    [Fact]
    public void TheStageSeesTheMemoryAsArgoAndTheCasterAsSrc()
    {
        var (engine, world, caster, target, _, path) = Setup("""
            TAG.FX_SRC=<SRC.UID>
            TAG.FX_ARGO=<ARGO.UID>
            ARGO.TAG.OVERRIDE.MARK=100
            """);
        try
        {
            caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));

            Assert.True(target.TryGetTag("FX_SRC", out var src));
            Assert.True(ScriptNumber.TryParseToken(src!, out long srcUid));
            Assert.Equal(caster.Uid.Value, (uint)srcUid);

            Assert.True(target.TryGetTag("FX_ARGO", out var argo));
            Assert.True(ScriptNumber.TryParseToken(argo!, out long memUid));
            var memory = FindMemory(target, (uint)memUid);
            Assert.NotNull(memory);
            Assert.Equal(ItemType.Spell, memory!.ItemType);
            Assert.True(memory.TryGetTag("OVERRIDE.MARK", out var mark) && mark == "100");
            Assert.True(target.Str > 30); // RETURN nothing: the buff applies
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnOneRefusesTheEffectAndDeletesTheMemory()
    {
        var (engine, world, caster, target, _, path) = Setup("TAG.FX_ARGO=<ARGO.UID>\nRETURN 1");
        try
        {
            caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));

            Assert.Equal(30, target.Str);
            Assert.True(target.TryGetTag("FX_ARGO", out var argo));
            Assert.True(ScriptNumber.TryParseToken(argo!, out long memUid));
            var memory = FindMemory(target, (uint)memUid);
            Assert.True(memory == null || memory.IsDeleted);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnZeroKeepsTheMemoryButNotTheNativeEffect()
    {
        var (engine, world, caster, target, _, path) = Setup("TAG.FX_ARGO=<ARGO.UID>\nRETURN 0");
        try
        {
            caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));

            Assert.Equal(30, target.Str);
            Assert.True(target.TryGetTag("FX_ARGO", out var argo));
            Assert.True(ScriptNumber.TryParseToken(argo!, out long memUid));
            var memory = FindMemory(target, (uint)memUid);
            Assert.NotNull(memory);
            Assert.False(memory!.IsDeleted);

            // Ending the effect must not take back what was never given.
            engine.StripDispellableEffects(target);
            Assert.Equal(30, target.Str);
        }
        finally { File.Delete(path); }
    }
}
