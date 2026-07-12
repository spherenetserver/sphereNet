namespace SphereNet.Core.Enums;

/// <summary>
/// Line-of-sight modifiers (Source-X CChar::CanSeeLOS_New flags). SphereNet's
/// default LOS already lets windows through (LOS_NB_WINDOWS) and blocks on
/// terrain/static/dynamic/multi, so only the behaviour-changing flags are
/// modelled here.
/// </summary>
[System.Flags]
public enum LosFlags
{
    None = 0,
    /// <summary>LOS_FISHING — the ray must run over water once it is two or more
    /// tiles from the caster; any non-water terrain along it blocks (you cannot
    /// fish across land).</summary>
    Fishing = 0x0001,
}
