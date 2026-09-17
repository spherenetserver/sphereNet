namespace SphereNet.Core.Enums;

/// <summary>
/// The component-property names an object carries that nothing else in this engine
/// already answers for.
///
/// Upstream keeps these on entity components - CCPropsItemEquippable, CCPropsItemWeapon,
/// CCPropsChar and the rest, declared in src/tables/CCProps*_props.tbl - which makes every
/// one of them settable and readable on an INSTANCE, not only on a definition. Here the
/// definition side already worked, because the ITEMDEF parser puts a key it does not
/// recognise into the definition's tags and the combat aggregations read those. The
/// instance side did not: TrySetProperty has no such catch-all and refused the write
/// outright.
///
/// That is exactly what an item's @Create block does, and the shipped packs do it about
/// 1,100 times for the five resists alone - "RESPHYSICAL=2" on a piece of armour, dropped
/// on the floor, leaving the armour with no resists at all.
///
/// The lists hold ONLY the names this engine did not already handle: the rest of those
/// tables are real typed fields here (a character's base resists, LUCK, the resist caps)
/// or belong to another tag-backed family, and putting them here would shadow the code
/// that owns them. RANGE, RANGEH and RANGEL are absent for the same reason - they answer
/// from the definition.
///
/// Tag-backed and persisted, the same way <see cref="AosEquipProperties"/> is. An unset
/// one reads 0, which is the default upstream answers.
/// </summary>
public static class ComponentProperties
{
    /// <summary>Names an ITEM carries (the equippable, weapon, ranged-weapon, item and
    /// item-char tables together).</summary>
    public static readonly string[] Item =
    [
        "ALTERED", "AMMOANIM", "AMMOANIMHUE", "AMMOANIMRENDER", "AMMOCONT", "AMMOSOUNDHIT",
        "AMMOSOUNDMISS", "AMMOTYPE", "ANTIQUE", "ASSASSINHONED", "BANE", "BATTLELUST",
        "BLOODDRINKER", "BONEBREAKER", "BONUSBERSERK", "BONUSDURABILITY", "BRITTLE",
        "CASTINGFOCUS", "COMBATBONUSPERCENT", "COMBATBONUSSTAT", "DAMCHAOS", "DAMCOLD",
        "DAMDIRECT", "DAMENERGY", "DAMFIRE", "DAMPHYSICAL", "DAMPOISON", "EATERCOLD",
        "EATERDAM", "EATERENERGY", "EATERFIRE", "EATERKINETIC", "EATERPOISON", "EPHEMERAL",
        "HITCURSE", "HITFATIGUE", "HITSPARKS", "HITSPELL", "HITSPELLSTR", "HITSWARM",
        "IMBUED", "INCREASEDEFCHANCEMAX", "INCREASEKARMALOSS", "LAVAINFUSED", "LOWERAMMOCOST",
        "LOWERREQ", "LUCK", "MASSIVE", "MYSTICWEAPON", "NIGHTSIGHT", "PRIZED", "RAGEFOCUS",
        "REACTIVEPARALYZE", "REGENFOOD", "REGENHITS", "REGENMANA", "REGENSTAM", "REGENVALFOOD",
        "REGENVALHITS", "REGENVALMANA", "REGENVALSTAM", "RESCOLD", "RESCOLDMAX", "RESENERGY",
        "RESENERGYMAX", "RESFIRE", "RESFIREMAX", "RESONANCECOLD", "RESONANCEENERGY",
        "RESONANCEFIRE", "RESONANCEKINETIC", "RESONANCEPOISON", "RESPHYSICAL", "RESPHYSICALMAX",
        "RESPOISON", "RESPOISONMAX", "SEARING", "SHIPWRECKITEM", "SOULCHARGE", "SOULCHARGECOLD",
        "SOULCHARGEENERGY", "SOULCHARGEFIRE", "SOULCHARGEKINETIC", "SOULCHARGEPOISON",
        "SPELLCONSUMPTION", "SPELLFOCUSING", "SPLINTERING", "UNLUCKY", "UNWIELDLY",
        "USEBESTWEAPONSKILL", "WEAPONSOUNDHIT", "WEAPONSOUNDMISS", "WEIGHTREDUCTION",
    ];

    /// <summary>Names a CHARACTER carries (CCPropsChar).</summary>
    public static readonly string[] Char =
    [
        "COMBATBONUSPERCENT", "COMBATBONUSSTAT", "DAMCHAOS", "DAMDIRECT", "DECREASEHITCHANCE",
        "EATERCOLD", "EATERDAM", "EATERENERGY", "EATERFIRE", "EATERKINETIC", "EATERPOISON",
        "HITCURSE", "HITFATIGUE", "HITSPARKS", "HITSPELL", "HITSPELLSTR", "HITSWARM",
        "INCREASEDEFCHANCEMAX", "INCREASEKARMALOSS", "LOWERAMMOCOST", "RAGEFOCUS", "REACTIVEPARALYZE",
        "RESONANCECOLD", "RESONANCEENERGY", "RESONANCEFIRE", "RESONANCEKINETIC", "RESONANCEPOISON",
        "SOULCHARGE", "SOULCHARGECOLD", "SOULCHARGEENERGY", "SOULCHARGEFIRE", "SOULCHARGEKINETIC",
        "SOULCHARGEPOISON", "SPELLCONSUMPTION", "SPELLFOCUSING",
    ];

    private static readonly HashSet<string> _item = new(Item, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _char = new(Char, StringComparer.OrdinalIgnoreCase);

    public static bool IsItemProperty(string name) => _item.Contains(name);
    public static bool IsCharProperty(string name) => _char.Contains(name);
}
