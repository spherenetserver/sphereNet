using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Spell disturb parity (reference OnTakeDamage): the chance comes from the
/// spell's INTERRUPT curve at the caster's skill — not the damage amount —
/// and only players are disturbed.
/// </summary>
public class SpellInterruptTests
{
    private static (SpellEngine engine, Character caster, GameWorld world) CreateCastingCaster(
        int interruptBase, int interruptScale, bool isPlayer)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Clumsy,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 50, // long cast so IsCasting stays set
            InterruptBase = interruptBase,
            InterruptScale = interruptScale,
        });

        var caster = world.CreateCharacter();
        caster.IsPlayer = isPlayer;
        caster.MaxMana = 100;
        caster.Mana = 100;
        caster.MaxHits = 100;
        caster.Hits = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        if (isPlayer)
        {
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            caster.Equip(pack, Layer.Pack);
            var book = world.CreateItem();
            book.ItemType = ItemType.Spellbook;
            book.More1 = 1u << ((int)SpellType.Clumsy - 1);
            pack.AddItem(book);
        }

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Clumsy, target.Uid, target.Position) > 0);
        Assert.True(caster.IsCasting);
        return (engine, caster, world);
    }

    [Fact]
    public void InterruptZero_NeverDisturbs()
    {
        // INTERRUPT=0 → the cast cannot be disturbed by damage.
        var (engine, caster, _) = CreateCastingCaster(0, 0, isPlayer: true);
        // Force the base to a literal zero (the default would be 1000).
        for (int i = 0; i < 100; i++)
            Assert.False(engine.TryInterruptFromDamage(caster, 50));
        Assert.True(caster.IsCasting);
    }

    [Fact]
    public void InterruptFull_AlwaysDisturbs_RegardlessOfDamageAmount()
    {
        // INTERRUPT=100.0 (per-mille 1000) → even 1 damage disturbs.
        var (engine, caster, _) = CreateCastingCaster(1000, 0, isPlayer: true);
        Assert.True(engine.TryInterruptFromDamage(caster, 1));
        Assert.False(caster.IsCasting);
    }

    [Fact]
    public void NpcCasters_AreNotDisturbed()
    {
        var (engine, caster, _) = CreateCastingCaster(1000, 0, isPlayer: false);
        Assert.False(engine.TryInterruptFromDamage(caster, 99));
        Assert.True(caster.IsCasting);
    }
}
