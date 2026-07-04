using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// NpcAI partial audit wave A1 (wiki/npcai-partial-audit.txt): the two P1s in
// the magery path — no fallback when the chosen spell is uncastable, and the
// melee-range behavior (unconditional kiting instead of the Source-X
// Tactics-gated stand-and-fight).
public class NpcAiWaveA1Tests
{
    private static (GameWorld World, NpcAI Ai, Character Caster, Character Victim) Setup(int victimX)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ai = new NpcAI(world, new SphereConfig());
        ai.OnNpcTickSpellCast = _ => false;

        var caster = world.CreateCharacter();
        caster.NpcBrain = NpcBrainType.Monster;
        caster.Hits = caster.MaxHits = 200;
        caster.Stam = caster.MaxStam = 100;
        caster.Mana = caster.MaxMana = 200;
        caster.Int = 50;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Hits = victim.MaxHits = 5000;
        world.PlaceCharacter(victim, new Point3D((short)victimX, 100, 0, 0));
        world.AddOnlinePlayer(victim);
        world.OnTick(); // activate the sector so the masterless caster acts

        caster.FightTarget = victim.Uid;
        return (world, ai, caster, victim);
    }

    [Fact]
    public void UncastableSpell_FallsBackToAnAffordableOne()
    {
        var (_, ai, caster, _) = Setup(victimX: 105);
        caster.NpcSpellAdd(SpellType.Flamestrike);
        caster.NpcSpellAdd(SpellType.MagicArrow);

        // The CANCAST-backed affordability check refuses Flamestrike (as a
        // too-low-Magery NPC would fail its per-spell skill requirement).
        Character.OnCanCastCheck = (_, id) => (SpellType)id != SpellType.Flamestrike;

        var cast = new List<SpellType>();
        ai.OnNpcCastSpell = (_, _, spell) => cast.Add(spell);

        for (int i = 0; i < 60 && cast.Count == 0; i++)
        {
            caster.NextNpcActionTime = 0;
            caster.NextAttackTime = 0;
            ai.OnTickAction(caster);
        }

        // Source-X NPC_FightCast loop: the tick is not wasted — the NPC falls
        // through to the affordable spell instead of casting nothing.
        Assert.NotEmpty(cast);
        Assert.DoesNotContain(SpellType.Flamestrike, cast);
        Assert.Contains(SpellType.MagicArrow, cast);
    }

    [Fact]
    public void MeleeRange_Tactician_SometimesStandsAndFights()
    {
        var (_, ai, caster, _) = Setup(victimX: 101); // adjacent
        caster.NpcSpellAdd(SpellType.EnergyBolt);
        caster.SetSkill(SkillType.Tactics, 1000); // Source-X gate: > 20.0

        bool meleeFired = false;
        ai.OnNpcAttack = (_, _, _) => meleeFired = true;
        ai.OnNpcCastSpell = (_, _, _) => { };

        for (int i = 0; i < 60 && !meleeFired; i++)
        {
            caster.NextNpcActionTime = 0;
            caster.NextAttackTime = 0;
            ai.OnTickAction(caster);
        }

        // The old code ALWAYS kited at melee range (never a swing); Source-X
        // stands and fights ~50% of the ticks when Tactics > 200.
        Assert.True(meleeFired, "the tactician caster never fell through to melee at range 1");
    }
}
