using System.IO.Compression;
using SphereNet.Core.Configuration;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Factory for format-aware save streams. Centralises the extension-to-format
/// mapping and GZip wrapping so WorldSaver / WorldLoader stay agnostic.
/// </summary>
public static class SaveIO
{
    // Cross-file generation stamp identifiers. Item/char shards open with a
    // [SAVEID] record; spheredata records the same save index as SAVECOUNT in its
    // [SPHERE] section. WorldLoader compares them to detect a torn multi-file
    // commit. Shared here so the writer and loader stay in sync.
    public const string SaveIdSection = "SAVEID";
    public const string SaveIdProperty = "ID";
    public const string ServerDataSection = "SPHERE";
    public const string SaveCountProperty = "SAVECOUNT";

    /// <summary>Canonical on-disk extension for each format.</summary>
    public static string ExtensionFor(SaveFormat fmt) => fmt switch
    {
        SaveFormat.Text => ".scp",
        SaveFormat.TextGz => ".scp.gz",
        SaveFormat.Binary => ".sbin",
        SaveFormat.BinaryGz => ".sbin.gz",
        _ => ".scp",
    };

    /// <summary>
    /// Infer the format from a file name by extension. Multi-dot extensions
    /// like <c>.scp.gz</c> are handled before single-dot fallbacks.
    /// </summary>
    public static SaveFormat FormatFromPath(string path)
    {
        // Ignore a trailing rotation/temp suffix so a backup or in-flight temp
        // ("sphereworld.0.sbin.bak3", "spherechars.scp.gz.tmp") still resolves to
        // its real save format instead of defaulting to text (which would read a
        // binary/gzip backup as garbage during boot fallback).
        string lower = StripRotationSuffix(path.ToLowerInvariant());
        if (lower.EndsWith(".scp.gz")) return SaveFormat.TextGz;
        if (lower.EndsWith(".sbin.gz")) return SaveFormat.BinaryGz;
        if (lower.EndsWith(".sbin")) return SaveFormat.Binary;
        return SaveFormat.Text; // .scp or unknown → treat as text
    }

    /// <summary>Strip a trailing <c>.tmp</c> or <c>.bak</c>/<c>.bakN</c> suffix
    /// (rotation/temp markers) so the underlying save extension is visible.</summary>
    internal static string StripRotationSuffix(string lower)
    {
        if (lower.EndsWith(".tmp")) return lower[..^4];
        int bak = lower.LastIndexOf(".bak", StringComparison.Ordinal);
        if (bak >= 0)
        {
            bool allDigits = true;
            for (int i = bak + 4; i < lower.Length; i++)
                if (!char.IsDigit(lower[i])) { allDigits = false; break; }
            if (allDigits) return lower[..bak];
        }
        return lower;
    }

    /// <summary>Open a writer at <paramref name="path"/> using <paramref name="fmt"/>.
    /// The path must already carry the correct extension (call <see cref="ExtensionFor"/>).
    /// <paramref name="sizeMonitor"/> (optional) receives the inner raw FileStream so
    /// callers can poll uncompressed bytes written for size-based shard rolling.</summary>
    public static ISaveWriter OpenWriter(string path, SaveFormat fmt, out FileStream rawStream)
    {
        rawStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: false);
        Stream stream = IsGzip(fmt)
            ? new GZipStream(rawStream, CompressionLevel.Fastest, leaveOpen: false)
            : rawStream;

        return IsBinary(fmt)
            ? new BinarySaveWriter(stream, ownsStream: true)
            : new TextSaveWriter(stream, ownsStream: true);
    }

    /// <summary>Convenience overload without the raw-stream handle — used by
    /// migration/test paths that don't need size monitoring.</summary>
    public static ISaveWriter OpenWriter(string path, SaveFormat fmt) => OpenWriter(path, fmt, out _);

    /// <summary>Open a reader, auto-detecting the format from the file extension.</summary>
    public static ISaveReader OpenReader(string path)
    {
        SaveFormat fmt = FormatFromPath(path);
        Stream raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: false);
        Stream stream = IsGzip(fmt)
            ? new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false)
            : raw;

        return IsBinary(fmt)
            ? new BinarySaveReader(stream, ownsStream: true)
            : new TextSaveReader(stream, ownsStream: true);
    }

    /// <summary>Replace or append the appropriate extension on <paramref name="basePath"/>.
    /// Strips any existing save extension (.scp/.scp.gz/.sbin/.sbin.gz) first so the
    /// resulting path is unique per format.</summary>
    public static string WithExtension(string basePath, SaveFormat fmt)
    {
        string trimmed = StripSaveExtension(basePath);
        return trimmed + ExtensionFor(fmt);
    }

    /// <summary>Remove any known save extension. Used when switching format.</summary>
    public static string StripSaveExtension(string path)
    {
        string lower = path.ToLowerInvariant();
        if (lower.EndsWith(".scp.gz")) return path[..^7];
        if (lower.EndsWith(".sbin.gz")) return path[..^8];
        if (lower.EndsWith(".sbin")) return path[..^5];
        if (lower.EndsWith(".scp")) return path[..^4];
        return path;
    }

    private static bool IsGzip(SaveFormat f) => f == SaveFormat.TextGz || f == SaveFormat.BinaryGz;
    private static bool IsBinary(SaveFormat f) => f == SaveFormat.Binary || f == SaveFormat.BinaryGz;
}
