using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Game.Combat;

/// <summary>
/// Shared combat gates used by player and NPC swing paths. Mirrors Source-X
/// <c>f_combat_hit</c> / <c>Fight_CanHit</c> checks that must run before
/// swing recoil is consumed.
/// </summary>
public static class CombatHelper
{
    public static bool IsRangedWeapon(Item? weapon) =>
        weapon != null &&
        (weapon.ItemType == ItemType.WeaponBow || weapon.ItemType == ItemType.WeaponXBow);

    public static bool IsMeleeWeapon(Item? weapon) => weapon == null || !IsRangedWeapon(weapon);

    /// <summary>Weapon min/max tile range. Ranged uses ITEMDEF RANGEL/RANGEH with ini fallback.</summary>
    public static (int Min, int Max) GetWeaponRange(Item? weapon)
    {
        if (!IsRangedWeapon(weapon))
            return (1, 1);

        var def = weapon != null ? DefinitionLoader.GetItemDef(weapon.BaseId) : null;
        int minDist = def is { RangeMin: > 0 } ? def.RangeMin : Character.ArcheryMinDist;
        int maxDist = def is { RangeMax: > 0 } ? def.RangeMax : Character.ArcheryMaxDist;
        if (maxDist < 1)
            maxDist = Character.ArcheryMaxDist;
        if (minDist < 0)
            minDist = Character.ArcheryMinDist;
        return (minDist, maxDist);
    }

    public static int GetChebyshevDistance(Character a, Character b)
    {
        if (a.MapIndex != b.MapIndex) return int.MaxValue;
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>Source-X safe/nopvp region combat blocks for f_combat_hit.</summary>
    public static bool IsCombatBlockedByRegion(GameWorld world, Character attacker, Character target)
    {
        var atkRegion = world.FindRegion(attacker.Position);
        if (atkRegion != null && atkRegion.IsFlag(RegionFlag.Safe))
            return true;

        var tgtRegion = world.FindRegion(target.Position);
        if (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.Safe))
            return true;

        if (attacker.IsPlayer && target.IsPlayer)
        {
            if (atkRegion != null && atkRegion.IsFlag(RegionFlag.NoPvP))
                return true;
            if (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.NoPvP))
                return true;
        }

        return false;
    }

    /// <summary>COMBAT_FACECOMBAT: player must face target within ±1 direction.</summary>
    public static bool IsFacingTarget(Character attacker, Character target)
    {
        if ((Character.CombatFlags & (int)CombatFlags.FaceCombat) == 0)
            return true;
        if (!attacker.IsPlayer)
            return true;

        var desired = attacker.Position.GetDirectionTo(target.Position);
        int cur = (int)attacker.Direction & 0x07;
        int want = (int)desired & 0x07;
        int diff = Math.Abs(cur - want);
        if (diff > 4)
            diff = 8 - diff;
        return diff <= 1;
    }

    public static bool HasShieldEquipped(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.HasShield))
            return true;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        return twoHand != null && twoHand.ItemType == ItemType.Shield;
    }

    /// <summary>Non-GM attackers lose hidden/invisible when initiating a swing.</summary>
    public static void RevealOnAttack(Character attacker, PrivLevel privLevel)
    {
        if (privLevel >= PrivLevel.GM)
            return;
        attacker.ClearHiddenState();
    }

    public enum SwingPrepResult
    {
        Ready,
        RetryLater,
        Abort,
    }

    public readonly struct SwingPrepFailure
    {
        public SwingPrepFailure(SwingPrepResult result, long retryMs, string? messageKey = null)
        {
            Result = result;
            RetryMs = retryMs;
            MessageKey = messageKey;
        }

        public SwingPrepResult Result { get; }
        public long RetryMs { get; }
        public string? MessageKey { get; }
    }

    /// <summary>
    /// Validates swing preconditions without consuming recoil. Returns
    /// <see cref="SwingPrepResult.Ready"/> when ResolveAttack may proceed.
    /// </summary>
    public static SwingPrepFailure ValidateSwingPrep(
        GameWorld world,
        Character attacker,
        Character target,
        Item? weapon,
        PrivLevel privLevel,
        long nowMs,
        Func<Point3D, Point3D, bool>? canSeeLos = null,
        bool ignoreRangeLos = false)
    {
        if (attacker.IsDead || target.IsDead)
            return new SwingPrepFailure(SwingPrepResult.Abort, 0);

        if (IsCombatBlockedByRegion(world, attacker, target))
            return new SwingPrepFailure(SwingPrepResult.Abort, 0);

        if (!IsFacingTarget(attacker, target))
            return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

        if (IsRangedWeapon(weapon))
        {
            if (HasShieldEquipped(attacker))
                return new SwingPrepFailure(SwingPrepResult.Abort, 0, Msg.ItemuseBowShield);

            // COMBAT_SWING_NORANGE: the swing may START at any range / without LoS;
            // reach + LoS are re-checked when the hit resolves instead.
            if (!ignoreRangeLos)
            {
                int dist = GetChebyshevDistance(attacker, target);
                var (minRange, maxRange) = GetWeaponRange(weapon);
                if (dist < minRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250, Msg.CombatArchTooclose);
                if (dist > maxRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                if (privLevel < PrivLevel.GM)
                {
                    canSeeLos ??= world.CanSeeLOS;
                    if (!canSeeLos(attacker.Position, target.Position))
                        return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
                }
            }

            // COMBAT_ARCHERYCANMOVE lets an archer fire while/just after moving,
            // bypassing the post-move settle delay.
            if (Character.CombatArcheryMovementDelay > 0 && attacker.LastMoveTick > 0 &&
                !IsCombatFlagSet(CombatFlags.ArcheryCanMove))
            {
                long sinceMove = nowMs - attacker.LastMoveTick;
                if (sinceMove < Character.CombatArcheryMovementDelay)
                {
                    long wait = Character.CombatArcheryMovementDelay - sinceMove;
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, wait);
                }
            }
        }
        else
        {
            if (!ignoreRangeLos)
            {
                if (GetChebyshevDistance(attacker, target) > 1)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                if (privLevel < PrivLevel.GM)
                {
                    canSeeLos ??= world.CanSeeLOS;
                    if (!canSeeLos(attacker.Position, target.Position))
                        return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
                }
            }

            if (Character.CombatMeleeMovementDelay > 0 && attacker.LastMoveTick > 0)
            {
                long sinceMove = nowMs - attacker.LastMoveTick;
                if (sinceMove < Character.CombatMeleeMovementDelay)
                {
                    long wait = Character.CombatMeleeMovementDelay - sinceMove;
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, wait);
                }
            }
        }

        return new SwingPrepFailure(SwingPrepResult.Ready, 0);
    }

    public static CombatFlags ActiveCombatFlags =>
        (CombatFlags)(Character.CombatFlags & 0xFFFFFFFF);

    /// <summary>True when the given COMBATFLAGS bit is enabled in sphere.ini.</summary>
    public static bool IsCombatFlagSet(CombatFlags flag) =>
        (Character.CombatFlags & (int)flag) != 0;

    // =====================================================================
    // Two-phase swing (Source-X windup -> hit). The default (flagless) case
    // keeps a zero-length windup so the hit resolves in the same tick the
    // swing starts — byte-for-byte the previous atomic behaviour. A windup
    // window only opens for STAYINRANGE / SWING_NORANGE (and PREHIT forces the
    // hit back to swing-start and disables SWING_NORANGE).
    // =====================================================================

    /// <summary>Outcome of the hit-time reach/LoS re-check.</summary>
    public enum HitTimeDecision { Resolve, Miss, Wait, Drop }

    /// <summary>Whether the attacker is within the weapon's reach AND has LoS to
    /// the target right now (used to re-validate at hit time).</summary>
    public static bool InWeaponReachAndLos(
        GameWorld world, Character attacker, Character target, Item? weapon,
        PrivLevel privLevel, Func<Point3D, Point3D, bool>? canSeeLos = null)
    {
        int dist = GetChebyshevDistance(attacker, target);
        if (IsRangedWeapon(weapon))
        {
            var (min, max) = GetWeaponRange(weapon);
            if (dist < min || dist > max) return false;
        }
        else if (dist > 1)
        {
            return false;
        }
        if (privLevel < PrivLevel.GM)
        {
            canSeeLos ??= world.CanSeeLOS;
            if (!canSeeLos(attacker.Position, target.Position)) return false;
        }
        return true;
    }

    /// <summary>Decide what to do with a pending hit when its windup elapses
    /// (Source-X StayInRange / SwingNoRange semantics).</summary>
    public static HitTimeDecision EvaluateHitTime(
        GameWorld world, Character attacker, Character? target, Item? weapon,
        PrivLevel privLevel, long nowMs, long deadlineMs, Func<Point3D, Point3D, bool>? canSeeLos = null)
    {
        if (target == null || target.IsDead || target.IsDeleted)
            return HitTimeDecision.Drop;

        if (InWeaponReachAndLos(world, attacker, target, weapon, privLevel, canSeeLos))
            return HitTimeDecision.Resolve;

        // Out of reach / LoS when the hit should land:
        bool preHit = IsCombatFlagSet(CombatFlags.PreHit);
        if (IsCombatFlagSet(CombatFlags.StayInRange) && !preHit)
            return HitTimeDecision.Miss;                 // moved out -> miss
        if (IsCombatFlagSet(CombatFlags.SwingNoRange) && !preHit)
            return nowMs >= deadlineMs ? HitTimeDecision.Drop : HitTimeDecision.Wait;
        // Default: resolve anyway — a flagless swing only starts in range, so the
        // hit landing in the same tick is exactly the previous atomic behaviour.
        return HitTimeDecision.Resolve;
    }

    /// <summary>Windup length before the hit lands. 0 = atomic (hit at swing
    /// start), which is the flagless default and the PREHIT case. STAYINRANGE /
    /// SWING_NORANGE (without PREHIT) open a full-swing window so the reach/LoS
    /// re-check has meaning. <paramref name="swingNoRange"/> overrides the
    /// SWING_NORANGE flag for this swing (the @HitCheck LOCAL.Recoil_NoRange
    /// contract); null keeps the global flag.</summary>
    public static int GetSwingHitDelayMs(int swingDelayMs, bool? swingNoRange = null)
    {
        if (IsCombatFlagSet(CombatFlags.PreHit)) return 0;
        bool window = IsCombatFlagSet(CombatFlags.StayInRange) ||
            (swingNoRange ?? IsCombatFlagSet(CombatFlags.SwingNoRange));
        return window ? Math.Max(0, swingDelayMs) : 0;
    }

    /// <summary>True when the swing may START out of range / without LoS
    /// (COMBAT_SWING_NORANGE, unless PREHIT overrides it).</summary>
    public static bool SwingIgnoresStartRange() =>
        IsCombatFlagSet(CombatFlags.SwingNoRange) && !IsCombatFlagSet(CombatFlags.PreHit);

    /// <summary>Legacy 0x6E animation per-frame delay for COMBAT_ANIM_HIT_SMOOTH:
    /// 0 when the flag is off (the fixed default swing speed), otherwise a value
    /// scaled to the swing time so a slow weapon shows a correspondingly slow swing.
    /// The exact pacing is client-interpreted; the value is proportional and clamped
    /// to a byte.</summary>
    public static byte GetSwingAnimDelay(int swingDelayMs)
    {
        if (!IsCombatFlagSet(CombatFlags.AnimHitSmooth)) return 0;
        // ~7-frame attack animation paced across the swing: per-frame delay scales
        // with the swing time, in the 0x6E delay unit. At least 1 when enabled.
        return (byte)Math.Clamp(swingDelayMs / 70, 1, 255);
    }

    /// <summary>
    /// Resolve which ammo a ranged weapon fires from its ITEMDEF. AMMOTYPE names
    /// the exact ammo item (resolved to a baseid via <paramref name="resolveDefName"/>)
    /// and AMMOANIM overrides the in-flight projectile graphic. When the weapon
    /// def specifies neither, the legacy defaults apply: arrows (0x0F3F) for bows,
    /// bolts (0x1BFB) for crossbows. A zero <c>BaseId</c> means "match by the
    /// fallback ammo ItemType" instead of a specific item id.
    /// </summary>
    public static (ushort BaseId, ItemType FallbackType, ushort Gfx) ResolveAmmoSpec(
        SphereNet.Scripting.Definitions.ItemDef? weaponDef, ItemType weaponType, Func<string, ushort>? resolveDefName)
    {
        bool bow = weaponType == ItemType.WeaponBow;
        ItemType fallbackType = bow ? ItemType.WeaponArrow : ItemType.WeaponBolt;
        ushort gfx = bow ? (ushort)0x0F3F : (ushort)0x1BFB;
        ushort baseId = 0;

        if (weaponDef != null)
        {
            if (!string.IsNullOrWhiteSpace(weaponDef.AmmoType) && resolveDefName != null)
            {
                ushort resolved = resolveDefName(weaponDef.AmmoType);
                if (resolved != 0) baseId = resolved;
            }
            if (weaponDef.AmmoAnim != 0) gfx = weaponDef.AmmoAnim;
        }
        return (baseId, fallbackType, gfx);
    }

    public static int ActiveDamageEra => Character.CombatDamageEra;
    public static int ActiveHitChanceEra => Character.CombatHitChanceEra;
}
