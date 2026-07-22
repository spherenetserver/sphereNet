using Microsoft.Extensions.Logging;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş #4 — the central oversize guard (NetState.DropOversize) keyed only on
/// PacketBuffer.IsOversize, which is set while a packet is built (WriteLengthAt).
/// A raw PacketBuffer(byte[]) — e.g. a recorded broadcast replayed verbatim by the
/// diagnostics tooling — never runs that path, so an over-wire-length buffer
/// carried IsOversize == false and slipped past the guard, desyncing the client's
/// packet stream. The guard now also drops any buffer longer than the wire ceiling
/// regardless of the flag.
/// </summary>
public sealed class OversizeRawPacketGuardTests
{
    private static NetState ActiveState() =>
        TestHarness.CreateActiveNetState(LoggerFactory.Create(_ => { }), 1);

    [Fact]
    public void RawOversizeBuffer_IsDroppedBySend_NotQueued()
    {
        var state = ActiveState();

        // A raw buffer past the wire ceiling carries IsOversize == false (the byte[]
        // ctor never runs WriteLengthAt) — the exact replay-bypass shape.
        var oversize = new PacketBuffer(new byte[PacketBuffer.MaxWireLength + 1]);
        Assert.False(oversize.IsOversize);
        state.Send(oversize);
        Assert.Empty(TestHarness.GetQueuedPackets(state)); // dropped by the length guard

        // A normal raw buffer still sends.
        var ok = new PacketBuffer(new byte[] { 0x0B, 0x00, 0x00, 0x00, 0x01, 0x00, 0x05 });
        state.Send(ok);
        Assert.Single(TestHarness.GetQueuedPackets(state));
    }

    [Fact]
    public void RawBufferExactlyAtCeiling_StillSends()
    {
        var state = ActiveState();

        // MaxWireLength is the inclusive ceiling — a buffer exactly that long is a
        // legal frame and must not be dropped.
        var atCeiling = new PacketBuffer(new byte[PacketBuffer.MaxWireLength]);
        state.Send(atCeiling);
        Assert.Single(TestHarness.GetQueuedPackets(state));
    }
}
