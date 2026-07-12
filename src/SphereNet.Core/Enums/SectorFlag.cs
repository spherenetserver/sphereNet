namespace SphereNet.Core.Enums;

/// <summary>
/// Per-sector behaviour flags (Source-X CSectorBase SECF_* defines). Controls
/// whether a sector is allowed to go to sleep when no client is nearby.
/// </summary>
[System.Flags]
public enum SectorFlag : uint
{
    None = 0,
    /// <summary>SECF_NoSleep — the sector never sleeps (always ticks).</summary>
    NoSleep = 0x00000001,
    /// <summary>SECF_InstaSleep — the sector sleeps immediately once its last
    /// client leaves, without waiting out the sleep-delay timeout.</summary>
    InstaSleep = 0x00000002,
}
