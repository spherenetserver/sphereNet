using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 264 — AOS suit-property aggregation (max-pool slice). Equipped items
/// contribute BONUSHITSMAX/BONUSMANAMAX/BONUSSTAMMAX to the wearer's effective
/// max hit/mana/stamina pool, derived on read. The Max* getters report the
/// effective pool so display, heal ceilings and ratios stay consistent, while
/// the stored BASE field is what persists (never the inflated total), and
/// unequip clamps the current pool down to the reduced max (Source-X parity).
/// </summary>
public sealed class SourceXWave264Tests
{
    private static (GameWorld world, Character ch) Make()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 40; ch.Dex = 40; ch.Int = 40;
        ch.MaxHits = 100; ch.MaxMana = 50; ch.MaxStam = 60;
        ch.Hits = 100; ch.Mana = 50; ch.Stam = 60;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item Piece(GameWorld world, string prop, int value)
    {
        var piece = world.CreateItem();
        piece.ItemType = ItemType.Armor;
        piece.SetTag(prop, value.ToString());
        return piece;
    }

    [Fact]
    public void NoEquipment_EffectiveEqualsBase()
    {
        var (_, ch) = Make();
        Assert.Equal(100, ch.MaxHits);
        Assert.Equal(100, ch.BaseMaxHits);
        Assert.Equal(100, CombatEngine.EffectiveMaxHits(ch));
        Assert.Equal(50, ch.MaxMana);
        Assert.Equal(60, ch.MaxStam);
    }

    [Fact]
    public void BonusHitsMax_RaisesEffectiveMax_SumsAcrossItems()
    {
        var (world, ch) = Make();
        ch.Equip(Piece(world, "BONUSHITSMAX", 20), Layer.Helm);
        ch.Equip(Piece(world, "BONUSHITSMAX", 5), Layer.Gloves);

        Assert.Equal(125, ch.MaxHits);   // 100 + 20 + 5
        Assert.Equal(50, ch.MaxMana);    // untouched pool
        Assert.Equal(60, ch.MaxStam);
    }

    [Fact]
    public void Heal_FillsIntoBonusPool()
    {
        var (world, ch) = Make();
        ch.Equip(Piece(world, "BONUSHITSMAX", 20), Layer.Helm);

        // The current pool can now be healed up to the effective (suit) max.
        ch.Hits = 500; // over-set; clamps to effective ceiling
        Assert.Equal(120, ch.Hits);
    }

    [Fact]
    public void BaseField_StaysOnBase_ForPersistence()
    {
        var (world, ch) = Make();
        ch.Equip(Piece(world, "BONUSHITSMAX", 20), Layer.Helm);
        ch.Hits = 120;

        // The effective read is inflated, but the base field (what the world save
        // serializes) and the script property stay at the base value.
        Assert.Equal(120, ch.MaxHits);
        Assert.Equal(100, ch.BaseMaxHits);
        Assert.True(ch.TryGetProperty("MAXHITS", out string v));
        Assert.Equal("100", v);
    }

    [Fact]
    public void Unequip_ClampsCurrentDownToReducedMax()
    {
        var (world, ch) = Make();
        var helm = Piece(world, "BONUSHITSMAX", 20);
        ch.Equip(helm, Layer.Helm);
        ch.Hits = 120; // filled into the bonus pool
        Assert.Equal(120, ch.Hits);

        ch.Unequip(Layer.Helm);
        // Suit gone -> effective max back to 100 -> current truncated down.
        Assert.Equal(100, ch.MaxHits);
        Assert.Equal(100, ch.Hits);
    }

    [Fact]
    public void ManaAndStam_ParallelBonuses()
    {
        var (world, ch) = Make();
        ch.Equip(Piece(world, "BONUSMANAMAX", 30), Layer.Helm);
        ch.Equip(Piece(world, "BONUSSTAMMAX", 15), Layer.Gloves);

        Assert.Equal(80, ch.MaxMana);  // 50 + 30
        Assert.Equal(75, ch.MaxStam);  // 60 + 15
        ch.Mana = 999; Assert.Equal(80, ch.Mana);
        ch.Stam = 999; Assert.Equal(75, ch.Stam);
        Assert.Equal(50, ch.BaseMaxMana);
        Assert.Equal(60, ch.BaseMaxStam);
    }

    [Fact]
    public void MaxPoolBonus_DoesNotTouchStatsOrCarryWeight()
    {
        var (world, ch) = Make();
        int baseWeight = ch.MaxWeight;
        ch.Equip(Piece(world, "BONUSHITSMAX", 40), Layer.Helm);

        // A max-pool bonus is a separate pool, not a stat: STR and carry weight
        // are unaffected (only BONUSSTR would move those).
        Assert.Equal(40, ch.Str);
        Assert.Equal(40, CombatEngine.EffectiveStr(ch));
        Assert.Equal(baseWeight, ch.MaxWeight);
    }

    [Fact]
    public void NegativeBonus_LowersEffectiveMax_FlooredAtZero()
    {
        var (world, ch) = Make();
        ch.Equip(Piece(world, "BONUSHITSMAX", -30), Layer.Helm);
        Assert.Equal(70, ch.MaxHits); // 100 - 30

        ch.Equip(Piece(world, "BONUSHITSMAX", -500), Layer.Gloves);
        Assert.Equal(0, CombatEngine.EffectiveMaxHits(ch)); // floored, no negative
    }
}
