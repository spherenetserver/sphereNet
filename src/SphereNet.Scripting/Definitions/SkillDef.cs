using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Skill definition. Maps to CSkillDef in Source-X.
/// Loaded from [SKILL] sections. Numeric fields use the legacy script
/// number convention (see <see cref="ValueCurve.ParseSphereNumber"/>):
/// decimal points concatenate digits ("2.5" → 25) and a leading zero marks
/// hex — plain int parsing silently dropped every decimal/multi-value
/// property in real script packs.
/// </summary>
public sealed class SkillDef : ResourceLink
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>ADV_RATE curve: skill uses per 0.1 gain across 0-100.0 (fixed-point ×10).</summary>
    public ValueCurve AdvRate { get; set; } = ValueCurve.Empty;
    /// <summary>DELAY curve in tenths of a second across 0-100.0.</summary>
    public ValueCurve Delay { get; set; } = ValueCurve.Empty;
    /// <summary>EFFECT curve (skill-dependent magnitude).</summary>
    public ValueCurve Effect { get; set; } = ValueCurve.Empty;
    public int StatStr { get; set; }
    public int StatDex { get; set; }
    public int StatInt { get; set; }
    public int GainRadius { get; set; }
    public int Flags { get; set; }
    public int BonusStr { get; set; }
    public int BonusDex { get; set; }
    public int BonusInt { get; set; }
    public int BonusStats { get; set; }
    public int Group { get; set; }
    public string PromptMsg { get; set; } = "";
    /// <summary>RANGE: max use distance, primarily for SKF_GATHER skills (Source-X m_Range).</summary>
    public int Range { get; set; }
    /// <summary>PROMPT_CLILOC: cliloc id shown on the skill's target cursor (Source-X m_sTargetPromptCliloc).</summary>
    public string PromptCliloc { get; set; } = "";
    public int Values { get; set; }

    public SkillDef(ResourceId id) : base(id) { }

    /// <summary>Parse a FLAGS value that may be a pipe-delimited mix of symbolic
    /// skf_* names and numeric/hex tokens (e.g. "skf_gather|skf_ranged" or
    /// "0400|0200" or a bare number). Maps to Source-X SKF_* bit values.</summary>
    private static int ParseFlags(string value)
    {
        int flags = 0;
        foreach (var token in value.Split('|', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
            flags |= TryParseSkillFlagName(token, out int bit) ? bit : ValueCurve.ParseSphereNumber(token);
        return flags;
    }

    private static bool TryParseSkillFlagName(string token, out int bit)
    {
        bit = token.ToLowerInvariant() switch
        {
            "skf_scripted"   => 0x0001,
            "skf_fight"      => 0x0002,
            "skf_magic"      => 0x0004,
            "skf_craft"      => 0x0008,
            "skf_immobile"   => 0x0010,
            "skf_selectable" => 0x0020,
            "skf_nomindist"  => 0x0040,
            "skf_noanim"     => 0x0080,
            "skf_nosfx"      => 0x0100,
            "skf_ranged"     => 0x0200,
            "skf_gather"     => 0x0400,
            "skf_disabled"   => 0x0800,
            _ => -1,
        };
        return bit >= 0;
    }

    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "TITLE": Title = value; break;
            case "DEFNAME":
            case "KEY": DefName = value; break;
            case "ADV_RATE": AdvRate = ValueCurve.Parse(value); break;
            case "DELAY": Delay = ValueCurve.Parse(value); break;
            case "EFFECT": Effect = ValueCurve.Parse(value); break;
            case "STAT_STR": StatStr = ValueCurve.ParseSphereNumber(value); break;
            case "STAT_DEX": StatDex = ValueCurve.ParseSphereNumber(value); break;
            case "STAT_INT": StatInt = ValueCurve.ParseSphereNumber(value); break;
            case "GAINRADIUS": GainRadius = ValueCurve.ParseSphereNumber(value); break;
            case "FLAGS": Flags = ParseFlags(value); break;
            case "BONUS_STR": BonusStr = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_DEX": BonusDex = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_INT": BonusInt = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_STATS": BonusStats = ValueCurve.ParseSphereNumber(value); break;
            case "GROUP": Group = ValueCurve.ParseSphereNumber(value); break;
            case "PROMPT_MSG": PromptMsg = value; break;
            case "PROMPT_CLILOC": PromptCliloc = value; break;
            case "RANGE": Range = ValueCurve.ParseSphereNumber(value); break;
            case "VALUES": Values = ValueCurve.ParseSphereNumber(value); break;
        }
    }
}
