namespace SphereNet.Core.Enums;

/// <summary>
/// Body-agnostic gesture categories for the 0xE2 new-animation packet
/// (High Seas+ clients). Unlike <see cref="AnimationType"/>, whose values are
/// raw human-body action indices for the legacy 0x6E packet, these high-level
/// gestures let the client resolve the correct animation group for whatever
/// body the mobile uses (human, monster, gargoyle), so the same gesture plays
/// a sensible animation across all body types.
/// </summary>
public enum NewAnimationGesture : ushort
{
    Attack = 0,
    Parry = 1,
    Block = 2,
    Die = 3,
    Impact = 4,
    Fidget = 5,
    Eat = 6,
    Emote = 7,
    Alert = 8,
    TakeOff = 9,
    Land = 10,
    Spell = 11,
    StartCombat = 12,
    EndCombat = 13,
    Pillage = 14,
    Spawn = 15
}
