using System.Reflection;

namespace SphereNet.Core.Diagnostics;

/// <summary>
/// Which commit the running binary was built from.
///
/// "Is the server running current code?" has no answer a process can give unless
/// the build says so, and answering it wrongly costs real time: a bug fixed days
/// ago gets hunted again because the shard is still on an older binary. The build
/// stamps the commit, the dirty flag and the build instant into
/// AssemblyInformationalVersion (Directory.Build.props); this reads it back.
///
/// Everything here is best-effort by design: a copy built without git, or from a
/// source archive, reports Unknown rather than refusing to start.
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<(string Commit, bool Dirty, string BuiltUtc, string Raw)> _parsed =
        new(Parse, isThreadSafe: true);

    /// <summary>Full 40-character commit hash, or "" when the build was not stamped.</summary>
    public static string Commit => _parsed.Value.Commit;

    /// <summary>First 12 characters of <see cref="Commit"/>, or "unknown".</summary>
    public static string ShortCommit =>
        Commit.Length >= 12 ? Commit[..12] : (Commit.Length > 0 ? Commit : "unknown");

    /// <summary>True when the working tree had uncommitted tracked changes at build
    /// time — the binary then matches NO commit exactly.</summary>
    public static bool Dirty => _parsed.Value.Dirty;

    /// <summary>Build instant in ISO-8601 UTC, or "" when not stamped.</summary>
    public static string BuiltUtc => _parsed.Value.BuiltUtc;

    /// <summary>The raw informational version, whatever shape it has.</summary>
    public static string Raw => _parsed.Value.Raw;

    /// <summary>Assembly file version (the numeric one), for completeness.</summary>
    public static string AssemblyVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0.0";

    /// <summary>Whether this build is the commit the caller expected. Null when
    /// either side is unknown — "I cannot tell" is a different answer from "no",
    /// and a caller that cannot tell must not report a mismatch.</summary>
    public static bool? Matches(string? expectedCommit)
    {
        if (string.IsNullOrWhiteSpace(expectedCommit) || Commit.Length == 0)
            return null;
        string expected = expectedCommit.Trim();
        if (expected.Length < 7)
            return null;   // too short to be an unambiguous prefix
        return Commit.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (string, bool, string, string) Parse()
    {
        string raw =
            (Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "";

        // "<40 hex>[+dirty] <iso-8601>" — anything else is an unstamped build and
        // is reported as raw text with no commit.
        string commit = "";
        bool dirty = false;
        string built = "";

        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1)
        {
            string head = parts[0];
            if (head.EndsWith("+dirty", StringComparison.Ordinal))
            {
                dirty = true;
                head = head[..^"+dirty".Length];
            }
            if (head.Length == 40 && head.All(Uri.IsHexDigit))
                commit = head.ToLowerInvariant();
            else
                dirty = false;   // not our shape; the flag would be a guess
        }
        if (commit.Length > 0 && parts.Length >= 2)
            built = parts[1];

        return (commit, dirty, built, raw);
    }
}
