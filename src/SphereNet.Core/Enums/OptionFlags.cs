namespace SphereNet.Core.Enums;

/// <summary>
/// Source-X <c>OF_TYPE</c> values from sphere.ini OPTIONFLAGS.
/// Keep these values separate from client feature/character-list packet flags.
/// </summary>
[Flags]
public enum OptionFlags : uint
{
    None = 0,
    NoDClickTarget = 0x0000001,
    NoSmoothSailing = 0x0000002,
    ScaleDamageByDurability = 0x0000004,
    CommandSysMessages = 0x0000008,
    PetSlots = 0x0000010,
    OsiMultiSight = 0x0000020,
    ItemsAutoName = 0x0000040,
    FileCommands = 0x0000080,
    NoItemNaming = 0x0000100,
    NoHouseMuteSpeech = 0x0000200,
    NoContextMenuLos = 0x0000400,
    MapBoundarySailing = 0x0000800,
    FloodProtection = 0x0001000,
    Buffs = 0x0002000,
    NoPrefix = 0x0004000,
    DyeType = 0x0008000,
    DrinkIsFood = 0x0010000,
    NoDClickTurn = 0x0020000,
    NoPaperdollTradeTitle = 0x0040000,
    NoTargetTurn = 0x0080000,
    StatAllowValueOverMax = 0x0100000,
    GuardOutsideGuardedArea = 0x0200000,
    OverweightNoDropCarriedItem = 0x0400000,
    AllowContainerInsideContainer = 0x0800000,
    VendorStockLimit = 0x1000000,
    EnableGuildAlignNotoriety = 0x2000000,
    NoDClickEquip = 0x4000000,
    PetBehaviorOwnerNeutral = 0x8000000,
    NpcMovementOldStyle = 0x10000000,
}
