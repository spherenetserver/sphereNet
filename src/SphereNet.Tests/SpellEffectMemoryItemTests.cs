using System;
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
/// Source-X parity: an active durational spell effect is mirrored by a worn
/// IT_SPELL memory item (CChar::Spell_Effect_Create) — visible to GM .edit,
/// hidden from the client, carrying the spell id/graphic/caster, and deleted
/// whenever the effect ends. The engine's active-effect list stays the
/// authority for stat deltas and expiry.
/// </summary>
public sealed class SpellEffectMemoryItemTests
{
    private const ushort NightSightRune = 0x1F14;

    private static (SpellEngine engine, Character caster) Setup(GameWorld world)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.NightSight,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 600,
            DurationScale = 600,
            RuneItemId = NightSightRune,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (engine, caster);
    }

    private static Item? SpellMemory(Character ch) =>
        ch.Memories.FirstOrDefault(m => m.ItemType == ItemType.Spell);

    [Fact]
    public void DurationalBuff_CreatesSpellMemoryItem_OnTarget()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = Setup(world);

        Assert.True(engine.CastStart(caster, SpellType.NightSight, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        var mem = SpellMemory(caster);
        Assert.NotNull(mem);
        // Source-X layout: rune graphic, IT_SPELL, MOREX = spell, LINK = caster,
        // hidden LAYER_SPECIAL, and NO MemoryType flag (so it is not persisted as
        // a MEMORY record — the SPELLEFFECT record is the persistence source).
        Assert.Equal(NightSightRune, mem!.BaseId);
        Assert.Equal((int)SpellType.NightSight, mem.MoreP.X);
        Assert.Equal(caster.Uid, mem.Link);
        Assert.Equal(Layer.Special, mem.EquipLayer);
        Assert.Equal(MemoryType.None, mem.GetMemoryTypes());
    }

    [Fact]
    public void SpellMemoryItem_Removed_WhenEffectExpires()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = Setup(world);

        engine.CastStart(caster, SpellType.NightSight, caster.Uid, caster.Position);
        engine.CastDone(caster);
        Assert.NotNull(SpellMemory(caster));

        // Drive the expiry pass well past the effect's lifetime.
        engine.ProcessExpirations(Environment.TickCount64 + 10_000_000);

        Assert.Null(SpellMemory(caster));
    }

    [Fact]
    public void SpellMemoryItem_Removed_WhenDispelled()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = Setup(world);

        engine.CastStart(caster, SpellType.NightSight, caster.Uid, caster.Position);
        engine.CastDone(caster);
        Assert.NotNull(SpellMemory(caster));

        engine.StripDispellableEffects(caster);

        Assert.Null(SpellMemory(caster));
    }

    [Fact]
    public void SpellMemoryItem_NotDuplicated_OnReCast()
    {
        var world = TestHarness.CreateWorld();
        var (engine, caster) = Setup(world);

        for (int i = 0; i < 3; i++)
        {
            engine.CastStart(caster, SpellType.NightSight, caster.Uid, caster.Position);
            engine.CastDone(caster);
        }

        // Re-casting refreshes the single effect; exactly one memory survives.
        Assert.Equal(1, caster.Memories.Count(m => m.ItemType == ItemType.Spell));
    }
}
