using SphereNet.Core.Enums;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Server.Admin;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The server-side paperdoll picture, drawn from synthetic gumps/tiledata/hues so
/// every rule the client applies can be checked pixel by pixel: layer order, the
/// gendered gump offset with its fallback, the gump hue shader (full and partial),
/// covered layers, and the look-keyed cache.
/// </summary>
public sealed class PaperdollRendererTests(ITestOutputHelper output)
{
    private const int M = PaperdollRenderer.Margin;

    /// <summary>8-bit value of a 5-bit channel, as the gump decoder produces it.</summary>
    private static byte C(int v5) => (byte)(v5 * 255 / 31);

    private sealed class FakeArt : IPaperdollArt
    {
        public readonly Dictionary<int, (int W, int H, byte[] Rgba)> Gumps = [];
        public readonly Dictionary<int, ItemTileData> Tiles = [];
        public readonly Dictionary<int, ushort[]> Hues = [];

        public bool TryGetGump(int id, out int width, out int height, out byte[] rgba)
        {
            if (Gumps.TryGetValue(id, out var g))
            {
                width = g.W; height = g.H; rgba = g.Rgba;
                return true;
            }
            width = height = 0; rgba = [];
            return false;
        }

        public ItemTileData GetItemTile(int itemId) => Tiles.GetValueOrDefault(itemId);
        public ushort[]? GetHueColorTable(int hue) => Hues.GetValueOrDefault(hue);
        public AsciiFontReader? Fonts;
        public AsciiFontReader? GetAsciiFonts() => Fonts;

        /// <summary>A 4x4 gump transparent except the listed pixels.</summary>
        public void Gump(int id, params (int X, int Y, byte R, byte G, byte B)[] pixels)
        {
            var rgba = new byte[4 * 4 * 4];
            foreach (var (x, y, r, g, b) in pixels)
            {
                int p = (y * 4 + x) * 4;
                rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = 255;
            }
            Gumps[id] = (4, 4, rgba);
        }

        public void Item(ushort dispId, ushort anim, TileFlag flags = TileFlag.Wearable) =>
            Tiles[dispId] = new ItemTileData { Animation = anim, Flags = flags, Name = "" };
    }

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] rgba, int width, int x, int y)
    {
        int p = ((y + M) * width + x + M) * 4;
        return (rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3]);
    }

    private static PaperdollLook Look(ushort body, ushort hue, params PaperdollWornItem[] items) =>
        new(0x1234, body, hue, body == 0x0191, items);

    private static PaperdollWornItem Worn(Layer layer, ushort dispId, ushort hue = 0) =>
        new((byte)layer, dispId, hue);

    private static FakeArt HumanWithBody()
    {
        var art = new FakeArt();
        art.Gump(0x000C, (0, 0, C(10), C(10), C(10)), (3, 3, C(10), C(10), C(10)));
        art.Gump(0x000D, (0, 0, C(12), C(12), C(12)), (3, 3, C(12), C(12), C(12)));
        return art;
    }

    [Fact]
    public void LaterLayersDrawOverEarlierOnesInTheClientOrder()
    {
        var art = HumanWithBody();
        art.Item(0x1515, 1);   // cloak
        art.Item(0x1F03, 2);   // robe
        art.Item(0x203B, 3);   // hair
        art.Gump(50001, (0, 0, C(31), 0, 0), (1, 0, C(31), 0, 0));
        art.Gump(50002, (0, 0, 0, C(31), 0), (1, 0, 0, C(31), 0), (2, 0, 0, C(31), 0));
        art.Gump(50003, (0, 0, 0, 0, C(31)));

        var r = new PaperdollRenderer(art);
        // Items listed in the "wrong" order on purpose: the renderer, not the
        // caller, decides the order.
        var rgba = r.Render(Look(0x0190, 0,
            Worn(Layer.Hair, 0x203B), Worn(Layer.Robe, 0x1F03), Worn(Layer.Cape, 0x1515)),
            frame: false, out int w, out _)!;

        Assert.Equal((0, 0, C(31), 255), Pixel(rgba, w, 0, 0)); // hair on top
        Assert.Equal((0, C(31), 0, 255), Pixel(rgba, w, 1, 0)); // robe over cloak
        Assert.Equal((0, C(31), 0, 255), Pixel(rgba, w, 2, 0));
        Assert.Equal((C(10), C(10), C(10), 255), Pixel(rgba, w, 3, 3)); // body shows
        Assert.Equal(4 + 2 * M, w);
    }

    [Fact]
    public void AContainerInTheCloakSlotIsDrawnOverTheRobe()
    {
        var art = HumanWithBody();
        art.Item(0x2FB7, 1, TileFlag.Wearable | TileFlag.Container); // quiver
        art.Item(0x1F03, 2);
        art.Gump(50001, (1, 0, C(31), 0, 0));
        art.Gump(50002, (1, 0, 0, C(31), 0));

        var rgba = new PaperdollRenderer(art).Render(Look(0x0190, 0,
            Worn(Layer.Cape, 0x2FB7), Worn(Layer.Robe, 0x1F03)), false, out int w, out _)!;

        Assert.Equal((C(31), 0, 0, 255), Pixel(rgba, w, 1, 0));
    }

    [Fact]
    public void ARobeCoversTheTorso()
    {
        var art = HumanWithBody();
        art.Item(0x1415, 4);  // plate chest
        art.Item(0x1F03, 2);  // robe - transparent where the chest is drawn
        art.Gump(50004, (2, 2, C(31), 0, 0));
        art.Gump(50002, (0, 1, 0, C(31), 0));

        var rgba = new PaperdollRenderer(art).Render(Look(0x0190, 0,
            Worn(Layer.Chest, 0x1415), Worn(Layer.Robe, 0x1F03)), false, out int w, out _)!;

        Assert.Equal(0, Pixel(rgba, w, 2, 2).A);
    }

    [Fact]
    public void FemaleGumpIsPreferredAndTheMaleOneIsTheFallback()
    {
        var art = HumanWithBody();
        art.Item(0x1516, 5);  // only a male gump exists
        art.Item(0x1517, 6);  // both exist
        art.Gump(50005, (1, 1, C(31), 0, 0));
        art.Gump(50006, (2, 2, 0, C(31), 0));
        art.Gump(60006, (2, 2, 0, 0, C(31)));

        var rgba = new PaperdollRenderer(art).Render(Look(0x0191, 0,
            Worn(Layer.Skirt, 0x1516), Worn(Layer.Shirt, 0x1517)), false, out int w, out _)!;

        Assert.Equal((C(31), 0, 0, 255), Pixel(rgba, w, 1, 1));
        Assert.Equal((0, 0, C(31), 255), Pixel(rgba, w, 2, 2));
        // and the female body gump
        Assert.Equal((C(12), C(12), C(12), 255), Pixel(rgba, w, 0, 0));
    }

    [Fact]
    public void HueIndexesTheTableWithTheRedChannel_PartialOnlyTouchesGrey()
    {
        var art = HumanWithBody();
        var table = new ushort[32];
        table[10] = 31 << 10;          // pure red
        table[12] = 31 << 5;           // pure green
        table[20] = 31;                // pure blue
        art.Hues[0x21] = table;

        art.Item(0x1F03, 7);                                    // full hue
        art.Item(0x1517, 8, TileFlag.Wearable | TileFlag.PartialHue);
        art.Gump(50007, (1, 0, C(20), C(3), C(7)));             // coloured, red=20
        art.Gump(50008, (2, 0, C(12), C(12), C(12)),            // grey -> hued
                        (2, 1, C(12), C(5), C(5)));             // coloured -> untouched

        var rgba = new PaperdollRenderer(art).Render(Look(0x0190, 0x8021,  // skin, partial flag
            Worn(Layer.Robe, 0x1F03, 0x21), Worn(Layer.Shirt, 0x1517, 0x4021)), // 0x4000 masked off
            false, out int w, out _)!;

        Assert.Equal((C(0), C(0), 0xFF, 255), Pixel(rgba, w, 1, 0));       // table[20]
        Assert.Equal((0, 0xFF, 0, 255), Pixel(rgba, w, 2, 0));             // table[12]
        Assert.Equal((C(12), C(5), C(5), 255), Pixel(rgba, w, 2, 1));      // unchanged
        Assert.Equal((0xFF, 0, 0, 255), Pixel(rgba, w, 0, 0));             // body grey r=10 -> red
    }

    [Fact]
    public void HueZeroAndUnknownHuesLeaveThePixelAlone()
    {
        var art = HumanWithBody();
        art.Item(0x1F03, 7);
        art.Gump(50007, (1, 0, C(20), C(3), C(7)));

        var rgba = new PaperdollRenderer(art).Render(Look(0x0190, 0x0777,
            Worn(Layer.Robe, 0x1F03, 0)), false, out int w, out _)!;

        Assert.Equal((C(20), C(3), C(7), 255), Pixel(rgba, w, 1, 0));
        Assert.Equal((C(10), C(10), C(10), 255), Pixel(rgba, w, 0, 0));
    }

    [Fact]
    public void NonHumanoidBodiesHaveNoPaperdoll()
    {
        var art = HumanWithBody();
        var r = new PaperdollRenderer(art);
        Assert.Null(r.Render(Look(0x00C8, 0), false, out _, out _)); // horse
        Assert.Null(r.GetPng(Look(0x00C8, 0), false));
        Assert.False(PaperdollRenderer.IsHumanoidBody(0x0009));
        Assert.True(PaperdollRenderer.IsHumanoidBody(0x025E));
    }

    [Fact]
    public void TheFramePutsTheDollAtTheClientOffset()
    {
        var art = HumanWithBody();
        var frame = new byte[40 * 50 * 4];
        for (int i = 3; i < frame.Length; i += 4) frame[i] = 255;
        art.Gumps[PaperdollRenderer.FrameGumpOther] = (40, 50, frame);

        var rgba = new PaperdollRenderer(art).Render(Look(0x0190, 0), true, out int w, out int h)!;

        Assert.Equal((40, 50), (w, h));
        int p = (PaperdollRenderer.FrameDollY * w + PaperdollRenderer.FrameDollX) * 4;
        Assert.Equal(C(10), rgba[p]);
    }

    [Fact]
    public void TheCacheRendersAgainOnlyWhenTheLookChanges()
    {
        var art = HumanWithBody();
        art.Item(0x1F03, 7);
        art.Gump(50007, (1, 0, C(20), C(3), C(7)));
        var r = new PaperdollRenderer(art);

        var dressed = Look(0x0190, 0, Worn(Layer.Robe, 0x1F03, 0x21));
        byte[]? first = r.GetPng(dressed, false);
        Assert.NotNull(first);
        Assert.Same(first, r.GetPng(dressed with { }, false));
        Assert.Equal(1, r.RenderCount);

        // Re-dyed robe: new hash, new picture.
        r.GetPng(Look(0x0190, 0, Worn(Layer.Robe, 0x1F03, 0x22)), false);
        Assert.Equal(2, r.RenderCount);
        // Robe taken off.
        r.GetPng(Look(0x0190, 0), false);
        Assert.Equal(3, r.RenderCount);
        // Same look again: cached.
        r.GetPng(Look(0x0190, 0), false);
        Assert.Equal(3, r.RenderCount);
        // The frame variant is its own entry.
        r.GetPng(Look(0x0190, 0), true);
        Assert.Equal(4, r.RenderCount);
    }

    [Fact]
    public void TheCacheIsBounded()
    {
        var art = HumanWithBody();
        var r = new PaperdollRenderer(art, cacheCapacity: 2);
        PaperdollLook L(uint serial) => new(serial, 0x0190, 0, false, []);

        r.GetPng(L(1), false);
        r.GetPng(L(2), false);
        r.GetPng(L(3), false);   // evicts 1
        r.GetPng(L(3), false);
        Assert.Equal(3, r.RenderCount);
        r.GetPng(L(1), false);
        Assert.Equal(4, r.RenderCount);
    }

    [Fact]
    public void HuesMulParsesEightEntriesPerGroup()
    {
        // one group: header + 8 x (32 colours + start + end + 20-byte name)
        var data = new byte[4 + 8 * 88];
        // entry 2 (hue value 3), colour 5 = 0x7C00
        int off = 4 + 2 * 88 + 5 * 2;
        data[off] = 0x00; data[off + 1] = 0x7C;
        var hues = HueReader.FromBytes(data)!;

        Assert.Equal(8, hues.Count);
        Assert.Equal(0x7C00, hues.GetColorTable(3)![5]);
        Assert.Null(hues.GetColorTable(0));
        Assert.Null(hues.GetColorTable(9));
    }

    [Fact]
    public void GumpRowOffsetsCountFourByteUnits()
    {
        // 2x2 gump: row table (2 dwords), then row 0 = one (color, run=2) pair at
        // dword 2 and row 1 at dword 3. Offsets read as 2-byte units put row 1
        // inside the row table.
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_gump_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var data = new byte[16];
            BitConverter.GetBytes(2).CopyTo(data, 0);
            BitConverter.GetBytes(3).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)0x7C00).CopyTo(data, 8);   // red
            BitConverter.GetBytes((ushort)2).CopyTo(data, 10);
            BitConverter.GetBytes((ushort)0x001F).CopyTo(data, 12);  // blue
            BitConverter.GetBytes((ushort)2).CopyTo(data, 14);
            var idx = new byte[12];
            BitConverter.GetBytes(0).CopyTo(idx, 0);
            BitConverter.GetBytes(data.Length).CopyTo(idx, 4);
            BitConverter.GetBytes((2 << 16) | 2).CopyTo(idx, 8);
            File.WriteAllBytes(Path.Combine(dir, "gumpidx.mul"), idx);
            File.WriteAllBytes(Path.Combine(dir, "gumpart.mul"), data);

            using var reader = new GumpArtReader(Path.Combine(dir, "gumpidx.mul"), Path.Combine(dir, "gumpart.mul"));
            Assert.True(reader.Load());
            Assert.True(reader.TryGetGump(0, out int w, out int h, out byte[] rgba));
            Assert.Equal((2, 2), (w, h));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[4..8]);     // row 0, x=1
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[12..16]);   // row 1, x=1
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Real client files, when this machine has them: a dressed human must
    /// come out as a PNG with the body drawn.</summary>
    [Fact]
    public void RendersFromTheRealMuls()
    {
        const string mulDir = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables",
                !File.Exists(Path.Combine(mulDir, "gumpart.mul")) ||
                !File.Exists(Path.Combine(mulDir, "tiledata.mul")) ||
                !File.Exists(Path.Combine(mulDir, "hues.mul"))))
            return;

        using var map = new MapDataManager(mulDir);
        try { map.Load(); }
        catch (FileNotFoundException) { return; }

        var r = new PaperdollRenderer(new MapDataPaperdollArt(map));
        var look = new PaperdollLook(1, 0x0190, 0x83EA, false,
        [
            Worn(Layer.Shirt, 0x1517, 0x0021),
            Worn(Layer.Pants, 0x152E, 0x0030),
            Worn(Layer.Shoes, 0x170F, 0),
            Worn(Layer.Hair, 0x203B, 0x044E),
            Worn(Layer.OneHanded, 0x0F5E, 0),
            Worn(Layer.Pack, 0x0E75, 0),
        ]);
        var rgba = r.Render(look, false, out int w, out int h);
        Assert.NotNull(rgba);
        Assert.True(w > 100 && h > 100, $"unexpected size {w}x{h}");
        Assert.Contains(rgba!.Where((_, i) => i % 4 == 3), a => a == 255);
        Assert.NotNull(r.GetPng(look, true));
        output.WriteLine($"rendered {w}x{h}");
    }

    // --- name line / fonts.mul -----------------------------------------------

    private const ushort Grey = 0x4210;   // r = g = b = 16
    private const ushort Red = 0x7C00;    // pure red, not grey

    /// <summary>A fonts.mul image of <paramref name="fonts"/> identical fonts: 'A' is
    /// 3x2 grey, 'b' 2x2 red, space 2 wide and empty, everything else 1x1 blank.</summary>
    private static byte[] SyntheticFonts(int fonts = 2)
    {
        var bytes = new List<byte>();
        for (int f = 0; f < fonts; f++)
        {
            bytes.Add(0); // header
            for (int i = 0; i < AsciiFontReader.GlyphCount; i++)
            {
                char c = (char)(i + 32);
                (int w, int h, ushort px) = c switch
                {
                    'A' => (3, 2, Grey),
                    'b' => (2, 2, Red),
                    ' ' => (2, 1, (ushort)0),
                    _ => (1, 1, (ushort)0),
                };
                bytes.Add((byte)w); bytes.Add((byte)h); bytes.Add(0);
                for (int p = 0; p < w * h; p++) { bytes.Add((byte)px); bytes.Add((byte)(px >> 8)); }
            }
        }
        return bytes.ToArray();
    }

    [Fact]
    public void FontsMulParsesHeaderAndGlyphs()
    {
        var fonts = AsciiFontReader.FromBytes(SyntheticFonts(3))!;
        Assert.Equal(3, fonts.FontCount);
        var a = fonts.GetGlyph(2, 'A');
        Assert.Equal((3, 2), (a.Width, a.Height));
        Assert.All(a.Pixels, p => Assert.Equal(Grey, p));
        Assert.Equal(2, fonts.GetGlyph(0, '\t').Width); // control chars draw as space
        Assert.Equal(10, fonts.GetWidth(1, "AA b"));
        Assert.Null(AsciiFontReader.FromBytes([]));

        // A truncated last font is not counted.
        var cut = SyntheticFonts(2);
        Assert.Equal(1, AsciiFontReader.FromBytes(cut.AsSpan(0, cut.Length - 1))!.FontCount);
    }

    [Fact]
    public void FontLayoutWrapsAtTheLastSpaceAndMidWordWhenItMust()
    {
        var fonts = AsciiFontReader.FromBytes(SyntheticFonts())!;
        Assert.Equal(["AA", "AA", "AA"], fonts.Layout(1, "AA AA AA", 9).Select(l => l.Text));
        Assert.Equal(["AA AA"], fonts.Layout(1, "AA AA", 14).Select(l => l.Text));
        Assert.Equal(["AA", "AA"], fonts.Layout(1, "AAAA", 7).Select(l => l.Text));
        var line = fonts.Layout(1, "AA AA AA", 9)[0];
        Assert.Equal((6, 2), (line.Width, line.MaxHeight));
    }

    [Fact]
    public void FontRenderAppliesAPartialHue()
    {
        var fonts = AsciiFontReader.FromBytes(SyntheticFonts())!;
        var table = new ushort[32];
        table[16] = 0x001F; // grey 16 -> blue
        var rgba = fonts.Render(1, "Ab", 20, table, out int w, out int h)!;
        Assert.Equal((24, 2), (w, h));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[0..4]);  // grey 'A' hued blue
        int b = 3 * 4;                                             // 'b' starts after the 3-wide 'A'
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[b..(b + 4)]); // red 'b' untouched

        // Fonts 5 and 8 hue every pixel, not only grey ones.
        var full = AsciiFontReader.FromBytes(SyntheticFonts(9))!;
        table[31] = 0x03E0; // red channel 31 -> green
        var all = full.Render(5, "b", 4, table, out _, out _)!;
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, all[0..4]);
    }

    [Fact]
    public void TheFrameCarriesTheNameLineWhereTheClientDrawsIt()
    {
        var art = HumanWithBody();
        var frame = new byte[40 * 50 * 4];
        for (int i = 3; i < frame.Length; i += 4) frame[i] = 255;
        art.Gumps[PaperdollRenderer.FrameGumpOther] = (40, 50, frame);
        art.Fonts = AsciiFontReader.FromBytes(SyntheticFonts())!;
        var table = new ushort[32];
        table[16] = 0x001F;
        art.Hues[PaperdollRenderer.TitleHue] = table;

        var look = Look(0x0190, 0) with { Text = "A" };
        var rgba = new PaperdollRenderer(art).Render(look, true, out int w, out int h)!;

        // The canvas grows to hold the label (185 + 4 wide, one 2-pixel line).
        Assert.Equal(PaperdollRenderer.TitleX + PaperdollRenderer.TitleMaxWidth + 4, w);
        Assert.Equal(PaperdollRenderer.TitleY + 2, h);
        int p = (PaperdollRenderer.TitleY * w + PaperdollRenderer.TitleX) * 4;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[p..(p + 4)]);

        // No frame, no text: the frameless picture is the doll alone.
        new PaperdollRenderer(art).Render(look, false, out int fw, out int fh);
        Assert.Equal((4 + 2 * M, 4 + 2 * M), (fw, fh));

        // Without fonts.mul the frame is drawn without the line.
        art.Fonts = null;
        new PaperdollRenderer(art).Render(look, true, out int nw, out int nh);
        Assert.Equal((40, 50), (nw, nh));
    }

    [Fact]
    public void ANameLineChangeRendersAgain()
    {
        var art = HumanWithBody();
        var r = new PaperdollRenderer(art);
        var look = Look(0x0190, 0) with { Text = "Yunus, Master Warrior" };
        r.GetPng(look, true);
        r.GetPng(look with { }, true);
        Assert.Equal(1, r.RenderCount);
        r.GetPng(look with { Text = "The Glorious Lord Yunus, Master Warrior" }, true);
        Assert.Equal(2, r.RenderCount);
    }

    [Fact]
    public void TheLabelGetsWhatThePacketCarries()
    {
        Assert.Equal(new string('x', 60), PaperdollRenderer.ClientTitleText(new string('x', 70)));
        Assert.Equal("ab", PaperdollRenderer.ClientTitleText("ab\0cd"));
        Assert.Equal("\u005F", PaperdollRenderer.ClientTitleText("\u015F")); // low byte
    }

    /// <summary>The real fonts.mul, when this machine has one: font 1 exists and a
    /// name line renders with ink in it.</summary>
    [Fact]
    public void RendersTextFromTheRealFontsMul()
    {
        string[] candidates =
        [
            @"C:\sphereNetServer\mul\fonts.mul",
            @"C:\Program Files (x86)\EA Games\Ultima Online Mondain's Legacy\fonts.mul",
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        if (Gate.Missing(output, "mul tables", path == null))
            return;

        var fonts = AsciiFontReader.Load(path!)!;
        Assert.True(fonts.FontCount > PaperdollRenderer.TitleFont);
        var rgba = fonts.Render(PaperdollRenderer.TitleFont, "The Glorious Lord Yunus, Grandmaster Warrior",
            PaperdollRenderer.TitleMaxWidth, null, out int w, out int h);
        Assert.NotNull(rgba);
        Assert.Equal(PaperdollRenderer.TitleMaxWidth + 4, w);
        Assert.True(h > 10, $"unexpected height {h}");
        Assert.Contains(rgba!.Where((_, i) => i % 4 == 3), a => a == 255);
        output.WriteLine($"{fonts.FontCount} fonts; line {w}x{h}");
    }
}
