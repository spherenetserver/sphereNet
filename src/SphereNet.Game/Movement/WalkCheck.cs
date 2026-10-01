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
/// (<see cref="SourceXWalk"/>: CheckValidMove / CanStandAt / GetHeightPoint),
/// and so are the standing-surface resolver used to seat a character (login,
/// mount, teleport) and the headroom check (IsVerticalSpace).
/// </summary>
public sealed class WalkCheck
{
    internal const int PersonHeight = 16;

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
    /// <paramref name="standZ"/> at (x, y) for this character - its own HEIGHT, 4 more
    /// when riding or hovering - and, with a non-zero <paramref name="extra"/>, the 4
    /// of a mount about to be climbed onto (fForceMount)? True when no ceiling
    /// blocks, or for a GM in GM mode / ALLMOVE. The ceiling comes from the same
    /// blocking-state geometry a step uses, so a script HEIGHT is honoured (it was a
    /// fixed 16 here).</summary>
    public bool HasVerticalSpace(Character mover, int mapId, int x, int y, int standZ, int extra) =>
        _sourceX.IsVerticalSpace(mover, mapId, x, y, standZ, forceMount: extra != 0);

    /// <summary>The floor flags under a collision-free step (GM ALLMOVE / PASSWALLS)
    /// - see <see cref="SourceXWalk.FloorFlagsAt"/>.</summary>
    internal bool StandsOnRoof(Character mover, int mapId, int x, int y, int z) =>
        SourceXWalk.IsRoofFloor(_sourceX.FloorFlagsAt(mover, mapId, x, y, z));

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
        int FwdMobileCount, string FwdMobileDump)
    {
        /// <summary>The accepted destination's floor carries CAN_I_ROOF - what
        /// CanMoveWalkTo turns into STATF_INDOORS (CCharAct.cpp:4831).</summary>
        public bool ForwardOnRoof { get; init; }
    }

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
        out int newZ, out Diagnostic diag) =>
        CheckMovementDetailed(mover, loc, d, checkChars: true, out newZ, out diag);

    /// <summary>As above. With <paramref name="checkChars"/> false the characters on
    /// the destination are left to the caller: a real walking step runs Source-X's
    /// ShoveCharAtPosition itself, AFTER CheckValidMove, so @PersonalSpace and
    /// @charShove get to change the cost and the full-stamina rule before either is
    /// judged (CanMoveWalkTo, CCharAct.cpp:4763-4768 -> :4641-4668). Judging the
    /// default rule here first refused the step before the scripts ever ran.</summary>
    public bool CheckMovementDetailed(Character mover, Point3D loc, Direction d, bool checkChars,
        out int newZ, out Diagnostic diag)
    {
        newZ = loc.Z;
        diag = default;
        var md = _world.MapData;
        if (md == null) return false;

        var can = CharDefHelper.GetCanFlags(mover);
        if (!mover.IsGmMode && ((can & (CanFlags.C_NonMover | CanFlags.C_Statue)) != 0 ||
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
            pathFinding: false, out newZ, out string fwdReason, out uint fwdFloorFlags);
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
        if (moveOk && checkChars)
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
            mobsForward.Count, mobDump.ToString())
        {
            ForwardOnRoof = moveOk && SourceXWalk.IsRoofFloor(fwdFloorFlags),
        };
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

    // -----------------------------------------------------------------
    //  Helpers — tile / mobile collection and direction offsets.
    // -----------------------------------------------------------------

    /// <summary>Resolves the committed custom-house design tiles for a
    /// MultiCustom item — wired by the host to CustomHousingEngine's cached
    /// design lookup. Unset → custom designs contribute no walk geometry.</summary>
    public static Func<Item, IReadOnlyList<HouseDesignTile>>? ResolveCustomDesign;

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
    internal static bool CanMoveOver(Character mover, Character blocker)
    {
        if ((CharDefHelper.GetCanFlags(blocker) & CanFlags.C_Statue) != 0) return false;
        if (mover.IsGmMode) return true;
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
