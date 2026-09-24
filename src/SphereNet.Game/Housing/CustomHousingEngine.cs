using System.Linq;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Housing;

/// <summary>An active house-customization session (one per designing character).
/// The working design lives only in the session; it becomes the committed
/// design (DESIGN_n tags) on Commit and is discarded on Revert/exit.</summary>
public sealed class HouseDesignSession
{
    public required Serial HouseUid { get; init; }
    public required HouseDesign Working { get; set; }
    public HouseDesign? Backup { get; set; }
    /// <summary>Current story being edited (1-based). Build places at this
    /// story's floor Z.</summary>
    public int Level { get; set; } = 1;
}

/// <summary>
/// Custom-house design state machine behind the 0xD7 encoded commands.
/// Maps to the design-state handling in Source-X CItemMultiCustom; packet
/// bridging (0xD8 stream, 0xBF 0x20 mode switch) stays in GameClient.
/// </summary>
public sealed class CustomHousingEngine
{
    private readonly GameWorld _world;
    private readonly HousingEngine _housing;
    private readonly Dictionary<Serial, HouseDesignSession> _sessions = [];
    private readonly Dictionary<Serial, Serial> _architects = [];

    // Committed-design cache keyed by DESIGN_REVISION. Concurrent because
    // WalkCheck consults it from the parallel NPC-pathfinding stage too
    // (see the GameWorld threading contract).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Serial, (uint Revision, HouseDesign Design)> _committedCache = new();

    /// <summary>Story height in Z units; story 1 floor sits at Z=7
    /// (client plane transform: z = (plane-1)%4*20+7).</summary>
    public const int StoryHeight = 20;
    public const int MaxLevel = 4;

    public CustomHousingEngine(GameWorld world, HousingEngine housing)
    {
        _world = world;
        _housing = housing;
    }

    public static sbyte LevelToZ(int level) => (sbyte)(((Math.Clamp(level, 1, MaxLevel) - 1) % 4) * StoryHeight + 7);

    /// <summary>
    /// Committed design tiles for a multi, cached and invalidated by the
    /// DESIGN_REVISION tag — Commit bumps the revision, so the next lookup
    /// reparses the tags. Used by WalkCheck (via ResolveCustomDesign) to turn
    /// the design into virtual walk geometry.
    /// </summary>
    public IReadOnlyList<HouseDesignTile> GetCommittedTiles(Item multi) =>
        GetCommittedDesign(multi).Tiles;

    /// <summary>Committed design with Source-X fixture visibility applied:
    /// tiles that Commit materialized as real items (doors/containers) are
    /// marked invisible so render (0xD8), walk and LOS don't double them.
    /// Gated on the COMMIT_FIXTURES tag — a legacy design committed before
    /// fixture materialization existed has no real door items, so its door
    /// tiles must stay visible until the next commit.</summary>
    public HouseDesign GetCommittedDesign(Item multi)
    {
        uint revision = 0;
        if (multi.TryGetTag(HouseDesign.RevisionTag, out string? revStr))
            uint.TryParse(revStr, out revision);

        if (_committedCache.TryGetValue(multi.Uid, out var cached) && cached.Revision == revision)
            return cached.Design;

        var design = HouseDesign.LoadFromTags(multi);
        if (multi.TryGetTag(FixturesTag, out string? fixtures) && !string.IsNullOrEmpty(fixtures))
        {
            for (int i = 0; i < design.Tiles.Count; i++)
                if (IsFixtureTile(design.Tiles[i].TileId, out _))
                    design.Tiles[i] = design.Tiles[i] with { Visible = false };
        }
        _committedCache[multi.Uid] = (revision, design);
        return design;
    }

    public HouseDesignSession? GetSession(Serial charUid) => _sessions.GetValueOrDefault(charUid);

    public bool IsSessionAuthorized(Character ch) =>
        TryGetAuthorizedSession(ch, out _, out _);

    /// <summary>Resolve the multi item of an active session.</summary>
    public Item? GetSessionMulti(Serial charUid) =>
        _sessions.TryGetValue(charUid, out var s) ? _world.FindItem(s.HouseUid) : null;

    /// <summary>True if the character may customize this house (owner only,
    /// or GM via the caller's own priv check).</summary>
    public bool CanCustomize(Character ch, Item multi)
    {
        var house = _housing.GetHouse(multi.Uid);
        if (house == null)
            return false;
        return house.Owner == ch.Uid;
    }

    /// <summary>Start (or restart) a design session from the committed design.</summary>
    public HouseDesignSession Begin(Character ch, Item multi)
    {
        if (_architects.TryGetValue(multi.Uid, out var previousArchitect) && previousArchitect != ch.Uid)
            _sessions.Remove(previousArchitect);
        if (_sessions.TryGetValue(ch.Uid, out var previousSession))
            _architects.Remove(previousSession.HouseUid);
        var session = new HouseDesignSession
        {
            HouseUid = multi.Uid,
            Working = HouseDesign.LoadFromTags(multi),
        };
        _sessions[ch.Uid] = session;
        _architects[multi.Uid] = ch.Uid;
        return session;
    }

    /// <summary>Source-X CItemMultiCustom::AddItem (CItemMultiCustom.cpp:438): at the
    /// current floor's height, or at ground level one row south of the design area
    /// (the front step).</summary>
    public bool Build(Character ch, ushort tileId, int x, int y)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi) ||
            !IsPlaceableTile(ch, tileId) ||
            !FitsDesignArea(session, x, y, allowSouthEdge: true))
            return false;
        sbyte z = IsSouthOfDesignArea(session, y) ? (sbyte)0 : LevelToZ(session.Level);
        return AddTile(session, multi, tileId, x, y, z, 0);
    }

    /// <summary>Source-X AddStairs (CItemMultiCustom.cpp:596): the id is a staircase
    /// MULTI; each of its visible pieces is added at the current floor's height plus
    /// its own offset, all tagged with one new staircase id so they come off together.
    /// It was a single static tile at Z 0, so no staircase could be walked.</summary>
    public bool Stairs(Character ch, ushort multiId, int x, int y)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi) ||
            !HouseDesignValidItems.IsValidStairMulti(multiId, ch.PrivLevel >= SphereNet.Core.Enums.PrivLevel.GM) ||
            !FitsDesignArea(session, x, y, allowSouthEdge: true))
            return false;
        var stairDef = _housing.MultiDefs.Get(multiId);
        if (stairDef == null || stairDef.Components.Count == 0)
            return false;

        ushort stairId = 0;
        foreach (var t in session.Working.Tiles)
            if (t.StairId > stairId) stairId = t.StairId;
        stairId++;

        sbyte baseZ = LevelToZ(session.Level);
        bool any = false;
        foreach (var comp in stairDef.Components)
        {
            if (!comp.Visible) continue;
            int px = x + comp.DeltaX, py = y + comp.DeltaY, pz = baseZ + comp.DeltaZ;
            if (!FitsOffset(px, py) || pz is < sbyte.MinValue or > sbyte.MaxValue) continue;
            any |= AddTile(session, multi, comp.TileId, px, py, (sbyte)pz, stairId);
        }
        return any;
    }

    public bool Roof(Character ch, ushort tileId, int x, int y, int z)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi) ||
            !IsPlaceableTile(ch, tileId) ||
            !FitsDesignArea(session, x, y, allowSouthEdge: false))
            return false;
        return AddTile(session, multi, tileId, x, y, (sbyte)Math.Clamp(z, sbyte.MinValue, sbyte.MaxValue), 0);
    }

    /// <summary>Source-X GetPlane (CItemMultiCustom.cpp:1878).</summary>
    public static int GetPlane(int z) => z >= 67 ? 4 : z >= 47 ? 3 : z >= 27 ? 2 : z >= 7 ? 1 : 0;

    /// <summary>ITEMID_DIRT_TILE: the ground a first-floor floor tile is replaced with.</summary>
    public const ushort DirtTile = 0x31F4;

    /// <summary>Source-X CItemMultiCustom::IsValidItem gate — the designer may
    /// only place real static design pieces; GMs bypass the whitelist but not
    /// the graphic-range check.</summary>
    private static bool IsPlaceableTile(Character ch, ushort tileId) =>
        HouseDesignValidItems.IsValidBuildTile(
            tileId, ch.PrivLevel >= SphereNet.Core.Enums.PrivLevel.GM);

    /// <summary>Source-X CItemMultiCustom::RemoveItem (CItemMultiCustom.cpp:698) for a
    /// designing client: the dirt of the first floor cannot be removed, at ground
    /// level only the south edge (the stairs) can, a staircase piece takes its whole
    /// staircase with it, and a first-floor floor tile leaves dirt behind.</summary>
    public bool Erase(Character ch, ushort tileId, int x, int y, int z)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out _))
            return false;
        int plane = GetPlane(z);
        if (plane == 1 && tileId == DirtTile)
            return false;
        if (plane == 0 && !IsSouthOfDesignArea(session, y))
            return false;

        var tiles = session.Working.Tiles;
        for (int i = tiles.Count - 1; i >= 0; i--)
        {
            var t = tiles[i];
            if (t.TileId != tileId || t.X != x || t.Y != y || t.Z != z)
                continue;

            var removed = new List<HouseDesignTile>();
            if (t.StairId != 0)
            {
                removed.AddRange(tiles.Where(o => o.StairId == t.StairId));
                tiles.RemoveAll(o => o.StairId == t.StairId);
            }
            else
            {
                removed.Add(t);
                tiles.RemoveAt(i);
            }
            foreach (var r in removed)
                if (r.TileId != DirtTile && IsFloorTile(r.TileId) && GetPlane(r.Z) == 1 && LevelToZ(1) == r.Z)
                    tiles.Add(new HouseDesignTile(DirtTile, r.X, r.Y, r.Z));
            session.Working.Revision++;
            return true;
        }
        return false;
    }

    public void SetLevel(Character ch, int level)
    {
        if (TryGetAuthorizedSession(ch, out var session, out _))
            session.Level = Math.Clamp(level, 1, MaxLevel);
    }

    public void Clear(Character ch)
    {
        if (TryGetAuthorizedSession(ch, out var session, out _))
        {
            session.Working.Tiles.Clear();
            session.Working.Revision++;
        }
    }

    public void BackupDesign(Character ch)
    {
        if (TryGetAuthorizedSession(ch, out var session, out _))
            session.Backup = session.Working.Clone();
    }

    public void RestoreDesign(Character ch)
    {
        if (TryGetAuthorizedSession(ch, out var session, out _) && session.Backup != null)
            session.Working = session.Backup.Clone();
    }

    /// <summary>Discard working changes — reload from the committed design.</summary>
    public void Revert(Character ch)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return;
        session.Working = HouseDesign.LoadFromTags(multi);
    }

    /// <summary>Persist the working design as the new committed design and end
    /// the session. Returns the new revision, or null without a session.</summary>
    /// <summary>What @HouseDesignCommit is told, read off the two designs BEFORE
    /// either is touched (Source-X CItemMultiCustom::CommitChanges:314-324).</summary>
    public readonly record struct CommitPreview(
        Item Multi, int OldTiles, int NewTiles, uint Revision,
        int OldFixtures, int NewFixtures, int MaxZ);

    /// <summary>The numbers a script needs to price or refuse a commit. Null when
    /// the character has no authorized session, or when the working design is
    /// unchanged — the reference returns early rather than commit a no-op (:277),
    /// so nobody is charged for pressing the button twice on the same design.
    ///
    /// The reference tests that with the revision because ITS working design
    /// carries its own revision that every edit moves. Here the revision is bumped
    /// by Commit itself, so an untouched session still reads equal to the committed
    /// one; the tiles are what actually says whether anything changed.</summary>
    public CommitPreview? PreviewCommit(Character ch)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return null;

        var committed = GetCommittedDesign(multi);
        if (session.Working.Tiles.Count == committed.Tiles.Count &&
            session.Working.Tiles.SequenceEqual(committed.Tiles))
            return null;

        int maxZ = 0;
        foreach (var tile in session.Working.Tiles)
            if (tile.Z > maxZ) maxZ = tile.Z;

        return new CommitPreview(multi, committed.Tiles.Count, session.Working.Tiles.Count,
            session.Working.Revision + 1, CountFixtures(committed), CountFixtures(session.Working),
            maxZ);
    }

    /// <summary>Source-X GetFixtureCount — how many of a design's tiles become real
    /// items on commit.</summary>
    public int CountFixtures(HouseDesign design)
    {
        int count = 0;
        foreach (var tile in design.Tiles)
            if (IsFixtureTile(tile.TileId, out _))
                count++;
        return count;
    }

    /// <summary>Source-X @HouseDesignCommitItem (CommitChanges, CItemMultiCustom.cpp:
    /// 284-305): asked once per piece before the commit, with LOCAL.ID, P.X, P.Y,
    /// P.Z and VISIBLE; an explicit RETURN 0 leaves the piece out.</summary>
    public static Func<Character, Item, HouseDesignTile, bool>? KeepCommitItem { get; set; }

    public uint? Commit(Character ch)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return null;

        if (KeepCommitItem != null)
            session.Working.Tiles.RemoveAll(t => !KeepCommitItem(ch, multi, t));

        session.Working.Revision++;
        session.Working.SaveToTags(multi);
        MaterializeFixtures(multi, session.Working);
        End(ch);
        return session.Working.Revision;
    }

    private const string FixturesTag = "COMMIT_FIXTURES";

    /// <summary>
    /// Source-X CItemMultiCustom::CommitChanges: INTERACTIVE design tiles —
    /// doors and containers — become REAL items on commit so they actually
    /// open, close and hold contents; walls/floors stay virtual render+walk
    /// geometry. Fixtures from the previous commit are replaced wholesale
    /// (tracked in the COMMIT_FIXTURES tag).
    /// </summary>
    private void MaterializeFixtures(Item multi, HouseDesign design)
    {
        // Tear down the previous commit's fixtures.
        if (multi.TryGetTag(FixturesTag, out string? prev) && !string.IsNullOrEmpty(prev))
        {
            foreach (var part in prev.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!uint.TryParse(part, out uint oldUid)) continue;
                var old = _world.FindItem(new Serial(oldUid));
                if (old != null && !old.IsDeleted)
                {
                    foreach (var content in old.Contents.ToList())
                    {
                        old.RemoveItem(content);
                        _world.PlaceItem(content, old.Position);
                    }
                    _world.RemoveItem(old);
                }
                _housing.GetHouse(multi.Uid)?.RemoveComponent(new Serial(oldUid));
            }
        }

        var house = _housing.GetHouse(multi.Uid);
        var created = new List<string>();
        foreach (var tile in design.Tiles)
        {
            if (!IsFixtureTile(tile.TileId, out bool isDoor))
                continue;

            var fixture = _world.CreateItem();
            fixture.BaseId = tile.TileId;
            fixture.ItemType = isDoor
                ? SphereNet.Core.Enums.ItemType.Door
                : SphereNet.Core.Enums.ItemType.Container;
            fixture.SetAttr(SphereNet.Core.Enums.ObjAttributes.Move_Never);
            fixture.Link = multi.Uid; // house key opens a door fixture
            fixture.SetTag("FIXTURE", multi.Uid.Value.ToString());
            _world.PlaceItem(fixture, new Core.Types.Point3D(
                (short)(multi.X + tile.X), (short)(multi.Y + tile.Y),
                (sbyte)(multi.Position.Z + tile.Z), multi.MapIndex));
            house?.AddComponent(fixture.Uid);
            created.Add(fixture.Uid.Value.ToString());
        }

        if (created.Count > 0)
            multi.SetTag(FixturesTag, string.Join(',', created));
        else
            multi.RemoveTag(FixturesTag);
    }

    /// <summary>Source-X CItemMultiCustom fixture test: interactive design
    /// tiles — doors and containers — that Commit replaces with real items.
    /// ITEMDEF TYPE first (works without map files), then the tiledata
    /// Door/Container flags.</summary>
    private bool IsFixtureTile(ushort tileId, out bool isDoor)
    {
        var md = _world.MapData;
        var defType = Definitions.DefinitionLoader.GetItemDef(tileId)?.Type;
        isDoor = defType is SphereNet.Core.Enums.ItemType.Door
                or SphereNet.Core.Enums.ItemType.DoorOpen
                or SphereNet.Core.Enums.ItemType.DoorLocked ||
            World.DoorHelper.IsDoorGraphic(md, tileId);
        if (isDoor)
            return true;
        return defType == SphereNet.Core.Enums.ItemType.Container ||
            (md != null &&
             (md.GetItemTileData(tileId).Flags & SphereNet.MapData.Tiles.TileFlag.Container) != 0);
    }

    /// <summary>End the session without committing (close/exit).</summary>
    public void End(Character ch)
    {
        if (!_sessions.Remove(ch.Uid, out var session)) return;
        if (_architects.TryGetValue(session.HouseUid, out var architect) && architect == ch.Uid)
            _architects.Remove(session.HouseUid);
    }

    private bool TryGetAuthorizedSession(Character ch, out HouseDesignSession session, out Item multi)
    {
        session = null!;
        multi = null!;
        if (!_sessions.TryGetValue(ch.Uid, out var found)) return false;
        var foundMulti = _world.FindItem(found.HouseUid);
        if (foundMulti == null || foundMulti.IsDeleted)
        {
            End(ch);
            return false;
        }
        var house = _housing.GetHouse(found.HouseUid);
        if (house != null && ch.PrivLevel < SphereNet.Core.Enums.PrivLevel.GM && house.Owner != ch.Uid)
        {
            End(ch);
            return false;
        }
        session = found;
        multi = foundMulti;
        return true;
    }

    private bool IsSouthOfDesignArea(HouseDesignSession session, int y)
    {
        var multi = _world.FindItem(session.HouseUid);
        var def = multi != null ? _housing.MultiDefs.Get(multi.BaseId) : null;
        return def != null && y > def.MaxY;
    }

    /// <summary>A floor tile: no height and the tile data's floor flag (UFLAG1_FLOOR).
    /// Floors and everything else share a square - a wall stands on a floor - so each
    /// only replaces its own kind.</summary>
    private bool IsFloorTile(ushort tileId)
    {
        var md = _world.MapData;
        if (md == null) return false;
        var data = md.GetItemTileData(tileId);
        return data.Height == 0 && (data.Flags & SphereNet.MapData.Tiles.TileFlag.Background) != 0;
    }

    private bool FitsDesignArea(HouseDesignSession session, int x, int y, bool allowSouthEdge)
    {
        if (!FitsOffset(x, y)) return false;
        var multi = _world.FindItem(session.HouseUid);
        var def = multi != null ? _housing.MultiDefs.Get(multi.BaseId) : null;
        if (def == null) return true;
        int maxY = def.MaxY + (allowSouthEdge ? 1 : 0);
        return x >= def.MinX && x <= def.MaxX && y >= def.MinY && y <= maxY;
    }

    private static bool FitsOffset(int x, int y) =>
        x is >= sbyte.MinValue and <= sbyte.MaxValue &&
        y is >= sbyte.MinValue and <= sbyte.MaxValue;

    private const int MaxDesignTiles = HouseDesign.MaxTiles;

    /// <summary>Source-X AddItem (CItemMultiCustom.cpp:525-594): a new piece replaces
    /// whatever of its own kind (floor or not) already stands on that square at that
    /// height - it used to stack on top - bumps the design revision, and moves the
    /// house's locked-down items standing there into the moving crate.</summary>
    private bool AddTile(HouseDesignSession session, Item multi, ushort tileId, int x, int y, sbyte z,
        ushort stairId)
    {
        var tile = new HouseDesignTile(tileId, (sbyte)x, (sbyte)y, z, StairId: stairId);
        var tiles = session.Working.Tiles;
        bool isFloor = IsFloorTile(tileId);
        tiles.RemoveAll(t => t.X == x && t.Y == y && t.Z == z && IsFloorTile(t.TileId) == isFloor);
        if (tiles.Count >= MaxDesignTiles)
            return false;
        tiles.Add(tile);
        session.Working.Revision++;
        MoveLockdownsToCrate(multi, x, y, z);
        return true;
    }

    /// <summary>Source-X GetLockdownsAt + UnlockItem into the moving crate: a locked-down
    /// item on the square a piece now occupies, on the same floor (CalculateLevel,
    /// CItemMultiCustom.cpp:1356). Upstream's secured-container pass walks the wrong
    /// list and never moves anything, so secure containers are left where they are.</summary>
    private void MoveLockdownsToCrate(Item multi, int x, int y, sbyte z)
    {
        var house = _housing.GetHouse(multi.Uid);
        if (house == null || house.Lockdowns.Count == 0) return;
        int wx = multi.X + x, wy = multi.Y + y;
        int floor = CalculateLevel(multi, multi.Position.Z + z);
        foreach (var uid in house.Lockdowns.ToArray())
        {
            var item = _world.FindItem(uid);
            if (item == null || item.IsDeleted || item.X != wx || item.Y != wy) continue;
            if (CalculateLevel(multi, item.Z) != floor) continue;
            house.ReleaseLockdown(uid, house.Owner);
            var crate = house.GetMovingCrate(create: true);
            var from = item.Position;
            if (crate == null || !crate.TryAddItem(item))
                continue;
            BroadcastRemove?.Invoke(item.Uid.Value, from);
        }
    }

    private static int CalculateLevel(Item multi, int z) => (z - multi.Position.Z - 6) / 20;

    /// <summary>Tell nearby clients an object left the world view.</summary>
    public static Action<uint, Core.Types.Point3D>? BroadcastRemove { get; set; }
}
