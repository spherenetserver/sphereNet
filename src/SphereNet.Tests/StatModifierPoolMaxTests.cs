using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A pool maximum nobody set follows the ADJUSTED stat (base + modifier): Source-X
/// Stat_GetMax returns Stat_GetAdjusted when m_max is unset (CCharStat.cpp:278-287).
/// Spell stat buffs live in MODSTR/MODDEX/MODINT, so a Strength buff must still raise
/// max hits, and its removal lower them again.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StatModifierPoolMaxTests
{
    private static Character Char()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 40; ch.Int = 30;
        ch.Hits = 50; ch.Stam = 40; ch.Mana = 30;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void AModifierMovesAnUnsetMaximum_AndItsRemovalTrimsTheCurrentPool()
    {
        var ch = Char();

        ch.ModStr = 20; ch.ModDex = 10; ch.ModInt = 5;
        Assert.Equal(70, ch.MaxHits);
        Assert.Equal(50, ch.MaxStam);
        Assert.Equal(35, ch.MaxMana);

        ch.Hits = 70;
        ch.ModStr = 0;
        Assert.Equal(50, ch.MaxHits);
        Assert.Equal(50, ch.Hits);
    }

    [Fact]
    public void AnExplicitMaximumIsNotMovedByTheStat()
    {
        var ch = Char();
        ch.MaxHits = 200;

        ch.ModStr = 20;
        ch.Str = 60;

        Assert.Equal(200, ch.MaxHits);
    }

    [Fact]
    public void ABaseChangeUnderAModifierKeepsFollowingTheAdjustedStat()
    {
        var ch = Char();
        ch.ModStr = 20;      // max 70

        ch.Str = 60;         // adjusted 80

        Assert.Equal(80, ch.MaxHits);
    }
}
