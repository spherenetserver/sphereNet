using System.Text.Json;
using SphereNet.Core.Types;
using SphereNet.MapData;

namespace SphereNet.Game.PlayerBots;

/// <summary>A named node of the road network a server-driven player walks.</summary>
public sealed class BotWaypoint
{
    public required string Name { get; init; }
    public Point3D Pos { get; set; }
    public int ArrivalRange { get; init; }
    public List<int> Links { get; } = [];
    /// <summary>Which connected piece of the road network the node is on. Islands
    /// and most dungeon levels are pieces of their own, reached by magic, not on foot.</summary>
    public int Component { get; set; } = -1;
}

/// <summary>A place worth walking to (a bank, a shop, a healer), with the spots a
/// visitor actually stops on.</summary>
public sealed class BotDestination
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string City { get; init; }
    public Point3D Pos { get; set; }
    public int NearestWaypoint { get; init; } = -1;
    public List<Point3D> Arrivals { get; } = [];
    /// <summary>For each arrival, the road nodes it is walked to and from (its door
    /// side): the route into a shop ends at one of them and the route out starts
    /// there, rather than at whichever node is nearest through a wall.</summary>
    public List<List<int>> ArrivalLinks { get; } = [];
    /// <summary>Arrival spots a bot could not walk out of or into this run (a shop
    /// whose door is locked in this world, say). Learned at a rescue; not chosen again.</summary>
    public HashSet<int> BadArrivals { get; } = [];
    /// <summary>Rescues seen per spot not yet marked bad: one can be bad luck (a doorway
    /// full of other bots), a second makes it a fact.</summary>
    public Dictionary<int, int> SpotFailures { get; } = [];

    /// <summary>A stopping spot not known to be bad, or -1 when there is none.</summary>
    public int PickArrival(Random rng)
    {
        if (Arrivals.Count == 0) return -1;
        int good = Arrivals.Count - BadArrivals.Count;
        if (good <= 0) return -1;
        int k = rng.Next(good);
        for (int i = 0; i < Arrivals.Count; i++)
            if (!BadArrivals.Contains(i) && k-- == 0) return i;
        return -1;
    }

    /// <summary>The arrival spot <paramref name="pos"/> stands on (within 2 tiles), or -1.</summary>
    public int ArrivalAt(Point3D pos)
    {
        for (int i = 0; i < Arrivals.Count; i++)
            if (Arrivals[i].GetDistanceTo(pos) <= 2 && Math.Abs(Arrivals[i].Z - pos.Z) <= 8) return i;
        return -1;
    }
}

/// <summary>
/// The road network and destination catalogue. Read from JSON (waypoints.json,
/// destinations.json) whose coordinates are classic map 0 tiles; every Z is
/// re-seated on the standable surface the local map data actually has there, so a
/// node recorded at a placeholder height (many dungeon nodes are written at Z 0)
/// lands on the real floor. A node with no standable surface is dropped.
/// </summary>
public sealed class BotNavGraph
{
    private const int CellSize = 32;
    private readonly List<BotWaypoint> _waypoints = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<int>> _cells = [];
    private readonly List<BotDestination> _destinations = [];

    public IReadOnlyList<BotWaypoint> Waypoints => _waypoints;
    public IReadOnlyList<BotDestination> Destinations => _destinations;
    public int DroppedNodes { get; private set; }
    public int ReseatedNodes { get; private set; }

    /// <summary>Loads nav/waypoints.json and nav/destinations.json under
    /// <paramref name="dataDir"/>. Missing files leave the graph empty.</summary>
    public static BotNavGraph Load(string dataDir, MapDataManager? map)
    {
        var g = new BotNavGraph();
        string wpPath = Path.Combine(dataDir, "nav", "waypoints.json");
        if (File.Exists(wpPath))
            g.LoadWaypoints(File.ReadAllText(wpPath), map);
        string destPath = Path.Combine(dataDir, "nav", "destinations.json");
        if (File.Exists(destPath))
            g.LoadDestinations(File.ReadAllText(destPath), map);
        return g;
    }

    public void LoadWaypoints(string json, MapDataManager? map)
    {
        using var doc = JsonDocument.Parse(json);
        var links = new List<string[]>();
        foreach (var e in doc.RootElement.GetProperty("Waypoints").EnumerateArray())
        {
            string name = e.GetProperty("Name").GetString() ?? "";
            if (name.Length == 0 || _byName.ContainsKey(name)) continue;
            var raw = new Point3D((short)e.GetProperty("X").GetInt32(), (short)e.GetProperty("Y").GetInt32(),
                (sbyte)Math.Clamp(e.GetProperty("Z").GetInt32(), -128, 127), 0);
            if (!TrySeat(map, raw, out var seated))
            {
                DroppedNodes++;
                continue;
            }
            if (seated.Z != raw.Z) ReseatedNodes++;
            int range = e.TryGetProperty("ArrivalRange", out var ar) ? ar.GetInt32() : 0;
            var wp = new BotWaypoint { Name = name, Pos = seated, ArrivalRange = Math.Max(1, range > 0 ? Math.Min(range, 4) : 1) };
            _byName[name] = _waypoints.Count;
            _waypoints.Add(wp);
            links.Add(e.TryGetProperty("Connects", out var c)
                ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : []);
        }
        // Edges are bidirectional, as the source data intends.
        for (int i = 0; i < _waypoints.Count; i++)
            foreach (string other in links[i])
                if (_byName.TryGetValue(other, out int j) && j != i)
                {
                    if (!_waypoints[i].Links.Contains(j)) _waypoints[i].Links.Add(j);
                    if (!_waypoints[j].Links.Contains(i)) _waypoints[j].Links.Add(i);
                }
        LabelComponents();
        for (int i = 0; i < _waypoints.Count; i++)
        {
            var p = _waypoints[i].Pos;
            long key = CellKey(p.X, p.Y);
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
            list.Add(i);
        }
    }

    public void LoadDestinations(string json, MapDataManager? map)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.GetProperty("Destinations").EnumerateArray())
        {
            string name = e.GetProperty("Name").GetString() ?? "";
            string nearest = e.TryGetProperty("NearestWaypoint", out var nw) ? nw.GetString() ?? "" : "";
            if (!_byName.TryGetValue(nearest, out int wpIndex)) continue;
            var raw = new Point3D((short)e.GetProperty("X").GetInt32(), (short)e.GetProperty("Y").GetInt32(),
                (sbyte)Math.Clamp(e.GetProperty("Z").GetInt32(), -128, 127), 0);
            var dest = new BotDestination
            {
                Name = name,
                Type = e.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "",
                City = e.TryGetProperty("City", out var c) ? c.GetString() ?? "" : "",
                Pos = TrySeatNear(map, raw, out var seated) ? seated : raw,
                NearestWaypoint = wpIndex,
            };
            if (e.TryGetProperty("Arrivals", out var arrivals))
                foreach (var a in arrivals.EnumerateArray())
                {
                    var ap = new Point3D((short)a.GetProperty("X").GetInt32(), (short)a.GetProperty("Y").GetInt32(),
                        (sbyte)Math.Clamp(a.GetProperty("Z").GetInt32(), -128, 127), 0);
                    if (!TrySeatNear(map, ap, out var seatedArrival)) continue;
                    dest.Arrivals.Add(seatedArrival);
                    var links = new List<int>();
                    if (a.TryGetProperty("Waypoints", out var aw))
                        foreach (var n in aw.EnumerateArray())
                            if (_byName.TryGetValue(n.GetString() ?? "", out int wi)) links.Add(wi);
                    dest.ArrivalLinks.Add(links);
                }
            _destinations.Add(dest);
        }
    }

    public int ComponentCount { get; private set; }

    private void LabelComponents()
    {
        foreach (var w in _waypoints) w.Component = -1;
        int next = 0;
        var queue = new Queue<int>();
        for (int i = 0; i < _waypoints.Count; i++)
        {
            if (_waypoints[i].Component >= 0) continue;
            _waypoints[i].Component = next;
            queue.Enqueue(i);
            while (queue.Count > 0)
                foreach (int n in _waypoints[queue.Dequeue()].Links)
                    if (_waypoints[n].Component < 0)
                    {
                        _waypoints[n].Component = next;
                        queue.Enqueue(n);
                    }
            next++;
        }
        ComponentCount = next;
    }

    /// <summary>The road-network piece a destination hangs off.</summary>
    public int ComponentOf(BotDestination d) =>
        d.NearestWaypoint >= 0 ? _waypoints[d.NearestWaypoint].Component : -1;

    public int FindWaypoint(string name) => _byName.TryGetValue(name, out int i) ? i : -1;

    public BotDestination? FindDestination(string name) =>
        _destinations.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The closest node within <paramref name="maxDist"/> tiles that is on
    /// the same floor band (|dz| &lt;= 16), or -1.</summary>
    public int FindNearest(Point3D p, int maxDist) => FindNearest(p, maxDist, null);

    /// <summary>As <see cref="FindNearest(Point3D, int)"/>, skipping the nodes in
    /// <paramref name="exclude"/>.</summary>
    public int FindNearest(Point3D p, int maxDist, ICollection<int>? exclude)
    {
        int best = -1, bestDist = int.MaxValue;
        int r = maxDist / CellSize + 1;
        int cx = p.X / CellSize, cy = p.Y / CellSize;
        for (int x = cx - r; x <= cx + r; x++)
        for (int y = cy - r; y <= cy + r; y++)
        {
            if (!_cells.TryGetValue(((long)x << 32) | (uint)y, out var list)) continue;
            foreach (int i in list)
            {
                if (exclude != null && exclude.Contains(i)) continue;
                var w = _waypoints[i].Pos;
                if (Math.Abs(w.Z - p.Z) > 16) continue;
                int d = Math.Max(Math.Abs(w.X - p.X), Math.Abs(w.Y - p.Y));
                if (d <= maxDist && d < bestDist) { bestDist = d; best = i; }
            }
        }
        return best;
    }

    /// <summary>A* over the waypoint graph. Returns the node indices from
    /// <paramref name="from"/> to <paramref name="to"/> inclusive, or null.</summary>
    public List<int>? Route(int from, int to, int maxExpanded = 20000)
    {
        if (from < 0 || to < 0) return null;
        if (from == to) return [from];
        var open = new PriorityQueue<int, int>();
        var g = new Dictionary<int, int> { [from] = 0 };
        var came = new Dictionary<int, int>();
        var goal = _waypoints[to].Pos;
        open.Enqueue(from, Heuristic(_waypoints[from].Pos, goal));
        int expanded = 0;
        while (open.TryDequeue(out int cur, out _))
        {
            if (cur == to)
            {
                var path = new List<int> { cur };
                while (came.TryGetValue(cur, out int prev)) { cur = prev; path.Add(cur); }
                path.Reverse();
                return path;
            }
            if (++expanded > maxExpanded) return null;
            int gc = g[cur];
            foreach (int n in _waypoints[cur].Links)
            {
                int cost = gc + Heuristic(_waypoints[cur].Pos, _waypoints[n].Pos);
                if (g.TryGetValue(n, out int old) && old <= cost) continue;
                g[n] = cost;
                came[n] = cur;
                open.Enqueue(n, cost + Heuristic(_waypoints[n].Pos, goal));
            }
        }
        return null;
    }

    private static int Heuristic(Point3D a, Point3D b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) + Math.Abs(a.Z - b.Z) / 4;

    private static long CellKey(int x, int y) => ((long)(x / CellSize) << 32) | (uint)(y / CellSize);

    /// <summary><see cref="TrySeat"/> on the tile itself, else on the nearest tile within
    /// two that has a standable surface. A destination or stopping spot recorded on a
    /// wall line (the source data's map and this one disagree by a tile there) lands
    /// beside it instead of being kept somewhere no one can stand.</summary>
    internal static bool TrySeatNear(MapDataManager? map, Point3D raw, out Point3D seated)
    {
        if (TrySeat(map, raw, out seated)) return true;
        for (int r = 1; r <= 2; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var near = new Point3D((short)(raw.X + dx), (short)(raw.Y + dy), raw.Z, raw.Map);
                    if (TrySeat(map, near, out seated) && Math.Abs(seated.Z - raw.Z) <= 8) return true;
                }
        seated = raw;
        return false;
    }

    /// <summary>Seat <paramref name="raw"/> on the standable surface nearest its
    /// recorded Z: walkable land at the tile centre, or the top of a surface/bridge
    /// static, provided nothing impassable fills the body's space there. Without map
    /// data the point is taken as recorded.</summary>
    internal static bool TrySeat(MapDataManager? map, Point3D raw, out Point3D seated)
    {
        seated = raw;
        if (map == null) return true;
        var (w, h) = map.GetMapSize(raw.Map);
        if (raw.X < 0 || raw.Y < 0 || raw.X >= w || raw.Y >= h) return false;

        int bestZ = int.MinValue, bestDist = int.MaxValue;
        void Consider(int z)
        {
            if (!map.IsPassable(raw.Map, raw.X, raw.Y, z)) return;
            int d = Math.Abs(z - raw.Z);
            if (d < bestDist) { bestDist = d; bestZ = z; }
        }

        var land = map.GetTerrainTile(raw.Map, raw.X, raw.Y);
        var landData = map.GetLandTileData(land.TileId);
        if (!landData.IsWet && !landData.IsImpassable && !MapDataManager.IsLandIgnored(land.TileId))
        {
            map.GetAverageZ(raw.Map, raw.X, raw.Y, out _, out int avg, out _);
            Consider(avg);
        }
        foreach (var s in map.GetStatics(raw.Map, raw.X, raw.Y))
        {
            var d = map.GetItemTileData(s.TileId);
            if (d.IsSurface || d.IsBridge) Consider(s.Z + d.CalcHeight);
        }
        if (bestZ == int.MinValue) return false;
        seated = new Point3D(raw.X, raw.Y, (sbyte)Math.Clamp(bestZ, -128, 127), raw.Map);
        return true;
    }
}
