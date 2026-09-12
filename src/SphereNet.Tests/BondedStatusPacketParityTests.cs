using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// 0xBF 0x19 carries two different messages (port plan İŞ-41 / PLAN-603).
///
/// The sub-command is told apart by the byte after it: type 0x00 is bonded status
/// and type 0x02 is the stat locks (EXTDATA_BondedStatus / EXTDATA_Stats_Enable,
/// sphereproto.h:243-244). Only the stat-lock half existed here, so a bonded pet's
/// ghost was never announced as bonded to anyone watching.
///
/// This is the shape of gap PLAN-603 is about: an inventory of packet CLASSES shows
/// 0xBF 0x19 as present and finds nothing wrong, because the missing piece is a
/// sub-sub-command inside a class that already exists.
/// </summary>
public sealed class BondedStatusPacketParityTests
{
    private static byte[] Bytes(PacketWriter p) => p.Build().Span.ToArray();

    [Fact]
    public void TheBondedPacketMatchesTheReferenceLayout()
    {
        // Source-X PacketBondedStatus (send.cpp:4307): 0xBF, len 11, sub 0x0019,
        // type 0x00, the uid, then the ghost flag.
        var bytes = Bytes(new PacketBondedStatus(0x0A0B0C0D, isGhost: true));

        Assert.Equal(0xBF, bytes[0]);
        Assert.Equal(11, (bytes[1] << 8) | bytes[2]);
        Assert.Equal(11, bytes.Length);
        Assert.Equal(0x0019, (bytes[3] << 8) | bytes[4]);
        Assert.Equal(0x00, bytes[5]);                       // type 0 = bonded
        Assert.Equal(0x0A0B0C0Du,
            (uint)((bytes[6] << 24) | (bytes[7] << 16) | (bytes[8] << 8) | bytes[9]));
        Assert.Equal(1, bytes[10]);
    }

    [Fact]
    public void ARaisedCreatureIsSentTheSameShapeWithTheGhostFlagDown()
    {
        var bytes = Bytes(new PacketBondedStatus(0x00000042, isGhost: false));

        Assert.Equal(11, bytes.Length);
        Assert.Equal(0x00, bytes[5]);
        Assert.Equal(0, bytes[10]);
    }

    [Fact]
    public void TheStatLockHalfOfTheSameSubCommandIsUnchanged()
    {
        // The two share sub-command 0x19 and are separated only by that type byte;
        // a test that pins one without the other would not catch a collision.
        var bonded = Bytes(new PacketBondedStatus(1, isGhost: false));
        var locks = Bytes(new PacketStatLockInfo(1, 0, 1, 2));

        Assert.Equal(0x0019, (bonded[3] << 8) | bonded[4]);
        Assert.Equal(0x0019, (locks[3] << 8) | locks[4]);
        Assert.Equal(0x00, bonded[5]);
        Assert.Equal(0x02, locks[5]);
    }
}
