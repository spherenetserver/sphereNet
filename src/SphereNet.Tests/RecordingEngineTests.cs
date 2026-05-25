using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Recording;
using SphereNet.Game.World;

namespace SphereNet.Tests;

public class RecordingEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecordingEngine _engine;
    private readonly GameWorld _world;
    private readonly Character _recorder;

    public RecordingEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"spherenet_test_{Guid.NewGuid():N}");
        _engine = new RecordingEngine(_tempDir);
        var loggerFactory = LoggerFactory.Create(_ => { });
        _world = new GameWorld(loggerFactory);
        _world.InitMap(0, 6144, 4096);
        _recorder = _world.CreateCharacter();
        _recorder.Name = "TestRecorder";
        _world.PlaceCharacter(_recorder, new Point3D(1000, 1000, 0, 0));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort cleanup */ }
    }

    // --- Recording lifecycle ---

    [Fact]
    public void StartRecording_SetsActiveFlag()
    {
        _engine.StartRecording(_recorder);
        Assert.True(_engine.IsRecording(_recorder.Uid.Value));
    }

    [Fact]
    public void StartRecording_DuplicateIgnored()
    {
        _engine.StartRecording(_recorder);
        _engine.StartRecording(_recorder); // should not throw
        Assert.True(_engine.IsRecording(_recorder.Uid.Value));
    }

    [Fact]
    public void StopRecording_ReturnsSession()
    {
        _engine.StartRecording(_recorder);
        var session = _engine.StopRecording(_recorder.Uid.Value);

        Assert.NotNull(session);
        Assert.False(_engine.IsRecording(_recorder.Uid.Value));
        Assert.Equal("TestRecorder", session.RecorderName);
    }

    [Fact]
    public void StopRecording_SavesFile()
    {
        _engine.StartRecording(_recorder);
        var session = _engine.StopRecording(_recorder.Uid.Value)!;

        string expectedPath = Path.Combine(_tempDir, session.Id + ".rec");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void StopRecording_LoadRecording_Roundtrip()
    {
        _engine.StartRecording(_recorder);
        _engine.CapturePacket(_recorder.Uid.Value, _recorder.Position, [0x77, 0x01, 0x02, 0x03]);
        var session = _engine.StopRecording(_recorder.Uid.Value)!;

        var loaded = _engine.LoadRecordingById(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(session.RecorderName, loaded.RecorderName);
        Assert.Equal(session.Packets.Count, loaded.Packets.Count);
        Assert.Equal(session.Packets[0].Data, loaded.Packets[0].Data);
    }

    // --- Capture ---

    [Fact]
    public void CapturePacket_InRange_AddsToSession()
    {
        _engine.StartRecording(_recorder, captureRange: 18);

        var nearbyPos = new Point3D(1005, 1000, 0, 0); // 5 tiles away
        _engine.CapturePacket(_recorder.Uid.Value, nearbyPos, [0x77, 0x01, 0x02]);

        var session = _engine.StopRecording(_recorder.Uid.Value)!;
        Assert.True(session.Packets.Count >= 1);
        Assert.Contains(session.Packets, p => p.Data.Length == 3 && p.Data[0] == 0x77);
    }

    [Fact]
    public void CapturePacket_OutOfRange_Ignored()
    {
        _engine.StartRecording(_recorder, captureRange: 18);

        var farPos = new Point3D(1100, 1000, 0, 0); // 100 tiles away
        _engine.CapturePacket(_recorder.Uid.Value, farPos, [0x77, 0x01, 0x02]);

        var session = _engine.StopRecording(_recorder.Uid.Value)!;
        Assert.DoesNotContain(session.Packets, p => p.Data.Length == 3 && p.Data[0] == 0x77);
    }

    [Fact]
    public void CaptureFromBroadcast_MultipleRecorders_EachGetsPacket()
    {
        var recorder2 = _world.CreateCharacter();
        recorder2.Name = "Recorder2";
        _world.PlaceCharacter(recorder2, new Point3D(1002, 1000, 0, 0));

        _engine.StartRecording(_recorder, captureRange: 18);
        _engine.StartRecording(recorder2, captureRange: 18);

        var broadcastCenter = new Point3D(1001, 1000, 0, 0);
        _engine.CaptureFromBroadcast(broadcastCenter, 18, [0x77, 0xAA]);

        var session1 = _engine.StopRecording(_recorder.Uid.Value)!;
        var session2 = _engine.StopRecording(recorder2.Uid.Value)!;

        Assert.Contains(session1.Packets, p => p.Data.Length == 2 && p.Data[1] == 0xAA);
        Assert.Contains(session2.Packets, p => p.Data.Length == 2 && p.Data[1] == 0xAA);
    }

    // --- Replay ---

    [Fact]
    public void StartReplay_SetsReplayState()
    {
        var session = BuildTestSession(5);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        var state = _engine.StartReplay(viewer, session);
        Assert.NotNull(state);
        Assert.True(_engine.IsReplaying(viewer.Uid.Value));
    }

    [Fact]
    public void StartReplay_Duplicate_ReturnsNull()
    {
        var session = BuildTestSession(5);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        _engine.StartReplay(viewer, session);
        var second = _engine.StartReplay(viewer, session);
        Assert.Null(second);
    }

    [Fact]
    public void StopReplay_ClearsState()
    {
        var session = BuildTestSession(5);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        _engine.StartReplay(viewer, session);
        _engine.StopReplay(viewer.Uid.Value);
        Assert.False(_engine.IsReplaying(viewer.Uid.Value));
    }

    // --- Seek ---

    [Fact]
    public void SeekReplay_Forward_SendsPacketsUpToTarget()
    {
        var session = BuildTestSession(10);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        _engine.StartReplay(viewer, session);

        var sentPackets = new List<byte[]>();
        _engine.SeekReplay(viewer.Uid.Value, 5000,
            (uid, data) => sentPackets.Add(data));

        // Should have sent packets with TickOffset <= 5000
        Assert.True(sentPackets.Count > 0);
    }

    [Fact]
    public void SeekReplay_ClearsPhantoms()
    {
        var session = BuildTestSession(5);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        var state = _engine.StartReplay(viewer, session)!;

        // Add a fake phantom mapping
        state.SerialMap[0x00000001] = 0x3FFF0001;

        var sentPackets = new List<byte[]>();
        _engine.SeekReplay(viewer.Uid.Value, 0,
            (uid, data) => sentPackets.Add(data));

        // 0x1D (DeleteObject) should be sent for the old phantom before rebuild
        Assert.Contains(sentPackets, p => p.Length == 5 && p[0] == 0x1D);
        // Verify the delete was for our phantom serial (0x3FFF0001)
        var delPacket = sentPackets.First(p => p.Length == 5 && p[0] == 0x1D);
        uint deletedSerial = (uint)(delPacket[1] << 24 | delPacket[2] << 16 | delPacket[3] << 8 | delPacket[4]);
        Assert.Equal(0x3FFF0001u, deletedSerial);
    }

    // --- Pause/Resume ---

    [Fact]
    public void PauseResume_MaintainsOffset()
    {
        var session = BuildTestSession(10);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        var state = _engine.StartReplay(viewer, session)!;
        Assert.False(state.IsPaused);

        _engine.PauseReplay(viewer.Uid.Value);
        Assert.True(state.IsPaused);

        int pausedMs = _engine.GetElapsedMs(viewer.Uid.Value);
        _engine.ResumeReplay(viewer.Uid.Value);
        Assert.False(state.IsPaused);

        int resumedMs = _engine.GetElapsedMs(viewer.Uid.Value);
        Assert.True(Math.Abs(resumedMs - pausedMs) < 100);
    }

    // --- Playback Speed ---

    [Fact]
    public void SetPlaybackSpeed_ChangesSpeed()
    {
        var session = BuildTestSession(10);
        var viewer = _world.CreateCharacter();
        _world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));

        var state = _engine.StartReplay(viewer, session)!;
        Assert.Equal(1.0f, state.PlaybackSpeed);

        _engine.SetPlaybackSpeed(viewer.Uid.Value, 2.0f);
        Assert.Equal(2.0f, state.PlaybackSpeed);
    }

    // --- Delete ---

    [Fact]
    public void DeleteRecording_RemovesFile()
    {
        _engine.StartRecording(_recorder);
        _engine.CapturePacket(_recorder.Uid.Value, _recorder.Position, [0x01]);
        var session = _engine.StopRecording(_recorder.Uid.Value)!;

        string path = Path.Combine(_tempDir, session.Id + ".rec");
        Assert.True(File.Exists(path));

        bool deleted = _engine.DeleteRecording(0);
        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    // --- Helpers ---

    private static RecordingSession BuildTestSession(int packetCount)
    {
        var session = new RecordingSession
        {
            Id = "test_session",
            RecorderName = "TestRecorder",
            RecorderUid = 0x00000001,
            Center = new Point3D(1000, 1000, 0, 0),
            CaptureRange = 18
        };
        for (int i = 0; i < packetCount; i++)
        {
            session.Packets.Add(new RecordedPacket
            {
                TickOffset = i * 1000,
                Data = [0x77, 0x00, 0x00, 0x00, 0x01, 0x01, 0x90,
                    (byte)(1000 >> 8), (byte)(1000 & 0xFF),
                    (byte)(1000 >> 8), (byte)((1000 + i) & 0xFF),
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x01]
            });
        }
        return session;
    }
}
