using SphereNet.Core.Types;

namespace SphereNet.Game.World.Regions;

/// <summary>
/// Read-only spatial index for room lookups. Each 64x64 cell holds the rooms whose
/// rects touch it, in registration order, so the first one that contains a point is
/// the one a full scan of the room list picks. Every step a character takes asks
/// which room it is in (MovementEngine.CheckLocationEffects); scanning every ROOMDEF
/// in the pack for each of them was the largest single cost of walking.
///
/// An instance is never mutated after <see cref="Build"/>, so parallel readers need no
/// lock. A room's rects or map changing bumps <see cref="Room.LayoutVersion"/>, and the
/// world builds a new index on its next lookup.
/// </summary>
internal sealed class RoomIndex
{
    private const int CellShift = 6;

    private sealed class MapGrid(int cols, int rows)
    {
        public readonly int Cols = cols;
        public readonly int Rows = rows;
        public readonly Room[]?[] Cells = new Room[]?[cols * rows];
    }

    private readonly Dictionary<byte, MapGrid> _grids;

    public int Version { get; }

    private RoomIndex(Dictionary<byte, MapGrid> grids, int version)
    {
        _grids = grids;
        Version = version;
    }

    public static RoomIndex Build(IReadOnlyList<Room> rooms, int version)
    {
        var byMap = new Dictionary<byte, List<Room>>();
        foreach (var room in rooms)
        {
            if (room.Rects.Count == 0) continue;
            if (!byMap.TryGetValue(room.MapIndex, out var list))
                byMap[room.MapIndex] = list = [];
            list.Add(room);
        }

        var grids = new Dictionary<byte, MapGrid>();
        foreach (var (map, list) in byMap)
        {
            int maxX = 0, maxY = 0;
            foreach (var room in list)
                foreach (var r in room.Rects)
                {
                    maxX = Math.Max(maxX, r.X2);
                    maxY = Math.Max(maxY, r.Y2);
                }
            var grid = new MapGrid((maxX >> CellShift) + 1, (maxY >> CellShift) + 1);
            var cellLists = new List<Room>?[grid.Cells.Length];
            foreach (var room in list)
                foreach (var r in room.Rects)
                {
                    if (r.X2 < 0 || r.Y2 < 0) continue;
                    int cx1 = Math.Max(0, (int)r.X1) >> CellShift, cx2 = r.X2 >> CellShift;
                    int cy1 = Math.Max(0, (int)r.Y1) >> CellShift, cy2 = r.Y2 >> CellShift;
                    for (int cy = cy1; cy <= cy2; cy++)
                        for (int cx = cx1; cx <= cx2; cx++)
                        {
                            var cell = cellLists[cy * grid.Cols + cx] ??= [];
                            // Rooms arrive in list order; a second rect of the same
                            // room in one cell is always the last entry.
                            if (cell.Count == 0 || !ReferenceEquals(cell[^1], room))
                                cell.Add(room);
                        }
                }
            for (int c = 0; c < cellLists.Length; c++)
                grid.Cells[c] = cellLists[c]?.ToArray();
            grids[map] = grid;
        }
        return new RoomIndex(grids, version);
    }

    public Room? Find(Point3D pt)
    {
        if (pt.X < 0 || pt.Y < 0 || !_grids.TryGetValue(pt.Map, out var grid)) return null;
        int cx = pt.X >> CellShift, cy = pt.Y >> CellShift;
        if (cx >= grid.Cols || cy >= grid.Rows || grid.Cells[cy * grid.Cols + cx] is not { } cell) return null;
        foreach (var room in cell)
            if (room.Contains(pt))
                return room;
        return null;
    }
}
