using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The reference's key tables, exported so a denominator can be re-derived
/// (port plan İŞ-48 / PLAN-001).
///
/// Coverage claims in this repo are written as "X of Y". Y is almost always the
/// size of some Source-X table, typed into a document by hand and never checked
/// again. Two of them do not reproduce: the verb surface is quoted as 206, which
/// is the sum of four tables and counts twenty keys twice; the property surface
/// is quoted as 645, which matches neither the 777 table entries nor the 571
/// distinct keys.
///
/// So the tables are extracted here instead of remembered, into
/// docs/data/sourcex_tables.csv, and this class re-extracts them on every run and
/// compares. The acceptance criterion for PLAN-001 is that another machine on the
/// same commit gets the same denominators; that only holds if the export is
/// derived rather than maintained.
///
/// Skips cleanly when oldSphere/ is absent, so CI without the reference tree stays
/// green - see docs/SOURCEX_TABLO_PAYDALARI_TR.md.
/// </summary>
public sealed class SourceXTableInventoryGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public SourceXTableInventoryGuardrailTests(ITestOutputHelper output) => _out = output;

    public sealed record Row(
        string Source, string Table, string Owner, string Prefix,
        string Kind, int Index, string Enum, string Key, string Era);

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir;
    }

    private static string? ReferenceRoot()
    {
        var root = RepoRoot();
        if (root == null) return null;
        string src = Path.Combine(root.FullName, "oldSphere", "Source-X-full", "src");
        return Directory.Exists(src) ? src : null;
    }

    // ADD(ENUM,"KEY") / MSG(ENUM,"KEY") / ADDPROP(ENUM,"KEY",ERA). The third field
    // on a component property is its expansion gate.
    private static readonly Regex TblEntry = new(
        @"^\s*(?:ADD|MSG|ADDPROP)\(\s*([A-Za-z0-9_]+)\s*,\s*""([^""]*)""\s*(?:,\s*([A-Za-z0-9_]+)\s*)?\)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // triggers.tbl and classnames.tbl carry a bare enum per line, no string.
    private static readonly Regex TblBareEntry = new(
        @"^\s*ADD\(\s*([A-Za-z0-9_]+)\s*\)", RegexOptions.Multiline | RegexOptions.Compiled);

    // A table declaration may carry a trailing comment on the same line, which is
    // how CChar::sm_szTrigName (191 entries) hides from a stricter pattern.
    private static readonly Regex TableDecl = new(
        @"^(?:static\s+)?(?:const\s+)?lpctstr\s+(?:const\s+)?(?:([A-Za-z_0-9]+)::)?([A-Za-z_0-9]+)\s*\[[^\]]*\]\s*=\s*(?://[^\n]*)?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TblInclude = new(
        @"#include\s+""[^""]*/([A-Za-z_0-9]+)\.tbl""", RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"""([^""]*)""", RegexOptions.Compiled);

    private static string KindOf(string tableName, string? declaredSet)
    {
        if (!string.IsNullOrEmpty(declaredSet)) return declaredSet;
        if (tableName.Contains("TrigName")) return "triggers";
        if (tableName.Contains("Verb")) return "verbs";
        if (tableName.Contains("Ref")) return "refs";
        if (tableName.Contains("Load")) return "props";
        return "other";
    }

    private static IEnumerable<string> SourceFiles(string refRoot) =>
        Directory.EnumerateFiles(refRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tables{Path.DirectorySeparatorChar}"));

    /// <summary>Which C++ table pulls in which .tbl file, so an entry can be named
    /// by the table a script actually reaches rather than by a filename.</summary>
    private static Dictionary<string, List<string>> TblOwners(string refRoot)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var decl = new Regex(
            @"lpctstr\s+(?:const\s+)?(?:([A-Za-z_0-9]+)::)?([A-Za-z_0-9]+)\s*\[[^\]]*\]\s*=\s*\{([^}]*)\}",
            RegexOptions.Singleline | RegexOptions.Compiled);

        foreach (string file in SourceFiles(refRoot))
        {
            string txt = File.ReadAllText(file);
            foreach (Match m in decl.Matches(txt))
            {
                var inc = TblInclude.Match(m.Groups[3].Value);
                if (!inc.Success) continue;
                string name = (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value + "::" : "")
                            + m.Groups[2].Value;
                if (!owners.TryGetValue(inc.Groups[1].Value, out var list))
                    owners[inc.Groups[1].Value] = list = [];
                list.Add(name);
            }
        }
        return owners;
    }

    public static List<Row> Extract(string refRoot)
    {
        var rows = new List<Row>();
        var owners = TblOwners(refRoot);

        string tdir = Path.Combine(refRoot, "tables");
        foreach (string path in Directory.EnumerateFiles(tdir, "*.tbl").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            string table = file[..^4];
            string txt = File.ReadAllText(path);

            string prefix = Regex.Match(txt, @"//\s*Prefix:\s*(?:\(special\)\s*)?(\w+)").Groups[1].Value;
            string set = Regex.Match(txt, @"//\s*Set:\s*(\w+)").Groups[1].Value;
            string owner = owners.TryGetValue(table, out var o) ? string.Join(" + ", o) : table;

            int idx = 0;
            foreach (Match m in TblEntry.Matches(txt))
                rows.Add(new Row("tables/" + file, table, owner, prefix,
                    set.Length > 0 ? set : (file.Contains("_functions") ? "functions" : "props"),
                    idx++, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));

            if (idx == 0)
                foreach (Match m in TblBareEntry.Matches(txt))
                    rows.Add(new Row("tables/" + file, table, owner, prefix,
                        set.Length > 0 ? set : "list", idx++, m.Groups[1].Value, m.Groups[1].Value, ""));
        }

        foreach (string path in SourceFiles(refRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            string txt = File.ReadAllText(path);
            string rel = Path.GetRelativePath(refRoot, path).Replace(Path.DirectorySeparatorChar, '/');

            foreach (Match m in TableDecl.Matches(txt))
            {
                int open = txt.IndexOf('{', m.Index + m.Length);
                if (open < 0) continue;

                int depth = 0, i = open;
                for (; i < txt.Length; i++)
                {
                    if (txt[i] == '{') depth++;
                    else if (txt[i] == '}' && --depth == 0) break;
                }
                if (i >= txt.Length) continue;

                string body = txt[(open + 1)..i];
                if (body.Contains("#include")) continue;   // macro-expanded from a .tbl
                body = LineComment.Replace(body, "");

                string name = (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value + "::" : "")
                            + m.Groups[2].Value;
                int idx = 0;
                foreach (Match s in StringLiteral.Matches(body))
                    rows.Add(new Row(rel, name, name, "", KindOf(name, null),
                        idx++, "", s.Groups[1].Value, ""));
            }
        }
        return rows;
    }

    private static string ToCsv(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder("source,table,owner,prefix,kind,index,enum,key,era\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', new[]
            {
                r.Source, r.Table, r.Owner, r.Prefix, r.Kind,
                r.Index.ToString(), r.Enum, r.Key, r.Era,
            }.Select(Quote))).Append('\n');
        return sb.ToString();

        static string Quote(string v) =>
            v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? '"' + v.Replace("\"", "\"\"") + '"'
                : v;
    }

    private static string? ExportPath()
    {
        var root = RepoRoot();
        return root == null ? null : Path.Combine(root.FullName, "docs", "data", "sourcex_tables.csv");
    }

    [Fact]
    public void TheExportedInventoryMatchesTheReference()
    {
        string? refRoot = ReferenceRoot();
        string? export = ExportPath();
        if (refRoot == null || export == null || !File.Exists(export))
        {
            _out.WriteLine("SKIP: reference tree or export not present");
            return;
        }

        string produced = ToCsv(Extract(refRoot));
        string committed = File.ReadAllText(export).Replace("\r\n", "\n");

        // Any drift means the export was hand-edited or the reference moved. Either
        // way the denominators in the documents stopped being derivable, which is
        // the exact failure PLAN-001 exists to prevent.
        if (produced != committed)
        {
            var p = produced.Split('\n');
            var c = committed.Split('\n');
            _out.WriteLine($"produced {p.Length} lines, committed {c.Length}");
            for (int i = 0; i < Math.Min(p.Length, c.Length); i++)
                if (p[i] != c[i])
                {
                    _out.WriteLine($"first difference at line {i + 1}:");
                    _out.WriteLine("  produced:  " + p[i]);
                    _out.WriteLine("  committed: " + c[i]);
                    break;
                }
        }
        Assert.Equal(committed, produced);
    }

    [Fact]
    public void TheTriggerDenominatorIsNotJustTriggersTbl()
    {
        string? refRoot = ReferenceRoot();
        if (refRoot == null) { _out.WriteLine("SKIP: reference tree not present"); return; }

        var rows = Extract(refRoot);
        var global = rows.Where(r => r.Table == "triggers")
                         .Select(r => r.Key.ToUpperInvariant().Replace("TRIGGER_", ""))
                         .ToHashSet(StringComparer.Ordinal);
        var perClass = rows.Where(r => r.Kind == "triggers" && r.Table != "triggers")
                           .Select(r => r.Key.ToUpperInvariant().TrimStart('@'))
                           .ToHashSet(StringComparer.Ordinal);

        _out.WriteLine($"triggers.tbl={global.Count} per-class unique={perClass.Count}");
        foreach (string e in global.Except(perClass).OrderBy(x => x, StringComparer.Ordinal))
            _out.WriteLine("  only in triggers.tbl: " + e);
        var extra = perClass.Except(global).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (string e in extra) _out.WriteLine("  only per-class: " + e);

        // triggers.tbl is the ordered global list, not the whole reachable surface:
        // five per-class triggers are absent from it. A claim measured against 248
        // is measuring against the smaller of two real numbers.
        Assert.Equal(248, global.Count);
        Assert.Equal(252, perClass.Count);
        Assert.Equal(5, extra.Length);

        // The two lists are not nested either way: one name sits in the ordered
        // global list and in no per-class table, so the whole reachable surface is
        // 253 names, not 248 and not 252.
        Assert.Single(global.Except(perClass));
        Assert.Equal(253, global.Union(perClass).Count());
    }

    [Fact]
    public void SummingTablesDoubleCountsSharedKeys()
    {
        string? refRoot = ReferenceRoot();
        if (refRoot == null) { _out.WriteLine("SKIP: reference tree not present"); return; }

        var rows = Extract(refRoot);
        string[] verbTables = ["CObjBase_functions", "CChar_functions", "CItem_functions", "CClient_functions"];
        var picked = rows.Where(r => verbTables.Contains(r.Table)).ToArray();

        int entries = picked.Length;
        int distinct = picked.Select(r => r.Key.ToUpperInvariant()).Distinct().Count();
        _out.WriteLine($"verb tables: {entries} entries, {distinct} distinct keys");

        // The port report quotes 206 for this surface. That is the entry count; the
        // distinct key count is 186, because twenty keys are declared in more than
        // one of the four tables. Both are true numbers of different things, and a
        // coverage claim has to say which one it means.
        Assert.Equal(206, entries);
        Assert.Equal(186, distinct);
    }

    [Fact]
    public void ThePropertySurfaceIsNeitherOfTheNumbersEverQuoted()
    {
        string? refRoot = ReferenceRoot();
        if (refRoot == null) { _out.WriteLine("SKIP: reference tree not present"); return; }

        var props = Extract(refRoot).Where(r => r.Table.EndsWith("_props", StringComparison.Ordinal)).ToArray();
        int entries = props.Length;
        int distinct = props.Select(r => r.Key.ToUpperInvariant()).Distinct().Count();
        _out.WriteLine($"*_props.tbl: {entries} entries, {distinct} distinct");

        // Recorded so the correction in the port report has a measured basis.
        Assert.Equal(777, entries);
        Assert.Equal(571, distinct);
        Assert.NotEqual(645, entries);
        Assert.NotEqual(645, distinct);
    }

    [Fact]
    public void ComponentPropertiesCarryTheirExpansionGate()
    {
        string? refRoot = ReferenceRoot();
        if (refRoot == null) { _out.WriteLine("SKIP: reference tree not present"); return; }

        var gated = Extract(refRoot).Where(r => r.Era.Length > 0).ToArray();
        var byEra = gated.GroupBy(r => r.Era).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (era, n) in byEra.OrderByDescending(kv => kv.Value))
            _out.WriteLine($"{era,-14}{n}");

        // ADDPROP's third field is the expansion that unlocks the property. It is
        // the only place the reference states the era gate in data rather than in
        // code, and the AOS property matrix depends on it.
        Assert.Equal(219, gated.Length);
        Assert.Equal(101, byEra["RDS_AOS"]);
        Assert.Equal(62, byEra["RDS_PRET2A"]);
    }

    [Fact]
    public void TheDenominatorDocumentStatesTheSameTotals()
    {
        var root = RepoRoot();
        string? doc = root == null ? null
            : Path.Combine(root.FullName, "docs", "SOURCEX_TABLO_PAYDALARI_TR.md");
        if (doc == null || !File.Exists(doc)) { _out.WriteLine("SKIP: doc not found"); return; }

        string text = File.ReadAllText(doc);
        string? export = ExportPath();
        if (export == null || !File.Exists(export)) { _out.WriteLine("SKIP: export not found"); return; }

        int lines = File.ReadAllLines(export).Length - 1;   // minus the header
        _out.WriteLine($"export rows: {lines}");

        Assert.Contains($"**{lines} giriş**", text);
        Assert.Contains("206", text);
        Assert.Contains("186", text);
        Assert.Contains("645", text);
    }
}
