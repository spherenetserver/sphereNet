using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;

namespace SphereNet.Game.Magic;

/// <summary>
/// Reading a spell out of a script value.
///
/// Upstream gets this for free: a spell argument goes through the expression engine,
/// where a defname resolves to its resource index (CItem.cpp:3244,
/// ResGetIndex(s.GetArgVal())). There is no such single door here, so the three
/// spellings a pack writes - a number, an enum name, and a SPELLDEF defname - are read
/// in one place that both a character and an item can reach.
/// </summary>
public static class SpellNames
{
    /// <summary>Resolve a spell written as "23", "paralyze", "s_paralyze" or
    /// "spell_paralyze". False when it names no spell.</summary>
    public static bool TryResolve(string value, out SpellType spell)
{
    spell = SpellType.None;
    string normalized = value.Trim();

    // SpellType is backed by ushort; Enum.IsDefined throws ArgumentException
    // unless the boxed value is the exact underlying type, so range-check
    // and cast to ushort before probing.
    if (int.TryParse(normalized, out int numeric) &&
        numeric >= 0 && numeric <= ushort.MaxValue &&
        Enum.IsDefined(typeof(SpellType), (ushort)numeric))
    {
        spell = (SpellType)numeric;
        return true;
    }

    if (normalized.StartsWith("spell_", StringComparison.OrdinalIgnoreCase))
        normalized = normalized[6..];
    else if (normalized.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
        normalized = normalized[2..];

    if (Enum.TryParse<SpellType>(normalized, true, out var named))
    {
        spell = named;
        return true;
    }

    var resources = DefinitionLoader.StaticResources;
    var rid = resources?.ResolveDefName(value.Trim()) ?? ResourceId.Invalid;
    if (rid.IsValid && rid.Type == ResType.SpellDef &&
        rid.Index >= 0 && rid.Index <= ushort.MaxValue &&
        Enum.IsDefined(typeof(SpellType), (ushort)rid.Index))
    {
        spell = (SpellType)rid.Index;
        return true;
    }

    return false;
    }
}
