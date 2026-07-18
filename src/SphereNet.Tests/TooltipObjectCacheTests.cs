using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// V2 (perf/parity): the built OPL tooltip is cached on the OBJECT (Source-X
/// CObjBase::SetPropertyList) with pure TTL expiry — one build serves every
/// observer, and the entry survives the object leaving a client's view, so
/// re-entry/login bursts reuse it instead of rebuilding from scratch.
/// </summary>
public sealed class TooltipObjectCacheTests
{
    private static GameClient MakeClient(ILoggerFactory loggerFactory, GameWorld world,
        out NetState state, Point3D pos)
    {
        state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        state.ClientVersionNumber = 70_020_000;
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, pos);
        TestHarness.AttachCharacter(client, player);
        return client;
    }

    [Fact]
    public void SecondObserver_ReusesObjectCachedBuild()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.ToolTipMode = 1;
        world.ToolTipCache = 30;

        var clientA = MakeClient(loggerFactory, world, out _, new Point3D(100, 100, 0, 0));
        var clientB = MakeClient(loggerFactory, world, out var stateB, new Point3D(101, 100, 0, 0));

        var item = world.CreateItem();
        item.Name = "shared build";
        world.PlaceItem(item, new Point3D(100, 101, 0, 0));

        clientA.SendAosTooltip(item, requested: false);
        var built = item.TooltipCache;
        Assert.NotNull(built);

        // A name change would alter the props — but within the TTL the second
        // observer must be served the object-cached entry, not a rebuild, and
        // an unrequested push of a cached entry is version-only (0xDC; Source-X
        // TOOLTIPMODE_SENDVERSION) — the client pulls 0xD6 itself if it needs it.
        item.Name = "renamed while cached";
        clientB.SendAosTooltip(item, requested: false);
        Assert.Same(built, item.TooltipCache);
        var opcodesB = TestHarness.GetQueuedPackets(stateB).Select(p => p.Span[0]).ToList();
        Assert.Single(opcodesB, op => op == 0xDC);
        Assert.DoesNotContain(opcodesB, op => op == 0xD6);
    }

    [Fact]
    public void CacheSurvivesViewExit_AndInvalidateBumpsRevision()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.ToolTipMode = 1;
        world.ToolTipCache = 30;

        var client = MakeClient(loggerFactory, world, out _, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "wanders in and out";
        world.PlaceItem(item, new Point3D(100, 101, 0, 0));

        client.SendAosTooltip(item, requested: false);
        var built = item.TooltipCache;
        Assert.NotNull(built);
        uint firstRevision = built!.Revision;

        // Simulate view-exit + re-entry: the view updater no longer evicts, so
        // the object entry must still be the same built list.
        client.View.KnownItems.Remove(item.Uid.Value);
        client.SendAosTooltip(item, requested: false);
        Assert.Same(built, item.TooltipCache);

        // A real change invalidates: rebuild with a new hash bumps the revision.
        item.Name = "changed for real";
        client.SendAosTooltip(item, requested: true, invalidate: true);
        Assert.NotNull(item.TooltipCache);
        Assert.NotSame(built, item.TooltipCache);
        Assert.Equal(unchecked(firstRevision + 1), item.TooltipCache!.Revision);
    }
}
