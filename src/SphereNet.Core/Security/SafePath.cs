namespace SphereNet.Core.Security;

/// <summary>Resolves an untrusted relative path while keeping it below a trusted root.</summary>
public static class SafePath
{
    public static bool TryResolveUnderRoot(
        string root,
        string candidate,
        out string fullPath,
        out string? error,
        bool allowRoot = false,
        bool rejectReparsePoints = true)
    {
        fullPath = "";
        error = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "Root path is not configured";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Path is required";
            return false;
        }

        try
        {
            if (Path.IsPathFullyQualified(candidate))
            {
                error = "Absolute paths are not allowed";
                return false;
            }

            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string resolved = Path.GetFullPath(Path.Combine(rootFull, candidate));
            string relative = Path.GetRelativePath(rootFull, resolved);

            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                error = "Path is outside the allowed root";
                return false;
            }

            if (!allowRoot && relative.Equals(".", StringComparison.Ordinal))
            {
                error = "Path must identify a child of the allowed root";
                return false;
            }

            if (rejectReparsePoints && ContainsReparsePoint(rootFull, relative))
            {
                error = "Symbolic links and reparse points are not allowed";
                return false;
            }

            fullPath = resolved;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = "Invalid path";
            return false;
        }
    }

    private static bool ContainsReparsePoint(string rootFull, string relative)
    {
        string current = rootFull;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }
}
