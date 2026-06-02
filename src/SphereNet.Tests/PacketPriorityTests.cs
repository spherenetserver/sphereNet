using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Verifies the Source-X-style packet priority model: opcode classification and
/// that high-priority traffic (movement ack/reject) is flushed ahead of bulk
/// world/UI/chatter regardless of enqueue order.
/// </summary>
public class PacketPriorityTests
{
    private static PacketBuffer Pkt(byte opcode)
    {
        var p = new PacketBuffer(2);
        p.WriteByte(opcode);
        p.WriteByte(0x00);
        return p;
    }

    [Theory]
    [InlineData(0x22, PacketPriority.Highest)] // movement ack
    [InlineData(0x21, PacketPriority.Highest)] // movement reject
    [InlineData(0x82, PacketPriority.Highest)] // login denied
    [InlineData(0x1B, PacketPriority.High)]    // login confirm
    [InlineData(0xA9, PacketPriority.High)]    // character list
    [InlineData(0xF2, PacketPriority.High)]    // time sync
    [InlineData(0x78, PacketPriority.Normal)]  // draw object (default tier)
    [InlineData(0x77, PacketPriority.Normal)]  // mobile moving
    [InlineData(0xAE, PacketPriority.Normal)]  // unicode speech
    [InlineData(0x11, PacketPriority.Low)]     // status
    [InlineData(0x3A, PacketPriority.Low)]     // skills
    [InlineData(0x6E, PacketPriority.Low)]     // animation
    [InlineData(0xB0, PacketPriority.Low)]     // gump
    [InlineData(0x65, PacketPriority.Idle)]    // weather
    [InlineData(0x6D, PacketPriority.Idle)]    // music
    [InlineData(0xD8, PacketPriority.Idle)]    // custom house
    [InlineData(0xEE, PacketPriority.Normal)]  // unlisted → default Normal
    public void Classify_MapsOpcodeToPriority(byte opcode, PacketPriority expected)
    {
        Assert.Equal(expected, PacketPriorityClassifier.Classify(opcode));
    }

    [Fact]
    public void Flush_DrainsInPriorityOrder_HighestFirst()
    {
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 1);

        // Enqueue out of priority order: Idle, Low, Normal, High, Highest.
        state.Send(Pkt(0x65)); // Idle (weather)
        state.Send(Pkt(0x11)); // Low (status)
        state.Send(Pkt(0x78)); // Normal (draw object)
        state.Send(Pkt(0x1B)); // High (login confirm)
        state.Send(Pkt(0x22)); // Highest (move ack)

        var order = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToArray();

        Assert.Equal(new byte[] { 0x22, 0x1B, 0x78, 0x11, 0x65 }, order);
    }

    [Fact]
    public void MovementAck_LeadsBulkTraffic_RegardlessOfEnqueueOrder()
    {
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 2);

        // A gump and speech queued first, then the movement ack last.
        state.Send(Pkt(0xB0)); // Low (gump)
        state.Send(Pkt(0xAE)); // Normal (speech)
        state.SendPriority(new PacketMoveAck(7, 0)); // Highest

        var order = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToList();

        Assert.Equal(0x22, order[0]); // ack is flushed first
        Assert.Contains((byte)0xAE, order);
        Assert.Contains((byte)0xB0, order);
        Assert.True(order.IndexOf(0x22) < order.IndexOf(0xAE));
        Assert.True(order.IndexOf(0x22) < order.IndexOf(0xB0));
    }

    [Fact]
    public void SamePriority_PreservesFifoOrder()
    {
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 3);

        // Three Normal-tier packets — relative order must be preserved.
        state.Send(Pkt(0x78));
        state.Send(Pkt(0x77));
        state.Send(Pkt(0x20));

        var order = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToArray();

        Assert.Equal(new byte[] { 0x78, 0x77, 0x20 }, order);
    }
}
