using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The mobile flags byte, per viewer.
///
/// Source-X builds it in CChar::GetModeFlag (CCharStatus.cpp:659-702) and the VIEWER
/// matters: bit 0x04 is POISONED for a pre-Stygian-Abyss client and FLYING for a newer
/// one - one bit, two meanings, decided by who is looking. ClassicUO reads it exactly
/// that way (Mobile.cs:126 and :145).
///
/// This engine set five bits from a fixed rule and ignored the viewer entirely, so an
/// invulnerable creature had no yellow health bar, a poisoned one was never green, a
/// petrified one was not frozen, staff did not walk through mobiles, an elf or gargoyle
/// woman was drawn as a man, and a sleeping or insubstantial character was not greyed.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MobileFlagsTests
{
    private readonly ITestOutputHelper _out;
    public MobileFlagsTests(ITestOutputHelper output) => _out = output;

    private static Character Subject()
    {
        var ch = new Character { BodyId = 0x0190 };
        return ch;
    }

    private static byte Flags(Character ch, bool modernViewer = true)
    {
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, modernViewer ? 17001 : 17002);
        state.ClientVersionNumber = modernViewer ? 70_030_000u : 40_000_000u;
        return GameClient.BuildMobileFlagsFor(ch, state);
    }

    [Fact]
    public void PetrifiedCountsAsFrozen()
    {
        // STATF_FREEZE|STATF_STONE share the bit upstream.
        var ch = Subject();
        ch.SetStatFlag(StatFlag.Stone);
        Assert.Equal(0x01, Flags(ch) & 0x01);
    }

    [Theory]
    [InlineData((ushort)0x0191)]   // human
    [InlineData((ushort)0x0193)]   // ...and her ghost
    [InlineData((ushort)0x025E)]   // elf
    [InlineData((ushort)0x029B)]   // gargoyle
    public void EveryFemaleBodyIsMarkedFemale(ushort body)
    {
        var ch = Subject();
        ch.BodyId = body;
        _out.WriteLine($"body {body:X4} -> flags 0x{Flags(ch):X2}");
        Assert.Equal(0x02, Flags(ch) & 0x02);
    }

    [Fact]
    public void AMaleBodyIsNotMarkedFemale()
    {
        Assert.Equal(0, Flags(Subject()) & 0x02);
    }

    [Fact]
    public void PoisonReachesAnOlderClientAndFlightReachesANewerOne()
    {
        // The same bit, both ways round.
        var poisoned = Subject();
        poisoned.SetStatFlag(StatFlag.Poisoned);
        var flying = Subject();
        flying.SetStatFlag(StatFlag.Hovering);

        _out.WriteLine($"poisoned: old {Flags(poisoned, false):X2} new {Flags(poisoned):X2}");
        _out.WriteLine($"flying:   old {Flags(flying, false):X2} new {Flags(flying):X2}");

        Assert.Equal(0x04, Flags(poisoned, modernViewer: false) & 0x04);
        Assert.Equal(0, Flags(poisoned, modernViewer: true) & 0x04);
        Assert.Equal(0x04, Flags(flying, modernViewer: true) & 0x04);
        Assert.Equal(0, Flags(flying, modernViewer: false) & 0x04);
    }

    [Fact]
    public void InvulnerabilityIsTheYellowBar()
    {
        var ch = Subject();
        ch.SetStatFlag(StatFlag.Invul);
        Assert.Equal(0x08, Flags(ch) & 0x08);
    }

    [Fact]
    public void StaffWalkThroughMobiles()
    {
        var plain = Subject();
        Assert.Equal(0, Flags(plain) & 0x10);

        var gm = Subject();
        gm.PrivLevel = PrivLevel.GM;
        Assert.Equal(0x10, Flags(gm) & 0x10);
    }

    [Theory]
    [InlineData(StatFlag.Sleeping)]
    [InlineData(StatFlag.Insubstantial)]
    [InlineData(StatFlag.Hidden)]
    [InlineData(StatFlag.Invisible)]
    public void EveryGreyedStateSetsTheOverlayBit(StatFlag flag)
    {
        var ch = Subject();
        ch.SetStatFlag(flag);
        _out.WriteLine($"{flag} -> flags 0x{Flags(ch):X2}");
        Assert.Equal(0x80, Flags(ch) & 0x80);
    }

    [Fact]
    public void AShardThatColoursAStateKeepsTheGreyOff()
    {
        // COLORHIDDEN and friends: a shard giving hidden its own hue does not want the
        // client's grey on top of it (CCharStatus.cpp:687-700).
        var ch = Subject();
        ch.SetStatFlag(StatFlag.Hidden);
        try
        {
            GameClient.ColorHiddenHue = 0x0481;
            Assert.Equal(0, Flags(ch) & 0x80);
        }
        finally
        {
            GameClient.ColorHiddenHue = 0;
        }
        Assert.Equal(0x80, Flags(ch) & 0x80);
    }

    [Fact]
    public void WarModeIsStillWarMode()
    {
        var ch = Subject();
        ch.SetStatFlag(StatFlag.War);
        Assert.Equal(0x40, Flags(ch) & 0x40);
    }
}
