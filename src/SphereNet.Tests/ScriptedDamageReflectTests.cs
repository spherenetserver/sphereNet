using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A blow dealt from script bounces the same way a swing does (port plan İŞ-10).
///
/// The reference has ONE damage entry - CChar::OnTakeDamage - and the DAMAGE verb calls
/// it directly (CObjBase.cpp:2249), so Reactive Armour, Blood Oath and
/// REFLECTPHYSICALDAM belong to `&lt;SRC.DAMAGE 40&gt;` exactly as much as to a weapon
/// hit. SphereNet grew a second entry for scripted damage and the whole family stayed
/// behind on the melee one: a shard dealing its damage from script - the usual way a
/// custom attack, an arena or a trap with a culprit is written - got none of it.
///
/// The gates are the reference's own and are checked here too, because widening this
/// too far would be its own bug: the family answers a PHYSICAL blow with somebody
/// behind it, which is why a fireball does not come back at the mage who threw it.
/// </summary>
public sealed class ScriptedDamageReflectTests
{
    private const DamageType Blow = DamageType.HitBlunt | DamageType.General;

    private static (Character Attacker, Character Defender) Fighters(GameWorld world)
    {
        var attacker = world.CreateCharacter();
        attacker.Str = 100; attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        var defender = world.CreateCharacter();
        defender.Str = 100; defender.MaxHits = 200; defender.Hits = 200;
        world.PlaceCharacter(defender, new Point3D(101, 100, 0, 0));
        return (attacker, defender);
    }

    [Fact]
    public void ReactiveArmourAnswersAScriptedBlow()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        int dealt = CombatEngine.ApplyScriptDamage(defender, 40, Blow, attacker);

        Assert.Equal(20, dealt);             // half was taken out of the blow
        Assert.Equal(180, defender.Hits);
        Assert.Equal(80, attacker.Hits);     // and the other half went back
    }

    [Fact]
    public void BloodOathAnswersAScriptedBlow()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.BloodOathEnemy = attacker.Uid;
        defender.BloodOathLevel = 40;

        CombatEngine.ApplyScriptDamage(defender, 40, Blow, attacker);

        // The bonded victim takes the blow plus a tenth of it...
        Assert.Equal(156, defender.Hits);
        // ...and sends back (100 - level)%.
        Assert.Equal(76, attacker.Hits);
    }

    [Fact]
    public void ASuitBouncesAScriptedBlow()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetTag("REFLECTPHYSICALDAM", "25");

        CombatEngine.ApplyScriptDamage(defender, 40, Blow, attacker);

        Assert.Equal(160, defender.Hits);
        Assert.Equal(90, attacker.Hits);     // a quarter came back
    }

    [Fact]
    public void ASpellDoesNotComeBack()
    {
        // The reference gates the family on a physical blow, which is why a mage does
        // not take his own fireball back off a target wearing Reactive Armour.
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        CombatEngine.ApplyScriptDamage(defender, 40, DamageType.Magic | DamageType.Fire, attacker);

        Assert.Equal(100, attacker.Hits);
    }

    [Fact]
    public void DamageWithNobodyBehindItBouncesOffNothing()
    {
        // A burning field or a trap with no culprit passes a null source upstream, and
        // the family never runs for it.
        var world = TestHarness.CreateWorld();
        var (_, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        int dealt = CombatEngine.ApplyScriptDamage(defender, 40, Blow, source: null);

        Assert.Equal(40, dealt);             // nothing was taken out of the blow
        Assert.Equal(160, defender.Hits);
    }

    [Fact]
    public void AWearerWhoAbsorbsItAllStillSendsItBack()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 100;

        int dealt = CombatEngine.ApplyScriptDamage(defender, 40, Blow, attacker);

        Assert.Equal(0, dealt);
        Assert.Equal(200, defender.Hits);    // untouched
        Assert.Equal(60, attacker.Hits);     // but the blow still went home
    }
}
