namespace SphereNet.Core.Enums;

/// <summary>
/// Source-X character/equippable spell-timing property names. Faster Casting
/// affects CAST_TIME; Faster Cast Recovery exists on the script/status surface
/// but is intentionally behaviorless upstream.
/// </summary>
public static class SpellCastingProperties
{
    public const string FasterCasting = "FASTERCASTING";
    public const string FasterCastRecovery = "FASTERCASTRECOVERY";

    public static readonly string[] All = [FasterCasting, FasterCastRecovery];

    private static readonly HashSet<string> Names = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Names.Contains(name);
}
