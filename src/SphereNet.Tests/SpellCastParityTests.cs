using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Spell cast parity helpers: the reference resist-chance formula, the
/// CAST_TIME curve and the spellbook bit-mask lookup.
/// </summary>
public class SpellCastParityTests
{
    [Fact]
    public void CalcResistChance_MatchesReferenceFormula()
    {
        // max(resist/50, resist - ((magery-200)/50 + (1 + spell/8)*50)) / 30
        Assert.Equal(29, SpellEngine.CalcResistChance(1000, 1000, 8));
        Assert.Equal(19, SpellEngine.CalcResistChance(1000, 1000, 57)); // high circle: second term shrinks
        Assert.Equal(0, SpellEngine.CalcResistChance(0, 1000, 1));
    }

    [Fact]
    public void GetCastTime_SingleValueIsConstant_TwoValuesInterpolate()
    {
        var constant = new SpellDef { Id = SpellType.Heal, CastTimeBase = 5 };
        Assert.Equal(5, constant.GetCastTime(0));
        Assert.Equal(5, constant.GetCastTime(1000));

        var curved = new SpellDef { Id = SpellType.Heal, CastTimeBase = 30, CastTimeScale = 15 };
        Assert.Equal(30, curved.GetCastTime(0));
        Assert.Equal(23, curved.GetCastTime(500));
        Assert.Equal(15, curved.GetCastTime(1000));
    }

    [Fact]
    public void HasSpellInBook_ReadsClassicBitMask()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var engine = new SpellEngine(world, new SpellRegistry());

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);

        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << 3; // spell 4 (Heal)
        pack.AddItem(book);

        Assert.True(engine.HasSpellInBook(ch, 4));
        Assert.False(engine.HasSpellInBook(ch, 5));

        book.More2 = 1u << (42 - 33); // spell 42 (EnergyBolt) in the high mask
        Assert.True(engine.HasSpellInBook(ch, 42));
    }
}
