using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.World;

/// <summary>
/// The ADVANCEDLOS ray, Source-X CChar::CanSeeLOS_New (CCharLOS.cpp:112-656).
///
/// The ray starts at the viewer's eyes - its feet plus GetHeightMount(true) - and
/// runs in three dimensions to the target point's own Z, one unit step along the
/// normalised 3D direction; every distinct rounded (x, y, z) it passes is a path
/// point, the viewer's own tile first, the target point last. Each path point is
/// asked, in order, about terrain, statics, world items and multi components:
/// an element blocks when the ray's Z lies inside its [z, z+height] span, unless
/// the point is the target's tile and the target Z lies inside that same span
/// (the target is standing on/in it). An element whose definition has
/// CAN_I_BLOCKLOS blocks wherever its Z is.
///
/// Path points are produced and tested one by one, so a check allocates nothing.
/// </summary>
internal sealed class AdvancedLineOfSight
{
    /// <summary>UO_SIZE_Z (uofiles_macros.h:28).</summary>
    private const int SizeZ = 127;
    /// <summary>TERRAIN_HOLE (uofiles_enums.h:1099).</summary>
    private const int TerrainHole = 0x0002;
    /// <summary>CUOMapMeter::IsTerrainNull (CUOMapMeter.cpp:3).</summary>
    private const int TerrainNull = 0x0244;

    private const TileFlag OccluderTileFlags = TileFlag.Wall | TileFlag.Impassable | TileFlag.Surface;
    private const CanFlags FishingSolidCan = CanFlags.I_Door | CanFlags.I_Platform | CanFlags.I_Block |
        CanFlags.I_Climb | CanFlags.I_Fire | CanFlags.I_Roof | CanFlags.I_BlockLOS | CanFlags.I_BlockLOSHeight;
    private const LosFlags RegionFlags = LosFlags.NbLocalTerrain | LosFlags.NbLocalStatic |
        LosFlags.NbLocalDynamic | LosFlags.NbLocalMulti | LosFlags.NoOtherRegion;

    private readonly GameWorld _world;

    public AdvancedLineOfSight(GameWorld world) => _world = world;

    /// <summary>One ray. <paramref name="src"/> is the viewer's feet (GetTopPoint),
    /// <paramref name="eyeHeight"/> its GetHeightMount(true), <paramref name="dst"/>
    /// the target point as the caller adjusted it. <paramref name="viewer"/> may be
    /// null for a check made on no character's behalf; it only decides which
    /// invisible items it can see (CanSeeItem).</summary>
    public bool Check(Character? viewer, Point3D src, int eyeHeight, Point3D dst, int maxDist, LosFlags flags)
    {
        var md = _world.MapData;
        if (src.Map != dst.Map) return false;
        if (src == dst) return true; // same point (CCharLOS.cpp:129)

        int srcTopZ = src.Z;
        int sx = src.X, sy = src.Y;
        int sz = Math.Min(src.Z + eyeHeight, SizeZ);
        int dstX = dst.X, dstY = dst.Y, dstZ = dst.Z;

        int dx = dstX - sx, dy = dstY - sy, dz = dstZ - sz;
        float dist2d = MathF.Sqrt(dx * dx + dy * dy);
        float dist3d = dz != 0 ? MathF.Sqrt(dist2d * dist2d + dz * dz) : dist2d;
        if (Approx(dist2d) > maxDist)
            return false;
        if (md == null) return true; // no map files: nothing to look through

        float fx = dx / dist3d, fy = dy / dist3d, fz = dz / dist3d;
        float px = sx, py = sy, pz = sz;

        var ctx = new RayContext
        {
            Md = md,
            Viewer = viewer,
            Flags = flags,
            Map = src.Map,
            SrcX = sx, SrcY = sy, SrcEyeZ = sz, SrcTopZ = srcTopZ,
            DstX = dstX, DstY = dstY, DstZ = dstZ,
        };
        if ((flags & RegionFlags) != 0)
            ctx.SrcRegion = LosRegionAt(new Point3D((short)sx, (short)sy, (sbyte)0, src.Map));
        if ((flags & LosFlags.NcMulti) != 0)
            ctx.SrcMulti = MultiRegionAt(new Point3D((short)sx, (short)sy, (sbyte)0, src.Map));

        bool any = false;
        int lx = 0, ly = 0, lz = 0;
        while (Between(px, dstX, sx) && Between(py, dstY, sy) && Between(pz, dstZ, sz))
        {
            int ix = (int)Approx(px), iy = (int)Approx(py), iz = (int)Approx(pz);
            if (!any || ix != lx || iy != ly || iz != lz)
            {
                if (!PointClear(ref ctx, ix, iy, iz))
                    return false;
                any = true;
                lx = ix; ly = iy; lz = iz;
            }
            px += fx;
            py += fy;
            pz += fz;
        }

        if (!any)
            return false; // empty path (CCharLOS.cpp:199-203)
        if (lx != dstX || ly != dstY || lz != dstZ)
            return PointClear(ref ctx, dstX, dstY, dstZ); // the target point closes the path (:196-197)
        return true;
    }

    /// <summary>BETWEENPOINT (CCharLOS.cpp:99): inside the coordinate span, half a unit of slack.</summary>
    private static bool Between(float coord, int a, int b) =>
        coord > Math.Min(a, b) - 0.5 && coord < Math.Max(a, b) + 0.5;

    /// <summary>APPROX (CCharLOS.cpp:100): round half down.</summary>
    private static float Approx(float num) =>
        num - MathF.Floor(num) > 0.5f ? MathF.Ceiling(num) : MathF.Floor(num);

    private struct RayContext
    {
        public MapDataManager Md;
        public Character? Viewer;
        public LosFlags Flags;
        public byte Map;
        public int SrcX, SrcY, SrcEyeZ, SrcTopZ;
        public int DstX, DstY, DstZ;
        public object? SrcRegion;
        public Regions.Region? SrcMulti;
    }

    /// <summary>The body of the per-point loop (CCharLOS.cpp:231-648).</summary>
    private bool PointClear(ref RayContext c, int x, int y, int z)
    {
        var flags = c.Flags;
        var md = c.Md;
        var now = new Point3D((short)x, (short)y, (sbyte)Math.Clamp(z, sbyte.MinValue, sbyte.MaxValue), c.Map);

        bool sameRegion = false;
        if ((flags & RegionFlags) != 0)
        {
            var nowRegion = LosRegionAt(now);
            sameRegion = ReferenceEquals(nowRegion, c.SrcRegion);
            if ((flags & LosFlags.NoOtherRegion) != 0 && !sameRegion)
                return false;
        }
        if ((flags & LosFlags.NcMulti) != 0)
        {
            var multi = MultiRegionAt(now);
            if (multi != null && !ReferenceEquals(multi, c.SrcMulti))
                return false;
        }

        var (w, h) = md.GetMapSize(c.Map);
        if (x < 0 || y < 0 || x >= w || y >= h)
            return false; // no map block (:259-264)

        bool nullTerrain = false;
        int distFromSrc = Math.Max(Math.Abs(x - c.SrcX), Math.Abs(y - c.SrcY));
        bool fishingFar = (flags & LosFlags.Fishing) != 0 && distFromSrc >= 2;

        // ---- terrain (:266-331) ----
        if ((flags & LosFlags.NbTerrain) == 0 &&
            !((flags & LosFlags.NbLocalTerrain) != 0 && sameRegion))
        {
            var cell = md.GetTerrainTile(c.Map, x, y);
            int terrainId = cell.TileId;
            if (fishingFar)
            {
                var it = NaturalResourceTiles.TerrainItemType(terrainId);
                if (it != ItemType.Water && it != ItemType.Normal)
                    return false;
            }
            if (terrainId != TerrainHole && terrainId != 475 && (terrainId < 430 || terrainId > 437))
            {
                TerrainSpan(md, c.Map, x, y, cell.Z, out int minZ, out int maxZ);
                if (terrainId == TerrainNull)
                    nullTerrain = true;
                if (minZ <= z && maxZ >= z &&
                    (x != c.DstX || y != c.DstY || minZ > c.DstZ || maxZ < c.DstZ))
                    return false;
                if ((flags & LosFlags.NcWater) != 0 && md.GetLandTileData(terrainId).IsWet)
                    nullTerrain = true;
            }
        }

        // ---- statics (:333-421) ----
        if ((flags & LosFlags.NbStatic) == 0 &&
            !((flags & LosFlags.NbLocalStatic) != 0 && sameRegion))
        {
            var statics = md.GetStaticBlock(c.Map, x, y, out int offX, out int offY);
            for (int i = 0; i < statics.Length; i++)
            {
                var s = statics[i];
                if (s.XOffset != offX || s.YOffset != offY)
                    continue;
                // The "stacked items" exemption (:346) compares the static's in-block
                // offset with the target's world coordinates, as upstream does.
                if (s.XOffset == c.DstX && s.YOffset == c.DstY && s.Z >= c.SrcTopZ && s.Z <= c.SrcEyeZ)
                    continue;
                ResolveArt(md, s.TileId, out var g);
                nullTerrain = false;
                if (Occludes(in g, g.Can, g.Type, s.Z, x, y, z, fishingFar, ref c))
                    return false;
            }
        }

        // ---- world items (:426-527) ----
        if ((flags & LosFlags.NbDynamic) == 0 &&
            !((flags & LosFlags.NbLocalDynamic) != 0 && sameRegion))
        {
            var sector = _world.GetSector(now);
            if (sector != null)
            {
                var items = sector.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (i >= items.Count) continue;
                    var item = items[i];
                    if (item.IsDeleted || !item.IsOnGround || item.X != x || item.Y != y || item.MapIndex != c.Map)
                        continue;
                    // A multi's own item carries a multi index, not an art id; its
                    // components are the multi pass below.
                    if (Item.IsMultiItemType(item.ItemType))
                        continue;
                    if (!CanSeeItem(c.Viewer, item))
                        continue;
                    if (item.X == c.DstX && item.Y == c.DstY && item.Z >= c.SrcTopZ && item.Z <= c.SrcEyeZ)
                        continue; // stacked items (:443)
                    ResolveWorldItem(md, item, out var g, out CanFlags itemCan);
                    nullTerrain = false;
                    if (Occludes(in g, itemCan, item.ItemType, item.Z, x, y, z, fishingFar, ref c))
                        return false;
                }
            }
        }

        // ---- multi components (:532-641) ----
        if ((flags & LosFlags.NbMulti) == 0 &&
            !((flags & LosFlags.NbLocalMulti) != 0 && sameRegion))
        {
            var multis = _world.GroundMultis;
            for (int m = 0; m < multis.Count; m++)
            {
                var multi = multis[m];
                if (multi.IsDeleted || multi.IsEquipped || !multi.IsOnGround || multi.MapIndex != c.Map)
                    continue;
                if (Math.Abs(multi.X - x) > 32 || Math.Abs(multi.Y - y) > 32)
                    continue;
                var mdef = md.GetMulti(multi.BaseId);
                if (mdef != null)
                {
                    foreach (var comp in mdef.Components)
                    {
                        if (!comp.IsVisible || multi.X + comp.XOffset != x || multi.Y + comp.YOffset != y)
                            continue;
                        ResolveArt(md, comp.TileId, out var g);
                        nullTerrain = false;
                        if (Occludes(in g, g.Can, g.Type, multi.Z + comp.ZOffset, x, y, z, fishingFar, ref c))
                            return false;
                    }
                }
                if (multi.ItemType == ItemType.MultiCustom && Movement.WalkCheck.ResolveCustomDesign != null)
                {
                    foreach (var tile in Movement.WalkCheck.ResolveCustomDesign(multi))
                    {
                        // An invisible design tile is a materialized fixture: the real item occludes.
                        if (!tile.Visible || multi.X + tile.X != x || multi.Y + tile.Y != y)
                            continue;
                        ResolveArt(md, tile.TileId, out var g);
                        nullTerrain = false;
                        if (Occludes(in g, g.Can, g.Type, multi.Z + tile.Z, x, y, z, fishingFar, ref c))
                            return false;
                    }
                }
            }
        }

        return !nullTerrain; // (:643-647)
    }

    /// <summary>One static / world item / multi component against the ray point
    /// (CCharLOS.cpp:360-417, 457-523, 576-632).</summary>
    private static bool Occludes(in ArtGeometry g, CanFlags can, ItemType type, int baseZ,
        int x, int y, int rayZ, bool fishingFar, ref RayContext c)
    {
        if ((can & CanFlags.I_BlockLOS) != 0)
            return true;
        if (fishingFar && type != ItemType.Water && (can & FishingSolidCan) != 0)
            return true;

        int height = (g.TFlags & TileFlag.Bridge) != 0 ? g.Height / 2 : g.Height;
        if (((g.TFlags & OccluderTileFlags) == 0 && (can & CanFlags.I_BlockLOSHeight) == 0) ||
            ((g.TFlags & TileFlag.Window) != 0 && (c.Flags & LosFlags.NbWindows) != 0))
            return false;

        int minZ = baseZ;
        int maxZ = Math.Min(height + minZ, SizeZ);
        if (minZ > rayZ || maxZ < rayZ)
            return false;
        return x != c.DstX || y != c.DstY || minZ > c.DstZ || maxZ < c.DstZ;
    }

    /// <summary>Terrain span at a path point (CCharLOS.cpp:297-310): from the lowest
    /// corner among the point and its neighbours inside the same 8x8 map block up to
    /// the point's own corner Z.</summary>
    private static void TerrainSpan(MapDataManager md, byte map, int x, int y, int ownZ, out int minZ, out int maxZ)
    {
        int ox = x & 7, oy = y & 7;
        int bx = x - ox, by = y - oy;
        int startX = ox > 1 ? ox - 1 : 0;
        int startY = oy > 1 ? oy - 1 : 0;
        minZ = md.GetTerrainTile(map, bx + startX, by + startY).Z;
        maxZ = ownZ;
        for (int px = startX; Math.Abs(ox - px) <= 1 && px <= 7; px++)
        for (int py = startY; Math.Abs(oy - py) <= 1 && py <= 7; py++)
        {
            int tz = md.GetTerrainTile(map, bx + px, by + py).Z;
            if (tz < minZ) minZ = tz;
        }
    }

    // =====================================================================
    // Occluder geometry: what an art id stands for (CCharLOS.cpp:349-398,
    // 446-504, 565-614). A definition's CAN, TYPE, tiledata flags and HEIGHT;
    // a DUPEITEM art keeps its master's CAN and TYPE but its own tiledata flags
    // and height (CItemBaseDupe, CItemBase.cpp:1801-1838).
    //
    // Upstream skips an art id no ITEMDEF names at all (FindItemBase returns
    // nullptr, :354). Script packs name every art id, so here such an id is read
    // as the definition upstream would build for it: its tiledata alone.
    // =====================================================================

    private struct ArtGeometry
    {
        public TileFlag TFlags;
        public int Height;
        public CanFlags Can;
        public ItemType Type;
    }

    private static void ResolveArt(MapDataManager md, int artId, out ArtGeometry g)
    {
        var def = DefinitionLoader.GetItemDef(artId);
        var data = md.GetItemTileData(artId);
        if (def != null && def.DupItemId != 0 && def.DupItemId != artId)
        {
            var master = DefinitionLoader.GetItemDef(def.DupItemId) ?? def;
            g.Can = DefinitionCan(md, master, def.DupItemId);
            g.Type = master.Type;
            DupeTileGeometry(artId, in data, out g.TFlags, out g.Height);
            return;
        }
        g.Can = DefinitionCan(md, def, artId);
        g.Type = def != null ? def.Type : (data.IsWet ? ItemType.Water : ItemType.Normal);
        DefinitionTileGeometry(def, in data, out g.TFlags, out g.Height);
    }

    /// <summary>A world item: CAN from its own definition XOR its CanMask, TFLAGS and
    /// HEIGHT from that definition, or from the art it is displayed as when its
    /// DISPID differs (a dupe art's own tiledata, else that art's definition).</summary>
    private static void ResolveWorldItem(MapDataManager md, Item item, out ArtGeometry g, out CanFlags itemCan)
    {
        int defIndex = ItemDefHelper.ResolveInstanceDefIndex(item);
        var def = DefinitionLoader.GetItemDef(defIndex);
        int defArt = def != null ? ItemDefHelper.CreateGraphic(def, defIndex) : item.BaseId;
        if (defArt == 0) defArt = item.BaseId;
        // An item made from a DUPEITEM art has the master as its definition
        // (FindItemBase hands back the master, CItemBase.cpp:2254-2256).
        if (def != null && def.DupItemId != 0 && def.DupItemId != defArt)
        {
            defArt = def.DupItemId;
            def = DefinitionLoader.GetItemDef(defArt) ?? def;
        }
        int disp = item.DispIdFull;

        var can = DefinitionCan(md, def, defArt);
        itemCan = (CanFlags)((ulong)can ^ item.CanMask);
        g.Can = itemCan;
        g.Type = item.ItemType;

        if (disp == defArt)
        {
            var data = md.GetItemTileData(defArt);
            DefinitionTileGeometry(def, in data, out g.TFlags, out g.Height);
            return;
        }

        var dispData = md.GetItemTileData(disp);
        var dispDef = DefinitionLoader.GetItemDef(disp);
        if (dispDef != null && dispDef.DupItemId != 0 && dispDef.DupItemId != disp)
            DupeTileGeometry(disp, in dispData, out g.TFlags, out g.Height);
        else
            DefinitionTileGeometry(dispDef, in dispData, out g.TFlags, out g.Height);
    }

    /// <summary>CItemBase m_qwFlags / m_Height: the tiledata flags (TFLAGS= replaces
    /// them) and GetItemHeightFlags' height (HEIGHT= replaces it).</summary>
    private static void DefinitionTileGeometry(ItemDef? def, in ItemTileData data, out TileFlag tflags, out int height)
    {
        tflags = def != null && def.TFlags != 0 ? (TileFlag)def.TFlags : data.Flags;
        height = def != null && def.Height != 0 ? def.Height : TerrainEngine.GetLegacyTileHeightFlags(data, out _);
    }

    /// <summary>CItemBaseDupe: the dupe art's own tiledata flags and GetItemHeightFlags
    /// height; a chair has none (CItemBase.cpp:1831-1836).</summary>
    private static void DupeTileGeometry(int artId, in ItemTileData data, out TileFlag tflags, out int height)
    {
        tflags = data.Flags;
        height = Movement.SourceXWalk.ChairIds.Contains(artId) ? 0 : TerrainEngine.GetLegacyTileHeightFlags(data, out _);
    }

    /// <summary>CItemBase m_Can: GetItemHeightFlags plus GetItemSpecificFlags' door
    /// rule (CItemBase.cpp:98-101, 719-728); a script CAN= replaces it all
    /// (CBase.cpp:363), while a single flag key (BLOCKLOS=1 and the like) only adds
    /// its bit to what the tiledata gave.</summary>
    private static CanFlags DefinitionCan(MapDataManager md, ItemDef? def, int artId)
    {
        if (def != null && def.HasCanKey)
            return def.Can;
        var data = md.GetItemTileData(artId);
        TerrainEngine.GetLegacyTileHeightFlags(data, out var legacy);
        var can = (CanFlags)(uint)legacy | (def?.Can ?? CanFlags.None);
        if (def != null && def.Type == ItemType.Door)
        {
            can &= ~CanFlags.I_Block;
            int doorDir = DoorHelper.GetDoorDir((ushort)artId);
            if (doorDir >= 0 && (doorDir & 1) != 0)
                can &= ~CanFlags.I_Door;
            else
                can |= CanFlags.I_Door;
        }
        return can;
    }

    /// <summary>CChar::CanSeeItem (CCharStatus.cpp:1265): an ATTR_INVIS item is seen
    /// only by an active GM or a viewer it carries a SeenBy_0&lt;uid&gt; tag for.</summary>
    private static bool CanSeeItem(Character? viewer, Item item)
    {
        if (!item.IsAttr(ObjAttributes.Invis))
            return true;
        if (viewer == null)
            return false;
        if (viewer.IsGmMode)
            return true;
        return item.TryGetTag($"SeenBy_0{viewer.Uid.Value:x}", out string? seen) &&
               ScriptNumber.TryParseArgument(seen ?? "", out long v) && v != 0;
    }

    /// <summary>ptNow.GetRegion(REGION_TYPE_AREA|ROOM|MULTI): the innermost region
    /// at the point - a room first, else the smallest area (house and ship regions
    /// included).</summary>
    private object? LosRegionAt(Point3D pt) =>
        (object?)_world.FindRoom(pt) ?? _world.FindRegion(pt);

    /// <summary>ptNow.GetRegion(REGION_TYPE_MULTI): the house or ship region at the point.</summary>
    private Regions.Region? MultiRegionAt(Point3D pt)
    {
        Regions.Region? best = null;
        long bestArea = long.MaxValue;
        var regions = _world.Regions;
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (!r.IsFlag(RegionFlag.House) && !r.IsFlag(RegionFlag.Ship))
                continue;
            if (!r.Contains(pt))
                continue;
            long area = r.TotalArea;
            if (area < bestArea)
            {
                bestArea = area;
                best = r;
            }
        }
        return best;
    }
}
