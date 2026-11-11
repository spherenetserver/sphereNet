using SphereNet.Core.Types;

namespace SphereNet.Core.Collections;

/// <summary>
/// Resource hash table. Maps to CResourceHash in Source-X.
/// Stores CResourceDef entries indexed by ResourceId.
/// </summary>
public sealed class SortedResourceHash<T> where T : class
{
    private readonly SortedDictionary<ulong, T> _entries = [];

    public int Count => _entries.Count;

    /// <summary>Changes whenever an entry is added, replaced or removed, so a caller
    /// that derived something from the table (a per-type list, say) can tell whether
    /// it is still looking at the same contents.</summary>
    public int Version { get; private set; }

    public void Add(ResourceId rid, T entry)
    {
        ulong key = Pack(rid);
        _entries[key] = entry;
        Version++;
    }

    public T? Get(ResourceId rid)
    {
        ulong key = Pack(rid);
        return _entries.GetValueOrDefault(key);
    }

    public bool TryGet(ResourceId rid, out T? entry)
    {
        ulong key = Pack(rid);
        return _entries.TryGetValue(key, out entry);
    }

    public bool Remove(ResourceId rid)
    {
        Version++;
        return _entries.Remove(Pack(rid));
    }

    public bool Contains(ResourceId rid)
    {
        return _entries.ContainsKey(Pack(rid));
    }

    public void Replace(ResourceId rid, T newEntry)
    {
        _entries[Pack(rid)] = newEntry;
        Version++;
    }

    public IEnumerable<T> GetAll() => _entries.Values;

    public void Clear()
    {
        _entries.Clear();
        Version++;
    }

    // The page is part of the key, below type and index so the order stays theirs: a
    // [REGIONTYPE name terrain] block is its own resource beside [REGIONTYPE name]
    // (CResourceHash compares the page after the id, CResourceHash.cpp:20-45).
    private static ulong Pack(ResourceId rid) =>
        ((ulong)(((uint)rid.Type << 24) | ((uint)rid.Index & 0x00FFFFFF)) << 16) | rid.Page;
}
