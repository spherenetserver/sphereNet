using SphereNet.Core.Collections;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging;
using SphereNet.Network.State;

namespace SphereNet.Tests;

[Collection("MoveClockSerial")]
public class MovementOverhaulTests
{
    // --- CircularBuffer ---

    [Fact]
    public void CircularBuffer_Push_ReadsBack()
    {
        var buf = new CircularBuffer<int>(5);
        buf.Push(10);
        buf.Push(20);
        buf.Push(30);

        Assert.Equal(3, buf.Count);
        Assert.Equal(10, buf[0]);
        Assert.Equal(20, buf[1]);
        Assert.Equal(30, buf[2]);
    }

    [Fact]
    public void CircularBuffer_Overflow_OldestDropped()
    {
        var buf = new CircularBuffer<int>(3);
        buf.Push(1);
        buf.Push(2);
        buf.Push(3);
        buf.Push(4);

        Assert.Equal(3, buf.Count);
        Assert.Equal(2, buf[0]);
        Assert.Equal(3, buf[1]);
        Assert.Equal(4, buf[2]);
    }

    [Fact]
    public void CircularBuffer_Clear_ResetsCount()
    {
        var buf = new CircularBuffer<int>(5);
        buf.Push(1);
        buf.Push(2);
        buf.Clear();

        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void CircularBuffer_ToArray_ReturnsOrdered()
    {
        var buf = new CircularBuffer<int>(3);
        buf.Push(10);
        buf.Push(20);
        buf.Push(30);
        buf.Push(40);

        var arr = buf.ToArray();
        Assert.Equal([20, 30, 40], arr);
    }

    // --- MovementHistory ---

    [Fact]
    public void MovementHistory_AverageInterval_Correct()
    {
        var history = new MovementHistory(10);
        history.Record(1000, Direction.North, false, false);
        history.Record(1400, Direction.North, false, false);
        history.Record(1800, Direction.North, false, false);
        history.Record(2200, Direction.North, false, false);
        history.Record(2600, Direction.North, false, false);

        double avg = history.AverageIntervalMs(5);
        Assert.Equal(400.0, avg);
    }

    [Fact]
    public void MovementHistory_BurstCount_DetectsRapidMoves()
    {
        var history = new MovementHistory(10);
        history.Record(1000, Direction.North, true, true);
        history.Record(1050, Direction.North, true, true);
        history.Record(1100, Direction.North, true, true);
        history.Record(1150, Direction.North, true, true);

        int bursts = history.CountBurstMoves(100, 4);
        Assert.Equal(3, bursts);
    }

    [Fact]
    public void MovementHistory_MinInterval_FindsSmallest()
    {
        var history = new MovementHistory(10);
        history.Record(1000, Direction.North, false, false);
        history.Record(1400, Direction.North, false, false);
        history.Record(1420, Direction.North, false, false);
        history.Record(1800, Direction.North, false, false);

        long min = history.MinIntervalMs(4);
        Assert.Equal(20, min);
    }

    [Fact]
    public void MovementHistory_Clear_ResetsAll()
    {
        var history = new MovementHistory(10);
        history.Record(1000, Direction.North, false, false);
        history.Record(1400, Direction.North, false, false);
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Equal(double.MaxValue, history.AverageIntervalMs(5));
    }

    // --- MovementCreditSystem ---

    [Fact]
    public void CreditSystem_AllowsMove_WhenSufficientCredit()
    {
        int credit = 1400;
        long lastTick = 1000;

        bool result = MovementCreditSystem.TryConsumeCredit(
            ref credit, ref lastTick, 200, 1400, 400, 1000);

        Assert.True(result);
        Assert.Equal(1000, credit);
    }

    [Fact]
    public void CreditSystem_RejectsMove_WhenInsufficientCredit()
    {
        int credit = 100;
        long lastTick = 1000;

        bool result = MovementCreditSystem.TryConsumeCredit(
            ref credit, ref lastTick, 200, 1400, 400, 1000);

        Assert.False(result);
    }

    [Fact]
    public void CreditSystem_Refill_AddsElapsedTime()
    {
        int credit = 500;
        long lastTick = 1000;

        MovementCreditSystem.RefillCredit(ref credit, ref lastTick, 1400, 1300);

        Assert.Equal(800, credit);
        Assert.Equal(1300, lastTick);
    }

    [Fact]
    public void CreditSystem_Refill_CapsAtMax()
    {
        int credit = 1300;
        long lastTick = 1000;

        MovementCreditSystem.RefillCredit(ref credit, ref lastTick, 1400, 2000);

        Assert.Equal(1400, credit);
    }

    [Fact]
    public void CreditSystem_FirstCall_InitializesToMax()
    {
        int credit = 0;
        long lastTick = 0;

        bool result = MovementCreditSystem.TryConsumeCredit(
            ref credit, ref lastTick, 200, 1400, 400, 5000);

        Assert.True(result);
        Assert.Equal(1000, credit);
    }

    // --- MovementQueueProcessor ---

    [Fact]
    public void Queue_Enqueue_Dequeue_FIFO()
    {
        var queue = new MovementQueueProcessor(5);
        queue.Enqueue(0x01, 1, 0, 1000);
        queue.Enqueue(0x02, 2, 0, 1100);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out byte dir, out byte seq, out _));
        Assert.Equal(0x01, dir);
        Assert.Equal(1, seq);

        Assert.True(queue.TryDequeue(out dir, out seq, out _));
        Assert.Equal(0x02, dir);
        Assert.Equal(2, seq);

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Queue_Full_RejectsNew()
    {
        var queue = new MovementQueueProcessor(2);
        Assert.True(queue.Enqueue(0x01, 1, 0, 1000));
        Assert.True(queue.Enqueue(0x02, 2, 0, 1100));
        Assert.True(queue.IsFull);
        Assert.False(queue.Enqueue(0x03, 3, 0, 1200));
    }

    [Fact]
    public void Queue_Clear_Empties()
    {
        var queue = new MovementQueueProcessor(5);
        queue.Enqueue(0x01, 1, 0, 1000);
        queue.Enqueue(0x02, 2, 0, 1100);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _, out _, out _));
    }

    // --- SpeedHackDetector ---

    [Fact]
    public void Detector_Normal_ReturnsNormal()
    {
        var detector = new SpeedHackDetector(1.5, 3, 60_000);
        var history = new MovementHistory(20);

        for (int i = 0; i < 6; i++)
            history.Record(1000 + i * 400, Direction.North, false, false);

        var verdict = detector.Analyze(history, false, false, 3000);
        Assert.Equal(SpeedVerdict.Normal, verdict);
    }

    [Fact]
    public void Detector_FastRate_ReturnsWarning()
    {
        var detector = new SpeedHackDetector(1.5, 3, 60_000);
        var history = new MovementHistory(20);

        for (int i = 0; i < 6; i++)
            history.Record(1000 + i * 200, Direction.North, false, false);

        var verdict = detector.Analyze(history, false, false, 2000);
        Assert.True(verdict == SpeedVerdict.Warning || verdict == SpeedVerdict.Violation);
    }

    [Fact]
    public void Detector_BurstPattern_ReturnsViolation()
    {
        var detector = new SpeedHackDetector(1.5, 3, 60_000);
        var history = new MovementHistory(20);

        for (int i = 0; i < 6; i++)
            history.Record(1000 + i * 50, Direction.North, false, false);

        var verdict = detector.Analyze(history, false, false, 1250);
        Assert.Equal(SpeedVerdict.Violation, verdict);
    }

    [Fact]
    public void Detector_Reset_ClearsState()
    {
        var detector = new SpeedHackDetector(1.5, 3, 60_000);
        var history = new MovementHistory(20);

        for (int i = 0; i < 6; i++)
            history.Record(1000 + i * 50, Direction.North, false, false);
        detector.Analyze(history, false, false, 1250);

        detector.Reset();
        history.Clear();

        for (int i = 0; i < 6; i++)
            history.Record(10000 + i * 400, Direction.North, false, false);
        var verdict = detector.Analyze(history, false, false, 12000);
        Assert.Equal(SpeedVerdict.Normal, verdict);
    }

    // --- Integration: Credit path ---

    private static GameClient CreateClient(GameWorld world, Character ch, int id = 1)
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var state = TestHarness.CreateActiveNetState(loggerFactory, id);
        var accounts = new AccountManager(loggerFactory);
        var client = new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
        client.SetEngines(movement: new MovementEngine(world));
        TestHarness.AttachCharacter(client, ch);
        return client;
    }

    [Fact]
    public void HandleMove_TokenBucket_StillWorksWhenCreditDisabled()
    {
        var savedCredit = GameClient.MovementCreditEnabled;
        var savedClock = GameClient.MoveClock;
        try
        {
            GameClient.MovementCreditEnabled = false;
            long fakeNow = 10000;
            GameClient.MoveClock = () => fakeNow;

            var loggerFactory = LoggerFactory.Create(b => { });
            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 6144, 4096);
            var ch = world.CreateCharacter();
            ch.Str = 50; ch.Dex = 50; ch.Int = 50;
            ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
            ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);

            bool moved = client.HandleMove(0x00, 0, 0);
            Assert.True(moved);
        }
        finally
        {
            GameClient.MovementCreditEnabled = savedCredit;
            GameClient.MoveClock = savedClock;
        }
    }

    [Fact]
    public void HandleMove_CreditPath_AcceptsNormalSpeed()
    {
        var savedCredit = GameClient.MovementCreditEnabled;
        var savedClock = GameClient.MoveClock;
        try
        {
            GameClient.MovementCreditEnabled = true;
            long fakeNow = 10000;
            GameClient.MoveClock = () => fakeNow;

            var loggerFactory = LoggerFactory.Create(b => { });
            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 6144, 4096);
            var ch = world.CreateCharacter();
            ch.Str = 50; ch.Dex = 50; ch.Int = 50;
            ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
            ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);

            bool moved = client.HandleMove(0x00, 0, 0);
            Assert.True(moved);
            Assert.Equal(999, ch.Y);

            fakeNow += 500;
            moved = client.HandleMove(0x00, 1, 0);
            Assert.True(moved);
            Assert.Equal(998, ch.Y);
        }
        finally
        {
            GameClient.MovementCreditEnabled = savedCredit;
            GameClient.MoveClock = savedClock;
        }
    }

    [Fact]
    public void ConfigurableDelays_SphereConfig_DefaultsMatch()
    {
        var config = new SphereConfig();

        Assert.Equal(400, config.WalkDelayFoot);
        Assert.Equal(200, config.WalkDelayMount);
        Assert.Equal(200, config.RunDelayFoot);
        Assert.Equal(100, config.RunDelayMount);
        Assert.False(config.MovementCreditEnabled);
        Assert.False(config.SpeedHackDetectionEnabled);
    }

    [Fact]
    public void MovementEngine_ConfigurableDelays_WorkCorrectly()
    {
        var saved = (MovementEngine.WalkDelayFoot, MovementEngine.WalkDelayMount,
                     MovementEngine.RunDelayFoot, MovementEngine.RunDelayMount);
        try
        {
            MovementEngine.WalkDelayFoot = 500;
            MovementEngine.WalkDelayMount = 250;
            MovementEngine.RunDelayFoot = 250;
            MovementEngine.RunDelayMount = 125;

            Assert.Equal(500, MovementEngine.GetMoveDelay(false, false));
            Assert.Equal(250, MovementEngine.GetMoveDelay(true, false));
            Assert.Equal(250, MovementEngine.GetMoveDelay(false, true));
            Assert.Equal(125, MovementEngine.GetMoveDelay(true, true));
        }
        finally
        {
            (MovementEngine.WalkDelayFoot, MovementEngine.WalkDelayMount,
             MovementEngine.RunDelayFoot, MovementEngine.RunDelayMount) = saved;
        }
    }

    [Fact]
    public void SphereConfig_Validate_WarnsForInvalidMoveDelay()
    {
        var config = new SphereConfig { ServPort = 2593, WalkDelayFoot = 0 };
        var warnings = config.Validate();
        Assert.Contains(warnings, w => w.Contains("WalkDelayFoot"));
    }

    [Fact]
    public void SphereConfig_Validate_WarnsForLargeQueue()
    {
        var config = new SphereConfig { ServPort = 2593, MovementQueueCapacity = 100 };
        var warnings = config.Validate();
        Assert.Contains(warnings, w => w.Contains("MovementQueueCapacity"));
    }

    [Fact]
    public void SphereConfig_Validate_NoWarningForDefaults()
    {
        var config = new SphereConfig { ServPort = 2593 };
        var warnings = config.Validate();
        Assert.DoesNotContain(warnings, w => w.Contains("WalkDelay"));
        Assert.DoesNotContain(warnings, w => w.Contains("MovementCredit"));
        Assert.DoesNotContain(warnings, w => w.Contains("MovementQueue"));
    }
}
