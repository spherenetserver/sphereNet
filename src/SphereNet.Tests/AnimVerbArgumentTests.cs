using System.Linq;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Network.Packets;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What the ANIM verb's arguments mean.
///
/// Upstream takes exactly three (CHV_ANIM, CChar.cpp:4459-4469):
///
///     ANIM &lt;action&gt;, &lt;frame delay = 0&gt;, &lt;frame count = 7&gt;
///
/// and hardcodes backwards to false and the repeat count to 1. This engine read the
/// second argument as the frame COUNT and the third as a repeat count, and invented a
/// fourth and fifth - so a script writing `ANIM 11,1,7`, meaning "action 11, played
/// slowly, over seven frames", got a ONE-frame animation repeated seven times.
///
/// The wire order is upstream's: serial, action, frame count, repeat count, backwards,
/// repeat flag, delay (PacketAction, send.cpp:1836-1847). The repeat FLAG is derived
/// from the count there, which is why it cannot disagree with it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AnimVerbArgumentTests
{
    private readonly ITestOutputHelper _out;
    public AnimVerbArgumentTests(ITestOutputHelper output) => _out = output;

    private sealed record Anim(ushort Action, ushort FrameCount, ushort RepeatCount,
        bool Backwards, bool RepeatFlag, byte Delay);

    private static Anim? Run(string args)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        PacketWriter? captured = null;
        var saved = Character.BroadcastNearby;
        Character.BroadcastNearby = (_, _, packet, _) => captured ??= packet;
        try
        {
            Assert.True(ch.TryExecuteCommand("ANIM", args, null!));
        }
        finally
        {
            Character.BroadcastNearby = saved;
        }

        if (captured == null) return null;
        var span = captured.Build().Span;
        Assert.Equal(0x6E, span[0]);
        return new Anim(
            (ushort)((span[5] << 8) | span[6]),
            (ushort)((span[7] << 8) | span[8]),
            (ushort)((span[9] << 8) | span[10]),
            span[11] != 0, span[12] != 0, span[13]);
    }

    [Fact]
    public void TheSecondArgumentIsTheDelayAndTheThirdIsTheFrameCount()
    {
        var a = Run("11,1,7");
        Assert.NotNull(a);
        _out.WriteLine($"ANIM 11,1,7 -> action {a!.Action}, frames {a.FrameCount}, " +
                       $"repeats {a.RepeatCount}, delay {a.Delay}");
        Assert.Equal(11, a.Action);
        Assert.Equal(7, a.FrameCount);    // NOT 1
        Assert.Equal(1, a.RepeatCount);   // NOT 7
        Assert.Equal(1, a.Delay);
    }

    [Fact]
    public void TheDefaultsAreUpstreamsDefaults()
    {
        var a = Run("11");
        Assert.NotNull(a);
        Assert.Equal(7, a!.FrameCount);
        Assert.Equal(1, a.RepeatCount);
        Assert.Equal(0, a.Delay);
        Assert.False(a.Backwards);
    }

    [Fact]
    public void ARepeatCountOfOneNeverSetsTheRepeatFlag()
    {
        // The flag is derived from the count upstream (send.cpp:1844), so the two
        // cannot disagree - a single playthrough never says "loop me".
        var a = Run("11,0,7");
        Assert.NotNull(a);
        Assert.Equal(1, a!.RepeatCount);
        Assert.False(a.RepeatFlag);
    }

    [Fact]
    public void ANonNumericFirstArgumentDoesNothing()
    {
        Assert.Null(Run("not-an-action"));
    }
}
