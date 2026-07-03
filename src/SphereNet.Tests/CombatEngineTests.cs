using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

public class CombatEngineTests
{
    private static Character MakeChar(short str = 50, short dex = 50, short intel = 50)
    {
        var ch = new Character();
        ch.Str = str; ch.Dex = dex; ch.Int = intel;
        ch.MaxHits = str; ch.MaxMana = intel; ch.MaxStam = dex;
        ch.Hits = str; ch.Mana = intel; ch.Stam = dex;
        ch.SetSkill(SkillType.Swordsmanship, 800);
        ch.SetSkill(SkillType.Tactics, 800);
        ch.SetSkill(SkillType.Anatomy, 500);
        ch.SetSkill(SkillType.Parrying, 500);
        return ch;
    }

    [Fact]
    public void GetWeaponSkill_Unarmed_ReturnsWrestling()
    {
        var ch = MakeChar();
        Assert.Equal(SkillType.Wrestling, CombatEngine.GetWeaponSkill(ch));
    }

    [Fact]
    public void GetWeaponSkill_OverrideTag_UsesNamedWeaponSkill()
    {
        var ch = MakeChar();
        var blade = new Item { ItemType = ItemType.WeaponSword }; // defaults to Swordsmanship
        ch.Equip(blade, Layer.OneHanded);
        Assert.Equal(SkillType.Swordsmanship, CombatEngine.GetWeaponSkill(ch));

        // TAG.OVERRIDE_SKILL re-routes the weapon to another combat skill.
        blade.SetTag("OVERRIDE_SKILL", ((int)SkillType.Fencing).ToString());
        Assert.Equal(SkillType.Fencing, CombatEngine.GetWeaponSkill(ch));

        // A non-combat skill id is ignored — the ItemType default stands.
        blade.SetTag("OVERRIDE_SKILL", ((int)SkillType.Alchemy).ToString());
        Assert.Equal(SkillType.Swordsmanship, CombatEngine.GetWeaponSkill(ch));
    }

    [Fact]
    public void CalcHitChance_Era0_ReturnsBetween0And100()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        int chance = CombatEngine.CalcHitChance(attacker, target, 0);
        Assert.InRange(chance, 0, 100);
    }

    [Fact]
    public void CalcHitChance_Era1_PreAOS_ReturnsBetween0And100()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        int chance = CombatEngine.CalcHitChance(attacker, target, 1);
        Assert.InRange(chance, 0, 100);
    }

    [Fact]
    public void CalcHitChance_Era2_AOS_ClampsMinTo2()
    {
        var attacker = MakeChar(str: 10);
        attacker.SetSkill(SkillType.Swordsmanship, 0);
        attacker.SetSkill(SkillType.Tactics, 0);
        var target = MakeChar(str: 100);
        int chance = CombatEngine.CalcHitChance(attacker, target, 2);
        Assert.True(chance >= 2, $"AOS hit chance should be >= 2, got {chance}");
    }

    [Fact]
    public void CalcHitChance_Era0_NpcWithZeroCombatSkills_UsesStatFallback()
    {
        var npc = MakeChar(str: 60, dex: 60);
        npc.IsPlayer = false;
        npc.SetSkill(SkillType.Wrestling, 0);
        npc.SetSkill(SkillType.Tactics, 0);

        var target = MakeChar(str: 60, dex: 50);
        target.IsPlayer = true;

        int chance = CombatEngine.CalcHitChance(npc, target, 0);

        Assert.InRange(chance, 35, 95);
    }

    [Fact]
    public void CalcHitChance_Era0_PlayerWithZeroCombatSkills_DoesNotUseNpcFallback()
    {
        var player = MakeChar(str: 60, dex: 60);
        player.IsPlayer = true;
        player.SetSkill(SkillType.Wrestling, 0);
        player.SetSkill(SkillType.Tactics, 0);

        var target = MakeChar(str: 60, dex: 50);
        target.IsPlayer = true;

        int chance = CombatEngine.CalcHitChance(player, target, 0);

        Assert.InRange(chance, 0, 25);
    }

    [Fact]
    public void CalcWeaponDamage_Unarmed_MinIsAtLeast1()
    {
        var ch = MakeChar(str: 10);
        var (min, max) = CombatEngine.CalcWeaponDamage(ch, null, 0);
        Assert.True(min >= 1);
        Assert.True(max >= min);
    }

    [Fact]
    public void CalcWeaponDamage_HigherStr_HigherDamage()
    {
        var weak = MakeChar(str: 10);
        var strong = MakeChar(str: 100);
        var (_, maxWeak) = CombatEngine.CalcWeaponDamage(weak, null, 0);
        var (_, maxStrong) = CombatEngine.CalcWeaponDamage(strong, null, 0);
        Assert.True(maxStrong > maxWeak);
    }

    [Fact]
    public void CalcArmorDefense_NoArmor_ReturnsZero()
    {
        var ch = MakeChar();
        int ar = CombatEngine.CalcArmorDefense(ch);
        Assert.Equal(0, ar);
    }

    [Fact]
    public void CalcArmorDefense_Elemental_ReturnsZero()
    {
        var ch = MakeChar();
        int ar = CombatEngine.CalcArmorDefense(ch, elementalEngine: true);
        Assert.Equal(0, ar);
    }

    [Fact]
    public void CalcArmorDefense_UsesArmorValueFromItemDefOrTag()
    {
        var ch = MakeChar();
        var chest = new Item();
        chest.SetTag("ARMOR", "40");
        ch.Equip(chest, Layer.Chest);

        int ar = CombatEngine.CalcArmorDefense(ch);

        Assert.Equal(14, ar);
    }

    [Fact]
    public void OnHitDamage_HookCanModifyAndCancelDamage()
    {
        var saved = CombatEngine.OnHitDamage;
        try
        {
            var attacker = MakeChar();
            var target = MakeChar();
            target.SetSkill(SkillType.Parrying, 0); // no parry interference

            // Cancel: the hook returns 0, so a connecting hit deals no HP loss.
            // (Loop past random misses; ResolveAttack returns -1 on a miss and
            // the hook value on a connecting hit.)
            CombatEngine.OnHitDamage = _ => 0;
            int canceled = -1;
            for (int i = 0; i < 200 && canceled < 0; i++)
            {
                target.Hits = target.MaxHits;
                canceled = CombatEngine.ResolveAttack(attacker, target, null);
            }
            Assert.Equal(0, canceled);
            Assert.Equal(target.MaxHits, target.Hits);

            // Modify: the hook forces exactly 7 damage regardless of the roll.
            CombatEngine.OnHitDamage = _ => 7;
            int modified = -1;
            for (int i = 0; i < 200 && modified < 0; i++)
            {
                target.Hits = target.MaxHits;
                modified = CombatEngine.ResolveAttack(attacker, target, null);
            }
            Assert.Equal(7, modified);
            Assert.Equal((short)(target.MaxHits - 7), target.Hits);
        }
        finally
        {
            CombatEngine.OnHitDamage = saved;
        }
    }

    private static int ResolveUntilHit(Character attacker, Character target, Item weapon, CombatFlags flags)
    {
        for (int i = 0; i < 400; i++)
        {
            target.Hits = target.MaxHits;
            int d = CombatEngine.ResolveAttack(attacker, target, weapon, flags, -1, 0);
            if (d >= 0) return d;
        }
        return -1;
    }

    [Fact]
    public void NpcBonusDamage_GatesDamageIncreaseForNpcsButNotPlayers()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10); // fixed base damage

            var target = MakeChar();
            target.SetSkill(SkillType.Parrying, 0);

            var npc = MakeChar();
            npc.IsPlayer = false;
            var npcWeapon = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x0F5E };
            npc.Equip(npcWeapon, Layer.OneHanded);
            npc.SetTag("INCREASEDAM", "50");

            // NPC without the flag: the +50% Damage Increase is ignored.
            Assert.Equal(10, ResolveUntilHit(npc, target, npcWeapon, CombatFlags.None));
            // NPC with COMBAT_NPC_BONUSDAMAGE: +50% applies.
            Assert.Equal(15, ResolveUntilHit(npc, target, npcWeapon, CombatFlags.NpcBonusDamage));

            // A player always gets the increase, flag or not.
            var player = MakeChar();
            player.IsPlayer = true;
            var pWeapon = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x0F5E };
            player.Equip(pWeapon, Layer.OneHanded);
            player.SetTag("INCREASEDAM", "50");
            Assert.Equal(15, ResolveUntilHit(player, target, pWeapon, CombatFlags.None));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void StackArmor_SumsAllPiecesCoveringRegion()
    {
        var oldFlags = Character.CombatFlags;
        try
        {
            var ch = new Character();
            var chest = new Item(); chest.SetTag("ARMOR", "30"); ch.Equip(chest, Layer.Chest);
            var tunic = new Item(); tunic.SetTag("ARMOR", "12"); ch.Equip(tunic, Layer.Tunic);

            // Without the flag only the primary chest layer counts.
            Character.CombatFlags = 0;
            Assert.Equal(30, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));

            // With COMBAT_STACKARMOR both torso pieces sum for that region.
            Character.CombatFlags = (int)CombatFlags.StackArmor;
            Assert.Equal(42, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));
            Assert.Equal(0, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Head));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void Discordance_TagReducesArmorDefenseUntilExpiry()
    {
        var ch = new Character();
        var chest = new Item(); chest.SetTag("ARMOR", "40"); ch.Equip(chest, Layer.Chest);
        Assert.Equal(40, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));

        // Active discord (50%) halves the region defense.
        ch.SetTag("DISCORD_PCT", "50");
        ch.SetTag("DISCORD_UNTIL", (Environment.TickCount64 + 60_000).ToString());
        Assert.Equal(20, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));

        // Expired discord no longer applies.
        ch.SetTag("DISCORD_UNTIL", (Environment.TickCount64 - 1000).ToString());
        Assert.Equal(40, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));
    }

    [Fact]
    public void DamageItem_ReducesDurabilityWhenEnabled()
    {
        var savedEnabled = CombatEngine.DurabilityEnabled;
        var savedChance = CombatEngine.DurabilityLossChance;
        try
        {
            CombatEngine.DurabilityEnabled = true;
            CombatEngine.DurabilityLossChance = 100; // always lose on the roll
            var tool = new Item { HitsMax = 50, HitsCur = 50 };
            CombatEngine.DamageItem(tool);
            Assert.True(tool.HitsCur < 50);
        }
        finally
        {
            CombatEngine.DurabilityEnabled = savedEnabled;
            CombatEngine.DurabilityLossChance = savedChance;
        }
    }

    [Fact]
    public void OnHitParry_PartialBlockLeaksDamageInsteadOfFullBlock()
    {
        var savedParry = CombatEngine.OnHitParry;
        try
        {
            var attacker = MakeChar();
            var target = MakeChar();
            target.SetSkill(SkillType.Parrying, 1000);        // max shield parry chance
            var shield = new Item { ItemType = ItemType.Shield };
            target.Equip(shield, Layer.TwoHanded);

            // Partial parry: 1 point leaks through (target has no armor → it stands).
            // Previously every successful parry was a full block (returned -1). The
            // hook flags when a parry actually fires so we can assert on that swing.
            bool parried = false;
            CombatEngine.OnHitParry = (_, _, _) => { parried = true; return 1; };
            int dmg = 0;
            for (int i = 0; i < 4000; i++)
            {
                parried = false;
                target.Hits = target.MaxHits;
                dmg = CombatEngine.ResolveAttack(attacker, target, null);
                if (parried) break;
            }
            Assert.True(parried, "expected at least one parry across the attempts");
            Assert.Equal(1, dmg);                                  // partial damage leaked through
            Assert.Equal((short)(target.MaxHits - 1), target.Hits); // not a full block

        }
        finally
        {
            CombatEngine.OnHitParry = savedParry;
        }
    }

    [Fact]
    public void ResolveAttack_DeadAttacker_ReturnsZero()
    {
        var attacker = MakeChar();
        attacker.Kill();
        var target = MakeChar();
        int dmg = CombatEngine.ResolveAttack(attacker, target, null);
        Assert.Equal(0, dmg);
    }

    [Fact]
    public void ResolveAttack_DeadTarget_ReturnsZero()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        target.Kill();
        int dmg = CombatEngine.ResolveAttack(attacker, target, null);
        Assert.Equal(0, dmg);
    }

    [Fact]
    public void GetWeaponDamageType_NullWeapon_ReturnsBlunt()
    {
        var type = CombatEngine.GetWeaponDamageType(null);
        Assert.Equal(DamageType.HitBlunt, type);
    }

    [Fact]
    public void GetSwingDelayMs_CombatSpeedEra_ChangesDelay()
    {
        var ch = MakeChar(dex: 80);
        var weapon = new Item { BaseId = 0x0F5E };
        int oldEra = Character.CombatSpeedEra;
        try
        {
            Character.CombatSpeedEra = 0;
            int era0 = CombatEngine.GetSwingDelayMs(ch, weapon);
            Character.CombatSpeedEra = 1;
            int era1 = CombatEngine.GetSwingDelayMs(ch, weapon);
            Character.CombatSpeedEra = 2;
            int era2 = CombatEngine.GetSwingDelayMs(ch, weapon);

            Assert.NotEqual(era0, era1);
            Assert.NotEqual(era1, era2);
        }
        finally
        {
            Character.CombatSpeedEra = oldEra;
        }
    }
}
