using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 259 — Necromancy Vengeful Spirit: summon a revenant (CREID_REVENANT
/// 0x2EE) controlled by the caster that hunts the target, reusing the existing
/// summon ownership/duration machinery (hardened to apply a body + Conjured).
/// </summary>
public sealed class SourceXWave259Tests
{
    [Fact]
    public void VengefulSpirit_SummonsRevenant_OwnedByCaster_HuntingTheTarget()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.VengefulSpirit,
            Flags = SpellFlag.TargChar, // routes to ApplySpecificSpell (no Summon flag)
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 1200,
            DurationScale = 1200,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var enemy = world.CreateCharacter();
        enemy.MaxHits = 100; enemy.Hits = 100;
        world.PlaceCharacter(enemy, new Point3D(105, 100, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.VengefulSpirit, enemy.Uid, enemy.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        var revenant = world.GetCharsInRange(caster.Position, 8)
            .FirstOrDefault(c => c.BodyId == 0x02EE);

        Assert.NotNull(revenant);
        Assert.Equal(caster.Uid, revenant!.OwnerSerial);
        Assert.True(revenant.IsSummoned);
        Assert.True(revenant.IsStatFlag(StatFlag.Conjured));
        Assert.Equal(enemy.Uid, revenant.FightTarget);
        Assert.Equal(NpcBrainType.Monster, revenant.NpcBrain);
    }
}
