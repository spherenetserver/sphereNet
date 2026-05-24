namespace SphereNet.Tests;

internal sealed record ScriptPackCompatibilitySummary(
    ScriptPackProfile Profile,
    int Files,
    int Sections,
    int Resources,
    int UnknownSections,
    int RiskHits,
    int DbHits,
    int ServHits,
    int PacketHits,
    int DialogButtonHits,
    int TimerFHits,
    int SkillMenuHits,
    int BuySellHits,
    int StartsSections,
    int MoongateSections,
    int MultiDefSections,
    int StaffWorldgenFiles,
    int ExpressionGaps,
    int UnhandledLines,
    int MissingFunctions)
{
    public static ScriptPackCompatibilitySummary Create(
        ScriptPackProfile profile,
        ScriptPackInventory inventory,
        int resources,
        ScriptDiagnosticCollector diagnostics)
    {
        int feature(string key) => inventory.FeatureCounts.TryGetValue(key, out int count) ? count : 0;
        int featureGroup(string prefix) => inventory.FeatureCounts
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Sum(kv => kv.Value);

        return new ScriptPackCompatibilitySummary(
            profile,
            inventory.Files.Count,
            inventory.TotalSections,
            resources,
            inventory.UnknownSectionCount,
            inventory.RiskHits.Count,
            featureGroup("DB"),
            feature("SERV"),
            featureGroup("PACKET") + feature("SENDPACKET"),
            feature("dialog-button"),
            feature("TIMERF"),
            feature("SKILLMENU"),
            feature("BUY") + feature("SELL"),
            sectionGroup(inventory, "STARTS") + sectionGroup(inventory, "STARTSGOLD"),
            sectionGroup(inventory, "MOONGATES"),
            sectionGroup(inventory, "MULTIDEF"),
            inventory.Files.Count(f => f.Replace('\\', '/').Contains("worldgen", StringComparison.OrdinalIgnoreCase)),
            diagnostics.Count("expr"),
            diagnostics.Count("unhandled"),
            diagnostics.Count("missing-function"));
    }

    private static int sectionGroup(ScriptPackInventory inventory, string prefix) => inventory.SectionCounts
        .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Sum(kv => kv.Value);

    public double LoadScore =>
        Files == 0 ? 0 : Math.Clamp(10.0 - UnknownSections * 0.05, 0, 10);

    public double RuntimeRiskScore =>
        Math.Clamp(10.0 - (ExpressionGaps + UnhandledLines + MissingFunctions) * 0.1 - (DbHits + PacketHits) * 0.01, 0, 10);

    public IReadOnlyList<CompatibilityCategoryScore> CategoryScores => new[]
    {
        Score("load", LoadScore, UnknownSections),
        Score("runtime-triggers", RuntimeRiskScore, ExpressionGaps + UnhandledLines + MissingFunctions),
        Score("db-ldb", DbHits > 0 ? 8.0 : 10.0, DbHits),
        Score("serv", ServHits > 0 ? 8.0 : 10.0, ServHits),
        Score("dialogs", DialogButtonHits > 0 ? 8.0 : 10.0, DialogButtonHits),
        Score("vendor-craft", BuySellHits + SkillMenuHits > 0 ? 7.5 : 10.0, BuySellHits + SkillMenuHits),
        Score("world-map", StartsSections + MoongateSections > 0 ? 8.0 : 10.0, StartsSections + MoongateSections),
        Score("multi-housing", MultiDefSections > 0 ? 7.5 : 10.0, MultiDefSections),
        Score("packet", PacketHits > 0 ? 7.0 : 10.0, PacketHits),
        Score("staff-worldgen", StaffWorldgenFiles > 0 ? 8.0 : 10.0, StaffWorldgenFiles),
    };

    private static CompatibilityCategoryScore Score(string name, double score, int gaps) =>
        new(name, Math.Round(Math.Clamp(score, 0, 10), 1), gaps);
}

internal sealed record CompatibilityCategoryScore(string Category, double Score, int GapCount);
