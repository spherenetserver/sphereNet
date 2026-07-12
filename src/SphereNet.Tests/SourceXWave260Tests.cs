using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 260 — Necromancy Animate Dead: target a corpse item and raise an undead
/// controlled by the caster (a humanoid corpse raises a zombie; a creature corpse
/// raises its own kind). Reuses the summon ownership machinery via a corpse-item
/// target path in CastDone.
/// </summary>
public sealed class SourceXWave260Tests
{
    private static (GameWorld world, SpellEngine engine, Character caster) Setup()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.AnimateDeadAOS,
            Flags = SpellFlag.TargItem,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 1200,
            DurationScale = 1200,
        });
        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, new SpellEngine(world, registry), caster);
    }

    private static Item MakeCorpse(GameWorld world, ushort body, int x, int y)
    {
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Amount = body; // corpse stores the original creature body
        world.PlaceItem(corpse, new Point3D((short)x, (short)y, 0, 0));
        return corpse;
    }

    [Fact]
    public void AnimateDead_HumanoidCorpse_RaisesZombie_OwnedByCaster()
    {
        var (world, engine, caster) = Setup();
        var corpse = MakeCorpse(world, 0x0190, 101, 100); // human male

        bool corpseConsumed = false;
        engine.OnItemRemoved = it => { if (it == corpse) corpseConsumed = true; };

        Assert.True(engine.CastStart(caster, SpellType.AnimateDeadAOS, corpse.Uid, corpse.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        var zombie = world.GetCharsInRange(corpse.Position, 4)
            .FirstOrDefault(c => c.BodyId == 0x0003);

        Assert.NotNull(zombie);
        Assert.Equal(caster.Uid, zombie!.OwnerSerial);
        Assert.True(zombie.IsSummoned);
        Assert.True(zombie.IsStatFlag(StatFlag.Conjured));
        Assert.True(corpseConsumed);
    }

    [Fact]
    public void AnimateDead_CreatureCorpse_RaisesItsOwnKind()
    {
        var (world, engine, caster) = Setup();
        var corpse = MakeCorpse(world, 0x0001, 101, 100); // an ogre-ish creature body

        Assert.True(engine.CastStart(caster, SpellType.AnimateDeadAOS, corpse.Uid, corpse.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        var undead = world.GetCharsInRange(corpse.Position, 4)
            .FirstOrDefault(c => c.BodyId == 0x0001);
        Assert.NotNull(undead);
        Assert.Equal(caster.Uid, undead!.OwnerSerial);
    }

    [Fact]
    public void AnimateDead_NonCorpseTarget_RaisesNothing()
    {
        var (world, engine, caster) = Setup();
        var crate = world.CreateItem();
        crate.ItemType = ItemType.Container;
        world.PlaceItem(crate, new Point3D(101, 100, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.AnimateDeadAOS, crate.Uid, crate.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.DoesNotContain(world.GetCharsInRange(crate.Position, 4), c => c != caster);
    }
}
