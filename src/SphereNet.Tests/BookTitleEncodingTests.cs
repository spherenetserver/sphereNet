using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// A book's title reaches the client as the text it was given.
///
/// The client reads both book headers' title and author with ReadUTF8 - 60 and 30 bytes
/// for the classic opcode, length-prefixed for the newer one (OpenBook). They were
/// written a byte per char, which is the same thing while the text is ASCII and
/// destroys it otherwise: Turkish 'i' without the dot (U+0131) came out as '1' and 's'
/// with a cedilla (U+015F) as '_', so a shard writing in anything but English had books
/// titled in punctuation.
/// </summary>
public sealed class BookTitleEncodingTests
{
    // A fixed 99-byte packet: opcode(1), uid(4), two flags, pages(2), then the fields.
    private const int TitleAt = 1 + 4 + 1 + 1 + 2;
    private const int TitleLen = 60;
    private const int AuthorLen = 30;

    private static (string Title, string Author) Read(PacketBookHeaderOut packet)
    {
        var bytes = packet.Build().Span.ToArray();
        string Field(int at, int len)
        {
            var span = bytes.AsSpan(at, len);
            int end = span.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(span[..(end < 0 ? len : end)]);
        }
        return (Field(TitleAt, TitleLen), Field(TitleAt + TitleLen, AuthorLen));
    }

    /// <summary>The case this is for.</summary>
    [Fact]
    public void ATurkishTitleSurvives()
    {
        var (title, author) = Read(new PacketBookHeaderOut(
            0x40000001, writable: false, pageCount: 4, "Büyü Kitabı", "Çağrı Şahin"));

        Assert.Equal("Büyü Kitabı", title);
        Assert.Equal("Çağrı Şahin", author);
    }

    /// <summary>An English title is byte-for-byte what it always was.</summary>
    [Fact]
    public void AnAsciiTitleIsUnchanged()
    {
        var (title, author) = Read(new PacketBookHeaderOut(
            0x40000002, writable: true, pageCount: 2, "A Tale of Two Cities", "Dickens"));

        Assert.Equal("A Tale of Two Cities", title);
        Assert.Equal("Dickens", author);
    }

    /// <summary>The packet keeps its fixed size whatever the text, and the fields stay
    /// where the client looks for them.</summary>
    [Fact]
    public void TheFieldsStayWhereTheyAre()
    {
        var packet = new PacketBookHeaderOut(
            0x40000003, writable: false, pageCount: 1, new string('ş', 80), new string('ğ', 40));

        Assert.Equal(99, packet.Build().Span.Length);
    }

    /// <summary>A title too long for the field is cut between characters, not through
    /// one - a half-written character would decode as a replacement mark.</summary>
    [Fact]
    public void ALongTitleIsCutBetweenCharacters()
    {
        var (title, _) = Read(new PacketBookHeaderOut(
            0x40000004, writable: false, pageCount: 1, new string('ş', 80), ""));

        Assert.DoesNotContain('\uFFFD', title);
        Assert.All(title, c => Assert.Equal('ş', c));
    }
}
