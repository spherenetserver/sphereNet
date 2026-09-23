namespace SphereNet.Core.Enums;

/// <summary>
/// The action field of the 0xE2 new-animation packet - Source-X ANIM_TYPE_NEW
/// (uofiles_enums.h). The server never picks one of these per call site: it is
/// derived from the legacy <see cref="AnimationType"/> action, the actor's body and
/// the weapon in hand, exactly as Source-X CChar::UpdateAnimate does
/// (CCharAct.cpp:2257-2427). See BodyAnimTranslator.ToNewAnimation.
/// </summary>
public enum NewAnimationGesture : ushort
{
    Attack = 0x00,       // NANIM_ATTACK - sub-action is a NewAnimationAttack
    Block = 0x01,        // NANIM_BLOCK
    Block2 = 0x02,       // NANIM_BLOCK2 (monsters)
    Death = 0x03,        // NANIM_DEATH - variation 1 = die backwards
    GetHit = 0x04,       // NANIM_GETHIT
    Idle = 0x05,         // NANIM_IDLE
    Eat = 0x06,          // NANIM_EAT
    Emote = 0x07,        // NANIM_EMOTE - sub-action is a NewAnimationEmote
    Anger = 0x08,        // NANIM_ANGER
    TakeOff = 0x09,      // NANIM_TAKEOFF
    Landing = 0x0A,      // NANIM_LANDING
    Spell = 0x0B,        // NANIM_SPELL - sub-action is a NewAnimationSpell
    StartCombat = 0x0C,  // NANIM_UNKNOWN1
    EndCombat = 0x0D,    // NANIM_UNKNOWN2
    Pillage = 0x0E,      // NANIM_PILLAGE
    Rise = 0x0F,         // NANIM_RISE
}

/// <summary>Sub-actions of <see cref="NewAnimationGesture.Attack"/> - Source-X
/// NANIM_ATTACK_* (uofiles_enums.h).</summary>
public enum NewAnimationAttack : ushort
{
    Wrestling = 0x00,
    Bow = 0x01,
    Crossbow = 0x02,
    OneHandBash = 0x03,
    OneHandSlash = 0x04,
    OneHandPierce = 0x05,
    TwoHandBash = 0x06,
    TwoHandSlash = 0x07,
    TwoHandPierce = 0x08,
    Throwing = 0x09,
}

/// <summary>Sub-actions of <see cref="NewAnimationGesture.Spell"/> - Source-X
/// NANIM_SPELL_*.</summary>
public enum NewAnimationSpell : ushort
{
    Normal = 0x00,
    Summon = 0x01,
}

/// <summary>Sub-actions of <see cref="NewAnimationGesture.Emote"/> - Source-X
/// NANIM_EMOTE_*.</summary>
public enum NewAnimationEmote : ushort
{
    Bow = 0x00,
    Salute = 0x01,
}
