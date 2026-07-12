using SphereNet.Core.Security;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Script FILE object backing store — one open file slot with Sphere MODE
/// flags. Maps to Source-X CSFileObj (each GameClient holds one, and the
/// host keeps a server-global slot for scripts running without a client,
/// mirroring g_Serv._hFile). All paths are sandboxed under the configured
/// files root (a deliberate hardening over Source-X, which has none).
/// </summary>
public sealed class ScriptFileHandle : IDisposable
{
    /// <summary>Optional diagnostic sink (wired by the host). Reports why a
    /// script FILE operation failed instead of dropping the reason — the
    /// script itself still just sees a false/empty return, Sphere-style.</summary>
    public static Action<string>? Diagnostic;

    /// <summary>Largest READBYTE request honored (Source-X caps at the script
    /// line buffer, SCRIPT_MAX_LINE_LEN).</summary>
    private const int MaxReadBytes = 4096;

    private readonly string _basePath;
    private FileStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string _filePath = "";
    private string _resolvedPath = "";

    // MODE flags — Source-X CSFileObj::SetDefaultMode: append + read + write.
    private bool _modeAppend = true;
    private bool _modeCreate;
    private bool _modeRead = true;
    private bool _modeWrite = true;

    public ScriptFileHandle(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public bool IsOpen => _stream != null;
    public bool IsEof => _stream == null || _stream.Position >= _stream.Length;
    /// <summary>Full resolved path while open (Source-X FILEPATH), else empty.</summary>
    public string FilePath => IsOpen ? _resolvedPath : "";
    public string BasePath => _basePath;
    public long Position => _stream?.Position ?? 0;
    /// <summary>File length, or -1 when no file is open (Source-X LENGTH).</summary>
    public long Length => _stream?.Length ?? -1;

    // Source-X refuses MODE changes while a file is open (logs an error and
    // keeps the old value).
    public bool ModeAppend
    {
        get => _modeAppend;
        set { if (GuardModeChange("MODE.APPEND")) { _modeAppend = value; if (value) _modeCreate = false; } }
    }
    public bool ModeCreate
    {
        get => _modeCreate;
        set { if (GuardModeChange("MODE.CREATE")) { _modeCreate = value; if (value) _modeAppend = false; } }
    }
    public bool ModeRead
    {
        get => _modeRead;
        set { if (GuardModeChange("MODE.READFLAG")) _modeRead = value; }
    }
    public bool ModeWrite
    {
        get => _modeWrite;
        set { if (GuardModeChange("MODE.WRITEFLAG")) _modeWrite = value; }
    }

    private bool GuardModeChange(string which)
    {
        if (!IsOpen)
            return true;
        Diagnostic?.Invoke($"FILE.{which} refused: file '{_filePath}' is open");
        return false;
    }

    /// <summary>Source-X MODE.SETDEFAULT: append + read + write, no create.
    /// Refused while a file is open.</summary>
    public void SetModeDefault()
    {
        if (!GuardModeChange("MODE.SETDEFAULT"))
            return;
        _modeAppend = true;
        _modeCreate = false;
        _modeRead = true;
        _modeWrite = true;
    }

    public bool Open(string path)
    {
        // Source-X refuses OPEN while a file is already open.
        if (IsOpen)
        {
            Diagnostic?.Invoke($"FILE.OPEN '{path}' refused: '{_filePath}' is already open");
            return false;
        }

        string? resolved = ResolveSafePath(_basePath, path);
        if (resolved == null)
            return false;

        try
        {
            // Source-X FileOpen mode mapping: CREATE wins; otherwise
            // (read && write) || append opens read-write; else single-flag.
            FileMode mode;
            FileAccess access;
            bool seekEnd = false;

            if (_modeCreate)
            {
                mode = FileMode.Create;
                access = FileAccess.ReadWrite;
            }
            else if ((_modeRead && _modeWrite) || _modeAppend)
            {
                mode = FileMode.OpenOrCreate;
                access = FileAccess.ReadWrite;
                seekEnd = _modeAppend;
            }
            else if (_modeRead)
            {
                if (!File.Exists(resolved))
                    return false;
                mode = FileMode.Open;
                access = FileAccess.Read;
            }
            else if (_modeWrite)
            {
                mode = FileMode.OpenOrCreate;
                access = FileAccess.Write;
            }
            else
            {
                return false;
            }

            // Ensure parent directory exists for write modes
            if (access != FileAccess.Read)
            {
                string? dir = Path.GetDirectoryName(resolved);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            _stream = new FileStream(resolved, mode, access, FileShare.ReadWrite);
            _filePath = path;
            _resolvedPath = resolved;
            if (seekEnd)
                _stream.Seek(0, SeekOrigin.End);

            if (access is FileAccess.Read or FileAccess.ReadWrite)
                _reader = new StreamReader(_stream, leaveOpen: true);
            if (access is FileAccess.Write or FileAccess.ReadWrite)
                _writer = new StreamWriter(_stream, leaveOpen: true) { AutoFlush = false };

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // Expected file-system failures (missing file, access denied, bad
            // path) keep the Sphere contract of returning false; the reason is
            // surfaced through the diagnostic sink so it isn't lost. Anything
            // else (OOM, engine bugs) propagates.
            Diagnostic?.Invoke($"FILE.OPEN '{path}' failed: {ex.GetType().Name}: {ex.Message}");
            Close();
            return false;
        }
    }

    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _filePath = "";
        _resolvedPath = "";
    }

    // Source-X write verbs error out (return false) when no file is open.
    public bool WriteLine(string text)
    {
        if (_writer == null)
        {
            Diagnostic?.Invoke("FILE.WRITELINE refused: no file open for writing");
            return false;
        }
        _writer.WriteLine(text);
        return true;
    }

    public bool Write(string text)
    {
        if (_writer == null)
        {
            Diagnostic?.Invoke("FILE.WRITE refused: no file open for writing");
            return false;
        }
        _writer.Write(text);
        return true;
    }

    public bool WriteChr(int asciiVal)
    {
        if (_writer == null)
        {
            Diagnostic?.Invoke("FILE.WRITECHR refused: no file open for writing");
            return false;
        }
        _writer.Write((char)asciiVal);
        return true;
    }

    public void Flush()
    {
        _writer?.Flush();
        _stream?.Flush();
    }

    /// <summary>
    /// Read a specific line (1-based; 0 = last line), Source-X READLINE:
    /// scans from the beginning, RESTORES the previous position afterwards
    /// (non-destructive to POSITION), and trims trailing non-graphic chars.
    /// </summary>
    public string ReadLine(int lineNum)
    {
        if (_stream == null || _reader == null)
            return "";

        long savedPos = _stream.Position;
        try
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _reader.DiscardBufferedData();

            string result = "";
            if (lineNum == 0)
            {
                while (!_reader.EndOfStream)
                    result = _reader.ReadLine() ?? "";
            }
            else
            {
                int current = 0;
                while (!_reader.EndOfStream)
                {
                    string? line = _reader.ReadLine();
                    if (++current == lineNum)
                    {
                        result = line ?? "";
                        break;
                    }
                }
            }

            int end = result.Length;
            while (end > 0 && (char.IsControl(result[end - 1]) || char.IsWhiteSpace(result[end - 1])))
                end--;
            return result[..end];
        }
        finally
        {
            _stream.Seek(savedPos, SeekOrigin.Begin);
            _reader.DiscardBufferedData();
        }
    }

    /// <summary>Source-X READCHAR: read ONE byte at the current position and
    /// return its numeric value; errors (empty) when at/over EOF.</summary>
    public string ReadChar()
    {
        if (_stream == null)
            return "";
        if (_stream.Position + 1 > _stream.Length)
        {
            Diagnostic?.Invoke("FILE.READCHAR refused: too near the end of file");
            return "";
        }
        int b = _stream.ReadByte();
        return b < 0 ? "" : b.ToString();
    }

    /// <summary>Source-X READBYTE &lt;n&gt;: read n bytes at the current
    /// position, returned as text; errors (empty) when the request runs past
    /// EOF or n is out of range.</summary>
    public string ReadBytes(int count)
    {
        if (_stream == null)
            return "";
        if (count <= 0 || count > MaxReadBytes ||
            _stream.Position + count > _stream.Length)
        {
            Diagnostic?.Invoke($"FILE.READBYTE {count} refused: out of range or too near the end of file");
            return "";
        }
        var buf = new byte[count];
        int read = _stream.Read(buf, 0, count);
        return System.Text.Encoding.Latin1.GetString(buf, 0, read);
    }

    public void Seek(string pos)
    {
        if (_stream == null) return;

        if (pos.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _reader?.DiscardBufferedData();
        }
        else if (pos.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            _stream.Seek(0, SeekOrigin.End);
            _reader?.DiscardBufferedData();
        }
        else if (long.TryParse(pos, out long offset))
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _reader?.DiscardBufferedData();
        }
    }

    // --- Root-relative helpers (FILELINES/FILEEXIST/DELETEFILE resolve
    // against the sandbox root, one consistent base). ---

    public bool FileExistsRelative(string path) => FileExists(_basePath, path);

    public int GetFileLinesRelative(string path) => GetFileLines(_basePath, path);

    /// <summary>Source-X DELETEFILE refuses to delete the currently-open file.</summary>
    public bool DeleteRelative(string path)
    {
        if (IsOpen && string.Equals(Path.GetFileName(path), Path.GetFileName(_filePath),
                StringComparison.OrdinalIgnoreCase))
        {
            Diagnostic?.Invoke($"FILE.DELETEFILE '{path}' refused: file is open");
            return false;
        }
        return DeleteFile(_basePath, path);
    }

    // --- Static helpers ---

    public static bool FileExists(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        return resolved != null && File.Exists(resolved);
    }

    public static int GetFileLines(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        if (resolved == null || !File.Exists(resolved))
            return 0;
        try { return File.ReadAllLines(resolved).Length; }
        catch { return 0; }
    }

    public static bool DeleteFile(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        if (resolved == null || !File.Exists(resolved))
            return false;
        try { File.Delete(resolved); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Resolve path safely under basePath, blocking path traversal.
    /// </summary>
    private static string? ResolveSafePath(string basePath, string path)
    {
        return SafePath.TryResolveUnderRoot(basePath, path, out string full, out _)
            ? full
            : null;
    }

    public void Dispose()
    {
        Close();
    }
}
