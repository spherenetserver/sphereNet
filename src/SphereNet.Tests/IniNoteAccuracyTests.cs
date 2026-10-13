using System.Reflection;
using System.Text.RegularExpressions;
using SphereNet.Core.Configuration;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A note in sphere.ini or the configuration reference (docs/CONFIG-EN.md,
/// docs/CONFIG-TR.md) that says a setting has no effect must be telling the truth.
///
/// Three of them were not. CLIENTMAXIP said NetworkManager does not check per-IP
/// connections, and it does. DEADCANNOTSEELIVING said the filter is not applied, and it
/// is. MAXPACKETSPERTICK said the packet limit is not applied - and because a reader
/// believed that, the shipped config left it at 0, which turned the packet quota and the
/// flood detection built on it off on every shard that used the file.
///
/// A wrong note in this direction is worse than no note: it tells an operator that a
/// value does not matter, so nobody looks at it again. This checks the claim mechanically
/// against the code, so the notes cannot drift back.
/// </summary>
public sealed class IniNoteAccuracyTests
{
    /// <summary>Phrases the files use to say "this value is read but does nothing".</summary>
    private static readonly string[] InertClaims =
    [
        "yapmıyor", "uygulamıyor", "etkilemiyor",
        "has no effect", "nothing reads", "does not affect",
    ];

    [Fact]
    public void NoSettingIsDocumentedAsInertWhileTheEngineUsesIt()
    {
        string root = TestRepo.Root;

        var props = typeof(SphereConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Everything the engine is built from, minus the config project itself: if a
        // property is read here, it is not inert.
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(p => !p.Contains("SphereNet.Tests", StringComparison.OrdinalIgnoreCase) &&
                        !p.EndsWith("SphereConfig.cs", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains(Path.Combine("obj"), StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToList();

        var claims = new List<(string Key, string Where)>();
        claims.AddRange(IniClaims(TestRepo.Tracked("config/sphere.ini")));
        claims.AddRange(DocClaims(TestRepo.Tracked("docs/CONFIG-EN.md")));
        claims.AddRange(DocClaims(TestRepo.Tracked("docs/CONFIG-TR.md")));

        // A setting copied into an engine static that nothing reads is inert although
        // "_config.X" appears in source. Each entry names that static, and stays only
        // while the static has no reader besides its declaration and that assignment.
        foreach (var (key, member) in ForwardedButUnused)
        {
            int uses = sources.Sum(src => Regex.Matches(src, @"\b" + member + @"\b").Count);
            Assert.True(uses <= 2, $"{key}: {member} is now read by the engine; its inert note is wrong");
        }

        var wrong = new List<string>();
        foreach (var (key, where) in claims)
        {
            if (ForwardedButUnused.ContainsKey(key)) continue;
            if (!props.TryGetValue(key, out var prop)) continue;
            string usage = "_config." + prop.Name;
            if (sources.Any(src => src.Contains(usage, StringComparison.Ordinal)))
                wrong.Add($"{key} ({where}): the note says it has no effect, but {usage} is read by the engine");
        }

        Assert.True(wrong.Count == 0,
            "these notes claim a setting does nothing, and the engine uses it: " +
            string.Join(" ; ", wrong.Distinct()));
    }

    /// <summary>Key -> the engine static it is copied into. ITEMDEFAULTHITS is assigned
    /// to CombatEngine.DefaultHits at wiring time and nothing reads that.</summary>
    private static readonly Dictionary<string, string> ForwardedButUnused =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ITEMDEFAULTHITS"] = "DefaultHits",
        };

    private static bool IsClaim(string text) =>
        InertClaims.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>In the ini a note belongs to the next non-comment assignment.</summary>
    private static IEnumerable<(string, string)> IniClaims(string ini)
    {
        var lines = File.ReadAllLines(ini);
        for (int i = 0; i < lines.Length; i++)
        {
            string note = lines[i].Trim();
            if (!note.StartsWith("//", StringComparison.Ordinal) || !IsClaim(note))
                continue;
            for (int j = i + 1; j < lines.Length && j < i + 8; j++)
            {
                string t = lines[j].Trim();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.Length == 0) continue;
                int eq = t.IndexOf('=');
                if (eq > 0) yield return (t[..eq].Trim(), "sphere.ini");
                break;
            }
        }
    }

    /// <summary>In the reference a note belongs to every key its "### KEY" heading names.</summary>
    private static IEnumerable<(string, string)> DocClaims(string doc)
    {
        string name = Path.GetFileName(doc);
        string[] keys = [];
        foreach (string raw in File.ReadAllLines(doc))
        {
            if (raw.StartsWith("### ", StringComparison.Ordinal))
            {
                keys = ConfigReferenceDocsTests.HeadingKeys(raw[4..]).ToArray();
                continue;
            }
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                keys = [];
                continue;
            }
            if (!IsClaim(raw)) continue;
            foreach (string key in keys)
                yield return (key, name);
        }
    }
}
