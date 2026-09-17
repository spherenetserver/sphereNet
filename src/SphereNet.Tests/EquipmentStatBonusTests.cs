using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The stat and pool bonuses a piece of equipment grants
/// (Source-X CCPropsItemEquippable BONUSSTR / BONUSDEX / BONUSINT and the three
/// max-pool bonuses).
///
/// CombatEngine already summed every one of them off the worn items on each read -
/// EffectiveStr and its neighbours - so the aggregation was there and working.
/// What was missing was any way to SET one: a script or an ITEMDEF writing
/// BonusStr=5 had the line consumed and discarded, so the only gear that could
/// carry a bonus was gear whose bonus was hardcoded somewhere else.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EquipmentStatBonusTests
{
    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Theory]
    [InlineData("BONUSSTR")]
    [InlineData("BONUSDEX")]
    [InlineData("BONUSINT")]
    [InlineData("BONUSHITSMAX")]
    [InlineData("BONUSMANAMAX")]
    [InlineData("BONUSSTAMMAX")]
    public void ABonusRoundTripsOnTheItem(string name)
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        Assert.True(item.TryGetProperty(name, out string unset));
        Assert.Equal("0", unset);          // the default the reference answers

        Assert.True(item.TrySetProperty(name, "7"), $"item refused {name}");
        Assert.True(item.TryGetProperty(name, out string set));
        Assert.Equal("7", set);
    }

    /// <summary>And what the script set is what the wearer gets. The bonus lands on
    /// the tag CombatEngine.SumEquippedItemProperty already walks, so setting it has
    /// to move the effective stat - that is the whole point of the key.</summary>
    [Fact]
    public void AScriptedBonusReachesTheWearersEffectiveStats()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 40; ch.Int = 30;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        int baseStr = CombatEngine.EffectiveStr(ch);
        int baseDex = CombatEngine.EffectiveDex(ch);
        int baseInt = CombatEngine.EffectiveInt(ch);

        var ring = world.CreateItem();
        ring.TrySetProperty("BONUSSTR", "5");
        ring.TrySetProperty("BONUSDEX", "3");
        ring.TrySetProperty("BONUSINT", "2");
        Assert.True(ch.Equip(ring, Layer.Ring));

        Assert.Equal(baseStr + 5, CombatEngine.EffectiveStr(ch));
        Assert.Equal(baseDex + 3, CombatEngine.EffectiveDex(ch));
        Assert.Equal(baseInt + 2, CombatEngine.EffectiveInt(ch));
    }

    /// <summary>The pool bonuses the same way, through the max-pool reads.</summary>
    [Fact]
    public void AScriptedPoolBonusReachesTheWearersMaxPools()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        int hits = CombatEngine.EffectiveMaxHits(ch);
        int mana = CombatEngine.EffectiveMaxMana(ch);
        int stam = CombatEngine.EffectiveMaxStam(ch);

        var amulet = world.CreateItem();
        amulet.TrySetProperty("BONUSHITSMAX", "10");
        amulet.TrySetProperty("BONUSMANAMAX", "8");
        amulet.TrySetProperty("BONUSSTAMMAX", "6");
        Assert.True(ch.Equip(amulet, Layer.Neck));

        Assert.Equal(hits + 10, CombatEngine.EffectiveMaxHits(ch));
        Assert.Equal(mana + 8, CombatEngine.EffectiveMaxMana(ch));
        Assert.Equal(stam + 6, CombatEngine.EffectiveMaxStam(ch));
    }

    /// <summary>Taking the item off takes the bonus with it. The suit is derived on
    /// read rather than applied at equip time, so nothing can compound across a save
    /// cycle - the failure mode this engine deliberately designs against.</summary>
    [Fact]
    public void RemovingTheItemRemovesTheBonus()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.Str = 60;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));
        int bare = CombatEngine.EffectiveStr(ch);

        var ring = world.CreateItem();
        ring.TrySetProperty("BONUSSTR", "9");
        Assert.True(ch.Equip(ring, Layer.Ring));
        Assert.Equal(bare + 9, CombatEngine.EffectiveStr(ch));

        ch.Unequip(Layer.Ring);
        Assert.Equal(bare, CombatEngine.EffectiveStr(ch));
    }
}
