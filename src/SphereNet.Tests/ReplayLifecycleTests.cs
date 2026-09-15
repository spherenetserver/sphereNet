using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Recording;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Driving a replay: seeking, pausing, stopping, and what the capture is allowed to
/// cost while it is still running (review work item D10).
///
/// A seek is the interesting one because the client cannot be told to rewind. Going
/// back means undoing what it was shown, and going forward means it must not be left
/// referring to objects it never saw, so both directions are the same operation: throw
/// the phantoms away and replay from the start up to the target. What that must never
/// leave behind is a phantom the client still holds and the replay has forgotten.
///
/// The capture side had no bound at all. The 64 MB limit was only ever applied when
/// READING a recording back, so one left running in a busy place grew in memory for as
/// long as it was forgotten - and past that limit the file it finally wrote could never
/// be loaded, which is an hour of recording refused at the moment somebody tries to
/// watch it.
/// </summary>
public sealed class ReplayLifecycleTests
{
    private readonly ITestOutputHelper _out;
    public ReplayLifecycleTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, RecordingEngine Engine, Character Gm) Stage()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        gm.MaxHits = 100; gm.Hits = 100;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));

        return (world, new RecordingEngine(System.IO.Path.GetTempPath()), gm);
    }

    /// <summary>A recording of three mobiles moving, one per second, each with its own
    /// serial so the phantom mapping has something to do.</summary>
    private static RecordingSession ThreeMovers()
    {
        var session = new RecordingSession
        {
            RecorderName = "subject",
            Center = new Point3D(200, 200, 0, 0),
            RecorderUid = 0x00000001,
        };
        for (int i = 0; i < 3; i++)
        {
            session.Packets.Add(new RecordedPacket
            {
                TickOffset = i * 1000,
                Data = MobileMoving((uint)(0x00001000 + i)),
            });
        }
        return session;
    }

    private static byte[] MobileMoving(uint serial)
    {
        var p = new byte[17];
        p[0] = 0x77;
        p[1] = (byte)(serial >> 24); p[2] = (byte)(serial >> 16);
        p[3] = (byte)(serial >> 8); p[4] = (byte)serial;
        return p;
    }

    /// <summary>What the spectator's client has been told: which phantoms it currently
    /// holds, rebuilt from every packet sent to it.</summary>
    private sealed class Screen
    {
        private readonly HashSet<uint> _held = [];
        public readonly List<string> Log = [];

        public void Take(byte[] packet)
        {
            uint serial = (uint)((packet[1] << 24) | (packet[2] << 16) | (packet[3] << 8) | packet[4]);
            switch (packet[0])
            {
                case 0x77: _held.Add(serial); Log.Add($"draw {serial:X8}"); break;
                case 0x1D: _held.Remove(serial); Log.Add($"delete {serial:X8}"); break;
            }
        }

        public IReadOnlyCollection<uint> Held => _held;
    }

    // ---- seeking ---------------------------------------------------------

    [Fact]
    public void SeekingBackwardsTakesBackWhatTheClientWasShown()
    {
        var (_, engine, gm) = Stage();
        var session = ThreeMovers();
        var screen = new Screen();
        engine.StartReplay(gm, session);

        // Play the whole thing, then go back to the start.
        engine.SeekReplay(gm.Uid.Value, 5000, (_, data) => screen.Take(data));
        var afterPlaying = screen.Held.ToHashSet();
        Assert.Equal(3, afterPlaying.Count);

        engine.SeekReplay(gm.Uid.Value, 0, (_, data) => screen.Take(data));

        _out.WriteLine($"after playing: {afterPlaying.Count} phantoms; after seeking to 0: " +
                       $"{screen.Held.Count} ({string.Join(",", screen.Held.Select(h => h.ToString("X8")))})");

        // Only the first mover belongs at offset 0, and the two the client had been
        // shown are gone from its screen rather than left standing there.
        Assert.Single(screen.Held);
        var state = engine.GetReplayState(gm.Uid.Value)!;
        Assert.Equal(screen.Held.ToHashSet(), state.SerialMap.Values.ToHashSet());
    }

    [Fact]
    public void SeekingForwardsStillShowsWhatHappenedBeforeTheTarget()
    {
        var (_, engine, gm) = Stage();
        var screen = new Screen();
        engine.StartReplay(gm, ThreeMovers());

        // Jump straight to the end. The client has seen nothing yet, so everything up
        // to the target has to be replayed - otherwise the packets after it refer to
        // objects that were never drawn.
        engine.SeekReplay(gm.Uid.Value, 5000, (_, data) => screen.Take(data));

        _out.WriteLine($"seek to the end from cold: {screen.Held.Count} phantoms, " +
                       $"[{string.Join(",", screen.Log)}]");
        Assert.Equal(3, screen.Held.Count);
    }

    [Fact]
    public void TheReplayNeverForgetsAPhantomTheClientStillHolds()
    {
        // The invariant behind both seeks: what the engine thinks the client has and
        // what the client actually has are the same set, at every point.
        var (_, engine, gm) = Stage();
        var screen = new Screen();
        engine.StartReplay(gm, ThreeMovers());

        foreach (int target in new[] { 5000, 1500, 0, 3000, 500 })
        {
            engine.SeekReplay(gm.Uid.Value, target, (_, data) => screen.Take(data));
            var state = engine.GetReplayState(gm.Uid.Value)!;
            Assert.Equal(state.SerialMap.Values.ToHashSet(), screen.Held.ToHashSet());
        }

        _out.WriteLine($"after five seeks: {screen.Held.Count} phantoms held, " +
                       $"{screen.Log.Count} packets sent");
    }

    // ---- pausing and stopping --------------------------------------------

    [Fact]
    public void APausedReplaySendsNothingAndResumesFromWhereItStopped()
    {
        var (_, engine, gm) = Stage();
        var screen = new Screen();
        engine.StartReplay(gm, ThreeMovers());
        engine.SeekReplay(gm.Uid.Value, 1500, (_, data) => screen.Take(data));

        engine.PauseReplay(gm.Uid.Value);
        int atPause = engine.GetElapsedMs(gm.Uid.Value);
        int packetsAtPause = screen.Log.Count;

        for (int i = 0; i < 5; i++)
            engine.TickReplays((_, data) => screen.Take(data), null);

        Assert.Equal(packetsAtPause, screen.Log.Count);
        Assert.Equal(atPause, engine.GetElapsedMs(gm.Uid.Value));

        engine.ResumeReplay(gm.Uid.Value);
        _out.WriteLine($"paused at {atPause}ms, resumed at {engine.GetElapsedMs(gm.Uid.Value)}ms");

        // Resuming picks the clock up where it was left, rather than restarting or
        // jumping to wherever the wall clock has got to.
        Assert.InRange(engine.GetElapsedMs(gm.Uid.Value), atPause - 50, atPause + 50);
    }

    [Fact]
    public void AStoppedReplaySendsNothingMore()
    {
        var (_, engine, gm) = Stage();
        var screen = new Screen();
        engine.StartReplay(gm, ThreeMovers());
        engine.SeekReplay(gm.Uid.Value, 0, (_, data) => screen.Take(data));
        int packets = screen.Log.Count;

        engine.StopReplay(gm.Uid.Value);
        for (int i = 0; i < 5; i++)
            engine.TickReplays((_, data) => screen.Take(data), null);

        Assert.Equal(packets, screen.Log.Count);
        Assert.False(engine.HasActiveReplays);

        // What the caller must do with the phantoms is its own step, and it needs the
        // list BEFORE the stop - which is why the engine gives it up and then forgets.
        Assert.Empty(engine.GetPhantomSerials(gm.Uid.Value));
    }

    // ---- what a recording may cost ---------------------------------------

    [Fact]
    public void ARecordingStopsCapturingAtItsBudgetInsteadOfGrowingForever()
    {
        var (world, engine, gm) = Stage();
        var recorder = world.CreateCharacter();
        recorder.Name = "subject";
        world.PlaceCharacter(recorder, new Point3D(200, 200, 0, 0));
        engine.StartRecording(recorder);

        // Flood it with far more than the budget allows.
        var big = new byte[4096];
        big[0] = 0x77;
        long attempted = 0;
        for (int i = 0; i < 20_000; i++)
        {
            engine.CapturePacket(recorder.Uid.Value, recorder.Position, big);
            attempted += big.Length;
        }

        var session = engine.StopRecording(recorder.Uid.Value)!;
        _out.WriteLine($"offered {attempted / (1024 * 1024)} MB: captured " +
                       $"{session.CapturedBytes / (1024 * 1024)} MB in {session.Packets.Count} packets, " +
                       $"truncated={session.Truncated}");

        // Bounded, and below the limit that READING applies - so anything recorded can
        // still be played back.
        Assert.True(session.Truncated);
        Assert.True(session.CapturedBytes <= RecordingEngine.MaxCapturedBytes);
        Assert.True(session.CapturedBytes < RecordingEngine.MaxRecordingFileBytes);
        Assert.True(session.Packets.Count > 0, "the recording kept what it had");
    }

    [Fact]
    public void AnOrdinaryRecordingIsNotTruncatedAndCountsWhatItHolds()
    {
        // The control: the budget must not have stopped ordinary recording. Every
        // assertion above would pass if capture had stopped working altogether.
        var (world, engine, _) = Stage();
        var recorder = world.CreateCharacter();
        recorder.Name = "subject";
        world.PlaceCharacter(recorder, new Point3D(200, 200, 0, 0));
        engine.StartRecording(recorder);

        for (int i = 0; i < 100; i++)
            engine.CapturePacket(recorder.Uid.Value, recorder.Position, MobileMoving(0x00001234));

        var session = engine.StopRecording(recorder.Uid.Value)!;
        _out.WriteLine($"100 packets: {session.CapturedBytes} bytes, truncated={session.Truncated}");

        Assert.False(session.Truncated);
        Assert.Equal(100, session.Packets.Count);
        Assert.Equal(100 * (17 + 6), session.CapturedBytes);
    }

    [Fact]
    public void APacketFromOutsideTheCaptureRangeCostsNothing()
    {
        var (world, engine, _) = Stage();
        var recorder = world.CreateCharacter();
        recorder.Name = "subject";
        world.PlaceCharacter(recorder, new Point3D(200, 200, 0, 0));
        engine.StartRecording(recorder, captureRange: 18);

        engine.CapturePacket(recorder.Uid.Value, new Point3D(400, 400, 0, 0), MobileMoving(0x1234));

        var session = engine.StopRecording(recorder.Uid.Value)!;
        Assert.Empty(session.Packets);
        Assert.Equal(0, session.CapturedBytes);
    }
}
