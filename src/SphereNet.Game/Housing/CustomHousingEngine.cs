using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Housing;

/// <summary>What a script returned from @HouseDesignBegin: RETURN 1 and the
/// N1/N2/N3 values it left behind (Source-X CItemMultiCustom::BeginCustomize,
/// CItemMultiCustom.cpp:118-145).</summary>
public readonly record struct HouseDesignBeginResult(bool Cancel, long N1, long N2, long N3);

/// <summary>
/// The client half of a design session - Source-X keeps the designing CClient on
/// the building (m_pArchitect) and talks to it from every lifecycle step, whoever
/// started that step (the client itself, a script, a GM taking over, a logout or
/// the building going away). Implemented by GameClient.
/// </summary>
public interface IHouseDesignClient
{
    /// <summary>0xBF 0x20: design mode on (begin) or off.</summary>
    void SendHouseCustomizationMode(Item multi, bool begin);
    /// <summary>0xD8 with the working design (fixture tiles included).</summary>
    void SendWorkingHouseDesign(Item multi, HouseDesign design);
    /// <summary>0xD8 with the committed design (what everyone else sees).</summary>
    void SendCommittedHouseDesign(Item multi);
    /// <summary>Drop one object from the designer's view (addObjectRemove).</summary>
    void SendDesignerRemoveObject(Item item);
    /// <summary>The designer was moved / hidden by the engine; redraw them.</summary>
    void SendDesignerMoved();
    /// <summary>@HouseDesignBegin with N1=1, N2=0, N3=2. Null when no script
    /// hooks the trigger - the reference then skips the N1/N2/N3 actions too.</summary>
    HouseDesignBeginResult? FireHouseDesignBegin(Item multi, long n1, long n2, long n3);
    /// <summary>@HouseDesignExit, ARGN1 = forced. True on RETURN 1.</summary>
    bool FireHouseDesignExit(Item multi, bool forced);
    /// <summary>@HouseDesignCommit. True on RETURN 1 (refuse the commit).</summary>
    bool FireHouseDesignCommit(Item multi, CustomHousingEngine.CommitPreview preview);
}

/// <summary>The design state of one customizable building (Source-X
/// CItemMultiCustom m_designWorking / m_designBackup / m_designRevert plus
/// m_pArchitect). It belongs to the building, not to whoever designs it: a script
/// can edit and commit the working design with nobody in design mode.</summary>
public sealed class HouseDesignSession
{
    public required Serial HouseUid { get; init; }
    public required HouseDesign Working { get; set; }
    public HouseDesign? Backup { get; set; }
    /// <summary>The working design as it stood when design mode began (RevertChanges).</summary>
    public HouseDesign? Revert { get; set; }
    /// <summary>Current story being edited (1-based). Build places at this
    /// story's floor Z; the designer stands on it.</summary>
    public int Level { get; set; } = 1;
    /// <summary>The character in design mode, or Invalid.</summary>
    public Serial Architect { get; set; } = Serial.Invalid;
    /// <summary>The architect's client, when they have one.</summary>
    public IHouseDesignClient? Client { get; set; }
}

/// <summary>
/// Custom-house design state machine behind the 0xD7 encoded commands and the
/// CItemMultiCustom script verbs/properties. Maps to Source-X CItemMultiCustom;
/// packet framing stays in GameClient (IHouseDesignClient).
/// </summary>
public sealed class CustomHousingEngine
{
    private readonly GameWorld _world;
    private readonly HousingEngine _housing;
    // Design state per building, and which building each architect is designing.
    private readonly Dictionary<Serial, HouseDesignSession> _designs = [];
    private readonly Dictionary<Serial, Serial> _designing = [];

    // Committed-design cache keyed by DESIGN_REVISION. Concurrent because
    // WalkCheck consults it from the parallel NPC-pathfinding stage too
    // (see the GameWorld threading contract).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Serial, (uint Revision, HouseDesign Design)> _committedCache = new();

    /// <summary>Story height in Z units; story 1 floor sits at Z=7
    /// (client plane transform: z = (plane-1)%4*20+7).</summary>
    public const int StoryHeight = 20;
    public const int MaxLevel = 4;

    /// <summary>The engine the script layer (CItemMultiCustom verbs/properties on
    /// Item) talks to. Wired by the host; null leaves those reading the tags.</summary>
    public static CustomHousingEngine? Active { get; set; }

    /// <summary>Raised after every successful commit with the new revision, whoever
    /// committed - the host tells nearby clients (Source-X Update()).</summary>
    public Action<Item, uint>? DesignCommitted { get; set; }

    public CustomHousingEngine(GameWorld world, HousingEngine housing)
    {
        _world = world;
        _housing = housing;
        _world.ObjectDeleting += OnWorldObjectDeleting;
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
    /// tiles that Commit materialized as real items (doors/telepads) are
    /// marked invisible so render (0xD8), walk and LOS don't double them.
    /// Gated on the COMMIT_FIXTURES tag — a legacy design committed before
    /// fixture materialization existed has no real door items, so its door
    /// tiles must stay visible until the next commit.</summary>
    public HouseDesign GetCommittedDesign(Item multi)
    {
        uint revision = MainRevision(multi);

        if (_committedCache.TryGetValue(multi.Uid, out var cached) && cached.Revision == revision)
            return cached.Design;

        var design = HouseDesign.LoadFromTags(multi);
        if (multi.TryGetTag(FixturesTag, out string? fixtures) && !string.IsNullOrEmpty(fixtures))
        {
            for (int i = 0; i < design.Tiles.Count; i++)
                if (IsFixtureTile(design.Tiles[i].TileId))
                    design.Tiles[i] = design.Tiles[i] with { Visible = false };
        }
        _committedCache[multi.Uid] = (revision, design);
        return design;
    }

    private static uint MainRevision(Item multi)
    {
        uint revision = 0;
        if (multi.TryGetTag(HouseDesign.RevisionTag, out string? revStr))
            ScriptNumber.TryParseUInt(revStr, out revision);
        return revision;
    }

    /// <summary>The design session a character is the architect of, or null.</summary>
    public HouseDesignSession? GetSession(Serial charUid) =>
        _designing.TryGetValue(charUid, out var houseUid) &&
        _designs.TryGetValue(houseUid, out var s) && s.Architect == charUid ? s : null;

    /// <summary>The design state of a building, created from its committed design
    /// the first time anything asks (Source-X keeps working == main after load).</summary>
    public HouseDesignSession GetDesignState(Item multi)
    {
        if (_designs.TryGetValue(multi.Uid, out var state))
            return state;
        state = new HouseDesignSession { HouseUid = multi.Uid, Working = LoadMainAsWorking(multi) };
        _designs[multi.Uid] = state;
        return state;
    }

    /// <summary>The character designing this building (Source-X DESIGNER), or Invalid.</summary>
    public Serial GetDesigner(Item multi) =>
        _designs.TryGetValue(multi.Uid, out var s) ? s.Architect : Serial.Invalid;

    public bool IsSessionAuthorized(Character ch) =>
        TryGetAuthorizedSession(ch, out _, out _);

    /// <summary>Resolve the multi item of an active session.</summary>
    public Item? GetSessionMulti(Serial charUid) =>
        GetSession(charUid) is { } s ? _world.FindItem(s.HouseUid) : null;

    /// <summary>True if the character may customize this house (owner only,
    /// or GM via the caller's own priv check).</summary>
    public bool CanCustomize(Character ch, Item multi)
    {
        var house = _housing.GetHouse(multi.Uid);
        if (house == null)
            return false;
        return house.Owner == ch.Uid;
    }

    /// <summary>The committed design copied into a fresh working design: the
    /// committed tags with each piece's fixture visibility (Source-X components
    /// carry m_visible = !fixture).</summary>
    private HouseDesign LoadMainAsWorking(Item multi)
    {
        var design = HouseDesign.LoadFromTags(multi);
        design.Revision = MainRevision(multi);
        for (int i = 0; i < design.Tiles.Count; i++)
            if (IsFixtureTile(design.Tiles[i].TileId))
                design.Tiles[i] = design.Tiles[i] with { Visible = false };
        return design;
    }

    // ------------------------------------------------------------------
    //  Session lifecycle (BeginCustomize / EndCustomize / SwitchToLevel)
    // ------------------------------------------------------------------

    /// <summary>
    /// Source-X CItemMultiCustom::BeginCustomize (CItemMultiCustom.cpp:88): a
    /// building already being designed ends that session first (forced, so the old
    /// designer is told design mode is over); the committed design is copied to
    /// working and its revision advanced; @HouseDesignBegin may refuse, and its
    /// N1/N2/N3 pick the addon redeed, the lockdown/secure transfer and who is
    /// ejected; then the designer is hidden, moved to the building's centre 7 above
    /// its floor and the building's loose items are dropped from their view.
    /// Returns null when the trigger refused.
    /// </summary>
    public HouseDesignSession? Begin(Character ch, Item multi, IHouseDesignClient? client = null,
        bool continueCustomize = false)
    {
        var state = GetDesignState(multi);
        if (state.Architect.IsValid)
            EndCustomize(state, forced: true);

        if (!continueCustomize)
        {
            state.Working = LoadMainAsWorking(multi);
            state.Working.Revision++;
        }

        if (client?.FireHouseDesignBegin(multi, 1, 0, 2) is { } begin)
        {
            if (begin.Cancel)
                return null;
            var house = _housing.GetHouse(multi.Uid);
            if (begin.N1 == 1)
                RedeedAddons(multi);
            if (begin.N2 == 1 && house != null)
                TransferHoldingsToMovingCrate(house);
            if (begin.N3 == 1)
                EjectAll(multi, except: ch);
            else if (begin.N3 == 2)
                EjectAll(multi, except: null);
        }

        state.Revert = state.Working.Clone();
        state.Architect = ch.Uid;
        state.Client = client;
        state.Level = 1;
        _designing[ch.Uid] = multi.Uid;

        client?.SendHouseCustomizationMode(multi, begin: true);
        client?.SendWorkingHouseDesign(multi, state.Working);

        // Move the designer onto the building and hide them.
        ch.SetStatFlag(StatFlag.Hidden);
        if (multi.IsOnGround && !multi.IsDeleted)
            _world.MoveCharacter(ch, new Point3D(multi.X, multi.Y,
                (sbyte)Math.Clamp(multi.Z + 7, sbyte.MinValue, sbyte.MaxValue), multi.MapIndex));
        client?.SendDesignerMoved();

        // Hide every dynamic item inside the building from the designer.
        if (client != null && multi.IsOnGround)
        {
            int radius = Math.Max(1, GetDesignWidth(multi) / 2);
            foreach (var item in _world.GetItemsInRange(multi.Position, radius).ToList())
            {
                if (item == multi || item.IsDeleted || !item.IsOnGround)
                    continue;
                if (Math.Abs(item.X - multi.X) > radius || Math.Abs(item.Y - multi.Y) > radius)
                    continue;
                client.SendDesignerRemoveObject(item);
            }
        }
        return state;
    }

    /// <summary>End the character's design session. A normal exit
    /// (<paramref name="forced"/> false) can be refused by @HouseDesignExit RETURN 1,
    /// which puts the designer straight back into design mode; a forced exit
    /// (logout, building gone, another designer taking over) cannot.</summary>
    public void End(Character ch, bool forced = false)
    {
        if (_designing.TryGetValue(ch.Uid, out var houseUid) && _designs.TryGetValue(houseUid, out var state))
            EndCustomize(state, forced);
    }

    /// <summary>End whatever session this building has (script ENDCUSTOMIZE).</summary>
    public void EndHouse(Item multi, bool forced = true)
    {
        if (_designs.TryGetValue(multi.Uid, out var state))
            EndCustomize(state, forced);
    }

    /// <summary>Source-X CItemMultiCustom::EndCustomize (CItemMultiCustom.cpp:192):
    /// the mode-end packet first, then @HouseDesignExit (ARGN1 = forced; RETURN 1
    /// on a normal exit restarts design mode), then the designer is revealed,
    /// moved to the sign if still inside, and sent the committed design.</summary>
    private void EndCustomize(HouseDesignSession state, bool forced)
    {
        if (!state.Architect.IsValid)
            return;

        var architect = state.Architect;
        var client = state.Client;
        state.Architect = Serial.Invalid;
        state.Client = null;
        if (_designing.TryGetValue(architect, out var designing) && designing == state.HouseUid)
            _designing.Remove(architect);

        var multi = _world.FindItem(state.HouseUid);
        bool multiAlive = multi != null && !multi.IsDeleted;
        if (multi != null)
            client?.SendHouseCustomizationMode(multi, begin: false);

        var ch = _world.FindChar(architect);
        if (ch == null || ch.IsDeleted)
            return;

        if (client != null && multiAlive &&
            client.FireHouseDesignExit(multi!, forced) && !forced)
        {
            Begin(ch, multi!, client);
            return;
        }

        ch.ClearStatFlag(StatFlag.Hidden);

        if (multiAlive && IsInsideBuilding(multi!, ch.Position))
        {
            var dest = GetSignPoint(multi!);
            if (_world.MapData != null)
                dest = new Point3D(dest.X, dest.Y, _world.Terrain.GetEffectiveZ(dest.X, dest.Y, dest.Map, dest.Z), dest.Map);
            _world.MoveCharacter(ch, dest);
        }
        client?.SendDesignerMoved();

        if (multiAlive)
            client?.SendCommittedHouseDesign(multi!);
    }

    /// <summary>Source-X SwitchToLevel (CItemMultiCustom.cpp:254): the level is capped
    /// at the building's level count and the designer is lifted to that floor.</summary>
    public void SetLevel(Character ch, int level)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return;
        // The level arrives as a dword narrowed to uchar and is only capped above.
        session.Level = Math.Min((byte)level, GetLevelCount(multi));
        if (multi.IsOnGround)
        {
            int z = multi.Z + GetPlaneZ(session.Level);
            _world.MoveCharacter(ch, new Point3D(ch.X, ch.Y, (sbyte)Math.Clamp(z, sbyte.MinValue, sbyte.MaxValue), ch.MapIndex));
        }
        session.Client?.SendDesignerMoved();
    }

    /// <summary>Source-X GetLevelCount (CItemMultiCustom.cpp:1092): a foundation 14
    /// or more tiles across has four levels (roof included), a smaller one three.</summary>
    public int GetLevelCount(Item multi)
    {
        var def = _housing.MultiDefs.Get(multi.BaseId);
        if (def == null)
            return MaxLevel;
        var (minX, minY, maxX, maxY) = GetDesignBounds(def);
        return (maxX - minX + 1 >= 14 || maxY - minY + 1 >= 14) ? 4 : 3;
    }

    /// <summary>Source-X GetDesignArea: the visible foundation pieces' extent,
    /// always including the anchor.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) GetDesignBounds(MultiDef def)
    {
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var c in def.Components)
        {
            if (!c.Visible) continue;
            minX = Math.Min(minX, c.DeltaX); maxX = Math.Max(maxX, c.DeltaX);
            minY = Math.Min(minY, c.DeltaY); maxY = Math.Max(maxY, c.DeltaY);
        }
        return (minX, minY, maxX, maxY);
    }

    private int GetDesignWidth(Item multi)
    {
        var def = _housing.MultiDefs.Get(multi.BaseId);
        if (def == null) return 1;
        var (minX, _, maxX, _) = GetDesignBounds(def);
        return maxX - minX + 1;
    }

    private bool IsInsideBuilding(Item multi, Point3D pos)
    {
        if (pos.Map != multi.MapIndex) return false;
        var def = _housing.MultiDefs.Get(multi.BaseId);
        if (def == null)
            return pos.X == multi.X && pos.Y == multi.Y;
        var (minX, minY, maxX, maxY) = def.RegionBounds;
        return pos.X >= multi.X + minX && pos.X <= multi.X + maxX &&
               pos.Y >= multi.Y + minY && pos.Y <= multi.Y + maxY;
    }

    /// <summary>Source-X Multi_GetSign: the sign the multi links to, else the
    /// multi itself.</summary>
    private Point3D GetSignPoint(Item multi)
    {
        var sign = multi.Link.IsValid ? _world.FindItem(multi.Link) : null;
        return sign != null && !sign.IsDeleted && sign.IsOnGround ? sign.Position : multi.Position;
    }

    /// <summary>Source-X CItemMulti::EjectAll (CItemMulti.cpp:1065): everyone inside
    /// the building but <paramref name="except"/> is teleported to the sign.</summary>
    private void EjectAll(Item multi, Character? except)
    {
        if (!multi.IsOnGround) return;
        var def = _housing.MultiDefs.Get(multi.BaseId);
        int range = def == null ? 0 : Math.Max(
            Math.Max(Math.Abs((int)def.RegionBounds.MinX), Math.Abs((int)def.RegionBounds.MaxX)),
            Math.Max(Math.Abs((int)def.RegionBounds.MinY), Math.Abs((int)def.RegionBounds.MaxY)));
        var dest = GetSignPoint(multi);
        foreach (var ch in _world.GetCharsInRange(multi.Position, range).ToList())
        {
            if (ch == except || ch.IsDeleted || !IsInsideBuilding(multi, ch.Position))
                continue;
            // Eject = Spell_Teleport(sign, fTakePets) (CItemMulti.cpp:1062).
            var from = ch.Position;
            foreach (var pet in _world.GetCharsInRange(from, 12).ToList())
            {
                if (pet == ch || pet.IsPlayer || pet.IsDead || pet.IsStatFlag(StatFlag.Ridden)) continue;
                if (!pet.HasOwner(ch.Uid) || pet.PetAIMode != PetAIMode.Follow) continue;
                _world.MoveCharacter(pet, dest);
            }
            ch.TeleportWithEffect(dest);
        }
    }

    /// <summary>Source-X RedeedAddons walks the building's addon list
    /// (CItemMulti::_lAddons). SphereNet keeps no addon list on a house and has no
    /// addon redeed, so there is nothing for N1=1 to act on yet.</summary>
    private static void RedeedAddons(Item multi)
    {
        _ = multi;
    }

    /// <summary>Source-X TransferSecuredToMovingCrate + TransferLockdownsToMovingCrate
    /// (CItemMulti.cpp:1445/1471): every secured container and locked-down item
    /// loses its house status and goes into the moving crate.</summary>
    private void TransferHoldingsToMovingCrate(House house)
    {
        TransferToCrate(house, house.SecureContainers.ToList(), House.SecureEvent, ObjAttributes.Secure);
        TransferToCrate(house, house.Lockdowns.ToList(), House.LockdownEvent, ObjAttributes.LockedDown);
    }

    /// <summary>One of the two transfers: each listed item loses its marker event,
    /// goes into the crate, loses the attribute and its link. Both transfers then
    /// clear the LOCKDOWN list (the secured pass clears _lLockDowns too, :1494), so
    /// with any secured container present the locked-down items stay where they are
    /// and only drop off the list - and the secure list is never cleared.</summary>
    private void TransferToCrate(House house, List<Serial> uids, string marker, ObjAttributes attr)
    {
        if (uids.Count == 0) return;
        var crate = house.GetMovingCrate(create: true);
        if (crate == null) return;
        var rid = ResourceId.FromString(marker, ResType.Events);
        foreach (var uid in uids)
        {
            var item = _world.FindItem(uid);
            if (item == null || item.IsDeleted) continue;
            item.Events.Remove(rid);
            var from = item.Position;
            bool onGround = item.IsOnGround;
            if (crate.TryAddItem(item) && onGround)
                BroadcastRemove?.Invoke(item.Uid.Value, from);
            item.ClearAttr(attr);
            item.Link = Serial.Invalid;
        }
        house.ForgetAllLockdowns();
    }

    private void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        if (obj is Item item && _designs.TryGetValue(item.Uid, out var state))
        {
            // ~CItemMultiCustom (CItemMultiCustom.cpp:43) only clears the architect
            // client's m_pHouseDesign: no packet, no @HouseDesignExit, no reveal.
            if (state.Architect.IsValid &&
                _designing.TryGetValue(state.Architect, out var designing) && designing == item.Uid)
                _designing.Remove(state.Architect);
            _designs.Remove(item.Uid);
            _committedCache.TryRemove(item.Uid, out _);
        }
        else if (obj is Character ch)
        {
            _designing.Remove(ch.Uid);
        }
    }

    // ------------------------------------------------------------------
    //  Client design edits (0xD7)
    // ------------------------------------------------------------------

    /// <summary>Source-X CItemMultiCustom::AddItem (CItemMultiCustom.cpp:438): at the
    /// current floor's height, or at ground level one row south of the design area
    /// (the front step).</summary>
    public bool Build(Character ch, ushort tileId, int x, int y)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi) ||
            !IsPlaceableTile(ch, tileId) ||
            !FitsDesignArea(session, x, y, allowSouthEdge: true))
            return false;
        sbyte z = IsSouthOfDesignArea(session, y) ? (sbyte)0 : DesignerPlaneZ(ch, multi);
        return AddTile(session.Working, multi, tileId, x, y, z, 0);
    }

    /// <summary>Source-X AddStairs (CItemMultiCustom.cpp:596): the id is a staircase
    /// MULTI; each of its visible pieces is added at the current floor's height plus
    /// its own offset, all tagged with one new staircase id so they come off together.
    /// It was a single static tile at Z 0, so no staircase could be walked.</summary>
    public bool Stairs(Character ch, ushort multiId, int x, int y)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi) ||
            !HouseDesignValidItems.IsValidStairMulti(multiId, ch.PrivLevel >= PrivLevel.GM))
            return false;
        // AddStairs adds each piece with no client, so no design-area check (:633).
        return AddMultiPieces(session.Working, multi, multiId, x, y, DesignerPlaneZ(ch, multi));
    }

    /// <summary>The staircase/ADDMULTI expansion shared by the client and script
    /// paths: every visible piece of the multi at (x,y,z) plus its own offset, under
    /// one new staircase id.</summary>
    private bool AddMultiPieces(HouseDesign design, Item multi, ushort multiId, int x, int y, int z)
    {
        var stairDef = _housing.MultiDefs.Get(multiId);
        if (stairDef == null || stairDef.Components.Count == 0)
            return false;

        ushort stairId = 0;
        foreach (var t in design.Tiles)
            if (t.StairId > stairId) stairId = t.StairId;
        stairId++;

        bool any = false;
        foreach (var comp in stairDef.Components)
        {
            if (!comp.Visible) continue;
            int px = x + comp.DeltaX, py = y + comp.DeltaY, pz = z + comp.DeltaZ;
            if (!FitsOffset(px, py) || pz is < sbyte.MinValue or > sbyte.MaxValue) continue;
            any |= AddTile(design, multi, comp.TileId, px, py, (sbyte)pz, stairId);
        }
        return any;
    }

    /// <summary>Source-X AddRoof (CItemMultiCustom.cpp:655): only a tile flagged as
    /// roof, only at an offset of -3..12 in steps of 3, and that offset is above the
    /// floor being edited - the client sends it relative to the current level.</summary>
    public bool Roof(Character ch, ushort tileId, int x, int y, int z)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return false;
        var md = _world.MapData;
        if (md == null || (md.GetItemTileData(tileId).Flags & SphereNet.MapData.Tiles.TileFlag.Roof) == 0)
            return false;
        if (z < -3 || z > 12 || z % 3 != 0)
            return false;
        if (!IsPlaceableTile(ch, tileId) || !FitsDesignArea(session, x, y, allowSouthEdge: true))
            return false;
        int finalZ = IsSouthOfDesignArea(session, y) ? 0 : z + DesignerPlaneZ(ch, multi);
        return AddTile(session.Working, multi, tileId, x, y, (sbyte)finalZ, 0);
    }

    /// <summary>Source-X GetPlaneZ (CItemMultiCustom.cpp:1898): 7 + (plane-1)*20.</summary>
    public static int GetPlaneZ(int plane) => 7 + (plane - 1) * 20;

    /// <summary>The floor height the designer edits: GetPlaneZ(GetPlane(char Z -
    /// building Z)) - the reference reads the level off where the designer stands
    /// (AddItem :500, AddStairs :624, AddRoof :689).</summary>
    private static sbyte DesignerPlaneZ(Character ch, Item multi) =>
        (sbyte)Math.Clamp(GetPlaneZ(GetPlane((sbyte)(ch.Z - multi.Z))), sbyte.MinValue, sbyte.MaxValue);

    /// <summary>Source-X GetPlane (CItemMultiCustom.cpp:1878).</summary>
    public static int GetPlane(int z) => z >= 67 ? 4 : z >= 47 ? 3 : z >= 27 ? 2 : z >= 7 ? 1 : 0;

    /// <summary>ITEMID_DIRT_TILE: the ground a first-floor floor tile is replaced with.</summary>
    public const ushort DirtTile = 0x31F4;

    /// <summary>Source-X CItemMultiCustom::IsValidItem gate — the designer may
    /// only place real static design pieces; GMs bypass the whitelist but not
    /// the graphic-range check.</summary>
    private static bool IsPlaceableTile(Character ch, ushort tileId) =>
        HouseDesignValidItems.IsValidBuildTile(
            tileId, ch.PrivLevel >= PrivLevel.GM);

    /// <summary>The designing client's delete (0xD7 0x05 / 0x14): Source-X
    /// CItemMultiCustom::RemoveItem with a client (CItemMultiCustom.cpp:698).</summary>
    public bool Erase(Character ch, ushort tileId, int x, int y, int z)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return false;
        return RemoveItemCore(session.Working, multi, tileId, x, y, (sbyte)z, fromClient: true);
    }

    /// <summary>Source-X RemoveItem (CItemMultiCustom.cpp:698). The pieces considered
    /// are those on that square on the same PLANE as <paramref name="z"/>
    /// (GetComponentsAt); each one of id <paramref name="id"/> (0 = any) goes.
    /// From a client: the dirt of the first floor cannot be removed, at ground level
    /// only the row just south of the design area can, a staircase piece takes its
    /// whole staircase (RemoveStairs), and a first-floor floor piece leaves one dirt
    /// tile at Z 7 when the square is not on the area's west or north edge. A script
    /// (no client) gets none of those rules.</summary>
    private bool RemoveItemCore(HouseDesign design, Item multi, ushort id, int x, int y, sbyte z, bool fromClient)
    {
        int plane = GetPlane(z);
        var (left, top, right, bottom) = GetDesignRect(multi);
        if (fromClient)
        {
            if (plane == 1 && id == DirtTile)
                return false;
            if (plane == 0 && y != bottom)
                return false;
        }

        var found = design.Tiles.Where(t => t.X == x && t.Y == y && GetPlane(t.Z) == plane).ToList();
        if (found.Count == 0)
            return false;

        bool changed = false, stairsRemoved = false, replaceDirt = false;
        foreach (var comp in found)
        {
            int index = design.Tiles.IndexOf(comp);
            if (index < 0)
                continue;   // already taken with a staircase
            if (id != 0 && comp.TileId != id)
                continue;
            if (fromClient && RemoveStairs(design, multi, comp))
            {
                stairsRemoved = true;
                continue;
            }
            if (comp.TileId != DirtTile && IsFloorTile(comp.TileId) && GetPlane(comp.Z) == 1 &&
                GetPlaneZ(1) == comp.Z)
                replaceDirt = true;
            design.Tiles.RemoveAt(index);
            changed = true;
        }

        if (changed)
            design.Revision++;

        // rectDesign.IsInsideX(x) && IsInsideX(x-1) && IsInsideY(y) && IsInsideY(y-1).
        if (fromClient && replaceDirt &&
            x - 1 >= left && x < right && y - 1 >= top && y < bottom)
            AddTile(design, multi, DirtTile, x, y, 7, 0);

        return changed || stairsRemoved;
    }

    /// <summary>Source-X RemoveStairs (CItemMultiCustom.cpp:843): a piece with a stair
    /// id takes every piece of that staircase with it, each moving the revision; a
    /// first-floor floor piece among them is replaced by dirt where it stood.</summary>
    private bool RemoveStairs(HouseDesign design, Item multi, HouseDesignTile stairPiece)
    {
        if (stairPiece.StairId == 0)
            return false;
        ushort stairId = stairPiece.StairId;
        for (int i = 0; i < design.Tiles.Count;)
        {
            var t = design.Tiles[i];
            if (t.StairId != stairId)
            {
                i++;
                continue;
            }
            bool replaceDirt = IsFloorTile(t.TileId) && GetPlane(t.Z) == 1 && GetPlaneZ(1) == t.Z;
            design.Tiles.RemoveAt(i);
            design.Revision++;
            if (replaceDirt)
            {
                AddTile(design, multi, DirtTile, t.X, t.Y, t.Z, 0);
                i = 0;   // the dirt may have replaced a piece before this one
            }
        }
        return true;
    }

    /// <summary>Source-X GetDesignArea as offsets, non-inclusive right/bottom like
    /// CRect: the anchor plus every visible foundation piece.</summary>
    private (int Left, int Top, int Right, int Bottom) GetDesignRect(Item multi)
    {
        var def = _housing.MultiDefs.Get(multi.BaseId);
        if (def == null)
            return (0, 0, 1, 1);
        var (minX, minY, maxX, maxY) = GetDesignBounds(def);
        return (minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>The client's Clear button: Source-X PacketHouseDesignClear calls
    /// ResetStructure (receive.cpp:3932) - back to the bare foundation, not empty.</summary>
    public void Clear(Character ch)
    {
        if (TryGetAuthorizedSession(ch, out var session, out var multi))
            ResetStructure(session.Working, multi);
    }

    /// <summary>Source-X ResetStructure (CItemMultiCustom.cpp:1078): the working
    /// design becomes the foundation - every visible piece of the multi's own
    /// definition - through the ordinary AddItem rules.</summary>
    private void ResetStructure(HouseDesign design, Item multi)
    {
        design.Tiles.Clear();
        design.Revision++;
        var def = _housing.MultiDefs.Get(multi.BaseId);
        if (def == null) return;
        foreach (var comp in def.Components)
        {
            if (!comp.Visible) continue;
            if (!FitsOffset(comp.DeltaX, comp.DeltaY) || comp.DeltaZ is < sbyte.MinValue or > sbyte.MaxValue)
                continue;
            AddTile(design, multi, comp.TileId, comp.DeltaX, comp.DeltaY, (sbyte)comp.DeltaZ, 0);
        }
    }

    /// <summary>Source-X CItemMultiCustom constructor (CItemMultiCustom.cpp:23): a
    /// new customizable building starts as ResetStructure + CommitChanges, so its
    /// committed design IS the foundation. Called when a custom foundation is placed.</summary>
    public static void InitializeFoundationDesign(Item multi, MultiDef def, SphereNet.MapData.MapDataManager? md)
    {
        var design = new HouseDesign { Revision = 0 };
        design.Revision++; // ResetStructure
        foreach (var comp in def.Components)
        {
            if (!comp.Visible) continue;
            if (!FitsOffset(comp.DeltaX, comp.DeltaY) || comp.DeltaZ is < sbyte.MinValue or > sbyte.MaxValue)
                continue;
            AddTileCore(design, comp.TileId, comp.DeltaX, comp.DeltaY, (sbyte)comp.DeltaZ, 0,
                id => IsFloorTile(md, id), id => IsFixtureTile(md, id));
        }
        design.Revision++; // CommitChanges
        design.SaveToTags(multi);
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

    /// <summary>Source-X RevertChanges: back to the working design as it stood when
    /// design mode began.</summary>
    public void Revert(Character ch)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return;
        session.Working = session.Revert?.Clone() ?? LoadMainAsWorking(multi);
    }

    // ------------------------------------------------------------------
    //  Commit (CommitChanges)
    // ------------------------------------------------------------------

    /// <summary>What @HouseDesignCommit is told (Source-X CommitChanges:314-324):
    /// ARGN1 = committed piece count, ARGN2 = working piece count, ARGN3 = the
    /// working revision, LOCAL.FIXTURES.OLD/NEW and LOCAL.MAXZ - all read off the
    /// working design AFTER the per-piece @HouseDesignCommitItem filter.</summary>
    public readonly record struct CommitPreview(
        Item Multi, int OldTiles, int NewTiles, uint Revision,
        int OldFixtures, int NewFixtures, int MaxZ);

    /// <summary>The numbers @HouseDesignCommit would see for the current working
    /// design. Null without an authorized session, or when the working revision has
    /// not moved since the last commit - the reference returns early then (:277), so
    /// pressing the button twice on the same design is not a second commit.</summary>
    public CommitPreview? PreviewCommit(Character ch)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return null;
        if (session.Working.Revision == MainRevision(multi))
            return null;
        return BuildPreview(session, multi);
    }

    private CommitPreview BuildPreview(HouseDesignSession session, Item multi)
    {
        var committed = GetCommittedDesign(multi);
        int maxZ = 0;
        foreach (var tile in session.Working.Tiles)
            if (tile.Z > maxZ) maxZ = tile.Z;
        return new CommitPreview(multi, committed.Tiles.Count, session.Working.Tiles.Count,
            session.Working.Revision, CountFixtures(committed), CountFixtures(session.Working), maxZ);
    }

    /// <summary>Source-X GetFixtureCount — how many of a design's pieces become real
    /// items on commit.</summary>
    public int CountFixtures(HouseDesign design)
    {
        int count = 0;
        foreach (var tile in design.Tiles)
            if (IsFixtureTile(tile.TileId))
                count++;
        return count;
    }

    /// <summary>Source-X @HouseDesignCommitItem (CommitChanges, CItemMultiCustom.cpp:
    /// 284-305): asked once per piece before the commit, with LOCAL.ID, P.X, P.Y,
    /// P.Z and VISIBLE; an explicit RETURN 0 leaves the piece out.</summary>
    public static Func<Character, Item, HouseDesignTile, bool>? KeepCommitItem { get; set; }

    /// <summary>The designer's commit (0xD7 0x04). The session stays open: Source-X
    /// CommitChanges does not end design mode. Returns the new committed revision,
    /// or null when nothing was committed.</summary>
    public uint? Commit(Character ch, IHouseDesignClient? client = null)
    {
        if (!TryGetAuthorizedSession(ch, out var session, out var multi))
            return null;
        return CommitCore(session, multi, ch, client ?? session.Client);
    }

    /// <summary>Source-X CommitChanges (CItemMultiCustom.cpp:272). With a designer:
    /// first the per-piece @HouseDesignCommitItem filter takes pieces out of the
    /// working design, then the counts / fixtures / MAXZ are read off what is left
    /// and @HouseDesignCommit may refuse. A script COMMIT has no designer and fires
    /// neither. Then working becomes the committed design, the fixtures are rebuilt
    /// and the revision moves on.</summary>
    private uint? CommitCore(HouseDesignSession session, Item multi, Character? ch, IHouseDesignClient? client)
    {
        if (session.Working.Revision == MainRevision(multi))
            return null;

        if (ch != null)
        {
            if (KeepCommitItem is { } keep)
                session.Working.Tiles.RemoveAll(t => !keep(ch, multi, t));
            var preview = BuildPreview(session, multi);
            if (client != null && client.FireHouseDesignCommit(multi, preview))
                return null;
        }

        var main = session.Working.Clone();
        main.Revision = session.Working.Revision + 1;
        session.Working.Revision = main.Revision;
        main.SaveToTags(multi);
        MaterializeFixtures(multi, main);
        DesignCommitted?.Invoke(multi, main.Revision);
        return main.Revision;
    }

    private const string FixturesTag = "COMMIT_FIXTURES";

    /// <summary>
    /// Source-X CItemMultiCustom::CommitChanges (CItemMultiCustom.cpp:354-410): the
    /// fixture pieces - doors and telepads - become REAL items made from their item
    /// definitions; walls/floors stay virtual render+walk geometry. Fixtures from the
    /// previous commit are replaced wholesale (tracked in the COMMIT_FIXTURES tag).
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
        var tiles = design.Tiles;
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!IsFixtureTile(tile.TileId))
                continue;

            var fixture = _world.CreateItem();
            fixture.BaseId = tile.TileId;
            // CItem::CreateScript: the item definition supplies the type, name,
            // TDATA, tags and @Create.
            if (!Definitions.ItemDefHelper.ApplyInstanceMetadata(fixture, tile.TileId))
            {
                if (World.DoorHelper.IsDoorGraphic(_world.MapData, tile.TileId))
                    fixture.ItemType = ItemType.Door;
            }
            fixture.ClearAttr(ObjAttributes.Decay);
            fixture.SetAttr(ObjAttributes.Move_Never);
            fixture.SetTag("FIXTURE", multi.Uid.Value.ToString());

            if (fixture.ItemType == ItemType.Telepad)
            {
                // Link to the next telepad of the same graphic, wrapping round
                // (:384); the point, never the link, or the char would land on
                // the house centre.
                for (int step = 1; step < tiles.Count; step++)
                {
                    var other = tiles[(i + step) % tiles.Count];
                    if (other.TileId != tile.TileId) continue;
                    fixture.MoreP = new Point3D((short)(multi.X + other.X), (short)(multi.Y + other.Y),
                        (sbyte)(multi.Position.Z + other.Z), multi.MapIndex);
                    break;
                }
            }
            else
            {
                fixture.Link = multi.Uid; // the house key opens a door fixture
            }

            _world.PlaceItem(fixture, new Point3D(
                (short)(multi.X + tile.X), (short)(multi.Y + tile.Y),
                (sbyte)(multi.Position.Z + tile.Z), multi.MapIndex));
            ApplyComponentCreate(multi, fixture);
            house?.AddComponent(fixture.Uid);
            created.Add(fixture.Uid.Value.ToString());
        }

        if (created.Count > 0)
            multi.SetTag(FixturesTag, string.Join(',', created));
        else
            multi.RemoveTag(FixturesTag);
    }

    public const string ComponentEvent = "t_house_component";
    public const string DoorEvent = "ei_house_door";
    public const string ContainerEvent = "ei_house_container";
    public const string TelepadEvent = "ei_house_telepad";

    /// <summary>Source-X CItemMulti::OnComponentCreate (CItemMulti.cpp:3468): every
    /// component carries t_house_component; a door gets ei_house_door and starts
    /// LOCKED, a container ei_house_container and locked, a telepad
    /// ei_house_telepad; an undyed component takes the building's hue.</summary>
    public static void ApplyComponentCreate(Item multi, Item component)
    {
        AddEvent(component, ComponentEvent);
        switch (component.ItemType)
        {
            case ItemType.Door:
                AddEvent(component, DoorEvent);
                component.ItemType = ItemType.DoorLocked;
                break;
            case ItemType.Container:
                AddEvent(component, ContainerEvent);
                component.ItemType = ItemType.ContainerLocked;
                break;
            case ItemType.ShipSide:
                component.ItemType = ItemType.ShipSideLocked;
                break;
            case ItemType.ShipHold:
                component.ItemType = ItemType.ShipHoldLock;
                break;
            case ItemType.Telepad:
                AddEvent(component, TelepadEvent);
                break;
        }
        if (component.Hue.Equals(Color.Default))
            component.Hue = multi.Hue;
    }

    private static void AddEvent(Item item, string name)
    {
        var rid = ResourceId.FromString(name, ResType.Events);
        if (!item.Events.Contains(rid))
            item.Events.Add(rid);
    }

    /// <summary>Source-X AddItem fixture test (CItemMultiCustom.cpp:458): a door
    /// (IT_DOOR / IT_DOOR_LOCKED / a door graphic) or a telepad - the pieces Commit
    /// replaces with real items.</summary>
    private bool IsFixtureTile(ushort tileId) => IsFixtureTile(_world.MapData, tileId);

    private static bool IsFixtureTile(SphereNet.MapData.MapDataManager? md, ushort tileId)
    {
        var defType = Definitions.DefinitionLoader.GetItemDef(tileId)?.Type;
        return defType is ItemType.Door or ItemType.DoorOpen or ItemType.DoorLocked or ItemType.Telepad ||
            World.DoorHelper.IsDoorGraphic(md, tileId);
    }

    // ------------------------------------------------------------------
    //  Script verbs / properties (CItemMultiCustom r_Verb / r_WriteVal)
    // ------------------------------------------------------------------

    /// <summary>Source-X CItemMultiCustom::r_Verb (CItemMultiCustom.cpp:1570). Returns
    /// false when the verb is not one of the custom-building verbs.</summary>
    public bool ExecuteScriptVerb(Item multi, string verb, string args, Character? src)
    {
        switch (verb.ToUpperInvariant())
        {
            case "ADDITEM":
            {
                if (!TryParseFour(args, out int id, out int x, out int y, out int z))
                    return true;
                // IsValidItem with no client: any non-multi graphic.
                if (id is <= 0 or >= HouseDesignValidItems.ItemIdMulti || !FitsOffset(x, y) ||
                    z is < sbyte.MinValue or > sbyte.MaxValue)
                    return true;
                AddTile(GetDesignState(multi).Working, multi, (ushort)id, x, y, (sbyte)z, 0);
                return true;
            }
            case "ADDMULTI":
            {
                if (!TryParseFour(args, out int id, out int x, out int y, out int z) || id <= 0)
                    return true;
                // IsValidItem(fMulti): an item-id-space multi id (ITEMID_MULTI+).
                if (id < HouseDesignValidItems.ItemIdMulti || id - HouseDesignValidItems.ItemIdMulti > ushort.MaxValue)
                    return true;
                id -= HouseDesignValidItems.ItemIdMulti;
                AddMultiPieces(GetDesignState(multi).Working, multi, (ushort)id, x, y, z);
                return true;
            }
            case "CLEAR":
            {
                var state = GetDesignState(multi);
                state.Working.Tiles.Clear();
                state.Working.Revision++;
                return true;
            }
            case "RESET":
                ResetStructure(GetDesignState(multi).Working, multi);
                return true;
            case "REVERT":
            {
                var state = GetDesignState(multi);
                var main = LoadMainAsWorking(multi);
                main.Revision++;
                state.Working = main;
                return true;
            }
            case "REMOVEITEM":
            {
                if (!TryParseFour(args, out int id, out int x, out int y, out int z))
                    return true;
                // RemoveItem with no client: no edge rules, staircase or dirt handling.
                if (id is < 0 or > ushort.MaxValue) return true;
                RemoveItemCore(GetDesignState(multi).Working, multi, (ushort)id, x, y, (sbyte)z,
                    fromClient: false);
                return true;
            }
            case "COMMIT":
                CommitCore(GetDesignState(multi), multi, null, null);
                return true;
            case "CUSTOMIZE":
            case "CONTINUECUSTOMIZE":
            {
                var who = args.Trim().Length > 0 ? ResolveCharArg(args) : src;
                if (who == null || SphereNet.Game.Objects.ObjBase.ResolveClientConsole?.Invoke(who)
                        is not IHouseDesignClient client)
                    return true;
                Begin(who, multi, client, continueCustomize: verb.Equals("CONTINUECUSTOMIZE",
                    StringComparison.OrdinalIgnoreCase));
                return true;
            }
            case "ENDCUSTOMIZE":
                EndHouse(multi, forced: true);
                return true;
            case "RESYNC":
            {
                var who = args.Trim().Length > 0 ? ResolveCharArg(args) : src;
                if (who == null || SphereNet.Game.Objects.ObjBase.ResolveClientConsole?.Invoke(who)
                        is not IHouseDesignClient client)
                    return true;
                var state = GetDesignState(multi);
                if (state.Architect == who.Uid)
                    client.SendWorkingHouseDesign(multi, state.Working);
                else
                    client.SendCommittedHouseDesign(multi);
                return true;
            }
        }
        return false;
    }

    private Character? ResolveCharArg(string args)
    {
        string a = args.Trim();
        if (a.Length == 0) return null;
        if (!ScriptNumber.TryParseToken(a, out long uid) || uid <= 0) return null;
        return _world.FindChar(new Serial((uint)uid));
    }

    private static bool TryParseFour(string args, out int a, out int b, out int c, out int d)
    {
        a = b = c = d = 0;
        var parts = args.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return false;
        return TryNum(parts[0], out a) && TryNum(parts[1], out b) &&
               TryNum(parts[2], out c) && TryNum(parts[3], out d);
    }

    private static bool TryNum(string s, out int value)
    {
        value = 0;
        if (!ScriptNumber.TryParseToken(s, out long v) || v is < int.MinValue or > int.MaxValue)
            return false;
        value = (int)v;
        return true;
    }

    /// <summary>Source-X CItemMultiCustom::r_WriteVal (CItemMultiCustom.cpp:1756):
    /// COMPONENTS, DESIGN[.n[.ID|DX|DY|DZ|D|FIXTURE]], DESIGNER, FIXTURES, REVISION -
    /// all from the committed design and the live session, never from stale tags.
    /// Without an engine the committed DESIGN_n tags are read directly.</summary>
    public static bool TryGetScriptProperty(Item multi, string upper, out string value)
    {
        value = "";
        var engine = Active;
        HouseDesign main = engine != null ? engine.GetCommittedDesign(multi) : HouseDesign.LoadFromTags(multi);
        switch (upper)
        {
            case "COMPONENTS":
            case "DESIGN":
                value = main.Tiles.Count.ToString();
                return true;
            case "DESIGNER":
            {
                var designer = engine?.GetDesigner(multi) ?? Serial.Invalid;
                value = $"0{(designer.IsValid ? designer.Value : 0):x}";
                return true;
            }
            case "FIXTURES":
                value = (engine != null ? engine.CountFixtures(main)
                    : main.Tiles.Count(t => IsFixtureTile(null, t.TileId))).ToString();
                return true;
            case "REVISION":
                value = MainRevision(multi).ToString();
                return true;
        }

        if (!upper.StartsWith("DESIGN.", StringComparison.Ordinal))
            return false;

        string rest = upper[7..];
        int dot = rest.IndexOf('.');
        string indexText = dot >= 0 ? rest[..dot] : rest;
        string sub = dot >= 0 ? rest[(dot + 1)..] : "";
        if (!int.TryParse(indexText, out int index) || index < 0 || index >= main.Tiles.Count)
            return false;
        var tile = main.Tiles[index];
        bool fixture = engine != null ? engine.IsFixtureTile(tile.TileId) : IsFixtureTile(null, tile.TileId);
        if (sub.StartsWith("ID", StringComparison.Ordinal)) value = tile.TileId.ToString();
        else if (sub.StartsWith("DX", StringComparison.Ordinal)) value = tile.X.ToString();
        else if (sub.StartsWith("DY", StringComparison.Ordinal)) value = tile.Y.ToString();
        else if (sub.StartsWith("DZ", StringComparison.Ordinal)) value = tile.Z.ToString();
        else if (sub.StartsWith("D", StringComparison.Ordinal)) value = $"{tile.X},{tile.Y},{tile.Z}";
        else if (sub.StartsWith("FIXTURE", StringComparison.Ordinal)) value = fixture ? "1" : "0";
        else if (sub.Length == 0) value = $"{tile.TileId},{tile.X},{tile.Y},{tile.Z}";
        else return false;
        return true;
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private bool TryGetAuthorizedSession(Character ch, out HouseDesignSession session, out Item multi)
    {
        session = null!;
        multi = null!;
        var found = GetSession(ch.Uid);
        if (found == null) return false;
        var foundMulti = _world.FindItem(found.HouseUid);
        if (foundMulti == null || foundMulti.IsDeleted)
        {
            End(ch, forced: true);
            return false;
        }
        var house = _housing.GetHouse(found.HouseUid);
        if (house != null && ch.PrivLevel < PrivLevel.GM && house.Owner != ch.Uid)
        {
            End(ch, forced: true);
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
    private bool IsFloorTile(ushort tileId) => IsFloorTile(_world.MapData, tileId);

    private static bool IsFloorTile(SphereNet.MapData.MapDataManager? md, ushort tileId)
    {
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
    /// whatever of its own kind (floor or not) already stands on that square on that
    /// floor - it used to stack on top - bumps the design revision, and moves the
    /// house's locked-down items standing there into the moving crate.</summary>
    private bool AddTile(HouseDesign design, Item multi, ushort tileId, int x, int y, sbyte z,
        ushort stairId)
    {
        if (!AddTileCore(design, tileId, x, y, z, stairId, IsFloorTile, IsFixtureTile))
            return false;
        MoveLockdownsToCrate(multi, x, y, z);
        return true;
    }

    private static bool AddTileCore(HouseDesign design, ushort tileId, int x, int y, sbyte z,
        ushort stairId, Func<ushort, bool> isFloor, Func<ushort, bool> isFixture)
    {
        var tile = new HouseDesignTile(tileId, (sbyte)x, (sbyte)y, z,
            Visible: !isFixture(tileId), StairId: stairId);
        var tiles = design.Tiles;
        bool floor = isFloor(tileId);
        int plane = GetPlane(z);
        tiles.RemoveAll(t => t.X == x && t.Y == y && GetPlane(t.Z) == plane && isFloor(t.TileId) == floor);
        if (tiles.Count >= MaxDesignTiles)
            return false;
        tiles.Add(tile);
        design.Revision++;
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
