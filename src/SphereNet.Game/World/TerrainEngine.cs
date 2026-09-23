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

    // =====================================================================
    // Legacy line of sight - Source-X CChar::CanSeeLOS with ADVANCEDLOS
    // disabled (CCharLOS.cpp:12-96), the reference default (m_iAdvancedLos =
    // ADVANCEDLOS_DISABLED, CServerConfig.cpp:243).
    //
    // It does not cast a ray at eye height. It WALKS from the viewer toward the
    // target one tile at a time, at the walker's own feet, and asks every tile
    // the walk-height question (CWorldMap::GetHeightPoint2): whatever the walker
    // would stand on - or bump into - in the lower half of its body decides. A
    // blocking tile (CAN_I_BLOCK) or a closed door (CAN_I_DOOR) there stops the
    // sight, and so does a height change of more than PLAYER_HEIGHT. The eye-
    // height ray passed clean over an 11-high stable fence or a closed wooden
    // gate (top at z+11, ray at z+16), so a fighter swung at a creature on the
    // far side of one.
    //
    // A diagonal step is tested through its two orthogonal neighbours first: it
    // is blocked only when BOTH are blocked (:44-76). The target's own tile is
    // never tested, and the verdict fails outright on a 20+ Z gap (:93).
    // =====================================================================

    /// <summary>Source-X PLAYER_HEIGHT.</summary>
    internal const int LegacyPlayerHeight = 16;

    /// <summary>Host bridge for the legacy walk-height probe: feed every in-world
    /// item and multi component at (map, x, y) into the blocking state
    /// (the dynamic + fHouseCheck passes of CWorldMap::GetHeightPoint2).</summary>
    public Action<byte, short, short, LegacyBlockingState>? LegacyDynamicTiles { get; set; }

    /// <summary>Source-X CAN_I_* movement flags, as the walk-height probe uses them.</summary>
    [Flags]
    public enum LegacyCan : uint
    {
        None = 0,
        Door = 0x0001,
        Water = 0x0002,
        Platform = 0x0004,
        Block = 0x0008,
        Climb = 0x0010,
        Fire = 0x0020,
        Roof = 0x0040,
        Hover = 0x0080,
    }

    /// <summary>Port of CServerMapBlockingState (CServerMap.cpp:70-176): the
    /// surface under the walker (Bottom), the lowest tile (Lowest) and the one
    /// overhead (Top) for a walker at <see cref="Z"/> with the given passable
    /// flags.</summary>
    public sealed class LegacyBlockingState
    {
        public const int SizeZ = 127, SizeMinZ = -127;

        public LegacyCan CanPass { get; }
        public int Z { get; }
        public int Height { get; }

        public LegacyCan TopFlags = LegacyCan.None; public int TopZ = SizeZ; public bool TopIsItem;
        public LegacyCan BottomFlags = LegacyCan.Block; public int BottomZ = SizeMinZ;
        public LegacyCan LowestFlags = LegacyCan.Block; public int LowestZ = SizeZ;

        public LegacyBlockingState(LegacyCan canPass, int z, int height)
        {
            CanPass = canPass;
            Z = z;
            Height = height;
        }

        /// <summary>CServerMapBlockingState::CheckTile.</summary>
        public void CheckTile(LegacyCan flags, int zBottom, int zHeight, bool isItem)
        {
            int zTop = (flags & LegacyCan.Climb) != 0
                ? Math.Min(zBottom + zHeight / 2, SizeZ)
                : Math.Min(zBottom + zHeight, SizeZ);

            if (zTop < BottomZ)
                return;     // below something I can already step on

            // CAN_C_HOVER is never part of the LOS walker's abilities.
            if ((flags & LegacyCan.Hover) != 0 && (CanPass & LegacyCan.Hover) == 0)
                flags &= ~LegacyCan.Hover;
            if (flags == LegacyCan.None)
                return;

            if ((flags & ~CanPass) == 0)
            {
                // Does not block me: a platform is stood on, anything else walked under.
                if ((flags & LegacyCan.Platform) != 0)
                    zBottom = zTop;
                else if ((flags & LegacyCan.Climb) == 0)
                    zTop = zBottom;
            }

            if (zTop < LowestZ)
            {
                LowestFlags = flags;
                LowestZ = zTop;
            }

            int reach = (Height + ((flags & (LegacyCan.Climb | LegacyCan.Platform)) != 0 ? zHeight : 0)) / 2;
            if (zBottom < Z + reach)
            {
                // The new tile under me.
                if (zTop >= BottomZ)
                {
                    if (zTop == BottomZ)
                    {
                        if ((BottomFlags & LegacyCan.Platform) != 0)
                            return;
                        if ((BottomFlags & LegacyCan.Water) != 0 && (flags & LegacyCan.Platform) == 0)
                            return;
                    }
                    BottomFlags = flags;
                    BottomZ = zTop;
                }
            }
            else if (zBottom <= TopZ)
            {
                // Something I could fit under - it is over my head.
                TopFlags = flags;
                TopZ = zTop;
                TopIsItem = isItem;
            }
        }
    }

    /// <summary>CItemBase::GetItemHeightFlags (CItemBase.cpp:766): the walk flags
    /// and effective height of a static/multi tile from its tiledata.</summary>
    public static int GetLegacyTileHeightFlags(in ItemTileData data, out LegacyCan flags)
    {
        var f = data.Flags;
        if ((f & TileFlag.Door) != 0)
        {
            flags = LegacyCan.Door;
            return data.Height;
        }

        if ((f & TileFlag.Impassable) != 0)
        {
            if ((f & TileFlag.Wet) != 0)
            {
                flags = LegacyCan.Water;
                return data.Height;
            }
            flags = LegacyCan.Block;
        }
        else
        {
            flags = LegacyCan.None;
            if ((f & (TileFlag.Surface | TileFlag.Roof | TileFlag.HoverOver)) == 0)
                return 0;   // no effective height if it doesn't block
        }

        if ((f & TileFlag.Roof) != 0)
            flags |= LegacyCan.Roof;
        else if ((f & TileFlag.Surface) != 0)
            flags |= LegacyCan.Platform;
        if ((f & TileFlag.Bridge) != 0)
            flags |= LegacyCan.Climb;
        if ((f & TileFlag.HoverOver) != 0)
            flags |= LegacyCan.Hover;
        return data.Height;
    }

    /// <summary>CItemBase::GetItemTiledataFlags (CItemBase.cpp:737): the union of
    /// walk flags an ITEMDEF inherits from its tiledata.</summary>
    public static LegacyCan GetLegacyTiledataFlags(in ItemTileData data)
    {
        var f = data.Flags;
        LegacyCan can = LegacyCan.None;
        if ((f & TileFlag.Door) != 0) can |= LegacyCan.Door;
        if ((f & TileFlag.Wet) != 0) can |= LegacyCan.Water;
        if ((f & TileFlag.Surface) != 0) can |= LegacyCan.Platform;
        if ((f & TileFlag.Impassable) != 0) can |= LegacyCan.Block;
        if ((f & TileFlag.Bridge) != 0) can |= LegacyCan.Climb;
        if ((f & TileFlag.Damaging) != 0) can |= LegacyCan.Fire;
        if ((f & TileFlag.Roof) != 0) can |= LegacyCan.Roof;
        if ((f & TileFlag.HoverOver) != 0) can |= LegacyCan.Hover;
        return can;
    }

    /// <summary>CWorldMap::GetHeightPoint2 with fHouseCheck (CWorldMap.cpp:1615-1839)
    /// for the LOS walker (CAN_C_SWIM|CAN_C_WALK|CAN_C_FLY). Returns the walker's
    /// new Z and the blocking flags at the point.</summary>
    public int GetLegacyHeightPoint(byte mapId, short x, short y, int z, out LegacyCan blockFlags)
    {
        const LegacyCan canPass = LegacyCan.Water | LegacyCan.Platform | LegacyCan.Climb;
        var block = new LegacyBlockingState(canPass, z, LegacyPlayerHeight);

        var staticBlock = _mapData!.GetStaticBlock(mapId, x, y, out int offX, out int offY);
        foreach (var s in staticBlock)
        {
            if (s.XOffset != offX || s.YOffset != offY)
                continue;
            var data = _mapData.GetItemTileData(s.TileId);
            int h = GetLegacyTileHeightFlags(data, out var f);
            // A map-static door the world has swung open is no door at the moment.
            if ((f & LegacyCan.Door) != 0 && StaticDoorOpen?.Invoke(mapId, x, y, s.Z) == true)
                f = LegacyCan.None;
            block.CheckTile(f, s.Z, h, isItem: true);
        }

        LegacyDynamicTiles?.Invoke(mapId, x, y, block);

        // Terrain (CWorldMap.cpp:1735-1758): the raw corner Z, height 0.
        var cell = _mapData.GetTerrainTile(mapId, x, y);
        LegacyCan land;
        if (cell.TileId == 0x0002)          // TERRAIN_HOLE
            land = LegacyCan.None;
        else if (cell.TileId == 0x0244)     // TERRAIN_NULL
            land = LegacyCan.Block;
        else
        {
            var lf = _mapData.GetLandTileData(cell.TileId).Flags;
            if ((lf & TileFlag.Surface) != 0) land = LegacyCan.Platform;
            else if ((lf & TileFlag.Wet) != 0) land = LegacyCan.Water;
            else if ((lf & TileFlag.Damaging) != 0) land = LegacyCan.Fire;
            else if ((lf & TileFlag.Impassable) != 0) land = LegacyCan.Block;
            else land = LegacyCan.Platform;
        }
        block.CheckTile(land, cell.Z, 0, isItem: false);

        if (block.BottomZ == LegacyBlockingState.SizeMinZ)
        {
            block.BottomFlags = block.LowestFlags;
            block.BottomZ = block.LowestZ;
        }

        blockFlags = block.BottomFlags;
        if (block.TopFlags != LegacyCan.None)
        {
            blockFlags |= LegacyCan.Roof;
            // Something over my head that is too low to fit under blocks me.
            if (block.TopIsItem && (block.TopFlags & ~LegacyCan.Roof) != 0 &&
                block.TopZ < block.BottomZ + LegacyPlayerHeight)
                blockFlags |= LegacyCan.Block;
        }

        // No CAN_C_HOVER for the walker.
        blockFlags &= ~LegacyCan.Hover;

        if ((blockFlags & (LegacyCan.Climb | LegacyCan.Platform)) != 0)
        {
            blockFlags &= ~LegacyCan.Climb;
            blockFlags |= LegacyCan.Platform;
            return block.BottomZ;
        }
        return z; // CAN_C_FLY: gravity does not pull the LOS walker down
    }

    /// <summary>Host bridge: is the map-static door at this cell swung open?</summary>
    public Func<byte, short, short, sbyte, bool>? StaticDoorOpen { get; set; }

    /// <summary>Source-X CPointBase::GetDir (CPointBase.cpp:857): the 2D heading
    /// from <paramref name="from"/> toward (tx, ty) - cardinal when one axis is
    /// more than twice the other, diagonal otherwise.</summary>
    internal static int LegacyGetDir(int fx, int fy, int tx, int ty)
    {
        int dx = fx - tx, dy = fy - ty;
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        bool steep = ay > ax;
        bool extreme = steep ? ay > ax * 2 : ax > ay * 2;
        bool north = dy > 0, south = dy < 0, east = dx < 0, west = dx > 0;
        if (extreme)
            return steep ? (north ? 0 : 4) : (east ? 2 : 6);
        if (north) return east ? 1 : 7;
        return east ? 3 : 5;
    }

    private static readonly (int Dx, int Dy)[] LegacyMoves =
        [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    private bool LegacyStepBlocked(byte mapId, int x, int y, int z, out int newZ)
    {
        newZ = GetLegacyHeightPoint(mapId, (short)x, (short)y, z, out var flags);
        return Math.Abs(newZ - z) > LegacyPlayerHeight ||
               (flags & (LegacyCan.Block | LegacyCan.Door)) != 0;
    }

    /// <summary>Source-X CChar::CanSeeLOS, legacy method (CCharLOS.cpp:12-96).
    /// <paramref name="from"/> and <paramref name="to"/> are the two feet
    /// positions (GetTopPoint), not eye heights.</summary>
    public bool CanSeeLOSLegacy(Point3D from, Point3D to, int maxDist = int.MaxValue)
    {
        if (_mapData == null) return true;
        if (from.Map != to.Map) return false;

        int sx = from.X, sy = from.Y, sz = from.Z;
        int dist = Math.Max(Math.Abs(to.X - sx), Math.Abs(to.Y - sy));
        if (dist > maxDist)
            return false;

        int distTry = 0;
        while (--dist >= 0)
        {
            int dir = LegacyGetDir(sx, sy, to.X, to.Y);
            if (sx == to.X && sy == to.Y)
                break; // GetDir falls back to its default; nothing left to walk
            if ((dir & 1) != 0)
            {
                // A diagonal passes if either orthogonal neighbour is open.
                var (d1x, d1y) = LegacyMoves[dir - 1];
                if (LegacyStepBlocked(from.Map, sx + d1x, sy + d1y, sz, out _))
                {
                    var (d2x, d2y) = LegacyMoves[(dir + 1) & 7];
                    if (LegacyStepBlocked(from.Map, sx + d2x, sy + d2y, sz, out _))
                        return false;
                }
            }

            if (dist != 0)
            {
                var (mx, my) = LegacyMoves[dir];
                sx += mx;
                sy += my;
                if (LegacyStepBlocked(from.Map, sx, sy, sz, out int z) || distTry > maxDist)
                    return false;
                sz = z;
                ++distTry;
            }
        }

        return Math.Abs(sz - to.Z) < 20;
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
