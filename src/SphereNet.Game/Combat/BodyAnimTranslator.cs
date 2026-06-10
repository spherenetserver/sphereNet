using SphereNet.Core.Enums;

namespace SphereNet.Game.Combat;

/// <summary>
/// Translates humanoid animation action indices (the 0x6E wire values our
/// engine uses internally, see <see cref="AnimationType"/>) into the anim.mul
/// group index of the actor's body type. Classic clients interpret the 0x6E
/// action field as a raw group index into the body's own animation file, so
/// a humanoid index sent to a monster or animal body plays the wrong group
/// (or none at all). Port of the reference body-translation table
/// (Sphere CChar::GenerateAnimate, credit: Sphere 0.56 / Source-X):
///   - bodies &lt; 200   : high-detail monster set (22 groups)
///   - bodies 200-399    : low-detail animal set (13 groups)
///   - bodies &gt;= 400  : humanoid set — returned unchanged
/// Mounted riders are a separate concern (the horse table) and are handled
/// by the callers' mounted mapping before this translation applies.
/// </summary>
public static class BodyAnimTranslator
{
    private const ushort MonsterBodyMax = 200;  // CREID_HORSE1
    private const ushort AnimalBodyMax = 400;   // CREID_MAN

    // Monster (high-detail) groups.
    private const ushort MonWalk = 0x00;
    private const ushort MonDie1 = 0x02;
    private const ushort MonDie2 = 0x03;
    private const ushort MonAttack1 = 0x04;
    private const ushort MonAttack2 = 0x05;
    private const ushort MonAttack3 = 0x06;
    private const ushort MonGetHit = 0x0A;
    private const ushort MonPillage = 0x0B;
    private const ushort MonStomp = 0x0C;
    private const ushort MonBlockRight = 0x0F;
    private const ushort MonBlockLeft = 0x10;
    private const ushort MonFidget1 = 0x11;
    private const ushort MonFidget2 = 0x12;

    // Animal (low-detail) groups.
    private const ushort AniWalk = 0x00;
    private const ushort AniRun = 0x01;
    private const ushort AniEat = 0x03;
    private const ushort AniAttack1 = 0x05;
    private const ushort AniAttack2 = 0x06;
    private const ushort AniGetHit = 0x07;
    private const ushort AniDie1 = 0x08;
    private const ushort AniFidget1 = 0x09;
    private const ushort AniFidget2 = 0x0A;
    private const ushort AniSleep = 0x0B;
    private const ushort AniDie2 = 0x0C;

    public static bool IsHumanoidBody(ushort bodyId) => bodyId >= AnimalBodyMax;
    public static bool IsAnimalBody(ushort bodyId) => bodyId is >= MonsterBodyMax and < AnimalBodyMax;
    public static bool IsMonsterBody(ushort bodyId) => bodyId < MonsterBodyMax;

    /// <summary>
    /// Translate a humanoid action index to the given body's group index.
    /// Humanoid bodies pass through unchanged. <paramref name="rand"/> is
    /// injectable for deterministic tests; the reference randomizes between
    /// equivalent attack/get-hit groups.
    /// </summary>
    public static ushort Translate(ushort bodyId, ushort action, Random? rand = null)
    {
        if (IsHumanoidBody(bodyId))
            return action;

        rand ??= Random.Shared;
        var anim = (AnimationType)action;

        if (IsAnimalBody(bodyId))
        {
            switch (anim)
            {
                case AnimationType.WalkUnarmed:
                case AnimationType.WalkArmed:
                case AnimationType.WalkWarmode:
                    return AniWalk;
                case AnimationType.RunUnarmed:
                case AnimationType.RunArmed:
                    return AniRun;
                case AnimationType.Stand:
                case AnimationType.StandWar1H:
                case AnimationType.StandWar2H:
                case AnimationType.Fidget1:
                    return AniFidget1;
                case AnimationType.FidgetYawn:
                    return AniFidget2;
                case AnimationType.CastDirected:
                    return AniAttack1;
                case AnimationType.CastArea:
                case AnimationType.Eat:
                    return AniEat;
                case AnimationType.GetHit:
                    return AniGetHit;
                case AnimationType.AttackWeapon:
                case AnimationType.Attack1HPierce:
                case AnimationType.Attack1HBash:
                case AnimationType.Attack2HBash:
                case AnimationType.Attack2HSlash:
                case AnimationType.Attack2HPierce:
                case AnimationType.AttackBow:
                case AnimationType.AttackXBow:
                case AnimationType.AttackWrestle:
                    return rand.Next(2) == 0 ? AniAttack1 : AniAttack2;
                case AnimationType.DieBackward:
                    return AniDie1;
                case AnimationType.DieForward:
                    return AniDie2;
                case AnimationType.Block:
                case AnimationType.Bow:
                case AnimationType.Salute:
                    return AniSleep;
                default:
                    return AniWalk;
            }
        }

        // Monster (high-detail) body.
        switch (anim)
        {
            case AnimationType.CastDirected:
                return MonStomp;
            case AnimationType.CastArea:
                return MonPillage;
            case AnimationType.DieBackward:
                return MonDie1;
            case AnimationType.DieForward:
                return MonDie2;
            case AnimationType.GetHit:
                return rand.Next(3) switch
                {
                    0 => MonGetHit,
                    1 => MonBlockRight,
                    _ => MonBlockLeft,
                };
            case AnimationType.AttackWeapon:
            case AnimationType.Attack1HPierce:
            case AnimationType.Attack1HBash:
            case AnimationType.Attack2HBash:
            case AnimationType.Attack2HSlash:
            case AnimationType.Attack2HPierce:
            case AnimationType.AttackBow:
            case AnimationType.AttackXBow:
            case AnimationType.AttackWrestle:
                return rand.Next(3) switch
                {
                    0 => MonAttack1,
                    1 => MonAttack2,
                    _ => MonAttack3,
                };
            case AnimationType.Fidget1:
                return MonFidget1;
            case AnimationType.FidgetYawn:
                return MonFidget2;
            default:
                return MonWalk;
        }
    }
}
