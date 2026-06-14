using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;

namespace SphereNet.Game.Combat;

/// <summary>
/// Combat flags. Maps exactly to COMBATFLAGS_TYPE in Source-X CServerConfig.h.
/// </summary>
[Flags]
public enum CombatFlags : uint
{
    None = 0,
    NoDirChange = 0x1,
    FaceCombat = 0x2,
    PreHit = 0x4,
    ElementalEngine = 0x8,
    DClickSelfUnmounts = 0x20,
    AllowHitFromShip = 0x40,
    NoPetDesert = 0x80,
    ArcheryCanMove = 0x100,
    StayInRange = 0x200,
    StackArmor = 0x1000,
    NoPoisonHit = 0x2000,
    Slayer = 0x4000,
    SwingNoRange = 0x8000,
    AnimHitSmooth = 0x10000,
    FirstHitInstant = 0x20000,
    NpcBonusDamage = 0x40000,
    ParalyzeCanSwing = 0x80000,
    AttackNoAggreived = 0x100000,
}

/// <summary>
/// Combat swing state. Maps to WAR_SWING_TYPE in Source-X.
/// </summary>
public enum SwingState
{
    Invalid = -1,
    Equipping = 0,
    Ready = 1,
    Swinging = 2,
    EquippingNoWait = 10,
}

/// <summary>
/// Damage type flags. Maps to DAMAGE_TYPE in Source-X.
/// </summary>
[Flags]
public enum DamageType : ushort
{
    General = 0x00,
    Physical = 0x01,
    Magic = 0x02,
    Poison = 0x04,
    Fire = 0x08,
    Cold = 0x10,
    Energy = 0x20,
    HitBlunt = 0x40,
    HitPierce = 0x80,
    HitSlash = 0x100,
    God = 0x200,
    NoReveal = 0x400,
    NoUnparalyze = 0x800,
    Fixed = 0x1000,
}

public enum ArmorHitRegion
{
    Head,
    Neck,
    Chest,
    Arms,
    Hands,
    Legs,
    Feet,
}

/// <summary>
/// Core combat engine. Maps to CChar::Fight_* functions in Source-X CCharFight.cpp.
/// Handles hit/miss, damage calculation, armor, and weapon skill routing.
/// </summary>
public static class CombatEngine
{
    private static Random _rand => Random.Shared;
    private static readonly (ArmorHitRegion Region, Layer Layer, int Weight)[] _armorRegions =
    [
        (ArmorHitRegion.Head, Layer.Helm, 14),
        (ArmorHitRegion.Neck, Layer.Neck, 7),
        (ArmorHitRegion.Chest, Layer.Chest, 35),
        (ArmorHitRegion.Arms, Layer.Arms, 14),
        (ArmorHitRegion.Hands, Layer.Gloves, 7),
        (ArmorHitRegion.Legs, Layer.Legs, 22),
        (ArmorHitRegion.Feet, Layer.Shoes, 1),
    ];

    public static bool DurabilityEnabled { get; set; }
    public static int DurabilityLossChance { get; set; } = 25;
    public static int DurabilityLossMin { get; set; } = 1;
    public static int DurabilityLossMax { get; set; } = 1;
    public static bool BreakOnZeroHits { get; set; } = true;
    public static int DefaultHits { get; set; } = 50;

    public static Action<Item>? OnItemBroken;
    public static Func<Item, int, bool>? OnItemDamaged;
    public static Action<Character, Character>? OnHitParry;

    /// <summary>
    /// Calculate hit chance. Maps to CServerConfig::Calc_CombatChanceToHit.
    /// Era 0 = Sphere custom, 1 = pre-AOS, 2 = AOS.
    /// </summary>
    public static int CalcHitChance(Character attacker, Character target, int era = 0)
    {
        int attackSkill = GetHitChanceSkill(attacker, GetWeaponSkill(attacker));
        int targetSkill = GetHitChanceSkill(target, GetWeaponSkill(target));
        int tacticsAtk = GetHitChanceTactics(attacker, attackSkill);
        int tacticsDef = GetHitChanceTactics(target, targetSkill);

        switch (era)
        {
            case 1: // pre-AOS
            {
                int chance = (attackSkill + 500) * 100 / Math.Max(1, (targetSkill + 500) * 2);
                return Math.Clamp(chance, 5, 95);
            }
            case 2: // AOS
            {
                int atkCalc = (attackSkill / 10 + 20) * 100;
                int defCalc = (targetSkill / 10 + 20) * 100;
                int chance = atkCalc * 100 / Math.Max(1, defCalc * 2);
                return Math.Clamp(chance, 5, 95);
            }
            default: // Sphere custom (era 0) — Source-X Calc_CombatChanceToHit
            {
                // Sleeping/frozen target: near-guaranteed hit window.
                if (target.IsStatFlag(StatFlag.Freeze))
                    return Math.Clamp(_rand.Next(10) * 10, 0, 100);

                int iSkillVal = attackSkill;
                // Offence: weapon skill + tactics, averaged.
                int iSkillAttack = (iSkillVal + tacticsAtk) / 2;
                // Defence: target's tactics blended with their DEX (the key
                // factor the old formula dropped entirely).
                int iSkillDefend = tacticsDef;
                int iStam = target.Dex;
                bool targetRanged = IsRangedSkill(GetWeaponSkill(target));
                bool attackerRanged = IsRangedSkill(GetWeaponSkill(attacker));
                if (targetRanged && !attackerRanged)
                    iSkillDefend = (iSkillDefend + iStam * 9) / 2;  // bows are easier to hit
                else
                    iSkillDefend = (iSkillDefend + iStam * 10) / 2;

                int iDiff = (iSkillAttack - iSkillDefend) / 5;
                iDiff = (iSkillVal - iDiff) / 10;
                return Math.Clamp(iDiff, 0, 100);
            }
        }
    }

    private static int GetHitChanceSkill(Character ch, SkillType skill)
    {
        int value = ch.GetSkill(skill);
        if (value > 0 || ch.IsPlayer || !IsWeaponSkill(skill))
            return value;

        return InferNpcCombatSkill(ch);
    }

    private static int GetHitChanceTactics(Character ch, int weaponSkill)
    {
        int value = ch.GetSkill(SkillType.Tactics);
        if (value > 0 || ch.IsPlayer)
            return value;

        return Math.Max(weaponSkill, InferNpcCombatSkill(ch));
    }

    private static int InferNpcCombatSkill(Character ch)
    {
        int stat = Math.Max(ch.Str, ch.Dex);
        return Math.Clamp(stat * 10, 250, 1000);
    }

    /// <summary>
    /// Calculate weapon damage range. Maps to Fight_CalcDamage in Source-X.
    /// </summary>
    /// <summary>Optional lookup for weapon definitions (BaseId → (damMin, damMax)).</summary>
    public static Func<ushort, (int Min, int Max)?>? WeaponDefLookup { get; set; }

    /// <summary>Optional lookup for NPC natural damage from CHARDEF (CharDefIndex → (damMin, damMax)).</summary>
    public static Func<int, (int Min, int Max)?>? NpcDamageDefLookup { get; set; }

    public static (int Min, int Max) CalcWeaponDamage(Character attacker, Item? weapon, int era = 0)
    {
        int dmgMin, dmgMax;

        if (weapon == null)
        {
            var npcDam = !attacker.IsPlayer ? NpcDamageDefLookup?.Invoke(attacker.CharDefIndex) : null;
            if (npcDam.HasValue && npcDam.Value.Max > 0)
            {
                dmgMin = npcDam.Value.Min;
                dmgMax = npcDam.Value.Max;
            }
            else
            {
                dmgMin = 1;
                dmgMax = Math.Max(2, attacker.Str / 4);
            }
        }
        else
        {
            var defDamage = WeaponDefLookup?.Invoke(weapon.BaseId);
            if (defDamage.HasValue && defDamage.Value.Max > 0)
            {
                dmgMin = defDamage.Value.Min;
                dmgMax = defDamage.Value.Max;
            }
            else
            {
                dmgMin = Math.Max(1, weapon.BaseId / 10);
                dmgMax = dmgMin + 5;
            }
        }

        // Source-X Fight_CalcDamage: the bonus is a PERCENTAGE applied to the
        // base damage and is era-specific (tactics/anatomy only count in era 1/2).
        int tactics = attacker.GetSkill(SkillType.Tactics);
        int anatomy = attacker.GetSkill(SkillType.Anatomy);
        int dmgBonus; // percent
        switch (era)
        {
            case 1: // pre-AOS
                dmgBonus = (tactics - 500) / 10;
                dmgBonus += anatomy / 50;
                if (anatomy >= 1000) dmgBonus += 10;
                if (weapon != null && weapon.ItemType == ItemType.WeaponAxe)
                {
                    int lj = attacker.GetSkill(SkillType.Lumberjacking);
                    dmgBonus += lj / 50;
                    if (lj >= 1000) dmgBonus += 10;
                }
                dmgBonus += attacker.Str * 20 / 100;
                break;
            case 2: // AOS
                dmgBonus = tactics / 16;
                dmgBonus += anatomy / 20;
                if (attacker.Str >= 100) dmgBonus += 5;
                dmgBonus += attacker.Str * 30 / 100;
                break;
            default: // era 0 — Sphere custom: STR% only, no tactics/anatomy
                dmgBonus = attacker.Str * 10 / 100;
                break;
        }

        dmgMin += dmgMin * dmgBonus / 100;
        dmgMax += dmgMax * dmgBonus / 100;

        return (Math.Max(1, dmgMin), Math.Max(1, dmgMax));
    }

    /// <summary>
    /// Calculate armor defense rating. Maps to CalcArmorDefense in Source-X.
    /// Non-elemental: sums equipped armor AR by body region coverage.
    /// </summary>
    public static int CalcArmorDefense(Character defender, bool elementalEngine = false)
    {
        if (elementalEngine)
            return 0; // elemental uses per-type resists

        int totalAR = 0;
        foreach (var region in _armorRegions)
        {
            var item = defender.GetEquippedItem(region.Layer);
            if (item == null) continue;

            int ar = item.GetArmorDefense();
            totalAR += region.Weight * ar;
        }

        return Math.Max(0, totalAR / 100);
    }

    public static ArmorHitRegion RollArmorHitRegion()
    {
        int total = 0;
        foreach (var region in _armorRegions)
            total += region.Weight;

        int roll = _rand.Next(total);
        int cumulative = 0;
        foreach (var region in _armorRegions)
        {
            cumulative += region.Weight;
            if (roll < cumulative)
                return region.Region;
        }

        return ArmorHitRegion.Chest;
    }

    public static Layer GetArmorLayerForRegion(ArmorHitRegion hitRegion)
    {
        foreach (var region in _armorRegions)
        {
            if (region.Region == hitRegion)
                return region.Layer;
        }

        return Layer.Chest;
    }

    public static int CalcArmorDefenseForRegion(Character defender, ArmorHitRegion hitRegion)
    {
        var armor = defender.GetEquippedItem(GetArmorLayerForRegion(hitRegion));
        return Math.Max(0, armor?.GetArmorDefense() ?? 0);
    }

    /// <summary>
    /// Perform a full attack resolution. Returns damage dealt (0 = miss/blocked).
    /// Maps to CChar::Fight_Hit flow in Source-X.
    /// </summary>
    public static int ResolveAttack(
        Character attacker,
        Character target,
        Item? weapon,
        CombatFlags flags = CombatFlags.None,
        int hitEra = -1,
        int damageEra = -1)
    {
        if (hitEra < 0) hitEra = Character.CombatHitChanceEra;
        if (damageEra < 0) damageEra = Character.CombatDamageEra;

        if (attacker.IsDead || target.IsDead)
            return 0;

        // Hit check
        int hitChance = CalcHitChance(attacker, target, hitEra);
        if (_rand.Next(100) >= hitChance)
            return 0; // miss

        // Calculate raw damage
        var (dmgMin, dmgMax) = CalcWeaponDamage(attacker, weapon, damageEra);
        // Guard against malformed weapon/NPC damage defs where Min > Max, which
        // would make Random.Next throw and crash the combat tick.
        if (dmgMax < dmgMin) dmgMax = dmgMin;
        int damage = _rand.Next(dmgMin, dmgMax + 1);

        // Parry check — Source-X Calc_CombatChanceToParry: a shield parries best,
        // a wielded weapon (one- or two-handed) can also parry at a lower rate.
        int parrySkill = target.GetSkill(SkillType.Parrying);
        if (parrySkill > 0)
        {
            var twoHanded = target.GetEquippedItem(Layer.TwoHanded);
            var oneHanded = target.GetEquippedItem(Layer.OneHanded);
            int parryChance;
            if (twoHanded != null && twoHanded.ItemType == ItemType.Shield)
                parryChance = parrySkill / 30;          // shield
            else if (twoHanded != null || oneHanded != null)
                parryChance = parrySkill / 80;          // weapon parry
            else
                parryChance = 0;                        // bare-handed: no parry
            if (parryChance > 0 && _rand.Next(100) < parryChance)
            {
                OnHitParry?.Invoke(target, attacker);
                return 0;
            }
        }

        // Armor reduction
        if (flags.HasFlag(CombatFlags.ElementalEngine))
        {
            // Split damage by attacker's elemental percentages, apply per-element resist
            damage = ApplyElementalDamageSplit(attacker, target, damage, weapon);
        }
        else
        {
            var hitRegion = RollArmorHitRegion();
            int armorRating = CalcArmorDefenseForRegion(target, hitRegion);
            int arMax = armorRating * _rand.Next(7, 36) / 100;
            int arMin = arMax / 2;
            int defense = _rand.Next(arMin, arMax + 1);
            damage -= defense;

            if (DurabilityEnabled)
                ApplyArmorDurabilityLoss(target, hitRegion);
        }

        damage = Math.Max(0, damage);

        // Weapon poison on-hit: transfer poison from weapon to target.
        // Source-X: HIT_POISON attribute on weapon. SphereNet: POISON_SKILL tag
        // set by Poisoning skill. Uses 1 charge per hit; cleared at 0.
        bool poisonApplied = false;
        if (damage > 0 && weapon != null && !flags.HasFlag(CombatFlags.NoPoisonHit))
        {
            if (weapon.TryGetTag("POISON_SKILL", out string? poisonStr) &&
                int.TryParse(poisonStr, out int poisonLevel) && poisonLevel > 0)
            {
                byte targetLevel = (byte)Math.Clamp(poisonLevel / 200, 1, 5);
                target.ApplyPoison(targetLevel, attacker.Uid);
                poisonApplied = true;

                int charges = 1;
                if (weapon.TryGetTag("POISON_CHARGES", out string? chargesStr))
                    int.TryParse(chargesStr, out charges);
                charges--;
                if (charges <= 0)
                {
                    weapon.RemoveTag("POISON_SKILL");
                    weapon.RemoveTag("POISON_CHARGES");
                }
                else
                    weapon.SetTag("POISON_CHARGES", charges.ToString());
            }
        }

        // Creature innate poison-on-hit (Source-X CChar::Fight_Hit: m_pNPC && 50%
        // && SKILL_POISONING > 0). A venomous creature — spider, snake, scorpion —
        // envenoms on a bite from its Poisoning skill alone, with no weapon and no
        // POISON_SKILL tag. Skipped when the weapon already poisoned this hit or
        // when poison hits are globally disabled. Poison level scales by skill on
        // the same 0-1000 → 1-5 curve the weapon path uses.
        if (damage > 0 && !poisonApplied && !attacker.IsPlayer &&
            !flags.HasFlag(CombatFlags.NoPoisonHit))
        {
            int poisonSkill = attacker.GetSkill(SkillType.Poisoning);
            if (poisonSkill > 0 && _rand.Next(100) < 50)
            {
                byte level = (byte)Math.Clamp(poisonSkill / 200, 1, 5);
                target.ApplyPoison(level, attacker.Uid);
            }
        }

        // Apply damage — do NOT call Kill() here; the caller handles death
        // via DeathEngine.ProcessDeath which creates the corpse, drops loot,
        // and sends the delete packet. Calling Kill() early makes
        // ProcessDeath bail out ("already dead") leaving a ghost NPC.
        if (damage > 0)
        {
            target.Hits -= (short)Math.Min(damage, short.MaxValue);
            target.RecordAttack(attacker.Uid, damage);

            if (target.IsStatFlag(StatFlag.Reactive) && attacker != target && !attacker.IsDead)
            {
                int reflect = Math.Max(1, damage / 4);
                attacker.Hits -= (short)Math.Min(reflect, short.MaxValue);
                // Credit reflected damage so a reactive-armor kill attributes to
                // the target (murder count / karma-fame / loot rights).
                attacker.RecordAttack(target.Uid, reflect);
            }

            if (DurabilityEnabled)
            {
                if (weapon != null)
                    ApplyDurabilityLoss(weapon);
            }
        }

        return damage;
    }

    private static void ApplyArmorDurabilityLoss(Character target, ArmorHitRegion hitRegion)
    {
        var layer = GetArmorLayerForRegion(hitRegion);
        var armor = target.GetEquippedItem(layer);
        if (armor != null)
            ApplyDurabilityLoss(armor);
    }

    private static void ApplyDurabilityLoss(Item item)
    {
        if (_rand.Next(100) >= DurabilityLossChance)
            return;

        int maxHits = item.GetHitsMax();
        if (maxHits <= 0)
        {
            maxHits = DefaultHits;
            item.HitsMax = maxHits;
            item.HitsCur = maxHits;
            return;
        }

        int curHits = item.GetHitsCur();
        int loss = DurabilityLossMin == DurabilityLossMax
            ? DurabilityLossMin
            : _rand.Next(DurabilityLossMin, DurabilityLossMax + 1);

        if (OnItemDamaged?.Invoke(item, loss) == true)
            return;

        curHits = Math.Max(0, curHits - loss);
        item.HitsCur = curHits;

        if (curHits <= 0 && BreakOnZeroHits)
            OnItemBroken?.Invoke(item);
    }

    /// <summary>
    /// Get the combat skill used by the attacker's weapon.
    /// Maps to CItem::Weapon_GetSkill in Source-X.
    /// </summary>
    /// <summary>True for ranged weapon skills (Source-X SKF_RANGED).</summary>
    private static bool IsRangedSkill(SkillType skill) =>
        skill is SkillType.Archery or SkillType.Throwing;

    private static bool IsWeaponSkill(SkillType skill) =>
        skill is SkillType.Wrestling or SkillType.Swordsmanship or SkillType.Fencing or
            SkillType.MaceFighting or SkillType.Archery or SkillType.Throwing;

    public static SkillType GetWeaponSkill(Character ch)
    {
        var weapon = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
        if (weapon == null)
            return SkillType.Wrestling;

        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe => SkillType.Swordsmanship,
            ItemType.WeaponFence => SkillType.Fencing,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
            ItemType.WeaponMacePick or ItemType.WeaponWhip => SkillType.MaceFighting,
            ItemType.WeaponBow or ItemType.WeaponXBow => SkillType.Archery,
            ItemType.WeaponThrowing => SkillType.Throwing,
            _ => SkillType.Wrestling,
        };
    }

    /// <summary>
    /// Get the damage type flags based on weapon type.
    /// Maps to Fight_GetWeaponDamType in Source-X.
    /// </summary>
    public static DamageType GetWeaponDamageType(Item? weapon)
    {
        if (weapon == null)
            return DamageType.HitBlunt;

        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponThrowing
                => DamageType.HitBlunt | DamageType.HitSlash,
            ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponXBow
                => DamageType.HitBlunt | DamageType.HitPierce,
            _ => DamageType.HitBlunt,
        };
    }

    /// <summary>
    /// Apply elemental resist to damage. Maps to COMBAT_ELEMENTAL_ENGINE in Source-X.
    /// Splits damage by attacker's elemental percentages then applies per-element resist.
    /// </summary>
    public static int ApplyElementalDamageSplit(Character attacker, Character target, int damage, Item? weapon)
    {
        int physPct = attacker.DamPhysical;
        int firePct = attacker.DamFire;
        int coldPct = attacker.DamCold;
        int poisonPct = attacker.DamPoison;
        int energyPct = attacker.DamEnergy;

        // If no elemental split defined, fall back to weapon damage type
        if (firePct == 0 && coldPct == 0 && poisonPct == 0 && energyPct == 0)
            return ApplyElementalResist(target, damage, GetWeaponDamageType(weapon));

        int sum = physPct + firePct + coldPct + poisonPct + energyPct;
        if (sum <= 0) sum = 100;

        int total = 0;
        if (physPct > 0) total += ApplyElementalResist(target, damage * physPct / sum, DamageType.Physical);
        if (firePct > 0) total += ApplyElementalResist(target, damage * firePct / sum, DamageType.Fire);
        if (coldPct > 0) total += ApplyElementalResist(target, damage * coldPct / sum, DamageType.Cold);
        if (poisonPct > 0) total += ApplyElementalResist(target, damage * poisonPct / sum, DamageType.Poison);
        if (energyPct > 0) total += ApplyElementalResist(target, damage * energyPct / sum, DamageType.Energy);
        return Math.Max(1, total);
    }

    /// <summary>
    /// Uses the highest matching resist for the damage type.
    /// </summary>
    public static int ApplyElementalResist(Character target, int damage, DamageType dmgType)
    {
        if (damage <= 0) return 0;

        int resist = 0;

        // Physical damage types (blunt/slash/pierce)
        if (dmgType.HasFlag(DamageType.Physical) || dmgType.HasFlag(DamageType.HitBlunt) ||
            dmgType.HasFlag(DamageType.HitSlash) || dmgType.HasFlag(DamageType.HitPierce))
            resist = Math.Max(resist, target.ResPhysical);

        if (dmgType.HasFlag(DamageType.Fire))
            resist = Math.Max(resist, target.ResFire);

        if (dmgType.HasFlag(DamageType.Cold))
            resist = Math.Max(resist, target.ResCold);

        if (dmgType.HasFlag(DamageType.Poison))
            resist = Math.Max(resist, target.ResPoison);

        if (dmgType.HasFlag(DamageType.Energy) || dmgType.HasFlag(DamageType.Magic))
            resist = Math.Max(resist, target.ResEnergy);

        // God damage ignores resist
        if (dmgType.HasFlag(DamageType.God))
            resist = 0;

        resist = Math.Clamp(resist, 0, 100);
        return damage - (damage * resist / 100);
    }

    /// <summary>
    /// Pre-AOS swing delay (Source-X <c>Calc_CombatAttackSpeed</c> formula 0).
    /// Returns the full swing recoil in milliseconds.
    /// </summary>
    public static int GetSwingDelayMs(Character attacker, Item? weapon)
    {
        int baseSpeed = weapon?.Speed > 0 ? weapon.Speed : 0;
        if (baseSpeed <= 0)
            baseSpeed = weapon == null ? 50 : 35;

        int dex = Math.Max(0, (int)attacker.Dex);
        long deciseconds;
        switch (Character.CombatSpeedEra)
        {
            case 2: // AOS: faster dex scaling, approximates UO:R/AOS weapon-speed feel.
                deciseconds = (long)Math.Round((40_000.0 / ((dex + 100) * Math.Max(1, baseSpeed))) * 10);
                break;
            case 1: // Pre-AOS: fixed weapon speed with lighter dex contribution.
                deciseconds = Math.Max(8, 50 - (baseSpeed / 2) - (dex / 30));
                break;
            default:
                const int speedScaleFactor = 15000;
                long iSwingSpeed = (long)(dex + 100) * baseSpeed;
                if (iSwingSpeed < 1) iSwingSpeed = 1;
                deciseconds = (speedScaleFactor * 10L) / iSwingSpeed;
                break;
        }

        if (deciseconds < 5) deciseconds = 5;

        if (weapon != null && weapon.IsTwoHanded)
            deciseconds += deciseconds / 4;

        int delayMs = (int)(deciseconds * 100);

        if (attacker.IsMounted)
            delayMs -= 200;

        return Math.Clamp(delayMs, 500, 7000);
    }
}
