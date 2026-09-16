using System.Globalization;
using System.Text.RegularExpressions;
using SphereNet.Game.Messages;

namespace SphereNet.Tests;

/// <summary>
/// Script and protocol text is matched invariantly or it is matched wrongly.
///
/// .NET's RegexOptions.IgnoreCase follows the CURRENT culture. In Turkish the
/// lower case of 'I' is 'ı' and the upper case of 'i' is 'İ', so under tr-TR a
/// case-insensitive pattern containing I does not match its own lowercase
/// spelling: "FUNCTION" and "function" are not equal, and neither are
/// &lt;NAME_TITLE&gt; and &lt;name_title&gt;. The shard this engine runs runs on a
/// Turkish machine, so this is not a hypothetical.
/// </summary>
public sealed class CultureInvariantMatchingTests
{
    private static T InTurkish<T>(Func<T> body)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try { return body(); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void TheHazardIsRealOnThisRuntime()
    {
        // If this ever stops holding, the rest of the file is still correct but the
        // reason for it has changed and should be re-read.
        bool differs = InTurkish(() =>
            !string.Equals("FUNCTION", "function", StringComparison.CurrentCultureIgnoreCase));
        Assert.True(differs, "tr-TR no longer treats I and i as different letters");

        int cultureAware = InTurkish(() =>
            new Regex("TITLE", RegexOptions.IgnoreCase).Matches("title").Count);
        Assert.Equal(0, cultureAware);
    }

    [Fact]
    public void ALowercaseNameTitleTagIsStillSubstituted()
    {
        string resolved = InTurkish(() => MessageMacros.Resolve(
            "hail <name_title>", MessageMacros.Context.FromCharacter(
                isFemale: false, name: "Yunus", title: "Lord Yunus")));

        Assert.DoesNotContain("<name_title>", resolved, StringComparison.Ordinal);
        Assert.Contains("Lord Yunus", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUppercaseSpellingKeepsWorking()
    {
        string resolved = InTurkish(() => MessageMacros.Resolve(
            "hail <NAME_TITLE>", MessageMacros.Context.FromCharacter(
                isFemale: false, name: "Yunus", title: "Lord Yunus")));

        Assert.Contains("Lord Yunus", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEngineRegexMatchesCaseInsensitivelyByCulture()
    {
        // The guardrail: a new IgnoreCase regex without CultureInvariant is the same
        // bug waiting on whichever keyword happens to contain an I.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        if (Gate.MissingValue(null, "engine source", dir?.FullName)) return;

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}SphereNet.Tests{Path.DirectorySeparatorChar}"))
                continue;

            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"RegexOptions\.[A-Za-z |.]*IgnoreCase[A-Za-z |.]*"))
            {
                if (m.Value.Contains("CultureInvariant", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these match case-insensitively by CURRENT culture, which mis-handles I on a " +
            "Turkish machine: " + string.Join("; ", offenders));
    }
}
