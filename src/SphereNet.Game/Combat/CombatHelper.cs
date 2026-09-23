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
        weapon.ItemType is ItemType.WeaponBow or ItemType.WeaponXBow or ItemType.WeaponThrowing;

    /// <summary>Throwing weapons are ranged but consume NO pack ammo — the
    /// wielded weapon itself is the projectile (Source-X SKILL_THROWING; pack
    /// t_weapon_throwing: TDATA3 empty, TDATA4 = flight animation art).</summary>
    public static bool IsThrowingWeapon(Item? weapon) =>
        weapon?.ItemType == ItemType.WeaponThrowing;

    public static bool IsMeleeWeapon(Item? weapon) => weapon == null || !IsRangedWeapon(weapon);

    /// <summary>The ITEMDEF an equipped weapon was made from (Source-X
    /// Item_GetDef). A named def such as "[ITEMDEF i_bow_exp] ID=I_BOW" shares
    /// its graphic with i_bow, so the BaseId alone resolves the wrong definition —
    /// the instance's SCRIPTDEF/ITEMDEF routing tags name the real one.</summary>
    public static SphereNet.Scripting.Definitions.ItemDef? GetWeaponDef(Item weapon) =>
        DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(weapon));

    /// <summary>Weapon min/max tile range. Ranged uses ITEMDEF RANGEL/RANGEH with ini fallback.</summary>
    public static (int Min, int Max) GetWeaponRange(Item? weapon)
    {
        if (!IsRangedWeapon(weapon))
        {
            var meleeDef = weapon != null ? GetWeaponDef(weapon) : null;
            int meleeMin = Math.Max(0, meleeDef?.RangeMin ?? 0);
            int meleeMax = meleeDef is { RangeMax: > 0 } ? meleeDef.RangeMax : 1;
            if (meleeMin > meleeMax)
                (meleeMin, meleeMax) = (meleeMax, meleeMin);
            return (meleeMin, meleeMax);
        }

        var def = weapon != null ? GetWeaponDef(weapon) : null;
        int minDist = def is { RangeMin: > 0 } ? def.RangeMin : Character.ArcheryMinDist;
        int maxDist = def is { RangeMax: > 0 } ? def.RangeMax : Character.ArcheryMaxDist;
        if (maxDist < 1)
            maxDist = Character.ArcheryMaxDist;
        if (minDist < 0)
            minDist = Character.ArcheryMinDist;

        // Malformed ITEMDEF ranges must not make the weapon permanently
        // unusable. Source-X's range parser normalises low/high; definitions
        // can also be supplied at runtime, so keep the combat boundary safe.
        minDist = Math.Max(0, minDist);
        maxDist = Math.Max(1, maxDist);
        if (minDist > maxDist)
            (minDist, maxDist) = (maxDist, minDist);
        return (minDist, maxDist);
    }

    /// <summary>State that makes a character invalid for a committed weapon
    /// swing. Kept here so player start, NPC start and the delayed hit phase
    /// cannot drift apart.</summary>
    public static bool IsInvalidSwingParticipant(Character ch, bool asTarget)
    {
        if (ch.IsDeleted || ch.IsDead)
            return true;
        if (ch.IsStatFlag(StatFlag.Stone))
            return true;
        if (!asTarget && (SphereNet.Game.Definitions.CharDefHelper.GetCanFlags(ch) & CanFlags.C_Statue) != 0)
            return true;
        if (asTarget && ch.IsStatFlag(StatFlag.Invul | StatFlag.Insubstantial | StatFlag.Ridden))
            return true;
        return false;
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

        // Source-X Fight_CanHit ship gate (CCharFight.cpp:1725): combat across
        // a ship boundary — one side aboard, the other not (different regions)
        // — is blocked unless COMBAT_ALLOWHITFROMSHIP. Both on the SAME ship
        // may always fight.
        if (!IsCombatFlagSet(CombatFlags.AllowHitFromShip) && atkRegion != tgtRegion &&
            ((atkRegion != null && atkRegion.IsFlag(RegionFlag.Ship)) ||
             (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.Ship))))
            return true;

        return false;
    }

    /// <summary>Source-X Cmd_Use_Obj self-dclick gate (CClientEvent.cpp:2368):
    /// without COMBAT_DCLICKSELF_UNMOUNTS, a mounted char in war mode with an
    /// active fight opens the paperdoll instead of accidentally dismounting.</summary>
    public static bool DClickSelfKeepsMount(Character ch) =>
        !IsCombatFlagSet(CombatFlags.DClickSelfUnmounts) &&
        ch.IsInWarMode && ch.FightTarget.IsValid;

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
        bool ignoreRangeLos = false,
        (int Min, int Max)? effectiveRange = null)
    {
        if (attacker == target || attacker.MapIndex != target.MapIndex ||
            IsInvalidSwingParticipant(attacker, asTarget: false) ||
            IsInvalidSwingParticipant(target, asTarget: true))
            return new SwingPrepFailure(SwingPrepResult.Abort, 0);

        // Fight_CanHit holds the swing while the target is hidden/invisible.
        // A known/stale serial must never bypass visibility and take damage.
        if (target.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible))
            return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

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
                var (minRange, maxRange) = effectiveRange ?? GetWeaponRange(weapon);
                NormaliseRange(ref minRange, ref maxRange);
                if (dist < minRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250, Msg.CombatArchTooclose);
                if (dist > maxRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                // Fight_CanHit checks LoS with bCombatCheck set (CCharFight.cpp:1720),
                // which withdraws the GM pass: staff do not swing through walls either.
                canSeeLos ??= (a, b) => world.CanSeeLOSFor(attacker, a, b);
                if (!canSeeLos(attacker.Position, target.Position))
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
            }

            // COMBAT_ARCHERYCANMOVE lets an archer fire while/just after moving,
            // bypassing the post-move settle delay.
            if (Character.CombatArcheryMovementDelay > 0 && attacker.LastMoveTick > 0 &&
                !IsCombatFlagSet(CombatFlags.ArcheryCanMove) &&
                !attacker.IsStatFlag(StatFlag.ArcherCanMove))
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
                var (minRange, maxRange) = effectiveRange ?? GetWeaponRange(weapon);
                NormaliseRange(ref minRange, ref maxRange);
                int distance = GetChebyshevDistance(attacker, target);
                if (distance < minRange || distance > maxRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                // Fight_CanHit checks LoS with bCombatCheck set (CCharFight.cpp:1720),
                // which withdraws the GM pass: staff do not swing through walls either.
                canSeeLos ??= (a, b) => world.CanSeeLOSFor(attacker, a, b);
                if (!canSeeLos(attacker.Position, target.Position))
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
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

    /// <summary>Attacker_Add's two fight-start lines. Both name the characters by
    /// their DISPLAY name (Source-X GetName(), CCharAttacker.cpp:72/:79, which falls
    /// back to the CHARDEF NAME):
    /// an NPC spawned from a chardef carries no instance name of its own, so the raw
    /// Name field left the emote reading "* is attacking you!*".</summary>
    public static (string AttackerName, string OthersText, string VictimText) FormatAttackEmotes(
        Character attacker, Character target)
    {
        string atkName = attacker.GetName();
        return (atkName,
            SphereNet.Game.Messages.ServerMessages.GetFormatted(
                SphereNet.Game.Messages.Msg.CombatAttacko, atkName, target.GetName()),
            SphereNet.Game.Messages.ServerMessages.GetFormatted(
                SphereNet.Game.Messages.Msg.CombatAttacks, atkName));
    }

    public static CombatFlags ActiveCombatFlags =>
        (CombatFlags)(Character.CombatFlags & 0xFFFFFFFF);

    /// <summary>True when the given COMBATFLAGS bit is enabled in sphere.ini.</summary>
    public static bool IsCombatFlagSet(CombatFlags flag) =>
        (Character.CombatFlags & (int)flag) != 0;

    // =====================================================================
    // Two-phase swing (Source-X windup -> hit). Source-X runs a swing in three
    // states (Fight_Hit, CCharFight.cpp:1923-1995): EQUIPPING waits the recoil,
    // READY sends the attack animation, SWINGING lands the blow once the
    // animation delay has passed. By default the animation lasts a second and
    // the recoil is the rest of the attack speed, so the damage arrives a second
    // after the swing is seen; COMBAT_PREHIT folds the animation delay into the
    // recoil and lands the blow as the animation starts.
    //
    // Here a swing starts at the animation: the pending hit is armed for the
    // animation delay and the next swing is due after it plus the recoil, which
    // is the same cycle measured from a different point.
    // =====================================================================

    /// <summary>kiMinSwingAnimationDelay (CCharFight.cpp:21): a swing animation
    /// lasts at least a second, and no attack is faster than that.</summary>
    public const int MinSwingAnimationTenths = 10;

    /// <summary>m_iRecoilDelay / m_iSwingAnimationDelay, in tenths of a second.</summary>
    public readonly record struct SwingDelays(int RecoilTenths, int AnimTenths);

    /// <summary>Source-X Fight_SetDefaultSwingDelays (CCharFight.cpp:1655-1679)
    /// for an attack speed of <paramref name="swingDelayMs"/>.</summary>
    public static SwingDelays GetDefaultSwingDelays(int swingDelayMs)
    {
        int attack = Math.Max(MinSwingAnimationTenths, swingDelayMs / 100);
        int recoil, anim;
        if (IsCombatFlagSet(CombatFlags.AnimHitSmooth))
        {
            // No recoil at all: the animation spans the whole attack.
            recoil = 0;
            anim = attack;
        }
        else
        {
            recoil = attack - MinSwingAnimationTenths;
            anim = MinSwingAnimationTenths;
        }
        if (IsCombatFlagSet(CombatFlags.PreHit))
        {
            recoil += anim;
            anim = 0;
        }
        return new SwingDelays(recoil, anim);
    }

    /// <summary>The @HitTry view of the delays (CCharFight.cpp:1929-1935): ARGN1 is
    /// the recoil and LOCAL.AnimDelay the animation delay - swapped under
    /// COMBAT_ANIM_HIT_SMOOTH, where ARGN1 is the one that paces the swing.</summary>
    public static (int ArgN1, int AnimDelay) ToHitTryArgs(SwingDelays delays) =>
        IsCombatFlagSet(CombatFlags.AnimHitSmooth)
            ? (delays.AnimTenths, delays.RecoilTenths)
            : (delays.RecoilTenths, delays.AnimTenths);

    /// <summary>Read the delays back from @HitTry, with the floors Source-X applies
    /// after the trigger (CCharFight.cpp:1945-1948): a recoil of at least a tenth,
    /// an animation delay of at least zero.</summary>
    public static SwingDelays FromHitTryArgs(long argN1, long animDelay)
    {
        bool smooth = IsCombatFlagSet(CombatFlags.AnimHitSmooth);
        long recoil = smooth ? animDelay : argN1;
        long anim = smooth ? argN1 : animDelay;
        return new SwingDelays(
            (int)Math.Clamp(recoil, 1, short.MaxValue),
            (int)Math.Clamp(anim, 0, short.MaxValue));
    }

    /// <summary>How long after the swing animation starts the blow lands
    /// (CCharFight.cpp:1995-1998): the animation delay, or - under
    /// COMBAT_ANIM_HIT_SMOOTH - the whole seconds the animation was sent with, so
    /// the damage does not trail the animation's end. 0 = at once (PREHIT).</summary>
    public static int GetSwingHitDelayMs(SwingDelays delays) =>
        IsCombatFlagSet(CombatFlags.AnimHitSmooth)
            ? GetSwingAnimDelay(delays) * 1000
            : delays.AnimTenths * 100;

    /// <summary>From one swing animation to the next: the hit delay, then the
    /// recoil that follows the blow.</summary>
    public static int GetSwingCycleMs(SwingDelays delays) =>
        GetSwingHitDelayMs(delays) + delays.RecoilTenths * 100;

    /// <summary>The wait before the first swing of a fight, or after a weapon
    /// change: Source-X enters the fight skill in WAR_SWING_EQUIPPING
    /// (CCharSkill.cpp:4559-4563), so the recoil runs before the first
    /// animation - the attack speed less the second the animation takes.</summary>
    public static int GetInitialSwingWaitMs(int swingDelayMs) =>
        GetDefaultSwingDelays(swingDelayMs).RecoilTenths * 100;

    /// <summary>Outcome of the hit-time reach/LoS re-check.</summary>
    public enum HitTimeDecision { Resolve, Miss, Wait, Drop }

    /// <summary>Whether the attacker is within the weapon's reach AND has LoS to
    /// the target right now (used to re-validate at hit time).</summary>
    public static bool InWeaponReachAndLos(
        GameWorld world, Character attacker, Character target, Item? weapon,
        PrivLevel privLevel, Func<Point3D, Point3D, bool>? canSeeLos = null,
        (int Min, int Max)? effectiveRange = null)
    {
        int dist = GetChebyshevDistance(attacker, target);
        var (min, max) = effectiveRange ?? GetWeaponRange(weapon);
        NormaliseRange(ref min, ref max);
        if (dist < min || dist > max)
            return false;
        // Combat LoS binds staff too (bCombatCheck, CCharLOS.cpp:25).
        canSeeLos ??= (a, b) => world.CanSeeLOSFor(attacker, a, b);
        if (!canSeeLos(attacker.Position, target.Position)) return false;
        return true;
    }

    /// <summary>Decide what to do with a pending hit when its windup elapses
    /// (Source-X StayInRange / SwingNoRange semantics).</summary>
    public static HitTimeDecision EvaluateHitTime(
        GameWorld world, Character attacker, Character? target, Item? weapon,
        PrivLevel privLevel, long nowMs, long deadlineMs,
        Func<Point3D, Point3D, bool>? canSeeLos = null,
        bool? swingNoRange = null,
        (int Min, int Max)? effectiveRange = null)
    {
        if (target == null || attacker == target || attacker.MapIndex != target.MapIndex ||
            IsInvalidSwingParticipant(attacker, asTarget: false) ||
            IsInvalidSwingParticipant(target, asTarget: true) ||
            target.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible) ||
            IsCombatBlockedByRegion(world, attacker, target))
            return HitTimeDecision.Drop;

        // States that arrive DURING the windup. Source-X re-runs Fight_CanHit at the
        // top of the hit phase (Fight_Hit, CCharFight.cpp:1813) and returns without
        // damage unless it says READY, so a swing wound up before a paralyse or a
        // sleep does not land after one. SphereNet checked these only in TrySwingAt,
        // which a pending hit never re-enters.
        //
        // The disposition is WAIT, not Drop: Fight_CanHit answers WAR_SWING_SWINGING
        // for all three (CCharFight.cpp:1696-1699), which holds the swing rather than
        // spending it. Clearing the pending hit here would be a different rule.
        bool paralyzeCanSwing = IsCombatFlagSet(CombatFlags.ParalyzeCanSwing);
        if ((attacker.IsStatFlag(StatFlag.Freeze) && !paralyzeCanSwing) ||
            attacker.IsStatFlag(StatFlag.Sleeping) ||
            target.IsStatFlag(StatFlag.Sleeping))
            return HitTimeDecision.Wait;

        // An archer who walked DURING the windup must still serve the post-move
        // settle. Source-X applies this inside the hit phase (Fight_Hit,
        // CCharFight.cpp:1854) and returns WAR_SWING_EQUIPPING, which SPENDS the
        // swing and restarts the recoil - a different disposition from the holds
        // above, and the same one out-of-range already uses here. Checking it only
        // where a swing starts let a player step out of the configured archery delay
        // by beginning the shot first.
        //
        // Ranged only: SphereNet's melee movement delay in ValidateSwingPrep has no
        // Source-X counterpart, so it is deliberately not repeated here.
        if (IsRangedWeapon(weapon) &&
            Character.CombatArcheryMovementDelay > 0 && attacker.LastMoveTick > 0 &&
            !IsCombatFlagSet(CombatFlags.ArcheryCanMove) &&
            !attacker.IsStatFlag(StatFlag.ArcherCanMove) &&
            nowMs - attacker.LastMoveTick < Character.CombatArcheryMovementDelay)
            return HitTimeDecision.Miss;

        if (InWeaponReachAndLos(world, attacker, target, weapon, privLevel, canSeeLos, effectiveRange))
            return HitTimeDecision.Resolve;

        // Out of reach / LoS when the hit should land. Source-X re-checks both in
        // the SWINGING state (Fight_CanHit LoS :1718, the reach test :1906-1918):
        // COMBAT_STAYINRANGE spends the swing (WAR_SWING_EQUIPPING - the recoil
        // restarts, nothing else happens); otherwise the loaded blow is HELD
        // (swingTypeHold) and lands as soon as the target is back in reach.
        //
        // The hold is bounded by the deadline the swing was armed with, so a
        // target that never comes back does not pin a pending hit for the rest of
        // the fight; upstream holds without a limit but also has no pending-hit
        // slot to leak.
        bool preHit = IsCombatFlagSet(CombatFlags.PreHit);
        if (IsCombatFlagSet(CombatFlags.StayInRange) && !preHit)
            return HitTimeDecision.Miss;                 // moved out -> swing spent
        return nowMs >= deadlineMs ? HitTimeDecision.Drop : HitTimeDecision.Wait;
    }

    private static void NormaliseRange(ref int min, ref int max)
    {
        min = Math.Max(0, min);
        max = Math.Max(0, max);
        if (min > max)
            (min, max) = (max, min);
    }

    /// <summary>True when the swing may START out of range / without LoS
    /// (COMBAT_SWING_NORANGE, unless PREHIT overrides it).</summary>
    public static bool SwingIgnoresStartRange() =>
        IsCombatFlagSet(CombatFlags.SwingNoRange) && !IsCombatFlagSet(CombatFlags.PreHit);

    /// <summary>Whether a struck character flinches - Source-X OnTakeDamage plays
    /// ANIM_GET_HIT only after damage actually landed, not on a blow that kills, and
    /// not while the character is in the middle of its own swing, so as not to cut
    /// that animation off (CCharFight.cpp:1027-1060). Callers ask only once damage is
    /// known to be above zero.</summary>
    public static bool ShouldPlayGetHit(Character target) =>
        !target.IsDead && !target.IsDeleted && target.Hits > 0 && !target.HasPendingHit;

    /// <summary>The 0x6E frame-delay byte of a swing - Source-X's
    /// iSwingAnimationDelayInSeconds (CCharFight.cpp:1973-1988): the animation delay
    /// in whole seconds, never below one. By default that is the one second a swing
    /// animation lasts (kiMinSwingAnimationDelay, :21); COMBAT_ANIM_HIT_SMOOTH
    /// stretches it to the whole attack; under COMBAT_PREHIT it is 1, or the recoil
    /// in seconds with ANIM_HIT_SMOOTH as well. The client paces a server animation
    /// at (delay + 2) frame times (ClassicUO Mobile.cs:610).</summary>
    public static byte GetSwingAnimDelay(SwingDelays delays)
    {
        bool preHit = IsCombatFlagSet(CombatFlags.PreHit);
        if (preHit && !IsCombatFlagSet(CombatFlags.AnimHitSmooth))
            return 1;
        int seconds = (preHit ? delays.RecoilTenths : delays.AnimTenths) / 10;
        return (byte)Math.Clamp(seconds, 1, 255);
    }

    /// <summary><see cref="GetSwingAnimDelay(SwingDelays)"/> for the default
    /// delays of an attack speed.</summary>
    public static byte GetSwingAnimDelay(int swingDelayMs) =>
        GetSwingAnimDelay(GetDefaultSwingDelays(swingDelayMs));

    /// <summary>
    /// Resolve which ammo a ranged weapon fires from its ITEMDEF (Source-X
    /// CItem::Weapon_GetRangedAmmoRes / Weapon_GetRangedAmmoAnim, CItem.cpp:5013-5056).
    /// AMMOTYPE names the exact ammo item (resolved to a baseid via
    /// <paramref name="resolveDefName"/>), else TDATA3 does (m_ttWeaponBow.m_ridAmmo).
    /// A def whose TDATA3 is 0 needs no ammo at all: Source-X only searches the pack
    /// when the resource id is valid (CCharFight.cpp:1864-1874), so the pack's
    /// "i_bow_exp TDATA3=0" fires without arrows. The in-flight graphic is AMMOANIM,
    /// else TDATA4 (m_ridAmmoX). Only when there is no def at all do the legacy
    /// defaults apply: arrows (0x0F3F) for bows, bolts (0x1BFB) for crossbows. A zero
    /// <c>BaseId</c> with <c>RequiresAmmo</c> means "match by the fallback ammo
    /// ItemType" instead of a specific item id.
    /// </summary>
    public static (ushort BaseId, ItemType FallbackType, ushort Gfx, bool RequiresAmmo) ResolveAmmoSpec(
        SphereNet.Scripting.Definitions.ItemDef? weaponDef, ItemType weaponType, Func<string, ushort>? resolveDefName)
    {
        bool bow = weaponType == ItemType.WeaponBow;
        bool throwing = weaponType == ItemType.WeaponThrowing;
        ItemType fallbackType = bow ? ItemType.WeaponArrow : ItemType.WeaponBolt;
        // Throwing: no legacy bolt default — gfx 0 tells the caller to fly the
        // weapon's own graphic (TDATA4/AMMOANIM still overrides when set).
        ushort gfx = bow ? (ushort)0x0F3F : throwing ? (ushort)0 : (ushort)0x1BFB;
        ushort baseId = 0;
        // Throwing weapons are their own projectile (pack TDATA3 empty).
        bool requiresAmmo = !throwing;

        if (weaponDef != null)
        {
            bool ammoTypeResolved = false;
            if (!string.IsNullOrWhiteSpace(weaponDef.AmmoType) && resolveDefName != null)
            {
                ushort resolved = resolveDefName(weaponDef.AmmoType);
                if (resolved != 0) { baseId = resolved; ammoTypeResolved = true; }
            }
            if (!ammoTypeResolved && !throwing)
            {
                if (weaponDef.TData3 is > 0 and <= ushort.MaxValue)
                    baseId = (ushort)weaponDef.TData3;
                else if (string.IsNullOrWhiteSpace(weaponDef.AmmoType))
                    requiresAmmo = false;
                // An AMMOTYPE that fails to resolve keeps the type-based fallback.
            }

            if (weaponDef.AmmoAnim != 0) gfx = weaponDef.AmmoAnim;
            else if (weaponDef.TData4 is > 0 and <= ushort.MaxValue) gfx = (ushort)weaponDef.TData4;
        }
        return (baseId, fallbackType, gfx, requiresAmmo);
    }

    /// <summary>The in-flight art, hue and render mode a ranged shot shows
    /// (Source-X CItem::Weapon_GetRangedAmmoAnim, CItem.cpp:5013-5041): the
    /// AMMOANIM / AMMOANIMHUE / AMMOANIMRENDER props, read from the item first and
    /// its ITEMDEF second (GetPropStr/GetPropNum with fDef), with TDATA4 as the
    /// graphic when no AMMOANIM is set. A throwing weapon with neither flies its
    /// own graphic.</summary>
    public static (ushort Gfx, ushort Hue, uint Render) ResolveRangedAnim(Item weapon)
    {
        var def = GetWeaponDef(weapon);
        var spec = ResolveAmmoSpec(def, weapon.ItemType, Item.ResolveDefName);
        ushort gfx = spec.Gfx;
        ushort hue = def?.AmmoAnimHue ?? 0;
        uint render = def?.AmmoAnimRender ?? 0;

        if (weapon.TryGetTag("AMMOANIM", out string? anim) && !string.IsNullOrWhiteSpace(anim))
        {
            ushort instGfx = ParseAnimValue(anim);
            if (instGfx != 0) gfx = instGfx;
        }
        if (weapon.TryGetTag("AMMOANIMHUE", out string? hueRaw) && !string.IsNullOrWhiteSpace(hueRaw))
        {
            ushort instHue = ParseAnimValue(hueRaw);
            if (instHue != 0) hue = instHue;
        }
        if (weapon.TryGetTag("AMMOANIMRENDER", out string? renderRaw) && !string.IsNullOrWhiteSpace(renderRaw))
        {
            ushort instRender = ParseAnimValue(renderRaw);
            if (instRender != 0) render = instRender;
        }

        if (gfx == 0 && weapon.ItemType == ItemType.WeaponThrowing)
            gfx = weapon.DispIdFull;
        return (gfx, hue, render);
    }

    /// <summary>A numeric item prop as Sphere writes it: a defname, 0x-hex, a
    /// leading-zero hex or plain decimal.</summary>
    internal static ushort ParsePropUShort(string raw) => ParseAnimValue(raw);

    private static ushort ParseAnimValue(string raw)
    {
        string s = raw.Trim();
        if (s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_'))
            return Item.ResolveDefName?.Invoke(s) ?? 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        // Sphere numerics with a leading 0 are hex (01c1c); plain digits decimal.
        bool hex = s.Length > 1 && s[0] == '0';
        return hex
            ? (ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ushort h) ? h : (ushort)0)
            : (ushort.TryParse(s, out ushort d) ? d : (ushort)0);
    }

    /// <summary>Find ranged ammo anywhere in the backpack tree. Source-X's
    /// ContentFind searches nested bags; limiting this to direct pack contents
    /// made ordinary bagged arrows unusable. Depth/cycle guards also tolerate a
    /// malformed loaded containment graph.</summary>
    public static Item? FindAmmoInContainer(Item? root, ushort baseId,
        ItemType fallbackType, int maxDepth = 16)
    {
        if (root == null || root.IsDeleted || maxDepth <= 0)
            return null;
        return FindAmmoInContainerCore(root, baseId, fallbackType, maxDepth, []);
    }

    private static Item? FindAmmoInContainerCore(Item container, ushort baseId,
        ItemType fallbackType, int depth, HashSet<uint> visited)
    {
        if (depth <= 0 || !visited.Add(container.Uid.Value))
            return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = baseId != 0 ? item.BaseId == baseId : item.ItemType == fallbackType;
            if (matches && item.Amount > 0)
                return item;
            // Source-X CContainer::ContentFind checks IsSearchable before descending
            // (CContainer.cpp:236), so a locked chest inside the pack is not part of
            // an ordinary resource search — arrows separated from the player's
            // reachable quiver were still being fired.
            if (!item.IsSearchableContainer) continue;

            var nested = FindAmmoInContainerCore(item, baseId, fallbackType, depth - 1, visited);
            if (nested != null)
                return nested;
        }
        return null;
    }

    public static int ActiveDamageEra => Character.CombatDamageEra;
    public static int ActiveHitChanceEra => Character.CombatHitChanceEra;
}
