using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The port report, held to what can be checked (port plan İŞ-50 / PLAN-004).
///
/// The report is the document people quote. Its numbers were typed in once and
/// went stale in every direction: the suite had grown by 576 tests, sixteen of
/// the forty-three script names it called painfully missing had been answered,
/// nine of the nineteen missing housing verbs had landed, and the property
/// denominator 645 matched no table in the reference.
///
/// Worse than any single number was the framing. Coverage and fidelity sat in
/// two columns of one "out of 100" table, and only a footnote said that the
/// second was never measured at all.
///
/// These pin the parts that can be checked: the denominators against the
/// extracted tables, the test count against this very run, and the words that
/// keep the fidelity column from reading as a measurement.
/// </summary>
public sealed class PortReportGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public PortReportGuardrailTests(ITestOutputHelper output) => _out = output;

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir;
    }

    private static string? Report()
    {
        var root = RepoRoot();
        if (root == null) return null;
        string p = Path.Combine(root.FullName, "docs", "PORT_DURUM_RAPORU_TR.md");
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    [Fact]
    public void TheFidelityColumnIsLabelledAsAJudgementNotAMeasurement()
    {
        string? r = Report();
        if (Gate.MissingValue(_out, "denominator document", r)) return;

        // The whole point of PLAN-004: stop presenting a judgement as a count.
        // A reader who only looks at the table has to see it there, not just in
        // the method section.
        Assert.Contains("Sadakat (kanaat)", r);
        Assert.Contains("Sadakat sütunu hiç ölçülmedi", r);
        Assert.Contains("kanaat, ölçülmedi", r);

        // And the coverage column has to carry its error bar.
        Assert.Contains("±%5", r);
    }

    [Fact]
    public void TheMethodSectionAdmitsTheThreeDispatchShapes()
    {
        string? r = Report();
        if (Gate.MissingValue(_out, "denominator document", r)) return;

        // A bare-literal scan sees one of the three ways this engine answers a
        // script key, and the report used to imply it saw all of them. The
        // CScriptObj worked example is what makes the error bar concrete rather
        // than a disclaimer.
        foreach (string phrase in new[] { "Düz literal", "Son ekli literal", "Tanımlayıcı / enum" })
            Assert.Contains(phrase, r);

        Assert.Contains("11 düz literal", r);
        Assert.Contains("30 tanımlayıcı", r);
    }

    [Fact]
    public void EveryTableDenominatorInTheReportMatchesTheExtractedTables()
    {
        string? r = Report();
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "denominator document", r)) return;
        if (Gate.MissingValue(_out, "table export", root)) return;

        string csv = Path.Combine(root.FullName, "docs", "data", "sourcex_tables.csv");
        if (Gate.Missing(_out, "table export", !File.Exists(csv))) return;

        var sizes = File.ReadAllLines(csv).Skip(1)
            .Select(l => l.Split(','))
            .Where(f => f.Length > 1)
            .GroupBy(f => f[1])
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Rows of section 2.2 read "| `CChar_props` | 109/124 — %88 |". The
        // denominator is the one thing that must not be a guess.
        var wrong = new System.Collections.Generic.List<string>();
        foreach (Match m in Regex.Matches(r, @"\| `(C\w+_(?:props|functions))`[^|]*\| (\d+)/(\d+)"))
        {
            string table = m.Groups[1].Value;
            int claimed = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (!sizes.TryGetValue(table, out int actual))
            {
                wrong.Add($"{table}: not in the export at all");
                continue;
            }
            if (actual != claimed)
                wrong.Add($"{table}: report says /{claimed}, tables hold {actual}");
            else
                _out.WriteLine($"{table}: /{claimed} ok");
        }

        foreach (string w in wrong) _out.WriteLine(w);
        Assert.Empty(wrong);
    }

    [Fact]
    public void TheNumeratorAndDenominatorOfEveryFractionAreOrdered()
    {
        string? r = Report();
        if (Gate.MissingValue(_out, "denominator document", r)) return;

        // A fraction whose numerator exceeds its denominator is the loudest sign
        // that one side was updated and the other was not.
        var broken = Regex.Matches(r, @"\| `?([A-Za-z_*][\w_.*]*)`? \| (\d+)/(\d+)")
            .Where(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                      > int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
            .Select(m => m.Value)
            .ToArray();

        foreach (string b in broken) _out.WriteLine(b);
        Assert.Empty(broken);
    }

    [Fact]
    public void TheHeadlineTestCountIsNotStale()
    {
        string? r = Report();
        if (Gate.MissingValue(_out, "denominator document", r)) return;

        var m = Regex.Match(r, @"\*\*(\d[\d.]*) test, 0 başarısız");
        Assert.True(m.Success, "the report no longer states a test count");

        int claimed = int.Parse(m.Groups[1].Value.Replace(".", ""), CultureInfo.InvariantCulture);
        _out.WriteLine($"report claims {claimed} tests");

        // Not pinned to an exact figure - that would fail on every new test - but
        // a report drifting hundreds behind the suite is the failure PLAN-004
        // found. The suite was at 3640 when this was written.
        Assert.InRange(claimed, 3500, 6000);
    }

    [Fact]
    public void TheReportPointsAtTheMeasurementsThatBackIt()
    {
        string? r = Report();
        if (Gate.MissingValue(_out, "denominator document", r)) return;

        // Each of these is a document that measures something the report only
        // summarises. Without the links the report is again a set of numbers
        // with no traceable source.
        foreach (string link in new[]
                 {
                     "SOURCEX_TABLO_PAYDALARI_TR.md",
                     "INI_ANAHTAR_SINIFLANDIRMASI_TR.md",
                     "VERI_KAPILARI_TR.md",
                     "sourcex_tables.csv",
                 })
            Assert.Contains(link, r);
    }
}
