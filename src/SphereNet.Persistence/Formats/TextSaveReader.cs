using System.Text;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Streaming reader for classic Sphere <c>.scp</c> text saves. Line-by-line
/// tokenizer — same behaviour as the pre-refactor WorldLoader parse loop,
/// but extracted so the loader can work over an abstract source.
/// </summary>
public sealed class TextSaveReader : ISaveReader
{
    /// <summary>
    /// Hard cap on a single physical line. A corrupt or malicious save with no
    /// line terminator would otherwise let one <see cref="StreamReader.ReadLine"/>
    /// buffer the whole (multi-gigabyte) file into memory. Real save lines are far
    /// smaller; exceeding this is treated as corruption so the caller's generation
    /// fallback can take over instead of the loader OOM-ing.
    /// </summary>
    internal const int MaxLineLength = 8 * 1024 * 1024;

    private readonly StreamReader _reader;
    private readonly bool _ownsStream;
    private bool _inRecord;

    public TextSaveReader(Stream stream, bool ownsStream = true)
    {
        _reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: !ownsStream);
        _ownsStream = ownsStream;
    }

    public bool NextRecord(out string section)
    {
        // Drain any UNREAD properties of the current record FIRST. NextProperty's
        // lookahead stashes the next record's header into _pendingSection when it
        // hits it, so this has to run before we consume the stash below — otherwise
        // an early-exit caller (one that stopped mid-record) would skip the whole
        // section the drain just stashed, and that stale header would surface out
        // of order on the following call.
        while (_inRecord && NextProperty(out _, out _)) { /* discard */ }

        // Consume a stashed header — from the drain above, or from a prior
        // NextProperty the caller stopped on. Single owner of "the next header".
        if (_pendingSection != null)
        {
            string pending = _pendingSection;
            _pendingSection = null;
            if (!pending.Equals("EOF", StringComparison.OrdinalIgnoreCase))
            {
                section = pending;
                _inRecord = true;
                return true;
            }
            // A stashed [EOF] ends the record; fall through to read any trailing
            // sections (classic saves put [EOF] last, so this usually hits EOF).
        }

        string? line;
        while ((line = ReadBoundedLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                string name = trimmed[1..^1];
                if (name.Equals("EOF", StringComparison.OrdinalIgnoreCase))
                {
                    _inRecord = false;
                    continue;
                }
                section = name;
                _inRecord = true;
                return true;
            }
        }

        section = string.Empty;
        return false;
    }

    public bool NextProperty(out string key, out string value)
    {
        if (!_inRecord)
        {
            key = value = string.Empty;
            return false;
        }

        string? line;
        while ((line = ReadBoundedLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            // Another section header or [EOF] — back out, caller re-enters via NextRecord.
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                _inRecord = false;
                _pendingSection = trimmed[1..^1];
                key = value = string.Empty;
                return false;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            key = trimmed[..eq].Trim();
            value = DecodeValue(trimmed[(eq + 1)..].Trim());
            return true;
        }

        _inRecord = false;
        key = value = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads one line, enforcing <see cref="MaxLineLength"/>. Handles LF, CR and
    /// CRLF terminators and a final unterminated line, matching
    /// <see cref="StreamReader.ReadLine"/> semantics for well-formed input while
    /// refusing to buffer an unbounded run of characters.
    /// </summary>
    private string? ReadBoundedLine()
    {
        int first = _reader.Read();
        if (first < 0) return null;

        var sb = new StringBuilder(256);
        int c = first;
        while (c >= 0)
        {
            if (c == '\n')
                return sb.ToString();
            if (c == '\r')
            {
                if (_reader.Peek() == '\n') _reader.Read();
                return sb.ToString();
            }
            if (sb.Length >= MaxLineLength)
                throw new InvalidDataException(
                    $"Save line exceeds {MaxLineLength} characters without a terminator; file is corrupt.");
            sb.Append((char)c);
            c = _reader.Read();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reverses <c>TextSaveWriter</c>'s multi-line encoding. A value that does not
    /// begin with <see cref="TextSaveWriter.MultilineSentinel"/> — every value a
    /// classic Sphere/Source-X save produces — is returned verbatim.
    /// </summary>
    private static string DecodeValue(string value)
    {
        if (value.Length == 0 || value[0] != TextSaveWriter.MultilineSentinel)
            return value;

        var sb = new StringBuilder(value.Length - 1);
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[++i];
                sb.Append(n switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    '\\' => '\\',
                    _ => n,
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Recovery for the "read ahead" case where NextProperty finds a new
    // [SECTION] before reporting end-of-record. Stashed here so the next
    // NextRecord returns it without re-reading the stream.
    private string? _pendingSection;

    public void Dispose() => _reader.Dispose();
}
