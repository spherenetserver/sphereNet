using System.Text.RegularExpressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

internal sealed class ScriptPackInventory
{
    private static readonly Regex SectionRegex = new(@"^\s*\[([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex OnRegex = new(@"^\s*ON\s*=\s*(@?[A-Za-z0-9_]+|\d+|\*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DialogButtonRegex = new(@"^\s*(BUTTON|ONBUTTON)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FeatureRegex = new(@"\b(DB\.|SQL|SQLDB|MYSQL|SQLITE|SERV\.|SENDPACKET|PACKET|STRREGEXNEW|ISOBSCENE|WEBPAGE|SKILLMENU|INPDLG|TRYSRV|TRYSRC|TRYP|BUY|SELL|TIMERF)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly List<ScriptRiskHit> _riskHits = [];

    public required string RootPath { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyDictionary<string, int> SectionCounts { get; init; }
    public required IReadOnlyDictionary<string, int> TriggerCounts { get; init; }
    public required IReadOnlyDictionary<string, int> FeatureCounts { get; init; }
    public IReadOnlyList<ScriptRiskHit> RiskHits => _riskHits;

    public int TotalSections => SectionCounts.Values.Sum();

    public int UnknownSectionCount => SectionCounts
        .Where(kv => ResourceHolder.SectionToResType(SectionName(kv.Key)) == SphereNet.Core.Enums.ResType.Unknown)
        .Sum(kv => kv.Value);

    public static ScriptPackInventory Build(string rootPath)
    {
        var files = Directory.GetFiles(rootPath, "*.scp", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var triggerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var featureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inventory = new ScriptPackInventory
        {
            RootPath = rootPath,
            Files = files,
            SectionCounts = sectionCounts,
            TriggerCounts = triggerCounts,
            FeatureCounts = featureCounts,
        };

        foreach (string file in files)
        {
            int lineNo = 0;
            foreach (string rawLine in File.ReadLines(file))
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                var sectionMatch = SectionRegex.Match(line);
                if (sectionMatch.Success)
                {
                    string section = sectionMatch.Groups[1].Value.Trim();
                    Increment(sectionCounts, section);
                    if (ResourceHolder.SectionToResType(SectionName(section)) == SphereNet.Core.Enums.ResType.Unknown)
                        inventory._riskHits.Add(new ScriptRiskHit("unknown-section", file, lineNo, section));
                }

                var onMatch = OnRegex.Match(line);
                if (onMatch.Success)
                    Increment(triggerCounts, onMatch.Groups[1].Value.Trim());

                if (DialogButtonRegex.IsMatch(line))
                    Increment(featureCounts, "dialog-button");

                var featureMatch = FeatureRegex.Match(line);
                if (featureMatch.Success)
                {
                    string feature = NormalizeFeature(featureMatch.Groups[1].Value);
                    Increment(featureCounts, feature);
                    inventory._riskHits.Add(new ScriptRiskHit("feature", file, lineNo, feature));
                }
            }
        }

        return inventory;
    }

    public IReadOnlyList<(string Key, int Count)> TopSections(int take = 20) => Top(SectionCounts, take);
    public IReadOnlyList<(string Key, int Count)> TopTriggers(int take = 20) => Top(TriggerCounts, take);
    public IReadOnlyList<(string Key, int Count)> TopFeatures(int take = 20) => Top(FeatureCounts, take);

    private static IReadOnlyList<(string Key, int Count)> Top(IReadOnlyDictionary<string, int> source, int take) =>
        source
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();

    private static string SectionName(string section)
    {
        int space = section.IndexOfAny([' ', '\t']);
        return space >= 0 ? section[..space] : section;
    }

    private static string NormalizeFeature(string value)
    {
        string upper = value.Trim().TrimEnd('.').ToUpperInvariant();
        return upper.StartsWith("DB", StringComparison.Ordinal) ? "DB.*" : upper;
    }

    private static void Increment(Dictionary<string, int> map, string key)
    {
        map.TryGetValue(key, out int count);
        map[key] = count + 1;
    }
}

internal readonly record struct ScriptRiskHit(string Kind, string File, int Line, string Feature);
