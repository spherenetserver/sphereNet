namespace SphereNet.Core.Enums;

/// <summary>
/// Object attribute flags. Maps to ATTR_* in Source-X.
/// </summary>
[Flags]
public enum ObjAttributes : ulong
{
    None = 0,
    Identified = 0x0001,
    Decay = 0x0002,
    Newbie = 0x0004,
    Move_Always = 0x0008,
    Move_Never = 0x0010,
    Magic = 0x0020,
    Owned = 0x0040,
    Invis = 0x0080,
    Cursed = 0x0100,
    Cursed2 = 0x0200,
    Blessed = 0x0400,
    Blessed2 = 0x0800,
    ForSale = 0x1000,
    Stolen = 0x2000,
    CanDecay = 0x4000,
    Static = 0x8000,
    Exceptional = 0x10000,
    Enchanted = 0x20000,
    // Bits 0x40000 and up follow Source-X CItem.h:135-148, which is also what every
    // script pack's attr_* defs and every classic save use. LockedDown and Secure
    // used to sit on 0x100000 / 0x40000 - Source-X's ATTR_INSURED / ATTR_IMBUED - so a
    // classic save's insured item loaded as locked down, a pack's attr_lockeddown
    // test never matched, and a locked-down item's tooltip would have read "Insured".
    Imbued = 0x40000,
    QuestItem = 0x80000,
    Insured = 0x100000,
    NoDrop = 0x200000,
    NoTrade = 0x400000,
    /// <summary>Older spelling of <see cref="NoDrop"/>, kept for existing callers.</summary>
    Nodropt = NoDrop,
    /// <summary>Older spelling of <see cref="NoTrade"/>, kept for existing callers.</summary>
    NotRading = NoTrade,
    Artifact = 0x800000,
    LockedDown = 0x1000000,
    Secure = 0x2000000,
    Reforged = 0x4000000,
    Opened = 0x8000000,
    ShardBound = 0x10000000,
    AccountBound = 0x20000000,
    CharacterBound = 0x40000000,
    CanUseParalyzed = 0x80000000,
    /// <summary>ATTR_CANNOTREPAIR (CItem.h:151): no repair, no fortify. Source-X's
    /// attribute word is 64-bit (uint64 m_Attr, CItem.h:106).</summary>
    CannotRepair = 0x400000000000,
}
