using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Reading and re-arming a buff through its spell-memory item.
///
/// A spell effect here is two things: the authoritative active-effect record, which
/// owns the expiry, and a memory ITEM equipped on the character that mirrors it for
/// scripts and for .edit. Upstream has no such split - CChar::Spell_Effect_Create
/// equips a real IT_SPELL item and the item's own timer IS the effect's - so a
/// TIMER read on the mirror answered with the mirror's permanent timeout, and a TIMER
/// WRITE set a field nothing ever consults: from the game it looked like the edit was
/// accepted and the buff ran exactly as long as before.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellMemoryTimerTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly SpellEngine _spells;
    private readonly GameWorld _world;
    private readonly Character _me;
    private readonly Item _memory;

    public SpellMemoryTimerTests(ITestOutputHelper output)
    {
        _out = output;
        _world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
        _spells = new SpellEngine(_world, new SpellRegistry());

        _me = _world.CreateCharacter();
        _me.IsPlayer = true;
        _world.PlaceCharacter(_me, new Point3D(100, 100, 0, 0));

        // The mirror as the memory state builds it, and an effect that owns it.
        _memory = _me.MemoryState.CreateSpellEffect(
            (int)SpellType.Invisibility, 0x2053, 10, _me.Uid, "invisibility");
        _spells.AddActiveEffectForTests(_me, SpellType.Invisibility, _memory,
            Environment.TickCount64 + 30_000);

    }

    /// <summary>The serialized-statics hook clears these between the constructor and
    /// the test body, so the wiring belongs in the test itself.</summary>
    private void Wire()
    {
        Character.SpellMemoryEffectRemaining = m => _spells.GetEffectRemainingMsByMemory(m);
        Character.SpellMemoryEffectRetimer = (m, ms) => _spells.TryRetimeEffectByMemory(m, ms);
    }

    public void Dispose()
    {
        Wire();
        Character.SpellMemoryEffectRemaining = null;
        Character.SpellMemoryEffectRetimer = null;
    }

    [Fact]
    public void ReadingTimerAnswersWithTheEffectsRemainingTime()
    {
        Wire();
        Assert.True(_memory.TryGetProperty("TIMER", out string value));
        _out.WriteLine($"TIMER = {value} (the effect has 30s left)");
        Assert.InRange(int.Parse(value), 28, 30);
    }

    [Fact]
    public void WritingTimerReArmsTheEffectItself()
    {
        Wire();
        Assert.True(_memory.TryExecuteCommand("TIMER", "300", null!));

        long remaining = _spells.GetEffectRemainingMsByMemory(_memory);
        _out.WriteLine($"after TIMER 300 the effect has {remaining / 1000}s left");
        Assert.InRange(remaining, 298_000, 300_000);

        // ...and reading it back agrees.
        Assert.True(_memory.TryGetProperty("TIMER", out string value));
        Assert.InRange(int.Parse(value), 298, 300);
    }

    [Fact]
    public void ANegativeTimerMakesTheBuffPermanent()
    {
        Wire();
        Assert.True(_memory.TryExecuteCommand("TIMER", "-1", null!));

        Assert.Equal(-1, _spells.GetEffectRemainingMsByMemory(_memory));
        Assert.True(_memory.TryGetProperty("TIMER", out string value));
        Assert.Equal("-1", value);
    }

    [Fact]
    public void AMemoryWithNoEffectBehindItKeepsItsOwnTimer()
    {
        Wire();
        // The fallback has to stay: an ordinary memory item is not a buff mirror, and
        // its TIMER is its own.
        var plain = _world.CreateItem();
        plain.BaseId = 0x2007;
        plain.ItemType = ItemType.EqMemoryObj;
        plain.SetTimeout(Environment.TickCount64 + 20_000);

        Assert.True(plain.TryGetProperty("TIMER", out string value));
        _out.WriteLine($"plain memory TIMER = {value}");
        Assert.InRange(int.Parse(value), 18, 20);
    }

    [Fact]
    public void ASpellMemoryThatLostItsEffectFallsBackToItsOwnTimer()
    {
        Wire();
        // A mirror whose effect is gone must not answer -1 forever: the bridge says
        // "no such effect" with a zero, and the item's own timer takes over.
        var orphan = _me.MemoryState.CreateSpellEffect(
            (int)SpellType.Bless, 0x2053, 1, _me.Uid, "bless");
        orphan.SetTimeout(Environment.TickCount64 + 5_000);

        Assert.Equal(0, _spells.GetEffectRemainingMsByMemory(orphan));
        Assert.True(orphan.TryGetProperty("TIMER", out string value));
        Assert.InRange(int.Parse(value), 3, 5);
    }
}
