using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Keeping the open status window honest.
///
/// Upstream refreshes it from the client's own cycle: every stat change calls
/// CChar::UpdateStatsFlag, and CClient::UpdateStats flushes one addStatusWindow per
/// tick (CClientMsg.cpp:2174). Nothing here did that - the 0x11 packet only went out
/// from the dozen call sites that remembered to ask for it, so a change made anywhere
/// else left the window showing the old numbers until the player closed and reopened
/// it.
///
/// The refresh compares only the NON-VITAL half of the window. Hits, mana and stamina
/// change on every swing and the client tracks them through 0xA1/0xA2/0xA3; forcing a
/// full status packet for those would put a ninety-byte packet on the wire every tick
/// of every fight.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StatusWindowRefreshTests
{
    private readonly ITestOutputHelper _out;
    public StatusWindowRefreshTests(ITestOutputHelper output) => _out = output;

    private const byte StatusFull = 0x11;

    private static (GameClient Client, Character Me) Stage(int port)
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.IsOnline = true;
        me.Str = 50; me.Dex = 50; me.Int = 50;
        me.MaxHits = 50; me.Hits = 50;
        me.MaxStam = 50; me.Stam = 50;
        me.MaxMana = 50; me.Mana = 50;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, me);
    }

    private static int CountStatus(GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Count(p => p.Span.Length > 0 && p.Span[0] == StatusFull);

    [Fact]
    public void AStatChangeReachesTheOpenWindow()
    {
        var (client, me) = Stage(16510);
        client.SendCharacterStatus(me);          // the window is open and current
        int before = CountStatus(client);

        me.Str = 80;                             // a script, a .edit, a stat gain
        client.RefreshStatusIfChanged();

        _out.WriteLine($"status packets {before} -> {CountStatus(client)}");
        Assert.True(CountStatus(client) > before, "the window was never told");
    }

    [Fact]
    public void NothingChangedSendsNothing()
    {
        // The refresh runs on every tick a character is dirty, so "no change" has to
        // cost nothing on the wire.
        var (client, me) = Stage(16511);
        client.SendCharacterStatus(me);
        int before = CountStatus(client);

        for (int i = 0; i < 20; i++)
            client.RefreshStatusIfChanged();

        Assert.Equal(before, CountStatus(client));
    }

    [Fact]
    public void TakingDamageDoesNotForceAFullStatusPacket()
    {
        // Vitals have their own small packets. A fight must not turn into one 0x11 per
        // swing - that is the reason the comparison excludes them.
        var (client, me) = Stage(16512);
        client.SendCharacterStatus(me);
        int before = CountStatus(client);

        for (short h = 49; h > 20; h--)
        {
            me.Hits = h;
            client.RefreshStatusIfChanged();
        }

        _out.WriteLine($"after 29 points of damage: {CountStatus(client)} status packets (was {before})");
        Assert.Equal(before, CountStatus(client));
    }

    [Theory]
    [InlineData("maxhits")]
    [InlineData("fame")]
    [InlineData("karma")]
    public void TheOtherFieldsCountToo(string field)
    {
        var (client, me) = Stage(16520 + field.Length);
        client.SendCharacterStatus(me);
        int before = CountStatus(client);

        switch (field)
        {
            case "maxhits": me.MaxHits = 120; break;
            case "fame": me.Fame = 1234; break;
            case "karma": me.Karma = -500; break;
        }
        client.RefreshStatusIfChanged();

        Assert.True(CountStatus(client) > before, $"{field} did not reach the window");
    }

    [Fact]
    public void AnExplicitSendLeavesNothingPending()
    {
        // SendCharacterStatus is still called directly from a dozen places. Each of
        // those has to leave the comparison up to date, or the next dirty tick sends a
        // second copy of what the client just received.
        var (client, me) = Stage(16530);
        me.Str = 77;
        client.SendCharacterStatus(me);
        int after = CountStatus(client);

        client.RefreshStatusIfChanged();
        Assert.Equal(after, CountStatus(client));
    }
}
