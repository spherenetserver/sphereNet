using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class ItemWeightSystemTests
{
    private const ushort HeavyItemId = 0x0AAA;
    private const ushort LightItemId = 0x0AAB;
    private const ushort WeightlessItemId = 0x0AAC;
    private const ushort ContainerId = 0x0AAD;

    private static void LoadWeightDefinitions()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_weight_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [ITEMDEF 0aaa]
            DEFNAME=i_weight_heavy
            WEIGHT=2

            [ITEMDEF 0aab]
            DEFNAME=i_weight_light
            WEIGHT=0.5

            [ITEMDEF 0aac]
            DEFNAME=i_weightless
            WEIGHT=0

            [ITEMDEF 0aad]
            DEFNAME=i_weight_container
            WEIGHT=1.0
            """);

        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void ItemDefWeight_ParsesScriptStonesIntoTenths()
    {
        LoadWeightDefinitions();

        Assert.Equal(20, DefinitionLoader.GetItemDef(HeavyItemId)!.Weight);
        Assert.Equal(5, DefinitionLoader.GetItemDef(LightItemId)!.Weight);
        Assert.Equal(0, DefinitionLoader.GetItemDef(WeightlessItemId)!.Weight);
    }

    [Fact]
    public void CharacterTotalWeight_CountsStacksInWholeStones()
    {
        LoadWeightDefinitions();

        var world = CreateWorld();
        var player = world.CreateCharacter();
        var pack = CreatePack(world, player);

        var heavy = world.CreateItem();
        heavy.BaseId = HeavyItemId;
        heavy.Amount = 3;
        pack.AddItem(heavy);

        var light = world.CreateItem();
        light.BaseId = LightItemId;
        light.Amount = 2;
        pack.AddItem(light);

        Assert.Equal(80, pack.TotalWeightTenths);
        Assert.Equal(8, pack.TotalWeight);
        Assert.Equal(8, player.GetTotalWeight());
    }

    [Fact]
    public void ContainerDropWeightLimit_UsesStoneLimitNotRawTenths()
    {
        LoadWeightDefinitions();

        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.MaxContainerWeight = 3;
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 9201);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = CreatePack(world, player);
        var bag = world.CreateItem();
        bag.BaseId = ContainerId;
        bag.ItemType = ItemType.Container;
        pack.AddItem(bag);

        var heavy = world.CreateItem();
        heavy.BaseId = HeavyItemId;
        heavy.Amount = 2; // 4 stones, over the 3-stone container cap
        pack.AddItem(heavy);

        client.HandleItemPickup(heavy.Uid.Value, heavy.Amount);
        client.HandleItemDrop(heavy.Uid.Value, 40, 40, 0, bag.Uid.Value);

        Assert.Empty(bag.Contents);
        Assert.Contains(heavy, pack.Contents);
    }

    [Fact]
    public void TradeWeightLimit_UsesNestedTenthsAndAllowsWeightlessItems()
    {
        LoadWeightDefinitions();

        var world = CreateWorld();
        var recipient = world.CreateCharacter();
        recipient.Str = 1;
        recipient.ModMaxWeight = -43; // cap ~= 3 stones

        var tradeContainer = world.CreateItem();
        tradeContainer.ItemType = ItemType.Container;

        var heavy = world.CreateItem();
        heavy.BaseId = HeavyItemId;
        heavy.Amount = 2; // 4 stones
        tradeContainer.AddItem(heavy);

        Assert.False(TradeManager.CanAcceptTradeItems(recipient, world, tradeContainer, out _));

        tradeContainer.RemoveItem(heavy);
        var weightless = world.CreateItem();
        weightless.BaseId = WeightlessItemId;
        weightless.Amount = 500;
        tradeContainer.AddItem(weightless);

        Assert.True(TradeManager.CanAcceptTradeItems(recipient, world, tradeContainer, out _));
    }

    private static GameWorld CreateWorld()
    {
        var world = TestHarness.CreateWorld();
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item CreatePack(GameWorld world, Character player)
    {
        var pack = world.CreateItem();
        pack.BaseId = ContainerId;
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        return pack;
    }
}
