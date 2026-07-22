using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 21 / T3 (broadcast) — 0x25 add-to-container has two wire formats: 21 bytes
/// for >=6.0.1.7 clients (an extra grid-index byte before the container serial)
/// and 20 bytes for older ones. The corpse-contents broadcast used to send a
/// single shared 21-byte packet to every nearby observer, desyncing any pre-6.0.1.7
/// client in range (it reads the grid byte as the first byte of the container
/// serial and every subsequent field is misaligned). The broadcast now dispatches
/// a per-recipient variant; these lock the two wire layouts so the grid byte and
/// the container-serial offset can never drift.
/// </summary>
public sealed class CorpseBroadcastVersionTests
{
    private const uint Serial = 0x11223344;
    private const ushort ItemId = 0x0A0B;
    private const ushort Amount = 0x0005;
    private const short X = 0x0006;
    private const short Y = 0x0007;
    private const uint ContainerSerial = 0x55667788;
    private const ushort Hue = 0x0009;

    private static PacketContainerItem Make(bool grid) =>
        new(Serial, ItemId, 0, Amount, X, Y, ContainerSerial, Hue, useGridIndex: grid);

    [Fact]
    public void GridVariant_Is21Bytes_WithGridByteBeforeContainerSerial()
    {
        var buf = Make(grid: true).Build();
        Assert.Equal(0x25, buf.Data[0]);
        Assert.Equal(21, buf.Length);

        // grid index byte at offset 14, then the container serial (big-endian).
        Assert.Equal(0, buf.Data[14]);
        Assert.Equal(new byte[] { 0x55, 0x66, 0x77, 0x88 }, buf.Data[15..19]);
    }

    [Fact]
    public void LegacyVariant_Is20Bytes_NoGridByte_ContainerSerialOneByteEarlier()
    {
        var buf = Make(grid: false).Build();
        Assert.Equal(0x25, buf.Data[0]);
        Assert.Equal(20, buf.Length);

        // No grid byte: the container serial sits at offset 14, exactly where the
        // grid byte lives in the 21-byte form — this one-byte shift is the desync.
        Assert.Equal(new byte[] { 0x55, 0x66, 0x77, 0x88 }, buf.Data[14..18]);
    }

    [Fact]
    public void TheTwoFormsDifferByExactlyTheGridByte()
    {
        var grid = Make(grid: true).Build();
        var legacy = Make(grid: false).Build();
        Assert.Equal(legacy.Length + 1, grid.Length);

        // Everything up to (and including) the y coordinate is identical; the forms
        // diverge only at offset 14 where the grid byte is inserted.
        Assert.Equal(legacy.Data[0..14], grid.Data[0..14]);
    }
}
