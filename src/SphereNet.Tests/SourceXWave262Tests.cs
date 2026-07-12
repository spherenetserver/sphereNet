using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 262 — AOS suit-property aggregation (elemental resist slice). Equipped
/// items contribute their resist bonuses to the wearer's effective resist,
/// derived on read (base field + equipped-item sum), so combat and the status
/// display reflect the suit without any equip-time mutation.
/// </summary>
public sealed class SourceXWave262Tests
{
    private static (GameWorld world, Character ch) Make()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item ResistPiece(GameWorld world, string prop, int value)
    {
        var piece = world.CreateItem();
        piece.ItemType = ItemType.Armor;
        piece.SetTag(prop, value.ToString());
        return piece;
    }

    [Fact]
    public void EffectiveResist_NoEquipment_EqualsBase()
    {
        var (_, ch) = Make();
        ch.ResFire = 20;
        Assert.Equal(20, CombatEngine.EffResFire(ch));
    }

    [Fact]
    public void EffectiveResist_AddsEquippedItemBonuses()
    {
        var (world, ch) = Make();
        ch.ResFire = 20;
        ch.Equip(ResistPiece(world, "RESFIRE", 30), Layer.Helm);
        ch.Equip(ResistPiece(world, "RESFIRE", 15), Layer.Gloves);

        Assert.Equal(65, CombatEngine.EffResFire(ch)); // 20 + 30 + 15
        Assert.Equal(0, CombatEngine.EffResCold(ch));  // untouched element
    }

    [Fact]
    public void EffectiveResist_ClampedToHundred()
    {
        var (world, ch) = Make();
        ch.ResPoison = 90;
        ch.Equip(ResistPiece(world, "RESPOISON", 40), Layer.Chest);
        Assert.Equal(100, CombatEngine.EffResPoison(ch)); // 130 -> clamped
    }

    [Fact]
    public void EquippedResist_ReducesElementalDamage()
    {
        var (world, ch) = Make();
        ch.ResFire = 0;
        // Base takes full fire damage.
        Assert.Equal(100, CombatEngine.ApplyElementalResist(ch, 100, DamageType.Fire));

        // A 50% fire-resist piece halves it, purely from the suit.
        ch.Equip(ResistPiece(world, "RESFIRE", 50), Layer.Chest);
        Assert.Equal(50, CombatEngine.ApplyElementalResist(ch, 100, DamageType.Fire));
    }

    [Fact]
    public void BaseResistFieldAndScriptGetter_StayOnBase()
    {
        var (world, ch) = Make();
        ch.ResFire = 20;
        ch.Equip(ResistPiece(world, "RESFIRE", 30), Layer.Helm);

        // The stored base field / script property is unchanged (no equip-time
        // mutation); only the effective read includes the suit.
        Assert.Equal(20, ch.ResFire);
        Assert.True(ch.TryGetProperty("RESFIRE", out string v));
        Assert.Equal("20", v);
        Assert.Equal(50, CombatEngine.EffResFire(ch));
    }
}
