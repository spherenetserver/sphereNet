using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SphereNet.Game.Recording;
using SphereNet.Network.Packets.Outgoing;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Which packets a replay knows how to rewrite, checked against the writers that
/// produce them (review finding B10, the inventory half).
///
/// A replay rewrites object serials into phantoms so a spectator's client cannot
/// bind to live objects. Being wrong about where a packet keeps its serials is not
/// a missed remap: it is four bytes written over whatever the packet actually keeps
/// there. Being SILENT about an opcode nobody has checked is worse - the packet goes
/// out untouched, carrying live serials.
///
/// Every case below builds the real packet with the real writer and asserts against
/// its bytes, which is the only way this table can be kept honest: one maintained by
/// memory drifts from the packets it describes and nothing complains.
/// </summary>
public sealed class ReplaySerialInventoryTests
{
    private readonly ITestOutputHelper _out;
    public ReplaySerialInventoryTests(ITestOutputHelper output) => _out = output;

    private const uint LiveChar = 0x00012345;
    private const uint LiveItem = 0x40054321;
    private const uint LiveItem2 = 0x40098765;

    /// <summary>Drive the engine's own remap, the way a replay does.</summary>
    private static byte[]? Remap(byte[] packet, out ReplayState state)
    {
        state = new ReplayState();
        var m = typeof(RecordingEngine).GetMethod("RemapSerials",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (byte[]?)m.Invoke(null, [packet, 0u, state]);
    }

    private static uint Read(byte[] d, int offset) =>
        (uint)(d[offset] << 24 | d[offset + 1] << 16 | d[offset + 2] << 8 | d[offset + 3]);

    /// <summary>Every byte outside the named serial fields must survive untouched.</summary>
    private static void AssertOnlyChangedAt(byte[] before, byte[] after, params (int Offset, int Len)[] fields)
    {
        Assert.Equal(before.Length, after.Length);
        var allowed = new HashSet<int>(fields.SelectMany(f => Enumerable.Range(f.Offset, f.Len)));
        for (int i = 0; i < before.Length; i++)
            if (!allowed.Contains(i))
                Assert.True(before[i] == after[i],
                    $"byte {i} changed ({before[i]:X2} -> {after[i]:X2}) outside the serial fields");
    }

    // ------------------------------------------------------------------

    [Fact]
    public void AWorldItemKeepsTheAmountFlagItCarriesInItsSerial()
    {
        // amount > 1 makes the writer set 0x80000000 on the serial, and the client
        // reads that bit to decide whether an amount field follows.
        var packet = new PacketWorldItem(LiveItem, 0x0EED, amount: 5, x: 100, y: 200, z: 0, hue: 0)
            .Build().Span.ToArray();
        Assert.Equal(0x1A, packet[0]);
        Assert.Equal(LiveItem | 0x80000000, Read(packet, 3));

        var after = Remap(packet, out var state)!;
        uint written = Read(after, 3);

        _out.WriteLine($"0x1A serial {Read(packet, 3):X8} -> {written:X8}");

        // The flag has to survive, or the client stops expecting the amount and reads
        // the next two bytes as a coordinate.
        Assert.Equal(0x80000000u, written & 0x80000000u);
        Assert.NotEqual(LiveItem, written & ~0x80000000u);

        // And the mapping key is the item, not the flagged word - otherwise the same
        // item becomes two different phantoms depending on how it was sent.
        Assert.True(state.SerialMap.ContainsKey(LiveItem),
            "the item's own serial should be the map key, not the flagged value");
        AssertOnlyChangedAt(packet, after, (3, 4));
    }

    [Fact]
    public void AWorldItemWithoutAnAmountIsStillRemapped()
    {
        var packet = new PacketWorldItem(LiveItem, 0x0EED, amount: 1, x: 100, y: 200, z: 0, hue: 0)
            .Build().Span.ToArray();
        Assert.Equal(LiveItem, Read(packet, 3));

        var after = Remap(packet, out _)!;
        Assert.NotEqual(LiveItem, Read(after, 3));
        Assert.Equal(0u, Read(after, 3) & 0x80000000u);
        AssertOnlyChangedAt(packet, after, (3, 4));
    }

    [Fact]
    public void AContainerListingRemapsEveryItemAndItsContainer()
    {
        var entries = new List<PacketContainerContents.Entry>
        {
            new(LiveItem, 0x0EED, 0, 1, 10, 20, LiveChar, 0, 0),
            new(LiveItem2, 0x0EEE, 0, 3, 30, 40, LiveChar, 0, 1),
        };
        var packet = new PacketContainerContents(entries, useGridIndex: true).Build().Span.ToArray();
        Assert.Equal(0x3C, packet[0]);

        var after = Remap(packet, out var state)!;

        // 0x3C is a repeating structure, so no fixed offset list describes it - and
        // until now it was in no list at all, which meant a replayed container went
        // out with live serials in it.
        for (int i = 0; i < 2; i++)
        {
            int entry = 5 + i * 20;
            _out.WriteLine($"entry {i}: item {Read(packet, entry):X8} -> {Read(after, entry):X8}, " +
                           $"container {Read(packet, entry + 15):X8} -> {Read(after, entry + 15):X8}");
            Assert.NotEqual(Read(packet, entry), Read(after, entry));
            Assert.NotEqual(Read(packet, entry + 15), Read(after, entry + 15));
        }

        // Both items lived in the same container, so the container maps to ONE phantom.
        Assert.Equal(Read(after, 5 + 15), Read(after, 5 + 20 + 15));
        AssertOnlyChangedAt(packet, after, (5, 4), (20, 4), (25, 4), (40, 4));
        Assert.Equal(3, state.SerialMap.Count);   // two items and the container
    }

    [Fact]
    public void AContainerListingWithoutGridIndexesUsesTheOtherLayout()
    {
        var entries = new List<PacketContainerContents.Entry>
        {
            new(LiveItem, 0x0EED, 0, 1, 10, 20, LiveChar, 0, 0),
        };
        var packet = new PacketContainerContents(entries, useGridIndex: false).Build().Span.ToArray();

        var after = Remap(packet, out _)!;

        // The packet says which layout it is by its own size, not by a field: 19 bytes
        // per entry without the grid index, 20 with it. Reading the container serial
        // one byte late would write over the hue and leave the container live.
        Assert.NotEqual(Read(packet, 5), Read(after, 5));
        Assert.NotEqual(Read(packet, 5 + 14), Read(after, 5 + 14));
        AssertOnlyChangedAt(packet, after, (5, 4), (19, 4));
    }

    [Fact]
    public void ASwingRemapsBothCombatants()
    {
        var packet = new PacketSwing(LiveChar, LiveChar + 1).Build().Span.ToArray();
        Assert.Equal(0x2F, packet[0]);

        var after = Remap(packet, out _)!;

        _out.WriteLine($"0x2F attacker {Read(packet, 2):X8} -> {Read(after, 2):X8}, " +
                       $"defender {Read(packet, 6):X8} -> {Read(after, 6):X8}");
        Assert.NotEqual(Read(packet, 2), Read(after, 2));
        Assert.NotEqual(Read(packet, 6), Read(after, 6));
        AssertOnlyChangedAt(packet, after, (2, 4), (6, 4));
    }

    [Fact]
    public void AParticleEffectRemapsSourceTargetAndItsOwnUid()
    {
        var packet = new PacketEffectParticle(
            type: 0, srcSerial: LiveChar, dstSerial: LiveChar + 1, effectId: 0x36D4,
            srcX: 10, srcY: 20, srcZ: 0, dstX: 11, dstY: 21, dstZ: 0,
            speed: 5, duration: 10, fixedDir: false, explode: false,
            hue: 0, renderMode: 0, particleEffectId: 0x1234, explodeId: 0,
            explodeSound: 0, effectUid: LiveItem, particleType: 0).Build().Span.ToArray();
        Assert.Equal(0xC7, packet[0]);
        Assert.Equal(49, packet.Length);

        var after = Remap(packet, out _)!;

        _out.WriteLine($"0xC7 src {Read(packet, 2):X8}->{Read(after, 2):X8} " +
                       $"dst {Read(packet, 6):X8}->{Read(after, 6):X8} " +
                       $"uid {Read(packet, 42):X8}->{Read(after, 42):X8}");
        Assert.NotEqual(Read(packet, 2), Read(after, 2));
        Assert.NotEqual(Read(packet, 6), Read(after, 6));
        Assert.NotEqual(Read(packet, 42), Read(after, 42));
        AssertOnlyChangedAt(packet, after, (2, 4), (6, 4), (42, 4));
    }

    [Fact]
    public void AnSaWorldItemIsRemappedAfterItsDataTypeByte()
    {
        var packet = new PacketWorldItemSA(LiveItem, 0x0EED, 1, 100, 200, 0, 0).Build().Span.ToArray();
        Assert.Equal(0xF3, packet[0]);
        Assert.Equal(LiveItem, Read(packet, 4));

        var after = Remap(packet, out _)!;

        Assert.NotEqual(LiveItem, Read(after, 4));
        AssertOnlyChangedAt(packet, after, (4, 4));
    }

    [Fact]
    public void ADeathTakesBothTheMobileAndItsCorpseWithIt()
    {
        var packet = new PacketDeathAnimation(LiveChar, LiveItem).Build().Span.ToArray();
        Assert.Equal(0xAF, packet[0]);

        var after = Remap(packet, out _)!;

        Assert.NotEqual(LiveChar, Read(after, 1));
        Assert.NotEqual(LiveItem, Read(after, 5));
        AssertOnlyChangedAt(packet, after, (1, 4), (5, 4));
    }

    [Fact]
    public void ACorpsesEquipmentListIsRemappedPairByPair()
    {
        var entries = new List<(byte Layer, uint Serial)>
        {
            (5, LiveItem),
            (2, LiveItem2),
        };
        var packet = new PacketCorpseEquipment(LiveChar, entries).Build().Span.ToArray();
        Assert.Equal(0x89, packet[0]);

        var after = Remap(packet, out var state)!;

        // Corpse serial, then (layer, item) pairs until the zero terminator. The
        // layer bytes must survive - writing a serial one byte early would move every
        // item to a different body part and leave the real serials in place.
        _out.WriteLine($"corpse {Read(packet, 3):X8} -> {Read(after, 3):X8}; " +
                       $"layers {after[7]},{after[12]}");
        Assert.NotEqual(Read(packet, 3), Read(after, 3));
        Assert.Equal(packet[7], after[7]);
        Assert.Equal(packet[12], after[12]);
        Assert.NotEqual(Read(packet, 8), Read(after, 8));
        Assert.NotEqual(Read(packet, 13), Read(after, 13));
        Assert.Equal(3, state.SerialMap.Count);
    }

    [Fact]
    public void ADragAnimationRemapsBothEnds()
    {
        var packet = new PacketDragAnimation(0x0EED, 0, 1, LiveChar, 10, 20, 0,
                                             LiveChar + 1, 11, 21, 0).Build().Span.ToArray();
        Assert.Equal(0x23, packet[0]);

        var after = Remap(packet, out _)!;

        Assert.NotEqual(Read(packet, 8), Read(after, 8));
        Assert.NotEqual(Read(packet, 15), Read(after, 15));
        AssertOnlyChangedAt(packet, after, (8, 4), (15, 4));
    }

    [Fact]
    public void AHealthUpdateIsRemappedAndItsNumbersAreNot()
    {
        var packet = new PacketUpdateHealth(LiveChar, 100, 37).Build().Span.ToArray();
        Assert.Equal(0xA1, packet[0]);

        var after = Remap(packet, out _)!;

        Assert.NotEqual(LiveChar, Read(after, 1));
        AssertOnlyChangedAt(packet, after, (1, 4));
    }

    [Fact]
    public void AnUncheckedOpcodeIsRefusedInsteadOfForwarded()
    {
        // A packet the table says nothing about. It may or may not carry a serial -
        // that is the point: nobody has checked, so forwarding it is a guess made on
        // the spectator's behalf.
        var packet = new PacketAttackResponse(LiveChar).Build().Span.ToArray();
        Assert.Equal(0xAA, packet[0]);

        var after = Remap(packet, out var state);

        _out.WriteLine($"0x{packet[0]:X2}: forwarded={after != null}, refused={state.RefusedPackets}, " +
                       $"opcodes={string.Join(",", state.RefusedOpcodes.Select(o => o.ToString("X2")))}");
        Assert.Null(after);
        Assert.Equal(1, state.RefusedPackets);
        Assert.Contains((byte)0xAA, state.RefusedOpcodes);
    }

    [Fact]
    public void ACheckedSerialFreePacketStillGoesThrough()
    {
        // The other half of refusing: a packet whose writer has been read and holds
        // no serial is forwarded untouched. Without this the policy would quietly
        // strip sound and weather out of every replay.
        var sound = new PacketSound(0x0055, 100, 200, 0).Build().Span.ToArray();
        var after = Remap(sound, out var state)!;

        Assert.Equal(sound, after);
        Assert.Equal(0, state.RefusedPackets);
    }
}
