namespace SphereNet.Core.Enums;

/// <summary>
/// The AOS suit property names a character or a piece of equipment carries
/// (Source-X CCPropsChar / CCPropsItemEquippable / CCPropsItemWeapon, registered
/// through ADDPROP so the reference answers every one of them on a bare read).
///
/// Four of these the combat engine ALREADY aggregates out of the same tags -
/// INCREASEHITCHANCE and INCREASEDEFCHANCE gate the AOS hit roll, INCREASEDAM the
/// damage bonus, REFLECTPHYSICALDAM the reflected share - while no script could
/// read or write any of them: the value the engine acted on was invisible from
/// the outside, and a line setting it did nothing at all.
///
/// Tag-backed and persisted, like <see cref="AosOnHitProperties"/>, and summed
/// across the suit by CombatEngine.GetEquipmentPropertyValue. Unset reads 0, which
/// is what the reference's own default answers.
/// </summary>
public static class AosEquipProperties
{
    public static readonly string[] All =
    [
        // CCPropsChar + CCPropsItemEquippable
        "ENHANCEPOTIONS", "HITLOWERATK", "HITLOWERDEF", 
        "INCREASEDAM",
        "INCREASEDEFCHANCE", "INCREASEGOLD", "INCREASEHITCHANCE",
        "INCREASESPELLDAM", "REFLECTPHYSICALDAM", "SPELLCHANNELING",
        // CCPropsItemEquippable only
        "MAGEARMOR",
        // CCPropsItemWeapon only
        "BALANCED", "MAGEWEAPON",
    ];

    private static readonly HashSet<string> _names = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => _names.Contains(name);
}
