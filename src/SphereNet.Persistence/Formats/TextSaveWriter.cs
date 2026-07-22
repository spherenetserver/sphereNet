using System.Text;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Writes the classic Sphere <c>.scp</c> text format. Each record opens with
/// <c>[SECTION]</c>, properties as <c>KEY=VALUE</c>, and the file is closed by
/// one <c>[EOF]</c> marker. Wire-compatible with Sphere / Source-X saves.
/// </summary>
public sealed class TextSaveWriter : ISaveWriter
{
    /// <summary>
    /// Leading marker on a property value whose original text contained a line
    /// break. Classic single-line saves never emit a leading control char, so a
    /// value beginning with this byte unambiguously signals "escape-encoded" to
    /// <see cref="TextSaveReader"/>; every other value round-trips verbatim.
    /// </summary>
    internal const char MultilineSentinel = '\u0001';

    private readonly StreamWriter _writer;
    private readonly bool _ownsStream;
    private bool _recordOpen;
    private bool _disposed;
    private long _written;

    public TextSaveWriter(Stream stream, bool ownsStream = true)
    {
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, bufferSize: 64 * 1024, leaveOpen: !ownsStream);
        _ownsStream = ownsStream;
    }

    public long WrittenBytes => _written;

    public void WriteHeaderComment(string line)
    {
        line = TruncateLine(line);
        _writer.Write("// ");
        _writer.WriteLine(line);
        _written += 3 + line.Length + Environment.NewLine.Length;
    }

    public void BeginRecord(string section)
    {
        ValidateToken(section, nameof(section));
        if (_recordOpen) EndRecord();
        // Source-X CScript::WriteSection prefixes every section with a newline.
        _writer.WriteLine();
        _writer.Write('[');
        _writer.Write(section);
        _writer.WriteLine(']');
        _written += 2 + section.Length + (2 * Environment.NewLine.Length);
        _recordOpen = true;
    }

    public void WriteProperty(string key, string value)
    {
        if (!_recordOpen)
            throw new InvalidOperationException("WriteProperty called before BeginRecord");
        ValidateToken(key, nameof(key));
        // A single-line value is written verbatim (byte-identical to classic
        // saves). A value carrying a line break can't be represented on one line;
        // rather than silently dropping everything after the first break, encode
        // it losslessly so it round-trips through TextSaveReader. See EncodeValue.
        value = EncodeValue(value);
        _writer.Write(key);
        _writer.Write('=');
        _writer.WriteLine(value);
        _written += key.Length + 1 + value.Length + Environment.NewLine.Length;
    }

    public void EndRecord()
    {
        if (!_recordOpen) return;
        _recordOpen = false;
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        if (_recordOpen) EndRecord();
        _writer.WriteLine("[EOF]");
        _writer.WriteLine();
        _written += 5 + (2 * Environment.NewLine.Length);
        _writer.Flush();
        _writer.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Values without a line break pass through unchanged (identical output to the
    /// classic writer). Values containing <c>\r</c>/<c>\n</c> are escape-encoded
    /// behind <see cref="MultilineSentinel"/> so no data is silently lost and the
    /// original text can be reconstructed on load.
    /// </summary>
    private static string EncodeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().IndexOfAny('\r', '\n') < 0 &&
            (value.Length == 0 || value[0] != MultilineSentinel))
            return value;

        var sb = new StringBuilder(value.Length + 8);
        sb.Append(MultilineSentinel);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string TruncateLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int separator = value.AsSpan().IndexOfAny('\r', '\n');
        return separator >= 0 ? value[..separator] : value;
    }

    private static void ValidateToken(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("Save section and property keys must be non-empty single-line tokens.", paramName);
    }
}
