using SphereNet.Core.Enums;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Core.Types;

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
/// Damage type flags: Source-X DAMAGE_TYPE, a 32-bit mask whose values are the
/// ones scripts write (game_macros.h:55-74). Every script-facing surface — the
/// DAMAGE verb, ARGN2 of @Hit/@GetHit/@HitParry/@HitCheck, LOCAL.DamageType,
/// OVERRIDE.DAMAGETYPE, BREATH.DAMTYPE, THROWDAMTYPE — carries these numbers
/// unconverted, so the enum values must stay identical to the reference.
/// The type is never persisted.
/// </summary>
[Flags]
public enum DamageType : uint
{
    None = 0,
    God = 0x0001,           // DAMAGE_GOD: nothing can block this
    HitBlunt = 0x0002,      // DAMAGE_HIT_BLUNT
    Magic = 0x0004,         // DAMAGE_MAGIC
    Poison = 0x0008,        // DAMAGE_POISON
    Fire = 0x0010,          // DAMAGE_FIRE
    Energy = 0x0020,        // DAMAGE_ENERGY
    General = 0x0080,       // DAMAGE_GENERAL: all-over damage
    Acidic = 0x0100,        // DAMAGE_ACIDIC
    Cold = 0x0200,          // DAMAGE_COLD
    HitSlash = 0x0400,      // DAMAGE_HIT_SLASH
    HitPierce = 0x0800,     // DAMAGE_HIT_PIERCE
    NoDisturb = 0x2000,     // DAMAGE_NODISTURB: the victim is not disturbed (no spell interrupt)
    NoReveal = 0x4000,      // DAMAGE_NOREVEAL: the attacker is not revealed
    NoUnparalyze = 0x8000,  // DAMAGE_NOUNPARALYZE: the victim stays paralyzed
    Fixed = 0x10000,        // DAMAGE_FIXED: already final, no armor/resist
    Breath = 0x20000,       // DAMAGE_BREATH
    Thrown = 0x40000,       // DAMAGE_THROWN
    Reactive = 0x80000,     // DAMAGE_REACTIVE: a reflected blow, never reflected again

    /// <summary>Engine shorthand for a plain physical blow. The reference has no
    /// separate physical bit: a physical blow is HIT_BLUNT (the HITAREAPHYSICAL
    /// splash passes DAMAGE_HIT_BLUNT, CCharFight.cpp:2327).</summary>
    Physical = HitBlunt,
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
/// The attacker side of a connecting swing, threaded through
/// <see cref="CombatEngine.OnHitDamage"/>: the attacker's @Hit and then the weapon's
/// @Hit (Source-X Fight_Hit, CCharFight.cpp:2178-2195). Both see the RAW blow -
/// before the victim's armour - and may rewrite its damage (ARGN1) and type (ARGN2).
/// The victim's @GetHit is a later stage of the shared damage entry
/// (<see cref="GetHitContext"/>); the armor-damage roll below only seeds it.
/// </summary>
public sealed class HitDamageContext
{
    public required Character Attacker { get; init; }
    public required Character Target { get; init; }
    public Item? Weapon { get; init; }
    public int Damage { get; set; }

    /// <summary>ARGN2 of the @Hit stage: the blow's damage type, seeded from the
    /// weapon (Fight_GetWeaponDamType) and carried on to the victim's damage entry.</summary>
    public DamageType DamageType { get; set; }

    /// <summary>Seed for the victim's LOCAL.ItemDamageLayer (the worn piece that
    /// takes the item @GetHit and the durability wear).</summary>
    public Layer ItemDamageLayer { get; set; }

    /// <summary>Seed for the victim's @GetHit LOCAL.ItemDamageChance (Source-X 25).</summary>
    public int ItemDamageChance { get; set; } = 25;

    /// <summary>Set by the hook when a @Hit trigger RETURNed 1: the swing does no
    /// damage and nothing after it runs (Source-X returns WAR_SWING_EQUIPPING).</summary>
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

    /// <summary>Poison strength taken off the weapon when the reduction chance passes
    /// (LOCAL.ItemPoisonReductionAmount, script-writable; Source-X seeds it with
    /// half of this swing's poison dose).</summary>
    public int PoisonReductionAmount { get; set; } = 1;

    /// <summary>This swing's poison dose, rand(weapon MOREZ) - rolled before @Hit as
    /// upstream rolls iPoison (CCharFight.cpp:2154). Not script-visible.</summary>
    public int PoisonDose { get; set; }

    /// <summary>UID of the pack ammo stack a ranged shot draws from (0 for
    /// melee) — exposed to @Hit and the weapon item @Hit as LOCAL.Arrow
    /// (Source-X CCharFight.cpp:2158).</summary>
    public uint AmmoUid { get; init; }

    /// <summary>Set when a @Hit-chain script wrote LOCAL.ArrowHandled=1: the
    /// script owns the ammo's fate, so the caller must neither consume the
    /// stack nor run the stick-in-body economy (Source-X pAmmo = nullptr).</summary>
    public bool ArrowHandled { get; set; }
}

/// <summary>The victim-side @GetHit stage of <see cref="CombatEngine.ApplyCharacterDamage"/>
/// (Source-X CChar::OnTakeDamage, CCharFight.cpp:750-788): the char @GetHit and then
/// the item @GetHit on the worn piece at LOCAL.ItemDamageLayer, both seeing the damage
/// AFTER armour/resist. ARGN1 (damage) and ARGN2 (type) are written back; the damage
/// is final from here on and is not reduced again. Shared by every character-damage
/// source: a swing, the DAMAGE verb, a spell and a reflected blow.</summary>
public sealed class GetHitContext
{
    public required Character Target { get; init; }
    /// <summary>SRC of the trigger: the damage source, or the victim itself when the
    /// damage has nobody behind it (Source-X pSrc = this).</summary>
    public required Character Source { get; init; }
    public int Damage { get; set; }
    public DamageType DamageType { get; set; }
    /// <summary>LOCAL.Spell (0 when the damage is not a spell's).</summary>
    public int Spell { get; init; }

    /// <summary>LOCAL.ItemDamageLayer - the worn piece that takes the item @GetHit and
    /// the durability wear; script-writable in the char @GetHit.</summary>
    public Layer ItemDamageLayer { get; set; }
    /// <summary>LOCAL.ItemDamageChance (script-writable; Source-X seeds 25).</summary>
    public int ItemDamageChance { get; set; } = 25;

    /// <summary>COMBAT_ELEMENTAL_ENGINE active: the DamagePercent* split below is
    /// exposed as read-only locals.</summary>
    public bool Elemental { get; init; }
    public int DamPercentPhysical { get; init; }
    public int DamPercentFire { get; init; }
    public int DamPercentCold { get; init; }
    public int DamPercentPoison { get; init; }
    public int DamPercentEnergy { get; init; }

    /// <summary>A trigger RETURNed 1: no damage, and nothing after the stage runs.</summary>
    public bool Cancelled { get; set; }
}

/// <summary>Who handles the feedback (damage number, health bar, blood, spell
/// disturb, death) of a <see cref="CombatEngine.ApplyCharacterDamage"/> call.</summary>
public enum DamageFeedback
{
    /// <summary>The host's <see cref="CombatEngine.OnDirectCharacterDamageApplied"/>.</summary>
    Host,
    /// <summary>The caller does it itself (the swing and spell paths).</summary>
    Caller,
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
    /// <summary>Item @Damage hook (Source-X CItem::OnTakeDamage ITRIG_DAMAGE,
    /// CItem.cpp:5826-5832): item, damage (ARGN1), the character dealing it (SRC,
    /// may be null), damage type (ARGN2). Returns true when a script RETURN 1
    /// spared the item.</summary>
    public static Func<Item, int, Character?, DamageType, bool>? OnItemDamaged;
    /// <summary>Mutable @HitParry arguments (Source-X CCharFight.cpp:2095-2119).
    /// The reference documents them in the source itself:
    /// <list type="bullet">
    /// <item>ARGN1 = the PERCENT of the blow the parry takes off (100 = a full
    /// block, which is the default and what the Parrying skill's own EFFECT curve
    /// overrides when a pack defines one);</item>
    /// <item>ARGN2 = the damage type;</item>
    /// <item>ARGO = the item doing the parrying;</item>
    /// <item>LOCAL.ParryChance = the chance the roll uses — a script may raise it
    /// from zero or drop it to zero, which is why the trigger fires BEFORE the
    /// roll rather than after a successful one;</item>
    /// <item>LOCAL.ParrySkillID = which skill rolls and trains;</item>
    /// <item>LOCAL.ItemParryDamageChance = the chance the parrying item is worn;</item>
    /// <item>LOCAL.Damage = the raw damage before any parry reduction.</item>
    /// </list></summary>
    public sealed class HitParryContext
    {
        public int ReductionPercent { get; set; } = 100;
        public int DamageType { get; set; }
        public int ParryChance { get; set; }
        public int ParrySkillId { get; set; }
        public int ItemParryDamageChance { get; set; } = 100;
        public int Damage { get; set; }
        public Item? ParryItem { get; init; }
    }

    /// <summary>@HitParry. Fired BEFORE the parry roll, with the whole contract
    /// in <see cref="HitParryContext"/>; a false result is the trigger's RETURN 1
    /// and drops the blow entirely (CCharFight.cpp:2113).</summary>
    public static Func<Character, Character, HitParryContext, bool>? OnHitParry;

    /// <summary>A parry that actually landed — the visible block effect hangs off
    /// this rather than off the trigger, which now fires before the roll.</summary>
    public static Action<Character>? OnParrySucceeded;

    /// <summary>
    /// The attacker side of a connecting swing: the attacker's @Hit and the weapon's
    /// @Hit, on the RAW blow (after parry, before the victim's armour), returning the
    /// damage the scripts left (ARGN1) and writing their ARGN2 back into
    /// <see cref="HitDamageContext.DamageType"/>. RETURN 1 sets
    /// <see cref="HitDamageContext.Cancelled"/>. Shared by the player and NPC swings.
    /// </summary>
    public static Func<HitDamageContext, int>? OnHitDamage;

    /// <summary>The victim's @GetHit stage of the shared character-damage entry: char
    /// @GetHit, then item @GetHit on the worn piece at LOCAL.ItemDamageLayer. Returns
    /// the script-final damage (not reduced again); ARGN2 and the armor-damage locals
    /// are written back into the context, RETURN 1 sets Cancelled.</summary>
    public static Func<GetHitContext, int>? OnGetHit;

    /// <summary>Host feedback after character damage that the caller does not handle
    /// itself (<see cref="DamageFeedback.Host"/>) is applied: interrupt casting,
    /// broadcast damage/health and run the death engine.</summary>
    public static Action<Character, Character?, int, DamageType>? OnDirectCharacterDamageApplied;

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

    /// <summary>What Reactive Armour is about to do with a blow, handed to the script
    /// and read back. The reference describes the bounce entirely in locals so a
    /// @HitReactive block can change any part of it - how much comes off the blow, how
    /// much lands on the attacker, and what the two of them see and hear
    /// (CCharFight.cpp:965-990).</summary>
    public sealed class ReactiveArmorContext
    {
        public required Character Defender { get; init; }
        public required Character Attacker { get; init; }
        /// <summary>The blow as it stands, before the bounce is taken out of it.</summary>
        public required int Damage { get; init; }
        /// <summary>LOCAL.Damage - the bounce the percentage worked out to.</summary>
        public int Bounce { get; set; }
        /// <summary>LOCAL.ReflectDamage - what the attacker takes.</summary>
        public int Reflect { get; set; }
        /// <summary>LOCAL.ReduceDamage - what comes off the blow.</summary>
        public int Reduce { get; set; }
        /// <summary>LOCAL.Sound / LOCAL.EffectID.</summary>
        public ushort Sound { get; set; }
        public ushort EffectId { get; set; }
        /// <summary>LOCAL.DamageType - the type the bounce reaches the attacker with
        /// (Source-X seeds DAMAGE_FIXED|DAMAGE_REACTIVE, CCharFight.cpp:963).</summary>
        public DamageType DamageType { get; set; } = DamageType.Fixed | DamageType.Reactive;
    }

    /// <summary>@HitReactive bridge: the host fires the trigger and copies the script's
    /// locals back into the context. Null when no script hooks it.</summary>
    public static Action<ReactiveArmorContext>? OnReactiveArmorTrigger;

    /// <summary>Reactive Armour feedback on the attacker: the sound and the effect the
    /// context ended up with. Args: defender, attacker, sound, effect id.</summary>
    public static Action<Character, Character, ushort, ushort>? OnReactiveArmorFeedback;

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
                return Math.Clamp(chance, 0, 100); // CResourceCalc.cpp:202
            }
            case 2: // AOS
            {
                // Source-X Calc_CombatChanceToHit era-2 (CResourceCalc.cpp:208):
                // each side scales by its Hit/Defense Chance Increase, capped at
                // +45%. HCI/DCI are AOS suit-wide properties (Source-X accumulates
                // every equipped item's value into PROPCH_INCREASEHITCHANCE/
                // INCREASEDEFCHANCE at equip time), so read the full suit via
                // GetEquipmentPropertyValue (char tag + all layers) — the previous
                // GetOnHitPropertyValue(...,weapon:null,...) missed armor/jewelry HCI.
                int hci = Math.Clamp(GetEquipmentPropertyValue(attacker, "INCREASEHITCHANCE"), 0, 45);
                int dci = Math.Clamp(GetEquipmentPropertyValue(target, "INCREASEDEFCHANCE"), 0, 45);
                int atkCalc = (attackSkill / 10 + 20) * (100 + hci);
                int defCalc = (targetSkill / 10 + 20) * (100 + dci);
                int chance = atkCalc * 100 / Math.Max(1, defCalc * 2);
                return Math.Clamp(chance, 2, 100); // minimum 2% (CResourceCalc.cpp:226)
            }
            default: // Sphere custom (era 0) — Source-X Calc_CombatChanceToHit
            {
                // The value returned is the ceiling of the random draw Source-X makes
                // (Calc_CombatChanceToHit returns rand(iDiff)). A sleeping or frozen
                // target draws from rand(10) instead (CResourceCalc.cpp:153).
                if (target.IsStatFlag(StatFlag.Sleeping) || target.IsStatFlag(StatFlag.Freeze))
                    return 10;

                int iSkillVal = attackSkill;
                // Offence: weapon skill + tactics, averaged.
                int iSkillAttack = (iSkillVal + tacticsAtk) / 2;
                // Defence: target's tactics blended with their DEX (the key
                // factor the old formula dropped entirely).
                int iSkillDefend = tacticsDef;
                // Stat_GetVal(STAT_DEX) is the CURRENT stamina, so a tired defender
                // is easier to hit (CResourceCalc.cpp:175).
                int iStam = target.Stam;
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
        // Source-X uses the raw chardef/@Create skill — an NPC whose def grants
        // no weapon skill fights at 0 and almost never lands a hit. The old
        // stat*10 inference (floor 25.0) had no reference basis and silently
        // buffed every under-scripted NPC.
        return ch.GetSkill(skill);
    }

    private static int GetHitChanceTactics(Character ch, int weaponSkill)
    {
        return ch.GetSkill(SkillType.Tactics);
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
            // Source-X Fight_CalcDamage unarmed: iDmgMin = m_attackBase, i.e.
            // the CHARDEF's DAM line — for players too (c_man DAM=1,4 is the
            // classic fists damage). The old path invented Str/4 for anyone
            // whose chardef didn't resolve.
            // The creature's OWN rating first: stamped from the chardef when it was
            // made, and changeable on the one creature (CCharFight.cpp:1220).
            if (attacker.AttackBaseRaw is > 0)
            {
                dmgMin = attacker.AttackLo;
                dmgMax = attacker.AttackHi;
            }
            else if (NpcDamageDefLookup?.Invoke(attacker.CharDefIndex) is { Max: > 0 } defDam)
            {
                dmgMin = defDam.Min;
                dmgMax = defDam.Max;
            }
            else if (attacker.IsPlayer)
            {
                dmgMin = 1; // c_man DAM=1,4 — classic bare-fist range
                dmgMax = 4;
            }
            else
            {
                dmgMin = 0; // chardef declares no DAM → attackBase 0
                dmgMax = 1;
            }

            // Source-X FEATURE_AOS_UPDATE_B Horrific Beast form replaces the
            // character's natural (unarmed) base damage with 5-15.
            if (HasHorrificBeastCombatForm(attacker))
            {
                dmgMin = 5;
                dmgMax = 15;
            }
        }
        else
        {
            // The weapon's OWN rating first. Upstream reads m_attackBase off the item
            // (CCharFight.cpp:1220), which is stamped from the definition when the
            // item is made - so a script that changed this one weapon is what decides
            // its damage, and every other weapon of the kind is untouched.
            if (weapon.AttackBaseRaw is > 0)
            {
                dmgMin = weapon.AttackLo;
                dmgMax = weapon.AttackHi;
            }
            else if (WeaponDefLookup?.Invoke(weapon.BaseId) is { Max: > 0 } defDamage)
            {
                dmgMin = defDamage.Min;
                dmgMax = defDamage.Max;
            }
            else
            {
                // Source-X Weapon_GetAttack: a weapon whose def declares no DAM
                // has attackBase 0 (anything non-weapon acting as one hits 1).
                // The old fallback derived damage from the ART TILE ID (/10),
                // so high-graphic items hit absurdly hard.
                dmgMin = 0;
                dmgMax = 1;
            }

            // The weapon's MODAR shifts both ends: Weapon_GetAttack(false/true) is
            // m_attackBase (+ m_attackRange) + m_ModAr, floored at 0 (CItem.cpp:4922).
            if (weapon.ModAr != 0)
            {
                dmgMin = (int)Math.Max(0L, (long)dmgMin + weapon.ModAr);
                dmgMax = (int)Math.Max(0L, (long)dmgMax + weapon.ModAr);
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
                dmgBonus += EffectiveStr(attacker) * 20 / 100;
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
                if (EffectiveStr(attacker) >= 100) dmgBonus += 5;
                dmgBonus += EffectiveStr(attacker) * 30 / 100;
                break;
            default: // era 0 — Sphere custom: STR% only, no tactics/anatomy
                dmgBonus = EffectiveStr(attacker) * 10 / 100;
                break;
        }
        // The whole bonus is a player's (or every NPC's under COMBAT_NPC_BONUSDAMAGE):
        // Fight_CalcDamage wraps it in m_pPlayer || IsSetCombatFlags(...)
        // (CCharFight.cpp:1235). Monsters got their STR percent on top of DAM.
        if (!attacker.IsPlayer && (Character.CombatFlags & (int)CombatFlags.NpcBonusDamage) == 0)
            dmgBonus = 0;

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
        bool scaleShield = (Character.CombatParryingEra & (int)ParryEraFlags.ArmorScaling) != 0;
        Item? shield = defender.GetEquippedItem(Layer.TwoHanded);
        int shieldDefense = shield?.ItemType == ItemType.Shield
            ? Math.Max(0, shield.GetArmorDefense())
            : 0;
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
            // Without PARRYERA_ARSCALING Source-X places shield AR in the
            // hands zone, so it contributes the legacy flat 7% coverage and
            // competes/stacks with gloves exactly like another hands piece.
            if (region == ArmorHitRegion.Hands && shieldDefense > 0 && !scaleShield)
                regionAr = stack ? regionAr + shieldDefense : Math.Max(regionAr, shieldDefense);
            total += coverage * regionAr;
        }

        if (shieldDefense > 0 && scaleShield)
        {
            // Source-X PARRYERA_ARSCALING: ((base Parry * shield AR) / 2000) + 1,
            // capped at half of the shield's base AR, with 100% body coverage.
            int parrying = defender.GetSkill(SkillType.Parrying);
            long skillScaled = ((long)parrying * shieldDefense / 2000) + 1;
            total += 100L * Math.Min(shieldDefense / 2L, skillScaled);
        }

        long rawAr = total / 100 + defender.ModAr + defender.ProtectionArmor;
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
        if (!ch.TryGetTag("DISCORD_PCT", out string? p) || !ScriptNumber.TryParseInt(p, out int pct) || pct <= 0)
            return 0;
        if (ch.TryGetTag("DISCORD_UNTIL", out string? u) && ScriptNumber.TryParseLong(u, out long until) &&
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

    /// <summary>The bounce at the top of Source-X CChar::OnTakeDamage (CCharFight.cpp:
    /// 640-650): a blow without DAMAGE_GOD does nothing to an invulnerable or petrified
    /// character, nor fire to a fire-immune one. The shared character-damage entry
    /// (<see cref="ApplyCharacterDamage"/>) applies it; the damage sites that still
    /// write hit points themselves (fields, traps, splashes) consult it directly.</summary>
    public static bool IsDamageImmune(Character target, DamageType type = DamageType.Physical)
        => !type.HasFlag(DamageType.God) &&
           (target.IsStatFlag(StatFlag.Invul) || target.IsStatFlag(StatFlag.Stone) ||
            (type.HasFlag(DamageType.Fire) && (SphereNet.Game.Definitions.CharDefHelper.GetCanFlags(target) & CanFlags.C_FireImmune) != 0));

    /// <summary>The region half of the OnTakeDamage bounce (CCharFight.cpp:656-676): a
    /// SAFE region protects everybody; a NO_PVP region protects a player from another
    /// player and from a pet whose top owner is a player. <paramref name="source"/> is
    /// the victim itself for sourceless damage, as the reference's pSrc = this is.</summary>
    public static bool IsRegionProtected(Character target, Character source)
    {
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        var region = world?.FindRegion(target.Position);
        if (region == null)
            return false;
        if (region.IsFlag(RegionFlag.Safe))
            return true;
        if (!region.IsFlag(RegionFlag.NoPvP) || !target.IsPlayer)
            return false;
        if (source.IsPlayer)
            return true;
        var owner = ResolvePetOwnerRecursive(world!, source);
        return owner != null && owner.IsPlayer;
    }

    /// <summary>NPC_PetGetOwnerRecursive: the top of a pet's ownership chain (a pet
    /// owned by a pet owned by a player resolves to the player).</summary>
    private static Character? ResolvePetOwnerRecursive(World.GameWorld world, Character pet)
    {
        Character? owner = null;
        var current = pet;
        for (int depth = 0; depth < 16 && !current.IsPlayer && current.OwnerSerial.IsValid; depth++)
        {
            var next = world.FindChar(current.OwnerSerial);
            if (next == null || next == current)
                break;
            owner = next;
            current = next;
        }
        return owner;
    }

    /// <summary>FEATURE_AOS_UPDATE_B in FEATUREAOS: the AOS necromancy curses and
    /// Focus regeneration (Source-X game_enums.h).</summary>
    public const int FeatureAosUpdateB = 0x02;

    private static bool IsSpellScripted(SpellType spell) =>
        Character.ResolveSpellDef?.Invoke(spell)?.IsFlag(SpellFlag.Scripted) == true;

    /// <summary>
    /// The one character-damage entry: Source-X CChar::OnTakeDamage (CCharFight.cpp:
    /// 633-1062). A swing (after its @Hit stage), the DAMAGE verb, a damaging spell
    /// (after @SpellEffect) and every reflected blow come through here, so they share
    /// one order:
    /// <list type="number">
    /// <item>the protection gates - invulnerable/stone, fire immunity, SAFE and NO_PVP
    /// regions - none of which stop DAMAGE_GOD;</item>
    /// <item>OnAttackedBy (aggressor memory, crime, retaliation) and the reveal of the
    /// attacker unless DAMAGE_NOREVEAL;</item>
    /// <item>Evil Omen (+25%) and Blood Oath (+10%, and the bonded attacker takes
    /// (100 - level)% of that raised RAW blow back as MAGIC|FIXED damage);</item>
    /// <item>armour: COMBAT_ELEMENTAL_ENGINE's resist split, else the pre-AOS armour
    /// roll (halved against magic) - skipped for DAMAGE_GOD and DAMAGE_FIXED;</item>
    /// <item>the victim's @GetHit and the worn piece's item @GetHit, which see the
    /// REDUCED damage and whose ARGN1/ARGN2 are final (never reduced again);</item>
    /// <item>armour wear, unparalyze (unless DAMAGE_NOUNPARALYZE), COMBAT_SLAYER;</item>
    /// <item>the attacker list, then Reactive Armour and REFLECTPHYSICALDAM for a
    /// physical blow - the bounce comes back through this same entry flagged
    /// DAMAGE_REACTIVE, so it is protected, triggers @GetHit and never bounces again;</item>
    /// <item>the hit points.</item>
    /// </list>
    /// <paramref name="damageType"/> uses the Source-X DAMAGE_* numbering. Returns the
    /// damage taken off the hit points (0 when none).
    /// </summary>
    public static int ApplyCharacterDamage(
        Character target,
        int damage,
        Character? source,
        DamageType damageType,
        int physicalPercent = 0,
        int firePercent = 0,
        int coldPercent = 0,
        int poisonPercent = 0,
        int energyPercent = 0,
        int spell = 0,
        DamageFeedback feedback = DamageFeedback.Host,
        CombatFlags? combatFlags = null,
        HitDamageContext? swing = null)
    {
        if (target.IsDeleted || target.IsDead)
            return 0;
        // A blow with nobody behind it is the victim's own (pSrc = this, :636).
        var src = source != null && !source.IsDeleted ? source : target;
        var type = damageType;
        var flags = combatFlags ?? (CombatFlags)Character.CombatFlags;

        // Protection gates (:640-677).
        if ((type & DamageType.God) == 0 &&
            (IsDamageImmune(target, type) || IsRegionProtected(target, src)))
            return 0;

        // OnAttackedBy (:681): a stone victim ignores it (even DAMAGE_GOD), and the
        // attacker is revealed unless the blow carries DAMAGE_NOREVEAL.
        if (src != target)
        {
            if (target.IsStatFlag(StatFlag.Stone))
                return 0;
            if ((type & DamageType.NoReveal) == 0)
                src.ClearHiddenState();
            if (!target.OnAttackedBy(src))
                return 0;
        }

        // Necromancy cursed effects (:684-703), on the RAW blow - only with
        // FEATURE_AOS_UPDATE_B (:688).
        bool aosUpdateB = (Character.FeatureAOS & FeatureAosUpdateB) != 0;
        if (aosUpdateB && !IsSpellScripted(SpellType.EvilOmen) && target.ConsumeEvilOmen())
            damage += damage / 4;
        // Blood Oath: the bond links the victim to the attacker. A FIXED blow is
        // already a reflection and must not come back again (:697).
        if (aosUpdateB && src != target && target.BloodOathEnemy == src.Uid && target.BloodOathLevel > 0 &&
            (type & DamageType.Fixed) == 0 && !IsSpellScripted(SpellType.BloodOath))
        {
            damage += damage / 10;
            ApplyCharacterDamage(src, damage * (100 - target.BloodOathLevel) / 100, target,
                DamageType.Magic | DamageType.Fixed, spell: (int)SpellType.BloodOath);
            if (target.IsDeleted || target.IsDead)
                return 0;
        }

        // MAGICF_IGNOREAR bypasses defence completely (:710).
        if ((type & DamageType.Magic) != 0 &&
            (Character.MagicFlags & (int)MagicConfigFlags.IgnoreArmor) != 0)
            type |= DamageType.Fixed;

        // Armour (:713-747): the elemental split or the pre-AOS roll - one choice,
        // made here for every kind of damage.
        bool elemental = flags.HasFlag(CombatFlags.ElementalEngine);
        if ((type & (DamageType.God | DamageType.Fixed)) == 0)
        {
            if (elemental)
            {
                if (physicalPercent == 0)
                    physicalPercent = 100 - (firePercent + coldPercent + poisonPercent + energyPercent);
                damage = ApplyDamageSplitResist(target, damage,
                    physicalPercent, firePercent, coldPercent, poisonPercent, energyPercent);
            }
            else
            {
                damage = ApplyPreAosArmor(target, damage, type);
            }
        }

        // @GetHit, then the worn piece's item @GetHit (:749-788). Their ARGN1 is the
        // final damage: it is not reduced a second time.
        var getHit = new GetHitContext
        {
            Target = target,
            Source = src,
            Damage = damage,
            DamageType = type,
            Spell = spell,
            ItemDamageLayer = swing?.ItemDamageLayer ?? ArmorDamageLayers[_rand.Next(ArmorDamageLayers.Length)],
            ItemDamageChance = swing?.ItemDamageChance ?? Math.Clamp(DurabilityLossChance, 0, 100),
            Elemental = elemental,
            DamPercentPhysical = physicalPercent,
            DamPercentFire = firePercent,
            DamPercentCold = coldPercent,
            DamPercentPoison = poisonPercent,
            DamPercentEnergy = energyPercent,
        };
        if (OnGetHit != null)
        {
            damage = OnGetHit(getHit);
            if (getHit.Cancelled)
                return 0;
            type = getHit.DamageType;
        }
        damage = Math.Clamp(damage, short.MinValue, short.MaxValue);

        // The worn piece at the script-final ItemDamageLayer takes the wear
        // ItemDamageChance% of the time (:790-793); a non-humanoid wears nothing.
        if (DurabilityEnabled &&
            (SphereNet.Game.Definitions.CharDefHelper.GetCanFlags(target) & CanFlags.C_NonHumanoid) == 0 &&
            _rand.Next(100) < Math.Clamp(getHit.ItemDamageChance, 0, 100))
        {
            var itemHit = target.GetEquippedItem(getHit.ItemDamageLayer);
            if (itemHit != null)
                ApplyDurabilityLoss(itemHit, rollConfiguredChance: false,
                    source: src, triggerDamage: damage, damageType: type);
        }

        // Unparalyze (:797-818): any blow without DAMAGE_NOUNPARALYZE ends a paralysis
        // (the spell's own NOUNPARALYZE flag keeps the paralyze memory) and a freeze.
        if ((type & DamageType.NoUnparalyze) == 0)
        {
            var spellDef = spell != 0 ? Character.ResolveSpellDef?.Invoke((SpellType)spell) : null;
            if (spellDef == null || !spellDef.IsFlag(SpellFlag.NoUnparalyze))
                Character.BreakParalyzeHook?.Invoke(target);
            if (target.IsStatFlag(StatFlag.Freeze))
                target.ClearStatFlag(StatFlag.Freeze);
        }

        // COMBAT_SLAYER (:820-877): the swing's weapon, or for magic the spellbook.
        if (flags.HasFlag(CombatFlags.Slayer))
            damage = ApplySlayerDamage(src, target, damage, ResolveSlayerSource(src, type, swing));

        if (src != target)
        {
            // The attacker list (:913-940) records the blow before any bounce.
            target.RecordAttack(src.Uid, Math.Max(0, damage));

            // A physical blow of some sort (:943-1026).
            if (IsReflectableBlow(src, target, type))
            {
                if ((type & (DamageType.God | DamageType.Reactive)) == 0)
                    ApplyReactiveArmor(src, target, ref damage, physicalPercent, firePercent,
                        coldPercent, poisonPercent, energyPercent);
                if ((type & DamageType.Reactive) == 0)
                {
                    int reflectPct = Math.Min(GetOnHitPropertyValue(target, null, "REFLECTPHYSICALDAM"), 250);
                    if (reflectPct != 0)
                        ApplyCharacterDamage(src, damage * reflectPct / 100, target,
                            DamageType.Fixed | DamageType.Reactive, physicalPercent, firePercent,
                            coldPercent, poisonPercent, energyPercent);
                }
                if (target.IsDeleted || target.IsDead)
                    return 0;
            }
        }

        if (damage <= 0)
            return 0;

        target.Hits -= (short)Math.Min(damage, short.MaxValue);
        if (feedback == DamageFeedback.Host)
            OnDirectCharacterDamageApplied?.Invoke(target, source, damage, type);
        return damage;
    }

    /// <summary>Pre-AOS armour (CCharFight.cpp:733-746): the creature's own ARMOR plus
    /// the coverage-weighted worn armour, rolled between half of and a 7-35% share of
    /// it, halved against magic.</summary>
    private static int ApplyPreAosArmor(Character target, int damage, DamageType type)
    {
        int armorRating = CalcArmorDefense(target) + target.CharDefArmor();
        int arMax = (int)Math.Min((long)armorRating * _rand.Next(7, 36) / 100, int.MaxValue);
        int arMin = arMax / 2;
        int defense = (int)_rand.NextInt64(arMin, (long)arMax + 1);
        if ((type & DamageType.Magic) != 0)
            defense /= 2;
        return Math.Max(0, damage - defense);
    }

    /// <summary>The item COMBAT_SLAYER reads (CCharFight.cpp:822-832): for magic the
    /// equipped spellbook, else - and failing that - the wielded weapon.</summary>
    private static Item? ResolveSlayerSource(Character src, DamageType type, HitDamageContext? swing)
    {
        var oneHand = src.GetEquippedItem(Layer.OneHanded);
        var twoHand = src.GetEquippedItem(Layer.TwoHanded);
        if ((type & DamageType.Magic) != 0)
        {
            if (oneHand?.ItemType == ItemType.Spellbook) return oneHand;
            if (twoHand?.ItemType == ItemType.Spellbook) return twoHand;
        }
        return swing != null ? swing.Weapon : oneHand ?? twoHand;
    }

    /// <summary>Is this the kind of blow the reflect family answers?
    ///
    /// The reference gates the whole family on "a physical blow of some sort"
    /// (CCharFight.cpp:946) and on there being a source other than the victim
    /// (:918). A spell, a burning field or a trap with nobody behind it therefore
    /// bounces off nothing - which is why a mage does not take his own fireball back
    /// from a target wearing Reactive Armour.</summary>
    public static bool IsReflectableBlow(Character? attacker, Character target, DamageType damageType) =>
        attacker != null && attacker != target && !attacker.IsDead && !attacker.IsDeleted &&
        (damageType & (DamageType.HitBlunt | DamageType.HitPierce | DamageType.HitSlash)) != 0;

    /// <summary>Reactive Armour: take the bounce out of the blow, send it back through
    /// the damage entry with the @HitReactive LOCAL.DamageType (FIXED|REACTIVE by
    /// default, so it cannot bounce again), and show it (CCharFight.cpp:950-1006).</summary>
    private static void ApplyReactiveArmor(Character attacker, Character target, ref int damage,
        int physicalPercent, int firePercent, int coldPercent, int poisonPercent, int energyPercent)
    {
        var reactive = PrepareReactiveArmor(attacker, target, ref damage);
        if (reactive == null)
            return;

        // Deliberately NOT gated on what is left of the blow: the bounce is the
        // reactive spell's own event, and a wearer who absorbed the whole blow still
        // sends it back.
        if (reactive.Reflect > 0)
            ApplyCharacterDamage(attacker, reactive.Reflect, target, reactive.DamageType,
                physicalPercent, firePercent, coldPercent, poisonPercent, energyPercent,
                spell: (int)SpellType.ReactiveArmor);
        if (reactive.Sound != 0 || reactive.EffectId != 0)
            OnReactiveArmorFeedback?.Invoke(target, attacker, reactive.Sound, reactive.EffectId);
    }

    /// <summary>Work out what Reactive Armour takes out of a blow and what it sends
    /// back, and let a script rewrite all of it.
    ///
    /// The reference (CCharFight.cpp:950-1006) asks four things before anything
    /// happens: the defender wears the flag, the blow is neither divine nor itself a
    /// bounce, the attacker is within two tiles, and a reactive memory is actually worn.
    /// The percentage comes from that memory, not from the engine. The blow is then
    /// REDUCED by the bounce and the attacker takes it.
    ///
    /// A zero percentage is a real answer, not a missing one: a definition with no
    /// EFFECT reflects nothing, and the trigger still runs so a script can supply the
    /// numbers itself.</summary>
    private static ReactiveArmorContext? PrepareReactiveArmor(
        Character attacker, Character target, ref int damage)
    {
        if (damage <= 0 || attacker == target) return null;
        if (!target.IsStatFlag(StatFlag.Reactive)) return null;
        if (attacker.IsDead || attacker.IsDeleted || target.IsDead) return null;
        if (target.Position.GetDistanceTo(attacker.Position) > 2) return null;

        int bounce = Math.Max(0, damage * target.ReactiveArmorPercent / 100);
        var ctx = new ReactiveArmorContext
        {
            Defender = target,
            Attacker = attacker,
            Damage = damage,
            Bounce = bounce,
            Reflect = bounce,
            Reduce = bounce,
            Sound = 0x01F1,
            EffectId = 0x374A,      // ITEMID_FX_CURSE_EFFECT
        };
        OnReactiveArmorTrigger?.Invoke(ctx);

        // Both halves fall back to the plain bounce when the script zeroed only the
        // specific figure, which is what the reference's `x ? x : base` reads as.
        if (ctx.Reduce > 0 || ctx.Bounce > 0)
            damage = Math.Max(0, damage - (ctx.Reduce > 0 ? ctx.Reduce : ctx.Bounce));
        if (ctx.Reflect <= 0 && ctx.Bounce > 0)
            ctx.Reflect = ctx.Bounce;
        return ctx;
    }

    /// <summary>The Source-X CObjBase DAMAGE verb (CObjBase.cpp:2230-2266) on a
    /// character or an item. A character goes through the shared damage entry
    /// (<see cref="ApplyCharacterDamage"/>); an item through CItem::OnTakeDamage.
    /// <paramref name="damageType"/> is the script's number, Source-X DAMAGE_*.</summary>
    public static int ApplyScriptDamage(
        ObjBase target,
        int rawDamage,
        DamageType damageType,
        Character? source = null,
        int physicalPercent = 0,
        int firePercent = 0,
        int coldPercent = 0,
        int poisonPercent = 0,
        int energyPercent = 0)
    {
        int damage = Math.Clamp(rawDamage, 0, short.MaxValue);

        // An item's DAMAGE is CItem::OnTakeDamage with SRC and the type (CObjBase.cpp:2259-2264).
        if (target is Item item)
            return damage > 0 ? ItemDamageEngine.OnTakeDamage(item, damage, source, damageType) : 0;
        if (target is not Character character)
            return 0;
        return ApplyCharacterDamage(character, damage, source, damageType,
            physicalPercent, firePercent, coldPercent, poisonPercent, energyPercent);
    }

    /// <summary>A sourceless DAMAGE_GOD blow to an item (the gathering tool's wear,
    /// CCharSkill.cpp:3960): CItem::OnTakeDamage through <see cref="ItemDamageEngine"/>.</summary>
    internal static int ApplyDirectItemDamage(Item item, int damage) =>
        ItemDamageEngine.OnTakeDamage(item, damage, null, DamageType.God);

    /// <summary>COMBAT_ELEMENTAL_ENGINE resist (CCharFight.cpp:717-730): each share
    /// of the blow is cut by the matching effective resist. The caller fills an unset
    /// physical share with what the elemental shares leave of 100, as the reference
    /// does; the shares are not otherwise normalised.</summary>
    public static int ApplyDamageSplitResist(Character target, int damage,
        int physicalPercent, int firePercent, int coldPercent,
        int poisonPercent, int energyPercent)
    {
        long total = (long)damage * physicalPercent * (100 - EffResPhysical(target));
        total += (long)damage * firePercent * (100 - EffResFire(target));
        total += (long)damage * coldPercent * (100 - EffResCold(target));
        total += (long)damage * poisonPercent * (100 - EffResPoison(target));
        total += (long)damage * energyPercent * (100 - EffResEnergy(target));
        return (int)Math.Clamp(total / 10_000, 0L, short.MaxValue);
    }

    /// <summary>
    /// Perform a full attack resolution. Returns damage dealt, 0 for a
    /// connecting but fully absorbed/cancelled hit, or an Attack* sentinel.
    /// Maps to CChar::Fight_Hit flow in Source-X.
    /// </summary>
    /// <summary>Attacker's Damage Increase % (Source-X INCREASEDAM), from the
    /// INCREASEDAM tag. 0 when absent or unparseable.</summary>
    private static int GetDamageIncrease(Character ch) =>
        ch.TryGetTag("INCREASEDAM", out string? s) && ScriptNumber.TryParseInt(s, out int v) ? v : 0;

    /// <summary>Source-X Fight_CalcDamage additive Damage Increase modifiers.
    /// The configured INCREASEDAM value is capped first; racial/form bonuses
    /// are then added and may take the effective total above 100%.</summary>
    public static int CalculateDamageIncrease(Character attacker)
    {
        int increase = Math.Clamp(GetDamageIncrease(attacker), -100, 100);
        if ((((RacialFlags)Character.RacialFlags) & RacialFlags.GargoyleBerserk) != 0 &&
            attacker.IsGargoyle)
        {
            int lostHits = Math.Max(0, attacker.MaxHits - attacker.Hits);
            increase += Math.Min(15 * (lostHits / 20), 60);
        }

        if (HasHorrificBeastCombatForm(attacker))
            increase += 25;
        return increase;
    }

    private static bool HasHorrificBeastCombatForm(Character attacker) =>
        (Character.FeatureAOS & 0x02) != 0 && attacker.HorrificBeastActive;

    /// <summary>Source-X Calc_CombatChanceToParry, including the Samurai
    /// Empire Bushido formulas and COMBATPARRYINGERA equipment gates.</summary>
    /// <summary>Roll one parry attempt: train on the try (Source-X rolls through
    /// Skill_UseQuick, which trains on the attempt with the chance as difficulty),
    /// and on a success wear the parrying item — the reference damages it for 1
    /// (CCharFight.cpp:2133), which this engine never did, so a shield parried
    /// forever for free.</summary>
    private static bool RollParry(Character defender, Character attacker, int chance,
        SkillType parrySkill, Item? parryItem, int itemDamageChance)
    {
        if (defender.IsPlayer)
            Skills.SkillEngine.GainExperience(defender, parrySkill, chance);

        if (_rand.Next(100) >= chance)
            return false;

        bool seWeaponParry = (Character.FeatureSE & 0x02) != 0 &&
            ((ParryEraFlags)Character.CombatParryingEra).HasFlag(ParryEraFlags.SeFormula) &&
            parryItem?.ItemType != ItemType.Shield;
        if (defender.IsPlayer && seWeaponParry)
            Skills.SkillEngine.GainExperience(defender, SkillType.Bushido, chance);

        // The reference damages the parrying item for 1 outright
        // (pItemHit->OnTakeDamage, CCharFight.cpp:2133) - the only roll in front of
        // it is LOCAL.ItemParryDamageChance, so the global durability chance must
        // not be rolled a second time here.
        if (parryItem != null && DurabilityEnabled && itemDamageChance > _rand.Next(100))
            ApplyDurabilityLoss(parryItem, rollConfiguredChance: false,
                source: attacker, triggerDamage: 1, damageType: GetWeaponDamageType(
                    attacker.GetEquippedItem(Layer.OneHanded) ?? attacker.GetEquippedItem(Layer.TwoHanded)));

        OnParrySucceeded?.Invoke(defender);
        return true;
    }

    public static int CalculateParryChance(Character defender, out Item? parryItem)
    {
        parryItem = null;
        var era = (ParryEraFlags)Character.CombatParryingEra;
        bool canShield = era.HasFlag(ParryEraFlags.ShieldBlock);
        bool canOneHand = era.HasFlag(ParryEraFlags.OneHandBlock);
        bool canTwoHand = era.HasFlag(ParryEraFlags.TwoHandBlock);
        bool seFormula = (Character.FeatureSE & 0x02) != 0 &&
            era.HasFlag(ParryEraFlags.SeFormula);

        int parrying = defender.GetSkill(SkillType.Parrying);
        int chance = -1;
        var twoHand = defender.GetEquippedItem(Layer.TwoHanded);
        bool hasShield = CombatHelper.HasShieldEquipped(defender);
        var oneHand = defender.GetEquippedItem(Layer.OneHanded);
        Item? weapon = oneHand ?? (hasShield ? null : twoHand);

        if (seFormula)
        {
            int bushido = defender.GetSkill(SkillType.Bushido);
            if (canShield && hasShield)
            {
                parryItem = twoHand;
                chance = (parrying - bushido) / 40;
                if (parrying >= 1000 || bushido >= 1000)
                    chance += 5;
                chance = Math.Max(0, chance);
            }
            else if (weapon != null)
            {
                int seChance;
                int legacyChance = parrying / 80;
                if (parrying >= 1000)
                    legacyChance += 5;

                if (weapon == oneHand && canOneHand)
                {
                    parryItem = weapon;
                    seChance = parrying * bushido / 48_000;
                }
                else if (weapon == twoHand && canTwoHand)
                {
                    parryItem = weapon;
                    seChance = parrying * bushido / 41_140;
                }
                else
                {
                    seChance = -1;
                }

                if (seChance >= 0)
                {
                    if (parrying >= 1000 || bushido >= 1000)
                        seChance += 5;
                    chance = Math.Max(seChance, legacyChance);
                }
            }
        }
        else
        {
            if (canShield && hasShield)
            {
                parryItem = twoHand;
                chance = parrying / 40;
            }
            else if (weapon != null &&
                     ((weapon == oneHand && canOneHand) || (weapon == twoHand && canTwoHand)))
            {
                parryItem = weapon;
                chance = parrying / 80;
            }

            if (chance > 0 && parrying >= 1000)
                chance += 5;
        }

        if (chance < 0)
            return 0;
        int dex = defender.Dex;
        if (dex < 80)
            chance = (int)(chance * (1.0f - ((80 - dex) / 100.0f)));
        return Math.Max(0, chance);
    }

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
        out bool ammoHandled,
        DamageType? swingDamageType = null)
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
        // Source-X Skill_Fighting's stroke (CCharSkill.cpp:3048): m_Act_Difficulty =
        // Calc_CombatChanceToHit, then Skill_CheckSuccess(skill, difficulty, false) -
        // a FLAT percent check, difficulty*10 >= rand(1000), with a GM always
        // landing. In era 0 the difficulty is itself a draw, rand(iDiff) (0..iDiff-1).
        // The bell curve used here instead let a skilled attacker land almost every
        // swing, where the reference tops out near iDiff/2 percent.
        int hitCap = CalcHitChanceCore(attacker, target, hitEra, GetWeaponSkill(attacker, weapon));
        int hitChance = hitEra == 0 ? _rand.Next(hitCap) : hitCap; // m_Act_Difficulty, also fed to the gain rolls
        bool hitLanded = attacker.PrivLevel >= PrivLevel.GM || hitChance * 10 >= _rand.Next(1000);
        if (!hitLanded)
            return AttackMiss;

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
            int di = CalculateDamageIncrease(attacker);
            if (di != 0)
                damage += damage * di / 100;
        }

        // The swing's damage type: @HitCheck's ARGN2 when the caller carried it,
        // else the weapon's own (OVERRIDE.DAMAGETYPE). It runs through the parry
        // and @Hit stages below (CCharFight.cpp:1759-1776, 2083, 2116).
        var swingType = swingDamageType ?? GetWeaponDamageType(weapon);

        // Parry check — Source-X Calc_CombatChanceToParry, selected by the
        // COMBATPARRYINGERA mask (legacy or Samurai Empire/Bushido formula).
        // Upstream skips the whole block - roll and @HitParry - for a DAMAGE_GOD
        // blow (CCharFight.cpp:2083).
        if ((swingType & DamageType.God) == 0)
        {
            int parryChance = CalculateParryChance(target, out Item? parryItem);
            var parrySkill = SkillType.Parrying;

            // Default reduction is a full block, unless the Parrying skill's own
            // EFFECT curve says otherwise (CCharFight.cpp:2091) — the pack decides
            // how much a parry takes off, not the engine.
            int reductionPercent = 100;
            var parrySkillDef = Definitions.DefinitionLoader.GetSkillDef((int)parrySkill);
            if (parrySkillDef is { Effect.IsEmpty: false })
                reductionPercent = parrySkillDef.Effect.GetLinear(target.GetSkill(parrySkill));

            // The trigger fires whether or not the engine would have rolled: its
            // LOCAL.ParryChance is writable, so a script can parry where the engine
            // would not (and refuse where it would). Firing it only after a SUCCESSFUL
            // roll — as this used to — puts both of those out of reach.
            if (OnHitParry != null)
            {
                var parryCtx = new HitParryContext
                {
                    ReductionPercent = reductionPercent,
                    DamageType = (int)swingType,
                    ParryChance = parryChance,
                    ParrySkillId = (int)parrySkill,
                    ItemParryDamageChance = 100,
                    Damage = damage,
                    ParryItem = parryItem,
                };
                if (!OnHitParry(target, attacker, parryCtx))
                    return AttackParried;   // RETURN 1: the blow is dropped whole

                reductionPercent = parryCtx.ReductionPercent;
                parryChance = parryCtx.ParryChance;
                damage = parryCtx.Damage;
                swingType = (DamageType)(uint)parryCtx.DamageType;   // ARGN2 (:2116)
                // SkillType is backed by a short, so the id has to be narrowed before
                // it can be asked about - Enum.IsDefined throws on a mismatched width.
                if (parryCtx.ParrySkillId is >= short.MinValue and <= short.MaxValue &&
                    Enum.IsDefined(typeof(SkillType), (short)parryCtx.ParrySkillId))
                    parrySkill = (SkillType)parryCtx.ParrySkillId;

                if (parryChance > 0 && RollParry(target, attacker, parryChance, parrySkill,
                        parryItem, parryCtx.ItemParryDamageChance))
                {
                    if (reductionPercent >= 100)
                        return AttackParried;
                    if (reductionPercent > 0)
                        damage -= damage * reductionPercent / 100;
                }
            }
            else if (parryChance > 0 && RollParry(target, attacker, parryChance, parrySkill,
                         parryItem, itemDamageChance: 100))
            {
                if (reductionPercent >= 100)
                    return AttackParried;
                if (reductionPercent > 0)
                    damage -= damage * reductionPercent / 100;
            }
        }

        damage = Math.Max(0, damage);

        // The attacker side of the blow (Source-X Fight_Hit, CCharFight.cpp:2143-2195):
        // the attacker's @Hit, then the weapon's @Hit, on the RAW damage - before the
        // victim's armour, which belongs to the victim's own damage entry below. Both
        // may rewrite the damage (ARGN1) and its type (ARGN2); RETURN 1 drops the blow.
        // The victim's @GetHit armor-damage roll is seeded here and handed on.
        int weaponPoison = weapon != null ? GetWeaponPoisonSkill(weapon) : 0;
        int poisonDose = weaponPoison > 0 ? _rand.Next(weaponPoison) : 0;
        var hitCtx = new HitDamageContext
        {
            Attacker = attacker,
            Target = target,
            Weapon = weapon,
            Damage = damage,
            DamageType = swingType,
            PoisonDose = poisonDose,
            PoisonReductionAmount = weapon != null ? poisonDose / 2 : 1,
            ItemDamageLayer = ArmorDamageLayers[_rand.Next(ArmorDamageLayers.Length)],
            ItemDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            WeaponDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            AmmoUid = ammoUid,
        };
        if (OnHitDamage != null)
            damage = Math.Clamp(OnHitDamage(hitCtx), 0, short.MaxValue);
        ammoHandled = hitCtx.ArrowHandled || hitCtx.Cancelled;

        // RETURN 1 in the @Hit chain drops the blow before poison, wear, damage,
        // procs and skill gain (Source-X returns WAR_SWING_EQUIPPING); it also leaves
        // ranged ammo alone.
        if (hitCtx.Cancelled)
            return 0;
        var damageType = hitCtx.DamageType;

        // Poisoned weapon / venomous creature (Source-X CCharFight.cpp:2224-2254). The
        // weapon's poison is its MOREZ (m_itWeapon.m_poison_skill, 0-100); a hit
        // delivers this swing's dose when the poison beats a d100 - or always, for a
        // very weak coat - and may wear the coat down by LOCAL.ItemPoisonReductionAmount.
        // A creature with no weapon bites with its Poisoning skill half the time.
        if (weapon != null)
        {
            int poisonSkill = GetWeaponPoisonSkill(weapon);
            if (!flags.HasFlag(CombatFlags.NoPoisonHit) && poisonSkill > 0 &&
                (poisonSkill > _rand.Next(100) || poisonSkill < 10))
            {
                int deliver = (byte)hitCtx.PoisonDose;
                target.SetPoison(10 * deliver, deliver / 5, attacker);

                if (hitCtx.PoisonReductionChance > _rand.Next(100))
                    SetWeaponPoisonSkill(weapon, poisonSkill - hitCtx.PoisonReductionAmount);
            }
        }
        else if (!attacker.IsPlayer)
        {
            if (!flags.HasFlag(CombatFlags.NoPoisonHit) && 50 >= _rand.Next(100))
            {
                int poisoning = attacker.GetSkill(SkillType.Poisoning);
                if (poisoning > 0)
                    target.SetPoison(_rand.Next(poisoning), _rand.Next(Math.Max(1, poisoning / 50)), attacker);
            }
        }

        // Source-X @Hit LOCAL.ItemDamageChance: the weapon takes damage only
        // WeaponDamageChance% of the time (script-writable, seeded 25), via
        // pWeapon->OnTakeDamage(iDmg, pCharTarg) (CCharFight.cpp:2240-2243) — BEFORE
        // the victim's own OnTakeDamage and whatever the victim's immunity does to
        // the blow. Its @Damage runs with SRC = the struck character and ARGN1 = the
        // blow, which is what a weapon's "ON=@DAMAGE SRC.EFFECT ..." relies on to
        // draw on the victim (the pack's exploding-bomb bow).
        if (DurabilityEnabled && weapon != null && damage > 0 &&
            _rand.Next(100) < Math.Clamp(hitCtx.WeaponDamageChance, 0, 100))
            ApplyDurabilityLoss(weapon, rollConfiguredChance: false,
                source: target, triggerDamage: damage, damageType: GetWeaponDamageType(weapon));

        // The victim's damage entry (CCharFight.cpp:2259): protection, OnAttackedBy,
        // armour, @GetHit on the reduced blow, armour wear, slayer, the reflect family
        // and the hit points - the same entry a script DAMAGE and a spell use. The
        // elemental split is the attacker's DAM* shares. Do NOT call Kill() here; the
        // caller handles death via DeathEngine.ProcessDeath.
        int dealt = ApplyCharacterDamage(target, damage, attacker, damageType,
            attacker.DamPhysical, attacker.DamFire, attacker.DamCold,
            attacker.DamPoison, attacker.DamEnergy,
            feedback: DamageFeedback.Caller, combatFlags: flags, swing: hitCtx);

        // AOS on-hit properties: leeches, mana drain, hit-area splashes and on-hit
        // spell procs.
        //
        // Source-X Fight_Hit applies the weapon's own damage FIRST
        // (OnTakeDamage, CCharFight.cpp:2259) and only then runs the procs
        // (:2270-2361), on its own iDmg - the post-@Hit blow, which OnTakeDamage
        // took by value, so neither the victim's armour nor its @GetHit changes it and
        // the procs fire even when the victim bounced the blow.
        if (damage > 0)
            ApplyAosOnHitEffects(attacker, target, damage, weapon, flags);

        // A proc can synchronously kill either party through the normal spell/death
        // engine. The strike itself is already applied and credited by this point;
        // stop here so the tail below does not add poison, durability or death
        // feedback on top of a mobile that is already dead.
        if (attacker.IsDeleted || attacker.IsDead || target.IsDeleted || target.IsDead)
            return AttackResolvedByProc;

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

        return dealt;
    }

    private static void ApplyDurabilityLoss(Item item, bool rollConfiguredChance = true,
        Character? source = null, int triggerDamage = 0, DamageType damageType = 0)
    {
        int chance = Math.Clamp(DurabilityLossChance, 0, 100);
        if (rollConfiguredChance && _rand.Next(100) >= chance)
            return;

        // Source-X CItem::OnTakeDamage fires @Damage before it looks at the item's
        // hit points at all (CItem.cpp:5826-5832), so a def without HITPOINTS still
        // runs its @Damage script; only the wear below needs them. ARGN1 is the
        // incoming damage (the blow, for combat) and SRC the character dealing it.
        if (source != null)
        {
            if (item.IsDeleted ||
                OnItemDamaged?.Invoke(item, triggerDamage > 0 ? triggerDamage : 1, source, damageType) == true ||
                item.IsDeleted)
                return;
        }

        // Source-X: only items whose def (or crafting) gave them HITPOINTS
        // wear out. Classic packs define none on most gear — inventing a
        // synthetic durability here made every weapon and armor piece break
        // and vanish after a few hundred swings.
        int maxHits = item.GetHitsMax();
        if (maxHits <= 0)
            return;

        int curHits = item.GetHitsCur();
        int minLoss = Math.Max(0, Math.Min(DurabilityLossMin, DurabilityLossMax));
        int maxLoss = Math.Max(minLoss, Math.Max(DurabilityLossMin, DurabilityLossMax));
        int loss = minLoss == maxLoss
            ? minLoss
            : (int)_rand.NextInt64(minLoss, (long)maxLoss + 1);

        if (loss <= 0)
            return;

        if (source == null && OnItemDamaged?.Invoke(item, loss, null, 0) == true)
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
    internal static bool IsRangedSkill(SkillType skill) =>
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

        var weaponDef = CombatHelper.GetWeaponDef(weapon);
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
            var def = CombatHelper.GetWeaponDef(weapon);
            overrideRaw = def?.TagDefs.Get("OVERRIDE.DAMAGETYPE")
                ?? def?.TagDefs.Get("OVERRIDE_DAMAGETYPE");
        }
        if (!string.IsNullOrWhiteSpace(overrideRaw))
        {
            uint numeric = Objects.ObjBase.ParseHexOrDecUInt(overrideRaw);
            return (DamageType)numeric;
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

    /// <summary>The poison coat on a weapon or a meal, 0-100 (Source-X m_poison_skill,
    /// the item's MOREZ). A pre-MOREZ save kept it in the POISON_SKILL tag as the
    /// potion quality, ten times the coat.</summary>
    public static int GetWeaponPoisonSkill(Item item)
    {
        if (item.MoreP.Z > 0)
            return item.MoreP.Z;
        return item.TryGetTag("POISON_SKILL", out string? legacy) && ScriptNumber.TryParseInt(legacy, out int q) && q > 0
            ? Math.Clamp(q / 10, 0, 100)
            : 0;
    }

    /// <summary>Write the poison coat back into MOREZ (and retire the legacy tag).</summary>
    public static void SetWeaponPoisonSkill(Item item, int value)
    {
        int coat = Math.Clamp(value, 0, 100);
        item.MoreP = new SphereNet.Core.Types.Point3D(item.MoreP.X, item.MoreP.Y, (sbyte)coat, item.MoreP.Map);
        item.RemoveTag("POISON_SKILL");
        item.RemoveTag("POISON_CHARGES");
    }

    /// <summary>
    /// Apply elemental resist to damage. Maps to COMBAT_ELEMENTAL_ENGINE in Source-X.
    /// Splits damage by attacker's elemental percentages then applies per-element resist.
    /// </summary>
    public static int ApplyElementalDamageSplit(Character attacker, Character target, int damage, Item? weapon)
    {
        var (physPct, firePct, coldPct, poisonPct, energyPct) = GetElementalSplit(attacker);
        long total = (long)damage * physPct * (100 - EffResPhysical(target));
        total += (long)damage * firePct * (100 - EffResFire(target));
        total += (long)damage * coldPct * (100 - EffResCold(target));
        total += (long)damage * poisonPct * (100 - EffResPoison(target));
        total += (long)damage * energyPct * (100 - EffResEnergy(target));
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
        // Source-X CCharFight.cpp:2272-2275: a Curse Weapon effect adds its level
        // to the life-leech percent, but only with a weapon equipped.
        if (weapon != null && attacker.CurseWeaponLevel > 0)
            leechLife += attacker.CurseWeaponLevel;
        // Necromancy Vampiric Embrace (reference SPELL_Vampiric_Embrace): the form
        // leeches life on any damaging hit, armed or not.
        if (attacker.VampiricEmbraceActive)
            leechLife += 20;
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
        // Source-X CCharFight.cpp:2299-2304: Wraith Form drains the target's mana
        // to the attacker, scaled by SpiritSpeak, on any damaging hit.
        if (attacker.WraithFormActive)
            manaDrain += 5 + 15 * attacker.GetSkill(SkillType.SpiritSpeak) / 1000;
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
        if (attacker.TryGetTag(prop, out var raw) && ScriptNumber.TryParseInt(raw, out int own))
            total += own;
        if (weapon != null)
            total += GetItemNumProperty(weapon, prop);
        var talisman = attacker.GetEquippedItem(Layer.Talisman);
        if (talisman != null && talisman != weapon)
            total += GetItemNumProperty(talisman, prop);
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    /// <summary>Source-X CCPropsChar equipment aggregate. LayerAdd/LayerRemove
    /// maintains this eagerly upstream; SphereNet derives it on demand from
    /// the character plus every equipped item to avoid stale totals.</summary>
    public static int GetEquipmentPropertyValue(Character character, string property)
    {
        long total = character.Tags.GetInt(property);
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var equipped = character.GetEquippedItem((Layer)layerIndex);
            if (equipped != null)
                total += GetItemNumProperty(equipped, property);
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static int GetItemNumProperty(Item item, string prop)
    {
        if (item.TryGetTag(prop, out var raw) && ScriptNumber.TryParseInt(raw, out int v))
            return v;
        var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
        if (def != null && ScriptNumber.TryParseInt(def.TagDefs.Get(prop), out int dv))
            return dv;
        return 0;
    }

    /// <summary>Sum a resist property across every equipped item — the AOS suit
    /// contribution (reference CCPropsChar equip aggregate). Excludes the
    /// character's own base field so it can be added to it without double count.</summary>
    private static int SumEquippedItemProperty(Character ch, string prop)
    {
        long total = 0;
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var equipped = ch.GetEquippedItem((Layer)layerIndex);
            if (equipped != null)
                total += GetItemNumProperty(equipped, prop);
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    /// <summary>Effective elemental resist: base resist plus the suit contribution
    /// from equipped items, derived on read (no equip-time mutation, no persistence
    /// risk). Clamped to a valid 0-100 percent.</summary>
    public static int EffectiveResist(Character ch, DamageType type)
    {
        (string prop, int baseVal) = type switch
        {
            DamageType.Fire => ("RESFIRE", (int)ch.ResFire),
            DamageType.Cold => ("RESCOLD", (int)ch.ResCold),
            DamageType.Poison => ("RESPOISON", (int)ch.ResPoison),
            DamageType.Energy => ("RESENERGY", (int)ch.ResEnergy),
            _ => ("RESPHYSICAL", (int)ch.ResPhysical),
        };
        return Math.Clamp(baseVal + SumEquippedItemProperty(ch, prop), 0, 100);
    }

    public static int EffResPhysical(Character ch) => EffectiveResist(ch, DamageType.Physical);
    public static int EffResFire(Character ch) => EffectiveResist(ch, DamageType.Fire);
    public static int EffResCold(Character ch) => EffectiveResist(ch, DamageType.Cold);
    public static int EffResPoison(Character ch) => EffectiveResist(ch, DamageType.Poison);
    public static int EffResEnergy(Character ch) => EffectiveResist(ch, DamageType.Energy);

    /// <summary>Effective STR/DEX/INT: the base stat, the script-set modifier, and the
    /// suit contribution from equipped items (BONUSSTR/BONUSDEX/BONUSINT).
    ///
    /// This is upstream's Stat_GetAdjusted, base + mod (CCharStat.cpp:143), where the
    /// modifier holds BOTH what a script set through MODSTR and what equipment added at
    /// equip time (CCharAct.cpp:3382). SphereNet derives the equipment share on read
    /// instead of at equip time, and simply never read MODSTR at all - it was stored,
    /// saved by nobody, and consulted by nothing, so a script raising a character's
    /// strength changed a number no part of the game looked at.
    ///
    /// Used by the correctness-facing reads (display, melee damage, carry weight, REQSTR
    /// gate, skill contribution); the stored max pools stay derived from the base stat
    /// so no feedback loop forms.</summary>
    public static int EffectiveStr(Character ch) =>
        Math.Max(0, ch.Str + ch.ModStr + SumEquippedItemProperty(ch, "BONUSSTR"));
    public static int EffectiveDex(Character ch) =>
        Math.Max(0, ch.Dex + ch.ModDex + SumEquippedItemProperty(ch, "BONUSDEX"));
    public static int EffectiveInt(Character ch) =>
        Math.Max(0, ch.Int + ch.ModInt + SumEquippedItemProperty(ch, "BONUSINT"));

    /// <summary>Effective max hit/mana/stamina pool: the stored base pool plus the
    /// suit contribution (Source-X BONUSHITSMAX/BONUSMANAMAX/BONUSSTAMMAX), derived
    /// on read so the base field persists clean. Floored at 0 to preserve the
    /// old-save "MaxHits &lt;= 0 -> backfill from stat" login path. Reads the raw base
    /// field (BaseMaxHits), never the effective property, so there is no recursion.</summary>
    // Three terms, kept apart on purpose: the base the definition and the save
    // carry, the script-owned MODMAX* modifier, and the suit. Upstream adds the
    // first two in Stat_GetMaxAdjusted (CCharStat.cpp:301) and accumulates the third
    // separately; folding any pair together would let it persist as base and
    // compound, which is the bug review 13J found in the suit term.
    public static int EffectiveMaxHits(Character ch) =>
        Math.Max(0, ch.BaseMaxHits + ch.ModMaxHits + SumEquippedItemProperty(ch, "BONUSHITSMAX"));
    public static int EffectiveMaxMana(Character ch) =>
        Math.Max(0, ch.BaseMaxMana + ch.ModMaxMana + SumEquippedItemProperty(ch, "BONUSMANAMAX"));
    public static int EffectiveMaxStam(Character ch) =>
        Math.Max(0, ch.BaseMaxStam + ch.ModMaxStam + SumEquippedItemProperty(ch, "BONUSSTAMMAX"));

    /// <summary>Effective luck: the base Luck plus the suit contribution (Source-X
    /// PROPCH_LUCK equip-time accumulation). Derived on read like the stat slice.
    /// Source-X aggregates and DISPLAYS luck but consumes it nowhere — no loot,
    /// damage, or spawn hook reads it — so this feeds the status display only;
    /// wiring it into drop quality would diverge from the reference. Unclamped:
    /// UNLUCKY items push it below zero, matching Source-X.</summary>
    public static int EffectiveLuck(Character ch) =>
        ch.Luck + SumEquippedItemProperty(ch, "LUCK");

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

        // Physical damage types (blunt/slash/pierce). Resists are the effective
        // suit totals (base + equipped-item bonuses), derived on read.
        if (dmgType.HasFlag(DamageType.Physical) || dmgType.HasFlag(DamageType.HitBlunt) ||
            dmgType.HasFlag(DamageType.HitSlash) || dmgType.HasFlag(DamageType.HitPierce))
            resist = Math.Max(resist, EffResPhysical(target));

        if (dmgType.HasFlag(DamageType.Fire))
            resist = Math.Max(resist, EffResFire(target));

        if (dmgType.HasFlag(DamageType.Cold))
            resist = Math.Max(resist, EffResCold(target));

        if (dmgType.HasFlag(DamageType.Poison))
            resist = Math.Max(resist, EffResPoison(target));

        if (dmgType.HasFlag(DamageType.Energy) || dmgType.HasFlag(DamageType.Magic))
            resist = Math.Max(resist, EffResEnergy(target));

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

        // Which DEX-ish number the formula takes is era-specific in Source-X
        // (CResourceCalc.cpp): era 0 uses Stat_GetAdjusted(STAT_DEX) — the effective
        // stat, equipment and buffs included (:61, :69) — while eras 1-4 use
        // Stat_GetVal(STAT_DEX), which is the CURRENT STAMINA POOL (:90, :101, :116,
        // :127; CChar.cpp:4271 writes the STAM save field from it).
        //
        // Reading the base field for every era meant stamina loss never slowed an
        // attack in the eras that price it that way, and a DEX bonus from equipment
        // never sped one up in era 0.
        int dex = Character.CombatSpeedEra == 0
            ? Math.Max(0, EffectiveDex(attacker))
            : Math.Max(0, (int)attacker.Stam);
        long speedScale = Math.Max(1, Character.CombatSpeedScaleFactor);
        int swingSpeedIncrease = Math.Clamp(GetEquipmentPropertyValue(
            attacker, CombatSpeedProperties.IncreaseSwingSpeed), -99, 10_000);
        long deciseconds;
        switch (Character.CombatSpeedEra)
        {
            case 2: // AOS (Source-X era 2)
            {
                long swingSpeed = (long)(dex + 100) * baseSpeed;
                swingSpeed = Math.Max(1, swingSpeed * (100 + swingSpeedIncrease) / 100);
                deciseconds = ((speedScale * 10) / swingSpeed) / 2;
                if (deciseconds < 12) deciseconds = 12;
                break;
            }
            case 3: // Samurai Empire (Source-X era 3)
            {
                long swingSpeed = Math.Max(1,
                    (long)baseSpeed * (100 + swingSpeedIncrease) / 100);
                long ticks = speedScale / Math.Max(1, (dex + 100L) * swingSpeed) - 2;
                if (ticks < 5) ticks = 5;
                deciseconds = ticks * 10 / 4;
                break;
            }
            case 4: // Mondain's Legacy (Source-X era 4)
            {
                long speedFactor = 100 / Math.Max(1, 100 + swingSpeedIncrease);
                long ticks = ((long)baseSpeed * 4 - (dex / 30)) * speedFactor;
                if (ticks < 5) ticks = 5;
                deciseconds = ticks * 10 / 4;
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
