using SphereNet.Core.Types;
using SphereNet.MapData;

namespace SphereNet.Game.World;

/// <summary>One close operation for an expired map-static door — the caller
/// broadcasts the shut sound + the world-item art swap at the same synthetic
/// serial the open used.</summary>
public readonly record struct StaticDoorClose(
    byte Map, short X, short Y, sbyte Z, uint Serial, ushort ClosedArt, ushort Hue);

/// <summary>Auto-close pass for map-static doors. Item-doors self-close via
/// Item.OnTick; map-static doors are only an open-overlay set on the world, so
/// this sweep (driven from post-tick maintenance) is the ONLY thing that ever
/// shuts them.</summary>
public static class StaticDoorSweeper
{
    /// <summary>Collect every open map-static door whose auto-close deadline has
    /// passed, remove it from the open set and emit the close op to broadcast.
    /// The closed art derives from the static via the same GetDoorDir slot math
    /// the manual toggle-close uses — a static baked as the OPEN leaf must close
    /// to (tile - 1), not back to its own open art. Doors whose static can no
    /// longer be located are dropped and counted in
    /// <paramref name="droppedNoStatic"/> (they'd otherwise leak silently).</summary>
    public static int CollectExpired(
        GameWorld world, MapDataManager mapData, long now,
        List<StaticDoorClose> output,
        List<(byte Map, short X, short Y, sbyte Z)> scratch,
        out int droppedNoStatic)
    {
        droppedNoStatic = 0;
        output.Clear();
        scratch.Clear();
        world.CollectExpiredStaticDoors(now, scratch);
        foreach (var (map, x, y, z) in scratch)
        {
            world.SetMapStaticDoorOpen(map, x, y, z, false);

            ushort closedArt = 0, hue = 0;
            foreach (var s in mapData.GetStatics(map, x, y))
            {
                if (s.Z != z || !DoorHelper.IsDoorGraphic(mapData, s.TileId))
                    continue;
                int doorDir = DoorHelper.GetDoorDir(s.TileId);
                closedArt = doorDir >= 0 ? (ushort)(s.TileId - (doorDir & 1)) : s.TileId;
                hue = s.Hue;
                break;
            }
            if (closedArt == 0)
            {
                droppedNoStatic++;
                continue;
            }

            uint serial = (uint)(Serial.ItemFlag |
                (uint)((x & 0x7FFF) << 16) |
                (uint)((y & 0x3FFF) << 3) |
                (uint)(z & 0x07));
            output.Add(new StaticDoorClose(map, x, y, z, serial, closedArt, hue));
        }
        return output.Count;
    }
}
