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
    /// <summary>Map an on-foot humanoid action to its mounted-rider variant.
    /// A foot animation sent for a mounted body makes the client visually
    /// dismount and remount the rider for the animation's duration (the
    /// fishing-swing report).
    ///
    /// Source-X's own horseback table (GenerateAnimate, CCharAct.cpp:852-906): the
    /// one-handed swings and the punch ride as the horse attack, the TWO-handed
    /// swings as the slap, a directed cast as the attack and an area cast as the
    /// bow attack, and a bow, salute or meal as the crossbow attack; walking, running,
    /// standing and fidgeting become the ride and stand actions, and anything else
    /// the stand. The table this replaced sent two-handed swings through the attack,
    /// both casts and the courtesies through the slap, and passed the foot actions
    /// through untranslated.</summary>
    public static ushort ToMounted(ushort action) => action switch
    {
        (ushort)AnimationType.WalkUnarmed or
        (ushort)AnimationType.WalkArmed or
        (ushort)AnimationType.WalkWarmode => (ushort)AnimationType.HorseRideSlow,
        (ushort)AnimationType.RunUnarmed or
        (ushort)AnimationType.RunArmed => (ushort)AnimationType.HorseRideFast,
        (ushort)AnimationType.Stand or
        (ushort)AnimationType.StandWar1H or
        (ushort)AnimationType.StandWar2H => (ushort)AnimationType.HorseStand,
        (ushort)AnimationType.Fidget1 or
        (ushort)AnimationType.FidgetYawn => (ushort)AnimationType.HorseSlap,
        (ushort)AnimationType.AttackWeapon or
        (ushort)AnimationType.Attack1HPierce or
        (ushort)AnimationType.Attack1HBash or
        (ushort)AnimationType.AttackWrestle or
        (ushort)AnimationType.CastDirected => (ushort)AnimationType.HorseAttack,
        (ushort)AnimationType.Attack2HBash or
        (ushort)AnimationType.Attack2HSlash or
        (ushort)AnimationType.Attack2HPierce or
        (ushort)AnimationType.GetHit or
        (ushort)AnimationType.Block => (ushort)AnimationType.HorseSlap,
        (ushort)AnimationType.CastArea or
        (ushort)AnimationType.AttackBow => (ushort)AnimationType.HorseAttackBow,
        (ushort)AnimationType.AttackXBow or
        (ushort)AnimationType.Bow or
        (ushort)AnimationType.Salute or
        (ushort)AnimationType.Eat => (ushort)AnimationType.HorseAttackXBow,
        // Already a rider's action (a script or a caller that translated first).
        >= (ushort)AnimationType.HorseRideSlow and <= (ushort)AnimationType.HorseSlap => action,
        _ => (ushort)AnimationType.HorseStand,
    };

    /// <summary>The generic weapon swing (ANIM_ATTACK_WEAPON) turned into the
    /// swing of the weapon in hand - Source-X GenerateAnimate (CCharAct.cpp:811-850):
    /// swords, axes and picks slash, fencing weapons pierce, the mace family (and
    /// anything unlisted) bashes, one- or two-handed by the weapon's hand; throwing
    /// slashes one-handed, a whip bashes one-handed, bows and crossbows draw their
    /// own actions. Any other action, or no weapon, is returned unchanged - so bare
    /// hands swing ANIM_ATTACK_WEAPON itself.</summary>
    public static ushort ForWeapon(ushort action, SphereNet.Game.Objects.Items.Item? weapon)
    {
        if (weapon == null || action != (ushort)AnimationType.AttackWeapon ||
            !SphereNet.Game.Objects.ObjBase.IsTypeWeapon(weapon.ItemType))
            return action;
        bool twoHand = weapon.IsTwoHanded;
        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponMacePick => twoHand
                ? (ushort)AnimationType.Attack2HSlash : (ushort)AnimationType.AttackWeapon,
            ItemType.WeaponFence => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponThrowing => (ushort)AnimationType.AttackWeapon,
            ItemType.WeaponBow => (ushort)AnimationType.AttackBow,
            ItemType.WeaponXBow => (ushort)AnimationType.AttackXBow,
            ItemType.WeaponWhip => (ushort)AnimationType.Attack1HBash,
            _ => twoHand
                ? (ushort)AnimationType.Attack2HBash : (ushort)AnimationType.Attack1HBash,
        };
    }

    /// <summary>The weapon a character fights with - Source-X m_uidWeapon, which
    /// only ever names a weapon-type item in a hand (a shield is not one).</summary>
    public static SphereNet.Game.Objects.Items.Item? WeaponInHand(SphereNet.Game.Objects.Characters.Character ch)
    {
        var hand = ch.GetEquippedItem(Layer.OneHanded);
        if (hand != null && SphereNet.Game.Objects.ObjBase.IsTypeWeapon(hand.ItemType))
            return hand;
        hand = ch.GetEquippedItem(Layer.TwoHanded);
        return hand != null && SphereNet.Game.Objects.ObjBase.IsTypeWeapon(hand.ItemType) ? hand : null;
    }

    /// <summary>GenerateAnimate with the character's own weapon in hand.</summary>
    public static ushort Generate(SphereNet.Game.Objects.Characters.Character ch, ushort action,
        Random? rand = null) => Generate(ch, action, WeaponInHand(ch), rand);

    /// <summary>The whole of Source-X GenerateAnimate for one character: the weapon
    /// in hand first, then the saddle, then the body (CCharAct.cpp:797-2250).</summary>
    public static ushort Generate(SphereNet.Game.Objects.Characters.Character ch, ushort action,
        SphereNet.Game.Objects.Items.Item? weapon, Random? rand = null)
    {
        action = ForWeapon(action, weapon);
        if (ch.IsMounted)
            return ToMounted(action);
        return Translate(ch.BodyId, action, rand);
    }

    private const ushort MonsterBodyMax = 200;  // CREID_HORSE1
    private const ushort AnimalBodyMax = 400;   // CREID_MAN

    // Monster (high-detail) groups.
    private const ushort MonWalk = 0x00;
    private const ushort MonStand = 0x01;
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
    private const ushort AniStand = 0x02;
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

        // Source-X GenerateAnimate, the animal branch without custom ANIM flags
        // (CCharAct.cpp:1470-1695): standing is ANI_STAND, a block is taken as a hit,
        // and anything unlisted stands - it used to fidget, lie down and walk.
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
                    return AniStand;
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
                case AnimationType.Block:
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
                case AnimationType.Bow:
                case AnimationType.Salute:
                    return AniSleep;
                default:
                    return AniStand;
            }
        }

        // Monster (high-detail) body - Source-X GenerateAnimate's monster branch
        // without mobtypes or custom ANIM flags (CCharAct.cpp:1700-2225): a get-hit
        // is MON_GETHIT (it was a random pick that included the two blocks), a block
        // is a random block, standing and anything unlisted is MON_STAND (it was the
        // walk), and the courtesies fidget.
        switch (anim)
        {
            case AnimationType.WalkUnarmed:
            case AnimationType.WalkArmed:
            case AnimationType.WalkWarmode:
            case AnimationType.RunUnarmed:
            case AnimationType.RunArmed:
                return MonWalk;
            case AnimationType.Stand:
            case AnimationType.StandWar1H:
            case AnimationType.StandWar2H:
                return MonStand;
            case AnimationType.CastDirected:
                return MonStomp;
            case AnimationType.CastArea:
                return MonPillage;
            case AnimationType.DieBackward:
                return MonDie1;
            case AnimationType.DieForward:
                return MonDie2;
            case AnimationType.GetHit:
                return MonGetHit;
            case AnimationType.Block:
                return rand.Next(2) == 0 ? MonBlockRight : MonBlockLeft;
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
            case AnimationType.Bow:
                return MonFidget1;
            case AnimationType.FidgetYawn:
            case AnimationType.Salute:
            case AnimationType.Eat:
                return MonFidget2;
            default:
                return MonStand;
        }
    }
}
