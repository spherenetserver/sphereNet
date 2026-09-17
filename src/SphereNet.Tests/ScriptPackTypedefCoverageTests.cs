using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Scripting.Definitions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every item TYPE a pack names that the REFERENCE knows is one this engine knows.
///
/// ParseItemType answers ItemType.Normal for any name it cannot match
/// (ItemDef.cs:390) - silently, so a type this engine is missing looks exactly like
/// an ordinary item. Of the hundred t_ names the three packs declare a [TYPEDEF]
/// for, sixty-three resolve to Normal; the alarming-looking number is why this test
/// compares against the REFERENCE rather than counting. Every one of those
/// sixty-three is a pack invention with no IT_ constant behind it, so the reference
/// engine resolves them to IT_NORMAL too, and their typedefs still run because
/// script-side typedef dispatch keys on the ITEMDEF's raw TYPE text rather than on
/// the parsed enum (TriggerDispatcher.cs:512).
///
/// What would be a real gap is the other direction: a name the reference has a type
/// for and this engine does not. That is what is asserted.
///
/// Resolved by asking the REAL parser, not by matching the enum by eye: the rule is
/// strip t_, drop underscores, match case-insensitively, so t_weapon_sword is
/// WeaponSword and a hand comparison gets it wrong.
/// </summary>
public sealed class ScriptPackTypedefCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    private static readonly Regex TypedefHeader = new(
        @"^\s*\[\s*TYPEDEF\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Names a pack declares a TYPEDEF for that are not engine item types
    /// at all. The packs use TYPEDEF as a place to hang a named EVENTS-like block -
    /// "ei_equipitem" is not a t_ type and was never meant to be one - so these are
    /// not gaps; a name only matters here if it claims to be a t_ type.</summary>
    private static bool IsItemTypeName(string name) =>
        name.StartsWith("t_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reference's own item-type vocabulary, read from its headers.
    /// A type exists there as an IT_ constant.</summary>
    private static HashSet<string>? ReferenceTypes()
    {
        string root = ResolveRoot("oldSphere/Source-X-full/src");
        if (!Directory.Exists(root)) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itName = new Regex(@"IT_[A-Z0-9_]+", RegexOptions.CultureInvariant);
        foreach (string f in Directory.EnumerateFiles(root, "*.h", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            foreach (Match m in itName.Matches(text))
                names.Add(m.Value);
        }
        return names;
    }

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

    [Fact]
    public void EveryTypedefTypeARealPackDeclaresIsOneTheEngineKnows()
    {
        var files = PackFiles();
        if (Gate.Missing(outp, "live script pack", files.Count == 0)) return;

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            foreach (Match m in TypedefHeader.Matches(text))
                seen.TryAdd(m.Groups[1].Value, Path.GetFileName(f));
        }

        var reference = ReferenceTypes();
        if (Gate.Missing(outp, "Source-X reference tree", reference == null)) return;

        var missing = new List<(string Name, string File)>();
        int packInvented = 0, recognised = 0;
        foreach (var (name, file) in seen)
        {
            if (!IsItemTypeName(name)) continue;
            if (name.Equals("t_normal", StringComparison.OrdinalIgnoreCase)) continue;

            if (ItemDef.ParseTypeName(name) != ItemType.Normal) { recognised++; continue; }

            // Unrecognised here. Does the REFERENCE have a type by that name?
            string constant = "IT_" + name[2..].ToUpperInvariant();
            if (reference!.Contains(constant)) missing.Add((name, file));
            else packInvented++;
        }

        outp.WriteLine($"{files.Count} files, {seen.Count} distinct TYPEDEF names, " +
                       $"{recognised} recognised, {packInvented} pack-invented " +
                       $"(the reference resolves those to IT_NORMAL too), " +
                       $"{missing.Count} the reference has and this engine does not");
        foreach (var (name, file) in missing.OrderBy(u => u.Name))
            outp.WriteLine($"MISSING {name} (first: {file})");

        Assert.True(seen.Count >= 20, $"expected a real pack typedef variety, saw {seen.Count}");
        Assert.True(recognised >= 20, $"expected the enum to answer real types, saw {recognised}");
        // The reference list has to have loaded, or the comparison is vacuous - and
        // it silently was: a mangled pattern made it empty, every name looked
        // pack-invented and the real assertion passed on nothing. A total failure is
        // caught by the sentinel, a partial one by the size.
        Assert.True(reference!.Contains("IT_NORMAL"), "the reference type list did not load");
        Assert.True(reference.Count >= 150,
            $"the reference type list looks partial: {reference.Count} names");

        Assert.True(missing.Count == 0,
            "the reference has a type for these and this engine resolves them to Normal, " +
            "so every engine branch keyed on the type treats them as ordinary items: " +
            string.Join(", ", missing.Select(m => m.Name).OrderBy(n => n)));
    }
}
