using System;
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
/// Upstream has one clock for an effect: CChar::Spell_Effect_Create equips a real
/// IT_SPELL item and the item's own timer IS the effect's (expiry, or the next tick
/// of a ticking spell). That is how the effect is kept here too, so a TIMER read or
/// write on the memory needs no bridge - it is the effect's timer - and a negative
/// TIMER turns it off (the TIMER=-1 "until cast again" memory, CCharSpell.cpp:2063).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellMemoryTimerTests
{
    private readonly ITestOutputHelper _out;
    private readonly SpellEngine _spells;
    private readonly GameWorld _world;
    private readonly Character _me;

    public SpellMemoryTimerTests(ITestOutputHelper output)
    {
        _out = output;
        _world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Invisibility, Name = "Invisibility", Flags = SpellFlag.TargChar | SpellFlag.Good,
            DurationBase = 300, DurationScale = 300,
        });
        _spells = new SpellEngine(_world, registry);

        _me = _world.CreateCharacter();
        _me.IsPlayer = true;
        _world.PlaceCharacter(_me, new Point3D(100, 100, 0, 0));
    }

    private Item CastInvisibility()
    {
        Character.MagicFlags = 0;
        _spells.ApplyDirectEffect(_me, _me, SpellType.Invisibility, 1000);
        return _me.FindLayer(SpellLayers.Invis)!;
    }

    [Fact]
    public void ReadingTimerAnswersWithTheEffectsRemainingTime()
    {
        var memory = CastInvisibility();
        Assert.True(memory.TryGetProperty("TIMER", out string value));
        _out.WriteLine($"TIMER = {value} (the effect has 30s left)");
        Assert.InRange(int.Parse(value), 28, 30);
    }

    [Fact]
    public void WritingTimerReArmsTheEffectItself()
    {
        var memory = CastInvisibility();
        Assert.True(memory.TryExecuteCommand("TIMER", "300", null!));

        long remaining = memory.Timeout - Environment.TickCount64;
        _out.WriteLine($"after TIMER 300 the effect has {remaining / 1000}s left");
        Assert.InRange(remaining, 298_000, 300_000);

        // ...and the effect really runs on it: 30 s later it is still on.
        _spells.ProcessExpirations(Environment.TickCount64 + 31_000);
        Assert.True(_me.IsStatFlag(StatFlag.Invisible));
        _spells.ProcessExpirations(Environment.TickCount64 + 301_000);
        Assert.False(_me.IsStatFlag(StatFlag.Invisible));
        Assert.True(memory.IsDeleted);
    }

    [Fact]
    public void ANegativeTimerMakesTheBuffPermanent()
    {
        var memory = CastInvisibility();
        Assert.True(memory.TryExecuteCommand("TIMER", "-1", null!));

        Assert.True(memory.TryGetProperty("TIMER", out string value));
        Assert.Equal("-1", value);
        _spells.ProcessExpirations(Environment.TickCount64 + 10_000_000);
        Assert.True(_me.IsStatFlag(StatFlag.Invisible));
        Assert.False(memory.IsDeleted);
    }

    [Fact]
    public void AMemoryWithNoEffectBehindItKeepsItsOwnTimer()
    {
        // An ordinary memory item is not a spell effect, and its TIMER is its own.
        var plain = _world.CreateItem();
        plain.BaseId = 0x2007;
        plain.ItemType = ItemType.EqMemoryObj;
        plain.SetTimeout(Environment.TickCount64 + 20_000);

        Assert.True(plain.TryGetProperty("TIMER", out string value));
        _out.WriteLine($"plain memory TIMER = {value}");
        Assert.InRange(int.Parse(value), 18, 20);
    }
}
