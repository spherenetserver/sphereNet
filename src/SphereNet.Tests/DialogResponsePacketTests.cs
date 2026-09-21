using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using Xunit;

namespace SphereNet.Tests;

public sealed class DialogResponsePacketTests
{
    [Theory]
    [InlineData(1000, 0)]
    [InlineData(0, 257)]
    [InlineData(0, 4096)]
    public void ValidResponseCountsArePreserved(int switches, int texts)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19534);
        bool called = false;
        state.GumpResponseHandler = (_, _, _, _, checks, entries) =>
        {
            called = true;
            Assert.Equal(switches, checks.Length);
            Assert.Equal(texts, entries.Length);
        };
        var b = Header((uint)switches);
        for (int i = 0; i < switches; i++) b.WriteUInt32((uint)i);
        b.WriteUInt32((uint)texts);
        for (int i = 0; i < texts; i++) Text(b, (ushort)i, "");
        b.Position = 0;
        new PacketGumpResponse().OnReceive(b, state);
        Assert.True(called);
        b.ReturnToPool();
    }

    [Theory]
    [InlineData("A\0B", "A")]
    [InlineData("A\nB", "A")]
    [InlineData("A\tB\tC", "A B\tC")]
    public void ResponseTextUsesSourceXTermination(string input, string expected)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19535);
        string? actual = null;
        state.GumpResponseHandler = (_, _, _, _, _, entries) => actual = Assert.Single(entries).Text;
        var b = Header();
        b.WriteUInt32(1);
        Text(b, 1, input);
        b.Position = 0;
        new PacketGumpResponse().OnReceive(b, state);
        Assert.Equal(expected, actual);
        b.ReturnToPool();
    }

    private static PacketBuffer Header(uint switches = 0)
    {
        var b = new PacketBuffer(32768);
        b.WriteUInt32(123);
        b.WriteUInt32(456);
        b.WriteUInt32(7);
        b.WriteUInt32(switches);
        return b;
    }
    private static void Text(PacketBuffer b, ushort id, string value)
    {
        b.WriteUInt16(id);
        b.WriteUInt16((ushort)value.Length);
        foreach (char c in value) b.WriteUInt16(c);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(5000)]
    public void LongTextCannotShiftFollowingEntry(int size)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19531);
        (ushort Id, string Text)[]? received = null;
        state.GumpResponseHandler = (_, _, _, _, _, entries) => received = entries;
        var b = Header();
        b.WriteUInt32(2);
        Text(b, 3, new string('x', size));
        Text(b, 4, "A\tB\tC\rD");
        b.Position = 0;
        new PacketGumpResponse().OnReceive(b, state);
        Assert.NotNull(received);
        Assert.Equal(new string('x', Math.Min(size, 4095)), received[0].Text);
        Assert.Equal((4, "A B\tC"), ((int)received[1].Id, received[1].Text));
        b.ReturnToPool();
    }

    [Theory]
    [InlineData("switch_overflow")]
    [InlineData("missing_switch")]
    [InlineData("text_overflow")]
    [InlineData("missing_text_header")]
    [InlineData("missing_text_body")]
    [InlineData("missing_header")]
    public void MalformedResponseDoesNotInvokeScripts(string kind)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19532);
        bool called = false;
        state.GumpResponseHandler = (_, _, _, _, _, _) => called = true;
        var b = Header(kind == "switch_overflow" ? 1001u : kind == "missing_switch" ? 1u : 0u);
        if (kind == "text_overflow") b.WriteUInt32(4097);
        if (kind is "missing_text_header" or "missing_text_body") b.WriteUInt32(1);
        if (kind == "missing_text_body") { b.WriteUInt16(3); b.WriteUInt16(8); b.WriteUInt16('A'); }
        b.Position = kind == "missing_header" ? b.Length - 1 : 0;
        new PacketGumpResponse().OnReceive(b, state);
        Assert.False(called);
        b.ReturnToPool();
    }
}
