using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Byte-layout parity for the health-bar colour packets (Source-X
/// PacketHealthBarUpdate 0x17 / PacketHealthBarUpdateNew 0x16): green while
/// poisoned, yellow while frozen/invulnerable.
/// </summary>
public class HealthBarPacketTests
{
    [Fact]
    public void HealthBarStatus_0x17_LayoutMatchesSourceX()
    {
        var span = new PacketHealthBarStatus(0x40000123, poisoned: true, yellow: false).Build().Span;

        Assert.Equal(0x17, span[0]);
        Assert.Equal(15, (span[1] << 8) | span[2]);                 // total length
        Assert.Equal(0x40000123u, ReadU32(span, 3));               // serial
        Assert.Equal(2, (span[7] << 8) | span[8]);                 // status count
        Assert.Equal(1, (span[9] << 8) | span[10]);                // GreenBar type
        Assert.Equal(1, span[11]);                                  // poisoned = on
        Assert.Equal(2, (span[12] << 8) | span[13]);               // YellowBar type
        Assert.Equal(0, span[14]);                                  // not frozen
        Assert.Equal(15, span.Length);
    }

    [Fact]
    public void HealthBarStatus_0x17_YellowWhenFrozen()
    {
        var span = new PacketHealthBarStatus(0x1, poisoned: false, yellow: true).Build().Span;
        Assert.Equal(0, span[11]); // green off
        Assert.Equal(1, span[14]); // yellow on
    }

    [Fact]
    public void HealthBarStatusNew_0x16_EnhancedClientLayout()
    {
        var span = new PacketHealthBarStatusNew(0x40000123, poisoned: true, yellow: false).Build().Span;

        Assert.Equal(0x16, span[0]);
        Assert.Equal(12, (span[1] << 8) | span[2]);
        Assert.Equal(0x40000123u, ReadU32(span, 3));
        Assert.Equal(1, (span[7] << 8) | span[8]);   // count
        Assert.Equal(1, (span[9] << 8) | span[10]);  // colour = green (poison)
        Assert.Equal(1, span[11]);                    // flag on
        Assert.Equal(12, span.Length);
    }

    [Fact]
    public void HealthBarStatusNew_0x16_NoStatus_ColourZeroFlagOff()
    {
        var span = new PacketHealthBarStatusNew(0x1, poisoned: false, yellow: false).Build().Span;
        Assert.Equal(0, (span[9] << 8) | span[10]); // colour 0
        Assert.Equal(0, span[11]);                   // flag off
    }

    private static uint ReadU32(System.ReadOnlySpan<byte> s, int i) =>
        (uint)((s[i] << 24) | (s[i + 1] << 16) | (s[i + 2] << 8) | s[i + 3]);
}
