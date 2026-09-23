namespace SphereNet.MapData;

/// <summary>One glyph of an ASCII font: 1555 colours row-major, 0 = transparent.</summary>
public readonly record struct AsciiGlyph(int Width, int Height, ushort[] Pixels);

/// <summary>One laid-out line of text: its characters and the metrics the renderer
/// needs (the client's MultilinesFontInfo).</summary>
public sealed record AsciiTextLine(string Text, int Width, int MaxHeight);

/// <summary>
/// Reader for fonts.mul, the client's ASCII fonts, and a text renderer that lays
/// text out and colours it the way the client does.
///
/// The file is a run of fonts; each is a header byte followed by 224 glyphs
/// (characters 32..255), and each glyph is a width byte, a height byte, one unused
/// byte and width*height 16-bit 1555 colours (0 = transparent). Loading, word wrap
/// (GetInfoASCII), the per-glyph baseline shift (GetFontOffsetY) and the pixel pass
/// (GeneratePixelsASCII with HuesLoader.GetColor / GetPartialHueColor) follow
/// ClassicUO's FontsLoader, for left-aligned text without the fixed / cropped /
/// indention flags - the only way the paperdoll title label uses it.
/// </summary>
public sealed class AsciiFontReader
{
    public const int GlyphCount = 224;
    private const int FirstChar = 32;

    // FontsLoader._offsetCharTable / _offsetSymbolTable (fonts 0..9).
    private static readonly int[] OffsetCharTable = [2, 0, 2, 2, 0, 0, 2, 2, 0, 0];
    private static readonly int[] OffsetSymbolTable = [1, 0, 1, 1, -1, 0, 1, 1, 0, 0];

    // HuesHelper._table: 5-bit channel -> 8-bit.
    private static readonly byte[] Channel5To8 =
    [
        0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39, 0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B,
        0x83, 0x8B, 0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD, 0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF,
    ];

    private readonly AsciiGlyph[,] _glyphs;

    private AsciiFontReader(AsciiGlyph[,] glyphs) => _glyphs = glyphs;

    public int FontCount => _glyphs.GetLength(0);

    /// <summary>Load fonts.mul; null when the file is missing or holds no font.</summary>
    public static AsciiFontReader? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        return FromBytes(File.ReadAllBytes(path));
    }

    /// <summary>Parse an in-memory fonts.mul image (also used by tests).</summary>
    public static AsciiFontReader? FromBytes(ReadOnlySpan<byte> data)
    {
        // First pass counts the complete fonts, as FontsLoader.Load does.
        int pos = 0, fontCount = 0;
        while (pos < data.Length)
        {
            bool exit = false;
            pos++; // header
            for (int i = 0; i < GlyphCount; i++)
            {
                if (pos + 3 >= data.Length)
                    break;
                int w = data[pos], h = data[pos + 1];
                pos += 3;
                int bytes = w * h * 2;
                if (pos + bytes > data.Length)
                {
                    exit = true;
                    break;
                }
                pos += bytes;
            }
            if (exit)
                break;
            fontCount++;
        }
        if (fontCount < 1)
            return null;

        var glyphs = new AsciiGlyph[fontCount, GlyphCount];
        pos = 0;
        for (int f = 0; f < fontCount; f++)
        {
            pos++; // header
            for (int j = 0; j < GlyphCount; j++)
            {
                if (pos + 3 >= data.Length)
                {
                    glyphs[f, j] = new AsciiGlyph(0, 0, []);
                    continue;
                }
                int w = data[pos], h = data[pos + 1];
                pos += 3;
                var px = new ushort[w * h];
                for (int k = 0; k < px.Length && pos + 1 < data.Length; k++, pos += 2)
                    px[k] = (ushort)(data[pos] | (data[pos + 1] << 8));
                glyphs[f, j] = new AsciiGlyph(w, h, px);
            }
        }
        return new AsciiFontReader(glyphs);
    }

    /// <summary>The glyph drawn for a character (FontsLoader.GetASCIIIndex: control
    /// characters draw as the space glyph, anything past 255 by its low byte).</summary>
    public AsciiGlyph GetGlyph(int font, char c)
    {
        byte b = (byte)c;
        return _glyphs[font, b < FirstChar ? 0 : b - FirstChar];
    }

    public int GetWidth(int font, string text)
    {
        if ((uint)font >= (uint)FontCount)
            return 0;
        int w = 0;
        foreach (char c in text)
            w += GetGlyph(font, c).Width;
        return w;
    }

    /// <summary>Word wrap to <paramref name="maxWidth"/>: a port of
    /// FontsLoader.GetInfoASCII for left alignment and no flags. A line breaks at the
    /// last space (which is dropped); a word wider than a line breaks mid-word.</summary>
    public IReadOnlyList<AsciiTextLine> Layout(int font, string str, int maxWidth)
    {
        var lines = new List<AsciiTextLine>();
        if ((uint)font >= (uint)FontCount || string.IsNullOrEmpty(str))
            return lines;

        var data = new List<char>();
        int lineWidth = 0, lineCharCount = 0, lineMaxHeight = 0, charStart = 0;
        int charCount = 0, lastSpace = 0, readWidth = 0;
        int len = str.Length;

        void Close(int dataLength)
        {
            if (lineWidth == 0) lineWidth = 1;
            if (lineMaxHeight == 0) lineMaxHeight = 14;
            if (dataLength < data.Count && dataLength >= 0)
                data.RemoveRange(dataLength, data.Count - dataLength);
            lines.Add(new AsciiTextLine(new string(data.ToArray()), lineWidth, lineMaxHeight));
            data.Clear();
            lineWidth = 0;
            lineCharCount = 0;
            lineMaxHeight = 0;
        }

        for (int i = 0; i < len; i++)
        {
            char si = str[i];

            if (si == ' ')
            {
                lastSpace = i;
                lineWidth += readWidth;
                readWidth = 0;
                lineCharCount += charCount;
                charCount = 0;
            }

            var glyph = GetGlyph(font, si);
            int eval = charStart;

            if (si == '\n' || lineWidth + readWidth + glyph.Width > maxWidth)
            {
                if (lastSpace == charStart && lastSpace == 0 && si != '\n')
                    ++eval;

                if (si == '\n')
                {
                    lineWidth += readWidth;
                    lineCharCount += charCount;
                    lastSpace = i;
                    Close(lineCharCount);
                    charStart = i + 1;
                    readWidth = 0;
                    charCount = 0;
                    continue;
                }

                if (lastSpace + 1 == eval)
                {
                    // No space on this line: break before the current character.
                    lineWidth += readWidth;
                    lineCharCount += charCount;
                    Close(data.Count);
                    charStart = i;
                    lastSpace = i - 1;
                    charCount = 0;
                    readWidth = 0;
                }
                else
                {
                    // Break at the last space and restart just after it.
                    i = lastSpace + 1;
                    si = i < len ? str[i] : '\0';
                    charCount = 0;
                    Close(lineCharCount);
                    charStart = i;
                    readWidth = 0;
                    // The client keeps measuring with the glyph that overflowed, not
                    // the one it restarted at; kept so lines wrap where the client's do.
                }
            }

            data.Add(si);
            readWidth += si == '\r' ? 0 : glyph.Width;
            if (glyph.Height > lineMaxHeight)
                lineMaxHeight = glyph.Height;
            charCount++;
        }

        lineWidth += readWidth;
        lineCharCount += charCount;
        if (readWidth == 0 && len > 0 && (str[len - 1] == '\n' || str[len - 1] == '\r'))
        {
            lineWidth = 1;
            lineMaxHeight = 14;
        }
        lines.Add(new AsciiTextLine(new string(data.ToArray()), lineWidth, lineMaxHeight));

        if (font == 4)
        {
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i] with { MaxHeight = lines[i].MaxHeight + (lines[i].Width > 1 ? 2 : 6) };
        }
        return lines;
    }

    /// <summary>
    /// Render left-aligned wrapped text to RGBA32, as FontsLoader.GeneratePixelsASCII
    /// does: the bitmap is <paramref name="maxWidth"/> + 4 wide and as tall as the
    /// lines. With a hue table each pixel takes the table colour its red channel
    /// indexes; a partial hue (every font but 5 and 8 in the client) touches only
    /// grey pixels. Null when there is nothing to draw.
    /// </summary>
    public byte[]? Render(int font, string text, int maxWidth, ushort[]? hueTable,
        out int width, out int height)
    {
        width = height = 0;
        if ((uint)font >= (uint)FontCount || string.IsNullOrEmpty(text))
            return null;
        if (maxWidth <= 0)
            maxWidth = GetWidth(font, text);
        if (maxWidth <= 0)
            return null;

        var lines = Layout(font, text, maxWidth);
        width = maxWidth + 4;
        foreach (var line in lines)
            height += line.MaxHeight;
        if (height <= 0)
            return null;

        bool partial = font != 5 && font != 8;
        int font6OffsetY = font == 6 ? 7 : 0;
        var rgba = new byte[width * height * 4];
        int lineOffsY = 0;

        foreach (var line in lines)
        {
            int w = 0;
            foreach (char c in line.Text)
            {
                int offsY = GetFontOffsetY(font, (byte)c);
                var g = GetGlyph(font, c);
                for (int y = 0; y < g.Height; y++)
                {
                    int testY = y + lineOffsY + offsY;
                    if (testY >= height)
                        break;
                    for (int x = 0; x < g.Width; x++)
                    {
                        if (x + w >= width)
                            break;
                        ushort pic = g.Pixels[y * g.Width + x];
                        if (pic == 0)
                            continue;
                        ushort color = pic;
                        if (hueTable != null)
                        {
                            bool grey = ((pic >> 10) & 0x1F) == ((pic >> 5) & 0x1F) &&
                                        ((pic >> 5) & 0x1F) == (pic & 0x1F);
                            if (!partial || grey)
                                color = hueTable[(pic >> 10) & 0x1F];
                        }
                        int block = testY * width + x + w;
                        if (block < 0)
                            continue;
                        int d = block * 4;
                        rgba[d] = Channel5To8[(color >> 10) & 0x1F];
                        rgba[d + 1] = Channel5To8[(color >> 5) & 0x1F];
                        rgba[d + 2] = Channel5To8[color & 0x1F];
                        rgba[d + 3] = 255;
                    }
                }
                w += g.Width;
            }
            lineOffsY += line.MaxHeight - font6OffsetY;
        }
        return rgba;
    }

    /// <summary>FontsLoader.GetFontOffsetY: lower-case letters and symbols sit a
    /// little lower than capitals in some fonts.</summary>
    private static int GetFontOffsetY(int font, byte index)
    {
        if (index == 0xB8)
            return 1;
        if (!(index >= 0x41 && index <= 0x5A) && !(index >= 0xC0 && index <= 0xDF) && index != 0xA8)
        {
            if (font < 10)
                return index >= 0x61 && index <= 0x7A ? OffsetCharTable[font] : OffsetSymbolTable[font];
            return 2;
        }
        return 0;
    }
}
