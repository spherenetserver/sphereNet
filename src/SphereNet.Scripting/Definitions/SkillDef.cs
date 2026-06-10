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
    public int Values { get; set; }

    public SkillDef(ResourceId id) : base(id) { }

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
            case "FLAGS": Flags = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_STR": BonusStr = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_DEX": BonusDex = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_INT": BonusInt = ValueCurve.ParseSphereNumber(value); break;
            case "BONUS_STATS": BonusStats = ValueCurve.ParseSphereNumber(value); break;
            case "GROUP": Group = ValueCurve.ParseSphereNumber(value); break;
            case "PROMPT_MSG": PromptMsg = value; break;
            case "VALUES": Values = ValueCurve.ParseSphereNumber(value); break;
        }
    }
}
