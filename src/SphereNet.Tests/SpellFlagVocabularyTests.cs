using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every SPELLFLAG_ name a real pack writes is one the loader maps to a flag.
///
/// An unrecognised token is dropped without a word (DefinitionLoader.cs:967 ORs in
/// nothing and moves on), and a spell that loses a flag does not fail to load - it
/// loads wrong. SPELLFLAG_TARG_CHAR going missing means no target cursor, so the
/// spell self-casts; SPELLFLAG_FIELD going missing means a wall spell puts nothing
/// on the ground.
///
/// Read from the packs and resolved through the real loader, one flag per spell, so
/// a name that maps to nothing shows up as a spell whose Flags came back None.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellFlagVocabularyTests(ITestOutputHelper outp) : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_spf_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    private static readonly Regex FlagToken = new(@"\bSPELLFLAG_[A-Za-z0-9_]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Drop // comments before looking for flags. Both the reference's
    /// sphere.ini and the packs' own defs mention SPELLFLAG_NO_ANIM only in a note
    /// about a config bit - counting that reported a flag nobody writes.</summary>
    private static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            int cut = -1;
            for (int i = 0; i + 1 < line.Length; i++)
            {
                if (line[i] != '/' || line[i + 1] != '/') continue;
                if (i > 0 && line[i - 1] == ':') continue;   // http://
                cut = i;
                break;
            }
            sb.Append(cut < 0 ? line : line[..cut]).Append('\n');
        }
        return sb.ToString();
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

    /// <summary>Names a pack writes that the REFERENCE has no constant for either.
    ///
    /// SPELLFLAG_TARG_ONLYSELF is this shard's own: it appears in seven spells of the
    /// live pack and its inherited copy, and in none of the 33 SPELLFLAG_ constants
    /// the reference defines. The reference engine drops it exactly as this one does,
    /// so those spells already behave the same on both. Giving it a meaning here
    /// would be inventing one.</summary>
    private static readonly HashSet<string> KnownPackInvented =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SPELLFLAG_TARG_ONLYSELF",
        };

    [Fact]
    public void EverySpellFlagARealPackWritesMapsToAFlag()
    {
        var files = PackFiles();
        if (Gate.Missing(outp, "live script pack", files.Count == 0)) return;

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            foreach (Match m in FlagToken.Matches(StripComments(text)))
                names.Add(m.Value.ToUpperInvariant());
        }

        Assert.True(names.Count >= 20, $"expected a real flag variety, saw {names.Count}");

        // One spell per flag, so a dropped token is visible as that spell alone.
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "spellflags.scp");
        var body = new System.Text.StringBuilder();
        var index = new Dictionary<int, string>();
        int id = 400;
        foreach (string name in names)
        {
            body.Append("[SPELL ").Append(id).Append("]\r\n")
                .Append("DEFNAME=s_flagprobe_").Append(id).Append("\r\n")
                .Append("NAME=Flag Probe\r\n")
                .Append("FLAGS=").Append(name).Append("\r\n\r\n");
            index[id] = name;
            id++;
        }
        File.WriteAllText(file, body.ToString());

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        var dropped = new List<string>();
        foreach (var (spellId, name) in index)
        {
            var def = registry.Get((SpellType)spellId);
            if (def == null || def.Flags == SpellFlag.None)
                dropped.Add(name);
        }

        outp.WriteLine($"{names.Count} distinct SPELLFLAG_ names, {dropped.Count} dropped");
        foreach (string d in dropped)
            outp.WriteLine($"DROPPED {d}");

        var surprises = dropped.Where(d => !KnownPackInvented.Contains(d)).OrderBy(d => d).ToList();
        Assert.True(surprises.Count == 0,
            "the loader maps these to nothing, so a spell writing one loses it silently: " +
            string.Join(", ", surprises));

        var nowMapped = KnownPackInvented
            .Where(k => names.Contains(k) && !dropped.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k).ToList();
        Assert.True(nowMapped.Count == 0,
            "these map to something now - take them out of the baseline: " +
            string.Join(", ", nowMapped));
    }
}
