using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 21 / T3 — version-conditional packet framing for the unicast tracking-arrow
/// (0xBA) and multi-placement target (0x99). Sending the modern (longer) form to a
/// client that expects the shorter form leaves trailing bytes the client reads as
/// the next opcode, desyncing its stream. The wire length must match the
/// recipient's version, gated at the 7.0.9 boundary (Source-X send.cpp).
/// </summary>
public sealed class LegacyClientFramingTests
{
    // ---- 0xBA quest/tracking arrow: 6 bytes (<7.0.9) vs 10 bytes (>=7.0.9) ----

    [Fact]
    public void ArrowQuest_Extended_Is10Bytes_WithTrailingSerial()
    {
        var buf = new PacketArrowQuest(true, 0x1234, 0x5678, extended: true).Build();
        Assert.Equal(0xBA, buf.Data[0]);
        Assert.Equal(10, buf.Length);
        Assert.Equal(1, buf.Data[1]);                       // active
        Assert.Equal(0x12, buf.Data[2]); Assert.Equal(0x34, buf.Data[3]); // x (BE)
        Assert.Equal(0x56, buf.Data[4]); Assert.Equal(0x78, buf.Data[5]); // y (BE)
    }

    [Fact]
    public void ArrowQuest_Legacy_Is6Bytes_NoSerial()
    {
        var buf = new PacketArrowQuest(false, 0x1234, 0x5678, extended: false).Build();
        Assert.Equal(0xBA, buf.Data[0]);
        Assert.Equal(6, buf.Length);
        Assert.Equal(0, buf.Data[1]);                       // inactive
        Assert.Equal(0x12, buf.Data[2]); Assert.Equal(0x34, buf.Data[3]);
        Assert.Equal(0x56, buf.Data[4]); Assert.Equal(0x78, buf.Data[5]);
    }

    // ---- 0x99 multi-placement target: 26 bytes (<7.0.9) vs 30 bytes (>=7.0.9) ----

    [Fact]
    public void TargetMulti_WithHue_Is30Bytes()
    {
        var buf = new PacketTargetMulti(1, 0x4000, 0, 0, 0, 0x0010, includeHue: true).Build();
        Assert.Equal(0x99, buf.Data[0]);
        Assert.Equal(30, buf.Length);
    }

    [Fact]
    public void TargetMulti_WithoutHue_Is26Bytes()
    {
        var buf = new PacketTargetMulti(1, 0x4000, 0, 0, 0, 0x0010, includeHue: false).Build();
        Assert.Equal(0x99, buf.Data[0]);
        Assert.Equal(26, buf.Length);
    }

    // ---- The 7.0.9 version boundary that selects the form ----

    [Theory]
    [InlineData(70_008_999u, false)] // just below 7.0.9
    [InlineData(70_009_000u, true)]  // exactly 7.0.9
    [InlineData(70_009_001u, true)]  // just above
    public void IsClientPost7090_HonorsThe7090Boundary(uint version, bool expected)
    {
        var state = new NetState(NullLogger<NetState>.Instance) { ClientVersionNumber = version };
        Assert.Equal(expected, state.IsClientPost7090);
    }
}
