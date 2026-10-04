using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Gameplay balance points checked against Source-X: the era-0 hit roll, a
/// creature's natural armour, the player-only damage bonus, hit-point regeneration
/// under hunger and poison, reveal on movement, and vendor markup.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BalanceParityWaveTests
{
    private static Character Fighter(GameWorld world, int x, bool player)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.IsOnline = player; // a player fighter is a connected one
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 1000; ch.Hits = 1000;
        ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static int LandedOutOf(Character attacker, Character target, int swings)
    {
        int landed = 0;
        for (int i = 0; i < swings; i++)
        {
            target.Hits = target.MaxHits;
            if (CombatEngine.ResolveAttack(attacker, target, null, CombatFlags.None, 0, 0, 0, out _) >= 0)
                landed++;
        }
        return landed;
    }

    [Fact]
    public void Era0_AMasterLandsTheComputedChance()
    {
        // Source-X CResourceCalc.cpp:193 (cf60700): the chance is returned as is and
        // Skill_CheckSuccess makes a flat chance*10 >= rand(1000) check. Here
        // iSkillAttack 1000, iSkillDefend (0 + 50*10)/2 = 250, (1000 - 150)/10 = 85.
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 1000);
        attacker.SetSkill(SkillType.Tactics, 1000);
        var target = Fighter(world, 101, player: true);

        Assert.Equal(85, CombatEngine.CalcHitChance(attacker, target));
        const int swings = 2000;
        Assert.InRange(LandedOutOf(attacker, target, swings), swings * 78 / 100, swings * 92 / 100);
    }

    [Fact]
    public void Era0_Legacy_AMasterDoesNotLandEverySwing()
    {
        // COMBATHITCHANCELEGACYERA0=1, the 0.56 / older Source-X roll: difficulty =
        // rand(iDiff), hit when difficulty*10 >= rand(1000) - about iDiff/2 percent.
        Character.CombatHitChanceLegacyEra0 = true;
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 1000);
        attacker.SetSkill(SkillType.Tactics, 1000);
        var target = Fighter(world, 101, player: true);

        Assert.Equal(85, CombatEngine.CalcHitChance(attacker, target));
        const int swings = 2000;
        Assert.InRange(LandedOutOf(attacker, target, swings), swings * 30 / 100, swings * 70 / 100);
    }

    [Fact]
    public void ASleepingTargetIsHitAtLeastEightyPercent()
    {
        // CResourceCalc.cpp:189-191: a sleeping or frozen target floors the chance at 80.
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        var target = Fighter(world, 101, player: true);
        target.SetStatFlag(StatFlag.Sleeping);

        Assert.Equal(80, CombatEngine.CalcHitChance(attacker, target));
    }

    [Fact]
    public void ASleepingTargetKeepsAHigherComputedChance()
    {
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 1000);
        attacker.SetSkill(SkillType.Tactics, 1000);
        var target = Fighter(world, 101, player: true);
        target.SetStatFlag(StatFlag.Sleeping);

        Assert.Equal(85, CombatEngine.CalcHitChance(attacker, target));
    }

    [Fact]
    public void ASleepingTargetDrawsFromTen_Legacy()
    {
        // Legacy era 0: rand(10) for a sleeping or frozen target.
        Character.CombatHitChanceLegacyEra0 = true;
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 1000);
        var target = Fighter(world, 101, player: true);
        target.SetStatFlag(StatFlag.Sleeping);

        Assert.Equal(10, CombatEngine.CalcHitChance(attacker, target));
    }

    [Fact]
    public void Era0_HitAndDefenseChanceIncreaseScaleTheChance()
    {
        // CResourceCalc.cpp:181-185: iChance * (100 + HCI) / 100 * (100 - DCI) / 100.
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 600);
        attacker.SetSkill(SkillType.Tactics, 600);
        var target = Fighter(world, 101, player: true);
        // iSkillAttack 600, iSkillDefend 250, (600 - 70)/10 = 53.
        Assert.Equal(53, CombatEngine.CalcHitChance(attacker, target));

        attacker.SetTag("INCREASEHITCHANCE", "50");
        Assert.Equal(79, CombatEngine.CalcHitChance(attacker, target));   // 53*150/100

        target.SetTag("INCREASEDEFCHANCE", "50");
        Assert.Equal(39, CombatEngine.CalcHitChance(attacker, target));   // 79*50/100

        // Legacy era 0 ignores both.
        Character.CombatHitChanceLegacyEra0 = true;
        Assert.Equal(53, CombatEngine.CalcHitChance(attacker, target));
    }

    [Fact]
    public void Era0_DefenseChanceIncreaseOverAHundredCannotHit()
    {
        var world = TestHarness.CreateWorld();
        var attacker = Fighter(world, 100, player: true);
        attacker.SetSkill(SkillType.Wrestling, 1000);
        var target = Fighter(world, 101, player: true);
        target.SetTag("INCREASEDEFCHANCE", "150");

        Assert.Equal(0, CombatEngine.CalcHitChance(attacker, target));
    }

    [Fact]
    public void AnNpcGetsNoStrengthDamageBonus()
    {
        // Fight_CalcDamage: the bonus is m_pPlayer || COMBAT_NPC_BONUSDAMAGE only.
        var world = TestHarness.CreateWorld();
        var npc = Fighter(world, 100, player: false);
        npc.TrySetProperty("DAM", "10,10");
        npc.Str = 500;
        var player = Fighter(world, 101, player: true);
        player.TrySetProperty("DAM", "10,10");
        player.Str = 500;

        Assert.Equal((10, 10), CombatEngine.CalcWeaponDamage(npc, null, 0));
        Assert.Equal((15, 15), CombatEngine.CalcWeaponDamage(player, null, 0)); // STR 500 * 10% = +50%
    }

    [Fact]
    public void HitsRegenerateWhileStarvingAndPoisoned()
    {
        // Stats_Regen has no hunger or poison gate.
        var ch = new Character { BodyId = 0x029A };
        ch.MaxHits = 100; ch.Hits = 50;
        ch.Food = 0;
        ch.ApplyPoison(2);

        ch.OnTick();

        Assert.True(ch.Hits > 50);
    }

    private static (GameWorld, MovementEngine, Character) Walker()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Stam = ch.MaxStam = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, new MovementEngine(world), ch);
    }

    [Fact]
    public void AHiddenPlayerIsRevealedByTheirFirstStep()
    {
        var (_, engine, ch) = Walker();
        ch.SetStatFlag(StatFlag.Hidden);

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void AStealthBudgetLastsItsSteps()
    {
        var (_, engine, ch) = Walker();
        ch.SetStatFlag(StatFlag.Hidden);
        ch.StepStealth = 2;

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));
        Assert.True(ch.IsStatFlag(StatFlag.Hidden));
        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 1));
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void StaffInvisibilitySurvivesWalking()
    {
        // .INVIS is STATF_INSUBSTANTIAL upstream, which movement never reveals.
        var (_, engine, ch) = Walker();
        ch.PrivLevel = PrivLevel.GM;
        ch.ApplyInvisVerb("1", null);
        Assert.True(ch.IsStatFlag(StatFlag.Insubstantial));

        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 0));

        Assert.True(ch.IsStatFlag(StatFlag.Insubstantial));
    }

    [Fact]
    public void AnOwnedVendorSellsAtItsOwnersPrice()
    {
        // NPC_GetVendorMarkup returns 0 for a pet vendor.
        var world = TestHarness.CreateWorld();
        VendorEngine.World = world;
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.SetTag("PRICE", "100");

        Assert.Equal(115, VendorEngine.GetVendorSellToPlayerPrice(vendor, item));
        vendor.SetStatFlag(StatFlag.Pet);
        Assert.Equal(100, VendorEngine.GetVendorSellToPlayerPrice(vendor, item));
    }
}
