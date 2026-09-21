using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using Xunit;

namespace SphereNet.Tests;

public sealed class InputTextPacketParityTests
{
    [Fact]
    public void SkillLockHasItsOwnDispatchAndRejectsTruncation()
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19545);
        bool textCalled = false;
        int count = 0;
        state.TextCommandHandler = (_, _, _) => textCalled = true;
        state.SkillLockHandler = (_, id, value) => { Assert.Equal(46, id); Assert.Equal(2, value); count++; };
        var buffer = new PacketBuffer(8);
        buffer.WriteUInt16(46); buffer.Position = 0;
        new PacketSkillLock().OnReceive(buffer, state);
        Assert.Equal(0, count);
        buffer.Position = 2; buffer.WriteByte(2); buffer.Position = 0;
        new PacketSkillLock().OnReceive(buffer, state);
        Assert.Equal(1, count);
        Assert.False(textCalled);
        buffer.ReturnToPool();
    }

    [Fact]
    public void LongInputIsBoundedButEntireWireFieldIsConsumed()
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19546);
        string? actual = null;
        state.GumpTextEntryHandler = (_, _, _, _, text) => actual = text;
        var buffer = new PacketBuffer(2048);
        buffer.WriteUInt32(1); buffer.WriteUInt16(2); buffer.WriteByte(1);
        buffer.WriteUInt16(1501);
        for (int i = 0; i < 1500; i++) buffer.WriteByte((byte)'x');
        buffer.WriteByte(0); buffer.Position = 0;
        new PacketGumpTextEntry().OnReceive(buffer, state);
        Assert.Equal(new string('x', 29), actual);
        Assert.Equal(0, buffer.Remaining);
        buffer.ReturnToPool();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    public void TruncatedInputDoesNotDispatch(int bytes)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19543);
        bool called = false;
        state.GumpTextEntryHandler = (_, _, _, _, _) => called = true;
        var buffer = new PacketBuffer(32);
        // Complete header declares one byte of text, but the packet lacks it.
        byte[] payload = [0, 0, 0, 1, 0, 2, 1, 0, 1];
        for (int i = 0; i < bytes; i++) buffer.WriteByte(payload[i]);
        buffer.Position = 0;
        new PacketGumpTextEntry().OnReceive(buffer, state);
        Assert.False(called);
        buffer.ReturnToPool();
    }

    [Theory]
    [InlineData("A\nB", "A")]
    [InlineData("A\rB", "A")]
    [InlineData("A\tB\tC", "A B\tC")]
    [InlineData("A\0B", "A")]
    public void InputTextUsesSourceXCleanup(string input, string expected)
    {
        using var logs = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(logs, 19544);
        string? actual = null;
        state.GumpTextEntryHandler = (_, _, _, _, text) => actual = text;
        var buffer = new PacketBuffer(128);
        buffer.WriteUInt32(1); buffer.WriteUInt16(2); buffer.WriteByte(1);
        buffer.WriteUInt16((ushort)(input.Length + 1));
        foreach (char c in input) buffer.WriteByte((byte)c);
        buffer.WriteByte(0); buffer.Position = 0;
        new PacketGumpTextEntry().OnReceive(buffer, state);
        Assert.Equal(expected, actual);
        buffer.ReturnToPool();
    }
}
