using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 246 — Source-X CServerConfig m_iRegenRate: the REGENx rates are SECONDS
/// to recover one point (CServerConfig.cpp:1075 multiplies by MSECS_PER_SEC), in
/// STAT order REGEN0=STR/hits(40s), REGEN1=INT/mana(20s), REGEN2=DEX/stam(10s).
/// SphereNet previously read them as tenths of a second (~10x too fast) and had
/// mana/stam swapped. The human race also regens +2 extra hp (CCharStat.cpp:520).
/// </summary>
public sealed class SourceXRegenWave246Tests
{
    private static Character MakeWounded(ushort body)
    {
        var ch = new Character { BodyId = body, IsPlayer = true };
        ch.MaxHits = 100;
        ch.Hits = 50;
        ch.Food = 10;
        return ch;
    }

    [Fact]
    public void RegenSecondsToMs_InterpretsRateAsSeconds()
    {
        Assert.Equal(40000, Character.RegenSecondsToMs(40, 12345)); // 40s, not 4s
        Assert.Equal(1000, Character.RegenSecondsToMs(1, 12345));
        Assert.Equal(12345, Character.RegenSecondsToMs(0, 12345)); // unset → fallback
    }

    [Fact]
    public void RegenDefaults_MatchSourceXStatOrder()
    {
        Assert.Equal(40, Character.RegenHitsSeconds); // STAT_STR / hits
        Assert.Equal(20, Character.RegenManaSeconds); // STAT_INT / mana
        Assert.Equal(10, Character.RegenStamSeconds); // STAT_DEX / stam
    }

    [Fact]
    public void HitRegen_HumanGetsRacialBonus_GargoyleDoesNot()
    {
        // A fresh character's next-regen timer starts at 0, so the first OnTick
        // applies one regen event.
        var human = MakeWounded(0x0190);
        human.OnTick();
        Assert.Equal(53, human.Hits); // base 1 + human racial 2

        var gargoyle = MakeWounded(0x029A);
        gargoyle.OnTick();
        Assert.Equal(51, gargoyle.Hits); // base 1, no racial bonus
    }
}
