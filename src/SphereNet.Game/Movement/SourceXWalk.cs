using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.Movement;

/// <summary>
/// The walk-height rule of Source-X, as one character moves onto one tile:
/// CChar::CheckValidMove -> CChar::CanStandAt (CCharStatus.cpp:1804-2058) over
/// CWorldMap::GetHeightPoint (CWorldMap.cpp:1258-1524) and the blocking state's
/// CheckTile_Item / CheckTile_Terrain (CServerMap.cpp:178-310).
///
/// Going up, an item can be stepped onto when its base is no higher than
/// z + climb + 3 (half its height more when it is climbable) and terrain when it is
/// no higher than z + climb + the mover's height. Going down there is no limit: the
/// highest surface under that reach is the one stood on, however far below.
/// </summary>
internal sealed class SourceXWalk
{
    // UO_SIZE_Z / UO_SIZE_MIN_Z, TERRAIN_QTY, PLAYER_HEIGHT.
    private const int SizeZ = 127;
    private const int SizeMinZ = -127;
    private const int TerrainQty = 0x4000;
    private const int PlayerHeight = 16;
    private const ushort TerrainHole = 0x0002;
    private const ushort TerrainNull = 0x0244;

    // CAN_I_* (the tile) and the CAN_C_* bits that share their values (the walker).
    private const uint Door = 0x01, Water = 0x02, Platform = 0x04, Block = 0x08,
        Climb = 0x10, Fire = 0x20, Roof = 0x40, Hover = 0x80;
    private const uint MoveMask = 0xFF;
    private const uint CGhost = 0x01, CSwim = 0x02, CWalk = 0x04, CPassWalls = 0x08,
        CFly = 0x10, CHover = 0x80;
    // CAN_C_MOVEMENTCAPABLEMASK: SWIM|WALK|FLY|RUN|HOVER.
    private const uint MovementCapable = CSwim | CWalk | CFly | 0x2000 | CHover;
    private const uint Everything = uint.MaxValue;

    /// <summary>sphere.ini EXPERIMENTAL EF_WalkCheckHeightMounted (0x40000): a rider
    /// walks with its mounted height.</summary>
    public static bool HeightMounted { get; set; }

    private readonly GameWorld _world;

    public SourceXWalk(GameWorld world) => _world = world;

    private struct Tile
    {
        public uint Flags;
        public int Id;
        public int Z;
        public int Height;

        public Tile(uint flags, int id, int z, int height)
        {
            Flags = flags; Id = id; Z = z; Height = height;
        }
    }

    /// <summary>CServerMapBlockingState.</summary>
    private sealed class BlockingState
    {
        public readonly uint CanFlags;
        public readonly int Z;
        public readonly int IHeight;
        public readonly int ZClimb;
        public Tile Top = new(0, 0, SizeZ, 0);
        public Tile Bottom = new(Block, 0, SizeMinZ, 0);
        public Tile Lowest = new(Block, 0, SizeZ, 0);
        public int ZClimbHeight;

        public BlockingState(uint canFlags, int z, int iHeight, int zClimb)
        {
            CanFlags = canFlags; Z = z; IHeight = iHeight; ZClimb = zClimb;
        }

        /// <summary>CServerMapBlockingState::CheckTile_Item (CServerMap.cpp:178).</summary>
        public void CheckTileItem(uint flags, int zBottom, int zHeight, int id)
        {
            if ((flags & Hover) != 0 && (CanFlags & CHover) == 0)
                flags &= ~Hover;
            if (flags == 0)
                return;

            int zTop = (flags & Climb) != 0 && (flags & Platform) != 0
                ? Math.Min(zBottom + zHeight / 2, SizeZ)
                : Math.Min(zBottom + zHeight, SizeZ);

            if (zTop < Bottom.Z)
                return;     // below something I can already step on
            if (zBottom > Top.Z)
                return;     // above my head

            if (zTop < Lowest.Z)
                Lowest = new Tile(flags, id, zTop, zHeight);

            if (zBottom <= ZClimb + ((flags & Climb) != 0 ? zHeight / 2 : 0))
            {
                if (zTop >= Bottom.Z)
                {
                    if (zTop == Bottom.Z)
                    {
                        if ((flags & Platform) != 0 && Bottom.Height != 0)
                        {
                            // a floor laid on a solid item: walk on the floor
                        }
                        else if ((flags & Climb) != 0)
                        {
                            // climbables have the highest priority
                        }
                        else if ((Bottom.Flags & Platform) != 0)
                        {
                            return;
                        }
                    }
                    Bottom = new Tile(flags, id, zTop, zHeight);
                    ZClimbHeight = (flags & Climb) != 0 ? (zHeight + 1) / 2 : 0;
                }
            }
            else if (zBottom < Top.Z)
            {
                // I could fit under this: it is over my head.
                Top = new Tile(flags, id, zBottom, zHeight);
            }
        }

        /// <summary>CServerMapBlockingState::CheckTile_Terrain (CServerMap.cpp:265).</summary>
        public void CheckTileTerrain(uint flags, int z, int id)
        {
            if (flags == 0)
                return;
            if (z < Bottom.Z)
                return;

            if (z < Lowest.Z)
                Lowest = new Tile(flags, id, z, 0);

            if (z <= IHeight)
            {
                if (z >= Bottom.Z)
                {
                    if ((Bottom.Flags & (Platform | Climb)) != 0 && z - Bottom.Z <= 4)
                        return;
                    if (z > Z + PlayerHeight / 2)
                    {
                        if ((Bottom.Flags & (Platform | Climb)) != 0 && z >= Bottom.Z + PlayerHeight / 2)
                        {
                            Top = new Tile(flags, id, z, 0);   // we can walk under it
                            return;
                        }
                    }
                    else if (z == Z)
                    {
                        if (Bottom.Height != 0)
                        {
                            // land on top of a solid item: walk on the land
                        }
                        else if ((Bottom.Flags & (Platform | Climb)) != 0 && z - Bottom.Z <= 4)
                        {
                            return;
                        }
                    }
                    Bottom = new Tile(flags, id, z, 0);
                    ZClimbHeight = 0;
                }
            }
            else if (z < Top.Z)
            {
                Top = new Tile(flags, id, z, 0);
            }
        }
    }

    /// <summary>CItemBase::IsID_Chair (CItemBase.cpp:553).</summary>
    private static readonly HashSet<int> ChairIds =
    [
        0x0459, 0x045a, 0x045b, 0x045c, 0x0b2c, 0x0b2d, 0x0b2e, 0x0b2f, 0x0b30, 0x0b31,
        0x0b32, 0x0b33, 0x0b4e, 0x0b4f, 0x0b50, 0x0b51, 0x0b52, 0x0b53, 0x0b54, 0x0b55,
        0x0b56, 0x0b57, 0x0b58, 0x0b59, 0x0b5a, 0x0b5b, 0x0b5c, 0x0b5d, 0x0b5e, 0x0b5f,
        0x0b60, 0x0b61, 0x0b62, 0x0b63, 0x0b64, 0x0b65, 0x0b66, 0x0b67, 0x0b68, 0x0b69,
        0x0b6a, 0x0b91, 0x0b92, 0x0b93, 0x0b94, 0x0c17, 0x0c18, 0x1049, 0x104a, 0x1207,
        0x1208, 0x1209, 0x120a, 0x120b, 0x120c, 0x1218, 0x1219, 0x121a, 0x121b, 0x1526,
        0x1527, 0x19f1, 0x19f2, 0x19f3, 0x19f5, 0x19f6, 0x19f7, 0x19f9, 0x19fa, 0x19fb,
        0x19fc, 0x1dc7, 0x1dc8, 0x1dc9, 0x1dca, 0x1dcb, 0x1dcc, 0x1dcd, 0x1dce, 0x1dcf,
        0x1dd0, 0x1dd1, 0x1dd2, 0x1e6f, 0x1e78, 0x3dff, 0x3e00,
    ];

    /// <summary>CItemBase's walk flags and height for an art id: the definition's
    /// CAN (script CAN= replaces what the tiledata gave, CBase.cpp:363) and HEIGHT,
    /// both starting from GetItemHeightFlags (CItemBase.cpp:98/766) plus the door rule
    /// of GetItemSpecificFlags (:721). An id with no ITEMDEF takes every tiledata
    /// flag and no height (GetItemTiledataFlags, CWorldMap.cpp:1316).</summary>
    private static void ItemFlags(MapDataManager md, int dispId, out uint flags, out int height)
    {
        var data = md.GetItemTileData((ushort)dispId);
        var def = DefinitionLoader.GetItemDef(dispId);
        if (def == null)
        {
            flags = (uint)TerrainEngine.GetLegacyTiledataFlags(data);
            height = 0;
            return;
        }

        height = TerrainEngine.GetLegacyTileHeightFlags(data, out var baseFlags);
        flags = (uint)baseFlags;
        if (def.DupItemId != 0 && def.DupItemId != dispId)
        {
            // A DUPEITEM art is a CItemBaseDupe: its own tiledata through
            // GetItemHeightFlags, and a chair has no height (CItemBase.cpp:1831-1836).
            if (ChairIds.Contains(dispId))
                height = 0;
            flags &= MoveMask;
            return;
        }
        if (def.Type == ItemType.Door || def.Type == ItemType.DoorLocked || def.Type == ItemType.DoorOpen)
        {
            flags &= ~Block;
            int doorDir = DoorHelper.GetDoorDir((ushort)dispId);
            if (doorDir >= 0 && (doorDir & 1) != 0)
                flags &= ~Door;
            else
                flags |= Door;
        }
        if (def.HasCanKey)
            flags = (uint)def.Can;
        if (def.Height != 0)
            height = def.Height;
        flags &= MoveMask;
    }

    /// <summary>CWorldMap::GetMapMeterAdjusted (CWorldMap.cpp:309): the terrain Z
    /// the walk uses, averaged along the flatter diagonal of the cell's corners.</summary>
    private static int AdjustedTerrainZ(MapDataManager md, int mapId, int x, int y, out ushort tileId)
    {
        var top = md.GetTerrainTile(mapId, x, y);
        tileId = top.TileId;
        int topZ = top.Z;
        int left = CornerZ(md, mapId, x, y + 1, topZ);
        int bottom = CornerZ(md, mapId, x + 1, y + 1, topZ);
        int right = CornerZ(md, mapId, x + 1, y, topZ);

        int span = AreaSpan(topZ, left, bottom, right);
        bool useLeftRight = Math.Abs(topZ - bottom) > Math.Abs(left - right);
        int v1 = useLeftRight ? left : topZ;
        int v2 = useLeftRight ? right : bottom;
        return FloorAverage(v1, v2, span);
    }

    /// <summary>CWorldMap::CheckMapTerrain (CWorldMap.cpp:1564): a neighbour corner,
    /// or the cell's own when the neighbour is off the map, the null terrain or
    /// water.</summary>
    private static int CornerZ(MapDataManager md, int mapId, int x, int y, int fallback)
    {
        var (w, h) = md.GetMapSize(mapId);
        if (x < 0 || y < 0 || x >= w || y >= h)
            return fallback;
        var cell = md.GetTerrainTile(mapId, x, y);
        if (cell.TileId == TerrainNull)
            return fallback;
        return (md.GetLandTileData(cell.TileId).Flags & TileFlag.Wet) != 0 ? fallback : cell.Z;
    }

    /// <summary>CWorldMap::GetAreaAverageHeight (CWorldMap.cpp:1544) - despite its
    /// name, the spread between the highest and the lowest corner.</summary>
    private static int AreaSpan(int top, int left, int bottom, int right)
    {
        if (bottom > top) (bottom, top) = (top, bottom);
        if (left > right) (left, right) = (right, left);
        return Math.Max(top, right) - Math.Min(bottom, left);
    }

    /// <summary>CWorldMap::GetFloorAverageHeight (CWorldMap.cpp:1526).</summary>
    private static int FloorAverage(int p1, int p2, int average)
    {
        int total = p1 + p2;
        int half = total / 2;
        int roundUp = (total & 1) != 0 && average - half > 5 ? 1 : 0;
        return half + roundUp;
    }

    /// <summary>CWorldMap::GetHeightPoint with fHouseCheck (CWorldMap.cpp:1258):
    /// statics, multi components, dynamic items, then the terrain.</summary>
    private void GetHeightPoint(MapDataManager md, int mapId, int x, int y, BlockingState block)
    {
        var statics = md.GetStaticBlock(mapId, x, y, out int offX, out int offY);
        for (int i = 0; i < statics.Length; i++)
        {
            var s = statics[i];
            if (s.XOffset != offX || s.YOffset != offY)
                continue;
            ItemFlags(md, s.TileId, out uint flags, out int height);
            // A map-static door the world has swung open is no door at the moment.
            if ((flags & Door) != 0 && _world.IsMapStaticDoorOpen((byte)mapId, (short)x, (short)y, s.Z))
                flags &= ~Door;
            block.CheckTileItem(flags, s.Z, height, s.TileId + TerrainQty);
        }

        var multis = _world.GroundMultis;
        for (int m = 0; m < multis.Count; m++)
        {
            var multi = multis[m];
            if (multi.IsDeleted || multi.IsEquipped || !multi.IsOnGround || multi.MapIndex != mapId)
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
                    ItemFlags(md, comp.TileId, out uint flags, out int height);
                    block.CheckTileItem(flags, multi.Z + comp.ZOffset, height, comp.TileId + TerrainQty);
                }
            }
            if (multi.ItemType == ItemType.MultiCustom && WalkCheck.ResolveCustomDesign != null)
            {
                foreach (var tile in WalkCheck.ResolveCustomDesign(multi))
                {
                    if (!tile.Visible || multi.X + tile.X != x || multi.Y + tile.Y != y)
                        continue;
                    ItemFlags(md, tile.TileId, out uint flags, out int height);
                    block.CheckTileItem(flags, multi.Z + tile.Z, height, tile.TileId + TerrainQty);
                }
            }
        }

        var pivot = new Core.Types.Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var item in _world.GetItemsInRange(pivot, 0))
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround)
                continue;
            if (item.X != x || item.Y != y || item.MapIndex != mapId)
                continue;
            // A multi's own item has no tile of its own; its components came above.
            if (item.ItemType is ItemType.Multi or ItemType.MultiCustom or ItemType.Ship)
                continue;
            // Flags and height come from the definition of the art the item shows
            // (FindItemBase(GetDispID()), CWorldMap.cpp:1438): a door opens by
            // changing its art, and the open art's definition carries no DOOR.
            int dispId = item.DispIdFull;
            ItemFlags(md, dispId, out uint flags, out int height);
            block.CheckTileItem(flags, item.Z, height, dispId + TerrainQty);
        }

        int terrainZ = AdjustedTerrainZ(md, mapId, x, y, out ushort landId);
        uint land;
        // The cave void (0x1AE-0x1B5, 0x1DB) is taken as no floor, like TERRAIN_HOLE.
        // Source-X reads its empty tiledata as PLATFORM, but the client never walks it
        // and creatures born on it could not walk out - a reported fault this keeps
        // fixed. It is the one place this port departs from the reference.
        if (landId == TerrainHole || MapDataManager.IsLandIgnored(landId))
            land = 0;
        else if (landId == TerrainNull)
            land = Block;
        else
        {
            var lf = md.GetLandTileData(landId).Flags;
            land = 0;
            if ((lf & TileFlag.Wet) != 0) land |= Water;
            if ((lf & TileFlag.Damaging) != 0) land |= Fire;
            if ((lf & TileFlag.Impassable) != 0) land |= Block;
            if (land == 0 || (lf & TileFlag.Surface) != 0)
                land = Platform;
        }
        block.CheckTileTerrain(land, terrainZ, landId);

        if (block.Bottom.Z == SizeMinZ)
        {
            block.Bottom = block.Lowest;
            if (block.Top.Z == block.Bottom.Z)
                block.Top = new Tile(0, 0, SizeZ, 0);
        }
    }

    /// <summary>CChar::GetCanMoveFlags (CCharStatus.cpp:733).</summary>
    internal static uint GetCanMoveFlags(Character ch)
    {
        if (ch.PrivLevel >= PrivLevel.GM || ch.AllMove)
            return Everything;
        uint can = (uint)CharDefHelper.GetCanFlags(ch);
        if (ch.IsDead)
            can |= CGhost;
        if (ch.IsStatFlag(StatFlag.Hovering))
            can |= CHover;
        if ((can & CWalk) != 0)
            can |= CFly;
        return can & ((uint)CanFlags.C_NoBlockHeight | MoveMask);
    }

    private static int WalkHeight(Character ch)
    {
        int height = ch.GetHeight();
        if (HeightMounted && (ch.IsMounted || ch.IsStatFlag(StatFlag.Hovering)))
            height += 4;    // GetHeightMount
        return height;
    }

    /// <summary>CChar::FixClimbHeight (CCharStatus.cpp:2060): standing on stairs, the
    /// climb is half the step's height; anywhere else it is nothing.</summary>
    public int ClimbHeightAt(Character ch, int mapId, int x, int y, int z)
    {
        var md = _world.MapData;
        if (md == null)
            return 0;
        int heightMount = ch.GetHeight() + (ch.IsMounted || ch.IsStatFlag(StatFlag.Hovering) ? 4 : 0);
        var block = new BlockingState(Climb, z, z + heightMount + 3, z + 2);
        GetHeightPoint(md, mapId, x, y, block);
        return block.Bottom.Z == z ? block.ZClimbHeight : 0;
    }

    /// <summary>CChar::CheckValidMove + CanStandAt for one tile: may <paramref name="ch"/>,
    /// standing at height <paramref name="z"/> with <paramref name="climb"/> of climb,
    /// stand on (x, y), and at what height? Characters are not considered here.</summary>
    public bool CanStandAt(Character ch, int mapId, int x, int y, int z, int climb,
        bool pathFinding, out int newZ, out string reason)
    {
        newZ = z;
        reason = "";
        var md = _world.MapData;
        if (md == null)
            return false;
        var (w, h) = md.GetMapSize(mapId);
        if (x < 0 || y < 0 || x >= w || y >= h)
        {
            reason = "off_map";
            return false;
        }

        uint movementCan = GetCanMoveFlags(ch);
        if ((movementCan & MovementCapable) == 0)
        {
            reason = "cannot_move";
            return false;
        }

        int height = WalkHeight(ch);
        var block = new BlockingState(movementCan, z, z + climb + height, z + climb + 3);
        GetHeightPoint(md, mapId, x, y, block);

        uint canFlags = (uint)CharDefHelper.GetCanFlags(ch);
        uint pointFlags = block.Bottom.Flags;
        uint blockedBy = 0;

        if (block.Top.Flags != 0)
        {
            bool topLand = block.Top.Id <= TerrainQty;
            if (!topLand && (block.Top.Flags & (Roof | Platform | Block)) != 0 &&
                (canFlags & (uint)CanFlags.C_NoIndoors) != 0)
            {
                reason = "no_indoors";
                return false;
            }

            int heightDiff = block.Top.Z - block.Bottom.Z;
            int heightReq = topLand ? height / 2 : height;
            bool noBlockHeight = (canFlags & (uint)CanFlags.C_NoBlockHeight) != 0 ||
                _world.FindRegion(new Core.Types.Point3D((short)x, (short)y, (sbyte)z, (byte)mapId))
                    ?.IsFlag(RegionFlag.WalkNoBlockHeight) == true;
            if (heightDiff < heightReq && !noBlockHeight)
            {
                pointFlags |= Block;
                blockedBy |= Roof;
            }
            else if (heightDiff < climb + heightReq)
            {
                pointFlags |= Block;
                blockedBy |= Climb;
            }
        }

        bool landTile = block.Bottom.Id <= TerrainQty;
        bool passThrough = false;
        if (movementCan != Everything && pointFlags != 0)
        {
            if ((pointFlags & Water) != 0)
            {
                if ((movementCan & CSwim) != 0)
                    pointFlags &= ~Block;
                else
                {
                    pointFlags |= Block;
                    blockedBy |= Water;
                }
            }
            if ((pointFlags & Platform) != 0 && (movementCan & CWalk) == 0)
            {
                pointFlags |= Block;
                blockedBy |= Platform;
            }
            if ((pointFlags & Door) != 0)
            {
                if ((movementCan & CGhost) != 0)
                    passThrough = true;
                else
                {
                    pointFlags |= Block;
                    blockedBy |= Door;
                }
            }
            if ((pointFlags & Hover) != 0 &&
                (movementCan & CHover) == 0 && !ch.IsStatFlag(StatFlag.Hovering))
            {
                pointFlags |= Block;
                blockedBy |= Hover;
            }

            if (block.Bottom.Z >= SizeZ)
            {
                reason = "no_floor";
                return false;
            }

            if (landTile)
            {
                if ((pointFlags & Block) != 0 && (blockedBy & Climb) == 0)
                {
                    reason = $"land_blocked by=0x{blockedBy:X}";
                    return false;
                }
                if (block.Bottom.Z > z + climb + height + 3)
                {
                    reason = $"land_too_high bottom={block.Bottom.Z}";
                    return false;
                }
            }
            else if (!passThrough && (pointFlags & Block) != 0)
            {
                if ((blockedBy & Climb) == 0)
                {
                    reason = $"item_blocked by=0x{blockedBy:X}";
                    return false;
                }
                if ((movementCan & CPassWalls) == 0)
                {
                    if ((movementCan & CFly) != 0)
                    {
                        if ((block.Top.Flags & Roof) != 0)
                        {
                            reason = "under_roof";
                            return false;
                        }
                    }
                    else if ((pointFlags & Climb) != 0)
                    {
                        // a climbable item this walker can climb
                    }
                    else if (block.Bottom.Z > z + climb + 2)
                    {
                        reason = $"too_high_to_climb bottom={block.Bottom.Z}";
                        return false;
                    }
                }
            }
        }

        if (ch.IsMounted && height + z >= block.Top.Z && WalkCheck.MountHeight &&
            ch.PrivLevel < PrivLevel.GM && !ch.AllMove)
        {
            reason = "mount_ceiling";
            return false;
        }

        if (!passThrough)
            newZ = block.Bottom.Z;
        reason = $"accepted z={newZ}";
        return true;
    }

    /// <summary>The floor a character is seated on at (x, y) from
    /// <paramref name="referenceZ"/>: the same gravity as a step - the highest
    /// surface within reach, however far below. With <paramref name="ignoreCollision"/>
    /// the walker passes everything, as a GM's CAN is (GetCanMoveFlags returns
    /// UINT64_MAX); otherwise the spot must be standable. Found is false when nothing
    /// is there to stand on.</summary>
    public (bool Found, int Z, bool HasHeadroom) Seat(Character ch, int mapId, int x, int y,
        int referenceZ, bool ignoreCollision)
    {
        var md = _world.MapData;
        if (md == null)
            return (false, referenceZ, true);

        int height = WalkHeight(ch);
        if (ignoreCollision)
        {
            var block = new BlockingState(Everything, referenceZ, referenceZ + height, referenceZ + 3);
            GetHeightPoint(md, mapId, x, y, block);
            if (block.Bottom.Z >= SizeZ || block.Bottom.Z <= SizeMinZ)
                return (false, referenceZ, true);
            return (true, block.Bottom.Z, block.Top.Z - block.Bottom.Z >= height);
        }

        bool ok = CanStandAt(ch, mapId, x, y, referenceZ, 0, pathFinding: true, out int z, out _);
        return ok ? (true, z, true) : (false, referenceZ, false);
    }

    /// <summary>CChar::CheckValidMove (CCharStatus.cpp:1978) for a step in
    /// <paramref name="dir"/>: a diagonal first needs both orthogonal neighbours of
    /// the starting point (not while pathfinding), then the destination.</summary>
    public bool CheckValidMove(Character ch, int mapId, int fromX, int fromY, int z, Direction dir,
        bool pathFinding, out int newZ, out string reason)
    {
        int climb = ClimbHeightAt(ch, mapId, fromX, fromY, z);
        int d = (int)dir & 0x7;
        if (!pathFinding && (d & 1) != 0)
        {
            foreach (int side in new[] { (d - 1) & 0x7, (d + 1) & 0x7 })
            {
                int sx = fromX, sy = fromY;
                WalkCheck.Offset((Direction)side, ref sx, ref sy);
                if (!CanStandAt(ch, mapId, sx, sy, z, climb, pathFinding, out _, out string sideReason))
                {
                    newZ = z;
                    reason = $"diagonal_side {(Direction)side}: {sideReason}";
                    return false;
                }
            }
        }

        int tx = fromX, ty = fromY;
        WalkCheck.Offset((Direction)d, ref tx, ref ty);
        return CanStandAt(ch, mapId, tx, ty, z, climb, pathFinding, out newZ, out reason);
    }
}
