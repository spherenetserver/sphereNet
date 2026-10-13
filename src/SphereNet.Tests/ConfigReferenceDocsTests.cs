using System.Text.RegularExpressions;

namespace SphereNet.Tests;

/// <summary>
/// config/sphere.ini carries one line per key; the full description of every key lives
/// in docs/CONFIG-EN.md and docs/CONFIG-TR.md under a "### KEY" heading. These keep the
/// three files describing the same set of keys: a key added to the ini without a
/// reference entry, an entry left behind for a key the ini no longer has, or an entry
/// written in one language only, fails here.
/// </summary>
public sealed class ConfigReferenceDocsTests
{
    /// <summary>The keys a "### A / B" heading names, upper-cased. Parts that are not a
    /// key name (a "[MYSQL &lt;name&gt;]" section heading) are not keys.</summary>
    internal static IEnumerable<string> HeadingKeys(string heading) =>
        Regex.Split(heading, @"\s*[/,]\s*")
            .Select(k => k.Trim().Trim('`').ToUpperInvariant())
            .Where(k => Regex.IsMatch(k, "^[A-Z][A-Z0-9_]*$"));

    private static HashSet<string> DocKeys(string relative)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(TestRepo.Tracked(relative)))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
                keys.UnionWith(HeadingKeys(line[4..]));
        }
        return keys;
    }

    /// <summary>Keys the ini sets: every non-comment KEY=value line.</summary>
    private static HashSet<string> IniSetKeys(string ini)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in ini.Split('\n'))
        {
            string t = raw.Trim();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            var m = Regex.Match(t, @"^([A-Za-z0-9_]+)\s*=");
            if (m.Success) keys.Add(m.Groups[1].Value.ToUpperInvariant());
        }
        return keys;
    }

    [Fact]
    public void EveryKeyTheIniSetsIsInBothReferences()
    {
        string ini = File.ReadAllText(TestRepo.Tracked("config/sphere.ini"));
        var en = DocKeys("docs/CONFIG-EN.md");
        var tr = DocKeys("docs/CONFIG-TR.md");

        var missing = IniSetKeys(ini)
            .SelectMany(k => new[] { en.Contains(k) ? null : $"{k} (CONFIG-EN)", tr.Contains(k) ? null : $"{k} (CONFIG-TR)" })
            .OfType<string>()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "no reference entry for: " + string.Join(", ", missing));
    }

    [Fact]
    public void BothReferencesDescribeTheSameKeys()
    {
        var en = DocKeys("docs/CONFIG-EN.md");
        var tr = DocKeys("docs/CONFIG-TR.md");

        Assert.Empty(en.Except(tr).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(tr.Except(en).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryReferenceEntryNamesAKeyTheIniCarries()
    {
        // Set or shown as a disabled "// KEY=value" line the operator can uncomment.
        string ini = File.ReadAllText(TestRepo.Tracked("config/sphere.ini"));
        var stale = DocKeys("docs/CONFIG-EN.md")
            .Where(k => !Regex.IsMatch(ini, @"^\s*(//\s*)?" + Regex.Escape(k) + @"\s*=",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.True(stale.Length == 0, "reference entries for keys sphere.ini does not carry: " +
            string.Join(", ", stale));
    }
}
