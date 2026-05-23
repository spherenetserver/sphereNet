using SphereNet.Core.Types;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.World;

/// <summary>
/// Terrain engine for height validation and line-of-sight checks.
/// Maps to CWorldMap::GetHeightPoint and CanSeeLOS in Source-X.
/// Wraps MapDataManager for movement and LOS validation.
/// </summary>
public sealed class TerrainEngine
{
    private readonly MapData.MapDataManager? _mapData;

    /// <summary>Max climb height per step (Source-X default).</summary>
    private const int MaxClimb = 18;

    /// <summary>Default character height for LOS checks.</summary>
    private const int PersonHeight = 16;

    public TerrainEngine(MapData.MapDataManager? mapData)
    {
        _mapData = mapData;
    }

    /// <summary>Get the ground Z height at a position.</summary>
    public sbyte GetGroundHeight(short x, short y, byte mapId)
    {
        if (_mapData == null) return 0;
        var cell = _mapData.GetTerrainTile(mapId, x, y);
        return cell.Z;
    }

    /// <summary>
    /// Get the effective standing Z including statics/bridges.
    /// </summary>
    public sbyte GetEffectiveZ(short x, short y, byte mapId, sbyte currentZ = 0)
    {
        if (_mapData == null) return 0;
        return _mapData.GetEffectiveZ(mapId, x, y, currentZ);
    }

    /// <summary>
    /// Validate that a move to the target position is physically possible.
    /// Checks terrain height difference and blocking statics.
    /// </summary>
    public bool CanMoveToPosition(Point3D from, Point3D to)
    {
        if (_mapData == null) return true;

        var target = _mapData.GetEffectiveZAndPassable(to.Map, to.X, to.Y, from.Z, from.Z);
        if (!target.Passable)
            return false;

        int heightDiff = Math.Abs(target.EffectiveZ - from.Z);
        if (heightDiff > MaxClimb)
            return false;

        return true;
    }

    /// <summary>
    /// Check line of sight between two points.
    /// Simplified Bresenham-based LOS with terrain + static obstruction.
    /// </summary>
    public bool CanSeeLOS(Point3D from, Point3D to)
    {
        if (_mapData == null) return true;
        if (from.Map != to.Map) return false;

        int dx = Math.Abs(to.X - from.X);
        int dy = Math.Abs(to.Y - from.Y);
        int steps = Math.Max(dx, dy);
        if (steps == 0) return true;

        float stepX = (float)(to.X - from.X) / steps;
        float stepY = (float)(to.Y - from.Y) / steps;

        // LOS height: eye level (Z + PersonHeight)
        float fromEye = from.Z + PersonHeight;
        float toEye = to.Z + PersonHeight;
        float stepZ = (toEye - fromEye) / steps;

        float cx = from.X, cy = from.Y, cz = fromEye;

        for (int i = 1; i < steps; i++)
        {
            cx += stepX;
            cy += stepY;
            cz += stepZ;

            short checkX = (short)Math.Round(cx);
            short checkY = (short)Math.Round(cy);
            int checkZ = (int)Math.Round(cz);

            if (HasLosOccluder(from.Map, checkX, checkY, checkZ))
                return false;
        }

        return true;
    }

    private bool HasLosOccluder(byte mapId, short x, short y, int rayZ)
    {
        if (_mapData == null)
            return false;

        var terrain = _mapData.GetTerrainTile(mapId, x, y);
        if (terrain.Z > rayZ)
            return true;

        var staticBlock = _mapData.GetStaticBlock(mapId, x, y, out int offX, out int offY);
        foreach (var s in staticBlock)
        {
            if (s.XOffset != offX || s.YOffset != offY)
                continue;

            var data = _mapData.GetItemTileData(s.TileId);
            if (!BlocksLineOfSight(data))
                continue;

            int height = Math.Max(1, Math.Max(data.Height, data.CalcHeight));
            int bottomZ = s.Z;
            int topZ = bottomZ + height;
            if (rayZ >= bottomZ && rayZ <= topZ)
                return true;
        }

        return false;
    }

    private static bool BlocksLineOfSight(ItemTileData data) =>
        data.IsWall ||
        data.IsImpassable ||
        data.IsRoof ||
        (data.Flags & TileFlag.NoShoot) != 0;
}
