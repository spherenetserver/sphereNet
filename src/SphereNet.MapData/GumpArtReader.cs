namespace SphereNet.MapData;

/// <summary>
/// Reader for the classic gump art pair (gumpidx.mul + gumpart.mul).
/// Each gump is stored as per-row RLE runs of 16-bit 555 colors; this
/// decodes to straight RGBA32 for the web dialog designer's previews.
/// Files are optional — when absent every lookup simply returns false.
/// </summary>
public sealed class GumpArtReader : IDisposable
{
    private readonly string _idxPath;
    private readonly string _mulPath;
    private FileStream? _idx;
    private FileStream? _mul;
    private int _count;

    public GumpArtReader(string idxPath, string mulPath)
    {
        _idxPath = idxPath;
        _mulPath = mulPath;
    }

    public bool IsAvailable => _idx != null && _mul != null;

    public bool Load()
    {
        if (!File.Exists(_idxPath) || !File.Exists(_mulPath))
            return false;
        _idx = new FileStream(_idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _mul = new FileStream(_mulPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _count = (int)(_idx.Length / 12);
        return true;
    }

    /// <summary>Decode a gump to RGBA32 (4 bytes per pixel, row-major).
    /// Returns false for missing/invalid entries.</summary>
    public bool TryGetGump(int id, out int width, out int height, out byte[] rgba)
    {
        width = 0; height = 0; rgba = [];
        if (_idx == null || _mul == null || id < 0 || id >= _count)
            return false;

        Span<byte> entry = stackalloc byte[12];
        lock (_idx)
        {
            _idx.Position = (long)id * 12;
            if (_idx.Read(entry) != 12) return false;
        }

        int lookup = BitConverter.ToInt32(entry[..4]);
        int size = BitConverter.ToInt32(entry[4..8]);
        int extra = BitConverter.ToInt32(entry[8..12]);
        if (lookup < 0 || size <= 0 || extra <= 0)
            return false;

        width = (extra >> 16) & 0xFFFF;
        height = extra & 0xFFFF;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
            return false;

        byte[] data = new byte[size];
        lock (_mul)
        {
            _mul.Position = lookup;
            int read = 0;
            while (read < size)
            {
                int n = _mul.Read(data, read, size - read);
                if (n <= 0) return false;
                read += n;
            }
        }

        // Row lookup table: one uint per row, offsets in 16-bit words from
        // the start of this gump's data block.
        if (size < height * 4)
            return false;

        rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowOff = BitConverter.ToInt32(data, y * 4) * 2;
            int x = 0;
            int dst = y * width * 4;
            while (x < width && rowOff + 4 <= size)
            {
                ushort color = BitConverter.ToUInt16(data, rowOff);
                ushort run = BitConverter.ToUInt16(data, rowOff + 2);
                rowOff += 4;
                if (run == 0) break;

                if (color != 0)
                {
                    byte r = (byte)(((color >> 10) & 0x1F) * 255 / 31);
                    byte g = (byte)(((color >> 5) & 0x1F) * 255 / 31);
                    byte b = (byte)((color & 0x1F) * 255 / 31);
                    for (int i = 0; i < run && x < width; i++, x++)
                    {
                        int p = dst + x * 4;
                        rgba[p] = r; rgba[p + 1] = g; rgba[p + 2] = b; rgba[p + 3] = 255;
                    }
                }
                else
                {
                    x += run; // transparent run (alpha stays 0)
                }
            }
        }
        return true;
    }

    public void Dispose()
    {
        _idx?.Dispose();
        _mul?.Dispose();
        _idx = null;
        _mul = null;
    }
}
