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

    /// <summary>Host bridge: does a dynamic (in-world) item occlude the ray at
    /// this cell/height? (Source-X CanSeeLOS_New LOS_NB_DYNAMIC block — the MUL
    /// static path can't see live items, so the world supplies them.)</summary>
    public Func<byte, short, short, int, bool>? DynamicOccluderAt { get; set; }

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
    /// Check line of sight between two points (Source-X CanSeeLOS_New): a
    /// Bresenham ray blocked by terrain, statics, dynamic in-world items and
    /// multi/custom-house geometry. <paramref name="flags"/> selects LOS_FISHING
    /// (the ray must run over water past two tiles).
    /// </summary>
    public bool CanSeeLOS(Point3D from, Point3D to, Core.Enums.LosFlags flags = Core.Enums.LosFlags.None)
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

            // LOS_FISHING: two or more tiles out, the ray must stay over water.
            if ((flags & Core.Enums.LosFlags.Fishing) != 0 &&
                from.GetDistanceTo(new Point3D(checkX, checkY, (sbyte)0, from.Map)) >= 2 &&
                !IsWaterCell(from.Map, checkX, checkY))
                return false;
        }

        return true;
    }

    private bool IsWaterCell(byte mapId, short x, short y)
    {
        if (_mapData == null) return true;
        var land = _mapData.GetLandTileData(_mapData.GetTerrainTile(mapId, x, y).TileId);
        return land.IsWet;
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
            if (!GraphicBlocksLos(s.TileId, data))
                continue;

            int height = Math.Max(1, Math.Max(data.Height, data.CalcHeight));
            int bottomZ = s.Z;
            int topZ = bottomZ + height;
            if (rayZ >= bottomZ && rayZ <= topZ)
                return true;
        }

        // Dynamic in-world items + multi/custom-house geometry
        // (Source-X LOS_NB_DYNAMIC / LOS_NB_MULTI passes).
        if (DynamicOccluderAt?.Invoke(mapId, x, y, rayZ) == true)
            return true;

        return false;
    }

    /// <summary>Whether a tile graphic occludes the ray. Wall/impassable/roof/
    /// no-shoot statics block, as do items flagged CAN_I_BLOCKLOS_HEIGHT; windows
    /// stay see-through (Source-X UFLAG2_WINDOW under the LOS_NB_WINDOWS default
    /// used for archery/magery). Shared by the static, dynamic and multi paths.</summary>
    public static bool GraphicBlocksLos(ushort tileId, in ItemTileData data)
    {
        if ((data.Flags & TileFlag.Window) != 0)
            return false;
        if (data.IsWall || data.IsImpassable || data.IsRoof || (data.Flags & TileFlag.NoShoot) != 0)
            return true;
        var def = Definitions.DefinitionLoader.GetItemDef(tileId);
        return def != null && (def.Can & Core.Enums.CanFlags.I_BlockLOSHeight) != 0;
    }
}
