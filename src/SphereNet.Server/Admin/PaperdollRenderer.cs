using SphereNet.Core.Enums;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Server.Admin;

/// <summary>Client art the paperdoll renderer draws from. Abstracted so tests can
/// feed synthetic gumps, tiledata and hues instead of the real muls.</summary>
public interface IPaperdollArt
{
    /// <summary>Gump art decoded to RGBA32 (row-major, alpha 0 = transparent).</summary>
    bool TryGetGump(int id, out int width, out int height, out byte[] rgba);

    /// <summary>tiledata.mul item entry (Animation = paperdoll gump base, flags).</summary>
    ItemTileData GetItemTile(int itemId);

    /// <summary>The 32-colour 1555 table of a hue value (1-based), or null.</summary>
    ushort[]? GetHueColorTable(int hue);

    /// <summary>fonts.mul (the client's ASCII fonts), or null without the file; the
    /// frame is then drawn without its name line.</summary>
    AsciiFontReader? GetAsciiFonts() => null;
}

/// <summary><see cref="IPaperdollArt"/> over the server's loaded mul files.</summary>
public sealed class MapDataPaperdollArt(MapDataManager map) : IPaperdollArt
{
    public bool TryGetGump(int id, out int width, out int height, out byte[] rgba) =>
        map.TryGetGumpArt(id, out width, out height, out rgba);

    public ItemTileData GetItemTile(int itemId) => map.GetItemTileData(itemId);

    public ushort[]? GetHueColorTable(int hue) => map.GetHueColorTable(hue);

    public AsciiFontReader? GetAsciiFonts() => map.GetAsciiFonts();
}

/// <summary>One worn item as the client sees it: layer, display id, hue.</summary>
public readonly record struct PaperdollWornItem(byte Layer, ushort DispId, ushort Hue);

/// <summary>Everything the picture depends on, captured from the live world on the
/// main loop so the drawing itself can run on any thread. <see cref="Text"/> is the
/// paperdoll name line (the 0x88 text).</summary>
public sealed record PaperdollLook(
    uint Serial,
    ushort Body,
    ushort Hue,
    bool IsFemale,
    IReadOnlyList<PaperdollWornItem> Items,
    string Text = "")
{
    /// <summary>64-bit FNV-1a over body, skin hue, gender, every worn item's
    /// (layer, display id, hue) and the name line. A change to any of them changes
    /// the hash, which is what invalidates the cached picture.</summary>
    public ulong ComputeHash()
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        }
        Mix(Body);
        Mix(Hue);
        Mix(IsFemale ? 1u : 0u);
        Mix((uint)Items.Count);
        foreach (var it in Items)
        {
            Mix(it.Layer);
            Mix(it.DispId);
            Mix(it.Hue);
        }
        Mix((uint)Text.Length);
        foreach (char c in Text)
            Mix(c);
        return h;
    }
}

/// <summary>
/// Server-side paperdoll picture, drawn the way the classic client draws its
/// paperdoll gump. The rules are the client's (ClassicUO PaperDollInteractable
/// UpdateUI / GetAnimID, MobileView.IsCovered, ShaderHueTranslator and the gump
/// hue shader), so the web image matches what a player sees in game:
///
///  * the body gump (human 0x0C/0x0D, elf 0x0E/0x0F, gargoyle 0x029A/0x0299)
///    partial-hued with the skin hue;
///  * each worn item's gump = tiledata Animation + 50000 (male) / 60000 (female),
///    the other gender's gump when that one is missing, hued with the item hue
///    (& 0x3FFF) and partial-hued when the tiledata carries PartialHue;
///  * the client's layer order, its "quiver" order when the cloak slot holds a
///    container, the arms/torso swap for 0x1410/0x1417 arms, and its covered-layer
///    rules (a robe hides the torso, leggings hide shoes, ...);
///  * the backpack gump last, at Animation + 50000;
///  * with the frame, the name line under the doll as the client's title label
///    draws it (PaperDollGump: ASCII font 1, hue 0x0386, left-aligned at 39,262,
///    wrapped to 185 pixels), from fonts.mul. The frameless picture carries no
///    text: it is the one meant for embedding, where the page lays out the name
///    parts from the JSON itself.
///
/// Not modelled: the client's Equipconv.def / tileart.uop gump substitutions (need
/// client files the server does not load) and the PaperdollBooks backpack x-shift.
/// Non-humanoid bodies have no paperdoll: <see cref="Render"/> returns null.
///
/// Results are cached per (serial, frame) and keyed by the look hash, so an
/// equipment, hue or body change renders anew; the cache is a bounded LRU.
/// </summary>
public sealed class PaperdollRenderer
{
    public const int MaleGumpOffset = 50000;
    public const int FemaleGumpOffset = 60000;
    private const int MaxGumpIndex = 0x10000;

    /// <summary>Background of another character's paperdoll (0x07D0 is the
    /// player's own, with the extra buttons).</summary>
    public const int FrameGumpOther = 0x07D1;
    public const int FrameGumpSelf = 0x07D0;
    /// <summary>Where the client places the doll inside the frame (PaperDollGump).</summary>
    public const int FrameDollX = 8, FrameDollY = 19;
    /// <summary>Transparent border around a frameless picture.</summary>
    public const int Margin = 4;
    /// <summary>The frame's name/title label (ClassicUO PaperDollGump _titleLabel:
    /// new Label("", false, 0x0386, 185, font: 1) { X = 39, Y = 262 }).</summary>
    public const int TitleX = 39, TitleY = 262, TitleMaxWidth = 185, TitleFont = 1;
    public const ushort TitleHue = 0x0386;
    /// <summary>The 0x88 name field is 60 single-byte characters.</summary>
    public const int TitleFieldLength = 60;

    // ClassicUO PaperDollInteractable._layerOrder (draw order, back to front).
    internal static readonly Layer[] LayerOrder =
    [
        Layer.Cape, Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Legs, Layer.Arms,
        Layer.Chest, Layer.Tunic, Layer.Ring, Layer.Bracelet, Layer.Face, Layer.Gloves,
        Layer.Skirt, Layer.Robe, Layer.Waist, Layer.Neck, Layer.Hair, Layer.FacialHair,
        Layer.Earrings, Layer.Helm, Layer.OneHanded, Layer.TwoHanded, Layer.Talisman,
    ];

    // ClassicUO PaperDollInteractable._layerOrder_quiver_fix: used when the cloak
    // slot holds a container (a quiver), which is drawn over the robe instead.
    internal static readonly Layer[] LayerOrderQuiverFix =
    [
        Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Legs, Layer.Arms, Layer.Chest,
        Layer.Tunic, Layer.Ring, Layer.Bracelet, Layer.Face, Layer.Gloves, Layer.Skirt,
        Layer.Robe, Layer.Cape, Layer.Waist, Layer.Neck, Layer.Hair, Layer.FacialHair,
        Layer.Earrings, Layer.Helm, Layer.OneHanded, Layer.TwoHanded, Layer.Talisman,
    ];

    // ClassicUO HuesHelper._table: 5-bit channel -> 8-bit.
    private static readonly byte[] Channel5To8 =
    [
        0x00, 0x08, 0x10, 0x18, 0x20, 0x29, 0x31, 0x39, 0x41, 0x4A, 0x52, 0x5A, 0x62, 0x6A, 0x73, 0x7B,
        0x83, 0x8B, 0x94, 0x9C, 0xA4, 0xAC, 0xB4, 0xBD, 0xC5, 0xCD, 0xD5, 0xDE, 0xE6, 0xEE, 0xF6, 0xFF,
    ];

    private readonly IPaperdollArt _art;
    private readonly int _capacity;
    private readonly Dictionary<(uint Serial, bool Frame), LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _lru = new();
    private int _renderCount;

    private sealed record CacheEntry((uint Serial, bool Frame) Key, ulong Hash, byte[]? Png);

    public PaperdollRenderer(IPaperdollArt art, int cacheCapacity = 256)
    {
        _art = art;
        _capacity = Math.Max(1, cacheCapacity);
    }

    /// <summary>How many pictures were actually drawn (cache misses).</summary>
    public int RenderCount => Volatile.Read(ref _renderCount);

    /// <summary>Bodies the client opens a paperdoll picture for.</summary>
    public static bool IsHumanoidBody(ushort body) => body is
        0x0190 or 0x0191 or 0x0192 or 0x0193 or        // human (+ ghosts)
        0x025D or 0x025E or 0x025F or 0x0260 or        // elf (+ ghosts)
        0x029A or 0x029B or 0x02B6 or 0x02B7 or        // gargoyle (+ ghosts)
        0x03DB or 0x04E5;                              // staff robe body, special

    /// <summary>The client derives gender from these bodies (Mobile.CheckGraphicChange);
    /// anything else keeps the flag the server sent.</summary>
    public static bool ResolveFemale(ushort body, bool fallback) => body switch
    {
        0x0190 or 0x0192 or 0x025D or 0x029A => false,
        0x0191 or 0x0193 or 0x025E or 0x029B => true,
        _ => fallback,
    };

    /// <summary>PNG of the paperdoll, from cache when the look is unchanged.
    /// Null for a body without a paperdoll or when the body gump is missing.</summary>
    public byte[]? GetPng(PaperdollLook look, bool frame)
    {
        var key = (look.Serial, frame);
        ulong hash = look.ComputeHash();
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var node) && node.Value.Hash == hash)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Png;
            }
        }

        byte[]? png = Render(look, frame, out int w, out int h) is { } rgba
            ? MiniPng.Encode(w, h, rgba)
            : null;

        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var old))
            {
                _lru.Remove(old);
                _cache.Remove(key);
            }
            var node = _lru.AddFirst(new CacheEntry(key, hash, png));
            _cache[key] = node;
            while (_cache.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
        return png;
    }

    /// <summary>Draw the paperdoll to RGBA32. Null when the body has no paperdoll.</summary>
    public byte[]? Render(PaperdollLook look, bool frame, out int width, out int height)
    {
        width = height = 0;
        if (!IsHumanoidBody(look.Body))
            return null;
        Interlocked.Increment(ref _renderCount);

        bool female = ResolveFemale(look.Body, look.IsFemale);
        var gumps = new Dictionary<int, Gump?>();
        Gump? Get(int id)
        {
            if (id < 0 || id >= MaxGumpIndex) return null;
            if (!gumps.TryGetValue(id, out var g))
            {
                g = _art.TryGetGump(id, out int gw, out int gh, out byte[] px) ? new Gump(gw, gh, px) : null;
                gumps[id] = g;
            }
            return g;
        }

        var layers = new List<(Gump Gump, ushort Hue, bool Partial)>();

        // --- body (ClassicUO PaperDollInteractable.UpdateUI) ---
        ushort bodyHue = look.Hue;
        int bodyGump = look.Body switch
        {
            0x0191 or 0x0193 => 0x000D,
            0x025D => 0x000E,
            0x025E => 0x000F,
            0x029A or 0x02B6 => 0x029A,
            0x029B or 0x02B7 => 0x0299,
            0x04E5 => 0xC835,
            0x03DB => 0x000C,
            _ => female ? 0x000D : 0x000C,
        };
        if (look.Body == 0x03DB)
            bodyHue = 0x03EA;
        if (Get(bodyGump) is not { } body)
            return null;
        layers.Add((body, bodyHue, true));
        if (look.Body == 0x03DB && Get(0xC72B) is { } robeOverlay)
            layers.Add((robeOverlay, look.Hue, true));

        // --- equipment ---
        var byLayer = new Dictionary<Layer, PaperdollWornItem>();
        foreach (var it in look.Items)
            byLayer[(Layer)it.Layer] = it;

        bool swapArmsTorso = byLayer.TryGetValue(Layer.Arms, out var arms) &&
                             arms.DispId is 0x1410 or 0x1417;
        var order = byLayer.TryGetValue(Layer.Cape, out var cloak) &&
                    (_art.GetItemTile(cloak.DispId).Flags & TileFlag.Container) != 0
            ? LayerOrderQuiverFix
            : LayerOrder;

        foreach (var orderLayer in order)
        {
            var layer = orderLayer;
            if (swapArmsTorso)
            {
                if (layer == Layer.Arms) layer = Layer.Chest;
                else if (layer == Layer.Chest) layer = Layer.Arms;
            }
            if (!byLayer.TryGetValue(layer, out var item))
                continue;
            if (IsCovered(byLayer, layer))
                continue;

            var tile = _art.GetItemTile(item.DispId);
            int gumpId = ResolveEquipGump(look.Body, tile.Animation, female, Get);
            if (gumpId < 0 || Get(gumpId) is not { } g)
                continue;
            layers.Add((g, (ushort)(item.Hue & 0x3FFF), (tile.Flags & TileFlag.PartialHue) != 0));
        }

        // Backpack: always the male gump offset, never partial-hued.
        if (byLayer.TryGetValue(Layer.Pack, out var pack))
        {
            var tile = _art.GetItemTile(pack.DispId);
            if (tile.Animation != 0 && Get(tile.Animation + MaleGumpOffset) is { } bp)
                layers.Add((bp, (ushort)(pack.Hue & 0x3FFF), false));
        }

        // --- canvas ---
        int dollW = 0, dollH = 0;
        foreach (var (g, _, _) in layers)
        {
            dollW = Math.Max(dollW, g.Width);
            dollH = Math.Max(dollH, g.Height);
        }

        Gump? frameGump = frame ? Get(FrameGumpOther) ?? Get(FrameGumpSelf) : null;
        int ox, oy;
        if (frameGump != null)
        {
            ox = FrameDollX; oy = FrameDollY;
            width = Math.Max(frameGump.Width, ox + dollW);
            height = Math.Max(frameGump.Height, oy + dollH);
        }
        else
        {
            ox = oy = Margin;
            width = dollW + Margin * 2;
            height = dollH + Margin * 2;
        }

        // Name line: rendered first so the canvas can grow to fit a wrapped
        // second line that runs past the bottom of the frame.
        byte[]? title = null;
        int titleW = 0, titleH = 0;
        if (frameGump != null && ClientTitleText(look.Text) is { Length: > 0 } text &&
            _art.GetAsciiFonts() is { } fonts && TitleFont < fonts.FontCount)
        {
            title = fonts.Render(TitleFont, text, TitleMaxWidth, _art.GetHueColorTable(TitleHue),
                out titleW, out titleH);
            if (title != null)
            {
                width = Math.Max(width, TitleX + titleW);
                height = Math.Max(height, TitleY + titleH);
            }
        }

        var canvas = new byte[width * height * 4];
        if (frameGump != null)
            Blit(canvas, width, height, 0, 0, frameGump, null, null, false);

        // GUMP shader: a pixel whose red channel is ~0 is looked up in hue row 0
        // (hue value 1) instead of the item's hue.
        ushort[]? zeroRow = _art.GetHueColorTable(1);
        foreach (var (g, hue, partialHue) in layers)
        {
            bool partial = partialHue;
            ushort[]? table = ResolveHue(hue, ref partial);
            Blit(canvas, width, height, ox, oy, g, table, zeroRow, partial);
        }

        if (title != null)
            Blit(canvas, width, height, TitleX, TitleY, new Gump(titleW, titleH, title), null, null, false);
        return canvas;
    }

    /// <summary>The text the client's label actually receives: the 0x88 field keeps
    /// the low byte of the first 60 characters and the client reads it up to the
    /// first zero byte.</summary>
    internal static string ClientTitleText(string text)
    {
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, TitleFieldLength));
        for (int i = 0; i < text.Length && i < TitleFieldLength; i++)
        {
            char c = (char)(byte)text[i];
            if (c == '\0')
                break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>ShaderHueTranslator.GetHueVector: 0x8000 = partial, 0x4000 =
    /// spectral (drawn unhued here), else hue N is table N.</summary>
    private ushort[]? ResolveHue(ushort hue, ref bool partial)
    {
        if ((hue & 0x8000) != 0)
        {
            partial = true;
            hue &= 0x7FFF;
        }
        if (hue == 0 || (hue & 0x4000) != 0)
            return null;
        return _art.GetHueColorTable(hue);
    }

    /// <summary>ClassicUO PaperDollInteractable.GetAnimID: gendered gump offset with a
    /// fall back to the other gender. -1 when neither exists.</summary>
    private static int ResolveEquipGump(ushort body, ushort animId, bool female, Func<int, Gump?> get)
    {
        // Dead gargoyles wear the shroud through its gargoyle gump (client 7.0+).
        if (animId == 0x03CA && body is 0x02B7 or 0x02B6)
            animId = 0x0223;

        int offset = female ? FemaleGumpOffset : MaleGumpOffset;
        if (animId + offset >= MaxGumpIndex || get(animId + offset) == null)
            offset = female ? MaleGumpOffset : FemaleGumpOffset;
        int id = animId + offset;
        return id < MaxGumpIndex && get(id) != null ? id : -1;
    }

    /// <summary>ClassicUO MobileView.IsCovered — which worn layers another one hides.</summary>
    internal static bool IsCovered(IReadOnlyDictionary<Layer, PaperdollWornItem> worn, Layer layer)
    {
        ushort? G(Layer l) => worn.TryGetValue(l, out var it) ? it.DispId : null;

        switch (layer)
        {
            case Layer.Shoes:
            {
                ushort? pants = G(Layer.Pants);
                if (G(Layer.Legs) != null || pants == 0x1411)
                    return true;
                ushort? robe = G(Layer.Robe);
                if (pants is 0x0513 or 0x0514 || robe == 0x0504)
                    return true;
                break;
            }
            case Layer.Pants:
            {
                ushort? robe = G(Layer.Robe);
                ushort? pants = G(Layer.Pants);
                if (G(Layer.Legs) != null || robe == 0x0504)
                    return true;
                if (pants is 0x01EB or 0x03E5 or 0x03EB)
                {
                    ushort? skirt = G(Layer.Skirt);
                    if (skirt != null && skirt != 0x01C7 && skirt != 0x01E4)
                        return true;
                    if (robe != null && robe != 0x0229 && (robe <= 0x04E7 || robe > 0x04EB))
                        return true;
                }
                break;
            }
            case Layer.Tunic:
            {
                ushort? robe = G(Layer.Robe);
                if (G(Layer.Tunic) == 0x0238)
                    return robe != null && robe != 0x9985 && robe != 0x9986 && robe != 0xA412;
                break;
            }
            case Layer.Chest:
            {
                ushort? robe = G(Layer.Robe);
                if (robe != null && robe != 0 && robe != 0x9985 && robe != 0x9986 &&
                    robe != 0xA412 && robe != 0xA2CA)
                    return true;
                ushort? tunic = G(Layer.Tunic);
                if (tunic != null && tunic != 0x1541 && tunic != 0x1542)
                {
                    ushort? torso = G(Layer.Chest);
                    if (torso is 0x782A or 0x782B)
                        return true;
                }
                break;
            }
            case Layer.Arms:
            {
                ushort? robe = G(Layer.Robe);
                return robe != null && robe != 0 && robe != 0x9985 && robe != 0x9986 && robe != 0xA412;
            }
            case Layer.Helm:
            case Layer.Hair:
            {
                if (G(Layer.Robe) is ushort robe)
                {
                    if (robe > 0x3173)
                    {
                        if (robe is 0x4B9D or 0x7816)
                            return true;
                    }
                    else if (robe <= 0x2687)
                    {
                        if (robe < 0x2683)
                            return robe is >= 0x204E and <= 0x204F;
                        return true;
                    }
                    else if (robe is 0x2FB9 or 0x3173)
                    {
                        return true;
                    }
                }
                break;
            }
        }
        return false;
    }

    /// <summary>Copy a gump onto the canvas, applying the client's gump hue shader:
    /// the pixel's 5-bit red channel indexes the hue's 32-colour table; a partial
    /// hue touches only grey pixels (r == g == b).</summary>
    private static void Blit(byte[] dst, int dstW, int dstH, int ox, int oy, Gump src,
        ushort[]? table, ushort[]? zeroRow, bool partial)
    {
        for (int y = 0; y < src.Height; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= dstH) continue;
            for (int x = 0; x < src.Width; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= dstW) continue;
                int s = (y * src.Width + x) * 4;
                if (src.Rgba[s + 3] == 0) continue;
                byte r = src.Rgba[s], g = src.Rgba[s + 1], b = src.Rgba[s + 2];

                if (table != null && (!partial || (r == g && g == b)))
                {
                    int r5 = (r * 31 + 127) / 255;
                    ushort? c = r5 == 0 ? zeroRow?[0] : table[r5];
                    if (c is ushort color)
                    {
                        r = Channel5To8[(color >> 10) & 0x1F];
                        g = Channel5To8[(color >> 5) & 0x1F];
                        b = Channel5To8[color & 0x1F];
                    }
                }

                int d = (dy * dstW + dx) * 4;
                dst[d] = r; dst[d + 1] = g; dst[d + 2] = b; dst[d + 3] = 255;
            }
        }
    }

    private sealed record Gump(int Width, int Height, byte[] Rgba);
}
