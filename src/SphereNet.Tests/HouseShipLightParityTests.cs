using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

/// <summary>
/// Faz-2 tracker items: B8 (0x99 multi target preview wire format), B11 (0xF6
/// deck-object list), B12 (wet statics count as sailable water), D1 (lit light
/// sources burn one charge per minute and burn out).
/// </summary>
public sealed class HouseShipLightParityTests
{
    [Fact]
    public void PacketTargetMulti_WireFormat_MatchesSourceX()
    {
        // HS layout: [0x99][ground u8][cursor u32][flags u8][11 unused]
        //            [multiId u16][x u16][y u16][z u16][hue u32] = 30 bytes.
        var pkt = new PacketTargetMulti(0x11223344, 0x0064, 0, 7, 0, 0x0489, includeHue: true);
        var buf = pkt.Build();
        Assert.Equal(30, buf.Length);
        Assert.Equal(0x99, buf.Data[0]);
        Assert.Equal(1, buf.Data[1]);                       // allow ground
        Assert.Equal(0x11, buf.Data[2]);                    // cursor id BE
        Assert.Equal(0x00, buf.Data[18]);                   // multi id BE hi
        Assert.Equal(0x64, buf.Data[19]);                   // multi id BE lo
        Assert.Equal(0x00, buf.Data[22]);                   // yOff BE hi
        Assert.Equal(0x07, buf.Data[23]);                   // yOff BE lo

        // Classic clients: same layout without the hue dword = 26 bytes.
        Assert.Equal(26, new PacketTargetMulti(1, 0x64, 0, 7, 0, 0, includeHue: false).Build().Length);
    }

    [Fact]
    public void PacketBoatSmoothMove_CarriesDeckObjectList()
    {
        var pkt = new PacketBoatSmoothMove(0x40000001, 4, 2, 2, 1500, 1600, 5,
            [(0x00000002u, (short)1501, (short)1601, (sbyte)7)]);
        var buf = pkt.Build();

        // Header (16 bytes incl. len) + count u16 + one 10-byte entry = 28.
        Assert.Equal(28, buf.Length);
        Assert.Equal(0xF6, buf.Data[0]);
        Assert.Equal(0x00, buf.Data[16]);  // count BE hi
        Assert.Equal(0x01, buf.Data[17]);  // count BE lo
        Assert.Equal(0x02, buf.Data[21]);  // entry serial BE lo byte

        // Empty list still writes the count field (Source-X always does).
        var empty = new PacketBoatSmoothMove(0x40000001, 4, 2, 2, 1500, 1600, 5).Build();
        Assert.Equal(18, empty.Length);
        Assert.Equal(0x00, empty.Data[16]);
        Assert.Equal(0x00, empty.Data[17]);
    }

    [Fact]
    public void LitLight_BurnsChargePerTick_AndBurnsOut()
    {
        var world = TestHarness.CreateWorld();
        var torch = world.CreateItem();
        torch.ItemType = ItemType.LightLit;
        torch.SetTag("LIGHT_CHARGES", "2");
        world.PlaceItem(torch, new Point3D(100, 100, 0, 0));

        // First tick: one charge left, still lit, timer re-armed.
        torch.SetTimeout(Environment.TickCount64 - 1);
        torch.OnTick();
        Assert.Equal(ItemType.LightLit, torch.ItemType);
        Assert.True(torch.TryGetTag("LIGHT_CHARGES", out string? c1) && c1 == "1");
        Assert.True(torch.Timeout > Environment.TickCount64);

        // Second tick: burned out — doused, marked, timer cleared.
        torch.SetTimeout(Environment.TickCount64 - 1);
        torch.OnTick();
        Assert.Equal(ItemType.LightOut, torch.ItemType);
        Assert.True(torch.TryGetTag("LIGHT_BURNED", out _));
        Assert.Equal(0, torch.Timeout);
    }

    [Fact]
    public void LitLight_StaticAttr_BurnsForever()
    {
        var world = TestHarness.CreateWorld();
        var lamp = world.CreateItem();
        lamp.ItemType = ItemType.LightLit;
        lamp.SetAttr(ObjAttributes.Move_Never);
        lamp.SetTag("LIGHT_CHARGES", "1");
        world.PlaceItem(lamp, new Point3D(100, 100, 0, 0));

        lamp.SetTimeout(Environment.TickCount64 - 1);
        lamp.OnTick();
        Assert.Equal(ItemType.LightLit, lamp.ItemType);
        Assert.True(lamp.TryGetTag("LIGHT_CHARGES", out string? c) && c == "1");
    }

    [Fact]
    public void ShipWater_WetStaticCountsAsSailable()
    {
        const ushort WaterStatic = 0x1796;
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3); // dry land everywhere
        md.SetSyntheticItemTile(WaterStatic, new ItemTileData { Flags = TileFlag.Wet });
        md.AddSyntheticStatic(0, 50, 50, WaterStatic, 0);

        var world = TestHarness.CreateWorld();
        world.MapData = md;
        var ships = new SphereNet.Game.Ships.ShipEngine(world,
            new SphereNet.Game.Housing.MultiRegistry(), md);

        Assert.True(ships.IsWaterAt(0, 50, 50));   // wet static over dry land
        Assert.False(ships.IsWaterAt(0, 60, 60));  // plain dry land
    }
}
