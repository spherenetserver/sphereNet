using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Components;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Invisible spawn worldgems render for AllShow AND for GM+ staff without AllShow
/// (ClientViewUpdater canSeeInvisItems). The double-click can-see gate only checked
/// AllShow, so a GM who legitimately saw an invisible spawner and double-clicked it
/// to fire a spawn instead got a PacketDeleteObject "correction" — the item vanished
/// from view (alive server-side) and never triggered. The gate now matches the
/// view-send audience (AllShow OR GM+).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnDoubleClickVisibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OwnerDoubleClickStoppedSpawnerRestartsProductionWithoutDialog(bool items, bool configuredPack)
    {
        string? root = Environment.GetEnvironmentVariable("SPHERENET_DIALOG_PACK");
        if (configuredPack && string.IsNullOrWhiteSpace(root)) return;
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreateClient(lf, world, new AccountManager(lf), out var player);
        player.PrivLevel = PrivLevel.Owner;
        var account = new Account { PrivLevel = PrivLevel.Owner };
        Character.ResolveAccountForChar = uid => uid == player.Uid ? account : null;
        if (configuredPack)
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            foreach (string file in new[] { "defnames/defs_types_hardcoded.scp",
                "itemdefs/misc/sphere_item_unsorted.scp", "itemdefs/sphere_item_typedefs.scp" })
                stack.Resources.LoadResourceFile(Path.Combine(root!, file));
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
        }
        var spawner = MakeInvisibleSpawner(world, player.Position);
        if (items)
        {
            spawner.SpawnChar = null;
            spawner.ItemType = ItemType.SpawnItem;
            spawner.SpawnItem = new ItemSpawnComponent(spawner, world)
                { ItemDefId = 0x0EED, MaxCount = 1, SpawnRange = 0 };
            spawner.SpawnItem.Stop();
            spawner.SpawnItem.OnTick(Environment.TickCount64 + 3600000);
            Assert.Equal(0, spawner.SpawnItem.CurrentCount);
        }
        else
        {
            spawner.SpawnChar!.Stop();
            spawner.SpawnChar.OnTick(Environment.TickCount64 + 3600000);
            Assert.Equal(0, spawner.SpawnChar.CurrentCount);
        }

        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(1, items ? spawner.SpawnItem!.CurrentCount : spawner.SpawnChar!.CurrentCount);
        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(0, items ? spawner.SpawnItem!.CurrentCount : spawner.SpawnChar!.CurrentCount);
        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(1, items ? spawner.SpawnItem!.CurrentCount : spawner.SpawnChar!.CurrentCount);
        Assert.False(spawner.IsDeleted);
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(client.NetState),
            packet => packet.Span[0] is 0xB0 or 0xDD);
    }

    [Fact]
    public void GmDoubleClick_InvisibleSpawner_TriggersSpawn_AndKeepsItem()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = CreateClient(lf, world, accounts, out var player);
        player.PrivLevel = PrivLevel.GM;

        var spawner = MakeInvisibleSpawner(world, player.Position);

        client.HandleDoubleClick(spawner.Uid.Value);

        // The GM sees the spawner, so the double-click reaches the spawn handler
        // instead of being "corrected" away — a child spawns and the gem survives.
        Assert.False(spawner.IsDeleted);
        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
    }

    [Fact]
    public void PlayerDoubleClick_InvisibleSpawner_DoesNotTrigger()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var client = CreateClient(lf, world, accounts, out var player);
        player.PrivLevel = PrivLevel.Player;
        player.AllShow = false;

        var spawner = MakeInvisibleSpawner(world, player.Position);

        client.HandleDoubleClick(spawner.Uid.Value);

        // A plain player cannot see the invisible gem, so the gate still blocks the
        // use (no spawn). The server item is never deleted regardless.
        Assert.False(spawner.IsDeleted);
        Assert.Equal(0, spawner.SpawnChar!.CurrentCount);
    }

    private static Item MakeInvisibleSpawner(GameWorld world, Point3D at)
    {
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.SetAttr(ObjAttributes.Invis);
        spawner.SpawnChar = new SpawnComponent(spawner, world) { CharDefId = 0x0190, MaxCount = 1 };
        world.PlaceItem(spawner, at);
        return spawner;
    }

    private static GameClient CreateClient(ILoggerFactory lf, GameWorld world,
        AccountManager accounts, out Character player)
    {
        var state = TestHarness.CreateActiveNetState(lf, System.Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return client;
    }
}
