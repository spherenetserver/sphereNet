using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Review findings stay findable (port plan İŞ-51 / PLAN-005).
///
/// docs/reviews/ holds 106 chapters and 347 findings, each tied to a reference
/// contract and a reproduction. The folder is gitignored, so a finding only
/// survives as long as it has a line in the tracker.
///
/// Forty-one did not. They were written in a heading style the tracker's
/// numbering never covered, so they sat in neither the open list nor the closed
/// one - invisible to anyone working from the tracker, and a standing invitation
/// to re-implement a fix that already shipped. All forty-one turned out to be
/// closed; what was missing was the bookkeeping.
///
/// These keep the two corpora in step. The mapping is
/// docs/REVIEW_KAYIT_ESLEME_TR.md.
/// </summary>
public sealed class ReviewRecordGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public ReviewRecordGuardrailTests(ITestOutputHelper output) => _out = output;

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir;
    }

    private static string? ReviewsDir()
    {
        var root = RepoRoot();
        if (root == null) return null;
        string d = Path.Combine(root.FullName, "docs", "reviews");
        return Directory.Exists(d) ? d : null;
    }

    private static string? Tracker()
    {
        var root = RepoRoot();
        if (root == null) return null;
        string p = Path.Combine(root.FullName, "docs", "INCELEME_DOGRULAMA_PLANI_TR.md");
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    public sealed record Finding(string Chapter, string Id, string Priority, string Title);

    /// <summary>Findings are headed four different ways across the corpus - the
    /// early chapters carry an SX id, the middle ones a chapter-local id, the late
    /// ones a bare section number, and one chapter only a priority. Missing any of
    /// these styles is how forty-one findings went unrecorded.</summary>
    public static List<Finding> ReadFindings(string dir)
    {
        var all = new List<Finding>();
        foreach (string path in Directory.EnumerateFiles(dir, "SOURCE_X_BOLUM_*.md")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var fm = Regex.Match(Path.GetFileName(path), @"^SOURCE_X_BOLUM_([0-9]+[A-Z]?)_");
            if (!fm.Success) continue;
            string chapter = fm.Groups[1].Value;
            string text = File.ReadAllText(path);
            var found = new List<Finding>();

            foreach (Match m in Regex.Matches(text,
                         @"^##+\s+(SX-[0-9]+[A-Z]*-[0-9]+)\s*[—-]+\s*(.*)$", RegexOptions.Multiline))
                found.Add(new Finding(chapter, m.Groups[1].Value, "", m.Groups[2].Value.Trim()));

            if (found.Count == 0)
                foreach (Match m in Regex.Matches(text,
                             @"^##+\s+([0-9]+[A-Z]?-[0-9]+)\s*[—-]+\s*(P\d)?:?\s*(.*)$", RegexOptions.Multiline))
                    found.Add(new Finding(chapter, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim()));

            if (found.Count == 0)
                foreach (Match m in Regex.Matches(text,
                             @"^##+\s+(\d+)\.\s*(P\d)?\s*[—-]*\s*(.*)$", RegexOptions.Multiline))
                    found.Add(new Finding(chapter, $"{chapter}-{m.Groups[1].Value}",
                                          m.Groups[2].Value, m.Groups[3].Value.Trim()));

            if (found.Count == 0)
            {
                int i = 0;
                foreach (Match m in Regex.Matches(text,
                             @"^##+\s+(P\d)\s*[—-]+\s*(.*)$", RegexOptions.Multiline))
                    found.Add(new Finding(chapter, $"{chapter}-{++i}",
                                          m.Groups[1].Value, m.Groups[2].Value.Trim()));
            }

            all.AddRange(found);
        }
        return all;
    }

    /// <summary>The tracker normalises every id to SX-&lt;chapter&gt;-&lt;nn&gt;;
    /// the reviews do not. Accept the spellings rather than rename 106 files.</summary>
    private static IEnumerable<string> Spellings(string id)
    {
        yield return id;
        string sx = id.StartsWith("SX-", StringComparison.Ordinal) ? id : "SX-" + id;
        yield return sx;

        int dash = sx.LastIndexOf('-');
        if (dash < 0) yield break;
        string head = sx[..dash];
        if (int.TryParse(sx[(dash + 1)..], out int n))
        {
            yield return $"{head}-{n}";
            yield return $"{head}-{n:00}";
        }
    }

    private static Dictionary<string, char> TrackerBoxes(string tracker)
    {
        var boxes = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(tracker, @"^- \[( |x)\] \*\*([A-Za-z0-9-]+)",
                     RegexOptions.Multiline))
            if (!boxes.ContainsKey(m.Groups[2].Value))
                boxes[m.Groups[2].Value] = m.Groups[1].Value[0];
        return boxes;
    }

    [Fact]
    public void EveryReviewFindingHasALineInTheTracker()
    {
        string? dir = ReviewsDir();
        string? tracker = Tracker();
        if (Gate.MissingValue(_out, "review corpus", dir)) return;
        if (Gate.MissingValue(_out, "findings document", tracker)) return;

        var boxes = TrackerBoxes(tracker);
        var findings = ReadFindings(dir);
        _out.WriteLine($"{findings.Count} findings, {boxes.Count} tracker boxes");

        // A finding with no tracker line is in neither the open list nor the
        // closed one. Someone working from the tracker never sees it; someone
        // working from the reviews may re-implement a fix that already shipped.
        var orphans = findings
            .Where(f => !Spellings(f.Id).Any(boxes.ContainsKey))
            .Select(f => $"{f.Id} ({f.Priority}) {f.Title}")
            .ToArray();

        foreach (string o in orphans) _out.WriteLine(o);
        Assert.Empty(orphans);
    }

    [Fact]
    public void AnyReviewFindingLeftOpenIsNamed()
    {
        string? dir = ReviewsDir();
        string? tracker = Tracker();
        if (Gate.MissingValue(_out, "review corpus", dir)) return;
        if (Gate.MissingValue(_out, "findings document", tracker)) return;

        var boxes = TrackerBoxes(tracker);
        var open = ReadFindings(dir)
            .Where(f => Spellings(f.Id).Any(s => boxes.TryGetValue(s, out char c) && c == ' '))
            .Select(f => $"{f.Id} ({f.Priority}) {f.Title}")
            .ToArray();

        foreach (string o in open) _out.WriteLine("OPEN: " + o);

        // An open finding is not a defect in itself - work in progress is normal.
        // It must not be silent, which is what this prints. The count is pinned so
        // that an item quietly reopening is visible rather than absorbed.
        Assert.Empty(open);
    }

    [Fact]
    public void TheMappingDocumentMatchesTheCorpus()
    {
        string? dir = ReviewsDir();
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "review corpus", dir)) return;
        if (Gate.MissingValue(_out, "findings document", root)) return;

        string doc = Path.Combine(root.FullName, "docs", "REVIEW_KAYIT_ESLEME_TR.md");
        if (Gate.Missing(_out, "findings document", !File.Exists(doc))) return;

        string text = File.ReadAllText(doc);
        var findings = ReadFindings(dir);
        int chapters = findings.Select(f => f.Chapter).Distinct().Count();
        _out.WriteLine($"chapters={chapters} findings={findings.Count}");

        Assert.Contains($"**{chapters} bölüm**", text);
        Assert.Contains($"**{findings.Count} bulgu**", text);
        Assert.Contains($"| Takip planında onay kutusu olan | **{findings.Count}** |", text);
    }
}
