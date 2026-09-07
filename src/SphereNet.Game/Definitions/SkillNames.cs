using SphereNet.Core.Enums;

namespace SphereNet.Game.Definitions;

/// <summary>
/// What a skill is called — in one place.
///
/// A skill has three kinds of name: the engine's own (the <see cref="SkillType"/>
/// member), the spellings the classic client and old saves use for it
/// (ANIMALLORE, RESISTINGSPELLS, EVALUATINGINTELLIGENCE …), and the one THIS SHARD
/// gave the slot. That last one is the KEY of the pack's <c>[SKILL n]</c> block, and
/// upstream looks skills up by it (CSkillDef, "KEY=") — 56T calls skill 54
/// "Sailormanship" rather than Spellweaving, and its characters are saved with that
/// name.
///
/// Two separate tables used to answer this question - one for scripts, one for the
/// world loader - and neither knew the pack's names, so a character loaded from that
/// shard lost every renamed skill into a SAVE.* tag and a script asking for
/// <c>&lt;Sailormanship&gt;</c> got nothing.
/// </summary>
public static class SkillNames
{
    /// <summary>Resolve a skill name to its slot. Tries the engine's own names and the
    /// classic spellings first, then the names the loaded pack declared.</summary>
    public static bool TryResolve(string? name, out SkillType skill)
    {
        skill = SkillType.None;
        string key = (name ?? "").Trim();
        if (key.Length == 0)
            return false;

        if (_builtIn.TryGetValue(key, out skill))
            return true;

        // The pack's own name for the slot. The index IS the skill number: a
        // [SKILL 54] block names slot 54 whatever its KEY says.
        DefinitionLoader.TryGetSkillIndexByName(key, out int index);
        if (index >= 0 && index < (int)SkillType.Qty)
        {
            skill = (SkillType)index;
            return true;
        }

        skill = SkillType.None;
        return false;
    }

    private static readonly Dictionary<string, SkillType> _builtIn = Build();

    private static Dictionary<string, SkillType> Build()
    {
        var map = new Dictionary<string, SkillType>(StringComparer.OrdinalIgnoreCase);
        foreach (SkillType st in Enum.GetValues<SkillType>())
        {
            if (st is SkillType.None or SkillType.Qty) continue;
            map[st.ToString()] = st;
        }

        // The spellings the classic client, the old saves and the reference use.
        map["ANIMALLORE"] = SkillType.AnimalLore;
        map["ARMSLORE"] = SkillType.ArmsLore;
        map["ARMSLOREBOWCRAFT"] = SkillType.Bowcraft;
        map["DETECTINGHIDDEN"] = SkillType.DetectingHidden;
        map["DETECTHIDDEN"] = SkillType.DetectingHidden;
        map["EVALINT"] = SkillType.EvalInt;
        map["EVALUATINGINTEL"] = SkillType.EvalInt;
        map["EVALUATINGINTELLECT"] = SkillType.EvalInt;
        map["EVALUATINGINTELLIGENCE"] = SkillType.EvalInt;
        map["EVALUATEINTEL"] = SkillType.EvalInt;
        map["ITEMID"] = SkillType.ItemId;
        map["ITEMIDENTIFICATION"] = SkillType.ItemId;
        map["MACEFIGHTING"] = SkillType.MaceFighting;
        map["MAGICRESISTANCE"] = SkillType.MagicResistance;
        map["RESISTINGSPELLS"] = SkillType.MagicResistance;
        map["REMOVETRAP"] = SkillType.RemoveTrap;
        map["SPIRITSPEAK"] = SkillType.SpiritSpeak;
        map["TASTEID"] = SkillType.TasteId;
        map["TASTEIDENTIFICATION"] = SkillType.TasteId;
        map["DISCORDANCE"] = SkillType.Enticement;   // post-UOR name for skill 15
        return map;
    }
}
