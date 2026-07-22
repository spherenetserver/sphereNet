using System;
using System.IO;
using System.Text;
using SphereNet.Persistence.Formats;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 20 / L2 — the classic text saver silently cut a property value at the first
/// line break (data loss, no log), and the reader used an unbounded ReadLine so a
/// corrupt save with no newline could buffer the whole file (OOM). The writer now
/// escape-encodes multi-line values losslessly, and the reader caps a single line
/// and raises a controlled InvalidDataException instead of allocating without
/// bound (letting the caller's generation fallback take over).
/// </summary>
public sealed class TextLinePolicyTests
{
    private static (string key, string value) RoundTrip(string key, string value)
    {
        using var ms = new MemoryStream();
        using (var w = new TextSaveWriter(ms, ownsStream: false))
        {
            w.BeginRecord("REC");
            w.WriteProperty(key, value);
            w.EndRecord();
        }
        ms.Position = 0;
        using var r = new TextSaveReader(ms, ownsStream: false);
        Assert.True(r.NextRecord(out var section));
        Assert.Equal("REC", section);
        Assert.True(r.NextProperty(out var k, out var v));
        return (k, v);
    }

    [Fact]
    public void ValueWithNewlines_RoundTripsLosslessly()
    {
        const string original = "line one\nline two\r\nline three\ttrailing";
        var (k, v) = RoundTrip("TAG.note", original);
        Assert.Equal("TAG.note", k);
        Assert.Equal(original, v); // no silent truncation at the first break
    }

    [Fact]
    public void ValueWithBackslashes_AndNewlines_RoundTripsLosslessly()
    {
        // Backslashes must survive because the encoder uses them as escape chars.
        const string original = @"C:\path\file" + "\n" + @"D:\other\\dir";
        var (_, v) = RoundTrip("TAG.p", original);
        Assert.Equal(original, v);
    }

    [Fact]
    public void SingleLineValue_IsWrittenVerbatim_NoSentinel()
    {
        // The common case must stay byte-identical to the classic writer: a plain
        // single-line value carries no encoding marker on disk.
        using var ms = new MemoryStream();
        using (var w = new TextSaveWriter(ms, ownsStream: false))
        {
            w.BeginRecord("REC");
            w.WriteProperty("NAME", "Sir Reginald");
            w.EndRecord();
        }
        string text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("NAME=Sir Reginald", text);
        Assert.DoesNotContain('\u0001', text); // no sentinel emitted for plain values
    }

    [Fact]
    public void LegacyValueStartingWithControlByte_IsNotMisdecoded()
    {
        // A value that legitimately began with the sentinel byte gets round-tripped
        // (it is treated as "encoded" on write, restoring the exact original).
        string original = "\u0001kept";
        var (_, v) = RoundTrip("TAG.x", original);
        Assert.Equal(original, v);
    }

    [Fact]
    public void OversizeLineWithoutTerminator_ThrowsInsteadOfOom()
    {
        // A property line that never terminates must be refused after a bounded
        // read — not buffered in full. An infinite source proves we stop early
        // rather than allocating gigabytes.
        using var stream = new PrefixThenInfiniteStream("[A]\nK=", (byte)'a');
        using var r = new TextSaveReader(stream, ownsStream: false);

        Assert.True(r.NextRecord(out var s));
        Assert.Equal("A", s);
        Assert.Throws<InvalidDataException>(() => r.NextProperty(out _, out _));
    }

    /// <summary>
    /// Serves a fixed prefix, then the same filler byte forever with no newline.
    /// Reading it whole would never terminate — the bounded reader must give up.
    /// </summary>
    private sealed class PrefixThenInfiniteStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly byte _filler;
        private int _pos;

        public PrefixThenInfiniteStream(string prefix, byte filler)
        {
            _prefix = Encoding.ASCII.GetBytes(prefix);
            _filler = filler;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            int written = 0;
            while (written < count)
            {
                buffer[offset + written] = _pos < _prefix.Length ? _prefix[_pos] : _filler;
                _pos++;
                written++;
            }
            return written;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
