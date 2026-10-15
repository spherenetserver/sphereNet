using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Definitions;

/// <summary>A resource list written for people - what an item's RESMAKE reads and what
/// a skill menu's &lt;resmake&gt; shows.</summary>
public static class ResourceText
{
    /// <summary>Source-X CResourceQtyArray::WriteNames (CResourceQty.cpp:253): each entry
    /// as "qty name" (a skill's fixed-point qty as "50.0"), separated by ", "; an item
    /// by its name, plural above one (CResourceQty::WriteNameSingle). Entries are read
    /// as CResourceQty::Load reads them: "qty name" or "name qty".</summary>
    public static string Names(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var resources = DefinitionLoader.StaticResources;
        var parts = new List<string>();
        foreach (string entry in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = entry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string name = tokens.FirstOrDefault(t => char.IsLetter(t[0]) || t[0] == '_') ?? "";
            if (name.Length == 0)
                continue;
            string qtyToken = tokens.FirstOrDefault(t => !ReferenceEquals(t, name)) ?? "1";
            var rid = resources?.ResolveDefName(name) ?? default;

            if (rid.IsValid && rid.Type == ResType.SkillDef)
            {
                parts.Add($"{qtyToken} {name}");
                continue;
            }
            long qty = ScriptNumber.TryParseToken(qtyToken, out long q) ? q : 1;
            string display = name;
            if (rid.IsValid && rid.Type == ResType.ItemDef &&
                DefinitionLoader.GetItemDef(rid.Index) is { } def && !string.IsNullOrEmpty(def.Name))
                display = ItemDef.Pluralize(def.Name, qty > 1);
            parts.Add(qty != 0 ? $"{qty} {display}" : display);
        }
        return string.Join(", ", parts);
    }
}
