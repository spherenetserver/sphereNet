using SphereNet.Game.Scripting;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CSFileObj parity for the script FILE object backing store:
/// default MODE flags, open/mode guards, read semantics (READCHAR numeric,
/// READBYTE n, READLINE position restore) and root-relative helpers.
/// </summary>
public class ScriptFileObjectParityTests : IDisposable
{
    private readonly string _root;

    public ScriptFileObjectParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"spherenet_fileobj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultMode_IsAppendReadWrite()
    {
        using var f = new ScriptFileHandle(_root);
        // Source-X SetDefaultMode: append + read + write, no create.
        Assert.True(f.ModeAppend);
        Assert.True(f.ModeRead);
        Assert.True(f.ModeWrite);
        Assert.False(f.ModeCreate);
    }

    [Fact]
    public void BareOpenThenWriteLine_AppendsLikeSourceX()
    {
        File.WriteAllText(Path.Combine(_root, "log.txt"), "first\n");
        using var f = new ScriptFileHandle(_root);
        // Default mode is append — a bare OPEN + WRITELINE must append,
        // not silently no-op (the old default was read-only).
        Assert.True(f.Open("log.txt"));
        Assert.True(f.WriteLine("second"));
        f.Close();

        var lines = File.ReadAllLines(Path.Combine(_root, "log.txt"));
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public void Open_RefusedWhileAlreadyOpen()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "y");
        using var f = new ScriptFileHandle(_root);
        Assert.True(f.Open("a.txt"));
        // Source-X refuses a second OPEN; the first file stays open.
        Assert.False(f.Open("b.txt"));
        Assert.EndsWith("a.txt", f.FilePath);
    }

    [Fact]
    public void ModeChanges_RefusedWhileOpen()
    {
        File.WriteAllText(Path.Combine(_root, "m.txt"), "x");
        using var f = new ScriptFileHandle(_root);
        Assert.True(f.Open("m.txt"));
        f.ModeCreate = true; // must be ignored while open
        Assert.False(f.ModeCreate);
        f.Close();
        f.ModeCreate = true;
        Assert.True(f.ModeCreate);
        Assert.False(f.ModeAppend); // CREATE clears APPEND (Source-X)
    }

    [Fact]
    public void Length_IsMinusOneWhenClosed()
    {
        using var f = new ScriptFileHandle(_root);
        Assert.Equal(-1, f.Length);
    }

    [Fact]
    public void ReadChar_ReturnsNumericByteWithEofGuard()
    {
        File.WriteAllText(Path.Combine(_root, "rc.txt"), "A");
        using var f = new ScriptFileHandle(_root);
        Assert.True(f.Open("rc.txt"));
        f.Seek("BEGIN");
        // Source-X READCHAR returns the numeric char code.
        Assert.Equal("65", f.ReadChar());
        // At EOF the read is refused (empty), not a silent stub.
        Assert.Equal("", f.ReadChar());
    }

    [Fact]
    public void ReadBytes_ReturnsTextAndGuardsOverread()
    {
        File.WriteAllText(Path.Combine(_root, "rb.txt"), "Hello");
        using var f = new ScriptFileHandle(_root);
        Assert.True(f.Open("rb.txt"));
        f.Seek("BEGIN");
        // Source-X READBYTE <n> returns n bytes as text.
        Assert.Equal("Hel", f.ReadBytes(3));
        // Over-read past EOF is refused.
        Assert.Equal("", f.ReadBytes(10));
        Assert.Equal("lo", f.ReadBytes(2));
    }

    [Fact]
    public void ReadLine_RestoresPositionAndTrimsTrailing()
    {
        File.WriteAllText(Path.Combine(_root, "rl.txt"), "one  \r\ntwo\r\nthree\r\n");
        using var f = new ScriptFileHandle(_root);
        Assert.True(f.Open("rl.txt"));
        f.Seek("2");
        long before = f.Position;
        // Source-X READLINE trims trailing non-graphic chars and restores
        // the pre-read position (non-destructive to POSITION).
        Assert.Equal("two", f.ReadLine(2));
        Assert.Equal(before, f.Position);
        Assert.Equal("three", f.ReadLine(0)); // 0 = last line
    }

    [Fact]
    public void RootRelativeHelpers_UseSandboxRoot()
    {
        File.WriteAllText(Path.Combine(_root, "data.txt"), "a\nb\nc\n");
        using var f = new ScriptFileHandle(_root);
        // Root-based even when no file is open (the old code keyed off the
        // open file's directory and failed with none open).
        Assert.True(f.FileExistsRelative("data.txt"));
        Assert.Equal(3, f.GetFileLinesRelative("data.txt"));
        // Traversal out of the sandbox is rejected.
        Assert.False(f.FileExistsRelative("../data.txt"));

        // DELETEFILE refuses the currently-open file.
        Assert.True(f.Open("data.txt"));
        Assert.False(f.DeleteRelative("data.txt"));
        f.Close();
        Assert.True(f.DeleteRelative("data.txt"));
        Assert.False(f.FileExistsRelative("data.txt"));
    }
}
