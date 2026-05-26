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

    public static int GetChebyshevDistance(Character a, Character b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

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
        if (attacker.IsStatFlag(StatFlag.Hidden))
            attacker.ClearStatFlag(StatFlag.Hidden);
        if (attacker.IsStatFlag(StatFlag.Invisible))
            attacker.ClearStatFlag(StatFlag.Invisible);
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
        Func<Point3D, Point3D, bool>? canSeeLos = null)
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

            if (Character.CombatArcheryMovementDelay > 0 && attacker.LastMoveTick > 0)
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
            if (GetChebyshevDistance(attacker, target) > 1)
                return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

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

    public static int ActiveDamageEra => Character.CombatDamageEra;
    public static int ActiveHitChanceEra => Character.CombatHitChanceEra;
}
