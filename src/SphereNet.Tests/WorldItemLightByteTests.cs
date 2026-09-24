using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The light shape byte of a ground item (upstream adjustItemData,
/// send.cpp:597): a light-source tile sends MOREZ while it burns and LIGHT_LARGE
/// otherwise. It was always zero, so every lamp or fire placed as an item lit up
/// with the smallest shape.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WorldItemLightByteTests
{
    private const ushort LampTile = 0x0B20;
    private const ushort PlainTile = 0x0B21;

    private static (GameWorld World, Game.Clients.GameClient Client) Setup()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(LampTile, new ItemTileData { Flags = TileFlag.LightSource, Quality = 29 });
        map.SetSyntheticItemTile(PlainTile, new ItemTileData());
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7911);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (world, client);
    }

    private static int LightByte(Game.Clients.GameClient client, Item item)
    {
        var buf = client.BuildWorldItemPacket(item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, 0, item.Direction, source: item).Build();
        var span = buf.Span;
        Assert.Equal(0x1A, span[0]);
        int pos = 3;
        bool hasAmount = (BinaryPrimitives.ReadUInt32BigEndian(span[pos..]) & 0x80000000) != 0;
        pos += 4 + 2 + (hasAmount ? 2 : 0);
        bool hasDir = (BinaryPrimitives.ReadUInt16BigEndian(span[pos..]) & 0x8000) != 0;
        pos += 4;
        return hasDir ? span[pos] : 0;
    }

    private static Item Place(GameWorld world, ushort id, ItemType type, sbyte moreZ = 0)
    {
        var item = world.CreateItem();
        item.BaseId = id;
        item.ItemType = type;
        item.MoreP = new Point3D(0, 0, moreZ, 0);
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        return item;
    }

    [Fact]
    public void ABurningLightSendsItsPattern()
    {
        var (world, client) = Setup();
        Assert.Equal(1, LightByte(client, Place(world, LampTile, ItemType.Fire, moreZ: 1)));
        Assert.Equal(7, LightByte(client, Place(world, LampTile, ItemType.LightLit, moreZ: 7)));
    }

    [Fact]
    public void ALightSourceThatIsNotBurningSendsTheLargeShape()
    {
        var (world, client) = Setup();
        Assert.Equal(1, LightByte(client, Place(world, LampTile, ItemType.Normal)));
    }

    [Fact]
    public void AnOrdinaryTileSendsNoLight()
    {
        var (world, client) = Setup();
        Assert.Equal(0, LightByte(client, Place(world, PlainTile, ItemType.Normal)));
    }
}
