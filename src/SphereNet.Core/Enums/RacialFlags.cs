namespace SphereNet.Core.Enums;

/// <summary>Source-X RACIALFLAGS_TYPE values from CServerConfig.h.</summary>
[Flags]
public enum RacialFlags
{
    None = 0,
    HumanStrongBack = 0x0001,
    HumanTough = 0x0002,
    HumanWorkhorse = 0x0004,
    HumanJackOfTrades = 0x0008,
    ElfNightSight = 0x0010,
    ElfDifficultTracking = 0x0020,
    ElfWisdom = 0x0040,
    GargoyleFly = 0x0080,
    GargoyleBerserk = 0x0100,
    GargoyleDeadlyAim = 0x0200,
    GargoyleMysticInsight = 0x0400,
}
