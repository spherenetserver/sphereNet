using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 06 / T2 — script-driven gump and tooltip (OPL) size budgets. The 65535
/// wire guard (İş 05) can't catch two things: a compression bomb (the 0xDD gump
/// is tiny on the wire but announces a huge decompressed size the client must
/// allocate) and an inner length field that wraps below 65535 total. The
/// builders now reject over-budget content up front (RejectOversize), so the
/// send path drops it with a reason instead of emitting a dangerous frame.
/// </summary>
public sealed class GumpOplBudgetTests
{
    private static readonly string[] OneText = ["Hello"];

    // ---- compressed gump (0xDD) ----

    [Fact]
    public void CompressedGump_WithinBudget_BuildsValidPacket()
    {
        var buf = new PacketGumpDialog(0x40000001, 1, 100, 100, "{page 0}{text 0 0 0 0}", OneText).Build();
        Assert.False(buf.IsOversize);
        Assert.Equal(0xDD, buf.Data[0]);
    }

    [Fact]
    public void CompressedGump_LayoutOverCap_IsRejected()
    {
        string big = new('A', PacketBudget.MaxGumpLayoutBytes + 1);
        var buf = new PacketGumpDialog(1, 1, 0, 0, big, Array.Empty<string>()).Build();
        Assert.True(buf.IsOversize);
        Assert.NotNull(buf.OversizeReason);
    }

    [Fact]
    public void CompressedGump_HighlyCompressibleButHugeDecompressed_IsRejected()
    {
        // Small layout, but a text block that decompresses to > the cap: the wire
        // form is tiny (compresses well) yet would force a multi-MB client alloc.
        var texts = Enumerable.Repeat(new string('x', 16000), 100).ToArray(); // ~3.2 MB decompressed
        var buf = new PacketGumpDialog(1, 1, 0, 0, "{page 0}", texts).Build();
        Assert.True(buf.IsOversize);
        Assert.Contains("decompressed", buf.OversizeReason);
    }

    [Fact]
    public void CompressedGump_TooManyTexts_IsRejected()
    {
        var many = Enumerable.Repeat("x", PacketBudget.MaxGumpTexts + 1).ToArray();
        var buf = new PacketGumpDialog(1, 1, 0, 0, "{page 0}", many).Build();
        Assert.True(buf.IsOversize);
    }

    [Fact]
    public void CompressedGump_SingleTextOverCap_IsRejected()
    {
        var texts = new[] { new string('x', PacketBudget.MaxGumpTextChars + 1) };
        var buf = new PacketGumpDialog(1, 1, 0, 0, "{page 0}", texts).Build();
        Assert.True(buf.IsOversize);
    }

    // ---- standard gump (0xB0) ----

    [Fact]
    public void StandardGump_WithinBudget_BuildsValidPacket()
    {
        var buf = new PacketGumpDialogStandard(1, 1, 0, 0, "{page 0}", OneText).Build();
        Assert.False(buf.IsOversize);
        Assert.Equal(0xB0, buf.Data[0]);
    }

    [Fact]
    public void StandardGump_LayoutOverCap_IsRejected()
    {
        string big = new('A', PacketBudget.MaxGumpLayoutBytes + 1);
        var buf = new PacketGumpDialogStandard(1, 1, 0, 0, big, Array.Empty<string>()).Build();
        Assert.True(buf.IsOversize);
    }

    // ---- tooltip / OPL (0xD6) ----

    [Fact]
    public void Opl_WithinBudget_BuildsValidPacket()
    {
        var props = new (uint, string)[] { (1042971, "Test Item") };
        var buf = new PacketOPLData(0x40000001, 0x1234, props).Build();
        Assert.False(buf.IsOversize);
        Assert.Equal(0xD6, buf.Data[0]);
    }

    [Fact]
    public void Opl_SingleArgsOverCap_IsRejected()
    {
        var props = new (uint, string)[] { (1042971, new string('x', PacketBudget.MaxOplArgsChars + 1)) };
        var buf = new PacketOPLData(1, 1, props).Build();
        Assert.True(buf.IsOversize);
        Assert.Contains("args", buf.OversizeReason);
    }

    [Fact]
    public void Opl_TooManyProperties_IsRejected()
    {
        var props = Enumerable.Range(0, PacketBudget.MaxOplProperties + 1)
            .Select(_ => ((uint)1042971, "x")).ToArray();
        var buf = new PacketOPLData(1, 1, props).Build();
        Assert.True(buf.IsOversize);
        Assert.Contains("property count", buf.OversizeReason);
    }

    [Fact]
    public void Opl_PropertiesWithinPerArgCap_ButOverTotal_IsRejectedByTotalBudget()
    {
        // 20 args of 8000 chars each: every property is under the per-arg cap
        // (8192) and the count is under 512, yet the total is ~320 KB — far past
        // the ushort wire ceiling. Without the total budget this built an ~8 MB-
        // class buffer only to have WriteLengthAt drop it; now it is refused up front.
        var props = Enumerable.Range(0, 20)
            .Select(_ => ((uint)1042971, new string('x', 8000))).ToArray();
        var buf = new PacketOPLData(1, 1, props).Build();
        Assert.True(buf.IsOversize);
        Assert.Contains("total", buf.OversizeReason);
    }

    [Fact]
    public void Opl_LargeButUnderTotalBudget_StillBuilds()
    {
        // Seven 4,000-char args (each under the 8192 per-arg cap) → ~56 KB, just
        // under the 65535 ceiling: the total budget must not reject content that
        // still fits on the wire.
        var props = Enumerable.Range(0, 7)
            .Select(_ => ((uint)1042971, new string('x', 4000))).ToArray();
        var buf = new PacketOPLData(1, 1, props).Build();
        Assert.False(buf.IsOversize);
        Assert.Equal(0xD6, buf.Data[0]);
    }

    // ---- integration: the send path drops an over-budget gump ----

    [Fact]
    public void Send_OverBudgetGump_IsDropped_WhileNormalGumpQueues()
    {
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);

        var bad = new PacketGumpDialog(1, 1, 0, 0,
            new string('A', PacketBudget.MaxGumpLayoutBytes + 1), Array.Empty<string>()).Build();
        Assert.True(bad.IsOversize);
        state.Send(bad);
        Assert.Empty(TestHarness.GetQueuedPackets(state)); // dropped, not queued

        var ok = new PacketGumpDialog(1, 1, 0, 0, "{page 0}", OneText).Build();
        state.Send(ok);
        Assert.Single(TestHarness.GetQueuedPackets(state));
    }
}
