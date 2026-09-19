using System.Reflection;
using SphereNet.Core.Configuration;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A note in sphere.ini that says a setting has no effect must be telling the truth.
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
public sealed class IniNoteAccuracyTests(ITestOutputHelper outp)
{
    /// <summary>Phrases the file uses to say "this value is read but does nothing".</summary>
    private static readonly string[] InertClaims =
    [
        "yapm\u0131yor", "uygulam\u0131yor", "etkilemiyor",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void NoSettingIsDocumentedAsInertWhileTheEngineUsesIt()
    {
        string root = RepoRoot();
        string ini = Path.Combine(root, "config", "sphere.ini");
        if (Gate.Missing(outp, "config/sphere.ini", !File.Exists(ini)))
            return;

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

        var lines = File.ReadAllLines(ini);
        var wrong = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string note = lines[i].Trim();
            if (!note.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!InertClaims.Any(c => note.Contains(c, StringComparison.OrdinalIgnoreCase)))
                continue;

            // The key this note belongs to is the next non-comment assignment.
            string? key = null;
            for (int j = i + 1; j < lines.Length && j < i + 8; j++)
            {
                string t = lines[j].Trim();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.Length == 0) continue;
                int eq = t.IndexOf('=');
                if (eq > 0) key = t[..eq].Trim();
                break;
            }
            if (key == null || !props.TryGetValue(key, out var prop)) continue;

            string usage = "_config." + prop.Name;
            if (sources.Any(src => src.Contains(usage, StringComparison.Ordinal)))
                wrong.Add($"{key}: the note says it has no effect, but {usage} is read by the engine");
        }

        Assert.True(wrong.Count == 0,
            "sphere.ini claims these do nothing, and the engine uses them: " +
            string.Join(" ; ", wrong));
    }
}
