using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Map;
using SphereNet.MapData.Tiles;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Movement;

/// <summary>
/// Per-step walk validator. A step is decided by Source-X's rule
/// (<see cref="SourceXWalk"/>: CheckValidMove / CanStandAt / GetHeightPoint);
/// the standing-surface resolver used to seat a character (login, mount,
/// teleport) keeps its sorted geometry list, whose structure was modelled on
/// ServUO's MovementImpl (with credit to its authors).
/// </summary>
public sealed class WalkCheck
{
    internal const int PersonHeight = 16;
    private const int StepHeight = 2;

    private const TileFlag ImpassableSurface = TileFlag.Impassable | TileFlag.Surface;

    /// <summary>sphere.ini MOUNTHEIGHT (Source-X m_iMountHeight, default 0): a rider
    /// (or a hovering gargoyle) is 4 taller (GetHeightMount, CChar.cpp:1498) and may
    /// not step where that height reaches the ceiling (CanStandAt,
    /// CCharStatus.cpp:1958: height + z &gt;= top fails). GM and AllMove pass.</summary>
    public static bool MountHeight { get; set; }

    /// <summary>The open space a rider needs above the floor: strictly more than
    /// PLAYER_HEIGHT + 4.</summary>
    internal const int MountedClearance = PersonHeight + 4 + 1;

    internal static bool RiderNeedsHeadroom(Character mover) =>
        MountHeight &&
        (mover.IsMounted || mover.IsStatFlag(StatFlag.Hovering)) &&
        mover.PrivLevel < PrivLevel.GM && !mover.AllMove;

    /// <summary>CChar::IsVerticalSpace (CCharStatus.cpp:1785): is there room above
    /// <paramref name="standZ"/> at (x, y) for this character plus
    /// <paramref name="extra"/> (4 when about to mount)? True when no ceiling
    /// blocks, or for GM / AllMove.</summary>
    public bool HasVerticalSpace(Character mover, int mapId, int x, int y, int standZ, int extra)
    {
        if (mover.PrivLevel >= PrivLevel.GM || mover.AllMove)
            return true;
        var md = _world.MapData;
        if (md == null)
            return true;
        var items = CollectItems(mapId, x, y);
        var trace = new CheckTrace();
        var list = BuildPathEntries(md, mapId, x, y, items, mover, ref trace);
        int height = PersonHeight + (mover.IsMounted || mover.IsStatFlag(StatFlag.Hovering) ? 4 : 0);
        foreach (var entry in list)
        {
            if ((entry.Flags & PathFlags.ImpSurf) == 0 || entry.Z <= standZ)
                continue;
            // list is sorted; the first blocking entry above is the ceiling.
            return height + standZ + extra < entry.Z;
        }
        return true;
    }

    /// <summary>
    /// Land-tile movement barrier rule: only WATER (Impassable + Wet) blocks a
    /// walking mover. Dry land — including steep "impassable"-flagged mountain
    /// and slope terrain — stays walkable, because the 2D/UO client predicts
    /// movement onto those slopes (it walks up/down them and renders the step
    /// before the server replies). If the server blocked them, the client would
    /// walk where the server rejects, and every rejection snaps the running
    /// client back several tiles (the "stairs throw me sideways" rubber-band).
    ///
    /// Matching the client's walkability is the controlling rule here. A blunt
    /// "all impassable land blocks" (ServUO-style) desynced from the client and
    /// broke walking the terrain beside stairs. Water still blocks via the Wet
    /// bit (plus impassable water statics), so a mover still cannot walk INTO
    /// the sea.
    /// </summary>
    internal static bool LandBlocks(LandTileData landData) => landData.IsImpassable && landData.IsWet;

    /// <summary>TERRAIN_NULL, the black void between dungeon walls. Source-X gives it
    /// no surface at all - CAN_I_BLOCK (CWorldMap.cpp, TerrainEngine's legacy path) -
    /// whereas the general rule above lets dry Impassable land be walked (hillside
    /// slopes carry that bit too). Without this, dungeon creatures wandered across
    /// the black.</summary>
    internal const ushort TerrainNull = 0x0244;

    internal static bool LandBlocks(ushort tileId, LandTileData landData) =>
        tileId == TerrainNull || LandBlocks(landData);

    private readonly GameWorld _world;

    private readonly SourceXWalk _sourceX;

    public WalkCheck(GameWorld world)
    {
        _world = world;
        _sourceX = new SourceXWalk(world);
    }

    /// <summary>Diagnostic result from <see cref="CheckMovementDetailed"/>,
    /// used by the walk-reject log so rejections can be attributed to the
    /// forward tile, a diagonal edge, or a mobile block instead of a generic
    /// "collision".</summary>
    public readonly record struct Diagnostic(
        int StartZ, int StartTop,
        bool ForwardOk, int ForwardNewZ,
        bool DiagonalChecked, bool LeftOk, bool RightOk,
        bool MobBlocked,
        // Forward tile geometry — populated when the forward check fails so
        // reject logs can distinguish "no surfaces at this tile" from "land
        // was there but IsOk rejected it" from "stair riser too tall".
        int FwdLandZ, int FwdLandCenter, int FwdLandTop,
        bool FwdLandBlocks, bool FwdConsiderLand,
        int FwdSurfaceCount, int FwdItemSurfaceCount,
        string FwdReason,
        // Raw tile inventory for the forward tile — useful when surface
        // counters are 0 to see what IS there (walls, decorative, etc.).
        int FwdStaticTotal, int FwdImpassableCount, ushort FwdLandTileId,
        string FwdStaticDump,
        int FwdMobileCount, string FwdMobileDump);

    /// <summary>The refusal trace, as lines a staff member can read.
    ///
    /// Both readers of a diagnostic print the same thing: the step handler when a
    /// step was actually refused, and .WALKDIAG's own probe. The probe exists
    /// because a refused step often never reaches the server at all - ClassicUO
    /// runs its own Pathfinder.CanWalk before sending and simply drops the step
    /// when it fails (PlayerMobile.cs:572), so "I could not step there" can come
    /// with no packet behind it and nothing for the server to explain. Asking the
    /// server what IT sees on the tile ahead is then the only way to compare the
    /// two.</summary>
    public static List<string> DescribeDiagnostic(Diagnostic diag, Direction d, Point3D from)
    {
        var lines = new List<string>(4)
        {
            $"[walk] {d} from {from.X},{from.Y},{from.Z}: " +
            $"reason={diag.FwdReason ?? "(none)"} fwdOk={diag.ForwardOk} " +
            $"mobBlocked={diag.MobBlocked} diag={diag.DiagonalChecked}/" +
            $"L{diag.LeftOk}R{diag.RightOk}",

            $"[walk] target land tile 0x{diag.FwdLandTileId:X4} " +
            $"z={diag.FwdLandZ} centre={diag.FwdLandCenter} top={diag.FwdLandTop} " +
            $"blocks={diag.FwdLandBlocks} considered={diag.FwdConsiderLand}; " +
            $"surfaces={diag.FwdSurfaceCount}+{diag.FwdItemSurfaceCount} " +
            $"statics={diag.FwdStaticTotal} impassable={diag.FwdImpassableCount} " +
            $"mobiles={diag.FwdMobileCount}",
        };
        if (!string.IsNullOrEmpty(diag.FwdStaticDump))
            lines.Add($"[walk] {diag.FwdStaticDump}");
        if (!string.IsNullOrEmpty(diag.FwdMobileDump))
            lines.Add($"[walk] {diag.FwdMobileDump}");
        return lines;
    }

    /// <summary>Entry point. Returns true if <paramref name="mover"/> can step
    /// in direction <paramref name="d"/> from <paramref name="loc"/>, and sets
    /// <paramref name="newZ"/> to the Z the mover should land on.</summary>
    public bool CheckMovement(Character mover, Point3D loc, Direction d, out int newZ)
    {
        return CheckMovementDetailed(mover, loc, d, out newZ, out _);
    }

    /// <summary>Same as <see cref="CheckMovement"/> but also returns a
    /// <see cref="Diagnostic"/> breakdown of which stage accepted/rejected the
    /// step. Intended for walk-reject telemetry.</summary>
    public bool CheckMovementDetailed(Character mover, Point3D loc, Direction d,
        out int newZ, out Diagnostic diag)
    {
        newZ = loc.Z;
        diag = default;
        var md = _world.MapData;
        if (md == null) return false;

        var can = CharDefHelper.GetCanFlags(mover);
        if (mover.PrivLevel < PrivLevel.GM && ((can & (CanFlags.C_NonMover | CanFlags.C_Statue)) != 0 ||
            ((can & (CanFlags.C_Walk | CanFlags.C_Swim | CanFlags.C_Fly | CanFlags.C_Hover | CanFlags.C_PassWalls)) == 0 &&
             !mover.IsStatFlag(StatFlag.Hovering))))
            return false;

        if (CharDefHelper.CanPassWalls(mover))
        {
            newZ = loc.Z;
            return true;
        }

        int mapId = mover.MapIndex;
        int xStart = loc.X;
        int yStart = loc.Y;

        int xForward = xStart, yForward = yStart;
        int xRight = xStart, yRight = yStart;
        int xLeft = xStart, yLeft = yStart;

        bool checkDiagonals = ((int)d & 0x1) == 0x1;

        Offset(d, ref xForward, ref yForward);
        Offset((Direction)(((int)d - 1) & 0x7), ref xLeft, ref yLeft);
        Offset((Direction)(((int)d + 1) & 0x7), ref xRight, ref yRight);

        // Bounds — reject walking off the map edge.
        var (mapW, mapH) = md.GetMapSize(mapId);
        if (xForward < 0 || yForward < 0 || xForward >= mapW || yForward >= mapH) return false;
        if (checkDiagonals && (xLeft < 0 || yLeft < 0 || xRight < 0 || yRight < 0 ||
            xLeft >= mapW || yLeft >= mapH || xRight >= mapW || yRight >= mapH))
            return false;

        var mobsForward = CollectMobiles(mapId, xForward, yForward, mover);

        // Raw tile inventory at the forward tile, for the reject log: every
        // static's (id, z, height, flags, name) and every character on it.
        int fwdImpassable = 0;
        int fwdStaticCount = 0;
        var dump = new System.Text.StringBuilder();
        md.ForEachStatic(mapId, xForward, yForward, s =>
        {
            fwdStaticCount++;
            var sd = md.GetItemTileData(s.TileId);
            if (sd.IsImpassable) fwdImpassable++;
            if (dump.Length > 0) dump.Append(',');
            string nm = sd.Name ?? "";
            if (nm.Length > 12) nm = nm.Substring(0, 12);
            dump.Append($"0x{s.TileId:X}@{s.Z}h{sd.Height}f0x{(ulong)sd.Flags:X}'{nm}'");
        });
        var fwdLandTile = md.GetTerrainTile(mapId, xForward, yForward);
        var mobDump = new System.Text.StringBuilder();
        foreach (var mob in mobsForward)
        {
            if (mobDump.Length > 0) mobDump.Append(',');
            string nm = mob.Name ?? "";
            if (nm.Length > 12) nm = nm.Substring(0, 12);
            mobDump.Append($"0x{mob.Uid.Value:X} z={mob.Z} dead={mob.IsDead} player={mob.IsPlayer} war={mob.IsInWarMode} '{nm}'");
        }

        // Source-X CChar::CheckValidMove (CCharStatus.cpp:1978): a diagonal step first
        // needs both orthogonal neighbours of the starting point, then the
        // destination; each is CanStandAt from the mover's own height and climb.
        int climb = _sourceX.ClimbHeightAt(mover, mapId, xStart, yStart, loc.Z);
        bool forwardOk = _sourceX.CanStandAt(mover, mapId, xForward, yForward, loc.Z, climb,
            pathFinding: false, out newZ, out string fwdReason);
        int forwardNewZ = newZ;

        bool leftOk = false, rightOk = false;
        if (checkDiagonals)
        {
            leftOk = _sourceX.CanStandAt(mover, mapId, xLeft, yLeft, loc.Z, climb, false, out _, out _);
            rightOk = _sourceX.CanStandAt(mover, mapId, xRight, yRight, loc.Z, climb, false, out _, out _);
        }
        bool moveOk = forwardOk && (!checkDiagonals || (leftOk && rightOk));

        // Characters on the destination (ShoveCharAtPosition ignores anyone more than
        // 5 Z away, CCharAct.cpp:4622).
        bool mobBlocked = false;
        if (moveOk)
        {
            foreach (var mob in mobsForward)
            {
                if (Math.Abs(mob.Z - newZ) <= 5 && !CanMoveOver(mover, mob))
                {
                    mobBlocked = true;
                    moveOk = false;
                    break;
                }
            }
        }

        if (!moveOk) newZ = loc.Z;

        diag = new Diagnostic(loc.Z, loc.Z, forwardOk, forwardNewZ,
            checkDiagonals, leftOk, rightOk, mobBlocked,
            fwdLandTile.Z, fwdLandTile.Z, fwdLandTile.Z,
            false, true, 0, 0,
            fwdReason,
            fwdStaticCount, fwdImpassable, fwdLandTile.TileId, dump.ToString(),
            mobsForward.Count, mobDump.ToString());
        return moveOk;
    }

    /// <summary>Source-X CChar::CheckValidMove with fPathFinding for one step of a
    /// route search: no diagonal side test and no characters. Returns the height the
    /// mover would stand at.</summary>
    public bool CheckPathStep(Character mover, int mapId, int fromX, int fromY, int fromZ,
        Direction d, out int newZ)
    {
        newZ = fromZ;
        if (_world.MapData == null)
            return false;
        return _sourceX.CheckValidMove(mover, mapId, fromX, fromY, fromZ, d, pathFinding: true,
            out newZ, out _);
    }

    /// <summary>Surface counters collected while building a tile's geometry list.</summary>
    private struct CheckTrace
    {
        public int LandZ, LandCenter, LandTop;
        public bool LandBlocks, ConsiderLand;
        public int SurfaceCandidates;       // static tiles with Surface flag
        public int ItemSurfaceCandidates;   // in-world items with Surface flag
        public string LastReason;
    }

    // -----------------------------------------------------------------
    //  Tile geometry list (land, statics, items) sorted by Z - used by the
    //  standing-surface resolver and the headroom check. A walking step is
    //  decided by SourceXWalk instead.
    // -----------------------------------------------------------------

    [Flags]
    private enum PathFlags : byte
    {
        None = 0,
        ImpSurf = 1,
        Surface = 2,
        Bridge  = 4,
    }

    private readonly record struct PathEntry(PathFlags Flags, int Z, int AverageZ, int Height) : IComparable<PathEntry>
    {
        public int CompareTo(PathEntry other) => Z.CompareTo(other.Z);
    }

    [ThreadStatic] private static List<PathEntry>? t_pathList;

    /// <summary>Collect every piece of standing/blocking geometry at a tile
    /// into a sorted PathEntry list — land, statics (open doors passable),
    /// dynamic items and virtual multi/custom-house components. This is THE
    /// surface inventory both the walk path and the standing-surface
    /// resolver select from; the trace counters feed the reject log.</summary>
    private List<PathEntry> BuildPathEntries(MapDataManager md, int mapId, int x, int y,
        List<Item> items, Character mover, ref CheckTrace trace)
    {
        var can = CharDefHelper.GetCanFlags(mover);
        bool ghost = CharDefHelper.CanPassDoors(mover);
        bool swims = (can & CanFlags.C_Swim) != 0;
        bool walks = (can & CanFlags.C_Walk) != 0;
        bool hovers = (can & CanFlags.C_Hover) != 0 || mover.IsStatFlag(StatFlag.Hovering);
        var landTile = md.GetTerrainTile(mapId, x, y);
        var landData = md.GetLandTileData(landTile.TileId);
        bool landBlocks = landTile.TileId == TerrainNull || (landData.IsWet ? !swims : !walks);
        bool considerLand = !MapDataManager.IsLandIgnored(landTile.TileId);

        md.GetAverageZ(mapId, x, y, out int landZ, out int landCenter, out int landTop);
        var staticBlock = md.GetStaticBlock(mapId, x, y, out int staticOffX, out int staticOffY);

        trace.LandZ = landZ;
        trace.LandCenter = landCenter;
        trace.LandTop = landTop;
        trace.LandBlocks = landBlocks;
        trace.ConsiderLand = considerLand;
        trace.LastReason = "no_candidates";

        var list = t_pathList ??= new List<PathEntry>(32);
        list.Clear();

        // --- Land tile → PathEntry ---
        // Dry land is always a walkable Surface/Bridge here even when the tiledata
        // carries the Impassable bit: many sloped "dirt"/hillside land tiles (e.g.
        // 0x91, 0x93) are Impassable-flagged yet the client (and RunUO/ServUO)
        // walk them as terrain. Only WATER (Impassable+Wet, via landBlocks) is a
        // true barrier. Treating Impassable land as a non-surface blocked those
        // slopes server-side while the client walked them — a reject/rubber-band
        // on every hillside step. See LandBlocks().
        if (considerLand && !landBlocks)
        {
            list.Add(new PathEntry(
                PathFlags.ImpSurf | PathFlags.Surface | PathFlags.Bridge,
                landZ, landCenter, landCenter - landZ));
        }

        // --- Static tiles → PathEntries ---
        for (int i = 0; i < staticBlock.Length; i++)
        {
            var tile = staticBlock[i];
            if (tile.XOffset != staticOffX || tile.YOffset != staticOffY)
                continue;
            var data = md.GetItemTileData(tile.TileId);

            // A ghost is not stopped by a door. Source-X gives every DEAD char
            // CAN_C_GHOST (CCharStatus.cpp:739) and that clears CAN_I_BLOCK off a
            // door tile (CChar.cpp:760) — only a door, walls still need
            // CAN_C_PASSWALLS. Treating it as an open door is the same geometry.
            bool isDoorOpen = (data.Flags & TileFlag.Door) != 0 &&
                (ghost || _world.IsMapStaticDoorOpen((byte)mapId, (short)x, (short)y, tile.Z));
            bool water = (data.Flags & TileFlag.Wet) != 0;
            bool surface = water ? swims : data.IsSurface && walks;
            bool effectiveImpassable = (data.IsImpassable && !(water && swims)) && !isDoorOpen;
            if ((data.Flags & TileFlag.HoverOver) != 0)
            {
                effectiveImpassable = !hovers;
                surface = hovers;
            }

            PathFlags pf = PathFlags.None;
            if (effectiveImpassable || surface || data.IsRoof)
                pf |= PathFlags.ImpSurf;
            if (!effectiveImpassable)
            {
                if (surface) pf |= PathFlags.Surface;
                if (data.IsBridge && walks) pf |= PathFlags.Bridge;
            }
            if (pf == PathFlags.None) continue;

            int tileZ = tile.Z;
            int h = data.Height;
            int avgZ = tileZ + (data.IsBridge ? h / 2 : h);
            list.Add(new PathEntry(pf, tileZ, avgZ, h));

            if ((pf & PathFlags.Surface) != 0)
                trace.SurfaceCandidates++;
        }

        // --- In-world items → PathEntries ---
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var data = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, data)) continue;

            // Same ghost rule as the static tiles above, for door ITEMS.
            bool water = (data.Flags & TileFlag.Wet) != 0;
            bool surface = water ? swims : data.IsSurface && walks;
            bool impassable = data.IsImpassable && !(water && swims) && !(ghost && IsDoorGeometry(item, data));
            if ((data.Flags & TileFlag.HoverOver) != 0)
            {
                impassable = !hovers;
                surface = hovers;
            }

            PathFlags pf = PathFlags.None;
            if (impassable || surface || data.IsRoof)
                pf |= PathFlags.ImpSurf;
            if (!impassable)
            {
                if (surface) pf |= PathFlags.Surface;
                if (data.IsBridge && walks) pf |= PathFlags.Bridge;
            }
            if (pf == PathFlags.None) continue;

            int itemZ = item.Z;
            int h = data.Height;
            int avgZ = itemZ + (data.IsBridge ? h / 2 : h);
            list.Add(new PathEntry(pf, itemZ, avgZ, h));

            if ((pf & PathFlags.Surface) != 0)
                trace.ItemSurfaceCandidates++;
        }

        list.Sort();
        return list;
    }

    // -----------------------------------------------------------------
    //  ResolveStandingSurface — the shared character surface resolver
    //  (audit design). Every path that SEATS a character (login, mount,
    //  dismount, teleport-derive, GM/pass-walls step, NPC step Z) resolves
    //  through this, so seat Z and walk Z can never diverge again.
    //  MapDataManager.GetEffectiveZ stays for non-character queries only
    //  (spawn gems, ground items, previews) — it sees neither dynamics,
    //  multis, custom houses nor headroom.
    // -----------------------------------------------------------------

    public enum StandingPolicy
    {
        /// <summary>GM / AllMove / pass-walls step: nothing blocks, but the mover
        /// still follows the ground - the highest surface within reach.</summary>
        IgnoreCollision,

        /// <summary>Seat a character at a coordinate whose Z must be re-derived
        /// (login, mount, dismount, teleport): the highest standable surface
        /// within reach of the reference Z, however far below it.</summary>
        Settle,
    }

    public readonly record struct StandingResult(bool Found, sbyte Z, bool HasHeadroom);

    /// <summary>Resolve the surface a character should stand on at (x, y) from
    /// <paramref name="referenceZ"/>, by the rule a walking step uses.</summary>
    public StandingResult ResolveStandingSurface(Character mover, int mapId, int x, int y,
        int referenceZ, StandingPolicy policy)
    {
        var md = _world.MapData;
        if (md == null)
            return new StandingResult(false, (sbyte)referenceZ, true);
        var (mapW, mapH) = md.GetMapSize(mapId);
        if (x < 0 || y < 0 || x >= mapW || y >= mapH)
            return new StandingResult(false, (sbyte)referenceZ, true);

        // The same Source-X gravity a step uses (SourceXWalk), so a seated Z and a
        // walked Z cannot disagree.
        var (found, z, headroom) = _sourceX.Seat(mover, mapId, x, y, referenceZ,
            policy == StandingPolicy.IgnoreCollision);
        return new StandingResult(found, (sbyte)Math.Clamp(z, -128, 127), headroom);
    }

    /// <summary>A door, by item type or by tiledata flag — what CAN_I_DOOR marks
    /// in Source-X. Only these let a ghost through; a wall does not.</summary>
    private static bool IsDoorGeometry(Item item, ItemTileData data) =>
        item.ItemType is ItemType.Door or ItemType.DoorLocked
        || (data.Flags & TileFlag.Door) != 0;

    private static bool ShouldTreatAsMovementGeometry(Item item, ItemTileData data)
    {
        // Only treat world items as movement geometry when they are meaningful
        // obstacles/floors. Small loose drops like reagents, weapons, bags,
        // etc. should not become collision just because tiledata carries a
        // Surface bit, while bulky/anchored objects still should.
        if (item.ItemType == ItemType.Corpse) return false;
        if (item.IsStaticBlock) return true;
        // Virtual multi/ship/custom-house components (AddVirtualGeometry
        // stamps them MultiAddon) are structural by definition — the loose-
        // item height filter below silently dropped every flat house-floor
        // tile (Surface, height 0) from the walk geometry, which the
        // synthetic standing-surface matrix exposed.
        if (item.ItemType == ItemType.MultiAddon) return true;
        if (item.IsAttr(ObjAttributes.Static)
            || item.IsAttr(ObjAttributes.Move_Never)
            || item.IsAttr(ObjAttributes.LockedDown))
            return true;
        if (data.IsBridge) return true;

        // Loose items that are low enough to step over should not affect
        // movement. Keep explicit impassables blocking even when movable.
        if (!data.IsImpassable && data.CalcHeight <= StepHeight)
            return false;

        return true;
    }

    // -----------------------------------------------------------------
    //  Helpers — tile / mobile collection and direction offsets.
    // -----------------------------------------------------------------

    /// <summary>Items placed on the ground at exactly (x, y) on mover's map.
    /// Filters out contained/equipped items since those are not walk-relevant.</summary>
    private List<Item> CollectItems(int mapId, int x, int y)
    {
        var list = new List<Item>();
        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var item in _world.GetItemsInRange(pivot, 0))
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) continue;
            if (item.X != x || item.Y != y) continue;
            list.Add(item);
        }
        AddVirtualMultiComponents(mapId, x, y, list);
        return list;
    }

    /// <summary>Resolves the committed custom-house design tiles for a
    /// MultiCustom item — wired by the host to CustomHousingEngine's cached
    /// design lookup. Unset → custom designs contribute no walk geometry.</summary>
    public static Func<Item, IReadOnlyList<HouseDesignTile>>? ResolveCustomDesign;

    private void AddVirtualMultiComponents(int mapId, int x, int y, List<Item> list)
    {
        var md = _world.MapData;
        if (md == null)
            return;

        // The world's multi index instead of a 32-tile spatial range query:
        // this runs on EVERY walk/standing check (players, NPC steps, seat
        // paths), and the per-step sector scan dominated the live server's
        // apply phase. A shard has few multis — a bounds check over the
        // short list is orders of magnitude cheaper, and an empty list
        // (open wilderness) costs nothing.
        var multis = _world.GroundMultis;
        for (int m = 0; m < multis.Count; m++)
        {
            var multi = multis[m];
            if (multi.IsDeleted || multi.IsEquipped || !multi.IsOnGround)
                continue;
            if (multi.MapIndex != mapId)
                continue;
            if (Math.Abs(multi.X - x) > 32 || Math.Abs(multi.Y - y) > 32)
                continue;

            var def = md.GetMulti(multi.BaseId);
            if (def != null)
            {
                foreach (var comp in def.Components)
                {
                    if (!comp.IsVisible)
                        continue;

                    int compX = multi.X + comp.XOffset;
                    int compY = multi.Y + comp.YOffset;
                    if (compX != x || compY != y)
                        continue;

                    AddVirtualGeometry(md, list, comp.TileId, x, y,
                        (sbyte)(multi.Z + comp.ZOffset), (byte)mapId);
                }
            }

            // Committed custom-house design tiles (DESIGN_n) are not real
            // items (the client renders them from the 0xD8 stream), so they
            // become walk geometry the same virtual way as multi components.
            if (multi.ItemType == ItemType.MultiCustom && ResolveCustomDesign != null)
            {
                foreach (var tile in ResolveCustomDesign(multi))
                {
                    // Invisible tiles are commit-materialized fixtures (doors)
                    // that already exist as real items — the design copy must
                    // not add a second, never-opening collision box.
                    if (!tile.Visible)
                        continue;
                    if (multi.X + tile.X != x || multi.Y + tile.Y != y)
                        continue;

                    AddVirtualGeometry(md, list, tile.TileId, x, y,
                        (sbyte)(multi.Z + tile.Z), (byte)mapId);
                }
            }
        }
    }

    private static void AddVirtualGeometry(MapData.MapDataManager md, List<Item> list,
        ushort tileId, int x, int y, sbyte z, byte mapId)
    {
        var data = md.GetItemTileData(tileId);
        if (!ShouldTreatAsVirtualMultiGeometry(data))
            return;

        list.Add(new Item
        {
            BaseId = tileId,
            ItemType = ItemType.MultiAddon,
            Position = new Point3D((short)x, (short)y, z, mapId)
        });
    }

    private static bool ShouldTreatAsVirtualMultiGeometry(ItemTileData data) =>
        data.IsSurface || data.IsBridge || data.IsImpassable;

    private List<Character> CollectMobiles(int mapId, int x, int y, Character mover)
    {
        var list = new List<Character>();
        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var ch in _world.GetCharsInRange(pivot, 0))
        {
            // A dead player ghost stays in the list: it costs nothing to pass but
            // it is still a mobile (CCharAct.cpp:4628); a dead NPC is a corpse.
            if (ch == mover || ch.IsDeleted || (ch.IsDead && !ch.IsPlayer)) continue;
            if (ch.X != x || ch.Y != y) continue;
            list.Add(ch);
        }
        return list;
    }

    /// <summary>The side-effect-free half of Source-X ShoveCharAtPosition
    /// (CCharAct.cpp:4605-4665): may <paramref name="mover"/> push past
    /// <paramref name="blocker"/>. MovementEngine re-runs the full check with the
    /// @PersonalSpace/@charShove triggers, the messages and the stamina charge once
    /// the step is taken. Only a GM is exempt; a dead, sleeping or insubstantial
    /// mover and an insubstantial blocker pass; one creature does not push past
    /// another (NPCSHOVENPC / TAG.OVERRIDE.SHOVE aside); a push costs 10 stamina and
    /// needs full stamina, except past the dead or - unless
    /// REVEALF_OSILIKEPERSONALSPACE - the hidden/invisible, which is free.</summary>
    private static bool CanMoveOver(Character mover, Character blocker)
    {
        if ((CharDefHelper.GetCanFlags(blocker) & CanFlags.C_Statue) != 0) return false;
        if (mover.PrivLevel >= PrivLevel.GM) return true;
        if (mover.IsDead || mover.IsStatFlag(StatFlag.Sleeping) || mover.IsStatFlag(StatFlag.Insubstantial))
            return true;
        if (blocker.IsStatFlag(StatFlag.Insubstantial)) return true;
        if (!mover.IsPlayer && !blocker.IsPlayer && !MovementEngine.NpcShoveNpc &&
            !(mover.TryGetTag("OVERRIDE.SHOVE", out string? ovr) &&
              ScriptNumber.TryParseToken(ovr, out long o) && o != 0))
            return false;

        bool osiLike = (Character.ActiveRevealFlags & RevealFlags.OsiLikePersonalSpace) != 0;
        bool concealed = blocker.IsStatFlag(StatFlag.Hidden) || blocker.IsStatFlag(StatFlag.Invisible);
        if (blocker.IsDead || (concealed && !osiLike))
            return true; // free push
        return mover.Stam >= mover.MaxStam && mover.Stam >= 10;
    }

    public static void Offset(Direction d, ref int x, ref int y)
    {
        switch (d)
        {
            case Direction.North: y--; break;
            case Direction.South: y++; break;
            case Direction.West: x--; break;
            case Direction.East: x++; break;
            case Direction.NorthEast: x++; y--; break;
            case Direction.SouthWest: x--; y++; break;
            case Direction.SouthEast: x++; y++; break;
            case Direction.NorthWest: x--; y--; break;
        }
    }
}
