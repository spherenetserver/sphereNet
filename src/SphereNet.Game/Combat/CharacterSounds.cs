using SphereNet.Core.Enums;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Combat;

/// <summary>
/// The sounds a character makes, chosen the way Source-X CChar::SoundChar does
/// (CCharAct.cpp:2612-2808), plus the weapon hit/miss props it consults
/// (CItem::Weapon_GetSoundHit / Weapon_GetSoundMiss, CItem.cpp:4979-5011) and the
/// miss whoosh Fight_Hit falls back to (CCharFight.cpp:2057-2072).
///
/// A CHARDEF names one base sound with SOUND= and may override single actions
/// with SOUNDIDLE / SOUNDNOTICE / SOUNDHIT / SOUNDGETHIT / SOUNDDIE (-1 = silent).
/// Everything not overridden is derived from the base by the client's sound-bank
/// layout, so a creature defined by SOUND= alone still roars, strikes, flinches
/// and dies audibly.
/// </summary>
public static class CharacterSounds
{
    /// <summary>SOUND_SPECIAL_HUMAN (uofiles_enums.h:137): the hardcoded human sets.</summary>
    public const ushort SpecialHuman = 0x900;

    /// <summary>An override of -1 (0xFFFF as a sound id) silences that action.</summary>
    public const ushort Silent = 0xFFFF;

    /// <summary>GENERICSOUNDS (Source-X m_fGenericSounds): 0 turns every
    /// SoundChar off (CCharAct.cpp:2615).</summary>
    public static bool GenericSoundsEnabled { get; set; } = true;

    private static readonly ushort[] s_humanHit = { 0x135, 0x137, 0x13B };
    private static readonly ushort[] s_manDie = { 0x15A, 0x15B, 0x15C, 0x15D };
    private static readonly ushort[] s_manOmf = { 0x154, 0x155, 0x156, 0x157, 0x158, 0x159 };
    private static readonly ushort[] s_womanDie = { 0x150, 0x151, 0x152, 0x153 };
    private static readonly ushort[] s_womanOmf = { 0x14B, 0x14C, 0x14D, 0x14E, 0x14F };
    private static readonly ushort[] s_missRanged = { 0x233, 0x238 };
    private static readonly ushort[] s_missMelee = { 0x238, 0x239, 0x23A };

    /// <summary>Source-X CChar::SoundChar(type): the id to play, or 0 for none.
    /// <paramref name="weapon"/> is the attacker's weapon and only matters for
    /// <see cref="CreatureSoundType.Hit"/> - an armed strike sounds like the weapon,
    /// and only a weapon type with no strike noise falls back to the body.</summary>
    public static ushort Resolve(Character ch, CreatureSoundType type, Item? weapon = null,
        Random? rng = null)
    {
        if (!GenericSoundsEnabled)
            return 0;
        rng ??= Random.Shared;

        if (type == CreatureSoundType.Hit && weapon != null)
        {
            ushort weaponSound = GetWeaponHitSound(weapon, rng);
            if (weaponSound != 0)
                return weaponSound;
        }

        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        if (def == null)
        {
            // No definition at all (a bare body or a test fixture): a human body
            // still sounds human, anything else stays silent - exactly what a
            // chardef without SOUND= gets upstream.
            return BodyAnimTranslator.IsHumanoidBody(ch.BodyId)
                ? HumanSound(type, ch.IsFemale, rng)
                : (ushort)0;
        }

        ushort over = type switch
        {
            CreatureSoundType.Idle => def.SoundIdle,
            CreatureSoundType.Notice => def.SoundNotice,
            CreatureSoundType.Hit => def.SoundHit,
            CreatureSoundType.GetHit => def.SoundGetHit,
            CreatureSoundType.Die => def.SoundDie,
            _ => 0,
        };
        if (over == Silent)
            return 0;
        if (over != 0)
            return over;

        return FromBase(def.SoundBase, type, ch.IsFemale, rng);
    }

    /// <summary>The SOUND= base plus the per-action offset (CCharAct.cpp:2725-2803).
    /// The client's creature sounds come in blocks of five - idle, notice, hit,
    /// get-hit, die - up to the crane (0x4D6); from there to the abyssal infernal
    /// (0x5D4) the block is die, hit, idle, notice, get-hit; after it creatures
    /// have four sounds.</summary>
    public static ushort FromBase(ushort soundBase, CreatureSoundType type, bool female,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        if (soundBase == 0)
            return 0;
        if (soundBase == SpecialHuman)
            return HumanSound(type, female, rng);

        int id = soundBase;
        if (id < 0x4D6)
            id += (int)type;
        else if (id < 0x5D4)
            id += type switch
            {
                CreatureSoundType.Idle => 2,
                CreatureSoundType.Notice => 3,
                CreatureSoundType.Hit => 1,
                CreatureSoundType.GetHit => 4,
                _ => 0, // Die
            };
        else
            id += type switch
            {
                CreatureSoundType.Idle => 3,
                CreatureSoundType.Notice => 3,
                CreatureSoundType.GetHit => 2,
                CreatureSoundType.Die => 1,
                _ => 0, // Hit
            };
        return (ushort)Math.Clamp(id, 0, ushort.MaxValue);
    }

    /// <summary>SOUND_SPECIAL_HUMAN (CCharAct.cpp:2735-2771): the strike is the same
    /// slap set for everyone, the pain and death cries are gendered, and a human
    /// has no idle or notice sound.</summary>
    private static ushort HumanSound(CreatureSoundType type, bool female, Random rng)
    {
        ushort[]? set = type switch
        {
            CreatureSoundType.Hit => s_humanHit,
            CreatureSoundType.GetHit => female ? s_womanOmf : s_manOmf,
            CreatureSoundType.Die => female ? s_womanDie : s_manDie,
            _ => null,
        };
        return set == null ? (ushort)0 : set[rng.Next(set.Length)];
    }

    /// <summary>The strike noise of an armed hit: the weapon's WEAPONSOUNDHIT prop
    /// (a bow's or crossbow's AMMOSOUNDHIT first), else the sound of its weapon class
    /// (CCharAct.cpp:2629-2680). 0 when the class has none - the caller then falls
    /// back to the character's own hit sound, as SoundChar does.</summary>
    public static ushort GetWeaponHitSound(Item weapon, Random? rng = null)
    {
        rng ??= Random.Shared;
        ushort prop = GetWeaponSoundProp(weapon, "WEAPONSOUNDHIT", "AMMOSOUNDHIT");
        if (prop != 0)
            return prop;

        switch (weapon.ItemType)
        {
            case ItemType.WeaponMaceCrook:
            case ItemType.WeaponMacePick:
            case ItemType.WeaponMaceSmith:
            case ItemType.WeaponMaceStaff:
                return 0x233; // blunt01
            case ItemType.WeaponMaceSharp:
                return 0x232; // axe01
            case ItemType.WeaponSword:
            case ItemType.WeaponAxe:
                // Upstream asks the DEFINITION's equip layer (Item_GetDef()->
                // GetEquipLayer() == LAYER_HAND2).
                if (weapon.IsTwoHanded)
                    return rng.Next(2) == 0 ? (ushort)0x236 : (ushort)0x237; // hvyswrd1/4
                goto case ItemType.WeaponFence;
            case ItemType.WeaponFence:
                return rng.Next(2) == 0 ? (ushort)0x23B : (ushort)0x23C; // sword1/sword7
            case ItemType.WeaponBow:
            case ItemType.WeaponXBow:
                return 0x234; // xbow
            case ItemType.WeaponThrowing:
                return 0x5D2; // throwH
            case ItemType.WeaponWhip:
                return 0x67E; // whip01
            default:
                return 0;
        }
    }

    /// <summary>Fight_Hit's miss sound (CCharFight.cpp:2057-2072): the weapon's
    /// WEAPONSOUNDMISS prop (a bow's or crossbow's AMMOSOUNDMISS first), else a
    /// whoosh from the ranged set when the swing used a ranged skill - archery AND
    /// throwing, SKF_RANGED - or from the melee set otherwise.</summary>
    public static ushort GetMissSound(Item? weapon, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (weapon != null)
        {
            ushort prop = GetWeaponSoundProp(weapon, "WEAPONSOUNDMISS", "AMMOSOUNDMISS");
            if (prop != 0)
                return prop;
        }
        var set = CombatHelper.IsRangedWeapon(weapon) ? s_missRanged : s_missMelee;
        return set[rng.Next(set.Length)];
    }

    /// <summary>Weapon_GetSoundHit/Miss: for a bow or crossbow the ammo prop wins
    /// when positive, then the weapon prop. Only IT_WEAPON_BOW / IT_WEAPON_XBOW read
    /// the ammo prop - a throwing weapon does not. Instance value first, then the
    /// ITEMDEF's (GetPropNum(..., true) falls back to the base def).</summary>
    private static ushort GetWeaponSoundProp(Item weapon, string weaponKey, string ammoKey)
    {
        if (weapon.ItemType is ItemType.WeaponBow or ItemType.WeaponXBow)
        {
            ushort ammo = ReadProp(weapon, ammoKey);
            if (ammo != 0)
                return ammo;
        }
        return ReadProp(weapon, weaponKey);
    }

    private static ushort ReadProp(Item weapon, string key)
    {
        string? raw = weapon.TryGetTag(key, out string? inst) && !string.IsNullOrWhiteSpace(inst)
            ? inst
            : CombatHelper.GetWeaponDef(weapon)?.TagDefs.Get(key);
        if (string.IsNullOrWhiteSpace(raw))
            return 0;
        string s = raw.Trim();
        // Only a positive value counts (iAmmoSoundHit > 0 / iWeaponSoundHit > 0).
        if (s.StartsWith('-'))
            return 0;
        return CombatHelper.ParsePropUShort(s);
    }
}
