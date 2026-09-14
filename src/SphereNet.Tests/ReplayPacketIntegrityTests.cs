using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Recording;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Replaying a recording rewrites the serials in it so the spectator sees phantom
/// objects rather than the live world's (review findings B10 and B11).
///
/// The rewrite worked from a table of "where the serials are" that was wrong for
/// several opcodes, and being wrong there is not a missed remap - it is four bytes
/// written over whatever the packet actually keeps at that offset. A sound packet has
/// no serial at all and had its mode, sound id and volume overwritten; an effect keeps
/// two serials at 2 and 6 and had its TYPE byte scribbled on while the destination was
/// left pointing at a real object; a container item keeps its item serial at 1 and its
/// container serial near the end, and had its offset, amount and coordinates rewritten
/// instead.
///
/// The assertions below decode the real writer output rather than counting packets:
/// the fields that are NOT serials have to survive byte for byte, and the fields that
/// ARE serials all have to move.
/// </summary>
public sealed class ReplayPacketIntegrityTests
{
    private readonly ITestOutputHelper _out;
    public ReplayPacketIntegrityTests(ITestOutputHelper output) => _out = output;

    private const uint RealChar = 0x00001234;
    private const uint RealItem = 0x40005678;

    /// <summary>Run one packet through the replay remapper exactly as playback does.</summary>
    private static byte[] Remap(byte[] packet)
    {
        var engineType = typeof(RecordingEngine);
        var stateType = engineType.GetNestedType("ReplayState", BindingFlags.NonPublic)
                        ?? engineType.Assembly.GetTypes().First(t => t.Name == "ReplayState");
        object state = Activator.CreateInstance(stateType, nonPublic: true)!;

        var remap = engineType.GetMethod("RemapSerials",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (byte[]?)remap.Invoke(null, [packet, 0u, state]) ?? packet;
    }

    private static byte[] Bytes(PacketWriter writer) => writer.Build().Span.ToArray();

    private static uint ReadUInt32(byte[] d, int at) =>
        (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

    // ---- B10: the fields that are not serials must survive ---------------

    [Fact]
    public void ASoundPacketIsLeftAloneBecauseItHasNoSerial()
    {
        byte[] original = Bytes(new PacketSound(0x0123, 100, 200, 5));
        byte[] replayed = Remap(original);

        _out.WriteLine("original: " + Convert.ToHexString(original));
        _out.WriteLine("replayed: " + Convert.ToHexString(replayed));

        // 0x54 is mode, sound id, volume and a position. Treating byte 1 as a serial
        // rewrote the first three of those: the review's run turned 540101230000...
        // into 543FFF000100..., a different sound at a different volume.
        Assert.Equal(original, replayed);
    }

    [Fact]
    public void AnEffectMovesBothOfItsSerialsAndKeepsItsType()
    {
        byte[] original = Bytes(new PacketEffect(
            type: 0x03, srcSerial: RealChar, dstSerial: RealItem, effectId: 0x36BD,
            srcX: 10, srcY: 20, srcZ: 0, dstX: 30, dstY: 40, dstZ: 0,
            speed: 5, duration: 30, fixedDir: false, explode: false));
        byte[] replayed = Remap(original);

        _out.WriteLine($"type {original[1]} -> {replayed[1]}");
        _out.WriteLine($"src {ReadUInt32(original, 2):X8} -> {ReadUInt32(replayed, 2):X8}");
        _out.WriteLine($"dst {ReadUInt32(original, 6):X8} -> {ReadUInt32(replayed, 6):X8}");

        // The serials live at 2 and 6; byte 1 is the effect TYPE. Remapping byte 1
        // corrupted the type, mangled the source serial's high bytes, and left the
        // destination pointing at a real object in the live world.
        Assert.Equal(original[1], replayed[1]);
        Assert.NotEqual(RealChar, ReadUInt32(replayed, 2));
        Assert.NotEqual(RealItem, ReadUInt32(replayed, 6));
        Assert.NotEqual(0u, ReadUInt32(replayed, 2));
        Assert.NotEqual(0u, ReadUInt32(replayed, 6));

        // Everything after the two serials is geometry and timing, untouched.
        Assert.Equal(original.Skip(10).ToArray(), replayed.Skip(10).ToArray());
    }

    [Fact]
    public void AContainerItemMovesItsItemAndContainerSerials()
    {
        byte[] original = Bytes(new PacketContainerItem(
            RealItem, itemId: 0x0EED, offset: 3, amount: 250, x: 44, y: 55,
            containerSerial: RealChar, hue: 0x0021, useGridIndex: true));
        byte[] replayed = Remap(original);

        _out.WriteLine("original: " + Convert.ToHexString(original));
        _out.WriteLine("replayed: " + Convert.ToHexString(replayed));

        // Item serial at 1, container serial at 15 on the 21-byte grid form. The table
        // said "7", which is the stack offset - so the amount and the coordinates took
        // the write and both serials stayed real.
        Assert.NotEqual(RealItem, ReadUInt32(replayed, 1));
        Assert.NotEqual(RealChar, ReadUInt32(replayed, 15));

        // itemId, offset, amount, x, y, grid index and hue all survive.
        Assert.Equal(original[5], replayed[5]);
        Assert.Equal(original[6], replayed[6]);
        Assert.Equal(original[7], replayed[7]);      // stack offset
        Assert.Equal(original[8], replayed[8]);      // amount high
        Assert.Equal(original[9], replayed[9]);      // amount low
        Assert.Equal(original[10], replayed[10]);    // x high
        Assert.Equal(original[12], replayed[12]);    // y high
        Assert.Equal(original[19], replayed[19]);    // hue high
        Assert.Equal(original[20], replayed[20]);    // hue low
    }

    [Fact]
    public void TheOlderContainerItemFormKeepsItsOwnLayout()
    {
        byte[] original = Bytes(new PacketContainerItem(
            RealItem, itemId: 0x0EED, offset: 3, amount: 250, x: 44, y: 55,
            containerSerial: RealChar, hue: 0x0021, useGridIndex: false));
        byte[] replayed = Remap(original);

        _out.WriteLine($"20-byte form, container serial at 14: " +
                       $"{ReadUInt32(original, 14):X8} -> {ReadUInt32(replayed, 14):X8}");

        // Without the grid byte the container serial sits one earlier. A single fixed
        // offset cannot be right for both client eras.
        Assert.Equal(20, original.Length);
        Assert.NotEqual(RealItem, ReadUInt32(replayed, 1));
        Assert.NotEqual(RealChar, ReadUInt32(replayed, 14));
        Assert.Equal(original[7], replayed[7]);
    }

    [Fact]
    public void AGlobalLightLevelHasNoSerialEither()
    {
        byte[] original = Bytes(new PacketGlobalLight(0x15));
        byte[] replayed = Remap(original);

        _out.WriteLine("light: " + Convert.ToHexString(original) + " -> " + Convert.ToHexString(replayed));

        // 0x4F is two bytes: opcode and level. There is nowhere for a serial to be.
        Assert.Equal(original, replayed);
    }

    [Fact]
    public void APacketThatDoesCarryASerialStillGetsRemapped()
    {
        byte[] original = Bytes(new PacketDeleteObject(RealChar));
        byte[] replayed = Remap(original);

        // The control. Tightening the table must not stop the remapping that works -
        // a spectator seeing a real serial is the bug the remapper exists to prevent.
        Assert.NotEqual(RealChar, ReadUInt32(replayed, 1));
        Assert.NotEqual(0u, ReadUInt32(replayed, 1));
    }

    // ---- B11: a truncated recording is not a recording -------------------

    [Fact]
    public void ATruncatedRecordingIsRefusedRatherThanShortened()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rec_test.rec");
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write("rec_test");             // id
                bw.Write("gm");                   // recorder
                bw.Write(DateTime.UtcNow.Ticks);
                bw.Write(1000);                   // duration
                bw.Write(2);                      // packet count
                bw.Write(0u);                     // recorder uid
                bw.Write((short)100); bw.Write((short)100);
                bw.Write((sbyte)0); bw.Write((byte)0);

                bw.Write(0);                      // packet 1
                bw.Write((ushort)4);
                bw.Write(new byte[] { 1, 2, 3, 4 });

                bw.Write(10);                     // packet 2 says 12 bytes...
                bw.Write((ushort)12);
                bw.Write(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });   // ...and has 9
            }

            var engine = new RecordingEngine(dir);
            var session = engine.LoadRecordingById("rec_test");

            _out.WriteLine($"truncated recording -> {(session == null ? "refused" : $"{session.Packets.Count} packets")}");

            // ReadBytes returns what it has rather than throwing, so the loader built a
            // session whose last packet is three bytes short of what it claims. Playing
            // that back sends a malformed packet to a real client.
            Assert.Null(session);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AWellFormedRecordingStillLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rec_ok.rec");
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write("rec_ok");
                bw.Write("gm");
                bw.Write(DateTime.UtcNow.Ticks);
                bw.Write(1000);
                bw.Write(1);
                bw.Write(0u);
                bw.Write((short)100); bw.Write((short)100);
                bw.Write((sbyte)0); bw.Write((byte)0);
                bw.Write(0);
                bw.Write((ushort)4);
                bw.Write(new byte[] { 1, 2, 3, 4 });
            }

            var engine = new RecordingEngine(dir);
            var session = engine.LoadRecordingById("rec_ok");

            // The control: refusing a short read must not refuse a complete file.
            Assert.NotNull(session);
            Assert.Single(session!.Packets);
            Assert.Equal(4, session.Packets[0].Data.Length);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
