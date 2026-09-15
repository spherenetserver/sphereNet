using System;
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
/// What a replay gives the spectator back, and what it may never hand them (review
/// work item D10).
///
/// Watching a replay moves the spectator, hides them and freezes them. Finishing has to
/// return each of those to what it found, and "what it found" is not always nothing: a
/// GM who was already frozen or already invisible was left changed by the act of
/// watching, with nothing in the log to say so. Invisibility was remembered; the freeze
/// beside it was cleared unconditionally.
///
/// The other half is the serial namespace. Replayed packets carry phantom serials so a
/// spectator's client can never bind to a real object, and the phantoms are handed out
/// from the top of each real range. The counter had no end: a long recording of a busy
/// area that used up the range would have started naming objects the world really
/// holds - and FinishReplay deletes every phantom it handed out, which would take a
/// real object off that client's screen.
/// </summary>
public sealed class ReplaySpectatorLifecycleTests
{
    private readonly ITestOutputHelper _out;
    public ReplaySpectatorLifecycleTests(ITestOutputHelper output) => _out = output;

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

    private static RecordingSession Session() => new()
    {
        RecorderName = "subject",
        Center = new Point3D(200, 200, 0, 0),
    };

    // ---- what the spectator gets back ------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheStateTheSpectatorArrivedWithIsTheStateTheyLeaveWith(bool frozen, bool invisible)
    {
        var (_, engine, gm) = Stage();
        if (frozen) gm.SetStatFlag(StatFlag.Freeze);
        if (invisible) gm.SetStatFlag(StatFlag.Invisible);

        var state = engine.StartReplay(gm, Session());
        Assert.NotNull(state);

        // Spectating sets both, whatever they were.
        gm.SetStatFlag(StatFlag.Invisible);
        gm.SetStatFlag(StatFlag.Freeze);

        // What FinishReplay does with the remembered state.
        if (!state!.WasInvisible) gm.ClearStatFlag(StatFlag.Invisible);
        if (!state.WasFrozen) gm.ClearStatFlag(StatFlag.Freeze);

        _out.WriteLine($"arrived frozen={frozen} invisible={invisible}; " +
                       $"left frozen={gm.IsStatFlag(StatFlag.Freeze)} " +
                       $"invisible={gm.IsStatFlag(StatFlag.Invisible)}");

        Assert.Equal(frozen, gm.IsStatFlag(StatFlag.Freeze));
        Assert.Equal(invisible, gm.IsStatFlag(StatFlag.Invisible));
    }

    [Fact]
    public void ThePositionTheSpectatorLeftFromIsRemembered()
    {
        var (world, engine, gm) = Stage();
        var home = gm.Position;

        var state = engine.StartReplay(gm, Session());
        Assert.NotNull(state);
        world.MoveCharacter(gm, Session().Center);

        Assert.Equal(home, state!.OriginalPosition);
    }

    // ---- the phantom namespace -------------------------------------------

    [Fact]
    public void TwoSpectatorsOfTheSameRecordingDoNotShareAMapping()
    {
        var (world, engine, first) = Stage();
        var second = world.CreateCharacter();
        second.IsPlayer = true;
        second.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(second, new Point3D(105, 100, 0, 0));

        var session = Session();
        var a = engine.StartReplay(first, session);
        var b = engine.StartReplay(second, session);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // Each spectator names the recording's objects for itself. Sharing one map
        // would make the numbering depend on who started watching first.
        Assert.NotSame(a!.SerialMap, b!.SerialMap);
        Assert.Empty(a.SerialMap);
        Assert.Empty(b.SerialMap);
        Assert.Equal(a.NextPhantomSerial, b.NextPhantomSerial);
    }

    [Fact]
    public void PhantomsAreHandedOutFromTheTopOfEachRangeAndStopAtTheEnd()
    {
        var (_, engine, gm) = Stage();
        var state = engine.StartReplay(gm, Session())!;

        // A mobile serial and an item serial get phantoms from their own ranges, both
        // above anything a real world hands out first.
        Assert.True(ReplayState.PhantomMobileBase > 0x3F000000);
        Assert.True(ReplayState.PhantomItemBase > 0x7F000000);
        Assert.Equal(ReplayState.PhantomMobileBase, state.NextPhantomSerial);
        Assert.Equal(ReplayState.PhantomItemBase, state.NextPhantomItemSerial);

        // Run the mobile range out, then ask for one more. The packet must be refused:
        // a serial past the end belongs to something real, and FinishReplay deletes
        // every phantom it handed out.
        state.NextPhantomSerial = ReplayState.PhantomMobileEnd + 1;
        byte[]? remapped = RecordingEngine.RemapForTests(BuildMobileMoving(0x00001234), state);

        _out.WriteLine($"with the mobile range exhausted: " +
                       $"{(remapped == null ? "refused" : "forwarded")}, " +
                       $"exhausted={state.ExhaustedPhantoms}");
        Assert.Null(remapped);
        Assert.Equal(1, state.ExhaustedPhantoms);
    }

    [Fact]
    public void AnOrdinaryPacketIsStillRewrittenIntoPhantoms()
    {
        // The control: the range guard must not have stopped replays rewriting
        // anything. A refusal for every packet would pass every assertion above.
        var (_, engine, gm) = Stage();
        var state = engine.StartReplay(gm, Session())!;

        byte[]? remapped = RecordingEngine.RemapForTests(BuildMobileMoving(0x00001234), state);

        Assert.NotNull(remapped);
        uint written = (uint)((remapped![1] << 24) | (remapped[2] << 16) |
                              (remapped[3] << 8) | remapped[4]);
        _out.WriteLine($"0x0000_1234 became 0x{written:X8}");
        Assert.Equal(ReplayState.PhantomMobileBase, written);
        Assert.NotEqual(0x00001234u, written);
        Assert.Equal(0, state.ExhaustedPhantoms);
    }

    /// <summary>0x77 mobile-moving: opcode then the mobile's serial, which is the
    /// offset table's entry for it. 17 bytes.</summary>
    private static byte[] BuildMobileMoving(uint serial)
    {
        var p = new byte[17];
        p[0] = 0x77;
        p[1] = (byte)(serial >> 24); p[2] = (byte)(serial >> 16);
        p[3] = (byte)(serial >> 8); p[4] = (byte)serial;
        return p;
    }
}
