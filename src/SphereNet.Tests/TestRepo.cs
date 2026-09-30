namespace SphereNet.Tests;

/// <summary>
/// The one place the suite finds the repository root.
///
/// The test assembly runs from src/SphereNet.Tests/bin/&lt;Configuration&gt;/&lt;tfm&gt;/,
/// so a fixed number of ".." hops is only right for one layout - four of them
/// land on src/, not the root, and every path derived from that silently points
/// at nothing. Walking up to the solution file is layout-independent.
///
/// Two kinds of data hang off the root:
///  - tracked data (tests/fixtures, docs, config, src): part of every checkout,
///    so <see cref="Tracked"/> throws when it is missing - a test that cannot
///    find its own fixtures must fail, not pass having measured nothing;
///  - optional reference data (oldSphere/ is gitignored, private server dirs):
///    <see cref="Optional"/> returns null when absent so the caller can gate
///    it with <see cref="Gate"/> and stay CI-safe.
/// </summary>
public static class TestRepo
{
    private static readonly string[] SolutionFiles = { "SphereNet.slnx", "sphereNet.sln" };

    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root (the directory holding the solution file).</summary>
    public static string Root => RootPath.Value;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (string sln in SolutionFiles)
                if (File.Exists(Path.Combine(dir.FullName, sln)))
                    return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Repository root not found: no {string.Join(" / ", SolutionFiles)} above {AppContext.BaseDirectory}");
    }

    /// <summary>Root-relative path, no existence check.</summary>
    public static string PathOf(string relative)
        => Path.GetFullPath(Path.Combine(Root, Normalize(relative)));

    /// <summary>A file or directory checked into the repository. Throws when it
    /// is missing: a tracked fixture that cannot be found is a broken test.</summary>
    public static string Tracked(string relative)
    {
        string path = PathOf(relative);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException(
                $"Tracked repository path '{relative}' is missing (looked in {path})", path);
        return path;
    }

    /// <summary>Optional, untracked data (the gitignored oldSphere/ reference
    /// tree, private packs). Null when absent. A git worktree nested inside the
    /// main checkout has no copy of the gitignored tree, so the ancestors of the
    /// root are probed after the root itself.</summary>
    public static string? Optional(string relative)
    {
        string rel = Normalize(relative);
        var dir = new DirectoryInfo(Root);
        while (dir != null)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir.FullName, rel));
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string Normalize(string relative)
        => relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
}
