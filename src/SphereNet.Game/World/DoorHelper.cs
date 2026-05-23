using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;
using SphereNet.MapData;
using SphereNet.MapData.Map;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.World;

/// <summary>Tiledata-based door detection and map-static door lookup.</summary>
public static class DoorHelper
{
    public static bool IsDoorGraphic(MapDataManager? mapData, ushort graphic)
    {
        if (HasDoorTileFlag(mapData, graphic))
            return true;

        // Open/closed door art is usually ±1; some portcullis pairs use ±2.
        if (graphic > 0 && HasDoorTileFlag(mapData, (ushort)(graphic - 1)))
            return true;
        if (graphic < ushort.MaxValue && HasDoorTileFlag(mapData, (ushort)(graphic + 1)))
            return true;
        if (graphic > 1 && HasDoorTileFlag(mapData, (ushort)(graphic - 2)))
            return true;
        return graphic < ushort.MaxValue - 1 && HasDoorTileFlag(mapData, (ushort)(graphic + 2));
    }

    public static bool IsDoorItem(Item item, MapDataManager? mapData)
    {
        if (item.ItemType is ItemType.Door or ItemType.DoorLocked or ItemType.DoorOpen
            or ItemType.Portculis or ItemType.PortLocked)
            return true;

        if (IsDoorGraphic(mapData, item.BaseId) || IsDoorGraphic(mapData, item.DispIdFull))
            return true;

        var def = DefinitionLoader.GetItemDef(item.BaseId);
        if (def == null)
            return false;

        if (def.Type is ItemType.Door or ItemType.DoorLocked or ItemType.DoorOpen
            or ItemType.Portculis or ItemType.PortLocked)
            return true;

        return (def.TFlags & (ulong)TileFlag.Door) != 0;
    }

    public static bool FindNearestStaticDoor(
        MapDataManager? mapData,
        byte mapId,
        short centerX,
        short centerY,
        int range,
        out short doorX,
        out short doorY,
        out sbyte doorZ,
        out ushort tileId,
        out ushort hue)
    {
        doorX = 0;
        doorY = 0;
        doorZ = 0;
        tileId = 0;
        hue = 0;

        if (mapData == null || range < 0)
            return false;

        int bestDist = int.MaxValue;
        bool found = false;

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                short x = (short)(centerX + dx);
                short y = (short)(centerY + dy);
                foreach (var s in mapData.GetStatics(mapId, x, y))
                {
                    if (!IsDoorGraphic(mapData, s.TileId))
                        continue;

                    int dist = Math.Abs(dx) + Math.Abs(dy);
                    if (dist >= bestDist)
                        continue;

                    bestDist = dist;
                    doorX = x;
                    doorY = y;
                    doorZ = s.Z;
                    tileId = s.TileId;
                    hue = s.Hue;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool HasDoorTileFlag(MapDataManager? mapData, ushort graphic)
    {
        if (mapData == null)
            return false;
        var data = mapData.GetItemTileData(graphic);
        return (data.Flags & TileFlag.Door) != 0;
    }
}
