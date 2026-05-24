using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Network.State;
using SphereNet.MapData;

namespace SphereNet.Tests;

public class MovementTests
{
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

    private static (GameWorld world, MovementEngine engine) CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var engine = new MovementEngine(world);
        return (world, engine);
    }

    [Fact]
    public void TryMove_NormalMove_Succeeds()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
        ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        bool moved = engine.TryMove(ch, Direction.North, false, 1);
        Assert.True(moved);
        Assert.Equal(999, ch.Y);
    }

    [Fact]
    public void TryMove_Frozen_Fails()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.MaxStam = 50; ch.Stam = 50;
        ch.SetStatFlag(StatFlag.Freeze);
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        bool moved = engine.TryMove(ch, Direction.East, false, 1);
        Assert.False(moved);
    }

    [Fact]
    public void MapData_Load_MissingRequiredFiles_ThrowsExplicitHealthError()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_map_missing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var manager = new MapDataManager(tmp);
            var ex = Assert.Throws<FileNotFoundException>(() => manager.Load());
            Assert.Contains("tiledata.mul", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CanWalkTo_RejectsNonAdjacentAndBlockedMobileInFixtureWorld()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50;
        ch.MaxStam = 100;
        ch.Stam = 0;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        Assert.False(engine.CanWalkTo(ch, new Point3D(1002, 1000, 0, 0)));

        var blocker = world.CreateCharacter();
        blocker.Str = 50;
        blocker.MaxStam = 100;
        blocker.Stam = 100;
        world.PlaceCharacter(blocker, new Point3D(1001, 1000, 0, 0));

        Assert.False(engine.CanWalkTo(ch, new Point3D(1001, 1000, 0, 0)));

        blocker.Kill();
        Assert.True(engine.CanWalkTo(ch, new Point3D(1001, 1000, 0, 0)));
    }

    [Fact]
    public void TryMove_AllDirections_UpdatesPosition()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.MaxHits = 50; ch.MaxStam = 50;
        ch.Hits = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        engine.TryMove(ch, Direction.East, false, 1);
        Assert.Equal(1001, ch.X);

        engine.TryMove(ch, Direction.South, false, 2);
        Assert.Equal(1001, ch.Y);

        engine.TryMove(ch, Direction.West, false, 3);
        Assert.Equal(1000, ch.X);

        engine.TryMove(ch, Direction.North, false, 4);
        Assert.Equal(1000, ch.Y);
    }

    [Fact]
    public void TryMove_DirectionChange_StillMoves()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.MaxHits = 50; ch.MaxStam = 50;
        ch.Hits = 50; ch.Stam = 50;
        ch.Direction = Direction.North;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        bool moved = engine.TryMove(ch, Direction.East, false, 1);

        Assert.True(moved);
        Assert.Equal(1001, ch.X);
        Assert.Equal(Direction.East, ch.Direction);
    }

    [Fact]
    public void Character_Direction_StripsRunningBit()
    {
        var ch = new Character();
        ch.Direction = Direction.East | Direction.Running;
        Assert.Equal(Direction.East, ch.Direction);
    }

    [Fact]
    public void BodyMatchesDefHash_DetectsCorruptSaveBody()
    {
        var ch = new Character { CharDefIndex = 0x1D03DB, BodyId = 0x03DB, IsPlayer = true };
        Assert.True(CharDefHelper.BodyMatchesDefHash(ch));
    }

    [Fact]
    public void BodyMatchesDefHash_AllowsNumericCharDefBody()
    {
        var ch = new Character { CharDefIndex = 0x00DC, BodyId = 0x00DC, IsPlayer = true };
        Assert.False(CharDefHelper.BodyMatchesDefHash(ch));
    }

    [Fact]
    public void ClearTransientVisualState_RemovesPersistedSpellAndReplayVisuals()
    {
        var ch = new Character { BodyId = 0x0190 };
        ch.Hue = new Color(0x03EC);
        ch.SetStatFlag(StatFlag.Freeze);
        ch.SetStatFlag(StatFlag.Invisible);
        ch.SetStatFlag(StatFlag.Hidden);
        ch.SetStatFlag(StatFlag.Reflection);

        Assert.True(ch.ClearTransientVisualState());
        Assert.Equal((ushort)0, ch.Hue.Value);
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
        Assert.False(ch.IsStatFlag(StatFlag.Invisible));
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
        Assert.False(ch.IsStatFlag(StatFlag.Reflection));
    }

    [Fact]
    public void GetMoveDelay_Running_FasterThanWalking()
    {
        int walkFoot = MovementEngine.GetMoveDelay(false, false);
        int runFoot = MovementEngine.GetMoveDelay(false, true);
        int walkMount = MovementEngine.GetMoveDelay(true, false);
        int runMount = MovementEngine.GetMoveDelay(true, true);

        Assert.True(runFoot < walkFoot);
        Assert.True(runMount < walkMount);
        Assert.True(walkMount < walkFoot);
    }

    [Fact]
    public void HandleMove_SeqMismatch_RejectsAndResetsSequence()
    {
        var (world, _) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        var client = CreateClient(world, ch);
        var state = client.NetState;
        state.WalkSequence = 2;

        client.HandleMove((byte)Direction.East, 1, 0);

        Assert.Equal(0, state.WalkSequence);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] == 0x21);
        Assert.Equal(1000, ch.X);
    }

    [Fact]
    public void HandleMove_CollisionReject_RedrawsSelfAfterMoveReject()
    {
        var (world, _) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.SetStatFlag(StatFlag.Freeze);
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        var client = CreateClient(world, ch);

        Assert.False(client.HandleMove((byte)Direction.East, 0, 0));

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        int rejectIndex = packets.FindIndex(p => p.Span.Length > 0 && p.Span[0] == 0x21);
        int drawIndex = packets.FindIndex(p => p.Span.Length > 0 && p.Span[0] == 0x20);
        int drawObjectIndex = packets.FindIndex(p => p.Span.Length > 0 && p.Span[0] == 0x78);
        Assert.InRange(rejectIndex, 0, int.MaxValue);
        Assert.True(drawIndex > rejectIndex);
        Assert.True(drawObjectIndex > drawIndex);
        Assert.Equal(0, client.NetState.WalkSequence);
        Assert.Equal(1000, ch.X);
    }

    [Fact]
    public void HandleMove_TooFast_RejectsWithMoveReject()
    {
        var oldClock = GameClient.MoveClock;
        var oldTolerance = GameClient.MoveToleranceMs;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        var oldResync = GameClient.MoveRejectResyncMs;
        try
        {
            long now = 1_000;
            GameClient.MoveClock = () => now;
            GameClient.MoveToleranceMs = 0;
            GameClient.WalkBufferMax = 10;
            GameClient.WalkRegenPerSecond = 0;
            GameClient.MoveRejectResyncMs = 0;

            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);
            var state = client.NetState;

            client.HandleMove((byte)Direction.East, 0, 0);
            TestHarness.GetQueuedPackets(state).Clear();
            now += 1;
            client.HandleMove((byte)Direction.East, 1, 0);

            Assert.Equal(1001, ch.X);
            Assert.Equal(0, state.WalkSequence);
            Assert.Contains(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] == 0x21);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTolerance;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
            GameClient.MoveRejectResyncMs = oldResync;
        }
    }

    [Fact]
    public void HandleMove_WalkBuffer_AllowsBurstThenRejects()
    {
        var oldClock = GameClient.MoveClock;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        var oldTolerance = GameClient.MoveToleranceMs;
        try
        {
            long now = 1_000;
            GameClient.MoveClock = () => now;
            GameClient.WalkBufferMax = 1;
            GameClient.WalkRegenPerSecond = 0;
            GameClient.MoveToleranceMs = 10_000;

            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);
            var state = client.NetState;

            client.HandleMove((byte)Direction.East, 0, 0);
            TestHarness.GetQueuedPackets(state).Clear();
            now += MovementEngine.GetMoveDelay(false, false) + 1;
            client.HandleMove((byte)Direction.East, 1, 0);

            Assert.Equal(1001, ch.X);
            Assert.Equal(0, state.WalkSequence);
            Assert.Contains(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] == 0x21);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
            GameClient.MoveToleranceMs = oldTolerance;
        }
    }

    [Fact]
    public void HandleMove_ThrottleDoesNotAccumulateFutureDebt()
    {
        var oldClock = GameClient.MoveClock;
        var oldTolerance = GameClient.MoveToleranceMs;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        try
        {
            long now = 1_000;
            GameClient.MoveClock = () => now;
            GameClient.MoveToleranceMs = 80;
            GameClient.WalkBufferMax = 100;
            GameClient.WalkRegenPerSecond = 100;

            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);
            var state = client.NetState;

            int delay = MovementEngine.GetMoveDelay(false, false);
            for (byte seq = 0; seq < 8; seq++)
            {
                client.HandleMove((byte)Direction.East, seq, 0);
                Assert.DoesNotContain(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] == 0x21);
                TestHarness.GetQueuedPackets(state).Clear();
                now += delay - GameClient.MoveToleranceMs;
            }

            Assert.Equal(1008, ch.X);
            Assert.Equal(8, state.WalkSequence);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTolerance;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
        }
    }

    [Fact]
    public void HandleMovementBatch_ProcessesMultipleStepsWithoutThrottleReject()
    {
        var oldClock = GameClient.MoveClock;
        var oldTolerance = GameClient.MoveToleranceMs;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        try
        {
            GameClient.MoveClock = () => 1_000;
            GameClient.MoveToleranceMs = 0;
            GameClient.WalkBufferMax = 100;
            GameClient.WalkRegenPerSecond = 100;

            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);
            var state = client.NetState;

            client.HandleMovementBatch([
                new SphereNet.Network.State.MovementStep((byte)Direction.East, 0, 0, 0),
                new SphereNet.Network.State.MovementStep((byte)Direction.East, 1, 0, 0),
                new SphereNet.Network.State.MovementStep((byte)Direction.East, 2, 0, 0)
            ]);

            Assert.Equal(1003, ch.X);
            Assert.Equal(3, state.WalkSequence);
            Assert.DoesNotContain(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] == 0x21);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTolerance;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
        }
    }

    [Fact]
    public void HandleMove_AfterRejectResyncWindow_DoesNotAcceptQueuedPredictedSteps()
    {
        var oldClock = GameClient.MoveClock;
        var oldTolerance = GameClient.MoveToleranceMs;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        var oldResync = GameClient.MoveRejectResyncMs;
        try
        {
            long now = 1_000;
            GameClient.MoveClock = () => now;
            GameClient.MoveToleranceMs = 0;
            GameClient.WalkBufferMax = 100;
            GameClient.WalkRegenPerSecond = 100;
            GameClient.MoveRejectResyncMs = 150;

            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
            ch.MaxStam = 50; ch.Stam = 50;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);

            Assert.True(client.HandleMove((byte)Direction.East, 0, 0));
            Assert.Equal(1001, ch.X);

            Assert.False(client.HandleMove((byte)Direction.East, 1, 0));
            Assert.Equal(1001, ch.X);

            now += 50;
            Assert.False(client.HandleMove((byte)Direction.East, 0, 0));
            Assert.Equal(1001, ch.X);

            now += 400;
            Assert.True(client.HandleMove((byte)Direction.East, 0, 0));
            Assert.Equal(1002, ch.X);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTolerance;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
            GameClient.MoveRejectResyncMs = oldResync;
        }
    }

    [Fact]
    public void ClientMovementSoakMatrix_RunsClassicAndModernProfilesWithoutRejectSpike()
    {
        var oldClock = GameClient.MoveClock;
        var oldTolerance = GameClient.MoveToleranceMs;
        var oldBuffer = GameClient.WalkBufferMax;
        var oldRegen = GameClient.WalkRegenPerSecond;
        try
        {
            long now = 10_000;
            GameClient.MoveClock = () => now;
            GameClient.MoveToleranceMs = 25;
            GameClient.WalkBufferMax = 100;
            GameClient.WalkRegenPerSecond = 100;

            var profiles = new[]
            {
                ("classic-0x02-run", false),
                ("modern-0xF0-batch-run", true),
            };

            foreach (var (name, batched) in profiles)
            {
                var (world, _) = CreateWorld();
                var ch = world.CreateCharacter();
                ch.IsPlayer = true;
                ch.MaxStam = 100;
                ch.Stam = 100;
                world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
                var client = CreateClient(world, ch);
                var state = client.NetState;

                int rejects = 0;
                int seq = 0;
                for (int i = 0; i < 50; i++)
                {
                    if (batched)
                    {
                        client.HandleMovementBatch([
                            new SphereNet.Network.State.MovementStep((byte)(Direction.East | Direction.Running), (byte)seq++, 0, 0),
                            new SphereNet.Network.State.MovementStep((byte)(Direction.East | Direction.Running), (byte)seq++, 0, 0),
                            new SphereNet.Network.State.MovementStep((byte)(Direction.SouthEast | Direction.Running), (byte)seq++, 0, 0),
                            new SphereNet.Network.State.MovementStep((byte)(Direction.South | Direction.Running), (byte)seq++, 0, 0)
                        ]);
                        now += MovementEngine.GetMoveDelay(false, true) * 4;
                    }
                    else
                    {
                        var dirs = new[]
                        {
                            Direction.East | Direction.Running,
                            Direction.East | Direction.Running,
                            Direction.SouthEast | Direction.Running,
                            Direction.South | Direction.Running,
                        };
                        foreach (var dir in dirs)
                        {
                            client.HandleMove((byte)dir, (byte)seq++, 0);
                            now += MovementEngine.GetMoveDelay(false, true);
                        }
                    }

                    rejects += TestHarness.GetQueuedPackets(state).Count(p => p.Span.Length > 0 && p.Span[0] == 0x21);
                    TestHarness.GetQueuedPackets(state).Clear();
                }

                Assert.Equal(0, rejects);
                Assert.Equal(200, state.WalkSequence);
                Assert.True(ch.X > 1000, $"{name} did not move east");
                Assert.True(ch.Y > 1000, $"{name} did not move south/diagonal");
            }
        }
        finally
        {
            GameClient.MoveClock = oldClock;
            GameClient.MoveToleranceMs = oldTolerance;
            GameClient.WalkBufferMax = oldBuffer;
            GameClient.WalkRegenPerSecond = oldRegen;
        }
    }

    [Fact]
    public void HandleMove_GmBypassesThrottleButNotSequence()
    {
        var oldClock = GameClient.MoveClock;
        try
        {
            long now = 1_000;
            GameClient.MoveClock = () => now;
            var (world, _) = CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.PrivLevel = PrivLevel.GM;
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var client = CreateClient(world, ch);
            var state = client.NetState;

            client.HandleMove((byte)Direction.East, 0, 0);
            now++;
            client.HandleMove((byte)Direction.East, 1, 0);

            Assert.Equal(1002, ch.X);
            state.WalkSequence = 9;
            client.HandleMove((byte)Direction.East, 1, 0);
            Assert.Equal(0, state.WalkSequence);
        }
        finally
        {
            GameClient.MoveClock = oldClock;
        }
    }
}
