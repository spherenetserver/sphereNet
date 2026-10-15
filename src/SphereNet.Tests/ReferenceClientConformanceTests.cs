using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The server and the 2D client must agree, per client version, on how long each
/// packet is and on the layout of the bodies that changed between versions. The
/// server's own tests build and read packets with the server's code, so they cannot
/// see a disagreement; these compare against the reference client's table and
/// readers (<see cref="ReferenceClientProtocol"/>). 7.0.20.0 is the client the
/// shard targets.
/// </summary>
public sealed class ReferenceClientConformanceTests
{
    private static readonly uint V7020 = ReferenceClientProtocol.Pack(7, 0, 20, 0);

    private static NetState StateFor(uint packed)
    {
        var ns = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 1);
        ns.ClientVersionNumber = ReferenceClientProtocol.ToServerNumber(packed);
        return ns;
    }

    [Fact]
    public void TheCalibrationAppliesTheClientsGatesInOrder()
    {
        uint v7000 = ReferenceClientProtocol.Pack(7, 0, 0, 0);
        Assert.Equal(7, ReferenceClientProtocol.Length(0x24, v7000));
        Assert.Equal(9, ReferenceClientProtocol.Length(0x24, V7020));
        Assert.Equal(0x18, ReferenceClientProtocol.Length(0xF3, v7000));
        Assert.Equal(0x1A, ReferenceClientProtocol.Length(0xF3, V7020));
        // 6.0.6 makes 0xF1 fixed; below 7.0.9 the next gate's else branch makes it
        // variable again.
        Assert.Equal(-1, ReferenceClientProtocol.Length(0xF1, v7000));
        Assert.Equal(9, ReferenceClientProtocol.Length(0xF1, V7020));
        Assert.Equal(0x0A, ReferenceClientProtocol.Length(0xEE, v7000));
        Assert.Equal(-1, ReferenceClientProtocol.Length(0xEE, ReferenceClientProtocol.Pack(6, 0, 14, 2)));
    }

    // Opcodes the server handles that the reference client never sends at 7.0.20, so
    // its table entry for them is never used to write one.
    private static readonly byte[] NotSentAt7020 =
    [
        0x00, // character creation goes out as 0xF8 from 7.0.16.0
        0xA6, // scroll closed: no client send
        0xEB, // no client send
        0xFA, // UO Store button: only from 7.0.64.0
        0xFB, // public house content option: shown only from 7.0.79.6
    ];

    /// <summary>Every packet a 7.0.20 client may send is framed by the server at the
    /// length the client writes it with.</summary>
    [Fact]
    public void TheServerFramesEveryIncomingPacketAtTheClientsLength()
    {
        var ns = StateFor(V7020);
        var opcodes = new NetworkManager(1, NullLoggerFactory.Instance).Packets.RegisteredOpcodes()
            .Where(op => !NotSentAt7020.Contains(op)).ToArray();
        var mismatches = opcodes
            .Select(op => (op, server: PacketDefinitions.GetPacketLength(op, ns), client: ReferenceClientProtocol.Length(op, V7020)))
            .Where(x => (x.server == 0 ? -1 : x.server) != x.client)
            .Select(x => $"0x{x.op:X2} server {(x.server == 0 ? "variable" : x.server.ToString())} client {(x.client < 0 ? "variable" : x.client.ToString())}")
            .ToList();
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    /// <summary>The server switches body layouts at the same versions the client
    /// does: new character list (7.0.13.0), new 0x78 (7.0.33.1), 7.0.9 sizes,
    /// extended features (6.0.14.2).</summary>
    [Theory]
    [InlineData(7, 0, 13, 0)]
    [InlineData(7, 0, 33, 1)]
    [InlineData(7, 0, 9, 0)]
    [InlineData(6, 0, 14, 2)]
    public void TheServerChangesLayoutsAtTheClientsVersions(int major, int minor, int build, int extra)
    {
        uint at = ReferenceClientProtocol.Pack(major, minor, build, extra);
        foreach (uint v in new[] { at - 1, at, at + 1, V7020 })
        {
            var ns = StateFor(v);
            Assert.Equal(v >= ReferenceClientProtocol.V70130, ns.SupportsNewCharacterList);
            Assert.Equal(v >= ReferenceClientProtocol.V70331, ns.SupportsNewMobileIncoming);
            Assert.Equal(v >= ReferenceClientProtocol.V7090, ns.IsClientPost7090);
            Assert.Equal(v >= ReferenceClientProtocol.V60142, ns.IsClientPost60142);
        }
    }

    private static byte[] Bytes(PacketWriter writer)
    {
        PacketBuffer buf = writer.Build();
        return buf.Data.AsSpan(0, buf.Length).ToArray();
    }

    [Theory]
    [InlineData(7, 0, 20, 0)]
    [InlineData(7, 0, 33, 1)]
    public void A0x78WithEquipmentReadsWholeInTheClientsLayout(int major, int minor, int build, int extra)
    {
        uint v = ReferenceClientProtocol.Pack(major, minor, build, extra);
        var ns = StateFor(v);
        var equipment = new (uint, ushort, byte, ushort)[]
        {
            (0x40000001, 0x1517, 0x05, 0x0000), // plain shirt
            (0x40000002, 0x1F03, 0x16, 0x0481), // hued robe
            (0x40000003, 0x0F5C, 0x01, 0x0000),
            (0x40000004, 0x203B, 0x0B, 0x044E), // hued hair
        };
        var packet = Bytes(new PacketDrawObject(0x00000101, 0x0190, 1500, 1600, 0, 2, 0x83EA, 0, 1,
            equipment, ns.SupportsNewMobileIncoming));
        Assert.Null(ReferenceClientProtocol.Check(packet, v));
    }

    [Theory]
    [InlineData(7, 0, 12, 0)]
    [InlineData(7, 0, 20, 0)]
    public void TheCharacterListReadsWholeInTheClientsLayout(int major, int minor, int build, int extra)
    {
        uint v = ReferenceClientProtocol.Pack(major, minor, build, extra);
        var ns = StateFor(v);
        var packet = Bytes(new PacketCharList(["Alpha", "Beta", ""], 7, ns.SupportsNewCharacterList));
        Assert.Null(ReferenceClientProtocol.Check(packet, v));
    }

    [Fact]
    public void TheCheckerCatchesAWrongLayout()
    {
        // The 7.0.33 layout sent to a 7.0.20 client: every hue the client does not
        // expect shifts the rest of the list.
        var packet = Bytes(new PacketDrawObject(0x00000101, 0x0190, 1500, 1600, 0, 2, 0, 0, 1,
            [(0x40000001, 0x1517, 0x05, 0x0000)], newMobileIncoming: true));
        Assert.NotNull(ReferenceClientProtocol.Check(packet, V7020));
        // A fixed packet at the wrong size.
        Assert.NotNull(ReferenceClientProtocol.Check(new byte[7] { 0x24, 0, 0, 0, 1, 0, 0x3C }, V7020));
    }
}
