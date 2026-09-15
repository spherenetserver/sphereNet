using System;
using System.Threading;
using System.Threading.Tasks;
using SphereNet.Game.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The bot has to be right about its own results before it can measure a server
/// (review work item D09).
///
/// A load report is only worth what its instrument is worth. The bot sends a move with
/// a sequence byte and the server echoes that byte back in the accept (0x22) or the
/// reject (0x21) — and the bot ignored it, completing whatever move happened to be in
/// flight. So a late answer to a move that had already timed out was counted as the
/// NEXT move's success: the one reading in the report that must never be invented is
/// the one that was.
///
/// The same principle applies to the requests nobody waits for. An attack leaves the
/// client and the server may refuse it, ignore it, or kill something with it; calling
/// that Success made "packets I sent" and "things that happened in the game" the same
/// number.
/// </summary>
public sealed class BotAckCorrelationTests
{
    private readonly ITestOutputHelper _out;
    public BotAckCorrelationTests(ITestOutputHelper output) => _out = output;

    /// <summary>A bot that is "playing" with no socket behind it: SendRawPacket drops
    /// the bytes, which is all these tests need - what matters is what the RECEIVE
    /// side does with an ack.</summary>
    private static (BotClient Bot, BotActionApi Api) Playing()
    {
        var bot = new BotClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            State = BotState.Playing,
        };
        return (bot, bot.Actions);
    }

    private static byte[] MoveAck(byte sequence) => [0x22, sequence, 0x00];

    private static byte[] MoveReject(byte sequence, short x, short y) =>
    [
        0x21, sequence,
        (byte)(x >> 8), (byte)x,
        (byte)(y >> 8), (byte)y,
        0x00, 0x00,
    ];

    // ---- the late ack ----------------------------------------------------

    [Fact]
    public async Task AnAckForAMoveThatAlreadyTimedOutDoesNotPassTheNextOne()
    {
        var (bot, api) = Playing();

        // The first move gets no answer and times out.
        var first = api.MoveDirection(0, CancellationToken.None);
        Assert.Equal(BotActionResult.TimedOut, await first);

        // The second move goes out with the next sequence, and the FIRST move's ack
        // arrives now - the ordinary shape of a server that was briefly slow.
        var second = api.MoveDirection(0, CancellationToken.None);
        await Task.Delay(20);
        bot.ProcessIncomingPacket(MoveAck(0));

        var result = await second;
        _out.WriteLine($"second move: {result}; acks={bot.World.TotalMoveAcks} " +
                       $"late={bot.World.LateMoveAcks} requests={bot.World.TotalMoveRequests}");

        // The second move was never answered, so it timed out too. Counting it as a
        // success is the false positive that makes a load report say the server kept
        // up when it did not.
        Assert.Equal(BotActionResult.TimedOut, result);
        Assert.Equal(1, bot.World.LateMoveAcks);
        Assert.Equal(0, bot.World.TotalMoveAcks);
        Assert.Equal(2, bot.World.TotalMoveRequests);
    }

    [Fact]
    public async Task TheAckForTheMoveInFlightIsAccepted()
    {
        // The control: sequence matching must not have stopped ordinary moves from
        // completing. Every assertion above would pass if acks were ignored entirely.
        var (bot, api) = Playing();

        var move = api.MoveDirection(0, CancellationToken.None);
        await Task.Delay(20);
        bot.ProcessIncomingPacket(MoveAck(0));

        Assert.Equal(BotActionResult.Success, await move);
        Assert.Equal(1, bot.World.TotalMoveAcks);
        Assert.Equal(0, bot.World.LateMoveAcks);
    }

    [Fact]
    public async Task ARejectForAnEarlierMoveStillCorrectsThePositionWithoutAnsweringThisOne()
    {
        var (bot, api) = Playing();

        // Two moves time out, so the sequence in flight is 2 and the reject below
        // belongs to move 1.
        Assert.Equal(BotActionResult.TimedOut, await api.MoveDirection(0, CancellationToken.None));
        Assert.Equal(BotActionResult.TimedOut, await api.MoveDirection(0, CancellationToken.None));

        var third = api.MoveDirection(0, CancellationToken.None);
        await Task.Delay(20);
        bot.ProcessIncomingPacket(MoveReject(1, 1234, 5678));

        // Where the server says the character is applies whatever sequence it carries;
        // whether it ANSWERS the move in flight is a separate question.
        _out.WriteLine($"after a late reject: pos=({bot.World.X},{bot.World.Y}) " +
                       $"late={bot.World.LateMoveAcks}");
        Assert.Equal(1234, bot.World.X);
        Assert.Equal(5678, bot.World.Y);
        Assert.Equal(1, bot.World.LateMoveAcks);
        Assert.Equal(BotActionResult.TimedOut, await third);
    }

    [Fact]
    public async Task ARejectedMoveIsAnsweredAndTheSequenceStartsAgainAtZero()
    {
        // What the real client does, and what the server's reject path is written
        // against: clear the queue and resend from sequence 0. A bot that kept
        // counting up would be out of step with the server from the first reject on,
        // and every later ack would look late.
        var (bot, api) = Playing();

        Assert.Equal(BotActionResult.TimedOut, await api.MoveDirection(0, CancellationToken.None));
        var second = api.MoveDirection(0, CancellationToken.None);
        await Task.Delay(20);
        bot.ProcessIncomingPacket(MoveReject(1, 100, 100));
        Assert.Equal(BotActionResult.Rejected, await second);

        var afterReject = api.MoveDirection(0, CancellationToken.None);
        await Task.Delay(20);
        bot.ProcessIncomingPacket(MoveAck(0));           // the sequence began again
        Assert.Equal(BotActionResult.Success, await afterReject);
    }

    [Fact]
    public async Task TheSequenceWrapsWithoutLosingTrack()
    {
        var (bot, api) = Playing();

        // Walk the sequence right round the byte. Each move is answered with the
        // sequence it went out with, so every one of them must complete.
        for (int i = 0; i < 300; i++)
        {
            var move = api.MoveDirection(0, CancellationToken.None);
            await Task.Delay(1);
            bot.ProcessIncomingPacket(MoveAck((byte)(i & 0xFF)));
            Assert.Equal(BotActionResult.Success, await move);
        }

        _out.WriteLine($"300 moves across the wrap: acks={bot.World.TotalMoveAcks} " +
                       $"late={bot.World.LateMoveAcks}");
        Assert.Equal(300, bot.World.TotalMoveAcks);
        Assert.Equal(0, bot.World.LateMoveAcks);
    }

    // ---- sent is not done ------------------------------------------------

    [Fact]
    public async Task AnAttackReportsThatItWasSentAndNotThatItWorked()
    {
        var (bot, api) = Playing();

        var result = await api.Attack(0x40000001);

        // The server may refuse it, ignore it, or kill something with it, and none of
        // that is observed here. A report that adds these to confirmed actions is
        // counting its own packets.
        _out.WriteLine($"attack: {result}; requests sent={bot.World.TotalRequestsSent}");
        Assert.Equal(BotActionResult.Sent, result);
        Assert.Equal(1, bot.World.TotalRequestsSent);
        Assert.Equal(0, bot.World.TotalMoveAcks);
    }

    [Fact]
    public async Task ADisconnectedBotReportsThatRatherThanSent()
    {
        var bot = new BotClient(2, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(BotActionResult.Disconnected, await bot.Actions.Attack(0x40000001));
        Assert.Equal(0, bot.World.TotalRequestsSent);
    }
}
