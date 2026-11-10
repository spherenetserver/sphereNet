using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Hit chance, weapon rating, resist, damage-bonus stat, reach, region and archery
/// gates, checked against the reference fight code (CResourceCalc.cpp,
/// CCharFight.cpp, CItem.cpp).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatHitDamageAuditTests
{
    private static Character MakeChar(GameWorld world, short x, short y, sbyte z = 0, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        if (player)
            ch.IsOnline = true;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 100; ch.Hits = 100;
        ch.MaxStam = 100; ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, z, 0));
        return ch;
    }

    private static string SkillModKey(SkillType skill) => $"SkillMod{(int)skill}";

    // -- 1. Hit chance reads the adjusted skill in era 0 only ------------------------

    [Fact]
    public void HitChance_Era0_ReadsAdjustedSkill_Era1And2ReadBase()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 101, 100);
        attacker.SetSkill(SkillType.Wrestling, 500);
        attacker.SetSkill(SkillType.Tactics, 500);
        target.SetSkill(SkillType.Tactics, 500);

        int era0 = CombatEngine.CalcHitChance(attacker, target, 0);
        int era1 = CombatEngine.CalcHitChance(attacker, target, 1);
        int era2 = CombatEngine.CalcHitChance(attacker, target, 2);

        // SkillMod<n> is part of Skill_GetAdjusted (CCharSkill.cpp:158-166).
        attacker.SetTag(SkillModKey(SkillType.Wrestling), "300");

        Assert.NotEqual(era0, CombatEngine.CalcHitChance(attacker, target, 0));
        Assert.Equal(era1, CombatEngine.CalcHitChance(attacker, target, 1));
        Assert.Equal(era2, CombatEngine.CalcHitChance(attacker, target, 2));
    }

    [Fact]
    public void HitChance_Era0_TargetTacticsIsAdjusted()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 101, 100);
        attacker.SetSkill(SkillType.Wrestling, 600);
        attacker.SetSkill(SkillType.Tactics, 600);
        target.SetSkill(SkillType.Tactics, 300);

        int before = CombatEngine.CalcHitChance(attacker, target, 0);
        target.SetTag(SkillModKey(SkillType.Tactics), "400");
        Assert.NotEqual(before, CombatEngine.CalcHitChance(attacker, target, 0));
    }

    // -- 1b. Parry DEX penalty reads the adjusted stat --------------------------------

    [Fact]
    public void ParryChance_DexPenaltyUsesAdjustedDex()
    {
        var world = TestHarness.CreateWorld();
        var defender = MakeChar(world, 100, 100);
        defender.SetSkill(SkillType.Parrying, 1000);
        var shield = world.CreateItem();
        shield.ItemType = ItemType.Shield;
        defender.Equip(shield, Layer.TwoHanded);

        defender.Dex = 80;
        int full = CombatEngine.CalculateParryChance(defender, out _);
        Assert.True(full > 0);

        // Base DEX 40 alone is penalised ...
        defender.Dex = 40;
        defender.ModDex = 0;
        int penalised = CombatEngine.CalculateParryChance(defender, out _);
        Assert.True(penalised < full);

        // ... but base + modifier reaching 80 is not (Stat_GetAdjusted, CResourceCalc.cpp:333).
        defender.ModDex = 40;
        Assert.Equal(full, CombatEngine.CalculateParryChance(defender, out _));
    }

    // -- 2. Weapon rating wear scaling -------------------------------------------------

    [Fact]
    public void WeaponDamage_ScalesWithDurabilityUnderTheOption()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100, player: false);
        CombatEngine.WeaponDefLookup = _ => (100, 100);
        var sword = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x13B6 };
        sword.HitsMax = 100;
        sword.HitsCur = 0;

        // Without OF_ScaleDamageByDurability the wear changes nothing.
        GameClient.ServerOptionFlags &= ~OptionFlags.ScaleDamageByDurability;
        Assert.Equal((100, 100), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));

        // Weapon_GetAttack: ... * (50 + 50 * hits / maxhits) / 100 (CItem.cpp:4925-4929).
        GameClient.ServerOptionFlags |= OptionFlags.ScaleDamageByDurability;
        Assert.Equal((50, 50), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));

        sword.HitsCur = 50;
        Assert.Equal((75, 75), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));
    }

    [Fact]
    public void WeaponDamage_ModArIsScaledToo()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100, player: false);
        CombatEngine.WeaponDefLookup = _ => (80, 80);
        var sword = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x13B6 };
        sword.ModAr = 20;
        sword.HitsMax = 100;
        sword.HitsCur = 0;

        GameClient.ServerOptionFlags |= OptionFlags.ScaleDamageByDurability;
        // (base + ModAr) is scaled together: 100 * 50% = 50.
        Assert.Equal((50, 50), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));
    }

    // -- 3. Negative resists ----------------------------------------------------------

    [Fact]
    public void EffectiveResist_NegativeResistRaisesDamageTaken()
    {
        var world = TestHarness.CreateWorld();
        var target = MakeChar(world, 100, 100);
        target.ResFire = -50;

        Assert.Equal(-50, CombatEngine.EffResFire(target));
        // 100 damage, all fire, (100 - -50)% = 150 (CCharFight.cpp:724-730).
        Assert.Equal(150, CombatEngine.ApplyDamageSplitResist(target, 100, 0, 100, 0, 0, 0));
    }

    // -- 4. COMBATBONUSSTAT / COMBATBONUSPERCENT --------------------------------------

    [Fact]
    public void DamageBonus_DefaultsToStrWithTheEraPercent()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        attacker.Str = 50; attacker.Dex = 100;
        attacker.SetSkill(SkillType.Tactics, 500);
        CombatEngine.WeaponDefLookup = _ => (100, 100);
        var sword = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x13B6 };

        // Era 0: STR * 10%, era 1: STR * 20% (+ (tactics - 50.0) / 10 = 0).
        Assert.Equal((105, 105), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));
        Assert.Equal((110, 110), CombatEngine.CalcWeaponDamage(attacker, sword, 1, CombatFlags.None));
    }

    [Fact]
    public void DamageBonus_ReadsCombatBonusStatAndPercent()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        attacker.Str = 50; attacker.Dex = 100; attacker.Int = 20;
        CombatEngine.WeaponDefLookup = _ => (100, 100);
        var sword = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x13B6 };

        // STAT_DEX = 2: DEX 100 * 50% = +50%.
        attacker.SetTag("COMBATBONUSSTAT", "2");
        attacker.SetTag("COMBATBONUSPERCENT", "50");
        Assert.Equal((150, 150), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));

        // STAT_INT = 1 with the era's own percent (10): INT 20 * 10% = +2%.
        attacker.SetTag("COMBATBONUSSTAT", "1");
        attacker.RemoveTag("COMBATBONUSPERCENT");
        Assert.Equal((102, 102), CombatEngine.CalcWeaponDamage(attacker, sword, 0, CombatFlags.None));
    }

    // -- 5. Reach counts height --------------------------------------------------------

    [Fact]
    public void CombatDistance_CountsHeightInHalfPlayerHeights()
    {
        var world = TestHarness.CreateWorld();
        var a = MakeChar(world, 100, 100, 0);
        var onTop = MakeChar(world, 101, 100, 40);
        var near = MakeChar(world, 101, 101, 8);

        Assert.Equal(5, CombatHelper.GetCombatDistance(a, onTop));   // 40 / 8
        Assert.Equal(1, CombatHelper.GetCombatDistance(a, near));    // max(1, 1)
        Assert.Equal(1, CombatHelper.GetChebyshevDistance(a, onTop)); // the planar one is unchanged
    }

    [Fact]
    public void MeleeSwing_TargetFloorsAboveIsOutOfReach()
    {
        int oldDelay = Character.CombatMeleeMovementDelay;
        try
        {
            Character.CombatMeleeMovementDelay = 0;
            var world = TestHarness.CreateWorld();
            var attacker = MakeChar(world, 100, 100, 0);
            var above = MakeChar(world, 101, 100, 40);
            var level = MakeChar(world, 99, 100, 8);

            var tooHigh = CombatHelper.ValidateSwingPrep(world, attacker, above, null,
                PrivLevel.Player, Environment.TickCount64, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, tooHigh.Result);

            var reachable = CombatHelper.ValidateSwingPrep(world, attacker, level, null,
                PrivLevel.Player, Environment.TickCount64, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.Ready, reachable.Result);

            Assert.False(CombatHelper.InWeaponReachAndLos(world, attacker, above, null,
                PrivLevel.Player, (_, _) => true));
            Assert.True(CombatHelper.InWeaponReachAndLos(world, attacker, level, null,
                PrivLevel.Player, (_, _) => true));
        }
        finally
        {
            Character.CombatMeleeMovementDelay = oldDelay;
        }
    }

    // -- 6. SAFE / NO_PVP gate ---------------------------------------------------------

    private static void AddRegion(GameWorld world, string name, RegionFlag flags, short x0, short y0, short x1, short y1)
    {
        var region = new Region { Name = name, Flags = flags, MapIndex = 0 };
        region.AddRect(x0, y0, x1, y1);
        world.AddRegion(region);
    }

    [Fact]
    public void SafeRegion_OnlyTheTargetsAreaRefusesTheSwing()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 150, 100);
        AddRegion(world, "safe_atk", RegionFlag.Safe, 90, 90, 110, 110);

        // Fight_CanHit refuses on the target's area only (CCharFight.cpp:1703).
        Assert.False(CombatHelper.IsCombatBlockedByRegion(world, attacker, target));
        // ... while the victim inside it is protected.
        Assert.True(CombatHelper.IsCombatBlockedByRegion(world, target, attacker));
    }

    [Fact]
    public void NoPvpRegion_IsNotASwingGate()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var target = MakeChar(world, 150, 100);
        AddRegion(world, "nopvp_tgt", RegionFlag.NoPvP, 140, 90, 160, 110);

        // NO_PVP bounces the damage on the victim's area (OnTakeDamage, :658-678) -
        // the swing itself is not refused.
        Assert.False(CombatHelper.IsCombatBlockedByRegion(world, attacker, target));
        Assert.True(CombatEngine.IsRegionProtected(target, attacker));
    }

    // -- 7. Archery settle delay binds a client only ------------------------------------

    [Fact]
    public void ArcheryMoveDelay_AppliesToAClientNotAnNpc()
    {
        int oldMin = Character.ArcheryMinDist, oldMax = Character.ArcheryMaxDist;
        int oldDelay = Character.CombatArcheryMovementDelay, oldFlags = Character.CombatFlags;
        try
        {
            Character.ArcheryMinDist = 1;
            Character.ArcheryMaxDist = 12;
            Character.CombatArcheryMovementDelay = 500;
            Character.CombatFlags = 0;

            var world = TestHarness.CreateWorld();
            var npc = MakeChar(world, 100, 100, player: false);
            var target = MakeChar(world, 105, 100);
            var bow = new Item { ItemType = ItemType.WeaponBow, BaseId = 0x13B2 };

            long now = Environment.TickCount64;
            npc.LastMoveTick = now;     // the world stamps this on every step, NPC or not

            var npcShot = CombatHelper.ValidateSwingPrep(world, npc, target, bow,
                PrivLevel.Player, now + 100, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.Ready, npcShot.Result);
            Assert.Equal(CombatHelper.HitTimeDecision.Resolve,
                CombatHelper.EvaluateHitTime(world, npc, target, bow, PrivLevel.Player,
                    now + 100, now + 10_000, (_, _) => true));

            var client = MakeChar(world, 100, 101);
            client.LastMoveTick = now;
            var clientShot = CombatHelper.ValidateSwingPrep(world, client, target, bow,
                PrivLevel.Player, now + 100, (_, _) => true);
            Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, clientShot.Result);
            Assert.Equal(CombatHelper.HitTimeDecision.Miss,
                CombatHelper.EvaluateHitTime(world, client, target, bow, PrivLevel.Player,
                    now + 100, now + 10_000, (_, _) => true));
        }
        finally
        {
            Character.ArcheryMinDist = oldMin;
            Character.ArcheryMaxDist = oldMax;
            Character.CombatArcheryMovementDelay = oldDelay;
            Character.CombatFlags = oldFlags;
        }
    }

    // -- 8. Parry goes through Skill_UseQuick --------------------------------------------

    [Fact]
    public void Parry_RollsThroughUseQuickFlatPercent()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        attacker.PrivLevel = PrivLevel.GM;          // the hit roll always lands
        var defender = MakeChar(world, 101, 100);
        defender.SetSkill(SkillType.Parrying, 1000);
        var shield = world.CreateItem();
        shield.ItemType = ItemType.Shield;
        defender.Equip(shield, Layer.TwoHanded);

        int expectedChance = CombatEngine.CalculateParryChance(defender, out _);
        Assert.True(expectedChance > 0);

        var seen = new List<(Character Who, int Skill, int Difficulty)>();
        Character.OnSkillUseQuickDetailed = (Character ch, int skillId, ref int difficulty, int result) =>
        {
            seen.Add((ch, skillId, difficulty));
            return result;
        };

        CombatEngine.ResolveAttack(attacker, defender, null);

        // @SkillUseQuick fired for the defender's Parrying with the chance as difficulty
        // (CCharFight.cpp:2122, CCharSkill.cpp:566-579).
        var parry = Assert.Single(seen, s => s.Skill == (int)SkillType.Parrying);
        Assert.Same(defender, parry.Who);
        Assert.Equal(expectedChance, parry.Difficulty);
    }

    [Fact]
    public void Parry_GmDefenderIsNotAutoSuccess()
    {
        // Skill_CheckSuccess exempts Parrying from the GM auto-success
        // (CCharSkill.cpp:526): a chance of 0 percent must still fail for a GM.
        var world = TestHarness.CreateWorld();
        var gm = MakeChar(world, 100, 100);
        gm.PrivLevel = PrivLevel.GM;
        Assert.False(SphereNet.Game.Skills.SkillEngine.UseQuick(
            gm, SkillType.Parrying, 0, allowGain: false, useBellCurve: false));
    }
}
