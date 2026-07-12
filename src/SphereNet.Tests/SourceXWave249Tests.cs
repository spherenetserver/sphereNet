using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 249 — CChar/CItem script-property parity: STAMINA alias, STATPERCENT,
/// BLOODCOLOR, FOLLOWERSLOTS (char) and BASEWEIGHT (item). Fills script-surface
/// gaps that mortechUO/Source-X scripts rely on.
/// </summary>
public sealed class SourceXWave249Tests
{
    private static string Get(Character ch, string key)
    {
        ch.TryGetProperty(key, out string value);
        return value;
    }

    [Fact]
    public void Stamina_IsAliasOfStam()
    {
        var ch = new Character { BodyId = 0x0190 };
        ch.MaxStam = 90;
        ch.TrySetProperty("STAMINA", "42");
        Assert.Equal("42", Get(ch, "STAM"));
        Assert.Equal("42", Get(ch, "STAMINA"));

        ch.TrySetProperty("STAM", "17");
        Assert.Equal("17", Get(ch, "STAMINA"));
    }

    [Fact]
    public void StatPercent_ReportsPoolAsPercentOfMax()
    {
        var ch = new Character { BodyId = 0x0190 };
        ch.MaxHits = 200; ch.Hits = 100;   // 50%
        ch.MaxStam = 80; ch.Stam = 20;     // 25%
        ch.MaxMana = 50; ch.Mana = 45;     // 90%

        Assert.Equal("50", Get(ch, "STATPERCENT.STR"));  // Sphere STR→hits
        Assert.Equal("25", Get(ch, "STATPERCENT.DEX"));  // DEX→stam
        Assert.Equal("90", Get(ch, "STATPERCENT.INT"));  // INT→mana
        Assert.Equal("50", Get(ch, "STATPERCENT.HITS")); // convenience alias
    }

    [Fact]
    public void StatPercent_ZeroMax_IsZero()
    {
        var ch = new Character { BodyId = 0x0190 };
        ch.MaxMana = 0; ch.Mana = 0;
        Assert.Equal("0", Get(ch, "STATPERCENT.INT"));
    }

    [Fact]
    public void BloodColor_RoundTripsAsHex()
    {
        var ch = new Character { BodyId = 0x0190 };
        Assert.Equal("00", Get(ch, "BLOODCOLOR")); // default 0

        ch.TrySetProperty("BLOODCOLOR", "0x1A2");
        Assert.Equal((ushort)0x1A2, ch.BloodHue);
        Assert.Equal("01A2", Get(ch, "BLOODCOLOR")); // Source-X FormatHex: "0" + hex
    }

    [Fact]
    public void FollowerSlots_DefaultsToOne_AndOverrideWins()
    {
        var ch = new Character { BodyId = 0x0190 };
        Assert.Equal("1", Get(ch, "FOLLOWERSLOTS")); // no chardef → 1
        Assert.Equal(1, ch.ControlSlots);

        ch.TrySetProperty("FOLLOWERSLOTS", "5");
        Assert.Equal("5", Get(ch, "FOLLOWERSLOTS"));
        Assert.Equal(5, ch.ControlSlots); // the override drives real slot accounting
        Assert.Equal(5, ch.FollowerSlotsOverride);
    }

    [Fact]
    public void BaseWeight_OverridesDefWeight_AndDrivesTotalWeight()
    {
        var item = new Item { BaseId = 0x0F5E };
        item.TrySetProperty("BASEWEIGHT", "50"); // 50 tenths = 5 stones/unit
        Assert.Equal(50, item.Weight);
        item.TryGetProperty("BASEWEIGHT", out string bw);
        Assert.Equal("50", bw);

        // A negative/blank set clears the override.
        item.TrySetProperty("BASEWEIGHT", "-1");
        Assert.Null(item.WeightOverride);
    }
}
