using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit finding (wiki/test.txt #10): the classic spinning wheel and loom
/// target flows (Source-X IT_WOOL/IT_COTTON → IT_SPINWHEEL, IT_THREAD/IT_YARN
/// → IT_LOOM with MORE1/MORE2 accumulation) — previously both materials only
/// printed a hint message.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class SpinLoomCraftTests
{
    private static (GameWorld World, SphereNet.Game.Clients.GameClient Client, Character Player)
        CreatePlayerClient(int port)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), port);
        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
    }

    [Fact]
    public void WoolOnSpinningWheel_YieldsThreeYarn()
    {
        var (world, client, player) = CreatePlayerClient(2616);

        var wheel = world.CreateItem();
        wheel.ItemType = ItemType.SpinWheel;
        world.PlaceItem(wheel, new Point3D(100, 101, 0, 0));

        var wool = world.CreateItem();
        wool.ItemType = ItemType.Wool;
        wool.BaseId = 0x0DF8;
        player.Backpack!.AddItem(wool);

        client.HandleDoubleClick(wool.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            wheel.Uid.Value, wheel.X, wheel.Y, wheel.Z, 0);

        Assert.True(wool.IsDeleted, "the wool pile was not consumed");
        var yarn = player.Backpack.Contents.FirstOrDefault(i => i.ItemType == ItemType.Yarn);
        Assert.NotNull(yarn);
        Assert.Equal(3, yarn!.Amount);
    }

    [Fact]
    public void ThreadOnLoom_AccumulatesToABoltOfCloth()
    {
        var (world, client, player) = CreatePlayerClient(2617);

        var loom = world.CreateItem();
        loom.ItemType = ItemType.Loom;
        world.PlaceItem(loom, new Point3D(100, 101, 0, 0));

        // First batch: 2 units — partial weave stored on the loom.
        var thread1 = world.CreateItem();
        thread1.ItemType = ItemType.Thread;
        thread1.BaseId = 0x0FA0;
        thread1.Amount = 2;
        player.Backpack!.AddItem(thread1);

        client.HandleDoubleClick(thread1.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            loom.Uid.Value, loom.X, loom.Y, loom.Z, 0);
        Assert.Equal(2u, loom.More2);
        Assert.True(thread1.IsDeleted);

        // Second batch: 5 units — the bolt completes on 2 more, 3 remain.
        var thread2 = world.CreateItem();
        thread2.ItemType = ItemType.Thread;
        thread2.BaseId = 0x0FA0;
        thread2.Amount = 5;
        player.Backpack.AddItem(thread2);

        client.HandleDoubleClick(thread2.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            loom.Uid.Value, loom.X, loom.Y, loom.Z, 0);

        Assert.Equal(0u, loom.More2); // loom reset after the finished bolt
        Assert.Equal(3, thread2.Amount);
        var bolt = player.Backpack.Contents.FirstOrDefault(i => i.BaseId == 0x0F95);
        Assert.NotNull(bolt);
    }
}
