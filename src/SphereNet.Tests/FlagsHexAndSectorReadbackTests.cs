using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Game.Magic;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// SPELL FLAGS and SERV.TILEDATA.ITEM(n).FLAGS read back as Sphere hex
/// (CSpellDef.cpp:119, CServerConfig.cpp:2042 FormatULLHex: '0' prefix, "00" for
/// none), and a bare SERV.MAP.n.SECTOR.n resolves for a valid sector without being
/// answered as the sector's name (CServerConfig.cpp:1763-1776).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class FlagsHexAndSectorReadbackTests
{
    [Fact]
    public void SpellFlags_ReadBackAsSphereHex()
    {
        var def = new SpellDef { Id = SpellType.Dispel, Flags = SpellFlag.TargChar };
        Assert.True(def.TryGetProperty("FLAGS", out string flags));
        Assert.Equal("0" + ((ulong)SpellFlag.TargChar).ToString("x"), flags);

        var none = new SpellDef { Id = SpellType.Dispel };
        Assert.True(none.TryGetProperty("FLAGS", out string zero));
        Assert.Equal("00", zero);
    }

    private static string? Serv(string key)
    {
        var p = typeof(SphereNet.Server.Program);
        var resolver = p.GetMethod("ResolveServerProperty", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?)resolver.Invoke(null, [key]);
    }

    [Fact]
    public void TileDataItemFlags_ReadBackAsSphereHex()
    {
        var p = typeof(SphereNet.Server.Program);
        var field = p.GetField("_mapData", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        try
        {
            var map = new MapDataManager("");
            map.SetSyntheticItemTile(0x0080, new ItemTileData
            { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });
            map.SetSyntheticItemTile(0x0081, new ItemTileData { Name = "plain" });
            field.SetValue(null, map);

            ulong wall = (ulong)(TileFlag.Wall | TileFlag.Impassable);
            Assert.Equal("0" + wall.ToString("x"), Serv("TILEDATA.ITEM(080).FLAGS"));
            Assert.Equal("00", Serv("TILEDATA.ITEM(081).FLAGS"));
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    [Fact]
    public void BareMapSector_ResolvesWithoutAnsweringTheSectorName()
    {
        var p = typeof(SphereNet.Server.Program);
        var field = p.GetField("_world", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        try
        {
            var world = TestHarness.CreateWorld();
            field.SetValue(null, world);
            Assert.NotNull(world.GetSectorByIndex(0, 1));

            Assert.Equal("0", Serv("MAP.0.SECTOR.1"));
            // A property on the sector still reads through.
            Assert.Equal("1", Serv("MAP.0.SECTOR.1.NUMBER"));
            // A sector that does not exist answers 0.
            Assert.Equal("0", Serv("MAP.0.SECTOR.99999999"));
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }
}
