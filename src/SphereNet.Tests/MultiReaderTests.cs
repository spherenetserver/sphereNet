using System;
using System.IO;
using SphereNet.MapData.Multi;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// B1 (house/ship review): multi.mul has two on-disk layouts — the original 12-byte
/// component record and the 16-byte High Seas+ record (adds a trailing ship-access
/// dword). MultiReader now auto-detects the format; reading a 16-byte file as 12-byte
/// shifts every offset and inflates the footprint, which makes house/ship placement
/// fail the map-bounds check.
/// </summary>
public sealed class MultiReaderTests
{
    private static void Write12(BinaryWriter w, ushort tile, short dx, short dy, short dz, uint visible)
    {
        w.Write(tile); w.Write(dx); w.Write(dy); w.Write(dz); w.Write(visible);
    }

    private static void Write16(BinaryWriter w, ushort tile, short dx, short dy, short dz, uint visible, uint ship)
    {
        w.Write(tile); w.Write(dx); w.Write(dy); w.Write(dz); w.Write(visible); w.Write(ship);
    }

    private static (string idx, string mul) NewPaths()
    {
        string stem = Path.Combine(Path.GetTempPath(), $"sphnet_multi_{Guid.NewGuid():N}");
        return (stem + ".idx", stem + ".mul");
    }

    [Fact]
    public void DetectsHighSeas16ByteFormatAndReadsOffsetsCorrectly()
    {
        var (idx, mul) = NewPaths();
        try
        {
            using (var iw = new BinaryWriter(File.Create(idx)))
            using (var dw = new BinaryWriter(File.Create(mul)))
            {
                Write16(dw, 0x64, -2, -3, 0, 1, 0);
                Write16(dw, 0x64, 2, 3, 5, 1, 7); // ship-access dword = 7
                // idx[0]: offset 0, length 2*16=32 (32 % 16 == 0, 32 % 12 == 8 → HS)
                iw.Write(0); iw.Write(32); iw.Write(0);
            }

            using var r = new MultiReader(idx, mul);
            Assert.Equal(16, r.ComponentSize);

            var def = r.GetMulti(0);
            Assert.NotNull(def);
            Assert.Equal(2, def!.Components.Length);
            var b = def.GetBounds();
            Assert.Equal((short)-2, b.MinX);
            Assert.Equal((short)-3, b.MinY);
            Assert.Equal((short)2, b.MaxX);
            Assert.Equal((short)3, b.MaxY);
            Assert.Equal(7u, def.Components[1].ShipAccess);
        }
        finally
        {
            TryDelete(idx); TryDelete(mul);
        }
    }

    [Fact]
    public void DetectsOriginal12ByteFormat()
    {
        var (idx, mul) = NewPaths();
        try
        {
            using (var iw = new BinaryWriter(File.Create(idx)))
            using (var dw = new BinaryWriter(File.Create(mul)))
            {
                Write12(dw, 0x64, -2, -3, 0, 1);
                Write12(dw, 0x64, 2, 3, 5, 1);
                // idx[0]: offset 0, length 2*12=24 (24 % 12 == 0, 24 % 16 == 8 → original)
                iw.Write(0); iw.Write(24); iw.Write(0);
            }

            using var r = new MultiReader(idx, mul);
            Assert.Equal(12, r.ComponentSize);

            var def = r.GetMulti(0);
            Assert.NotNull(def);
            Assert.Equal(2, def!.Components.Length);
            var b = def.GetBounds();
            Assert.Equal((short)-2, b.MinX);
            Assert.Equal((short)3, b.MaxY);
            Assert.Equal(0u, def.Components[0].ShipAccess); // no HS field in 12-byte
        }
        finally
        {
            TryDelete(idx); TryDelete(mul);
        }
    }

    [Fact]
    public void AmbiguousLength_ResolvedByOffsetPlausibility_PicksHighSeas()
    {
        var (idx, mul) = NewPaths();
        try
        {
            using (var iw = new BinaryWriter(File.Create(idx)))
            using (var dw = new BinaryWriter(File.Create(mul)))
            {
                // 3 x 16 = 48 bytes, and 48 is divisible by BOTH 12 and 16 → strict vote
                // ties, so the reader falls back to offset plausibility. The large
                // ship-access dword scrambles the misaligned 12-byte reinterpretation.
                Write16(dw, 0x64, 1, 1, 1, 1, 0x27100000);
                Write16(dw, 0x64, 1, 1, 1, 1, 0x27100000);
                Write16(dw, 0x64, 1, 1, 1, 1, 0x27100000);
                iw.Write(0); iw.Write(48); iw.Write(0);
            }

            using var r = new MultiReader(idx, mul);
            Assert.Equal(16, r.ComponentSize);
            Assert.Equal(3, r.GetMulti(0)!.Components.Length);
        }
        finally
        {
            TryDelete(idx); TryDelete(mul);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
