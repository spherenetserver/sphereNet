using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The shipped ini's per-key status markers, kept honest (port plan İŞ-47 / PLAN-002).
///
/// config/sphere.ini annotates every key with what the engine does with it -
/// [ÇALIŞIYOR], [OKUNUYOR], [UYGULANMADI]. Those markers are the operator's only
/// map of which settings are worth touching, and they rot in one direction: a
/// wave implements a setting and nobody walks back to the ini. Seventeen keys had
/// drifted that way, several of them implemented in this very port round, each
/// still telling the operator the key was not defined in SphereConfig at all.
///
/// A marker that understates is not harmless. A working setting labelled dead is
/// a setting nobody tries.
///
/// The measurement is the assertions below; config/sphere.ini is the source.
/// </summary>
public sealed class IniKeyClassificationGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public IniKeyClassificationGuardrailTests(ITestOutputHelper output) => _out = output;

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir;
    }

    private static string? ReadRepo(string relative)
    {
        var root = RepoRoot();
        if (root == null) return null;
        string path = Path.Combine(root.FullName, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static readonly string[] WorkingMarkers =
        ["[ÇALIŞIYOR]", "[CALISIYOR]", "[UYGULANIYOR]", "[AKTİF]"];

    /// <summary>Each assignment in the ini paired with the status marker that governs
    /// it: the one in the contiguous comment block directly above. A blank line ends
    /// the block, so a marker cannot leak onto a later key.</summary>
    private static List<(string Key, string? Marker, int Line)> ParseIni(string ini)
    {
        var rows = new List<(string, string?, int)>();
        var block = new List<string>();
        var lines = ini.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim('\r', ' ', '\t');
            if (t.StartsWith("//")) { block.Add(t); continue; }
            if (t.Length == 0) { block.Clear(); continue; }

            var m = Regex.Match(t, @"^([A-Za-z0-9_]+)\s*=");
            if (m.Success)
            {
                string? marker = null;
                foreach (string c in block)
                {
                    if (WorkingMarkers.Any(c.Contains)) marker = "CALISIYOR";
                    else if (c.Contains("[UYGULANMADI]")) marker = "UYGULANMADI";
                    else if (c.Contains("[UYUMLULUK NO-OP]")) marker = "NOOP";
                    else if (c.Contains("[OKUNUYOR]")) marker = "OKUNUYOR";
                    else if (c.Contains("[DOKUMAN")) marker = "DOKUMAN";
                }
                rows.Add((m.Groups[1].Value.ToUpperInvariant(), marker, i + 1));
            }
            block.Clear();
        }
        return rows;
    }

    /// <summary>Every string literal in non-test source, upper-cased. Presence of a
    /// key's name here means something reads it - which is all this needs to decide,
    /// and it needs no knowledge of how the read is spelled.</summary>
    private static HashSet<string> SourceLiterals()
    {
        var root = RepoRoot();
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root == null) return set;

        string src = Path.Combine(root.FullName, "src");
        if (!Directory.Exists(src)) return set;

        char sep = Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("SphereNet.Tests", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{sep}obj{sep}")) continue;
            if (file.Contains($"{sep}bin{sep}")) continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"([A-Za-z][A-Za-z0-9_]{2,})\""))
                set.Add(m.Groups[1].Value.ToUpperInvariant());
        }
        return set;
    }

    [Fact]
    public void TheIniDefinesNoKeyTwice()
    {
        string? ini = ReadRepo(@"config\sphere.ini");
        if (Gate.MissingValue(_out, "config/sphere.ini", ini)) return;

        // The parser matches key names case-insensitively, so a second spelling of
        // the same key silently overrides the first and the file no longer says
        // which value the server actually used. ADVANCEDLOS was defined twice.
        var dupes = ParseIni(ini)
            .GroupBy(r => r.Key)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} @ {string.Join(", ", g.Select(x => x.Line))}")
            .ToArray();

        foreach (string d in dupes) _out.WriteLine(d);
        Assert.Empty(dupes);
    }

    [Fact]
    public void NoKeyMarkedUnimplementedIsActuallyRead()
    {
        string? ini = ReadRepo(@"config\sphere.ini");
        if (Gate.MissingValue(_out, "config/sphere.ini", ini)) return;

        var literals = SourceLiterals();
        if (Gate.Missing(_out, "engine source", literals.Count == 0)) return;

        // This is the drift that actually happened: a wave implements the setting,
        // the ini keeps saying it does nothing. Whoever implements one of the
        // remaining [UYGULANMADI] keys gets a red test until they update the ini.
        var lying = ParseIni(ini)
            .Where(r => r.Marker == "UYGULANMADI" && literals.Contains(r.Key))
            .Select(r => $"{r.Key} (line {r.Line}) is read in source but marked unimplemented")
            .ToArray();

        foreach (string l in lying) _out.WriteLine(l);
        Assert.Empty(lying);
    }

    /// <summary>Keys read in a way no literal scan can see. Each was traced by hand;
    /// the comment says how, so this list cannot quietly become a dumping ground.</summary>
    private static readonly HashSet<string> ReadWithoutALiteral = new(StringComparer.Ordinal)
    {
        "MAP0", "MAP1", "MAP2", "MAP3", "MAP4", "MAP5",  // read as $"Map{i}" in a loop
    };

    [Fact]
    public void EveryKeyMarkedWorkingIsReachableFromSource()
    {
        string? ini = ReadRepo(@"config\sphere.ini");
        if (Gate.MissingValue(_out, "config/sphere.ini", ini)) return;

        var literals = SourceLiterals();
        if (Gate.Missing(_out, "engine source", literals.Count == 0)) return;

        // The opposite drift: a marker promising behaviour for a key the engine
        // does not read at all. That one is worse - the operator sets it and
        // believes something changed.
        var promises = ParseIni(ini)
            .Where(r => r.Marker == "CALISIYOR")
            .Where(r => !literals.Contains(r.Key) && !ReadWithoutALiteral.Contains(r.Key))
            .Select(r => $"{r.Key} (line {r.Line}) is marked working but appears nowhere in source")
            .ToArray();

        foreach (string p in promises) _out.WriteLine(p);
        Assert.Empty(promises);
    }


}
