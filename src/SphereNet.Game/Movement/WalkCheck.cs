using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Map;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.Movement;

/// <summary>
/// Per-step walk validator. For a given character and direction, walks the
/// full surface stack at both the start and target tiles — terrain, static
/// tiles, in-world items — and picks the single walkable Z that a 16-tall
/// character can stand on without head-butting anything above. Replaces an
/// earlier single-Z IsPassable + fixed MaxClimbHeight check that did not
/// model stair / slope / bridge / multi-surface cases.
///
/// Algorithm structure is modelled on the open-source reference
/// implementation in ServUO's MovementImpl (with credit to its authors);
/// the C# here is an independent reimplementation against our own map-data
/// readers and game objects.
/// </summary>
public sealed class WalkCheck
{
    private const int PersonHeight = 16;
    private const int StepHeight = 2;

    private const TileFlag ImpassableSurface = TileFlag.Impassable | TileFlag.Surface;

    /// <summary>
    /// Maximum Z a mover may DROP in a single step. Beyond this the step is
    /// rejected so the mover stops at the edge instead of plunging — e.g. the
    /// cliff/beach tiles whose corners span ~25 units, where the walkable centre
    /// Z sits 13+ below the mover. The 2D/UO client blocks such cliff drops
    /// locally, so allowing them server-side desynced the two and the running
    /// player teleported down then snapped back. Tuned to clear ordinary stair
    /// steps and slopes (≤ ~2 stair risers) while blocking true cliff faces.
    /// Climbing UP is unaffected (handled by the maxZ window).
    /// </summary>
    public static int MaxDescendZ { get; set; } = 11;

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

    private readonly GameWorld _world;

    public WalkCheck(GameWorld world)
    {
        _world = world;
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

    /// <summary>Entry point. Returns true if <paramref name="mover"/> can step
    /// in direction <paramref name="d"/> from <paramref name="loc"/>, and sets
    /// <paramref name="newZ"/> to the Z the mover should land on. Matches
    /// ServUO MovementImpl.CheckMovement semantics including diagonal
    /// double-edge check.</summary>
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

        var itemsStart = CollectItems(mapId, xStart, yStart);
        var itemsForward = CollectItems(mapId, xForward, yForward);
        var itemsLeft = checkDiagonals ? CollectItems(mapId, xLeft, yLeft) : null;
        var itemsRight = checkDiagonals ? CollectItems(mapId, xRight, yRight) : null;

        var mobsForward = CollectMobiles(mapId, xForward, yForward, mover);

        GetStartZ(mover, md, mapId, xStart, yStart, loc.Z, itemsStart, out int startZ, out int startTop);

        // Pre-capture raw tile inventory at the forward tile for diagnostics
        // — total statics, count of impassable ones, and a compact dump of
        // every static's (id,z,h,flags). This lets the reject log show what
        // is actually on the tile beyond the Surface-only candidate count.
        int fwdImpassable = 0;
        int fwdStaticCount = 0;
        var dump = new System.Text.StringBuilder();
        md.ForEachStatic(mapId, xForward, yForward, s =>
        {
            fwdStaticCount++;
            var sd = md.GetItemTileData(s.TileId);
            if (sd.IsImpassable) fwdImpassable++;
            if (dump.Length > 0) dump.Append(',');
            // Flag değerini ve ilk 12 karakter adı da yaz — böylece tile'ın
            // gerçekten ne olduğunu (dekoratif mi, yüzey mi, yanlış flag'li
            // mi) ayırt edebiliriz.
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

        CalculateMinMaxZ(md, mapId, xStart, yStart, loc.Z, (int)d, itemsStart,
            out int fwdMinZ, out int fwdMaxZ);

        var fwdTrace = new CheckTrace();
        bool forwardOk = Check(mover, md, mapId, xForward, yForward, startTop, startZ, loc.Z,
            fwdMinZ, fwdMaxZ, itemsForward, mobsForward, (int)d, out newZ, ref fwdTrace);
        int forwardNewZ = newZ;
        bool moveOk = forwardOk;
        bool mobBlocked = false;

        // If the forward tile alone passed the surface check but a mob
        // blocker flipped it to false, we know that was the cause.
        if (!forwardOk && mobsForward.Count > 0)
        {
            var noMobTrace = new CheckTrace();
            bool surfaceWithoutMob = Check(mover, md, mapId, xForward, yForward, startTop, startZ,
                loc.Z, fwdMinZ, fwdMaxZ, itemsForward, null, (int)d, out _, ref noMobTrace);
            if (surfaceWithoutMob) mobBlocked = true;
        }

        bool leftOk = false, rightOk = false;
        if (moveOk && checkDiagonals)
        {
            // ServUO rule: players (non-staff) need BOTH diagonal edges clear;
            // mobs / staff need only ONE. We apply the stricter rule to
            // everyone below GM so corners cannot be cut through walls.
            bool bothRequired = mover.PrivLevel < PrivLevel.GM;

            int leftDir = ((int)d - 1) & 0x7;
            int rightDir = ((int)d + 1) & 0x7;
            CalculateMinMaxZ(md, mapId, xStart, yStart, loc.Z, leftDir, itemsStart,
                out int leftMinZ, out int leftMaxZ);
            CalculateMinMaxZ(md, mapId, xStart, yStart, loc.Z, rightDir, itemsStart,
                out int rightMinZ, out int rightMaxZ);

            var leftTrace = new CheckTrace();
            var rightTrace = new CheckTrace();
            leftOk = Check(mover, md, mapId, xLeft, yLeft, startTop, startZ, loc.Z,
                leftMinZ, leftMaxZ, itemsLeft!, null, leftDir, out _, ref leftTrace);
            rightOk = Check(mover, md, mapId, xRight, yRight, startTop, startZ, loc.Z,
                rightMinZ, rightMaxZ, itemsRight!, null, rightDir, out _, ref rightTrace);

            moveOk = bothRequired ? (leftOk && rightOk) : (leftOk || rightOk);
        }

        if (!moveOk) newZ = startZ;

        diag = new Diagnostic(startZ, startTop, forwardOk, forwardNewZ,
            checkDiagonals, leftOk, rightOk, mobBlocked,
            fwdTrace.LandZ, fwdTrace.LandCenter, fwdTrace.LandTop,
            fwdTrace.LandBlocks, fwdTrace.ConsiderLand,
            fwdTrace.SurfaceCandidates, fwdTrace.ItemSurfaceCandidates,
            fwdTrace.LastReason,
            fwdStaticCount, fwdImpassable, fwdLandTile.TileId, dump.ToString(),
            mobsForward.Count, mobDump.ToString());
        return moveOk;
    }

    /// <summary>Per-tile trace captured during <see cref="Check"/> so the walk
    /// diagnostic can report why the forward tile rejected every candidate.</summary>
    private struct CheckTrace
    {
        public int LandZ, LandCenter, LandTop;
        public bool LandBlocks, ConsiderLand;
        public int SurfaceCandidates;       // static tiles with Surface flag
        public int ItemSurfaceCandidates;   // in-world items with Surface flag
        public string LastReason;
    }

    // -----------------------------------------------------------------
    //  CalculateMinMaxZ — ClassicUO-style pre-filter that establishes a
    //  vertical [minZ, maxZ+2] window from the SOURCE tile. Surfaces on
    //  the TARGET tile must be reachable within this window.
    // -----------------------------------------------------------------

    private void CalculateMinMaxZ(MapDataManager md, int mapId,
        int srcX, int srcY, int currentZ, int direction, List<Item> sourceItems,
        out int minZ, out int maxZ)
    {
        minZ = -128;
        maxZ = currentZ;

        var landTile = md.GetTerrainTile(mapId, srcX, srcY);
        if (!MapDataManager.IsLandIgnored(landTile.TileId))
        {
            var landData = md.GetLandTileData(landTile.TileId);
            bool landBlocks = LandBlocks(landData);

            if (!landBlocks)
            {
                int zNW = landTile.Z;
                md.GetAverageZ(mapId, srcX, srcY, out int landLow, out int landAvg, out int landHigh);
                bool isStretched = (landLow != landHigh);

                if (isStretched && landAvg <= currentZ)
                {
                    int dirZ = md.GetDirectionalLandZ(mapId, srcX, srcY, direction);
                    if (minZ < dirZ) minZ = dirZ;
                    if (maxZ < dirZ) maxZ = dirZ;
                }
                else if (!isStretched)
                {
                    if (landAvg <= currentZ && minZ < landAvg)
                        minZ = landAvg;
                    if (currentZ == landAvg)
                    {
                        if (maxZ < landAvg) maxZ = landAvg;
                        if (minZ > landLow) minZ = landLow;
                    }
                }
            }
        }

        var statics = md.GetStaticBlock(mapId, srcX, srcY, out int offX, out int offY);
        for (int i = 0; i < statics.Length; i++)
        {
            var s = statics[i];
            if (s.XOffset != offX || s.YOffset != offY) continue;
            var data = md.GetItemTileData(s.TileId);

            bool isDoorOpen = (data.Flags & TileFlag.Door) != 0 &&
                _world.IsMapStaticDoorOpen((byte)mapId, (short)srcX, (short)srcY, s.Z);
            bool effectiveImp = data.IsImpassable && !isDoorOpen;

            bool isImpOrSurf = effectiveImp || data.IsSurface;
            bool isBridge = !effectiveImp && data.IsBridge;

            int tileZ = s.Z;
            int avgZ = tileZ + (data.IsBridge ? data.Height / 2 : data.Height);

            if (isImpOrSurf && avgZ <= currentZ && minZ < avgZ)
                minZ = avgZ;

            if (isBridge && currentZ == avgZ)
            {
                int top = tileZ + data.Height;
                if (maxZ < top) maxZ = top;
                if (minZ > tileZ) minZ = tileZ;
            }
        }

        for (int i = 0; i < sourceItems.Count; i++)
        {
            var item = sourceItems[i];
            var data = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, data)) continue;

            bool isImpOrSurf = data.IsImpassable || data.IsSurface;
            bool isBridge = !data.IsImpassable && data.IsBridge;

            int itemZ = item.Z;
            int avgZ = itemZ + (data.IsBridge ? data.Height / 2 : data.Height);

            if (isImpOrSurf && avgZ <= currentZ && minZ < avgZ)
                minZ = avgZ;

            if (isBridge && currentZ == avgZ)
            {
                int top = itemZ + data.Height;
                if (maxZ < top) maxZ = top;
                if (minZ > itemZ) minZ = itemZ;
            }
        }

        maxZ += 2;
    }

    // -----------------------------------------------------------------
    //  Per-tile check — ClassicUO sorted-list algorithm (CalculateNewZ)
    //
    //  Builds a unified list of all geometry (land, statics, items),
    //  sorts by Z, and walks upward looking for headroom gaps ≥ 16
    //  between a blocker and the surfaces below it. Picks the surface
    //  closest to the mover's current Z. A sentinel at Z=128 ensures
    //  the topmost surface is always evaluated.
    //
    //  This replaces the earlier per-candidate IsOk approach to
    //  guarantee Z parity with the ClassicUO client.
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

    private bool Check(Character mover, MapDataManager md, int mapId, int x, int y,
        int startTop, int startZ, int moverZ, int preMinZ, int preMaxZ,
        List<Item> items, List<Character>? mobiles, int direction, out int newZ,
        ref CheckTrace trace)
    {
        newZ = 0;

        var landTile = md.GetTerrainTile(mapId, x, y);
        var landData = md.GetLandTileData(landTile.TileId);
        bool landBlocks = LandBlocks(landData);
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

            bool isDoorOpen = (data.Flags & TileFlag.Door) != 0 &&
                _world.IsMapStaticDoorOpen((byte)mapId, (short)x, (short)y, tile.Z);
            bool effectiveImpassable = data.IsImpassable && !isDoorOpen;

            PathFlags pf = PathFlags.None;
            if (effectiveImpassable || data.IsSurface)
                pf |= PathFlags.ImpSurf;
            if (!effectiveImpassable)
            {
                if (data.IsSurface) pf |= PathFlags.Surface;
                if (data.IsBridge) pf |= PathFlags.Bridge;
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

            PathFlags pf = PathFlags.None;
            if (data.IsImpassable || data.IsSurface)
                pf |= PathFlags.ImpSurf;
            if (!data.IsImpassable)
            {
                if (data.IsSurface) pf |= PathFlags.Surface;
                if (data.IsBridge) pf |= PathFlags.Bridge;
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
        list.Add(new PathEntry(PathFlags.ImpSurf, 128, 128, 128));

        int resultZ = -128;
        int minZ = preMinZ;
        int currentZ = -128;
        int bestDelta = 1_000_000;

        int z = moverZ;
        if (z < minZ) z = minZ;

        for (int i = 0; i < list.Count; i++)
        {
            var obj = list[i];
            if ((obj.Flags & PathFlags.ImpSurf) == 0) continue;

            int objZ = obj.Z;

            if (objZ - minZ >= PersonHeight)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    var cand = list[j];
                    if ((cand.Flags & (PathFlags.Surface | PathFlags.Bridge)) == 0)
                        continue;

                    int candAvg = cand.AverageZ;
                    if (candAvg < currentZ) continue;
                    if (objZ - candAvg < PersonHeight) continue;

                    bool maxOk = ((cand.Flags & PathFlags.Surface) != 0 && candAvg <= preMaxZ)
                              || ((cand.Flags & PathFlags.Bridge) != 0 && cand.Z <= preMaxZ);
                    if (!maxOk) continue;

                    int delta = Math.Abs(z - candAvg);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        resultZ = candAvg;
                    }
                }
            }

            int avgZ2 = obj.AverageZ;
            if (minZ < avgZ2) minZ = avgZ2;
            if (currentZ < avgZ2) currentZ = avgZ2;
        }

        bool moveIsOk = resultZ != -128;

        // Reject a single-step drop steeper than MaxDescendZ (cliff edge). The
        // client blocks these locally; matching it here stops the mover at the
        // top instead of teleporting down onto the cliff/beach centre Z.
        if (moveIsOk && moverZ - resultZ > MaxDescendZ)
        {
            trace.LastReason = $"descent_too_steep drop={moverZ - resultZ} ourZ={resultZ}";
            moveIsOk = false;
        }

        if (moveIsOk)
        {
            newZ = resultZ;
            trace.LastReason = $"accepted ourZ={resultZ}";
        }

        // --- Mobile blocking ---
        if (moveIsOk && mobiles != null)
        {
            for (int i = 0; moveIsOk && i < mobiles.Count; i++)
            {
                var mob = mobiles[i];
                if (mob == mover) continue;
                if ((mob.Z + 15) > newZ && (newZ + 15) > mob.Z && !CanMoveOver(mover, mob))
                    moveIsOk = false;
            }
        }

        return moveIsOk;
    }

    /// <summary>Source tile Z baseline — what does "standing here" mean? Walks
    /// the surface stack at <paramref name="loc"/> and picks the highest
    /// surface that the mover's feet rest on.</summary>
    private void GetStartZ(Character mover, MapDataManager md, int mapId,
        int x, int y, int locZ, List<Item> itemList, out int zLow, out int zTop)
    {
        var landTile = md.GetTerrainTile(mapId, x, y);
        var landData = md.GetLandTileData(landTile.TileId);
        // Same land-barrier rule as Check() — see LandBlocks().
        bool landBlocks = LandBlocks(landData);
        bool considerLand = !MapDataManager.IsLandIgnored(landTile.TileId);

        md.GetAverageZ(mapId, x, y, out int landZ, out int landCenter, out int landTopAvg);

        int zCenter = 0;
        zLow = 0;
        zTop = 0;
        bool isSet = false;

        if (considerLand && !landBlocks && locZ >= landCenter)
        {
            zLow = landZ;
            zCenter = landCenter;
            if (!isSet || landTopAvg > zTop) zTop = landTopAvg;
            isSet = true;
        }

        var staticTiles = md.GetStaticBlock(mapId, x, y, out int staticOffX, out int staticOffY);
        for (int i = 0; i < staticTiles.Length; i++)
        {
            var tile = staticTiles[i];
            if (tile.XOffset != staticOffX || tile.YOffset != staticOffY)
                continue;
            var id = md.GetItemTileData(tile.TileId);
            int calcTop = tile.Z + id.CalcHeight;

            if ((!isSet || calcTop >= zCenter) && id.IsSurface && locZ >= calcTop)
            {
                zLow = tile.Z;
                zCenter = calcTop;
                int top = tile.Z + id.Height;
                if (!isSet || top > zTop) zTop = top;
                isSet = true;
            }
        }

        for (int i = 0; i < itemList.Count; i++)
        {
            var item = itemList[i];
            var id = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, id)) continue;
            int calcTop = item.Z + id.CalcHeight;

            if ((!isSet || calcTop >= zCenter) && id.IsSurface && locZ >= calcTop)
            {
                zLow = item.Z;
                zCenter = calcTop;
                int top = item.Z + id.Height;
                if (!isSet || top > zTop) zTop = top;
                isSet = true;
            }
        }

        if (!isSet)
        {
            zLow = zTop = locZ;
        }
        else if (locZ > zTop)
        {
            zTop = locZ;
        }
    }

    private static bool ShouldTreatAsMovementGeometry(Item item, ItemTileData data)
    {
        // Only treat world items as movement geometry when they are meaningful
        // obstacles/floors. Small loose drops like reagents, weapons, bags,
        // etc. should not become collision just because tiledata carries a
        // Surface bit, while bulky/anchored objects still should.
        if (item.ItemType == ItemType.Corpse) return false;
        if (item.IsStaticBlock) return true;
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

    private void AddVirtualMultiComponents(int mapId, int x, int y, List<Item> list)
    {
        var md = _world.MapData;
        if (md == null)
            return;

        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var multi in _world.GetItemsInRange(pivot, 32))
        {
            if (multi.IsDeleted || multi.IsEquipped || !multi.IsOnGround)
                continue;
            if (multi.ItemType is not (ItemType.Multi or ItemType.MultiCustom or ItemType.MultiAddon or ItemType.Ship))
                continue;

            var def = md.GetMulti(multi.BaseId);
            if (def == null)
                continue;

            foreach (var comp in def.Components)
            {
                if (!comp.IsVisible)
                    continue;

                int compX = multi.X + comp.XOffset;
                int compY = multi.Y + comp.YOffset;
                if (compX != x || compY != y)
                    continue;

                var componentData = md.GetItemTileData(comp.TileId);
                if (!ShouldTreatAsVirtualMultiGeometry(componentData))
                    continue;

                var virtualItem = new Item
                {
                    BaseId = comp.TileId,
                    ItemType = ItemType.MultiAddon,
                    Position = new Point3D((short)x, (short)y, (sbyte)(multi.Z + comp.ZOffset), (byte)mapId)
                };
                list.Add(virtualItem);
            }
        }
    }

    private static bool ShouldTreatAsVirtualMultiGeometry(ItemTileData data) =>
        data.IsSurface || data.IsBridge || data.IsImpassable;

    private List<Character> CollectMobiles(int mapId, int x, int y, Character mover)
    {
        var list = new List<Character>();
        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var ch in _world.GetCharsInRange(pivot, 0))
        {
            if (ch == mover || ch.IsDeleted || ch.IsDead) continue;
            if (ch.X != x || ch.Y != y) continue;
            list.Add(ch);
        }
        return list;
    }

    private static bool CanMoveOver(Character mover, Character blocker)
    {
        // ServUO / RunUO-style shove rule (PURE predicate — no side effects):
        // - staff can always move through mobiles
        // - dead bodies / dead movers do not block
        // - hidden staff do not block
        // - players can shove only when at full stamina
        // The stamina cost + reveal for a real shove is applied once by
        // MovementEngine after the move commits; deducting it here as well made
        // the subsequent MovementEngine.CanShove check fail (full-stam gate),
        // so the player lost stamina yet never actually moved.
        if (mover.PrivLevel >= PrivLevel.Counsel) return true;
        if (blocker.IsDead) return true;
        if (mover.IsDead) return true;
        if ((blocker.IsStatFlag(StatFlag.Hidden) || blocker.IsStatFlag(StatFlag.Invisible))
            && blocker.PrivLevel >= PrivLevel.Counsel)
            return true;

        return mover.Stam == mover.MaxStam && mover.MaxStam > 0;
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
