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
    public void AnNpcAlreadyFightingSomeoneMostlyKeepsItsTarget()
    {
        // OnHarmedBy (CCharFight.cpp:300-307): an NPC fighting someone who still
        // exists stays on them unless g_Rand.Get16ValFast(10) comes up 0 - it turns
        // on the new attacker one time in ten. (This test used to pin "never
        // switches", with a fight target that did not even exist - for which
        // Source-X switches every time, see below.)
        var (engine, caster, npc) = Setup(SpellType.Clumsy,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse);
        var foe = CreateFoe(npc);
        npc.FightTarget = foe.Uid;

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);
        Assert.True(npc.Attacker_GetIndex(caster.Uid) >= 0);   // it knows either way

        int switched = 0;
        const int Trials = 400;
        for (int i = 0; i < Trials; i++)
        {
            npc.FightTarget = foe.Uid;
            npc.OnAttackedBy(caster);
            if (npc.FightTarget == caster.Uid) switched++;
            else Assert.Equal(foe.Uid, npc.FightTarget);
        }
        // Expected ~40 (1 in 10); the bounds are many standard deviations wide.
        Assert.InRange(switched, 5, 110);
    }

    [Fact]
    public void AnNpcWhoseFightTargetIsGoneTurnsOnTheAttacker()
    {
        // m_Fight_Targ_UID.CharFind() == nullptr: no fight to keep (CCharFight.cpp:300).
        var (engine, caster, npc) = Setup(SpellType.Clumsy,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse);
        npc.FightTarget = new Serial(0x1234); // no such character

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);

        Assert.Equal(caster.Uid, npc.FightTarget);
    }

    private static Character CreateFoe(Character npc)
    {
        var world = (SphereNet.Game.World.GameWorld)SphereNet.Game.Objects.ObjBase.ResolveWorld!()!;
        var foe = world.CreateCharacter();
        foe.Hits = foe.MaxHits = 50;
        world.PlaceCharacter(foe, new Point3D((short)(npc.X + 1), npc.Y, 0, 0));
        return foe;
    }
}
