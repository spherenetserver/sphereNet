namespace SphereNet.Core.Enums;

/// <summary>
/// Region flags. Numeric values match Source-X REGION_FLAG_* / REGION_ANTIMAGIC_*
/// in CRegion.h so a legacy numeric FLAGS=&lt;hex&gt; from a Source-X / mortechUO
/// script maps to the correct behaviour (e.g. FLAGS=04000 = Guarded, not Safe).
/// SphereNet-specific flags with no Source-X equivalent live in the high bits so
/// they never collide with a real REGION_FLAG_* value.
/// </summary>
[Flags]
public enum RegionFlag : uint
{
    None = 0,

    // Source-X anti-magic sub-flags (REGION_ANTIMAGIC_*)
    NoMagic       = 0x00000001, // REGION_ANTIMAGIC_ALL — all magic banned
    Recall        = 0x00000002, // REGION_ANTIMAGIC_RECALL_IN — recall/mark/gate INTO blocked
    RecallOut     = 0x00000004, // REGION_ANTIMAGIC_RECALL_OUT — can't recall out
    Gate          = 0x00000008, // REGION_ANTIMAGIC_GATE — gate blocked
    NoTeleport    = 0x00000010, // REGION_ANTIMAGIC_TELEPORT — can't teleport in
    NoMagicDamage = 0x00000020, // REGION_ANTIMAGIC_DAMAGE — no harmful magic

    // Source-X region flags (REGION_FLAG_*)
    Ship          = 0x00000040, // REGION_FLAG_SHIP
    NoBuild       = 0x00000080, // REGION_FLAG_NOBUILDING
    House         = 0x00000100, // REGION_FLAG_HOUSE
    Announce      = 0x00000200, // REGION_FLAG_ANNOUNCE
    InstaLogout   = 0x00000400, // REGION_FLAG_INSTA_LOGOUT
    Underground   = 0x00000800, // REGION_FLAG_UNDERGROUND — dungeon, no weather
    NoDecay       = 0x00001000, // REGION_FLAG_NODECAY — ground items don't decay
    Safe          = 0x00002000, // REGION_FLAG_SAFE — safe from all harm
    Guarded       = 0x00004000, // REGION_FLAG_GUARDED
    NoPvP         = 0x00008000, // REGION_FLAG_NO_PVP
    Arena         = 0x00010000, // REGION_FLAG_ARENA — no murder counts or crimes
    NoMining      = 0x00020000, // REGION_FLAG_NOMINING
    WalkNoBlockHeight   = 0x00040000, // REGION_FLAG_WALK_NOBLOCKHEIGHT
    InheritParentEvents = 0x00100000, // REGION_FLAG_INHERIT_PARENT_EVENTS
    InheritParentFlags  = 0x00200000, // REGION_FLAG_INHERIT_PARENT_FLAGS
    InheritParentTags   = 0x00400000, // REGION_FLAG_INHERIT_PARENT_TAGS

    // SphereNet-specific flags (no Source-X numeric equivalent) — high bits only.
    Mark          = 0x01000000, // block marking (Source-X folds this into RECALL_IN)
    SafeZone      = 0x02000000, // legacy SAFEZONE alias
    NoPeraCrime   = 0x04000000, // no perma-crime
    Jail          = 0x08000000, // jail region
    RedZone       = 0x10000000, // murders allowed, no notoriety penalties
    GuardedOff    = 0x20000000, // explicit "not guarded" override of a parent region
}
