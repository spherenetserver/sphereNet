using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The release acceptance package, kept honest (port plan İŞ-46 / PLAN-705).
///
/// An acceptance document is dangerous exactly where it is convenient: it is
/// written once, read as authoritative, and drifts silently from the work it
/// claims to summarise. These tie the two numbers that would drift first - the
/// count of recorded deviations and the not-run items - back to their sources, so
/// the package cannot quietly overstate what was done.
///
/// Nothing here checks whether a deviation is a GOOD decision; the findings
/// document argues each one. This only checks the package is not lying about how
/// many there are, or about what was skipped.
/// </summary>
public sealed class ReleaseAcceptanceGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public ReleaseAcceptanceGuardrailTests(ITestOutputHelper output) => _out = output;

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir == null ? relative : Path.Combine(dir.FullName, relative);
    }

    private static string? Read(string relative)
    {
        string path = RepoPath(relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Every "Kayıtlı sapma" section in the findings document, paired with
    /// the work item it sits under.</summary>
    private static (string Work, string Heading)[] RecordedDeviations(string findings)
    {
        var lines = findings.Split('\n');
        string work = "?";
        var found = new System.Collections.Generic.List<(string, string)>();
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            var w = Regex.Match(line, @"^##\s+(İŞ-\d+)\s");
            if (w.Success) work = w.Groups[1].Value;
            if (Regex.IsMatch(line, @"^###\s+Kayıtlı sapma"))
                found.Add((work, line.Trim()));
        }
        return found.ToArray();
    }

    [Fact]
    public void ThePackageExists()
    {
        Assert.NotNull(Read(@"docs\RELEASE_KABUL_PAKETI_TR.md"));
    }

    [Fact]
    public void TheDeviationCountMatchesWhatTheFindingsActuallyRecord()
    {
        string? findings = Read(@"docs\INCELEME_DOGRULAMA_PLANI_TR.md");
        string? package = Read(@"docs\RELEASE_KABUL_PAKETI_TR.md");
        if (Gate.MissingValue(_out, "findings document", findings)) return;
        if (Gate.MissingValue(_out, "release package", package)) return;

        var deviations = RecordedDeviations(findings);
        _out.WriteLine($"recorded deviation sections: {deviations.Length}");
        foreach (var (w, h) in deviations)
            _out.WriteLine($"  {w}  {h}");

        // The package states the number in prose; it must be the real one. If a
        // later wave records another deviation, this fails until the package is
        // brought along with it.
        Assert.Contains($"**{deviations.Length} nokta**", package);
    }

    [Fact]
    public void EveryRecordedDeviationBelongsToANumberedWorkItem()
    {
        string? findings = Read(@"docs\INCELEME_DOGRULAMA_PLANI_TR.md");
        if (Gate.MissingValue(_out, "findings document", findings)) return;

        // A deviation with no owning İŞ heading cannot be traced back to the
        // measurement that justified it, which is the whole point of recording it.
        Assert.All(RecordedDeviations(findings), d => Assert.NotEqual("?", d.Work));
    }

    [Fact]
    public void ThePackageSaysPlainlyWhatWasNotRun()
    {
        string? package = Read(@"docs\RELEASE_KABUL_PAKETI_TR.md");
        if (Gate.MissingValue(_out, "release package", package)) return;

        // The two steps that really were not performed. An acceptance document
        // that goes quiet about them is worse than no document, so the words have
        // to be there.
        Assert.Contains("Gerçek istemci smoke", package);
        Assert.Contains("Soak", package);
        Assert.Equal(2, Regex.Matches(package, @"\*\*KOŞULMADI\*\*").Count);
    }

    [Fact]
    public void ThePackageCarriesTheOpenItemRatherThanClaimingAllGreen()
    {
        string? package = Read(@"docs\RELEASE_KABUL_PAKETI_TR.md");
        string? plan = Read(@"docs\PORT_PLAN_ILERLEME_TR.md");
        if (Gate.MissingValue(_out, "release package", package)) return;
        if (Gate.MissingValue(_out, "progress plan", plan)) return;

        // The unreproduced failure recorded in İŞ-43 is still open; both the plan
        // and the package must still be carrying it.
        Assert.Contains("Açık kalem", plan);
        Assert.Contains("Bilinen sorunlar", package);
        Assert.Contains("Tekrar üretilemeyen", package);
    }

    [Fact]
    public void TheRestoreDrillsAreClaimedAsRunBecauseTheyAre()
    {
        string? package = Read(@"docs\RELEASE_KABUL_PAKETI_TR.md");
        if (Gate.MissingValue(_out, "release package", package)) return;

        // Restore is the one operational drill that WAS performed, and the test
        // class backing that claim has to exist for the claim to stand.
        Assert.Contains("KOŞULDU", package);
        Assert.NotNull(Read(@"src\SphereNet.Tests\CrashDuringSaveDrillTests.cs"));
        Assert.NotNull(Read(@"src\SphereNet.Tests\BootFallbackTests.cs"));
    }
}
