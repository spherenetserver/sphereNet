using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which ITEMDEF and CHARDEF keys a real pack writes that no parser case names.
///
/// These are the two biggest definition surfaces in a pack - a hundred item files
/// and eighteen NPC files here. CHARDEF has a case for all 54 keys the packs use;
/// ITEMDEF has none for 35 of its 99, and those are recorded by
/// UnknownKeyDiagnostics, which the loader logs a top-ten summary of and nothing
/// else has ever looked at.
///
/// Unnamed is NOT lost, and the difference is the point of this test's baseline.
/// The parser's default branch stores the key in the definition's TagDefs
/// (ItemDef.cs:207) and the suit aggregations read def tags as well as instance tags
/// (CombatEngine.GetItemNumProperty), so the AOS families the packs write on gear -
/// the five resists, the five damage types, the three regens, LUCK, NIGHTSIGHT -
/// arrive and work. ItemDefTagFallbackTests holds that half down. The rest carry no
/// engine behaviour anywhere, and a pack reading one back gets what it wrote.
///
/// So this measures one thing only: whether a key has a typed parser case. The keys
/// are fed through the REAL parser one at a time, because the AOS families are
/// matched by a guard clause rather than a case label and reading the switch by eye
/// would call every one of them missing.
/// </summary>
public sealed class DefinitionKeyCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    private static readonly Regex SectionHeader = new(@"^\s*\[\s*([A-Za-z_]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TriggerHeader = new(@"^\s*ON\s*=\s*@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex KeyLine = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Keys with no typed parser case, each reaching the engine as a
    /// definition TAG instead.
    ///
    /// Three groups. The AOS suit families - RES*, DAM*, REGEN*, LUCK, NIGHTSIGHT -
    /// are read straight off def tags by the combat aggregations, so they work as
    /// written; ItemDefTagFallbackTests proves it for the resists. USESMAX and PILE
    /// have consumers on the same footing. The remainder - SELFREPAIR, LOWERREQ,
    /// RARITY, USEBESTWEAPONSKILL, the BONUSSKILL pairs, DEFNAME2, EXPANSION - have
    /// no engine behaviour behind them at all, so a typed field would be storage
    /// nobody consults. NAMELOC, CATEGORY, SUBSECTION and DESCRIPTION are
    /// house-placement menu metadata, and COMPONENT and MULTIREGION belong to a
    /// MULTIDEF written with an ITEMDEF header.
    ///
    /// A name LEAVING this set means it gained a typed case, which is worth
    /// noticing; a name joining it means a pack started writing something new.</summary>
    private static readonly HashSet<string> KnownUnparsed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ITEMDEF.BONUSSKILL1", "ITEMDEF.BONUSSKILL1AMT", "ITEMDEF.BONUSSKILL2",
            "ITEMDEF.BONUSSKILL2AMT", "ITEMDEF.BONUSSKILL3", "ITEMDEF.BONUSSKILL3AMT",
            "ITEMDEF.CATEGORY", "ITEMDEF.COMPONENT", "ITEMDEF.DAMCOLD",
            "ITEMDEF.DAMENERGY", "ITEMDEF.DAMFIRE", "ITEMDEF.DAMPHYSICAL",
            "ITEMDEF.DAMPOISON", "ITEMDEF.DEFNAME2", "ITEMDEF.DESCRIPTION",
            "ITEMDEF.EXPANSION", "ITEMDEF.LOWERREQ", "ITEMDEF.LUCK",
            "ITEMDEF.MULTIREGION", "ITEMDEF.NAMELOC", "ITEMDEF.NIGHTSIGHT",
            "ITEMDEF.PILE", "ITEMDEF.RARITY", "ITEMDEF.REGENHITS",
            "ITEMDEF.REGENMANA", "ITEMDEF.REGENSTAM", "ITEMDEF.RESCOLD",
            "ITEMDEF.RESENERGY", "ITEMDEF.RESFIRE", "ITEMDEF.RESPHYSICAL",
            "ITEMDEF.RESPOISON", "ITEMDEF.SELFREPAIR", "ITEMDEF.SUBSECTION",
            "ITEMDEF.USEBESTWEAPONSKILL", "ITEMDEF.USESMAX",
        };

    private static string ResolveRoot(string root)
    {
        if (Path.IsPathRooted(root)) return root;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(dir.FullName, root));
    }

    private static List<string> PackFiles()
    {
        var all = new List<string>();
        foreach (string root in PackRoots)
        {
            string full = ResolveRoot(root);
            if (!Directory.Exists(full)) continue;
            try
            {
                all.AddRange(Directory.EnumerateFiles(full, "*.scp", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_incomplete{Path.DirectorySeparatorChar}",
                                            StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
        }
        return all;
    }

    private static string StripComment(string line)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] != '/' || line[i + 1] != '/') continue;
            if (i > 0 && line[i - 1] == ':') continue;
            return line[..i];
        }
        return line;
    }

    /// <summary>The keys a pack writes directly in a section body - not inside an
    /// ON=@Trigger block, where the lines are script rather than definition.</summary>
    private static SortedSet<string> BodyKeysOf(string sectionName)
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in PackFiles())
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }

            bool inside = false, inTrigger = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = StripComment(raw).TrimEnd('\r');
                var header = SectionHeader.Match(line);
                if (header.Success)
                {
                    inside = header.Groups[1].Value.Equals(sectionName, StringComparison.OrdinalIgnoreCase);
                    inTrigger = false;
                    continue;
                }
                if (!inside) continue;
                if (TriggerHeader.IsMatch(line)) { inTrigger = true; continue; }
                if (inTrigger) continue;

                var m = KeyLine.Match(line);
                if (m.Success) keys.Add(m.Groups[1].Value.ToUpperInvariant());
            }
        }
        return keys;
    }

    [Fact]
    public void EveryItemdefKeyARealPackWritesIsOneTheParserKnows()
    {
        RunFor("ITEMDEF", (key, value) => new ItemDef(new ResourceId(ResType.ItemDef, 0x0EED)).LoadFromKey(key, value));
    }

    [Fact]
    public void EveryChardefKeyARealPackWritesIsOneTheParserKnows()
    {
        RunFor("CHARDEF", (key, value) => new CharDef(new ResourceId(ResType.CharDef, 0x0190)).LoadFromKey(key, value));
    }

    private void RunFor(string sectionName, Action<string, string> feed)
    {
        var keys = BodyKeysOf(sectionName);
        if (Gate.Missing(outp, "live script pack", keys.Count == 0)) return;
        Assert.True(keys.Count >= 30, $"expected a real {sectionName} key variety, saw {keys.Count}");

        var unparsed = new List<string>();
        foreach (string key in keys)
        {
            // TAG.* is a namespace by design and DEFNAME names the section itself.
            if (key.StartsWith("TAG", StringComparison.OrdinalIgnoreCase)) continue;

            UnknownKeyDiagnostics.Clear();
            try { feed(key, "1"); } catch { /* a parse that throws still knew the key */ }
            if (UnknownKeyDiagnostics.TotalDropped > 0)
                unparsed.Add(key);
        }
        UnknownKeyDiagnostics.Clear();

        outp.WriteLine($"{sectionName}: {keys.Count} distinct body keys, {unparsed.Count} unparsed");
        foreach (string u in unparsed) outp.WriteLine($"UNPARSED {sectionName}.{u}");

        var surprises = unparsed.Where(u => !KnownUnparsed.Contains($"{sectionName}.{u}")).ToList();
        Assert.True(surprises.Count == 0,
            $"the {sectionName} parser has no case for these - check whether anything " +
            "reads them off the def tags before adding one: " + string.Join(", ", surprises));

        var nowKnown = KnownUnparsed
            .Where(k => k.StartsWith(sectionName + ".", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(sectionName.Length + 1)..])
            .Where(k => keys.Contains(k) && !unparsed.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.True(nowKnown.Count == 0,
            "these parse now - take them out of the baseline: " + string.Join(", ", nowKnown));
    }
}
