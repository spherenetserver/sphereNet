namespace SphereNet.Core.Enums;

/// <summary>
/// Source-X character/equippable spell COST and TIMING property names. Faster
/// Casting affects CAST_TIME; Faster Cast Recovery exists on the script/status
/// surface but is intentionally behaviorless upstream. Lower Mana Cost and Lower
/// Reagent Cost are what Calc_SpellManaCost and Calc_SpellReagentsConsume read
/// (CResourceCalc.cpp:545/569).
/// </summary>
public static class SpellCastingProperties
{
    public const string FasterCasting = "FASTERCASTING";
    public const string FasterCastRecovery = "FASTERCASTRECOVERY";

    /// <summary>Percent off a spell's mana cost. NEGATIVE raises it, which is how
    /// the reference's own Mind Rot writes a penalty (CResourceCalc.cpp:547).</summary>
    public const string LowerManaCost = "LOWERMANACOST";

    /// <summary>Percent CHANCE that a cast spends no reagents at all
    /// (CResourceCalc.cpp:570) - not a discount on how many.</summary>
    public const string LowerReagentCost = "LOWERREAGENTCOST";

    public static readonly string[] All =
        [FasterCasting, FasterCastRecovery, LowerManaCost, LowerReagentCost];

    private static readonly HashSet<string> Names = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Names.Contains(name);
}
