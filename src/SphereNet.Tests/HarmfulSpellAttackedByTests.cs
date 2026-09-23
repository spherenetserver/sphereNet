using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: a player cast Magic Arrow - and Clumsy - on a grey NPC and it never
/// fought back. Upstream runs OnAttackedBy for every harmful spell that lands,
/// damage or not (CCharSpell.cpp:3777 -> CCharFight.cpp:329): the victim remembers
/// the caster as HARMEDBY (AGGREIVED when struck first), puts them on its attacker
/// list, and an NPC turns on them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HarmfulSpellAttackedByTests
{
    private static (SpellEngine, Character caster, Character npc) Setup(SpellType spell, SpellFlag flags)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef { Id = spell, Flags = flags, DurationBase = 100 });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;     // a grey townsfolk
        npc.Hits = npc.MaxHits = 50;
        world.PlaceCharacter(npc, new Point3D(103, 100, 0, 0));
        return (engine, caster, npc);
    }

    [Fact]
    public void ACurseWithNoDamageStillMakesTheNpcFightBack()
    {
        var (engine, caster, npc) = Setup(SpellType.Clumsy,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse);
        int woken = 0;
        Character.WakeNpc = _ => woken++;

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);

        Assert.Equal(caster.Uid, npc.FightTarget);
        Assert.True(npc.Attacker_GetIndex(caster.Uid) >= 0);
        Assert.NotNull(npc.Memory_FindObjTypes(caster.Uid, MemoryType.HarmedBy | MemoryType.Aggreived));
        Assert.Equal(1, woken);
    }

    [Fact]
    public void ABeneficialSpellIsNotAnAttack()
    {
        var (engine, caster, npc) = Setup(SpellType.Agility,
            SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless);

        engine.ApplyDirectEffect(caster, npc, SpellType.Agility, 500);

        Assert.False(npc.FightTarget.IsValid);
        Assert.True(npc.Attacker_GetIndex(caster.Uid) < 0);
    }

    [Fact]
    public void AnNpcAlreadyFightingSomeoneKeepsItsTarget()
    {
        var (engine, caster, npc) = Setup(SpellType.Clumsy,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse);
        var other = new Serial(0x1234);
        npc.FightTarget = other;

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);

        Assert.Equal(other, npc.FightTarget);
        Assert.True(npc.Attacker_GetIndex(caster.Uid) >= 0);   // but it knows
    }
}
