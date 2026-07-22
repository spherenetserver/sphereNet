using System.Text;
using SphereNet.Persistence.Formats;

namespace SphereNet.Tests;

public sealed class SourceXScriptWriteWave222Tests
{
    [Fact]
    public void TextWriter_MultipleSectionsHaveOneFileLevelEof()
    {
        using var stream = new MemoryStream();
        using (var writer = new TextSaveWriter(stream, ownsStream: false))
        {
            writer.BeginRecord("WORLDITEM i_gold");
            writer.WriteProperty("SERIAL", "040000001");
            writer.EndRecord();
            writer.BeginRecord("WORLDCHAR c_man");
            writer.WriteProperty("SERIAL", "01");
            writer.EndRecord();
        }

        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal(1, Count(text, "[EOF]"));
        Assert.True(text.IndexOf("[WORLDITEM i_gold]", StringComparison.Ordinal) <
                    text.IndexOf("[WORLDCHAR c_man]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[WORLDCHAR c_man]", StringComparison.Ordinal) <
                    text.IndexOf("[EOF]", StringComparison.Ordinal));
    }

    [Fact]
    public void TextWriter_EncodesLineBreaksInValue_NoCrlfInjection_AndRoundTrips()
    {
        using var stream = new MemoryStream();
        using (var writer = new TextSaveWriter(stream, ownsStream: false))
        {
            writer.BeginRecord("WORLDITEM");
            writer.WriteProperty("NAME", "safe value\r\nINJECTED=1");
        }

        string text = Encoding.UTF8.GetString(stream.ToArray());
        // The embedded CRLF must not split the value into a second physical line
        // that the reader would parse as a separate INJECTED=1 property; the break
        // is escaped, so no real newline precedes INJECTED.
        Assert.DoesNotContain("\nINJECTED=1", text.Replace("\r\n", "\n"));

        // And the full original value round-trips losslessly — no silent truncation.
        stream.Position = 0;
        using var reader = new TextSaveReader(stream, ownsStream: false);
        Assert.True(reader.NextRecord(out var section));
        Assert.Equal("WORLDITEM", section);
        Assert.True(reader.NextProperty(out var key, out var value));
        Assert.Equal("NAME", key);
        Assert.Equal("safe value\r\nINJECTED=1", value);
        Assert.False(reader.NextProperty(out _, out _)); // no injected property leaked in
    }

    [Fact]
    public void TextWriter_OutputRoundTripsAllSections()
    {
        using var stream = new MemoryStream();
        using (var writer = new TextSaveWriter(stream, ownsStream: false))
        {
            writer.BeginRecord("ONE");
            writer.WriteProperty("A", "1");
            writer.EndRecord();
            writer.BeginRecord("TWO");
            writer.WriteProperty("B", "2");
        }

        stream.Position = 0;
        using var reader = new TextSaveReader(stream, ownsStream: false);
        Assert.True(reader.NextRecord(out string first));
        Assert.Equal("ONE", first);
        Assert.True(reader.NextProperty(out string keyA, out string valueA));
        Assert.Equal(("A", "1"), (keyA, valueA));
        Assert.False(reader.NextProperty(out _, out _));
        Assert.True(reader.NextRecord(out string second));
        Assert.Equal("TWO", second);
        Assert.True(reader.NextProperty(out string keyB, out string valueB));
        Assert.Equal(("B", "2"), (keyB, valueB));
        Assert.False(reader.NextRecord(out _));
    }

    private static int Count(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }
}
