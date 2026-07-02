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
    public IReadOnlyList<HouseDesignTile> GetCommittedTiles(Item multi)
    {
        uint revision = 0;
        if (multi.TryGetTag(HouseDesign.RevisionTag, out string? revStr))
            uint.TryParse(revStr, out revision);

        if (_committedCache.TryGetValue(multi.Uid, out var cached) && cached.Revision == revision)
            return cached.Design.Tiles;

        var design = HouseDesign.LoadFromTags(multi);
        _committedCache[multi.Uid] = (revision, design);
        return design.Tiles;
    }

    public HouseDesignSession? GetSession(Serial charUid) => _sessions.GetValueOrDefault(charUid);

    /// <summary>Resolve the multi item of an active session.</summary>
    public Item? GetSessionMulti(Serial charUid) =>
        _sessions.TryGetValue(charUid, out var s) ? _world.FindItem(s.HouseUid) : null;

    /// <summary>True if the character may customize this house (owner/co-owner,
    /// or GM via the caller's own priv check).</summary>
    public bool CanCustomize(Character ch, Item multi)
    {
        var house = _housing.GetHouse(multi.Uid);
        if (house == null)
            return false;
        var priv = house.GetPriv(ch.Uid);
        return priv is HousePriv.Owner or HousePriv.CoOwner;
    }

    /// <summary>Start (or restart) a design session from the committed design.</summary>
    public HouseDesignSession Begin(Character ch, Item multi)
    {
        var session = new HouseDesignSession
        {
            HouseUid = multi.Uid,
            Working = HouseDesign.LoadFromTags(multi),
        };
        _sessions[ch.Uid] = session;
        return session;
    }

    public bool Build(Character ch, ushort tileId, int x, int y)
    {
        var session = GetSession(ch.Uid);
        if (session == null || !FitsOffset(x, y))
            return false;
        AddTile(session, tileId, x, y, LevelToZ(session.Level));
        return true;
    }

    /// <summary>Stairs are placed at ground level (Z=0) outside the plane grid.</summary>
    public bool Stairs(Character ch, ushort tileId, int x, int y)
    {
        var session = GetSession(ch.Uid);
        if (session == null || !FitsOffset(x, y))
            return false;
        AddTile(session, tileId, x, y, 0);
        return true;
    }

    public bool Roof(Character ch, ushort tileId, int x, int y, int z)
    {
        var session = GetSession(ch.Uid);
        if (session == null || !FitsOffset(x, y))
            return false;
        AddTile(session, tileId, x, y, (sbyte)Math.Clamp(z, sbyte.MinValue, sbyte.MaxValue));
        return true;
    }

    public bool Erase(Character ch, ushort tileId, int x, int y, int z)
    {
        var session = GetSession(ch.Uid);
        if (session == null)
            return false;
        var tiles = session.Working.Tiles;
        for (int i = tiles.Count - 1; i >= 0; i--)
        {
            var t = tiles[i];
            if (t.TileId == tileId && t.X == x && t.Y == y && t.Z == z)
            {
                tiles.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public void SetLevel(Character ch, int level)
    {
        var session = GetSession(ch.Uid);
        if (session != null)
            session.Level = Math.Clamp(level, 1, MaxLevel);
    }

    public void Clear(Character ch)
    {
        GetSession(ch.Uid)?.Working.Tiles.Clear();
    }

    public void BackupDesign(Character ch)
    {
        var session = GetSession(ch.Uid);
        if (session != null)
            session.Backup = session.Working.Clone();
    }

    public void RestoreDesign(Character ch)
    {
        var session = GetSession(ch.Uid);
        if (session?.Backup != null)
            session.Working = session.Backup.Clone();
    }

    /// <summary>Discard working changes — reload from the committed design.</summary>
    public void Revert(Character ch)
    {
        var session = GetSession(ch.Uid);
        var multi = GetSessionMulti(ch.Uid);
        if (session == null || multi == null)
            return;
        session.Working = HouseDesign.LoadFromTags(multi);
    }

    /// <summary>Persist the working design as the new committed design and end
    /// the session. Returns the new revision, or null without a session.</summary>
    public uint? Commit(Character ch)
    {
        var session = GetSession(ch.Uid);
        var multi = GetSessionMulti(ch.Uid);
        if (session == null || multi == null)
            return null;

        session.Working.Revision++;
        session.Working.SaveToTags(multi);
        MaterializeFixtures(multi, session.Working);
        _sessions.Remove(ch.Uid);
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
                    _world.RemoveItem(old);
            }
        }

        var house = _housing.GetHouse(multi.Uid);
        var created = new List<string>();
        var md = _world.MapData;
        foreach (var tile in design.Tiles)
        {
            // Interactive detection: ITEMDEF TYPE first (works without map
            // files), then the tiledata Door/Container flags.
            var defType = Definitions.DefinitionLoader.GetItemDef(tile.TileId)?.Type;
            bool isDoor = defType is SphereNet.Core.Enums.ItemType.Door
                    or SphereNet.Core.Enums.ItemType.DoorOpen
                    or SphereNet.Core.Enums.ItemType.DoorLocked ||
                World.DoorHelper.IsDoorGraphic(md, tile.TileId);
            bool isContainer = !isDoor &&
                (defType == SphereNet.Core.Enums.ItemType.Container ||
                 (md != null &&
                  (md.GetItemTileData(tile.TileId).Flags & SphereNet.MapData.Tiles.TileFlag.Container) != 0));
            if (!isDoor && !isContainer)
                continue;

            var fixture = _world.CreateItem();
            fixture.BaseId = tile.TileId;
            fixture.ItemType = isDoor
                ? SphereNet.Core.Enums.ItemType.Door
                : SphereNet.Core.Enums.ItemType.Container;
            fixture.SetAttr(SphereNet.Core.Enums.ObjAttributes.Move_Never);
            fixture.Link = multi.Uid; // house key opens a door fixture
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

    /// <summary>End the session without committing (close/exit).</summary>
    public void End(Character ch) => _sessions.Remove(ch.Uid);

    private static bool FitsOffset(int x, int y) =>
        x is >= sbyte.MinValue and <= sbyte.MaxValue &&
        y is >= sbyte.MinValue and <= sbyte.MaxValue;

    private static void AddTile(HouseDesignSession session, ushort tileId, int x, int y, sbyte z)
    {
        // Replace an identical-position tile of the same graphic instead of
        // stacking duplicates (repeated client clicks).
        var tile = new HouseDesignTile(tileId, (sbyte)x, (sbyte)y, z);
        if (!session.Working.Tiles.Contains(tile))
            session.Working.Tiles.Add(tile);
    }
}
