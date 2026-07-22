using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 11 / M1 — the parent-chain walk in SendItemVisualUpdate had no depth cap or
/// cycle guard, so a corrupt/hand-edited save storing a containment cycle
/// (A cont B, B cont A, or an item contained in itself) looped forever on the
/// main thread and hung the shard. The walk is now bounded (16, like every other
/// parent-chain walk), and the loader quarantines a cyclic CONT to the ground so
/// the cycle never reaches the runtime.
/// </summary>
public sealed class ContainerCycleGuardTests
{
    private static (GameClient client, NetState state, GameWorld world) MakePlayingClient()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        Assert.True(client.IsPlaying);
        return (client, state, world);
    }

    [Fact]
    public void SendItemVisualUpdate_TwoItemCycle_DoesNotHang()
    {
        var (client, _, world) = MakePlayingClient();
        var a = world.CreateItem();
        var b = world.CreateItem();
        a.ContainedIn = b.Uid;
        b.ContainedIn = a.Uid; // A -> B -> A

        var ex = Record.Exception(() => client.SendItemVisualUpdate(a));
        Assert.Null(ex); // returns via the depth cap instead of looping forever
    }

    [Fact]
    public void SendItemVisualUpdate_SelfContainedItem_DoesNotHang()
    {
        var (client, _, world) = MakePlayingClient();
        var a = world.CreateItem();
        a.ContainedIn = a.Uid; // A -> A

        Assert.Null(Record.Exception(() => client.SendItemVisualUpdate(a)));
    }

    [Fact]
    public void SendItemVisualUpdate_HundredLevelChain_DoesNotHang()
    {
        var (client, _, world) = MakePlayingClient();
        var items = new Item[101];
        for (int i = 0; i < items.Length; i++) items[i] = world.CreateItem();
        for (int i = 0; i < 100; i++) items[i].ContainedIn = items[i + 1].Uid; // 100-deep chain

        Assert.Null(Record.Exception(() => client.SendItemVisualUpdate(items[0])));
    }

    [Fact]
    public void SendItemVisualUpdate_NormalNestedContainer_StillSendsToOwner()
    {
        var (client, state, world) = MakePlayingClient();
        var owner = client.Character!;

        var container = world.CreateItem();
        container.ContainedIn = owner.Uid; // container owned by the viewing character
        var inside = world.CreateItem();
        inside.ContainedIn = container.Uid; // item in that container

        client.SendItemVisualUpdate(inside);

        var queued = TestHarness.GetQueuedPackets(state);
        Assert.Contains(queued, p => p.Span.Length > 0 && p.Span[0] == 0x25); // 0x25 container item
    }

    [Fact]
    public void Load_SaveWithContainmentCycle_BreaksCycle_WithoutHanging()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_cyc_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Hand-crafted corrupt save: two items each naming the other as CONT.
            string path = Path.Combine(dir, "sphereworld.scp");
            using (var w = SaveIO.OpenWriter(path, SaveFormat.Text))
            {
                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", "040000001");
                w.WriteProperty("ID", "0EED");
                w.WriteProperty("P", "1000,1000,0,0");
                w.WriteProperty("CONT", "040000002");
                w.EndRecord();
                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", "040000002");
                w.WriteProperty("ID", "0EED");
                w.WriteProperty("P", "1001,1000,0,0");
                w.WriteProperty("CONT", "040000001");
                w.EndRecord();
            }

            var world = TestHarness.CreateWorld();
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
            (int items, int chars) result = default;
            var ex = Record.Exception(() => result = loader.Load(world, dir));
            Assert.Null(ex);                 // load completes, no hang
            Assert.Equal(2, result.items);

            var a = world.FindItem(new Serial(0x40000001));
            var b = world.FindItem(new Serial(0x40000002));
            Assert.NotNull(a);
            Assert.NotNull(b);
            // The cycle is broken: at least one item was quarantined to the ground
            // (ContainedIn cleared), so no ContainedIn chain loops.
            Assert.True(!a!.ContainedIn.IsValid || !b!.ContainedIn.IsValid);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
