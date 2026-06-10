using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NotifyCharMoved must handle a mobile that is in range on both ends of the
/// step but not yet tracked by the observer (e.g. an NPC that unhid after a
/// scout-hide): it has to emit the initial draw instead of staying silent.
/// </summary>
public class NpcVisibilityNotifyTests
{
    private static (GameWorld world, GameClient client, Character observer) CreateObserver()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);

        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.MaxHits = 50; observer.Hits = 50;
        world.PlaceCharacter(observer, new Point3D(1000, 1000, 0, 0));

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        var accounts = new AccountManager(loggerFactory);
        var client = new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, observer);
        return (world, client, observer);
    }

    [Fact]
    public void NotifyCharMoved_UntrackedNpcInRange_SendsInitialDraw()
    {
        var (world, client, _) = CreateObserver();

        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        npc.BodyId = 0xC8;
        npc.MaxHits = 20; npc.Hits = 20;
        world.PlaceCharacter(npc, new Point3D(1003, 1000, 0, 0));

        // Both old and new positions are in range; the observer never got an
        // appear for this NPC (KnownChars empty).
        Assert.DoesNotContain(npc.Uid.Value, client.View.KnownChars);
        var oldPos = new Point3D(1004, 1000, 0, 0);

        client.NotifyCharMoved(npc, oldPos);

        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x78);
        Assert.Contains(npc.Uid.Value, client.View.KnownChars);
    }

    [Fact]
    public void NotifyCharMoved_TrackedNpc_StillSendsMoveUpdate()
    {
        var (world, client, _) = CreateObserver();

        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        npc.BodyId = 0xC8;
        npc.MaxHits = 20; npc.Hits = 20;
        world.PlaceCharacter(npc, new Point3D(1003, 1000, 0, 0));

        var oldPos = new Point3D(1004, 1000, 0, 0);
        // First call registers the NPC (appear), second behaves as a tracked move.
        client.NotifyCharMoved(npc, oldPos);
        _ = TestHarness.GetQueuedPackets(client.NetState);

        world.MoveCharacter(npc, new Point3D(1002, 1000, 0, 0));
        client.NotifyCharMoved(npc, new Point3D(1003, 1000, 0, 0));

        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x77);
    }
}
