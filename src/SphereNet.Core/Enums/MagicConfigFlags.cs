namespace SphereNet.Core.Enums;

/// <summary>MAGICFLAGS bits from sphere.ini — Source-X g_Cfg.m_iMagicFlags.</summary>
[Flags]
public enum MagicConfigFlags : int
{
    Precast              = 0x0001,
    NoInterrupt          = 0x0002,
    NoLos                = 0x0004,
    PolymorphRevertDeath = 0x0008,
    DungeonOutdoorSpells = 0x0010,
    DisableGate          = 0x0020,
    DisableRecall        = 0x0040,
    DisableMark          = 0x0080,
    LimitSummons         = 0x0100,
    GateBothSides        = 0x0200,
    NoRevealOnCast       = 0x0400,
    DispelKillSummons    = 0x0800,
}
