namespace SphereNet.Core.Enums;

/// <summary>MAGICFLAGS bits from sphere.ini — Source-X g_Cfg.m_iMagicFlags.</summary>
[Flags]
public enum MagicConfigFlags : int
{
    NoDirectionChange    = 0x0000001,
    Precast              = 0x0000002,
    IgnoreArmor          = 0x0000004,
    CanHarmSelf          = 0x0000008,
    StackStats           = 0x0000010,
    FreezeOnCast         = 0x0000020,
    SummonWalkCheck      = 0x0000040,
    NoFieldsOverWalls    = 0x0000080,
    NoAnimation          = 0x0000100,
    OsiFormulas          = 0x0000200,
    NoCastFrozenHands    = 0x0000400,
    PolymorphStats       = 0x0000800,
    OverrideFields       = 0x0001000,
    CastParalyzed        = 0x0002000,
    NoReflectOwn         = 0x0004000,
    DeleteReflectOwn     = 0x0008000,

    // SphereNet extensions. Kept outside Source-X's 0x0000001-0x0008000
    // range so raw sphere.ini MAGICFLAGS retain their upstream meaning.
    NoInterrupt          = 0x0010000,
    NoLos                = 0x0020000,
    PolymorphRevertDeath = 0x0040000,
    DungeonOutdoorSpells = 0x0080000,
    DisableGate          = 0x0100000,
    DisableRecall        = 0x0200000,
    DisableMark          = 0x0400000,
    LimitSummons         = 0x0800000,
    GateBothSides        = 0x1000000,
    NoRevealOnCast       = 0x2000000,
    DispelKillSummons    = 0x4000000,
}
