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
/// What the client has to agree with the server about, the moment a player dies.
///
/// Two things go out on death that reset client state, and both were half-applied.
///
/// The 0x20 player redraw resets the CLIENT's walk sequence to zero. Upstream resets
/// its own at the one place it sends that packet, and says why in as many words:
/// "This will reset client-side walk sequence to 0, so reset it on server side too, to
/// prevent client request an unnecessary 'resync'" (CClient::addPlayerUpdate,
/// CClientMsg.cpp:2162). Every 0x20 here did it except the two that matter most -
/// death and resurrection, the moments a player is most likely to be mid-stride. The
/// mismatch costs a rejected step and a resync round trip, and leaves the ghost
/// standing wherever the client had predicted instead of on the corpse.
///
/// The season packet is the other one: the client answers it by walking every loaded
/// map chunk and re-deriving each object's seasonal graphic (ClassicUO
/// World.ChangeSeason), so sending a season the client is already in is a visible
/// stall for no change. Upstream returns early on a repeat (CClientMsg.cpp:509).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DeathResyncTests
{
    private readonly ITestOutputHelper _out;
    public DeathResyncTests(ITestOutputHelper output) => _out = output;

    private const byte SeasonPacket = 0xBC;

    private static (GameClient Client, Character Me) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.MaxHits = 50; me.Hits = 50;
        me.BodyId = 0x0190;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, me);
    }

    private static int CountSeason(GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Count(p => p.Span.Length > 0 && p.Span[0] == SeasonPacket);

    [Fact]
    public void DyingResetsTheWalkSequenceTheRedrawJustResetOnTheClient()
    {
        var (client, me) = Stage(16710);
        client.NetState.WalkSequence = 37;      // mid-stride

        me.Kill();
        client.OnCharacterDeath();

        _out.WriteLine($"walk sequence after death: {client.NetState.WalkSequence}");
        Assert.Equal(0, client.NetState.WalkSequence);
    }

    [Fact]
    public void ResurrectingDoesTheSame()
    {
        var (client, me) = Stage(16711);
        me.Kill();
        client.OnCharacterDeath();
        client.NetState.WalkSequence = 12;

        client.OnResurrect();

        Assert.Equal(0, client.NetState.WalkSequence);
    }

    [Fact]
    public void DyingSendsTheDesolateSeasonOnce()
    {
        var (client, me) = Stage(16712);
        int before = CountSeason(client);

        me.Kill();
        client.OnCharacterDeath();

        int afterFirst = CountSeason(client);
        _out.WriteLine($"season packets: {before} -> {afterFirst}");
        Assert.True(afterFirst > before, "the dying player was never put in the desolate season");
    }

    [Fact]
    public void TheSameSeasonIsNotSentTwice()
    {
        // A second death, a region change, anything: the client is already desolate and
        // must not be made to rebuild every chunk's graphics again.
        var (client, me) = Stage(16713);
        me.Kill();
        client.OnCharacterDeath();
        int afterFirst = CountSeason(client);

        client.SendSeason((byte)SeasonType.Desolation, playSound: true);
        client.SendSeason((byte)SeasonType.Desolation, playSound: false);

        _out.WriteLine($"after two more requests for the same season: {CountSeason(client)} (was {afterFirst})");
        Assert.Equal(afterFirst, CountSeason(client));
    }

    [Fact]
    public void ADifferentSeasonStillGoesOut()
    {
        var (client, me) = Stage(16714);
        me.Kill();
        client.OnCharacterDeath();
        int afterDeath = CountSeason(client);

        client.SendSeason((byte)SeasonType.Summer, playSound: false);

        Assert.True(CountSeason(client) > afterDeath);
    }

    [Fact]
    public void AResyncSendsItRegardless()
    {
        // A resync exists to rebuild client state that may have been lost, so it cannot
        // assume what the client still holds.
        var (client, me) = Stage(16715);
        me.Kill();
        client.OnCharacterDeath();
        int afterDeath = CountSeason(client);

        client.SendSeason((byte)SeasonType.Desolation, playSound: false, force: true);

        Assert.True(CountSeason(client) > afterDeath);
    }
}
