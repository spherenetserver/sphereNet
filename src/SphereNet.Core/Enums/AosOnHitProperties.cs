namespace SphereNet.Core.Enums;

/// <summary>
/// The AOS on-hit combat property names SphereNet wires (the working subset of
/// Source-X CCPropsChar / CCPropsItemEquippable): life/mana/stam leech, mana
/// drain, the hit-area splashes and the on-hit spell procs. Shared between the
/// runtime property surface (Character/Item, tag-backed) and the def parsers
/// (CHARDEF/ITEMDEF lines land as def-tags). HitLowerAtk/Def, HitCurse and
/// HitFatigue are unimplemented in Source-X too and stay deferred.
/// </summary>
public static class AosOnHitProperties
{
    public static readonly string[] All =
    [
        "HITLEECHLIFE", "HITLEECHMANA", "HITLEECHSTAM", "HITMANADRAIN",
        "HITAREAPHYSICAL", "HITAREAFIRE", "HITAREACOLD", "HITAREAPOISON", "HITAREAENERGY",
        "HITDISPEL", "HITFIREBALL", "HITHARM", "HITLIGHTNING", "HITMAGICARROW",
    ];

    private static readonly HashSet<string> _names = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => _names.Contains(name);
}
