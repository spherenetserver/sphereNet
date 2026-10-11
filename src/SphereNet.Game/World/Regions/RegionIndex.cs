using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.World.Regions;

/// <summary>
/// Read-only spatial index for region lookups, the equivalent of the region links
/// Source-X keeps on every sector (CSectorBase::GetRegion walks m_RegionLinks, sorted
/// smallest first, and returns the first region that contains the point).
///
/// Each 64x64 cell holds the regions whose rects touch it, ordered by total area and
/// then by registration order, so the first one that contains a point is the one a
/// full scan would pick: the smallest, the earliest among equals. Ship regions move
/// on every step and are left out of the grid; they are checked live on each lookup.
///
/// An instance is never mutated after <see cref="Build"/>, so the parallel tick phases
/// read it without locks. Adding or removing a region builds a new one.
/// </summary>
internal sealed class RegionIndex
{
    private const int CellShift = 6; // 64 tiles, the Source-X sector size

    private readonly record struct Entry(Region Region, long Area, int Order);

    private sealed class MapGrid(int cols, int rows)
    {
        public readonly int Cols = cols;
        public readonly int Rows = rows;
        public readonly Entry[]?[] Cells = new Entry[]?[cols * rows];
    }

    private readonly Dictionary<byte, MapGrid> _grids;
    private readonly Entry[] _live;

    private RegionIndex(Dictionary<byte, MapGrid> grids, Entry[] live)
    {
        _grids = grids;
        _live = live;
    }

    public static RegionIndex Build(IReadOnlyList<Region> regions)
    {
        var live = new List<Entry>();
        var byMap = new Dictionary<byte, List<Entry>>();
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if ((region.Flags & RegionFlag.Ship) != 0)
            {
                live.Add(new Entry(region, 0, i));
                continue;
            }
            long area = region.TotalArea;
            if (area <= 0) continue;
            if (!byMap.TryGetValue(region.MapIndex, out var list))
                byMap[region.MapIndex] = list = [];
            list.Add(new Entry(region, area, i));
        }

        var grids = new Dictionary<byte, MapGrid>();
        foreach (var (map, entries) in byMap)
        {
            entries.Sort(static (a, b) => a.Area != b.Area ? a.Area.CompareTo(b.Area) : a.Order.CompareTo(b.Order));

            int maxX = 0, maxY = 0;
            foreach (var e in entries)
                foreach (var r in e.Region.Rects)
                {
                    maxX = Math.Max(maxX, r.X2);
                    maxY = Math.Max(maxY, r.Y2);
                }
            var grid = new MapGrid((maxX >> CellShift) + 1, (maxY >> CellShift) + 1);

            var cellLists = new List<Entry>?[grid.Cells.Length];
            foreach (var e in entries)
            {
                foreach (var r in e.Region.Rects)
                {
                    if (r.X2 < 0 || r.Y2 < 0) continue;
                    int cx1 = Math.Max(0, (int)r.X1) >> CellShift, cx2 = r.X2 >> CellShift;
                    int cy1 = Math.Max(0, (int)r.Y1) >> CellShift, cy2 = r.Y2 >> CellShift;
                    for (int cy = cy1; cy <= cy2; cy++)
                        for (int cx = cx1; cx <= cx2; cx++)
                        {
                            var cell = cellLists[cy * grid.Cols + cx] ??= [];
                            // Rects of one region can share a cell; entries arrive in
                            // sorted order, so a repeat is always the last one added.
                            if (cell.Count == 0 || !ReferenceEquals(cell[^1].Region, e.Region))
                                cell.Add(e);
                        }
                }
            }
            for (int c = 0; c < cellLists.Length; c++)
                grid.Cells[c] = cellLists[c]?.ToArray();
            grids[map] = grid;
        }

        return new RegionIndex(grids, live.ToArray());
    }

    public Region? Find(Point3D pt)
    {
        Region? best = null;
        long bestArea = long.MaxValue;
        int bestOrder = int.MaxValue;

        if (pt.X >= 0 && pt.Y >= 0 && _grids.TryGetValue(pt.Map, out var grid))
        {
            int cx = pt.X >> CellShift, cy = pt.Y >> CellShift;
            if (cx < grid.Cols && cy < grid.Rows && grid.Cells[cy * grid.Cols + cx] is { } cell)
            {
                foreach (var e in cell)
                {
                    if (!e.Region.Contains(pt)) continue;
                    best = e.Region;
                    bestArea = e.Area;
                    bestOrder = e.Order;
                    break;
                }
            }
        }

        foreach (var e in _live)
        {
            if (!e.Region.Contains(pt)) continue;
            long area = e.Region.TotalArea;
            if (area <= 0) continue;
            if (area < bestArea || (area == bestArea && e.Order < bestOrder))
            {
                best = e.Region;
                bestArea = area;
                bestOrder = e.Order;
            }
        }
        return best;
    }
}
