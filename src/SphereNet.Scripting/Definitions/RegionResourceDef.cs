using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// REGIONRESOURCE definition. Maps to CRegionResourceDef in Source-X.
/// Defines a harvestable resource type (ore, logs, fish, etc.) with skill requirements and yield.
/// </summary>
public sealed class RegionResourceDef : ResourceLink
{
    /// <summary>Spawn amount range (how many resource units exist in a region).</summary>
    public int AmountMin { get; set; }
    public int AmountMax { get; set; }

    /// <summary>The BASEID of the item produced when gathered.</summary>
    public ushort Reap { get; set; }

    /// <summary>Raw REAP value from script (defname like "i_ore_iron"). Resolved post-load.</summary>
    public string? ReapRaw { get; set; }

    /// <summary>Amount of items yielded per successful gather.</summary>
    public int ReapAmountMin { get; set; } = 1;
    public int ReapAmountMax { get; set; } = 1;

    /// <summary>Regeneration time in seconds.</summary>
    public int Regen { get; set; }

    /// <summary>Skill difficulty range (in tenths: 0-1000).</summary>
    public int SkillMin { get; set; }
    public int SkillMax { get; set; }
    private readonly List<int> _skillCurve = [];
    /// <summary>Source-X CValueCurveDef points, preserved in script order.</summary>
    public IReadOnlyList<int> SkillCurve => _skillCurve;

    public RegionResourceDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string arg)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "AMOUNT":
                ParseRange(arg, out int amin, out int amax);
                AmountMin = amin;
                AmountMax = amax;
                break;
            case "REAP":
                Reap = ParseHexOrDec(arg);
                if (Reap == 0)
                    ReapRaw = arg.Trim();
                break;
            case "REAPAMOUNT":
                ParseRange(arg, out int rmin, out int rmax);
                ReapAmountMin = rmin;
                ReapAmountMax = rmax;
                break;
            case "REGEN":
                Regen = EvalSimpleExpression(arg);
                break;
            case "SKILL":
                ParseFloatCurve(arg, _skillCurve);
                SkillMin = _skillCurve.Count > 0 ? _skillCurve[0] : 0;
                SkillMax = _skillCurve.Count > 0 ? _skillCurve[^1] : SkillMin;
                break;
            case "DEFNAME":
                base.DefName = arg.Trim();
                break;
        }
    }

    /// <summary>Source-X m_vcSkill.GetRandom()/10. A random 0..999 sample is
    /// evaluated across the resource's full value curve.</summary>
    public int GetRandomSkillDifficulty(Random random) =>
        GetSkillDifficultyAt(random.Next(1000));

    internal int GetSkillDifficultyAt(int samplePermille)
    {
        int sample = Math.Clamp(samplePermille, 0, 999);
        IReadOnlyList<int> points = _skillCurve.Count > 0
            ? _skillCurve
            : SkillMin == SkillMax ? [SkillMin] : [SkillMin, SkillMax];
        if (points.Count == 0) return 0;
        if (points.Count == 1) return Math.Max(0, points[0] / 10);

        int lowIndex;
        int segmentSize;
        int segmentSample = sample;
        if (points.Count == 2)
        {
            lowIndex = 0;
            segmentSize = 1000;
        }
        else if (points.Count == 3)
        {
            lowIndex = sample >= 500 ? 1 : 0;
            if (lowIndex == 1) segmentSample -= 500;
            segmentSize = 500;
        }
        else
        {
            lowIndex = sample * points.Count / 1000;
            int lastSegment = points.Count - 2;
            if (lowIndex > lastSegment) lowIndex = lastSegment;
            segmentSize = 1000 / (points.Count - 1);
            segmentSample -= lowIndex * segmentSize;
        }

        long low = points[lowIndex];
        long high = points[lowIndex + 1];
        long value = low + (high - low) * segmentSample / segmentSize;
        return (int)Math.Max(0L, value / 10L);
    }

    private static void ParseRange(string val, out int min, out int max)
    {
        min = 0; max = 0;
        var parts = val.Split(',');
        if (parts.Length >= 1) int.TryParse(parts[0].Trim(), out min);
        if (parts.Length >= 2) int.TryParse(parts[1].Trim(), out max);
        else max = min;
    }

    private static void ParseFloatCurve(string val, List<int> destination)
    {
        destination.Clear();
        foreach (string raw in val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                destination.Add((int)Math.Round(parsed * 10));
            else if (int.TryParse(raw, out int integer))
                destination.Add(integer);
        }
    }

    /// <summary>Evaluate simple arithmetic expressions like "60*60*10" → 36000.</summary>
    private static int EvalSimpleExpression(string val)
    {
        val = val.Trim();
        if (val.Contains('*'))
        {
            long result = 1;
            foreach (var part in val.Split('*'))
            {
                if (long.TryParse(part.Trim(), out long v))
                    result *= v;
                else
                    return 0;
            }
            return (int)Math.Min(result, int.MaxValue);
        }
        return int.TryParse(val, out int simple) ? simple : 0;
    }

    private static ushort ParseHexOrDec(string val)
    {
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ushort.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
                return hex;
        }
        else if (val.StartsWith('0') && val.Length > 1)
        {
            if (ushort.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
                return hex;
        }
        if (ushort.TryParse(val, out ushort dec))
            return dec;
        return 0;
    }
}
