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

    /// <summary>UID of the pack ammo stack a ranged shot draws from (0 for
    /// melee) — exposed to @Hit and the weapon item @Hit as LOCAL.Arrow
    /// (Source-X CCharFight.cpp:2158).</summary>
    public uint AmmoUid { get; init; }

    /// <summary>Set when a @Hit-chain script wrote LOCAL.ArrowHandled=1: the
    /// script owns the ammo's fate, so the caller must neither consume the
    /// stack nor run the stick-in-body economy (Source-X pAmmo = nullptr).</summary>
    public bool ArrowHandled { get; set; }

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
    public const int AttackMiss = -1;
    public const int AttackParried = -2;
    public const int AttackResolvedByProc = -3;

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
        [ArmorHitRegion.Chest] = [Layer.Shirt, Layer.Chest, Layer.Tunic, Layer.Robe],
        [ArmorHitRegion.Arms] = [Layer.Arms, Layer.Cape, Layer.Robe],
        [ArmorHitRegion.Hands] = [Layer.Gloves],
        [ArmorHitRegion.Legs] = [Layer.Pants, Layer.Skirt, Layer.Waist, Layer.Robe, Layer.Legs],
        [ArmorHitRegion.Feet] = [Layer.Shoes, Layer.Legs],
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

    /// <summary>Leech feedback on an AOS on-hit drain (Source-X sound 0x44D
    /// at the attacker). Wired to a nearby-sound broadcast.</summary>
    public static Action<Character>? OnLeechEffect;

    /// <summary>HITAREA* splash (Source-X OnTakeDamageInflictArea): damage
    /// the chars around the struck target. Args: attacker, epicenter target,
    /// damage, damage type.</summary>
    public static Action<Character, Character, int, DamageType>? OnHitAreaDamage;

    /// <summary>HITFIREBALL/HARM/LIGHTNING/MAGICARROW/DISPEL proc (Source-X
    /// OnSpellEffect from Fight_Hit): cast the spell's effect directly on the
    /// victim — no cast time, mana or reagents. Args: attacker, target,
    /// SpellType id.</summary>
    public static Action<Character, Character, int>? OnHitSpell;

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
        => CalcHitChanceCore(attacker, target, era, GetWeaponSkill(attacker));

    private static int CalcHitChanceCore(Character attacker, Character target, int era,
        SkillType attackerWeaponSkill)
    {
        int attackSkill = GetHitChanceSkill(attacker, attackerWeaponSkill);
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
                bool attackerRanged = IsRangedSkill(attackerWeaponSkill);
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
                if (tactics >= 1000) dmgBonus += 6;
                dmgBonus += anatomy / 20;
                if (anatomy >= 1000) dmgBonus += 5;
                if (weapon != null && weapon.ItemType == ItemType.WeaponAxe)
                {
                    int lj = attacker.GetSkill(SkillType.Lumberjacking);
                    dmgBonus += lj / 50;
                    if (lj >= 1000) dmgBonus += 10;
                }
                if (attacker.Str >= 100) dmgBonus += 5;
                dmgBonus += attacker.Str * 30 / 100;
                break;
            default: // era 0 — Sphere custom: STR% only, no tactics/anatomy
                dmgBonus = attacker.Str * 10 / 100;
                break;
        }

        // Definitions and callback-provided ranges are external input. Keep
        // arithmetic in 64-bit space and normalise reversed/negative ranges
        // before Random receives them.
        int baseLow = Math.Min(dmgMin, dmgMax);
        int baseHigh = Math.Max(dmgMin, dmgMax);
        long adjustedLow = baseLow + (long)baseLow * dmgBonus / 100;
        long adjustedHigh = baseHigh + (long)baseHigh * dmgBonus / 100;
        int low = (int)Math.Clamp(Math.Min(adjustedLow, adjustedHigh), 1L, short.MaxValue);
        int high = (int)Math.Clamp(Math.Max(adjustedLow, adjustedHigh), 1L, short.MaxValue);
        return (low, high);
    }

    /// <summary>
    /// Calculate armor defense rating. Maps to CalcArmorDefense in Source-X.
    /// Non-elemental: sums equipped armor AR by body region coverage.
    /// </summary>
    public static int CalcArmorDefense(Character defender, bool elementalEngine = false)
    {
        // Keep the compatibility overload on the same implementation as the
        // exact overload. The former copy ignored STACKARMOR, MODAR and
        // Discordance, so CalcArmorDefense(ch, false) disagreed with
        // CalcArmorDefense(ch).
        return elementalEngine ? 0 : CalcArmorDefense(defender);
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
        long total = 0;
        foreach (var (region, coverage) in _armorCoverage)
        {
            if (coverage == 0) continue;
            long regionAr = 0;
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

        long rawAr = total / 100 + defender.ModAr;
        int ar = (int)Math.Clamp(rawAr, 0L, int.MaxValue);

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar = (int)Math.Max(0L, ar - (long)ar * discord / 100);
        return Math.Max(0, ar);
    }

    public static int CalcArmorDefenseForRegion(Character defender, ArmorHitRegion hitRegion)
    {
        int ar;
        // COMBAT_STACKARMOR: sum the AR of every piece covering this region.
        if ((Character.CombatFlags & (int)CombatFlags.StackArmor) != 0 &&
            _stackArmorLayers.TryGetValue(hitRegion, out var layers))
        {
            long total = 0;
            foreach (var layer in layers)
            {
                var piece = defender.GetEquippedItem(layer);
                if (piece != null)
                    total += Math.Max(0, piece.GetArmorDefense());
            }
            ar = (int)Math.Min(total, int.MaxValue);
        }
        else
        {
            var armor = defender.GetEquippedItem(GetArmorLayerForRegion(hitRegion));
            ar = Math.Max(0, armor?.GetArmorDefense() ?? 0);
        }

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar = (int)Math.Max(0L, ar - (long)ar * discord / 100);
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
    /// Perform a full attack resolution. Returns damage dealt, 0 for a
    /// connecting but fully absorbed/cancelled hit, or an Attack* sentinel.
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
        int damageEra = -1) =>
        ResolveAttack(attacker, target, weapon, flags, hitEra, damageEra, 0, out _);

    /// <summary>Full overload for the ranged path: <paramref name="ammoUid"/>
    /// is the pack ammo stack the shot draws from (exposed to the @Hit chain
    /// as LOCAL.Arrow); <paramref name="ammoHandled"/> reports a script's
    /// LOCAL.ArrowHandled=1 takeover so the caller skips the ammo economy.</summary>
    public static int ResolveAttack(
        Character attacker,
        Character target,
        Item? weapon,
        CombatFlags flags,
        int hitEra,
        int damageEra,
        uint ammoUid,
        out bool ammoHandled)
    {
        ammoHandled = false;
        if (hitEra < 0) hitEra = Character.CombatHitChanceEra;
        if (damageEra < 0) damageEra = Character.CombatDamageEra;

        if (attacker == target || attacker.MapIndex != target.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(attacker, asTarget: false) ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
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
                : CalcHitChanceCore(attacker, target, 0, GetWeaponSkill(attacker, weapon));
            int actDifficulty = _rand.Next(diffCap + 1);
            int effSkill = GetHitChanceSkill(attacker, GetWeaponSkill(attacker, weapon));
            bool hitLanded = attacker.PrivLevel >= PrivLevel.GM ||
                Skills.SkillEngine.CheckSuccessValue(effSkill, actDifficulty);
            hitChance = actDifficulty; // m_Act_Difficulty for the gain rolls
            if (!hitLanded)
                return AttackMiss;
        }
        else
        {
            hitChance = CalcHitChanceCore(attacker, target, hitEra, GetWeaponSkill(attacker, weapon));
            if (_rand.Next(100) >= hitChance)
                return AttackMiss;
        }

        // Calculate raw damage
        var (dmgMin, dmgMax) = CalcWeaponDamage(attacker, weapon, damageEra);
        // Guard against malformed weapon/NPC damage defs where Min > Max, which
        // would make Random.Next throw and crash the combat tick.
        if (dmgMax < dmgMin) (dmgMin, dmgMax) = (dmgMax, dmgMin);
        int damage = (int)_rand.NextInt64(dmgMin, (long)dmgMax + 1);

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
                        return AttackParried;
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
            int arMax = (int)Math.Min((long)armorRating * _rand.Next(7, 36) / 100, int.MaxValue);
            int arMin = arMax / 2;
            int defense = (int)_rand.NextInt64(arMin, (long)arMax + 1);
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
            ItemDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            WeaponDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            AmmoUid = ammoUid,
            Elemental = elemental,
            DamPercentPhysical = split.Phys,
            DamPercentFire = split.Fire,
            DamPercentCold = split.Cold,
            DamPercentPoison = split.Poison,
            DamPercentEnergy = split.Energy,
        };
        if (OnHitDamage != null)
            damage = Math.Clamp(OnHitDamage(hitCtx), 0, short.MaxValue);
        ammoHandled = hitCtx.ArrowHandled || hitCtx.Cancelled;

        // RETURN 1 is a full cancellation, irrespective of a buggy/custom
        // hook returning a positive number. Source-X exits before durability,
        // poison, procs, HP and skill gain; it also leaves ranged ammo alone.
        if (hitCtx.Cancelled)
            return 0;

        // Source-X OnTakeDamage tail of the @GetHit block: the (script-final)
        // ItemDamageLayer item wears ItemDamageChance% of the time — in BOTH
        // armor modes, so elemental combat wears armor too. A RETURN 1 anywhere
        // in the chain (Cancelled) returned before this roll in Source-X.
        if (!hitCtx.Cancelled && DurabilityEnabled &&
            _rand.Next(100) < Math.Clamp(hitCtx.ItemDamageChance, 0, 100))
        {
            var itemHit = target.GetEquippedItem(hitCtx.ItemDamageLayer);
            if (itemHit != null)
                ApplyDurabilityLoss(itemHit, rollConfiguredChance: false);
        }

        // COMBAT_SLAYER (Source-X OnTakeDamage, CCharFight.cpp:821): the
        // weapon's (or talisman's) SLAYER vs the victim's FACTION scales the
        // damage after the trigger chain has settled it.
        if (damage > 0 && flags.HasFlag(CombatFlags.Slayer))
            damage = ApplySlayerDamage(attacker, target, damage, weapon);
        damage = Math.Clamp(damage, 0, short.MaxValue);

        // AOS on-hit properties (Source-X Fight_Hit tail, CCharFight.cpp:2270):
        // leeches, mana drain, hit-area splashes and on-hit spell procs run on
        // any hit that dealt damage.
        if (damage > 0)
            ApplyAosOnHitEffects(attacker, target, damage, weapon, flags);

        // An on-hit spell/area callback can synchronously kill either party
        // through the normal spell/death engine. Do not continue the original
        // strike into a dead mobile (double damage, duplicate death feedback,
        // poison and durability side effects).
        if (attacker.IsDeleted || attacker.IsDead || target.IsDeleted || target.IsDead)
            return AttackResolvedByProc;

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
                charges = Math.Max(0, charges);
                // Source-X @Hit LOCAL.ItemPoisonReductionChance/Amount: the
                // script controls whether (and by how much) delivering the
                // poison spends the weapon's charges. Defaults (100% / 1)
                // reproduce the fixed 1-per-hit spend.
                if (_rand.Next(100) < Math.Clamp(hitCtx.PoisonReductionChance, 0, 100))
                {
                    long remaining = (long)charges - Math.Max(0, hitCtx.PoisonReductionAmount);
                    charges = (int)Math.Max(remaining, int.MinValue);
                }
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
                _rand.Next(100) < Math.Clamp(hitCtx.WeaponDamageChance, 0, 100))
                ApplyDurabilityLoss(weapon, rollConfiguredChance: false);
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
                Skills.SkillEngine.GainExperience(attacker, GetWeaponSkill(attacker, weapon), hitChance);
                Skills.SkillEngine.GainExperience(attacker, SkillType.Tactics, hitChance);
            }
        }

        return damage;
    }

    private static void ApplyDurabilityLoss(Item item, bool rollConfiguredChance = true)
    {
        int chance = Math.Clamp(DurabilityLossChance, 0, 100);
        if (rollConfiguredChance && _rand.Next(100) >= chance)
            return;

        int maxHits = item.GetHitsMax();
        if (maxHits <= 0)
        {
            maxHits = Math.Max(1, DefaultHits);
            item.HitsMax = maxHits;
            item.HitsCur = maxHits;
            return;
        }

        int curHits = item.GetHitsCur();
        int minLoss = Math.Max(0, Math.Min(DurabilityLossMin, DurabilityLossMax));
        int maxLoss = Math.Max(minLoss, Math.Max(DurabilityLossMin, DurabilityLossMax));
        int loss = minLoss == maxLoss
            ? minLoss
            : (int)_rand.NextInt64(minLoss, (long)maxLoss + 1);

        if (loss <= 0)
            return;

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
        return GetWeaponSkill(ch, weapon);
    }

    /// <summary>Weapon-snapshot overload used by delayed swings. The character
    /// parameter is retained for API symmetry and future char-level overrides.</summary>
    public static SkillType GetWeaponSkill(Character ch, Item? weapon)
    {
        if (weapon == null)
            return SkillType.Wrestling;

        // Item-level skill override: a weapon may declare a non-default combat
        // skill via TAG.OVERRIDE_SKILL (the SkillType number), so e.g. a blade
        // can be wielded with Fencing. Honored only when it names a real weapon
        // skill; otherwise the ItemType-inferred default below applies.
        string? ovr = null;
        if (!weapon.TryGetTag("OVERRIDE_SKILL", out ovr))
            weapon.TryGetTag("OVERRIDE.SKILL", out ovr);
        if (ovr != null &&
            int.TryParse(ovr, out int ovrId) &&
            IsWeaponSkill((SkillType)ovrId))
            return (SkillType)ovrId;

        var weaponDef = Definitions.DefinitionLoader.GetItemDef(weapon.BaseId);
        if (weaponDef is { HasSkill: true } && IsWeaponSkill(weaponDef.Skill))
            return weaponDef.Skill;

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

        string? overrideRaw = null;
        if (!weapon.TryGetTag("OVERRIDE.DAMAGETYPE", out overrideRaw))
            weapon.TryGetTag("OVERRIDE_DAMAGETYPE", out overrideRaw);
        if (overrideRaw == null)
        {
            var def = Definitions.DefinitionLoader.GetItemDef(weapon.BaseId);
            overrideRaw = def?.TagDefs.Get("OVERRIDE.DAMAGETYPE")
                ?? def?.TagDefs.Get("OVERRIDE_DAMAGETYPE");
        }
        if (!string.IsNullOrWhiteSpace(overrideRaw))
        {
            uint numeric = Objects.ObjBase.ParseHexOrDecUInt(overrideRaw);
            return (DamageType)(ushort)Math.Min(numeric, ushort.MaxValue);
        }

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
        var (physPct, firePct, coldPct, poisonPct, energyPct) = GetElementalSplit(attacker);
        long total = (long)damage * physPct * (100 - Math.Clamp((int)target.ResPhysical, 0, 100));
        total += (long)damage * firePct * (100 - Math.Clamp((int)target.ResFire, 0, 100));
        total += (long)damage * coldPct * (100 - Math.Clamp((int)target.ResCold, 0, 100));
        total += (long)damage * poisonPct * (100 - Math.Clamp((int)target.ResPoison, 0, 100));
        total += (long)damage * energyPct * (100 - Math.Clamp((int)target.ResEnergy, 0, 100));
        // Source-X lets full resists zero the hit (CCharFight.cpp:730) — no
        // forced 1-damage floor.
        return (int)Math.Clamp(total / 10_000, 0L, int.MaxValue);
    }

    /// <summary>COMBAT_SLAYER damage scaling (Source-X OnTakeDamage,
    /// CCharFight.cpp:835-877). The attacker's weapon SLAYER is consulted
    /// first; when it yields nothing, the equipped talisman is the fallback.
    /// An NPC victim takes the slayer bonus (Lesser +200% / Super +100%); a
    /// player victim hit by an NPC wielding a Super Slayer of the player's
    /// opposing faction group takes the Source-X penalty arithmetic (-100%).
    /// (Source-X also consults the spellbook for magic damage — SphereNet's
    /// spell damage does not flow through ResolveAttack, so that path is
    /// deferred.)</summary>
    public static int ApplySlayerDamage(Character attacker, Character target, int damage, Item? weapon)
    {
        var victimFaction = SlayerFaction.FromChar(target);
        if (victimFaction.IsNone)
            return damage;

        int bonusPct = 0;
        if (weapon != null)
            bonusPct = SlayerBonusPercent(SlayerFaction.FromItem(weapon), victimFaction, attacker, target);
        if (bonusPct == 0)
        {
            var talisman = attacker.GetEquippedItem(Layer.Talisman);
            if (talisman != null)
                bonusPct = SlayerBonusPercent(SlayerFaction.FromItem(talisman), victimFaction, attacker, target);
        }

        long scaled = damage + (long)damage * bonusPct / 100;
        return (int)Math.Clamp(scaled, 0L, int.MaxValue);
    }

    private static int SlayerBonusPercent(SlayerFaction slayer, SlayerFaction victimFaction,
        Character attacker, Character target)
    {
        if (slayer.IsNone)
            return 0;
        if (!target.IsPlayer)
            return slayer.GetSlayerDamageBonusPercent(victimFaction);
        if (!attacker.IsPlayer)
            return slayer.GetSlayerDamagePenaltyPercent(victimFaction);
        return 0;
    }

    /// <summary>AOS on-hit properties (Source-X Fight_Hit tail,
    /// CCharFight.cpp:2270-2361), with the reference formulas:
    /// HITLEECHLIFE heals rand(0 .. dmg×prop×30/10000), HITLEECHMANA
    /// rand(0 .. dmg×prop×40/10000), HITLEECHSTAM restores the full damage
    /// prop% of the time, HITMANADRAIN steals 20% of the damage from the
    /// victim's mana prop% of the time. With a weapon equipped, HITAREA*
    /// splash half the damage around the victim (the elemental variants only
    /// under COMBAT_ELEMENTAL_ENGINE) and HITDISPEL/FIREBALL/HARM/LIGHTNING/
    /// MAGICARROW proc their spell — both via engine hooks. Property values
    /// are read from the attacker plus the weapon and talisman (instance tag,
    /// then ITEMDEF def-tag).</summary>
    public static void ApplyAosOnHitEffects(Character attacker, Character target, int damage,
        Item? weapon, CombatFlags flags)
    {
        bool leeched = false;

        int leechLife = GetOnHitPropertyValue(attacker, weapon, "HITLEECHLIFE");
        if (leechLife > 0)
        {
            long maxHeal = (long)damage * leechLife * 30 / 10000;
            int heal = (int)_rand.NextInt64(0, Math.Min(maxHeal, short.MaxValue) + 1);
            attacker.Hits = (short)Math.Min((long)attacker.MaxHits, (long)attacker.Hits + heal);
            leeched = true;
        }

        int leechMana = GetOnHitPropertyValue(attacker, weapon, "HITLEECHMANA");
        if (leechMana > 0)
        {
            long maxGain = (long)damage * leechMana * 40 / 10000;
            int gain = (int)_rand.NextInt64(0, Math.Min(maxGain, short.MaxValue) + 1);
            attacker.Mana = (short)Math.Min((long)attacker.MaxMana, (long)attacker.Mana + gain);
            leeched = true;
        }

        if (RollOnHitChance(attacker, weapon, "HITLEECHSTAM"))
        {
            attacker.Stam = (short)Math.Min((long)attacker.MaxStam, (long)attacker.Stam + damage);
            leeched = true;
        }

        int manaDrain = 0;
        if (RollOnHitChance(attacker, weapon, "HITMANADRAIN"))
            manaDrain = (int)((long)damage * 20 / 100);
        manaDrain = Math.Min(manaDrain, (int)target.Mana);
        if (manaDrain > 0)
        {
            target.Mana = (short)(target.Mana - manaDrain);
            attacker.Mana = (short)Math.Min((long)attacker.MaxMana, (long)attacker.Mana + manaDrain);
            leeched = true;
        }

        if (leeched)
            OnLeechEffect?.Invoke(attacker);

        // The weapon-borne procs (Source-X gates the whole block on pWeapon).
        if (weapon == null)
            return;

        if (RollOnHitChance(attacker, weapon, "HITAREAPHYSICAL"))
            OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Physical);
        if (flags.HasFlag(CombatFlags.ElementalEngine))
        {
            if (RollOnHitChance(attacker, weapon, "HITAREAFIRE"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Fire);
            if (RollOnHitChance(attacker, weapon, "HITAREACOLD"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Cold);
            if (RollOnHitChance(attacker, weapon, "HITAREAPOISON"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Poison);
            if (RollOnHitChance(attacker, weapon, "HITAREAENERGY"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Energy);
        }

        if (RollOnHitChance(attacker, weapon, "HITDISPEL"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Dispel);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITFIREBALL"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Fireball);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITHARM"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Harm);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITLIGHTNING"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Lightning);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITMAGICARROW"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.MagicArrow);
        }
    }

    private static bool CanContinueOnHitProcs(Character attacker, Character target) =>
        !attacker.IsDeleted && !attacker.IsDead && !target.IsDeleted && !target.IsDead;

    private static bool RollOnHitChance(Character attacker, Item? weapon, string property) =>
        _rand.Next(100) < Math.Clamp(GetOnHitPropertyValue(attacker, weapon, property), 0, 100);

    /// <summary>An AOS on-hit property's effective value: the attacker's own
    /// tag plus the weapon's and the equipped talisman's (instance tag first,
    /// ITEMDEF def-tag fallback). Source-X reads a char-level aggregate;
    /// SphereNet aggregates at read time from the realistic carriers.</summary>
    public static int GetOnHitPropertyValue(Character attacker, Item? weapon, string prop)
    {
        long total = 0;
        if (attacker.TryGetTag(prop, out var raw) && int.TryParse(raw, out int own))
            total += own;
        if (weapon != null)
            total += GetItemNumProperty(weapon, prop);
        var talisman = attacker.GetEquippedItem(Layer.Talisman);
        if (talisman != null && talisman != weapon)
            total += GetItemNumProperty(talisman, prop);
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static int GetItemNumProperty(Item item, string prop)
    {
        if (item.TryGetTag(prop, out var raw) && int.TryParse(raw, out int v))
            return v;
        var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
        if (def != null && int.TryParse(def.TagDefs.Get(prop), out int dv))
            return dv;
        return 0;
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
        return (int)(damage - (long)damage * resist / 100);
    }

    /// <summary>
    /// Pre-AOS swing delay (Source-X <c>Calc_CombatAttackSpeed</c> formula 0).
    /// Returns the full swing recoil in milliseconds.
    /// </summary>
    public static int GetSwingDelayMs(Character attacker, Item? weapon)
    {
        int weaponSpeed = weapon?.Speed ?? 0;
        int baseSpeed = weaponSpeed > 0 ? weaponSpeed : 50;
        int dex = Math.Max(0, (int)attacker.Dex);
        long speedScale = Math.Max(1, Character.CombatSpeedScaleFactor);
        long deciseconds;
        switch (Character.CombatSpeedEra)
        {
            case 2: // AOS (Source-X era 2)
            {
                int swingSpeedIncrease = Math.Clamp(
                    GetOnHitPropertyValue(attacker, weapon, "INCREASESWINGSPEED"), -99, 100);
                long swingSpeed = (long)(dex + 100) * baseSpeed;
                swingSpeed = Math.Max(1, swingSpeed * (100 + swingSpeedIncrease) / 100);
                deciseconds = ((speedScale * 10) / swingSpeed) / 2;
                if (deciseconds < 12) deciseconds = 12;
                break;
            }
            case 1: // pre-AOS (Source-X era 1)
            {
                long swingSpeed = Math.Max(1, (long)(dex + 100) * baseSpeed);
                deciseconds = (speedScale * 10) / swingSpeed;
                if (deciseconds < 1) deciseconds = 1;
                break;
            }
            default: // Sphere custom (Source-X era 0)
            {
                if (weapon != null && weaponSpeed > 0)
                {
                    long swingSpeed = Math.Max(1, (long)(dex + 100) * weaponSpeed);
                    deciseconds = (speedScale * 10) / swingSpeed;
                    if (deciseconds < 5) deciseconds = 5;
                    break;
                }

                deciseconds = (long)(100 - dex) * 40 / 100;
                if (deciseconds < 5) deciseconds = 5;
                else deciseconds += 5;
                if (weapon != null)
                {
                    long weightMod = (long)Math.Max(0, weapon.Weight) * 10 /
                        (4 * Item.WeightUnits);
                    if (weapon.IsTwoHanded)
                        weightMod += deciseconds / 2;
                    deciseconds += weightMod;
                }
                else
                {
                    deciseconds += 2;
                }
                break;
            }
        }

        return (int)Math.Clamp(deciseconds * 100, 100L, 60_000L);
    }
}
