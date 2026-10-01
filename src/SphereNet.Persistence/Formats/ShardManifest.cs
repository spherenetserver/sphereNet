using SphereNet.Core.Configuration;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Sidecar descriptor written alongside sharded saves. Plain key=value text
/// (<c>FORMAT=</c>, <c>SHARDS=</c>, <c>FILE=</c>) — loads in any editor and
/// one can hand-edit shard lists for recovery. Lives at
/// <c>{base}.manifest</c> (e.g. <c>sphereworld.manifest</c>).
/// From <c>VERSION=2</c> each <c>SIZE=</c> is the shard's exact published length and
/// the loader rejects a shard of any other size; after hand-editing a shard, update or
/// delete its <c>SIZE=</c> line.
/// </summary>
public sealed class ShardManifest
{
    public SaveFormat Format { get; set; } = SaveFormat.Text;
    public int ShardCount { get; set; } = 1;
    public List<string> Files { get; set; } = new();

    public static string PathFor(string savePath, string baseName) =>
        Path.Combine(savePath, baseName + ".manifest");

    public Dictionary<string, long> FileSizes { get; set; } = new();

    /// <summary>The manifest layout this engine writes. Version 2 is the first whose
    /// <c>SIZE=</c> lines are the exact byte length of the shard as published.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Layout version read from <c>VERSION=</c>; 1 when the line is absent.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Whether <see cref="FileSizes"/> can be held against the files on disk.
    /// Manifests written before version 2 measured the file being REPLACED (the
    /// shards were still <c>.tmp</c> when the manifest was written), so their sizes
    /// describe the previous generation and must not reject the current one.</summary>
    public bool SizesAreExact => Version >= 2;

    /// <summary>Write the manifest. A shard's size comes from <see cref="FileSizes"/>
    /// when the caller supplied it - the saver measures the staged shard it is about
    /// to publish - and is otherwise measured from the file at its listed name.</summary>
    public void Save(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var sw = new StreamWriter(fs, leaveOpen: true))
            {
                sw.WriteLine("// SphereNet shard manifest");
                sw.WriteLine($"VERSION={CurrentVersion}");
                sw.WriteLine($"FORMAT={Format}");
                sw.WriteLine($"SHARDS={ShardCount}");
                foreach (var f in Files)
                {
                    if (!FileSizes.TryGetValue(f, out long size))
                    {
                        string fullPath = Path.Combine(dir, f);
                        size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                    }
                    sw.WriteLine($"FILE={f}");
                    sw.WriteLine($"SIZE={size}");
                }
            }
            fs.Flush(flushToDisk: true);
        }
    }

    public static ShardManifest? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var m = new ShardManifest { Files = new List<string>(), Version = 1 };
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            switch (key.ToUpperInvariant())
            {
                case "VERSION":
                    if (int.TryParse(val, out int v)) m.Version = v;
                    break;
                case "FORMAT":
                    if (Enum.TryParse<SaveFormat>(val, ignoreCase: true, out var f))
                        m.Format = f;
                    break;
                case "SHARDS":
                    if (int.TryParse(val, out int n)) m.ShardCount = n;
                    break;
                case "FILE":
                    m.Files.Add(val);
                    break;
                case "SIZE":
                    if (m.Files.Count > 0 && long.TryParse(val, out long sz))
                        m.FileSizes[m.Files[^1]] = sz;
                    break;
            }
        }
        return m;
    }

    /// <summary>Map a UID to its shard index. UID hashes via FNV-1a (fast, no
    /// collisions bias at these counts) so entity placement is deterministic
    /// across runs — a save and the subsequent migration land each object in
    /// the same file.</summary>
    public static int ShardIndexForUid(uint uid, int shardCount)
    {
        if (shardCount <= 1) return 0;
        unchecked
        {
            uint hash = 2166136261;
            hash = (hash ^ (uid & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 8) & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 16) & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 24) & 0xFF)) * 16777619;
            return (int)(hash % (uint)shardCount);
        }
    }
}
