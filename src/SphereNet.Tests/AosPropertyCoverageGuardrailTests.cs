using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which AOS+ item properties the running content actually uses (port plan İŞ-40 /
/// PLAN-602).
///
/// The plan warns against counting a property as done because it shows in a tooltip.
/// Measured here, the opposite holds: this engine's tooltip emits NO component
/// property at all (name, DAM, SPEED, ARMOR, DURABILITY, container count and the
/// comm crystal lines are the whole list), so nothing can be mistaken for finished
/// on the strength of a tooltip line.
///
/// The era question is the one that matters. The reference groups all 139 component
/// properties by the expansion that introduced them (RDS_PRET2A / AOS / ML / SA / HS
/// / TOL, src/tables/CCProps*_props.tbl), and the live shard runs a classic-era pack
/// against a 7.0.20 client: it sets exactly two of them, both pre-T2A. The modern
/// reference pack sets 59. This pins both numbers so a claim about AOS property
/// coverage has to answer to the content that exists.
/// </summary>
public sealed class AosPropertyCoverageGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public AosPropertyCoverageGuardrailTests(ITestOutputHelper output) => _out = output;

    private const string RefTables = @"oldSphere\Source-X-full\src\tables";
    private const string LivePack = @"C:\sphereNetServer\scripts";
    private const string ModernPack = @"oldSphere\Scripts-X-main";

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? relative : Path.Combine(dir.FullName, relative);
    }

    /// <summary>Expansion order, oldest first. A name can appear on more than one
    /// component class with different tags - NIGHTSIGHT is PRET2A on one and AOS on
    /// another - and the EARLIEST is what decides whether classic content may set
    /// it, so that is the one kept.</summary>
    private static readonly string[] EraOrder = ["PRET2A", "AOS", "ML", "SA", "HS", "TOL"];

    /// <summary>Every component property the reference defines, with the earliest
    /// expansion that introduced it.</summary>
    private static Dictionary<string, string> ReadReferenceProperties()
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        string dir = RepoPath(RefTables);
        if (!Directory.Exists(dir))
            return props;

        foreach (var file in Directory.EnumerateFiles(dir, "CCProps*_props.tbl"))
            foreach (var line in File.ReadLines(file))
            {
                var m = Regex.Match(line,
                    @"^\s*ADDPROP\(\s*[A-Z0-9_]+\s*,\s*""([A-Z0-9_]+)""\s*,\s*RDS_([A-Z0-9]+)");
                if (!m.Success) continue;
                string name = m.Groups[1].Value, era = m.Groups[2].Value;
                if (!props.TryGetValue(name, out var seen) ||
                    Array.IndexOf(EraOrder, era) < Array.IndexOf(EraOrder, seen))
                    props[name] = era;
            }
        return props;
    }

    /// <summary>Property names a pack assigns anywhere (<c>KEY=value</c> at the head
    /// of a line, which is how a def sets one).</summary>
    private static HashSet<string> ReadAssignedKeys(string packDir)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(packDir))
            return keys;

        var head = new Regex(@"^\s*([A-Za-z0-9_]+)\s*=", RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(packDir, "*.scp", SearchOption.AllDirectories))
        {
            IEnumerable<string> lines;
            try { lines = File.ReadLines(file); }
            catch (IOException) { continue; }
            foreach (var line in lines)
            {
                var m = head.Match(line);
                if (m.Success)
                    keys.Add(m.Groups[1].Value.ToUpperInvariant());
            }
        }
        return keys;
    }

    [Fact]
    public void TheReferenceGroupsItsComponentPropertiesByExpansion()
    {
        var props = ReadReferenceProperties();
        if (props.Count == 0)
        {
            _out.WriteLine("SKIP: reference tables not found");
            return;
        }

        foreach (var g in props.GroupBy(p => p.Value).OrderByDescending(g => g.Count()))
            _out.WriteLine($"{g.Key}: {g.Count()}");

        Assert.Equal(139, props.Count);
        // The era tag is the whole point of the exercise: it is what says whether a
        // property belongs to the content a given shard runs.
        Assert.Contains("PRET2A", props.Values);
        Assert.Contains("AOS", props.Values);
        Assert.Contains("TOL", props.Values);
    }

    [Fact]
    public void TheLiveShardsContentUsesAlmostNoneOfThem()
    {
        var props = ReadReferenceProperties();
        if (props.Count == 0 || !Directory.Exists(LivePack))
        {
            _out.WriteLine("SKIP: reference tables or live pack not found");
            return;
        }

        var used = ReadAssignedKeys(LivePack).Where(props.ContainsKey).OrderBy(k => k).ToList();
        _out.WriteLine($"live pack sets {used.Count} of {props.Count}: {string.Join(", ", used)}");

        // A classic-era pack against a 7.0.20 client. Both of these are pre-T2A
        // Sphere properties, not part of the AOS property system at all.
        Assert.Equal(new[] { "NIGHTSIGHT", "RANGE" }, used);
        Assert.All(used, k => Assert.Equal("PRET2A", props[k]));
    }

    [Fact]
    public void TheModernReferencePackIsWhereTheseLive()
    {
        var props = ReadReferenceProperties();
        string modern = RepoPath(ModernPack);
        if (props.Count == 0 || !Directory.Exists(modern))
        {
            _out.WriteLine("SKIP: reference tables or modern pack not found");
            return;
        }

        var used = ReadAssignedKeys(modern).Where(props.ContainsKey).OrderBy(k => k).ToList();
        _out.WriteLine($"modern pack sets {used.Count} of {props.Count}");

        // Two orders of magnitude apart from the live pack - that difference IS the
        // era gate, and it is why AOS property work has no consumer on this shard.
        Assert.Equal(59, used.Count);
        Assert.Contains("RESFIRE", used);
        Assert.Contains("HITLEECHLIFE", used);
    }
}
