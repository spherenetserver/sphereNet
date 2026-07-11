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
        // Source-X FindResourceFile dedups by file TITLE (basename), not the
        // whole path — a same-named file in another directory is a duplicate.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(table) };

        using var script = new ScriptFile { UseCache = false };
        if (!script.Open(table))
            return result;

        // Source-X processes EVERY [RESOURCES] section in the table, not just
        // the first one — packs split the manifest into multiple blocks.
        var resourceSections = script.ReadAllSections()
            .Where(section => section.Name.Equals("RESOURCES", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (resourceSections.Count == 0)
        {
            warning?.Invoke($"[RESOURCES] section missing in {table}; using only spheretables.scp");
            return result;
        }

        foreach (var resources in resourceSections)
        foreach (var key in resources.Keys)
        {
            string entry = key.RawLine.Trim();
            if (entry.Length == 0)
                continue;

            bool directoryEntry = entry.EndsWith('/') || entry.EndsWith('\\');
            // Source-X AddResourceFile appends the .scp extension when missing.
            if (!directoryEntry && !Path.HasExtension(entry))
                entry += ".scp";
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

                // Source-X AddResourceDir sorts by case-SENSITIVE filename.
                foreach (string file in Directory.EnumerateFiles(candidate, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsScriptFile)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    string full = Path.GetFullPath(file);
                    if (seen.Add(Path.GetFileName(full))) result.Add(full);
                }
            }
            else if (File.Exists(candidate))
            {
                if (seen.Add(Path.GetFileName(candidate))) result.Add(candidate);
            }
            else
            {
                warning?.Invoke($"Script resource file not found: {entry}");
            }
        }

        // Source-X AddResourceDir(m_sSCPBaseDir): any top-level *.scp not named
        // by the table is still loaded, after everything listed.
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(IsScriptFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string full = Path.GetFullPath(file);
            if (seen.Add(Path.GetFileName(full))) result.Add(full);
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
