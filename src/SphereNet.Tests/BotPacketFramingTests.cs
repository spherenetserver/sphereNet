using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SphereNet.Game.Diagnostics;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The bot splits the server's stream by a table of packet lengths. That table knew
/// only the opcodes someone had needed, and read every other one as variable-length:
/// a fixed-size weather or sound packet then lent two of its payload bytes as a length
/// and the stream never lined up again. In an hour of soak no bot ever saw its own
/// 0x78, so none knew its backpack and no trade was ever offered; positions read out
/// of misaligned bytes showed up as Z jumps and snaps to 0,256. The table also gave
/// 0x24 the 9 bytes of a 7.0.9+ client while the server, answering the 7.0.0 client
/// the bot announces, sends 7.
/// </summary>
public sealed class BotPacketFramingTests
{
    private static byte[] Bytes(PacketWriter writer)
    {
        PacketBuffer buf = writer.Build();
        return buf.Data.AsSpan(0, buf.Length).ToArray();
    }

    [Fact]
    public void LoginBurstFromTheServerFramesPacketByPacket()
    {
        const uint me = 0x00001234;
        const uint pack = 0x40000100;
        var stream = new List<byte>();
        stream.AddRange(Bytes(new PacketGlobalLight(0)));
        stream.AddRange(Bytes(new PacketWeather(0xFF, 0, 0)));        // 0x65, fixed 4
        stream.AddRange(new byte[] { 0x5B, 12, 30, 0 });               // 0x5B game time, fixed 4
        stream.AddRange(new byte[] { 0x54, 1, 0, 0x2A, 0, 0, 0, 1, 0, 2, 0, 0 }); // 0x54 sound, fixed 12
        stream.AddRange(Bytes(new PacketOpenContainer(pack, 0x3C, useNewFormat: false)));
        stream.AddRange(Bytes(new PacketDrawObject(me, 0x190, 100, 200, 0, 0, 0, 0, 1,
            [(0x40000101u, (ushort)0x1517, (byte)0x05, (ushort)0), (pack, (ushort)0x0E75, (byte)0x15, (ushort)0)])));

        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        int packets = bot.ProcessDecompressed(stream.ToArray(), me);

        Assert.Equal(6, packets);
        Assert.Equal(pack, bot.World.BackpackSerial);
        Assert.True(bot.World.OpenContainers.ContainsKey(pack));
        Assert.Equal(2, bot.World.Equipment.Count);
    }

    private static byte[] Reject(byte seq, short x, short y, byte dir, sbyte z) =>
        [0x21, seq, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, dir, (byte)z];

    /// <summary>0x22 carries no position, so the bot has to apply its own step: a
    /// request facing another way turns, one along the facing moves a tile. It never
    /// did, so the bot believed it stood where it logged in all hour; paths were
    /// planned from there and each reject's correction read as a jump across the
    /// map.</summary>
    [Fact]
    public void AcceptedMovesTurnThenStep_AndOnlyARealDisagreementIsASnap()
    {
        const uint me = 0x00001234;
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        // Facing north (0) at 100,200.
        bot.ProcessDecompressed(Reject(0, 100, 200, 0, 5), me);
        Assert.Null(bot.World.PendingSnap);

        byte turn = bot.SendMovePacket(2);
        bot.ProcessDecompressed([0x22, turn, 1], me);
        Assert.Equal((100, 200, (byte)2), (bot.World.X, bot.World.Y, bot.World.Direction));

        byte step = bot.SendMovePacket(2 | 0x80);
        bot.ProcessDecompressed([0x22, step, 1], me);
        Assert.Equal((101, 200), (bot.World.X, bot.World.Y));

        // A reject taking back that step is no anomaly...
        bot.ProcessDecompressed(Reject(step, 100, 200, 2, 5), me);
        Assert.Null(bot.World.PendingSnap);
        Assert.Null(bot.World.PendingZJump);
        // ...a reject far from where the steps put the bot is.
        bot.ProcessDecompressed(Reject(0, 160, 200, 2, 5), me);
        Assert.NotNull(bot.World.PendingSnap);
        // As is Z moving on the very tile the server last reported.
        bot.ProcessDecompressed(Reject(0, 160, 200, 2, 60), me);
        Assert.NotNull(bot.World.PendingZJump);
    }

    /// <summary>The server goes from 255 to 1 (0 only opens a run after a reset), and
    /// a 0x20 resets the sequence on both sides.</summary>
    [Fact]
    public void WalkSequenceWrapsToOne_AndRestartsAfterARedraw()
    {
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        byte last = 0;
        for (int i = 0; i < 257; i++)
            last = bot.SendMovePacket(0);
        Assert.Equal(1, last); // 0, 1..255, then 1

        var draw = new byte[19];
        draw[0] = 0x20;
        bot.ProcessDecompressed(draw, 0x00001234);
        Assert.Equal(0, bot.SendMovePacket(0));
    }

    /// <summary>A refused step along the facing marks the tile ahead; the bot does
    /// not send that step again, and a walk toward a point routes round it. Bots that
    /// kept walking into the same water and walls made terrain nearly all of their
    /// reject rate.</summary>
    [Fact]
    public async Task ARefusedTileIsNotWalkedIntoAgain()
    {
        const uint me = 0x00001234;
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        // Facing east (2) at 100,200; a step east is refused where it stands.
        bot.ProcessDecompressed(Reject(0, 100, 200, 2, 0), me);
        byte step = bot.SendMovePacket(2);
        bot.ProcessDecompressed(Reject(step, 100, 200, 2, 0), me);
        Assert.True(bot.World.IsKnownBlocked(101, 200, Environment.TickCount64));

        int sent = bot.World.TotalMoveRequests;
        Assert.Equal(BotActionResult.Rejected, await bot.Actions.MoveDirection(2, CancellationToken.None));
        Assert.Equal(sent, bot.World.TotalMoveRequests);
        Assert.Equal(1, bot.World.SkippedBlockedSteps);
    }

    private static byte[] Content(uint container, params uint[] serials)
    {
        var bytes = new List<byte> { 0x3C, 0, 0, (byte)(serials.Length >> 8), (byte)serials.Length };
        foreach (uint serial in serials)
        {
            bytes.AddRange([(byte)(serial >> 24), (byte)(serial >> 16), (byte)(serial >> 8), (byte)serial]);
            bytes.AddRange([0x0F, 0x0C, 0, 0, 1, 0, 30, 0, 30, 0]);       // id, unk, amount, x, y, grid
            bytes.AddRange([(byte)(container >> 24), (byte)(container >> 16), (byte)(container >> 8), (byte)container]);
            bytes.AddRange([0, 0]);                                        // hue
        }
        bytes[1] = (byte)(bytes.Count >> 8);
        bytes[2] = (byte)bytes.Count;
        return bytes.ToArray();
    }

    /// <summary>A 0x3C is the whole content of the container, and a deleted item
    /// leaves it. The bot appended to its list and never removed from it, so it kept
    /// offering a potion long since drunk: 1855 trade offers in a soak were refused
    /// as "does not exist".</summary>
    [Fact]
    public void ContainerListFollowsTheServer()
    {
        const uint me = 0x00001234;
        const uint pack = 0x40000100;
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        var open = Bytes(new PacketOpenContainer(pack, 0x3C, useNewFormat: false));
        bot.ProcessDecompressed(open, me);
        bot.ProcessDecompressed(Content(pack, 0x40000201, 0x40000202), me);
        Assert.Equal(2, bot.World.OpenContainers[pack].Items.Count);

        bot.ProcessDecompressed([0x1D, 0x40, 0x00, 0x02, 0x01], me);      // 0x40000201 deleted
        Assert.Equal([0x40000202u], bot.World.OpenContainers[pack].Items.ConvertAll(i => i.Serial));

        // Opened again: the new listing replaces the old one, no duplicates.
        bot.ProcessDecompressed(open, me);
        bot.ProcessDecompressed(Content(pack, 0x40000202, 0x40000203), me);
        Assert.Equal([0x40000202u, 0x40000203u], bot.World.OpenContainers[pack].Items.ConvertAll(i => i.Serial));
        Assert.False(bot.World.KnownItems.ContainsKey(0x40000201));
    }

    /// <summary>A variable-length packet shorter than its own header means the stream
    /// is off a packet boundary. The bot skipped such bytes without a trace; it now
    /// counts them, so a misaligned stream shows in the soak report.</summary>
    [Fact]
    public void AStreamOffAPacketBoundaryIsCounted()
    {
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        // 0xAE (variable) claiming a length of 1.
        bot.ProcessDecompressed([0xAE, 0x00, 0x01], 0x00001234);
        Assert.Equal(1, bot.World.FramingErrors);
    }
}
