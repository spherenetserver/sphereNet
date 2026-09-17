using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Scripting.Definitions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// An item type resolves to the NUMBER the reference gives it.
///
/// The reference distribution ships its own authority for this: a [TYPEDEFS] block
/// listing every hardcoded t_ name beside its value (core/defs_types_hardcoded.scp,
/// 200 entries). That is a stronger question than the one asked elsewhere - not "is
/// the name recognised" but "does it resolve to the same thing" - and the failure it
/// catches is worse. A name that resolves to nothing becomes Normal and behaves like
/// an ordinary item; a name that resolves to the WRONG number behaves like some
/// other type entirely, so a t_door might open as a t_key.
///
/// The engine's enum is compared through the real parser, because the rule is strip
/// t_, drop underscores, match case-insensitively.
/// </summary>
public sealed class ItemTypeNumberParityTests(ITestOutputHelper outp)
{
    private static readonly Regex Entry = new(@"^\s*(t_[A-Za-z0-9_]+)\s+(\d+)\s*(//.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SectionHeader = new(@"^\s*\[\s*([A-Za-z_]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Names whose number this engine deliberately does not share. Empty -
    /// every hardcoded type the reference numbers, this engine numbers the same.</summary>
    private static readonly HashSet<string> KnownDivergent =
        new(StringComparer.OrdinalIgnoreCase)
        {
        };

    /// <summary>Names in the block that the REFERENCE ENGINE has no type for either.
    /// The block's 500-range is the distribution's own extension - there is no
    /// IT_FOREST, IT_JUNGLE, IT_FURNITURE or IT_SOUL_FORGE in Source-X - so an engine
    /// that does not know them matches the reference. Anything else going absent is a
    /// type this engine lost.</summary>
    private static readonly HashSet<string> KnownPackExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "t_forest", "t_jungle", "t_furniture", "t_soul_forge",
        };

    private static string ResolveRoot(string root)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(dir.FullName, root));
    }

    [Fact]
    public void EveryHardcodedItemTypeResolvesToItsReferenceNumber()
    {
        string file = ResolveRoot("oldSphere/Scripts-X-main/core/defs_types_hardcoded.scp");
        if (Gate.Missing(outp, "Source-X reference tree", !File.Exists(file))) return;

        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool inBlock = false;
        foreach (string raw in File.ReadAllLines(file))
        {
            var header = SectionHeader.Match(raw);
            if (header.Success)
            {
                inBlock = header.Groups[1].Value.Equals("TYPEDEFS", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inBlock) continue;
            var m = Entry.Match(raw);
            if (m.Success) expected[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
        }

        // The block has to have loaded, or every assertion below is vacuous.
        Assert.True(expected.Count >= 150,
            $"the reference [TYPEDEFS] block looks partial: {expected.Count} entries");
        Assert.Equal(0, expected["t_normal"]);

        var wrong = new List<string>();
        var absent = new List<string>();
        int matched = 0;
        foreach (var (name, number) in expected)
        {
            var parsed = ItemDef.ParseTypeName(name);
            if (parsed == ItemType.Normal && number != 0) { absent.Add(name); continue; }
            if ((int)parsed == number) { matched++; continue; }
            wrong.Add($"{name}: reference {number}, engine {(int)parsed} ({parsed})");
        }

        outp.WriteLine($"{expected.Count} hardcoded types: {matched} agree, " +
                       $"{absent.Count} absent from the enum, {wrong.Count} disagree");
        foreach (string a in absent) outp.WriteLine($"ABSENT {a} = {expected[a]}");
        foreach (string w in wrong) outp.WriteLine($"MISMATCH {w}");

        Assert.True(matched >= 100, $"expected the enum to answer most types, saw {matched}");

        var surprises = wrong.Where(w => !KnownDivergent.Contains(w.Split(':')[0])).ToList();
        Assert.True(surprises.Count == 0,
            "these resolve to a DIFFERENT type than the reference gives them, so an item " +
            "declared with one behaves as something else: " + string.Join("; ", surprises));

        var lost = absent.Where(a => !KnownPackExtensions.Contains(a)).OrderBy(a => a).ToList();
        Assert.True(lost.Count == 0,
            "the reference numbers these and this engine resolves them to Normal: " +
            string.Join(", ", lost));

        var nowKnown = KnownPackExtensions
            .Where(k => expected.ContainsKey(k) && !absent.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k).ToList();
        Assert.True(nowKnown.Count == 0,
            "these resolve now - take them out of the baseline: " + string.Join(", ", nowKnown));
    }
}
