using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using SphereNet.Game.Magic;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class InventoryStackSplitTests
{
    private static void LoadPileDefinitions()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_inventory_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [ITEMDEF 0f3f]
            DEFNAME=i_arrow
            CAN=0100
            FLIP=1
            """);

        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static (GameWorld World, GameClient Client, Character Player, Item Pack)
        CreatePlayerWithArrowStack(int port)
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var arrows = world.CreateItem();
        arrows.BaseId = 0x0F3F;
        arrows.Amount = 200;
        arrows.Position = new Point3D(44, 65, 0, 0);
        pack.AddItem(arrows);

        return (world, client, player, pack);
    }

    [Fact]
    public void DropPartialStackOntoOriginalStack_MergesWithoutLeavingSplitItem()
    {
        LoadPileDefinitions();

        var (world, client, player, pack) = CreatePlayerWithArrowStack(9101);
        var arrows = pack.Contents.Single(i => i.BaseId == 0x0F3F);

        client.HandleItemPickup(arrows.Uid.Value, 50);

        Assert.True(player.TryGetTag("DRAGGING", out var dragging));
        Assert.Equal(arrows.Uid.Value.ToString(), dragging);
        Assert.Equal(50, arrows.Amount);

        var remainder = pack.Contents.Single(i => i.Uid != arrows.Uid);
        Assert.Equal(150, remainder.Amount);

        var splitPackets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        Assert.Contains(splitPackets, p =>
            p.Span[0] == 0x25 &&
            ((uint)(p.Span[1] << 24 | p.Span[2] << 16 | p.Span[3] << 8 | p.Span[4])) == remainder.Uid.Value &&
            ((ushort)((p.Span[8] << 8) | p.Span[9])) == 150);

        client.HandleItemDrop(arrows.Uid.Value, remainder.X, remainder.Y, 0, remainder.Uid.Value);

        Assert.Equal(200, remainder.Amount);
        Assert.Null(world.FindItem(arrows.Uid));
        Assert.Single(pack.Contents);
        Assert.Same(remainder, pack.Contents[0]);
    }

    [Fact]
    public void DropPartialStackIntoBackpack_KeepsSplitItemVisible()
    {
        LoadPileDefinitions();

        var (_, client, player, pack) = CreatePlayerWithArrowStack(9103);
        var arrows = pack.Contents.Single(i => i.BaseId == 0x0F3F);

        client.HandleItemPickup(arrows.Uid.Value, 50);
        client.HandleItemDrop(arrows.Uid.Value, 70, 80, 0, pack.Uid.Value);

        Assert.False(player.TryGetTag("DRAGGING", out _));
        Assert.Equal(2, pack.Contents.Count);
        Assert.Contains(pack.Contents, i => i.Uid == arrows.Uid && i.Amount == 50 && i.X == 70 && i.Y == 80);
        Assert.Contains(pack.Contents, i => i.Uid != arrows.Uid && i.Amount == 150);
    }

    [Fact]
    public void DropPartialStackIntoBank_KeepsSplitItemVisible()
    {
        LoadPileDefinitions();

        var (world, client, _, pack) = CreatePlayerWithArrowStack(9104);
        var player = world.FindChar(client.Character!.Uid)!;
        var arrows = pack.Contents.Single(i => i.BaseId == 0x0F3F);

        var bank = world.CreateItem();
        bank.BaseId = 0x0E75;
        bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);

        client.HandleItemPickup(arrows.Uid.Value, 50);
        client.HandleItemDrop(arrows.Uid.Value, 25, 30, 0, bank.Uid.Value);

        Assert.Single(pack.Contents);
        Assert.Equal(150, pack.Contents[0].Amount);
        Assert.Single(bank.Contents);
        Assert.Same(arrows, bank.Contents[0]);
        Assert.Equal(50, arrows.Amount);
    }

    [Fact]
    public void DropPartialStackOnGround_KeepsSplitItemVisibleAndPileGraphic()
    {
        LoadPileDefinitions();

        var (_, client, _, pack) = CreatePlayerWithArrowStack(9105);
        client.NetState.ClientVersionNumber = 70_096_000;
        client.BroadcastNearby = (_, _, packet, _) => client.NetState.Send(packet);
        var arrows = pack.Contents.Single(i => i.BaseId == 0x0F3F);

        client.HandleItemPickup(arrows.Uid.Value, 50);
        client.HandleItemDrop(arrows.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        Assert.True(arrows.IsOnGround);
        Assert.Equal(50, arrows.Amount);
        Assert.Equal(0, arrows.Direction);
        Assert.Single(pack.Contents);
        Assert.Equal(150, pack.Contents[0].Amount);

        var worldItem = TestHarness.GetQueuedPackets(client.NetState)
            .Last(p => p.Span[0] == 0xF3 && ((uint)(p.Span[4] << 24 | p.Span[5] << 16 | p.Span[6] << 8 | p.Span[7])) == arrows.Uid.Value);
        Assert.Equal(0x0F3F, (ushort)((worldItem.Span[8] << 8) | worldItem.Span[9]));
        Assert.Equal(0, worldItem.Span[10]);
        Assert.Equal(50, (ushort)((worldItem.Span[11] << 8) | worldItem.Span[12]));
        Assert.Equal(50, (ushort)((worldItem.Span[13] << 8) | worldItem.Span[14]));
    }

    [Fact]
    public void DropPileItemOnGround_KeepsBaseGraphic()
    {
        LoadPileDefinitions();

        var (_, client, _, pack) = CreatePlayerWithArrowStack(9102);
        client.NetState.ClientVersionNumber = 70_096_000; // wiki repro: 7.0.96
        client.BroadcastNearby = (_, _, packet, _) => client.NetState.Send(packet);
        var arrows = pack.Contents.Single(i => i.BaseId == 0x0F3F);

        client.HandleItemPickup(arrows.Uid.Value, 200);
        client.HandleItemDrop(arrows.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        Assert.Equal(0x0F3F, arrows.BaseId);
        Assert.Equal(0x0F3F, arrows.DispIdFull);
        Assert.Equal(0, arrows.Direction);

        var worldItem = TestHarness.GetQueuedPackets(client.NetState).Single(p => p.Span[0] == 0xF3);
        Assert.Equal(0x0F3F, (ushort)((worldItem.Span[8] << 8) | worldItem.Span[9]));
        Assert.Equal(0, worldItem.Span[10]); // direction must stay DIR_N for pile items
        Assert.Equal(200, (ushort)((worldItem.Span[11] << 8) | worldItem.Span[12]));
        Assert.Equal(200, (ushort)((worldItem.Span[13] << 8) | worldItem.Span[14]));
    }
}
