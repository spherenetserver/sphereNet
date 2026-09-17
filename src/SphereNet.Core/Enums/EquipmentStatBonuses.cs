namespace SphereNet.Core.Enums;

/// <summary>
/// The stat and pool bonuses a piece of equipment grants its wearer
/// (Source-X CCPropsItemEquippable: BONUSSTR, BONUSDEX, BONUSINT and the three
/// max-pool bonuses).
///
/// CombatEngine ALREADY sums every one of these off the worn items on each read -
/// EffectiveStr/Dex/Int and EffectiveMaxHits/Mana/Stam - so the aggregation existed
/// and worked; what did not exist was any way for a script to set one. An
/// ITEMDEF or a trigger writing BonusStr=5 had its line consumed and discarded,
/// which meant the only magic gear a shard could have was gear whose bonus was
/// hardcoded somewhere else.
///
/// Tag-backed like <see cref="AosEquipProperties"/>, and read by the same
/// GetItemNumProperty path, so an instance tag and an ITEMDEF tag both land where
/// the suit aggregation already looks.
/// </summary>
public static class EquipmentStatBonuses
{
    public static readonly string[] All =
    [
        "BONUSSTR", "BONUSDEX", "BONUSINT",
        "BONUSHITSMAX", "BONUSMANAMAX", "BONUSSTAMMAX",
    ];

    private static readonly HashSet<string> _names = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => _names.Contains(name);
}
