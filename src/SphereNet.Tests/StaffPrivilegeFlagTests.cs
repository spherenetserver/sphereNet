using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The staff privilege flags a pack expects to find: DEBUG, DETAIL and HEARALL.
///
/// The shipped login script clears all three on every player, which it cannot do
/// while nothing answers the names - and each carries a real upstream behaviour,
/// so they are flags with effects rather than stored booleans.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StaffPrivilegeFlagTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x, short y, PrivLevel priv = PrivLevel.Player)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = true;
        ch.PrivLevel = priv;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("DETAIL")]
    [InlineData("HEARALL")]
    public void EachFlagReadsBackWhatWasWritten(string key)
    {
        var ch = Player(World(), 100, 100);

        Assert.True(ch.TryGetProperty(key, out string off));
        Assert.Equal("0", off);
        Assert.True(ch.TrySetProperty(key, "1"));
        Assert.True(ch.TryGetProperty(key, out string on));
        Assert.Equal("1", on);
        Assert.True(ch.TrySetProperty(key, "0"));
        Assert.True(ch.TryGetProperty(key, out string cleared));
        Assert.Equal("0", cleared);
    }

    [Fact]
    public void DebugShowsAMountThatIsHiddenFromEverybodyElse()
    {
        // A mount is drawn as part of its rider, so it is filtered from every view;
        // upstream lets a viewer holding DEBUG see it (CClient.cpp:421).
        var world = World();
        var lf = LoggerFactory.Create(_ => { });
        var watcher = Player(world, 100, 100, PrivLevel.GM);
        var state = TestHarness.CreateActiveNetState(lf, 5101);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, watcher);

        var mount = Player(world, 101, 100);
        mount.SetStatFlag(StatFlag.Ridden);

        Assert.DoesNotContain(client.BuildViewDelta()!.NewChars, e => e.Character == mount);

        watcher.DebugView = true;
        Assert.Contains(client.BuildViewDelta()!.NewChars, e => e.Character == mount);
    }

    [Fact]
    public void HearAllCarriesSpeechFromOutsideEarshot()
    {
        var world = World();
        var lf = LoggerFactory.Create(_ => { });

        var speaker = Player(world, 100, 100);
        var speakerState = TestHarness.CreateActiveNetState(lf, 5102);
        var speakerClient = new GameClient(speakerState, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(speakerClient, speaker);

        // Far outside any say range.
        var listener = Player(world, 400, 400, PrivLevel.GM);
        var listenerState = TestHarness.CreateActiveNetState(lf, 5103);
        var listenerClient = new GameClient(listenerState, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(listenerClient, listener);

        speakerClient.ForEachPlayingClient = action => action(listener, listenerClient);

        speakerClient.HandleSpeech(0, 0x3B2, 3, "over here");
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(listenerState), p => p.Span[0] == 0xAE);

        listener.HearAll = true;
        speakerClient.HandleSpeech(0, 0x3B2, 3, "over here");
        Assert.Contains(TestHarness.GetQueuedPackets(listenerState), p => p.Span[0] == 0xAE);
    }

    [Fact]
    public void HearAllDoesNotReachDownFromBelowThePrivilegeLine()
    {
        // Upstream only carries a speaker at or below the listener's own privilege
        // (CClient.cpp:440): it is an ear on the shard, not on the staff.
        var world = World();
        var lf = LoggerFactory.Create(_ => { });

        var speaker = Player(world, 100, 100, PrivLevel.Owner);
        var speakerState = TestHarness.CreateActiveNetState(lf, 5104);
        var speakerClient = new GameClient(speakerState, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(speakerClient, speaker);

        var listener = Player(world, 400, 400);
        listener.HearAll = true;
        var listenerState = TestHarness.CreateActiveNetState(lf, 5105);
        var listenerClient = new GameClient(listenerState, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(listenerClient, listener);

        speakerClient.ForEachPlayingClient = action => action(listener, listenerClient);
        speakerClient.HandleSpeech(0, 0x3B2, 3, "staff business");

        Assert.DoesNotContain(TestHarness.GetQueuedPackets(listenerState), p => p.Span[0] == 0xAE);
    }
}
