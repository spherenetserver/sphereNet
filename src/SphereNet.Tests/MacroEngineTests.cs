using SphereNet.Game.Macro;
using SphereNet.Server.Macro;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Macro record/playback engine. Playback is driven by an explicit nowMs and an
/// injected getClient delegate, so the control-flow paths (cap enforcement, step
/// typing, mutual exclusivity of record/play, stop-on-missing-client) are fully
/// deterministic without a live game client.
/// </summary>
public class MacroEngineTests
{
    private static MacroEngine NewEngine(int maxSteps = 50, int maxLoopMinutes = 5) =>
        new(maxSteps, maxLoopMinutes);

    [Fact]
    public void Recording_CapturesStepsAndPromotesToLastRecorded()
    {
        var engine = NewEngine();
        uint uid = 1;

        Assert.False(engine.IsRecording(uid));
        engine.StartRecording(uid);
        Assert.True(engine.IsRecording(uid));

        engine.CaptureUseObject(uid, dispId: 0x0F0E);
        engine.CaptureUseSkill(uid, skillId: 7);

        var session = engine.StopRecording(uid);
        Assert.NotNull(session);
        Assert.Equal(2, session!.Steps.Count);
        Assert.False(engine.IsRecording(uid));
        Assert.True(engine.HasRecording(uid));
        Assert.Same(session, engine.GetRecording(uid));

        // The very first captured step always has zero lead-in delay.
        Assert.Equal(0, session.Steps[0].DelayMs);
        Assert.Equal(MacroStepType.UseObject, session.Steps[0].Type);
        Assert.Equal(MacroStepType.UseSkill, session.Steps[1].Type);
    }

    [Fact]
    public void StopRecording_WithNoSteps_ReturnsNullAndClears()
    {
        var engine = NewEngine();
        uint uid = 2;
        engine.StartRecording(uid);
        Assert.Null(engine.StopRecording(uid)); // empty session is discarded
        Assert.False(engine.IsRecording(uid));
        Assert.False(engine.HasRecording(uid));
    }

    [Fact]
    public void Capture_RespectsMaxStepCap()
    {
        var engine = NewEngine(maxSteps: 2);
        uint uid = 3;
        engine.StartRecording(uid);
        engine.CaptureUseSkill(uid, 1);
        engine.CaptureUseSkill(uid, 2);
        engine.CaptureUseSkill(uid, 3); // dropped — over the cap

        var session = engine.StopRecording(uid);
        Assert.NotNull(session);
        Assert.Equal(2, session!.Steps.Count);
    }

    [Fact]
    public void CaptureTarget_ClassifiesSelfObjectAndLocation()
    {
        var engine = NewEngine();
        uint uid = 4;
        engine.StartRecording(uid);

        // serial == selfUid -> TargetSelf
        engine.CaptureTarget(uid, serial: uid, x: 0, y: 0, z: 0, graphic: 0, selfUid: uid);
        // a different serial -> TargetObject
        engine.CaptureTarget(uid, serial: 99, x: 10, y: 20, z: 1, graphic: 0, selfUid: uid);
        // serial == 0 -> TargetLocation
        engine.CaptureTarget(uid, serial: 0, x: 30, y: 40, z: 2, graphic: 0x1234, selfUid: uid);

        var session = engine.StopRecording(uid)!;
        Assert.Equal(MacroStepType.TargetSelf, session.Steps[0].Type);
        Assert.Equal(MacroStepType.TargetObject, session.Steps[1].Type);
        Assert.Equal((uint)99, session.Steps[1].Serial);
        Assert.Equal(MacroStepType.TargetLocation, session.Steps[2].Type);
        Assert.Equal((ushort)0x1234, session.Steps[2].Graphic);
    }

    [Fact]
    public void RecordAndPlayback_AreMutuallyExclusive()
    {
        var engine = NewEngine();
        uint uid = 5;

        // No recording yet -> playback cannot start.
        Assert.False(engine.StartPlayback(uid, loop: false));

        engine.StartRecording(uid);
        engine.CaptureUseSkill(uid, 1);
        engine.StopRecording(uid);

        Assert.True(engine.StartPlayback(uid, loop: false));
        Assert.True(engine.IsPlaying(uid));

        // Starting a recording stops the active playback.
        engine.StartRecording(uid);
        Assert.False(engine.IsPlaying(uid));
        Assert.True(engine.IsRecording(uid));
    }

    [Fact]
    public void Tick_StopsPlaybackWhenClientMissing()
    {
        var engine = NewEngine();
        uint uid = 6;
        engine.StartRecording(uid);
        engine.CaptureUseSkill(uid, 1);
        engine.StopRecording(uid);
        Assert.True(engine.StartPlayback(uid, loop: false));

        var messages = new List<(uint Uid, string Text)>();
        // getClient returns null -> the player is gone; playback must stop cleanly.
        engine.Tick(
            nowMs: 1_000_000,
            getClient: _ => null,
            findItemInBackpack: (_, _) => null,
            sendMessage: (u, t) => messages.Add((u, t)));

        Assert.False(engine.IsPlaying(uid));
        Assert.Contains(messages, m => m.Uid == uid && m.Text == "Macro stopped.");
    }

    [Fact]
    public void OnCharDisconnect_ClearsRecordingAndPlayback()
    {
        var engine = NewEngine();
        uint uid = 7;
        engine.StartRecording(uid);
        engine.CaptureUseSkill(uid, 1);
        Assert.True(engine.IsRecording(uid));

        engine.OnCharDisconnect(uid);
        Assert.False(engine.IsRecording(uid));
        Assert.False(engine.IsPlaying(uid));
    }
}
