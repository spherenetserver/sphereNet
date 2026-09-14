namespace SphereNet.Core.Enums;

/// <summary>
/// REVEALFLAGS — which actions drop a character's concealment (Source-X
/// REVEALF_*, CServerConfig.h).
///
/// Read the names carefully: they do not all point the same way. Most say "reveal
/// when this happens", but <see cref="Snooping"/>, <see cref="Stealing"/> and
/// <see cref="OsiLikePersonalSpace"/> say the opposite - set them and the engine
/// does NOT reveal in that situation. The reference ini's own comments spell the
/// inversion out for exactly those three, and a flag set that assumed one direction
/// throughout would quietly invert half a shard's stealth rules.
///
/// A hidden character is still revealed by things no flag governs; these are the
/// cases the shard gets to choose.
/// </summary>
[System.Flags]
public enum RevealFlags
{
    None = 0,

    /// <summary>Detecting Hidden actually reveals. Clear, and the skill succeeds
    /// with no effect (CCharSkill.cpp:1716).</summary>
    DetectingHidden = 0x001,

    /// <summary>Reveal when looting one's own corpse.</summary>
    LootingSelf = 0x002,

    /// <summary>Reveal when looting somebody else's corpse.</summary>
    LootingOthers = 0x004,

    /// <summary>Reveal when speaking. A character's own OVERRIDE.NOREVEALSPEAK tag
    /// excuses it (CCharAct.cpp:3558).</summary>
    Speak = 0x008,

    /// <summary>Reveal when a spell starts casting.</summary>
    SpellCast = 0x010,

    /// <summary>INVERTED: do NOT reveal when somebody walks into this character's
    /// tile. Set, the shove also costs the mover no stamina.</summary>
    OsiLikePersonalSpace = 0x020,

    /// <summary>INVERTED: do NOT reveal when starting to snoop.</summary>
    Snooping = 0x040,

    /// <summary>INVERTED: do NOT reveal when starting to steal.</summary>
    Stealing = 0x080,

    /// <summary>Reveal after a theft SUCCEEDS.</summary>
    StealingSuccess = 0x100,

    /// <summary>Reveal after a theft FAILS.</summary>
    StealingFail = 0x200,

    /// <summary>Reveal a mounted character on its stealth step.</summary>
    OnHorse = 0x400,
}
