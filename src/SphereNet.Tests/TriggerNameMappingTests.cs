using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which trigger names the reference really has, mapped by context rather than by
/// enum name (port plan İŞ-64 / PLAN-206).
///
/// PLAN-206 is explicit about the method: derive the missing triggers from the
/// reference's NAME / ALIAS / CONTEXT mapping, not from whether an enum member
/// happens to be spelled the same. Comparing raw name sets reports 73 missing,
/// and almost none of them are missing:
///
///   a name lives in a per-class table, so SELECT is CSkillDef's and CSpellDef's
///   trigger and this engine spells those SkillSelect and SpellSelect;
///
///   the char table mirrors part of the item table with an ITEM prefix, so
///   DROPON_CHAR and ITEMDROPON_CHAR are one trigger seen from two sides;
///
///   AAAUNUSED is a placeholder that pads the enum, not a trigger at all.
///
/// So the comparison has to carry the owning table with each name. What survives
/// that is the real backlog, and it is what the port report's "~27 missing" figure
/// should be measured against - İŞ-50 deliberately left that number alone because
/// a name appearing in source proves it is an enum member, not that it fires.
/// </summary>
public sealed class TriggerNameMappingTests
{
    private readonly ITestOutputHelper _out;
    public TriggerNameMappingTests(ITestOutputHelper output) => _out = output;

    private static string? RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        if (dir == null) return null;
        string p = Path.Combine(dir.FullName, relative);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Reference trigger names with the table that owns each, from the
    /// extracted inventory.</summary>
    private static List<(string Owner, string Name)> ReferenceTriggers(string csv)
    {
        var rows = new List<(string, string)>();
        foreach (string line in File.ReadLines(csv).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 8) continue;
            string table = f[1], owner = f[2], kind = f[4], key = f[7];
            if (kind != "triggers" || table == "triggers") continue;
            rows.Add((owner, key.TrimStart('@').ToUpperInvariant()));
        }
        return rows;
    }

    /// <summary>How this engine spells a trigger that belongs to a particular
    /// reference table. The prefix IS the context: upstream distinguishes
    /// CSkillDef's SELECT from CSpellDef's by which table the name sits in, and this
    /// engine does it by name because its enums are flat.</summary>
    private static readonly Dictionary<string, string> ContextPrefix = new(StringComparer.Ordinal)
    {
        ["CSkillDef::sm_szTrigName"] = "SKILL",
        ["CSpellDef::sm_szTrigName"] = "SPELL",
        ["CRegion::sm_szTrigName"] = "REGION",
        ["CRegionResourceDef::sm_szTrigName"] = "REGIONRESOURCE",
        ["CWebPageDef::sm_szTrigName"] = "WEB",
        ["CChar::sm_szTrigName"] = "",
        ["CItem::sm_szTrigName"] = "",
    };

    /// <summary>Every spelling this engine would accept for a reference name in a
    /// given table.</summary>
    /// <summary>Upstream separates the words of a compound trigger with an
    /// underscore (@DropOn_Char, @Ship_Move); the enums here run them together
    /// because a C# member cannot carry the reference's exact punctuation. The
    /// underscore is spelling, not identity.</summary>
    private static string Squash(string name) => name.Replace("_", "");

    private static IEnumerable<string> Spellings(string owner, string name)
    {
        yield return name;
        yield return Squash(name);
        if (ContextPrefix.TryGetValue(owner, out string? prefix) && prefix.Length > 0)
        {
            yield return prefix + name;
            yield return prefix + Squash(name);
        }

        // A handful of names this engine spells differently on purpose. Each is the
        // SAME event, not a near miss: a region's EXIT is its leave trigger, and
        // upstream's ENTER/EXIT pair reads as RegionEnter/RegionLeave here.
        if (owner == "CRegion::sm_szTrigName")
        {
            if (name == "EXIT") yield return "REGIONLEAVE";
            if (name == "ENTER") yield return "REGIONENTER";
        }

        // The char table mirrors the item triggers with an ITEM prefix - the same
        // event seen from the character rather than the object (@ItemDropOn_Char and
        // @DropOn_Char). Either spelling satisfies the other.
        if (name.StartsWith("ITEM", StringComparison.Ordinal) && name.Length > 4)
        {
            yield return name[4..];
            yield return Squash(name[4..]);
        }
    }

    private static HashSet<string> OurTriggerNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string n in Enum.GetNames<CharTrigger>()) names.Add(n.ToUpperInvariant());
        foreach (string n in Enum.GetNames<ItemTrigger>()) names.Add(n.ToUpperInvariant());
        return names;
    }

    /// <summary>Names that are not triggers: padding the reference keeps at the head
    /// of an enum so the first real member is index 1.</summary>
    private static readonly HashSet<string> NotTriggers = new(StringComparer.Ordinal)
    {
        "AAAUNUSED",
    };

    [Fact]
    public void ComparingRawNamesOverstatesTheGap()
    {
        string? csv = RepoFile(@"docs\data\sourcex_tables.csv");
        if (Gate.MissingValue(_out, "table export", csv)) return;

        var refTriggers = ReferenceTriggers(csv);
        var ours = OurTriggerNames();

        int rawMissing = refTriggers.Select(t => t.Name).Distinct()
            .Count(n => !ours.Contains(n) && !NotTriggers.Contains(n));
        int mappedMissing = refTriggers
            .Where(t => !NotTriggers.Contains(t.Name))
            .Where(t => !Spellings(t.Owner, t.Name).Any(ours.Contains))
            .Select(t => t.Name).Distinct().Count();

        _out.WriteLine($"raw name comparison: {rawMissing} missing");
        _out.WriteLine($"context/alias mapping: {mappedMissing} missing");

        // The number PLAN-206 is warning about. A raw diff counts SkillSelect twice
        // over and calls both halves absent; the mapped one is the figure worth
        // acting on.
        Assert.True(mappedMissing < rawMissing,
            "the context mapping should resolve names a raw diff calls missing");
    }

    [Fact]
    public void TheRemainingBacklogIsListedByNameAndOwner()
    {
        string? csv = RepoFile(@"docs\data\sourcex_tables.csv");
        if (Gate.MissingValue(_out, "table export", csv)) return;

        var ours = OurTriggerNames();
        var missing = ReferenceTriggers(csv)
            .Where(t => !NotTriggers.Contains(t.Name))
            .Where(t => !Spellings(t.Owner, t.Name).Any(ours.Contains))
            .GroupBy(t => t.Name)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        foreach (var g in missing)
            _out.WriteLine($"  {g.Key,-32} {string.Join(", ", g.Select(x => x.Owner).Distinct())}");
        _out.WriteLine($"backlog: {missing.Length} trigger name(s)");

        // Printed rather than pinned to an exact number: implementing one should not
        // fail a test, and the authority on what actually FIRES stays
        // TriggerCoverageGuardrailTests, which recomputes its set from source every
        // run. What this holds is that the backlog stays small enough to read, so a
        // regression that stopped resolving a whole table would be obvious.
        Assert.InRange(missing.Length, 0, 40);
    }

    [Fact]
    public void EveryReferenceTableIsAccountedFor()
    {
        string? csv = RepoFile(@"docs\data\sourcex_tables.csv");
        if (Gate.MissingValue(_out, "table export", csv)) return;

        var owners = ReferenceTriggers(csv).Select(t => t.Owner).Distinct()
            .OrderBy(o => o, StringComparer.Ordinal).ToArray();
        foreach (string o in owners)
            _out.WriteLine($"{o,-40} prefix='{(ContextPrefix.TryGetValue(o, out var p) ? p : "<UNMAPPED>")}'");

        // A table with no prefix rule would have every one of its names counted as
        // missing, which is how a mapping quietly reverts to a raw diff.
        var unmapped = owners.Where(o => !ContextPrefix.ContainsKey(o)).ToArray();
        Assert.Empty(unmapped);
    }
}
