namespace SphereNet.Persistence.Formats;

/// <summary>
/// Write side of the save/load abstraction. Both the classic Sphere
/// <c>.scp</c> text format and the binary tag-stream format implement this —
/// WorldSaver is oblivious to which one is in use.
/// </summary>
public interface ISaveWriter : IDisposable
{
    /// <summary>Begin one record (Source-X section marker). The text writer
    /// emits <c>[SECTION]</c>; the binary writer emits a framed entry header.</summary>
    void BeginRecord(string section);

    /// <summary>Write one property inside the current record. Values are
    /// UTF-8 in both formats — no type information is carried because the
    /// loader re-interprets them via the existing per-field parsers.</summary>
    void WriteProperty(string key, string value);

    /// <summary>A value already formatted into a buffer. Writers that pack bytes
    /// override it to encode the characters directly; the rest get a string.</summary>
    void WriteProperty(string key, ReadOnlySpan<char> value) => WriteProperty(key, value.ToString());

    /// <summary>A number, written in invariant decimal - what <c>ToString()</c> on
    /// it gave. Packing writers format it straight into their buffer.</summary>
    void WriteProperty(string key, long value) =>
        WriteProperty(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>An interpolated value (<c>$"0{uid:x8}"</c>) formatted into a pooled
    /// buffer instead of a fresh string. The capture holds the world still while it
    /// runs, and a string per property per object was most of what it allocated.</summary>
    void WriteProperty(string key, ref SaveValueBuilder value)
    {
        try { WriteProperty(key, value.Text); }
        finally { value.Dispose(); }
    }

    /// <summary><see cref="BeginRecord(string)"/> for a section built by
    /// interpolation (<c>$"WORLDITEM {defname}"</c>).</summary>
    void BeginRecord(ref SaveValueBuilder section)
    {
        try { BeginRecord(section.Text); }
        finally { section.Dispose(); }
    }

    /// <summary>A section already formatted into a buffer.</summary>
    void BeginRecord(ReadOnlySpan<char> section) => BeginRecord(section.ToString());

    /// <summary>Close the current record (text: logical section end, binary: frame end).
    /// Text writers emit the file-level <c>[EOF]</c> marker once on disposal.</summary>
    void EndRecord();

    /// <summary>Optional file-level comment. No-op in binary.</summary>
    void WriteHeaderComment(string line);

    /// <summary>Flush buffered writes to the underlying stream.</summary>
    void Flush();

    /// <summary>Approximate byte count written through this writer. Tracked
    /// at the writer layer (not the file stream) so it stays accurate even
    /// when an inner StreamWriter / GZipStream buffer delays disk writes.
    /// Used by size-based shard rolling.</summary>
    long WrittenBytes { get; }
}

/// <summary>
/// Read side. Walks records in file order and yields (key, value) pairs
/// per record. Streaming API to keep memory flat on million-entity loads.
/// </summary>
public interface ISaveReader : IDisposable
{
    /// <summary>Advance to the next record. <paramref name="section"/> is set
    /// to the section name (e.g. "WORLDITEM", "WORLDCHAR"). Returns false
    /// when the file is exhausted.</summary>
    bool NextRecord(out string section);

    /// <summary>Read the next property of the current record. Returns false
    /// when the record has no more properties (caller should loop NextRecord).</summary>
    bool NextProperty(out string key, out string value);

    /// <summary>True when the last thing read was the format's explicit end marker:
    /// <c>[EOF]</c> as the final section of a text file, the zero-length terminator
    /// of a binary one. Only meaningful once <see cref="NextRecord"/> has returned
    /// false. A file cut exactly between two records parses as a shorter valid file;
    /// this is what tells the two apart (Source-X CWorld::LoadFile rejects a world
    /// file without its [EOF]).</summary>
    bool EndMarkerSeen { get; }

    /// <summary>The file-level <c>KEY=VALUE</c> lines a classic text save carries ahead
    /// of its first section (Source-X CWorld::r_Write: TITLE, VERSION, PREVBUILD, TIME,
    /// SAVECOUNT). Complete once the first <see cref="NextRecord"/> call has returned;
    /// empty for a file without such a header (a binary save, a SphereNet text save).</summary>
    IReadOnlyDictionary<string, string> FileHeader { get; }
}
