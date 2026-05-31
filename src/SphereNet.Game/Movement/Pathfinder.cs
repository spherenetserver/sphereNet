using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.Movement;

/// <summary>
/// A* pathfinding for NPC movement. Grid-based 3D pathfinding.
/// Maps to NPC_AI_PATH logic in Source-X.
/// </summary>
public sealed class Pathfinder
{
    private readonly World.GameWorld _world;
    // Sized for player-triggered .walk commands crossing half-continents.
    // NPC AI calls this every tick, so keep enough budget for long paths but
    // not unbounded — A* with open set + closed set is O(N log N) worst-case.
    private const int MaxNodes = 20000;
    private const int MaxPathLength = 256;
    private const int MaxClimb = 12;

    public Pathfinder(World.GameWorld world)
    {
        _world = world;
    }

    // A* scratch collections, pooled per worker thread. NPC BuildDecision runs
    // FindPath inside a Parallel.ForEach, so these MUST be thread-local: each
    // worker reuses (and clears) its own set instead of allocating a fresh
    // PriorityQueue + HashSet + two Dictionaries on every call. Before pooling,
    // these four collections (each growing to thousands of entries, with the
    // attendant resize churn) were the dominant per-tick allocation under combat.
    [ThreadStatic] private static PriorityQueue<PathNode, int>? _tlOpenSet;
    [ThreadStatic] private static HashSet<long>? _tlClosedSet;
    [ThreadStatic] private static Dictionary<long, PathNode>? _tlCameFrom;
    [ThreadStatic] private static Dictionary<long, int>? _tlGScore;

    /// <summary>
    /// Find a path from start to goal. Returns the next step direction,
    /// or null if no path found.
    /// </summary>
    public List<Point3D>? FindPath(Point3D start, Point3D goal, byte mapIndex, CanFlags canFlags = CanFlags.None, Objects.Characters.Character? self = null, int maxNodes = MaxNodes)
    {
        if (maxNodes <= 0) maxNodes = MaxNodes;
        if (IsGoalReached(start, goal))
            return [goal];

        if (start.GetDistanceTo(goal) <= 1 && Math.Abs(start.Z - goal.Z) <= MaxClimb)
            return [goal];

        var openSet = _tlOpenSet ??= new PriorityQueue<PathNode, int>();
        var closedSet = _tlClosedSet ??= new HashSet<long>();
        var cameFrom = _tlCameFrom ??= new Dictionary<long, PathNode>();
        var gScore = _tlGScore ??= new Dictionary<long, int>();
        openSet.Clear();
        closedSet.Clear();
        cameFrom.Clear();
        gScore.Clear();

        var startNode = new PathNode(start.X, start.Y, start.Z, 0, Heuristic(start, goal));
        openSet.Enqueue(startNode, startNode.F);
        gScore[PackKey(startNode.X, startNode.Y, startNode.Z)] = 0;

        int nodesExplored = 0;

        while (openSet.Count > 0 && nodesExplored < maxNodes)
        {
            var current = openSet.Dequeue();
            long currentKey = PackKey(current.X, current.Y, current.Z);

            if (IsGoalReached(current, goal))
                return ReconstructPath(cameFrom, current, mapIndex);

            if (!closedSet.Add(currentKey))
                continue;

            nodesExplored++;

            // Explore 8 neighbors
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    short nx = (short)(current.X + dx);
                    short ny = (short)(current.Y + dy);

                    // Each tile has its own surface Z (terrain vs. static floors,
                    // bridges, steps). Using current.Z for every neighbor makes
                    // pathfinding fail as soon as terrain height changes by one
                    // step. Resolve the effective Z per-tile using MapData.
                    sbyte nz = _world.MapData?.GetEffectiveZ(mapIndex, nx, ny, current.Z) ?? current.Z;
                    long neighborKey = PackKey(nx, ny, nz);

                    if (closedSet.Contains(neighborKey))
                        continue;

                    // Climb limit: avoid jumping onto rooftops / off cliffs.
                    if (Math.Abs(nz - current.Z) > MaxClimb)
                        continue;

                    var neighborPos = new Point3D(nx, ny, nz, mapIndex);
                    if (!IsWalkable(neighborPos, canFlags, self))
                        continue;

                    int moveCost = (dx != 0 && dy != 0) ? 14 : 10; // diagonal vs cardinal
                    int newG = current.G + moveCost;
                    int h = Heuristic(neighborPos, goal);

                    var neighbor = new PathNode(nx, ny, nz, newG, h);

                    if (gScore.TryGetValue(neighborKey, out int existingG) && newG >= existingG)
                        continue;

                    cameFrom[neighborKey] = current;
                    gScore[neighborKey] = newG;
                    openSet.Enqueue(neighbor, neighbor.F);
                }
            }
        }

        if (nodesExplored >= maxNodes)
            Objects.Characters.Character.Diagnostic?.Invoke(
                $"Pathfinder: node budget ({maxNodes}) exhausted from {start} to {goal}");

        return null;
    }

    private bool IsWalkable(Point3D pos, CanFlags canFlags = CanFlags.None, Objects.Characters.Character? self = null)
    {
        // Single-tile char/item blocker check, done allocation-free in GameWorld
        // (A* calls this per explored neighbour; the old range-query iterators
        // were the dominant pathfinding allocation).
        if (_world.IsPathTileBlockedByObject(pos, canFlags, self))
            return false;

        var mapData = _world.MapData;
        if (mapData != null)
        {
            if (!mapData.IsPassable(pos.Map, pos.X, pos.Y, pos.Z))
                return false;

            var terrain = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
            var landData = mapData.GetLandTileData(terrain.TileId);
            if (landData.IsWet && (canFlags & CanFlags.C_Swim) == 0)
                return false;
        }

        return true;
    }

    private static int Heuristic(Point3D a, Point3D b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        int dz = Math.Abs(a.Z - b.Z);
        return 10 * (dx + dy) + (14 - 20) * Math.Min(dx, dy) + dz; // octile distance + Z pressure
    }

    private static long PackKey(short x, short y, sbyte z) =>
        ((long)(ushort)x << 24) | ((long)(ushort)y << 8) | (byte)z;

    private static bool IsGoalReached(Point3D current, Point3D goal) =>
        current.X == goal.X && current.Y == goal.Y && current.Map == goal.Map && Math.Abs(current.Z - goal.Z) <= MaxClimb;

    private static bool IsGoalReached(PathNode current, Point3D goal) =>
        current.X == goal.X && current.Y == goal.Y && Math.Abs(current.Z - goal.Z) <= MaxClimb;

    private static List<Point3D> ReconstructPath(Dictionary<long, PathNode> cameFrom, PathNode current, byte mapIndex)
    {
        var path = new List<Point3D>();
        var node = current;
        long key = PackKey(node.X, node.Y, node.Z);

        while (cameFrom.ContainsKey(key))
        {
            path.Add(new Point3D(node.X, node.Y, node.Z, mapIndex));
            node = cameFrom[key];
            key = PackKey(node.X, node.Y, node.Z);
        }

        path.Reverse();
        if (path.Count > MaxPathLength)
            path.RemoveRange(MaxPathLength, path.Count - MaxPathLength);
        return path;
    }

    private readonly struct PathNode(short x, short y, sbyte z, int g, int h)
    {
        public short X { get; } = x;
        public short Y { get; } = y;
        public sbyte Z { get; } = z;
        public int G { get; } = g;
        public int H { get; } = h;
        public int F => G + H;
    }
}
