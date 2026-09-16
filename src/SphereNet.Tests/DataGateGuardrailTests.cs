using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// No data gate goes back to being silent (port plan İŞ-49 / PLAN-003).
///
/// The suite's data-dependent tests used to return early and report as passing,
/// so a run that measured nothing looked exactly like a run that measured
/// everything. They now go through Gate, which records what was and was not
/// there and writes the tally beside the TRX.
///
/// That only stays true if the next data-dependent test does the same. A static
/// scan is the enforcement: a check on a directory, a file or a null resource
/// that leads to a bare return, without passing through Gate, fails here.
/// </summary>
public sealed class DataGateGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public DataGateGuardrailTests(ITestOutputHelper output) => _out = output;

    private static string TestsDirectory([System.Runtime.CompilerServices.CallerFilePath] string self = "")
        => Path.GetDirectoryName(self)!;

    /// <summary>Conditions that look like a data check to a regex but are ordinary
    /// control flow. Each is named with the file it lives in so the list cannot
    /// grow into a blanket exemption.</summary>
    private static readonly HashSet<string> NotDataGates = new(StringComparer.Ordinal)
    {
        "SaveRoundTripParityTests.cs|section.Length == 0",
        "SpellRuneItemGraphicTests.cs|spell == null || spell.RuneItemId == 0",
    };

    private static readonly Regex DataCheck = new(
        @"Directory\.Exists|File\.Exists|== null|is null|\.Count == 0|\.Length == 0",
        RegexOptions.Compiled);

    private static IEnumerable<(string File, int Line, string Condition)> SilentGates()
    {
        foreach (string path in Directory.EnumerateFiles(TestsDirectory(), "*.cs")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name is "DataGate.cs" or "DataGateGuardrailTests.cs") continue;

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                var m = Regex.Match(t, @"^(?:if|else if) \((?<cond>.*)\)\s*$|^(?:if|else if) \((?<cond2>.*)\).*return;");
                if (!m.Success) continue;

                string cond = m.Groups["cond"].Success ? m.Groups["cond"].Value : m.Groups["cond2"].Value;
                if (cond.Length == 0 || t.Contains("Gate.")) continue;
                if (!DataCheck.IsMatch(cond)) continue;
                if (NotDataGates.Contains($"{name}|{cond}")) continue;

                // Does it lead to a bare return, either on the line or in the block?
                string window = string.Join(" ", lines.Skip(i).Take(6).Select(l => l.Trim()));
                if (!Regex.IsMatch(window, @"^\S.*?\breturn;")) continue;
                if (Regex.IsMatch(window, @"return\s+[^;]")) continue;   // returns a value: not a gate

                yield return (name, i + 1, cond);
            }
        }
    }

    [Fact]
    public void EveryDataDependentTestGoesThroughTheGate()
    {
        var silent = SilentGates().ToArray();
        foreach (var (f, l, c) in silent)
            _out.WriteLine($"{f}:{l}  {c}");

        // A new early return on missing data is the failure this catches. Route it
        // through Gate.Missing / Gate.MissingValue, or - if it really is ordinary
        // control flow - name it in NotDataGates with its file.
        Assert.Empty(silent);
    }

    [Fact]
    public void TheResourceVocabularyStaysSmallEnoughToRead()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(TestsDirectory(), "*.cs"))
        {
            if (Path.GetFileName(path) is "DataGate.cs" or "DataGateGuardrailTests.cs") continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                         @"Gate\.Missing(?:Value)?\([^,]+,\s*""([^""]*)"""))
                used.Add(m.Groups[1].Value);
        }

        foreach (string r in used) _out.WriteLine(r);

        // The report is only useful if a reader recognises the resource names. A
        // typo or a one-off phrasing makes the same resource look like two, so the
        // set is pinned rather than merely counted.
        Assert.Equal(
        [
            "56T save",
            "56T scripts and save",
            "Source-X reference tree",
            "UOP map files",
            "build stamp",
            "config/sphere.ini",
            "engine source",
            "external script pack",
            "live script pack",
            "live shard (scripts + mul + save)",
            "mul tables",
            "reference tables",
            "reference tables + live pack",
            "reference tables + modern pack",
            "script pack fixtures",
            "table export",
        ], used.ToArray());
    }

    [Fact]
    public void TheRunWritesItsGateTallyNextToTheTrx()
    {
        string? dir = Gate.ReportDirectory();
        if (Gate.MissingValue(_out, "table export", dir)) return;

        // This test's own gate above guarantees at least one observation, so the
        // report exists regardless of which order the runner picked.
        string report = Path.Combine(dir, "data-gates.md");
        _out.WriteLine(report);

        Assert.True(File.Exists(report), $"no gate report at {report}");
        string text = File.ReadAllText(report);
        Assert.Contains("gate(s) evaluated", text);
        Assert.Contains("| Test | Resource | Data |", text);
    }

}
