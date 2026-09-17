using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every TAG.OVERRIDE.&lt;key&gt; a real pack writes is one the engine reads.
///
/// This family is the cleanest example of the bug class that keeps turning up: the
/// engine acts on a value, the pack sets it, and nothing connects the two. An
/// unread OVERRIDE key looks exactly like a working one from the script side - the
/// line loads, the tag is readable, and the behaviour is simply the default.
///
/// Reading the engine source is not enough to answer "does it read this key",
/// because some are built by concatenation - OVERRIDE.PracticeMax.SKILL_&lt;n&gt;
/// appears as a literal in neither engine - so the set the engine honours is written
/// out here by hand and the test is the thing that keeps it honest against the packs.
/// </summary>
public sealed class OverrideTagCoverageTests(ITestOutputHelper outp)
{
    private static readonly string[] PackRoots =
    [
        @"C:\sphereNetServer",
        "oldSphere/scripts",
        "oldSphere/Scripts-X-main",
    ];

    /// <summary>OVERRIDE keys this engine acts on. A key LEAVING this set means a
    /// consumer was removed, which the pack lines then silently stop reaching.</summary>
    private static readonly HashSet<string> Honoured =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DAMAGETYPE", "MAPHEIGHT", "MAPWIDTH", "MAXITEMS", "NOREVEALSPEAK", "NOTO",
            "NPCAI", "REQSTR", "SHOVE", "SKILL", "SKILLSUM", "SPEED", "SPIDERWEB",
            "TRAINSKILLCOST", "TRAINSKILLMAX", "TRAINSKILLMAXPERCENT",
            // Per-skill, built by concatenation on both sides.
            "PRACTICEMAX",
        };

    /// <summary>Keys a pack writes that neither engine reads. ROCK appears nowhere in
    /// the reference source either - it is the pack's own tag, read by the pack's own
    /// scripts, and adding an engine consumer for it would be inventing behaviour. The
    /// list exists so a NEW unread key cannot slip in beside it unnoticed.
    ///
    /// (SOUND_MISS is another, but it lives only in an _incomplete file, which the
    /// loader skips and this sweep skips with it.)</summary>
    private static readonly HashSet<string> PackOwned =
        new(StringComparer.OrdinalIgnoreCase) { "ROCK" };

    private static readonly Regex OverrideTag = new(
        @"\bTAG\.OVERRIDE\.([A-Za-z_][A-Za-z0-9_]*)",
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

    [Fact]
    public void EveryOverrideKeyAPackWritesIsOneTheEngineReads()
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in PackRoots)
        {
            string full = ResolveRoot(root);
            if (!Directory.Exists(full)) continue;
            string[] files;
            try { files = Directory.GetFiles(full, "*.scp", SearchOption.AllDirectories); }
            catch (IOException) { continue; }

            foreach (string file in files)
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}_incomplete{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (IOException) { continue; }

                foreach (string raw in lines)
                {
                    var m = OverrideTag.Match(StripComment(raw));
                    if (!m.Success) continue;
                    string key = m.Groups[1].Value.ToUpperInvariant();
                    used[key] = used.GetValueOrDefault(key) + 1;
                    firstSeen.TryAdd(key, Path.GetFileName(file));
                }
            }
        }

        if (Gate.Missing(outp, "live script pack", used.Count == 0)) return;

        foreach (var (key, count) in used.OrderByDescending(kv => kv.Value))
            outp.WriteLine($"{count,6}  TAG.OVERRIDE.{key}  ({firstSeen[key]})");

        // Live lines only: the shipped packs carry several hundred COMMENTED
        // TAG.OVERRIDE.SPEED lines, and counting those would make the sweep look
        // healthier than it is.
        Assert.True(used.Values.Sum() >= 50,
            $"expected a real OVERRIDE surface, saw {used.Values.Sum()} lines");

        var unread = used.Keys
            .Where(k => !Honoured.Contains(k) && !PackOwned.Contains(k))
            .Select(k => $"{k} (x{used[k]}, {firstSeen[k]})")
            .ToList();

        Assert.True(unread.Count == 0,
            "a pack sets these and the engine reads none of them - check the reference " +
            "source before adding a consumer, some are the pack's own: " + string.Join(", ", unread));

        // And the other direction: a key listed as pack-owned that the engine started
        // honouring, or a honoured key no pack writes any more, is worth knowing about.
        var nowHonoured = PackOwned.Where(Honoured.Contains).ToList();
        Assert.True(nowHonoured.Count == 0,
            "these are honoured now - take them out of the pack-owned list: " +
            string.Join(", ", nowHonoured));
    }
}
