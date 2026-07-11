using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Resolves the Source-X <c>spheretables.scp/[RESOURCES]</c> load manifest.
/// Directory entries include only that directory's direct .scp children;
/// subdirectories are listed explicitly by Source-X to preserve dependency
/// order. Falls back to a deterministic recursive scan when no table exists.
/// </summary>
public static class ScriptResourceManifest
{
    public static IReadOnlyList<string> Resolve(string rootPath, Action<string>? warning = null)
    {
        string root = Path.GetFullPath(rootPath);
        string? table = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("spheretables.scp", StringComparison.OrdinalIgnoreCase));

        if (table == null)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsScriptFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath)
                .ToArray();
        }

        var result = new List<string> { Path.GetFullPath(table) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(table) };

        using var script = new ScriptFile { UseCache = false };
        if (!script.Open(table))
            return result;

        var resources = script.ReadAllSections()
            .FirstOrDefault(section => section.Name.Equals("RESOURCES", StringComparison.OrdinalIgnoreCase));
        if (resources == null)
        {
            warning?.Invoke($"[RESOURCES] section missing in {table}; using only spheretables.scp");
            return result;
        }

        foreach (var key in resources.Keys)
        {
            string entry = key.HasArg ? $"{key.Key} {key.Arg}" : key.Key;
            entry = entry.Trim();
            if (entry.Length == 0)
                continue;

            bool directoryEntry = entry.EndsWith('/') || entry.EndsWith('\\');
            string candidate = Path.GetFullPath(Path.Combine(root,
                entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsUnderRoot(root, candidate))
            {
                warning?.Invoke($"Ignoring [RESOURCES] path outside script root: {entry}");
                continue;
            }

            if (directoryEntry)
            {
                if (!Directory.Exists(candidate))
                {
                    warning?.Invoke($"Script resource directory not found: {entry}");
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(candidate, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsScriptFile)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string full = Path.GetFullPath(file);
                    if (seen.Add(full)) result.Add(full);
                }
            }
            else if (File.Exists(candidate))
            {
                if (seen.Add(candidate)) result.Add(candidate);
            }
            else
            {
                warning?.Invoke($"Script resource file not found: {entry}");
            }
        }

        return result;
    }

    private static bool IsScriptFile(string path) =>
        Path.GetExtension(path).Equals(".scp", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderRoot(string root, string candidate)
    {
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
