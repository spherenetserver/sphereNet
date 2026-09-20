using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every EVENTS / TEVENTS reference a real pack writes names a section that exists,
/// and one the trigger dispatch can actually run.
///
/// The reference is resolved against the DEFNAME table before it is labelled with a
/// type ("Do not enforce the restype", CResourceHolder.cpp:99-104), so the name may
/// belong to [EVENTS], [TYPEDEF], [REGIONTYPE] or [FUNCTION] - and across the three
/// packs here it overwhelmingly does NOT mean [EVENTS]: about 3,100 references name a
/// typedef and 18,000 a regiontype, against 2,300 that name an events block. Hashing
/// each name into the EVENTS namespace produced an id no resource answers to, and
/// every consumer skips a reference that resolves to nothing, so those 21,000 lines
/// were loaded and then silently did nothing at all.
///
/// This sweep is what keeps that honest. It is deliberately a STATIC census rather
/// than a load: the question it answers is "what did the pack write", so that the day
/// a pack starts pointing EVENTS at a section kind the resolver does not accept, the
/// count moves here instead of the behaviour quietly disappearing in-game.
/// </summary>
public sealed class ScriptPackEventReferenceCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    /// <summary>Section kinds that carry a trigger body, which is what makes a
    /// reference to one worth resolving. DefinitionLoader.ResolveEventName accepts
    /// exactly these.</summary>
    private static readonly HashSet<string> RunnableKinds =
        new(StringComparer.OrdinalIgnoreCase) { "EVENTS", "TYPEDEF", "REGIONTYPE", "FUNCTION" };

    /// <summary>Names an EVENTS line references that no section in any pack defines.
    /// These resolve to nothing in this engine AND in the reference one - they are
    /// pack mistakes, and the list is here so a NEW one cannot slip in unnoticed.
    ///
    /// A name leaving this set means a pack started defining it, which is worth
    /// knowing; a name joining it means a new dangling reference was written.</summary>
    private static readonly HashSet<string> KnownDangling =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "R_FIGHT_PITS", "R_PLAINS", "T_ANVIL", "T_METEOR_BREATH",
        };

    private static readonly Regex SectionHeader = new(@"^\s*\[\s*([A-Za-z_]+)\s*([^\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DefNameLine = new(@"^\s*DEFNAME\s*=\s*(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EventsLine = new(@"^\s*T?EVENTS\s*=\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ResolveRoot(string root)
    {
        if (Path.IsPathRooted(root)) return root;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "oldSphere")))
            dir = dir.Parent;
        return dir == null ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(dir.FullName, root));
    }

    private static IEnumerable<string> PackFiles()
    {
        foreach (string root in PackRoots)
        {
            string full = ResolveRoot(root);
            if (!Directory.Exists(full)) continue;
            string[] found;
            try { found = Directory.GetFiles(full, "*.scp", SearchOption.AllDirectories); }
            catch (IOException) { continue; }
            foreach (string f in found)
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}_incomplete{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return f;
            }
        }
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

    // The archived 0.56b save references a shard-specific Britain event that is
    // absent from the bundled packs. Preserve the historical fixture and scope
    // this allowance to that exact record; a new live-script R_BRIT typo must fail.
    private static bool IsKnownArchivedReference(string file, string section, string sectionName, string name) =>
        name.Equals("R_BRIT", StringComparison.OrdinalIgnoreCase) &&
        section.Equals("WORLDSCRIPT", StringComparison.OrdinalIgnoreCase) &&
        sectionName.Equals("a_townBritain", StringComparison.OrdinalIgnoreCase) &&
        file.Replace('\\', '/').EndsWith("/oldSphere/scripts/add-on/worldfiles-55a/save/spheredata.scp",
            StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("/repo/oldSphere/scripts/add-on/worldfiles-55a/save/spheredata.scp", "a_townBritain", "R_BRIT", true)]
    [InlineData("/repo/scripts/spheredata.scp", "a_townBritain", "R_BRIT", false)]
    [InlineData("/repo/oldSphere/scripts/add-on/worldfiles-55a/save/spheredata.scp", "a_other", "R_BRIT", false)]
    [InlineData("/repo/oldSphere/scripts/add-on/worldfiles-55a/save/spheredata.scp", "a_townBritain", "R_NEW_TYPO", false)]
    public void ArchivedReferenceAllowanceDoesNotHideNewBrokenReferences(string file, string region, string name, bool expected) =>
        Assert.Equal(expected, IsKnownArchivedReference(file, "WORLDSCRIPT", region, name));

    [Fact]
    public void EveryEventsReferenceNamesASectionThatCanRun()
    {
        // name -> the section kind that defines it; the first definition wins
        var definedAs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knownArchivedRefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in PackFiles())
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }

            string section = "";
            string sectionName = "";
            foreach (string raw in lines)
            {
                string line = StripComment(raw);
                var header = SectionHeader.Match(line);
                if (header.Success)
                {
                    section = header.Groups[1].Value.ToUpperInvariant();
                    string arg = header.Groups[2].Value.Trim();
                    sectionName = arg.Split(' ', '\t')[0];
                    if (arg.Length > 0)
                    {
                        string name = arg.Split(' ', '\t')[0].ToUpperInvariant();
                        if (name.Length > 0 && !definedAs.ContainsKey(name))
                            definedAs[name] = section;
                    }
                    continue;
                }

                var dn = DefNameLine.Match(line);
                if (dn.Success && section.Length > 0)
                {
                    string name = dn.Groups[1].Value.ToUpperInvariant();
                    if (!definedAs.ContainsKey(name)) definedAs[name] = section;
                }

                var ev = EventsLine.Match(line);
                if (!ev.Success) continue;
                foreach (string tok in ev.Groups[1].Value
                             .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries |
                                                     StringSplitOptions.TrimEntries))
                {
                    string name = tok.TrimStart('+', '-').ToUpperInvariant();
                    if (name.Length == 0 || name is "0" or "*") continue;
                    refs[name] = refs.GetValueOrDefault(name) + 1;
                    if (IsKnownArchivedReference(file, section, sectionName, name))
                        knownArchivedRefs[name] = knownArchivedRefs.GetValueOrDefault(name) + 1;
                    firstSeen.TryAdd(name, $"{section} in {Path.GetFileName(file)}");
                }
            }
        }

        if (Gate.Missing(outp, "live script pack", refs.Count == 0)) return;
        int total = refs.Values.Sum();
        Assert.True(total >= 5000, $"expected a real EVENTS surface, saw {total} references");

        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dangling = new List<string>();
        var unrunnable = new List<string>();

        foreach (var (name, count) in refs)
        {
            if (!definedAs.TryGetValue(name, out string? kind))
            {
                byKind["<undefined>"] = byKind.GetValueOrDefault("<undefined>") + count;
                if (!KnownDangling.Contains(name) && count > knownArchivedRefs.GetValueOrDefault(name))
                    dangling.Add($"{name} (x{count}, {firstSeen[name]})");
                continue;
            }
            byKind[kind] = byKind.GetValueOrDefault(kind) + count;
            if (!RunnableKinds.Contains(kind))
                unrunnable.Add($"{name} -> [{kind}] (x{count}, {firstSeen[name]})");
        }

        outp.WriteLine($"{refs.Count} distinct EVENTS/TEVENTS names, {total} references");
        foreach (var (kind, count) in byKind.OrderByDescending(kv => kv.Value))
            outp.WriteLine($"  -> [{kind}] {count}");

        Assert.True(unrunnable.Count == 0,
            "an EVENTS reference names a section kind the resolver does not accept - " +
            "either it should, or the pack means something else: " + string.Join(", ", unrunnable));

        Assert.True(dangling.Count == 0,
            "these EVENTS references name nothing any pack defines, so they do nothing " +
            "here and nothing upstream either: " + string.Join(", ", dangling));

        var nowDefined = KnownDangling.Where(n => definedAs.ContainsKey(n)).ToList();
        Assert.True(nowDefined.Count == 0,
            "these are defined now - take them out of the baseline: " + string.Join(", ", nowDefined));

        // All three families have to be present, or the scan has silently stopped
        // seeing one of them and would no longer notice it breaking.
        Assert.True(byKind.GetValueOrDefault("TYPEDEF") > 500, "typedef references missing from the census");
        Assert.True(byKind.GetValueOrDefault("REGIONTYPE") > 5000, "regiontype references missing from the census");
        Assert.True(byKind.GetValueOrDefault("EVENTS") > 500, "events references missing from the census");
    }
}
