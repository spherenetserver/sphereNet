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
/// On-hit trigger context threaded through <see cref="CombatEngine.OnHitDamage"/>
/// (the Source-X CChar::OnTakeDamage @GetHit block, CCharFight.cpp:750). The
/// engine seeds the armor-damage roll and the elemental split; the hook exposes
/// them to scripts as LOCAL.* and writes script changes back before the engine
/// rolls the durability wear.
/// </summary>
public sealed class HitDamageContext
{
    public required Character Attacker { get; init; }
    public required Character Target { get; init; }
    public Item? Weapon { get; init; }
    public int Damage { get; set; }

    /// <summary>Layer whose worn item takes the item @GetHit trigger and the
    /// durability wear (LOCAL.ItemDamageLayer, script-writable).</summary>
    public Layer ItemDamageLayer { get; set; }

    /// <summary>% chance the ItemDamageLayer item takes durability wear
    /// (LOCAL.ItemDamageChance, script-writable; Source-X seeds 25).</summary>
    public int ItemDamageChance { get; set; } = 25;

    /// <summary>Set by the hook when a trigger RETURNed 1: the hit is fully
    /// cancelled and skips the armor durability roll (Source-X returns 0
    /// before it).</summary>
    public bool Cancelled { get; set; }

    /// <summary>% chance the attacker's weapon takes durability wear on a hit
    /// (LOCAL.ItemDamageChance in the @Hit trigger args, script-writable;
    /// Source-X seeds 25 — a separate roll from the @GetHit armor-side
    /// ItemDamageChance above).</summary>
    public int WeaponDamageChance { get; set; } = 25;

    /// <summary>% chance a poisoned weapon loses poison charges after
    /// delivering (LOCAL.ItemPoisonReductionChance, script-writable;
    /// Source-X seeds 100).</summary>
    public int PoisonReductionChance { get; set; } = 100;

    /// <summary>Poison charges removed when the reduction chance passes
    /// (LOCAL.ItemPoisonReductionAmount, script-writable; SphereNet's
    /// level+charges poison model spends 1 per delivery by default).</summary>
    public int PoisonReductionAmount { get; set; } = 1;

    /// <summary>COMBAT_ELEMENTAL_ENGINE active — the DamagePercent* split below
    /// is exposed to @GetHit as read-only locals.</summary>
    public bool Elemental { get; init; }
    public int DamPercentPhysical { get; init; }
    public int DamPercentFire { get; init; }
    public int DamPercentCold { get; init; }
    public int DamPercentPoison { get; init; }
    public int DamPercentEnergy { get; init; }
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

    // Layers covering each hit region when COMBAT_STACKARMOR is on: a region
    // protected by several worn pieces (e.g. a tunic under a robe over the
    // chest) sums all their AR instead of only the primary layer's.
    private static readonly Dictionary<ArmorHitRegion, Layer[]> _stackArmorLayers = new()
    {
        [ArmorHitRegion.Head] = [Layer.Helm],
        [ArmorHitRegion.Neck] = [Layer.Neck],
        [ArmorHitRegion.Chest] = [Layer.Chest, Layer.Tunic, Layer.Shirt, Layer.Robe, Layer.Cape],
        [ArmorHitRegion.Arms] = [Layer.Arms, Layer.Robe],
        [ArmorHitRegion.Hands] = [Layer.Gloves],
        [ArmorHitRegion.Legs] = [Layer.Legs, Layer.Pants, Layer.Skirt, Layer.Robe],
        [ArmorHitRegion.Feet] = [Layer.Shoes],
    };

    public static bool DurabilityEnabled { get; set; }
    public static int DurabilityLossChance { get; set; } = 25;
    public static int DurabilityLossMin { get; set; } = 1;
    public static int DurabilityLossMax { get; set; } = 1;
    public static bool BreakOnZeroHits { get; set; } = true;
    public static int DefaultHits { get; set; } = 50;

    public static Action<Item>? OnItemBroken;
    public static Func<Item, int, bool>? OnItemDamaged;
    /// <summary>
    /// Fired on a successful parry. Returns the damage that still leaks THROUGH
    /// the parry — 0 (the default, when unwired or when no @HitParry script
    /// overrides ARGN1) is a full block; a positive value is a partial block.
    /// Args: defender, attacker, blockedDamage → damageThrough.
    /// </summary>
    public static Func<Character, Character, int, int>? OnHitParry;

    /// <summary>
    /// On-hit damage pipeline. Fires the @Hit / @GetHit char triggers and the
    /// weapon/armor item triggers on a connecting hit — after armor/parry have
    /// resolved a number, but BEFORE it is applied to HP — and returns the final
    /// damage. A script may raise, lower or fully cancel it (return &lt;= 0 or
    /// set <see cref="HitDamageContext.Cancelled"/>). Wired in the engine so
    /// the player and NPC swing paths share one trigger pipeline.
    /// </summary>
    public static Func<HitDamageContext, int>? OnHitDamage;

    /// <summary>Armor layers a hit may pick for the item @GetHit trigger and
    /// the durability wear (Source-X sm_ArmorDamageLayers, CCharFight.cpp:388).
    /// Hand layers (weapons/shields) are excluded.</summary>
    public static readonly Layer[] ArmorDamageLayers =
    [
        Layer.Shoes, Layer.Pants, Layer.Shirt, Layer.Helm, Layer.Gloves, Layer.Neck,
        Layer.Waist, Layer.Chest, Layer.Tunic, Layer.Arms, Layer.Cape, Layer.Robe,
        Layer.Skirt, Layer.Legs,
    ];

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
                // Sleeping/frozen target: Source-X returns rand(10) as the skill-
                // check DIFFICULTY (trivially easy → a near-certain hit). In this
                // percent model that means a fixed high chance, not a random one —
                // the old `rand(10)*10` averaged 45% and made paralyzed targets
                // dodge more than half the swings.
                if (target.IsStatFlag(StatFlag.Freeze))
                    return 95;

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

    // Source-X sm_ArmorLayers coverage percentages (CCharFight.cpp:409): the
    // share of the humanoid body each region contributes to the whole-body AR.
    private static readonly (ArmorHitRegion Region, int Coverage)[] _armorCoverage =
    [
        (ArmorHitRegion.Head, 15),
        (ArmorHitRegion.Neck, 7),
        (ArmorHitRegion.Chest, 35),
        (ArmorHitRegion.Arms, 14),
        (ArmorHitRegion.Hands, 7),
        (ArmorHitRegion.Legs, 22),
        (ArmorHitRegion.Feet, 0),
    ];

    /// <summary>
    /// Whole-body armor rating (Source-X CChar::CalcArmorDefense): each body
    /// region takes the best (or, with COMBAT_STACKARMOR, the summed) AR of
    /// the pieces covering it, weighted by the region's body-coverage percent.
    /// Every worn piece thus softens EVERY blow proportionally — the old
    /// single-region model made a helm matter only on the ~14% of hits that
    /// landed on the head.
    /// </summary>
    public static int CalcArmorDefense(Character defender)
    {
        bool stack = (Character.CombatFlags & (int)CombatFlags.StackArmor) != 0;
        int total = 0;
        foreach (var (region, coverage) in _armorCoverage)
        {
            if (coverage == 0) continue;
            int regionAr = 0;
            if (_stackArmorLayers.TryGetValue(region, out var layers))
            {
                foreach (var layer in layers)
                {
                    int def = Math.Max(0, defender.GetEquippedItem(layer)?.GetArmorDefense() ?? 0);
                    regionAr = stack ? regionAr + def : Math.Max(regionAr, def);
                }
            }
            total += coverage * regionAr;
        }

        int ar = Math.Max(0, total / 100 + defender.ModAr);

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar -= ar * discord / 100;
        return Math.Max(0, ar);
    }

    public static int CalcArmorDefenseForRegion(Character defender, ArmorHitRegion hitRegion)
    {
        int ar;
        // COMBAT_STACKARMOR: sum the AR of every piece covering this region.
        if ((Character.CombatFlags & (int)CombatFlags.StackArmor) != 0 &&
            _stackArmorLayers.TryGetValue(hitRegion, out var layers))
        {
            int total = 0;
            foreach (var layer in layers)
            {
                var piece = defender.GetEquippedItem(layer);
                if (piece != null)
                    total += Math.Max(0, piece.GetArmorDefense());
            }
            ar = total;
        }
        else
        {
            var armor = defender.GetEquippedItem(GetArmorLayerForRegion(hitRegion));
            ar = Math.Max(0, armor?.GetArmorDefense() ?? 0);
        }

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar -= ar * discord / 100;
        return Math.Max(0, ar);
    }

    /// <summary>Active Discordance defense penalty % on a character (0 when none
    /// or expired). Read from the DISCORD_PCT / DISCORD_UNTIL tags set by the
    /// Discordance skill — lazy expiry, no separate timer.</summary>
    private static int GetActiveDiscordPct(Character ch)
    {
        if (!ch.TryGetTag("DISCORD_PCT", out string? p) || !int.TryParse(p, out int pct) || pct <= 0)
            return 0;
        if (ch.TryGetTag("DISCORD_UNTIL", out string? u) && long.TryParse(u, out long until) &&
            Environment.TickCount64 > until)
            return 0;
        return Math.Clamp(pct, 0, 100);
    }

    /// <summary>Apply one durability-loss roll to an arbitrary item (e.g. a
    /// crafting tool). Honors the DurabilityEnabled gate and break handling.</summary>
    public static void DamageItem(Item item)
    {
        if (DurabilityEnabled)
            ApplyDurabilityLoss(item);
    }

    /// <summary>
    /// Perform a full attack resolution. Returns damage dealt (0 = miss/blocked).
    /// Maps to CChar::Fight_Hit flow in Source-X.
    /// </summary>
    /// <summary>Attacker's Damage Increase % (Source-X INCREASEDAM), from the
    /// INCREASEDAM tag. 0 when absent or unparseable.</summary>
    private static int GetDamageIncrease(Character ch) =>
        ch.TryGetTag("INCREASEDAM", out string? s) && int.TryParse(s, out int v) ? v : 0;

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

        // Hit check. A failed roll is a true miss: return -1 so the caller can
        // distinguish it from a connecting hit that armor fully absorbs (which
        // returns 0 — Source-X still plays the hit sound/animation for that).
        //
        // Era 0 is the Source-X two-stage roll: Calc_CombatChanceToHit returns
        // a RANDOM difficulty 0..iDiff which then runs Skill_CheckSuccess on
        // the attacker's weapon skill (bell curve). The drawn difficulty is
        // m_Act_Difficulty — it also feeds the passive skill gain below. The
        // old single percent-roll flattened the compounded variance.
        int hitChance;
        if (hitEra == 0)
        {
            // Frozen/sleeping target: the reference returns rand(10) — a
            // trivially easy difficulty — instead of the computed iDiff.
            int diffCap = target.IsStatFlag(StatFlag.Freeze)
                ? 9
                : CalcHitChance(attacker, target, 0);
            int actDifficulty = _rand.Next(diffCap + 1);
            int effSkill = GetHitChanceSkill(attacker, GetWeaponSkill(attacker));
            bool hitLanded = attacker.PrivLevel >= PrivLevel.GM ||
                Skills.SkillEngine.CheckSuccessValue(effSkill, actDifficulty);
            hitChance = actDifficulty; // m_Act_Difficulty for the gain rolls
            if (!hitLanded)
                return -1; // miss
        }
        else
        {
            hitChance = CalcHitChance(attacker, target, hitEra);
            if (_rand.Next(100) >= hitChance)
                return -1; // miss
        }

        // Calculate raw damage
        var (dmgMin, dmgMax) = CalcWeaponDamage(attacker, weapon, damageEra);
        // Guard against malformed weapon/NPC damage defs where Min > Max, which
        // would make Random.Next throw and crash the combat tick.
        if (dmgMax < dmgMin) dmgMax = dmgMin;
        int damage = _rand.Next(dmgMin, dmgMax + 1);

        // Damage Increase (Source-X PROPCH_INCREASEDAM): applies to players
        // always, and to NPCs only when COMBAT_NPC_BONUSDAMAGE is set, capped at
        // ±100%. Read from the attacker's INCREASEDAM tag (absent/0 = none).
        if (attacker.IsPlayer || flags.HasFlag(CombatFlags.NpcBonusDamage))
        {
            int di = Math.Clamp(GetDamageIncrease(attacker), -100, 100);
            if (di != 0)
                damage += damage * di / 100;
        }

        // Parry check — Source-X Calc_CombatChanceToParry (legacy formula):
        // shield = parry/40, wielded weapon = parry/80, +5 at GM parry, and
        // DEX below 80 erodes the chance proportionally.
        int parrySkill = target.GetSkill(SkillType.Parrying);
        if (parrySkill > 0)
        {
            var twoHanded = target.GetEquippedItem(Layer.TwoHanded);
            var oneHanded = target.GetEquippedItem(Layer.OneHanded);
            int parryChance;
            if (twoHanded != null && twoHanded.ItemType == ItemType.Shield)
                parryChance = parrySkill / 40;          // shield
            else if (twoHanded != null || oneHanded != null)
                parryChance = parrySkill / 80;          // weapon parry
            else
                parryChance = 0;                        // bare-handed: no parry
            if (parryChance > 0 && parrySkill >= 1000)
                parryChance += 5;
            int targetDex = target.Dex;
            if (parryChance > 0 && targetDex < 80)
                parryChance = parryChance * (100 - (80 - targetDex)) / 100;

            if (parryChance > 0)
            {
                // Source-X rolls the parry through Skill_UseQuick, which also
                // trains Parrying on the attempt.
                if (target.IsPlayer)
                    Skills.SkillEngine.GainExperience(target, SkillType.Parrying, hitChance);

                if (_rand.Next(100) < parryChance)
                {
                    // A parry fully blocks by default. A wired @HitParry can let
                    // some damage leak through (partial block) by returning a
                    // positive value; that damage then still runs through armor.
                    int through = OnHitParry?.Invoke(target, attacker, damage) ?? 0;
                    if (through <= 0)
                        return -1; // fully parried — treated as a miss by the caller
                    damage = Math.Min(damage, through);
                }
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
            // Source-X OnTakeDamage pre-AOS path: the WHOLE-BODY coverage-
            // weighted AR mitigates every hit; which worn piece takes the
            // durability wear is the @GetHit ItemDamageLayer roll below.
            int armorRating = CalcArmorDefense(target);
            int arMax = armorRating * _rand.Next(7, 36) / 100;
            int arMin = arMax / 2;
            int defense = _rand.Next(arMin, arMax + 1);
            damage -= defense;
        }

        damage = Math.Max(0, damage);

        // On-hit damage triggers (@Hit / @GetHit and the weapon/armor item
        // hooks) run here — after armor/parry resolved a number, but BEFORE it
        // is applied to HP — so a script may raise, lower or fully cancel the
        // damage. Source-X fires these around damage application; centralizing
        // them in one hook means the player and NPC swing paths share a single
        // pipeline. The context carries the Source-X @GetHit armor-damage roll
        // (LOCAL.ItemDamageLayer / ItemDamageChance) and the elemental split
        // (LOCAL.DamagePercent*) for the hook to expose to scripts.
        bool elemental = flags.HasFlag(CombatFlags.ElementalEngine);
        var split = elemental ? GetElementalSplit(attacker) : default;
        var hitCtx = new HitDamageContext
        {
            Attacker = attacker,
            Target = target,
            Weapon = weapon,
            Damage = damage,
            ItemDamageLayer = ArmorDamageLayers[_rand.Next(ArmorDamageLayers.Length)],
            Elemental = elemental,
            DamPercentPhysical = split.Phys,
            DamPercentFire = split.Fire,
            DamPercentCold = split.Cold,
            DamPercentPoison = split.Poison,
            DamPercentEnergy = split.Energy,
        };
        if (OnHitDamage != null)
            damage = Math.Max(0, OnHitDamage(hitCtx));

        // Source-X OnTakeDamage tail of the @GetHit block: the (script-final)
        // ItemDamageLayer item wears ItemDamageChance% of the time — in BOTH
        // armor modes, so elemental combat wears armor too. A RETURN 1 anywhere
        // in the chain (Cancelled) returned before this roll in Source-X.
        if (!hitCtx.Cancelled && DurabilityEnabled &&
            _rand.Next(100) < hitCtx.ItemDamageChance)
        {
            var itemHit = target.GetEquippedItem(hitCtx.ItemDamageLayer);
            if (itemHit != null)
                ApplyDurabilityLoss(itemHit);
        }

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
                // Source-X @Hit LOCAL.ItemPoisonReductionChance/Amount: the
                // script controls whether (and by how much) delivering the
                // poison spends the weapon's charges. Defaults (100% / 1)
                // reproduce the fixed 1-per-hit spend.
                if (_rand.Next(100) < hitCtx.PoisonReductionChance)
                    charges -= Math.Max(0, hitCtx.PoisonReductionAmount);
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

            // Source-X @Hit LOCAL.ItemDamageChance: the weapon wears only
            // WeaponDamageChance% of the time (script-writable, seeded 25).
            if (DurabilityEnabled && weapon != null &&
                _rand.Next(100) < hitCtx.WeaponDamageChance)
                ApplyDurabilityLoss(weapon);
        }

        // Passive combat skill gain — Source-X Fight_Hit tail: every landed
        // swing trains the active weapon skill AND Tactics for a player
        // attacker, unless the victim is a player standing in a NO_PVP region.
        // The gain difficulty mirrors m_Act_Difficulty (the 0-100 hit-chance
        // value), so easy prey stops training via the GAINRADIUS gate.
        if (attacker.IsPlayer)
        {
            bool noPvpBlock = false;
            if (target.IsPlayer)
            {
                var region = Objects.ObjBase.ResolveWorld?.Invoke()?.FindRegion(target.Position);
                noPvpBlock = region != null && region.IsFlag(RegionFlag.NoPvP);
            }
            if (!noPvpBlock)
            {
                Skills.SkillEngine.GainExperience(attacker, GetWeaponSkill(attacker), hitChance);
                Skills.SkillEngine.GainExperience(attacker, SkillType.Tactics, hitChance);
            }
        }

        return damage;
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

        // Item-level skill override: a weapon may declare a non-default combat
        // skill via TAG.OVERRIDE_SKILL (the SkillType number), so e.g. a blade
        // can be wielded with Fencing. Honored only when it names a real weapon
        // skill; otherwise the ItemType-inferred default below applies.
        if (weapon.TryGetTag("OVERRIDE_SKILL", out string? ovr) &&
            int.TryParse(ovr, out int ovrId) &&
            IsWeaponSkill((SkillType)ovrId))
            return (SkillType)ovrId;

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
        // If no elemental split defined, fall back to weapon damage type
        if (attacker.DamFire == 0 && attacker.DamCold == 0 &&
            attacker.DamPoison == 0 && attacker.DamEnergy == 0)
            return ApplyElementalResist(target, damage, GetWeaponDamageType(weapon));

        var (physPct, firePct, coldPct, poisonPct, energyPct) = GetElementalSplit(attacker);
        int sum = physPct + firePct + coldPct + poisonPct + energyPct;
        if (sum <= 0) sum = 100;

        int total = 0;
        if (physPct > 0) total += ApplyElementalResist(target, damage * physPct / sum, DamageType.Physical);
        if (firePct > 0) total += ApplyElementalResist(target, damage * firePct / sum, DamageType.Fire);
        if (coldPct > 0) total += ApplyElementalResist(target, damage * coldPct / sum, DamageType.Cold);
        if (poisonPct > 0) total += ApplyElementalResist(target, damage * poisonPct / sum, DamageType.Poison);
        if (energyPct > 0) total += ApplyElementalResist(target, damage * energyPct / sum, DamageType.Energy);
        // Source-X lets full resists zero the hit (CCharFight.cpp:730) — no
        // forced 1-damage floor.
        return total;
    }

    /// <summary>Attacker's elemental damage split percentages. Source-X
    /// OnTakeDamage (CCharFight.cpp:721): an unset physical share is assumed
    /// to be the remainder the elemental percents leave of 100.</summary>
    public static (int Phys, int Fire, int Cold, int Poison, int Energy) GetElementalSplit(Character attacker)
    {
        int fire = attacker.DamFire, cold = attacker.DamCold,
            poison = attacker.DamPoison, energy = attacker.DamEnergy;
        int phys = attacker.DamPhysical;
        if (phys == 0)
            phys = Math.Max(0, 100 - (fire + cold + poison + energy));
        return (phys, fire, cold, poison, energy);
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
