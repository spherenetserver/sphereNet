using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Field report (2026-07-19): after a GM .remove'd their backpack, dropping an
/// item onto themselves did not create a new pack. Root cause: the cached
/// Character.Backpack field kept the DELETED pack reference, so the
/// GetPackSafe-style recreation (EnsurePlayerBackpack) never fired and the
/// drop landed inside a deleted container.
/// </summary>
public sealed class BackpackRecreationTests
{
    [Fact]
    public void DeletedBackpack_SelfDrop_CreatesFreshPackAndHoldsItem()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        // Give the player a pack, then GM-remove it.
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;
        world.DeleteObject(pack);

        // The stale cached reference must not resurface.
        Assert.Null(player.Backpack);

        // Dropping an item onto self must mint a fresh pack and land inside it.
        var apple = world.CreateItem();
        apple.Name = "apple";
        world.PlaceItem(apple, player.Position);
        client.PlaceItemInPack(player, apple);

        var newPack = player.Backpack;
        Assert.NotNull(newPack);
        Assert.False(newPack!.IsDeleted);
        Assert.NotEqual(pack.Uid, newPack.Uid);
        Assert.Equal(newPack, player.GetEquippedItem(Layer.Pack));
        Assert.Contains(apple, newPack.Contents);
        Assert.Equal(player.Uid, new Serial(newPack.ContainedIn.Value));
    }
}
