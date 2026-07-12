using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Byte-layout parity for the radar-waypoint packets used to pin party members
/// on the world map (Source-X CPartyDef::UpdateWaypointAll / RemoveWaypoint).
/// The add uses type 2 (party member) with the party cliloc; the remove is a
/// fixed 5-byte drop by serial.
/// </summary>
public class PartyWaypointPacketTests
{
    [Fact]
    public void WaypointAdd_PartyMember_LayoutMatchesSourceX()
    {
        var span = new PacketWaypointAdd(0x40000123, x: 1000, y: 2000, z: -5,
            map: 1, type: 2, name: "Rin").Build().Span;

        Assert.Equal(0xE5, span[0]);
        Assert.Equal(span.Length, (span[1] << 8) | span[2]);        // self-length
        Assert.Equal(0x40000123u, ReadU32(span, 3));                // serial
        Assert.Equal(1000, (short)((span[7] << 8) | span[8]));      // x
        Assert.Equal(2000, (short)((span[9] << 8) | span[10]));     // y
        Assert.Equal(-5, (sbyte)span[11]);                          // z
        Assert.Equal(1, span[12]);                                  // map
        Assert.Equal(2, (span[13] << 8) | span[14]);               // type = party member
        // type != 1 selects the non-corpse cliloc (1062613).
        Assert.Equal(1062613u, ReadU32(span, 17));
    }

    [Fact]
    public void WaypointRemove_DropsBySerial()
    {
        var span = new PacketWaypointRemove(0x40000123).Build().Span;

        Assert.Equal(0xE6, span[0]);
        Assert.Equal(5, span.Length);
        Assert.Equal(0x40000123u, ReadU32(span, 1));
    }

    private static uint ReadU32(System.ReadOnlySpan<byte> s, int i) =>
        (uint)((s[i] << 24) | (s[i + 1] << 16) | (s[i + 2] << 8) | s[i + 3]);
}
