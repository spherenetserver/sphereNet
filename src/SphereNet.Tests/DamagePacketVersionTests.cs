using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 21 / T3 (broadcast) — combat damage popup framing. A pre-4.0.7a client does
/// not understand the 7-byte 0x0B packet; Source-X sends 0xBF.0x22 (11 bytes)
/// instead, and nothing at all below 4.0.0. The damage broadcast now buckets
/// recipients by version and sends each its own format. These lock the two wire
/// layouts and the per-version selection so a mixed-era broadcast can't desync a
/// legacy client with a modern packet.
/// </summary>
public sealed class DamagePacketVersionTests
{
    // ---- 0x0B new damage: 7 bytes ----

    [Fact]
    public void NewDamage_Is7Bytes_SerialThenDamageBigEndian()
    {
        var buf = new PacketDamage(0x11223344, 0x00FA).Build();
        Assert.Equal(7, buf.Length);
        Assert.Equal(0x0B, buf.Data[0]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, buf.Data[1..5]); // serial BE
        Assert.Equal(new byte[] { 0x00, 0xFA }, buf.Data[5..7]);             // damage BE
    }

    // ---- 0xBF.0x22 old damage: 11 bytes ----

    [Fact]
    public void OldDamage_Is11Bytes_WithSubcommandLeadByteSerialAndDamage()
    {
        var buf = new PacketDamageOld(0x11223344, 0xFA).Build();
        Assert.Equal(11, buf.Length);
        Assert.Equal(0xBF, buf.Data[0]);
        Assert.Equal(new byte[] { 0x00, 0x0B }, buf.Data[1..3]); // length = 11
        Assert.Equal(new byte[] { 0x00, 0x22 }, buf.Data[3..5]); // subcommand 0x0022
        Assert.Equal(0x01, buf.Data[5]);                          // constant lead byte
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, buf.Data[6..10]); // serial BE
        Assert.Equal(0xFA, buf.Data[10]);                         // damage byte
    }

    [Fact]
    public void OldDamage_ClampsDamageToAByte()
    {
        var buf = new PacketDamageOld(1, 0xFF).Build();
        Assert.Equal(0xFF, buf.Data[10]);
    }

    // ---- Per-recipient version selection (Source-X thresholds) ----

    private static NetState State(uint version) =>
        new(NullLogger<NetState>.Instance) { ClientVersionNumber = version };

    [Theory]
    [InlineData(0u, true, false)]           // unreported -> treated as modern (0x0B)
    [InlineData(70_009_000u, true, false)]  // 7.0.9 -> new
    [InlineData(50_000_000u, true, false)]  // 5.0.0 -> new
    [InlineData(40_007_000u, true, false)]  // 4.0.7 exactly -> new
    [InlineData(40_006_000u, false, true)]  // 4.0.6 -> old (0xBF.0x22)
    [InlineData(40_000_000u, false, true)]  // 4.0.0 exactly -> old
    [InlineData(30_000_000u, false, false)] // 3.x -> nothing at all
    public void DamageVariant_SelectedByVersion(uint version, bool expectNew, bool expectOld)
    {
        var s = State(version);
        Assert.Equal(expectNew, s.SendsNewDamagePacket);
        Assert.Equal(expectOld, s.SendsOldDamagePacket);
        Assert.False(expectNew && expectOld); // never both
    }
}
