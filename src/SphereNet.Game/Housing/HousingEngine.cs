using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Game.Scripting;
using SphereNet.MapData;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Housing;

/// <summary>
/// House privilege levels. Maps to HOUSE_PRIV in Source-X CItemMulti.h.
/// </summary>
public enum HousePriv : byte
{
    None = 0,
    Owner,
    CoOwner,
    Friend,
    AccessOnly,
    Ban,
    Vendor,
    Guild,
    Qty,
}

/// <summary>
/// House type. Maps to HOUSE_TYPE in Source-X.
/// </summary>
public enum HouseType : byte
{
    Private = 0,
    Public,
    Guild,
}

/// <summary>
/// A multi component record (from multi.mul / scripts).
/// Maps to CUOMultiItemRec_HS in Source-X.
/// </summary>
public readonly struct MultiComponent
{
    public ushort TileId { get; init; }
    public short DeltaX { get; init; }
    public short DeltaY { get; init; }
    public short DeltaZ { get; init; }
    public bool Visible { get; init; }
}

/// <summary>
/// Multi definition (template). Maps to CItemBaseMulti in Source-X.
/// Loaded from multi.mul and/or [MULTIDEF] script sections.
/// </summary>
public sealed class MultiDef
{
    public ushort Id { get; init; }
    public string Name { get; set; } = "";

    /// <summary>Script [MULTIDEF] TYPE (t_multi / t_multi_custom / t_ship). Empty until
    /// the script metadata is merged in — see MultiRegistry.MergeScriptMetadata.</summary>
    public string MultiTypeName { get; set; } = "";

    /// <summary>Script [MULTIDEF] BaseStorage (secure/lockdown budget); 0 = unset.</summary>
    public int BaseStorage { get; set; }

    /// <summary>Script [MULTIDEF] BaseVendors (max house vendors); 0 = unset.</summary>
    public int BaseVendors { get; set; }

    /// <summary>Script [MULTIDEF] SHIPSPEED=period,tiles. Period is TENTHS of a
    /// second between movement steps (Source-X MSECS_PER_TENTH), tiles is the
    /// distance moved per step. 0 = unset (a placed ship keeps the engine default).</summary>
    public int ShipSpeedPeriodTenths { get; set; }
    public int ShipSpeedTiles { get; set; }

    /// <summary>Script [MULTIDEF] REGIONFLAGS - the base region flags a placed multi
    /// starts from (Source-X CItemBase.cpp:2073, applied in MultiRealizeRegion,
    /// CItemMulti.cpp:232). None = unset.</summary>
    public RegionFlag RegionFlags { get; set; }

    public List<MultiComponent> Components { get; } = [];

    // Bounding rect
    public short MinX { get; set; }
    public short MinY { get; set; }
    public short MaxX { get; set; }
    public short MaxY { get; set; }

    /// <summary>The region rectangle the SCRIPT declares, when it declares one
    /// (Source-X CItemBaseMulti::SetMultiRegion, CItemBase.cpp:1936). It is not the
    /// component footprint: a house's MULTIREGION reaches past its walls to take in
    /// the step and the ground the structure owns, so the small stone-and-plaster
    /// house declares -3,-3,3,4 over a 7x7 footprint - one row further south, which
    /// is exactly where its front step is. Null when the definition declares none,
    /// and then the footprint stands in as before.</summary>
    public (short MinX, short MinY, short MaxX, short MaxY)? ScriptRegion { get; set; }

    /// <summary>The rectangle a placed multi's region should cover: what the script
    /// declared if it declared anything, else the footprint.</summary>
    public (short MinX, short MinY, short MaxX, short MaxY) RegionBounds =>
        ScriptRegion ?? (MinX, MinY, MaxX, MaxY);

    /// <summary>The tiles the structure actually occupies, as offsets from its anchor.
    /// Built once on <see cref="RecalcBounds"/>.
    ///
    /// The bounding rectangle is not the footprint, and for a ship the difference is the
    /// whole question: a hull narrows at the bow, so the rectangle also covers the water
    /// and the quay beside it. Anything that asks "is this character standing ON the
    /// structure" has to ask this, not the rectangle - a person on the dock next to the
    /// bow is inside the rectangle, and on a dock they are at the deck's own height too,
    /// so no height test separates them either.</summary>
    public IReadOnlySet<(short Dx, short Dy)> Footprint => _footprint;
    private readonly HashSet<(short Dx, short Dy)> _footprint = [];

    /// <summary>Does the structure occupy this offset from its anchor?</summary>
    public bool OccupiesOffset(short dx, short dy) => _footprint.Contains((dx, dy));

    public void RecalcBounds()
    {
        _footprint.Clear();
        foreach (var c in Components)
            _footprint.Add((c.DeltaX, c.DeltaY));

        if (Components.Count == 0)
        {
            MinX = MinY = MaxX = MaxY = 0;
            return;
        }
        MinX = MinY = short.MaxValue;
        MaxX = MaxY = short.MinValue;
        foreach (var c in Components)
        {
            if (c.DeltaX < MinX) MinX = c.DeltaX;
            if (c.DeltaY < MinY) MinY = c.DeltaY;
            if (c.DeltaX > MaxX) MaxX = c.DeltaX;
            if (c.DeltaY > MaxY) MaxY = c.DeltaY;
        }
    }
}

/// <summary>Why a house/ship placement was rejected (B4 structured result). Lets the
/// deed handler show a specific message instead of one generic "Cannot place here".</summary>
public enum PlacementFailure
{
    None = 0,
    PlayerLimitReached,
    AccountLimitReached,
    MultiDefinitionMissing,
    OutOfMap,
    LocationBlocked,   // terrain / overlap / (ship) water — the CanPlace* checks
    ScriptVeto,        // @HouseCheck vetoed
}

/// <summary>
/// House decay stage. Maps to HOUSE_DECAY_STAGE in Source-X.
/// </summary>
public enum HouseDecayStage : byte
{
    LikeNew = 0,
    SlightlyWorn = 1,
    SomewhatWorn = 2,
    FairlyWorn = 3,
    GreatlyWorn = 4,
    InDangerOfCollapsing = 5, // IDOC
}

/// <summary>
/// House instance (placed multi item in the world).
/// Maps to CItemMulti in Source-X CItemMulti.h.
/// </summary>
public sealed class House
{
    private readonly Item _multiItem;
    private Serial _owner = Serial.Invalid;
    private Serial _guildStone = Serial.Invalid;
    private HouseType _houseType = HouseType.Private;

    private readonly HashSet<Serial> _coOwners = [];
    private readonly HashSet<Serial> _friends = [];
    private readonly HashSet<Serial> _bans = [];
    private readonly HashSet<Serial> _accessList = [];
    private readonly HashSet<Serial> _lockdowns = [];
    private readonly HashSet<Serial> _secureContainers = [];
    private readonly List<Serial> _components = [];
    private bool _redeeded;
    private readonly List<Serial> _vendors = [];

    // CItemBaseMulti defaults, the smallest 7x7 house (CItemBase.cpp:1902-1903).
    private int _baseStorage = 489;
    private int _lockdownsPercent = 50;
    private int _baseVendors = 10;
    private int _increasedStorage;

    // Decay tracking
    private long _lastRefreshTick;
    private HouseDecayStage _decayStage = HouseDecayStage.LikeNew;

    // Source-X CItemMulti::MultiRealizeRegion — the dynamic world region created
    // for this house's footprint (REGION_FLAG_HOUSE). 0 = none.
    private uint _regionUid;

    public Item MultiItem => _multiItem;
    public Serial Owner { get => _owner; set => _owner = value; }
    public HouseType Type { get => _houseType; set => _houseType = value; }
    public Serial GuildStone { get => _guildStone; set => _guildStone = value; }
    /// <summary>Source-X CItemMulti::GetMaxStorage — the base budget plus the
    /// percentage bonus an upgraded house carries.</summary>
    public int MaxStorage => _baseStorage + _baseStorage * _increasedStorage / 100;

    /// <summary>Source-X CItemMulti::GetCurrentStorage — lockdowns and secured
    /// containers draw on the same budget.</summary>
    public int CurrentStorage => _lockdowns.Count + _secureContainers.Count;

    /// <summary>Source-X CItemMulti::GetMaxLockdowns, whose expression reduces to
    /// maxStorage * lockdownsPercent / 100.</summary>
    public int MaxLockdowns => MaxStorage * _lockdownsPercent / 100;
    public int MaxSecure => MaxStorage - MaxLockdowns;

    /// <summary>Source-X CItemMulti::GetMaxVendors.</summary>
    public int MaxVendors => _baseVendors + _baseVendors * _increasedStorage / 100;

    public IReadOnlyList<Serial> Components => _components;
    public int BaseStorage { get => _baseStorage; set => _baseStorage = Math.Max(0, value); }

    /// <summary>Script [MULTIDEF] BaseVendors / the BASEVENDORS property.</summary>
    public int BaseVendors { get => _baseVendors; set => _baseVendors = Math.Max(0, value); }

    /// <summary>Percentage bonus applied to storage and vendor caps (Source-X
    /// INCREASEDSTORAGE). 0 = no bonus, so an unscripted house is unchanged.</summary>
    public int IncreasedStorage { get => _increasedStorage; set => _increasedStorage = Math.Max(0, value); }

    /// <summary>Share of the storage budget usable for lockdowns rather than
    /// secured containers (Source-X LOCKDOWNSPERCENT).</summary>
    public int LockdownsPercent
    {
        get => _lockdownsPercent;
        set => _lockdownsPercent = Math.Clamp(value, 0, 100);
    }
    public long LastRefreshTick { get => _lastRefreshTick; set => _lastRefreshTick = value; }
    public HouseDecayStage DecayStage { get => _decayStage; set => _decayStage = value; }
    public uint RegionUid { get => _regionUid; set => _regionUid = value; }

    /// <summary>The house's standing moving crate, or Invalid when it has none.
    /// Source-X keeps this on the multi (_uidMovingCrate) — it is not a thing that
    /// only exists during a redeed. The housing dialogs read it back
    /// (Scripts-X house_dialogs.scp:238/277, house_typedefs.scp:632).</summary>
    public Serial MovingCrate { get => _movingCrate; set => _movingCrate = value; }
    private Serial _movingCrate = Serial.Invalid;

    /// <summary>ITEMID_CRATE1 — what Source-X makes a moving crate out of.</summary>
    public const ushort MovingCrateId = 0x0E3D;

    /// <summary>The crate item, when one exists and still does.</summary>
    public Item? ResolveMovingCrate()
    {
        if (!_movingCrate.IsValid)
            return null;
        var crate = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(_movingCrate);
        if (crate == null || crate.IsDeleted)
        {
            _movingCrate = Serial.Invalid;   // the crate went away; forget it
            return null;
        }
        return crate;
    }

    /// <summary>Source-X GetMovingCrate(fCreate) (CItemMulti.cpp:1329). With
    /// <paramref name="create"/> it mints one at the house's own spot but 20 below
    /// it, so the crate sits under the floor rather than in the room, and links it
    /// back to the house.</summary>
    public Item? GetMovingCrate(bool create)
    {
        var existing = ResolveMovingCrate();
        if (existing != null || !create)
            return existing;

        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        if (world == null)
            return null;

        var crate = world.CreateItem();
        crate.BaseId = MovingCrateId;
        crate.ItemType = Core.Enums.ItemType.Container;
        crate.Name = "a moving crate";
        world.PlaceItem(crate, new Core.Types.Point3D(
            _multiItem.X, _multiItem.Y, (sbyte)(_multiItem.Z - 20), _multiItem.MapIndex));
        AssignMovingCrate(crate);
        return crate;
    }

    /// <summary>Source-X SetMovingCrate (CItemMulti.cpp:1306). A crate already
    /// holding something is not simply dropped: its contents move into the new one
    /// and the old crate is deleted, so replacing a crate never strands goods.
    /// Passing null (or a dead item) just forgets the crate.</summary>
    public void AssignMovingCrate(Item? crate)
    {
        if (crate == null || crate.IsDeleted)
        {
            _movingCrate = Serial.Invalid;
            return;
        }

        var current = ResolveMovingCrate();
        if (current != null && current != crate && current.Contents.Count > 0)
        {
            foreach (var item in new List<Item>(current.Contents))
            {
                current.RemoveItem(item);
                crate.TryAddItem(item);
            }
            current.Delete();
        }

        _movingCrate = crate.Uid;
        crate.Link = _multiItem.Uid;
    }
    public IReadOnlyCollection<Serial> CoOwners => _coOwners;
    public IReadOnlyCollection<Serial> Friends => _friends;
    public IReadOnlyCollection<Serial> Bans => _bans;
    public IReadOnlyCollection<Serial> AccessList => _accessList;
    public IReadOnlyCollection<Serial> Lockdowns => _lockdowns;
    public IReadOnlyCollection<Serial> SecureContainers => _secureContainers;

    public House(Item multiItem)
    {
        _multiItem = multiItem;
        _lastRefreshTick = Environment.TickCount64;
    }

    public HousePriv GetPriv(Serial charUid)
    {
        if (charUid == _owner) return HousePriv.Owner;
        if (_bans.Contains(charUid)) return HousePriv.Ban;
        if (_coOwners.Contains(charUid)) return HousePriv.CoOwner;
        if (_friends.Contains(charUid)) return HousePriv.Friend;
        if (_accessList.Contains(charUid)) return HousePriv.AccessOnly;
        if (_vendors.Contains(charUid)) return HousePriv.Vendor;
        return HousePriv.None;
    }

    private void RevokeListedPrivileges(Serial uid)
    {
        _coOwners.Remove(uid);
        _friends.Remove(uid);
        _bans.Remove(uid);
        _accessList.Remove(uid);
        _vendors.Remove(uid);
    }

    public bool AddCoOwner(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _coOwners.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _coOwners.Add(uid);
    }
    public bool RemoveCoOwner(Serial uid) => _coOwners.Remove(uid);
    public bool AddFriend(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _friends.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _friends.Add(uid);
    }
    public bool RemoveFriend(Serial uid) => _friends.Remove(uid);
    public bool AddBan(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _bans.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _bans.Add(uid);
    }
    public bool RemoveBan(Serial uid) => _bans.Remove(uid);
    public bool AddAccess(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _accessList.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _accessList.Add(uid);
    }
    public bool RemoveAccess(Serial uid) => _accessList.Remove(uid);

    public bool CanAccess(Serial charUid)
    {
        var priv = GetPriv(charUid);
        if (priv == HousePriv.Ban) return false;
        return _houseType == HouseType.Public || priv != HousePriv.None;
    }

    public bool CanLockdown(Serial charUid) =>
        GetPriv(charUid) is HousePriv.Owner or HousePriv.CoOwner;

    /// <summary>Lock down an item in the house. Source-X CItemMulti::LockItem:
    /// the item itself gains ATTR_LOCKEDDOWN and links back to the multi —
    /// previously only the house-side hash set was updated, so anything that
    /// reads the item's attributes (WalkCheck, scripts) saw it as loose.</summary>
    public bool Lockdown(Serial itemUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (_lockdowns.Contains(itemUid) || _secureContainers.Contains(itemUid)) return false;
        if (_lockdowns.Count >= MaxLockdowns) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        if (item == null || item.IsDeleted || !item.IsOnGround || item.IsEquipped) return false;
        // Source-X: only items INSIDE the house region may be locked down —
        // an owner could previously lock items anywhere in the world.
        if (!IsInsideHouse(item.Position)) return false;
        _lockdowns.Add(itemUid);
        item.SetAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
        item.Link = _multiItem.Uid;
        AddMarkerEvent(item, LockdownEvent);
        return true;
    }

    /// <summary>The marker events Source-X puts ON a locked-down or secured item
    /// ("EVENTS +ei_house_lockdown" / "+ei_house_secure", CItemMulti.cpp:1779/1856).
    ///
    /// They are not scripts the pack defines — they are how it RECOGNISES such an
    /// item: the housing pack clears a house by asking each item
    /// <c>&lt;isevent.ei_house_lockdown&gt;</c> and then calling unlockitem/release on it
    /// (Scripts-X house_functions.scp:297/881/885/927/930). Without the marker
    /// nothing in that pack can tell a locked item from a loose one.</summary>
    public const string LockdownEvent = "ei_house_lockdown";
    public const string SecureEvent = "ei_house_secure";

    private static void AddMarkerEvent(Item item, string name)
    {
        var rid = Core.Types.ResourceId.FromString(name, Core.Enums.ResType.Events);
        if (!item.Events.Contains(rid))
            item.Events.Add(rid);
    }

    private static void RemoveMarkerEvent(Item item, string name) =>
        item.Events.Remove(Core.Types.ResourceId.FromString(name, Core.Enums.ResType.Events));

    /// <summary>Inside-the-house-region test for lockdown/secure targets.
    /// A house with no realized region (bare tests) accepts everything.</summary>
    private bool IsInsideHouse(Core.Types.Point3D pos)
    {
        if (_regionUid == 0) return true;
        var region = Objects.ObjBase.ResolveWorld?.Invoke()?.FindRegionByUid(_regionUid);
        return region == null || region.Contains(pos);
    }

    /// <summary>Release a locked down item (Source-X UnlockItem).</summary>
    public bool ReleaseLockdown(Serial itemUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_lockdowns.Remove(itemUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        if (item != null)
        {
            item.ClearAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
            if (item.Link == _multiItem.Uid)
                item.Link = Serial.Invalid;
            RemoveMarkerEvent(item, LockdownEvent);
        }
        return true;
    }

    /// <summary>Secure a container in the house (Source-X Secure: ATTR_SECURE
    /// on the container + link to the multi).</summary>
    public bool SecureContainer(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (_lockdowns.Contains(containerUid)) return false;
        if (_secureContainers.Count >= MaxSecure) return false;
        if (_secureContainers.Contains(containerUid)) return false;
        var secureItem = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        if (secureItem == null || secureItem.IsDeleted || !secureItem.IsOnGround || secureItem.IsEquipped)
            return false;
        if (secureItem.ItemType is not (ItemType.Container or ItemType.ContainerLocked))
            return false;
        if (!IsInsideHouse(secureItem.Position)) return false;
        _secureContainers.Add(containerUid);
        secureItem.SetAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
        secureItem.Link = _multiItem.Uid;
        AddMarkerEvent(secureItem, SecureEvent);
        return true;
    }

    /// <summary>Release a secured container.</summary>
    public bool ReleaseSecure(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_secureContainers.Remove(containerUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        if (item != null)
        {
            item.ClearAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
            if (item.Link == _multiItem.Uid)
                item.Link = Serial.Invalid;
            RemoveMarkerEvent(item, SecureEvent);
        }
        return true;
    }

    /// <summary>Let go of every locked-down item and every secured container,
    /// without asking anyone's privileges (Source-X UnlockAllItems,
    /// CItemMulti.cpp:1804, and the Release loop beside it).
    ///
    /// This is what a house owes the world when it stops existing by any route
    /// other than a redeed. A locked-down item is NOT MOVABLE (ObjAttributes
    /// LockedDown fails Item.IsMovable), so one left behind by a deleted house is
    /// stuck in the world for good, still linked to a multi that is gone.</summary>
    /// <summary>Drop every entry of the lockdown list without touching the items -
    /// what Source-X's TransferSecuredToMovingCrate / TransferLockdownsToMovingCrate
    /// do to _lLockDowns (CItemMulti.cpp:1468/1494), used by the custom-house
    /// design begin.</summary>
    internal void ForgetAllLockdowns() => _lockdowns.Clear();

    public void ReleaseAllHoldings()
    {
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        foreach (var (uids, attr, marker) in new (IEnumerable<Serial>, Core.Enums.ObjAttributes, string)[]
        {
            (_lockdowns.ToList(), Core.Enums.ObjAttributes.LockedDown, LockdownEvent),
            (_secureContainers.ToList(), Core.Enums.ObjAttributes.Secure, SecureEvent),
        })
        {
            foreach (var uid in uids)
            {
                if (world?.FindItem(uid) is not { IsDeleted: false } item)
                    continue;
                item.ClearAttr(attr);
                if (item.Link == _multiItem.Uid)
                    item.Link = Serial.Invalid;
                RemoveMarkerEvent(item, marker);
            }
        }
        _lockdowns.Clear();
        _secureContainers.Clear();
    }

    /// <summary>Restore a persisted lockdown/secure on world load WITHOUT the priv
    /// or capacity checks. A saved house may legitimately hold more than the
    /// current MaxLockdowns/MaxSecure (config or storage changed since the save);
    /// re-running the normal checks would silently drop the overflow entries.</summary>
    public void LockdownForLoad(Serial itemUid) => _lockdowns.Add(itemUid);
    public void SecureForLoad(Serial containerUid) => _secureContainers.Add(containerUid);

    /// <summary>Check if an item is locked down or secured.</summary>
    public bool IsLockedDown(Serial itemUid) => _lockdowns.Contains(itemUid);
    public bool IsSecured(Serial itemUid) => _secureContainers.Contains(itemUid);

    /// <summary>Add a house component item UID.</summary>
    public void AddComponent(Serial uid)
    {
        if (uid.IsValid && !_components.Contains(uid))
            _components.Add(uid);
    }
    public bool RemoveComponent(Serial uid) => _components.Remove(uid);

    /// <summary>Add a vendor NPC to the house.</summary>
    public void AddVendor(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _vendors.Contains(uid)) return;
        RevokeListedPrivileges(uid);
        _vendors.Add(uid);
    }
    public bool RemoveVendor(Serial uid) => _vendors.Remove(uid);
    public IReadOnlyList<Serial> Vendors => _vendors;

    /// <summary>Transfer ownership to another character.</summary>
    public void TransferOwnership(Serial newOwnerUid)
    {
        if (!newOwnerUid.IsValid) return;
        RevokeListedPrivileges(newOwnerUid);
        _owner = newOwnerUid;
        Refresh();
    }

    /// <summary>Refresh the house (reset decay timer).</summary>
    public void Refresh()
    {
        _lastRefreshTick = Environment.TickCount64;
        _decayStage = HouseDecayStage.LikeNew;
    }

    /// <summary>Source-X @Redeed (CItemMulti::Redeed, CItemMulti.cpp:1195), fired on
    /// the MULTI while it is still standing: SRC is the redeeming character, ARGO1 the
    /// new deed, ARGN1 the deed's id, ARGN2 starts at 1 (move everything to the moving
    /// crate) and ARGN3 at the caller's move-to-bank choice; both are read back.
    /// RETURN 1 suppresses the deed; the teardown happens anyway. Null (or a null
    /// result) means no script uses the trigger - and then, as upstream, nothing is
    /// moved to a crate at all.</summary>
    public static Func<Item, TriggerArgs, TriggerResult?>? OnRedeed { get; set; }

    /// <summary>The housing engine this house is registered with, when it is. Lets the
    /// item-level script keys (OWNER, GUILD) reach the shared ownership operations.</summary>
    internal HousingEngine? Engine { get; set; }

    /// <summary>Convenience: redeed with no SRC, no message, deed to the pack.</summary>
    public Item? Redeed(GameWorld world) => Redeed(world, source: null, moveToBank: false);

    /// <summary>Source-X CItemMulti::Redeed(fDisplayMsg, fMoveToBank, uidRedeedingChar),
    /// CItemMulti.cpp:1195, step for step. Returns the deed that was handed out, or
    /// null when the house was already redeeded, @Redeed suppressed the deed, or
    /// there was no player to hand it to - in which case, as upstream, the multi is
    /// left standing (stripped of its components) and can be redeeded again.</summary>
    public Item? Redeed(GameWorld world, Character? source, bool moveToBank)
    {
        if (_redeeded) return null;   // GetKeyNum("REMOVED")

        var deed = world.CreateItem();
        deed.BaseId = 0x14F0; // ITEMID_DEED1
        deed.ItemType = ItemType.Deed;
        if (_multiItem.ItemType == ItemType.MultiCustom)
            deed.SetTag("CUSTOMHOUSE", "1");
        deed.Name = _multiItem.Name + " deed";
        deed.SetTag("HOUSE_MULTI_UUID", _multiItem.Uuid.ToString("D"));
        deed.SetTag("HOUSE_MULTI_BASEID", _multiItem.BaseId.ToString());

        var args = new TriggerArgs
        {
            ItemSrc = _multiItem, CharSrc = source, O1 = deed,
            N1 = deed.BaseId, N2 = 1, N3 = moveToBank ? 1 : 0,
        };
        bool transferAll = false;
        TriggerResult? result = OnRedeed?.Invoke(_multiItem, args);
        if (result.HasValue)
        {
            if (args.N2 == 0)
                moveToBank = false;
            else
            {
                transferAll = true;
                moveToBank = args.N3 != 0;
            }
        }

        RemoveAllComponents(world);
        if (transferAll)
            TransferAllItemsToMovingCrate(world);

        var owner = _owner.IsValid ? world.FindChar(_owner) : null;
        if ((source == null || !source.IsPlayer) && (owner == null || !owner.IsPlayer))
        {
            // No player to redeed to: upstream returns here, before the deed is placed
            // and before the multi is deleted (CItemMulti.cpp:1254).
            world.RemoveItem(deed);
            return null;
        }

        if (moveToBank)
            TransferMovingCrateToBank(world);

        if (result == TriggerResult.True || deed.IsDeleted)
        {
            if (!deed.IsDeleted) world.RemoveItem(deed);
            deed = null;
        }
        if (deed != null)
        {
            deed.Hue = _multiItem.Hue;
            deed.More1 = _multiItem.BaseId;          // m_itDeed.m_Type
            if (_multiItem.IsAttr(ObjAttributes.Magic))
                deed.SetAttr(ObjAttributes.Magic);
            var recipient = owner ?? source!;
            if (moveToBank)
            {
                var bank = recipient.GetEquippedItem(Layer.BankBox) ?? recipient.Backpack;
                if (bank == null || !bank.TryAddItem(deed))
                    world.PlaceItemWithDecay(deed, recipient.Position);
            }
            else if (recipient.Backpack == null || !recipient.Backpack.TryAddItem(deed))
            {
                world.PlaceItemWithDecay(deed, recipient.Position);   // ItemBounce
            }
        }

        _redeeded = true;   // SetKeyNum("REMOVED", 1)
        // Delete(): the destructor lets go of whatever is still locked down or
        // secured, in place, and forgets the moving crate - it does not move it
        // (CItemMulti.cpp:88-128). The engine's deletion handler does the rest.
        ReleaseAllHoldings();
        _movingCrate = Serial.Invalid;
        world.RemoveItem(_multiItem);
        return deed;
    }

    /// <summary>Source-X RemoveAllComponents: the structure's own items go.</summary>
    private void RemoveAllComponents(GameWorld world)
    {
        foreach (var compUid in _components.ToList())
        {
            var item = world.FindItem(compUid);
            if (item != null && !item.IsDeleted)
                world.RemoveItem(item);
        }
        _components.Clear();
    }

    /// <summary>Source-X TransferAllItemsToMovingCrate(TRANSFER_ALL), CItemMulti.cpp:
    /// 1351: the lockdowns, the secured containers and every other item standing in
    /// the house region (but the guild stone, the multi, its crate and its components)
    /// go into the moving crate, which sits at house Z - 20. An empty crate is deleted.
    /// The client cannot show more than MaxContainerItems in one container, so what
    /// does not fit goes into a further crate beside the first.</summary>
    private void TransferAllItemsToMovingCrate(GameWorld world)
    {
        var crate = GetMovingCrate(create: true);
        if (crate == null) return;
        var crates = new List<Item> { crate };

        void Put(Item item)
        {
            if (item.IsDeleted) return;
            if (crates[^1].TryAddItem(item)) { item.ClearDecay(); return; }
            var extra = world.CreateItem();
            extra.BaseId = MovingCrateId;
            extra.ItemType = ItemType.Container;
            extra.Name = "a moving crate";
            world.PlaceItem(extra, crate.Position);
            crates.Add(extra);
            _overflowCrates.Add(extra);
            if (extra.TryAddItem(item)) item.ClearDecay();
        }

        // TransferLockdownsToMovingCrate / TransferSecuredToMovingCrate.
        foreach (var (uids, attr, marker) in new (List<Serial>, ObjAttributes, string)[]
        {
            (_lockdowns.ToList(), ObjAttributes.LockedDown, LockdownEvent),
            (_secureContainers.ToList(), ObjAttributes.Secure, SecureEvent),
        })
        {
            foreach (var uid in uids)
            {
                if (world.FindItem(uid) is not { IsDeleted: false } item) continue;
                RemoveMarkerEvent(item, marker);
                Put(item);
                item.ClearAttr(attr);
                item.Link = Serial.Invalid;
            }
        }
        _lockdowns.Clear();
        _secureContainers.Clear();

        // Everything else standing in this house's region.
        var footprint = _regionUid != 0 ? world.FindRegionByUid(_regionUid) : null;
        if (footprint != null)
        {
            var found = new HashSet<Item>();
            foreach (var rect in footprint.Rects)
            {
                int range = Math.Max(rect.X2 - rect.X1, rect.Y2 - rect.Y1) / 2 + 1;
                var center = new Point3D(
                    (short)((rect.X1 + rect.X2) / 2), (short)((rect.Y1 + rect.Y2) / 2),
                    _multiItem.Z, _multiItem.MapIndex);
                foreach (var loose in world.GetItemsInRange(center, range))
                {
                    if (loose.IsDeleted || !loose.IsOnGround) continue;
                    if (!footprint.Contains(loose.Position)) continue;
                    if (loose.ItemType == ItemType.StoneGuild) continue;
                    if (loose == _multiItem || crates.Contains(loose) || _components.Contains(loose.Uid)) continue;
                    if (loose.IsAttr(ObjAttributes.Static)) continue;   // map statics are not world items upstream
                    found.Add(loose);
                }
            }
            foreach (var loose in found)
                Put(loose);
        }

        if (crate.Contents.Count == 0)
        {
            _movingCrate = Serial.Invalid;
            world.RemoveItem(crate);
        }
    }

    /// <summary>Source-X TransferMovingCrateToBank (CItemMulti.cpp:1522): a crate
    /// holding something goes into the owner's bank; an empty one is deleted. Without
    /// an owner nothing happens.</summary>
    private void TransferMovingCrateToBank(GameWorld world)
    {
        var crate = ResolveMovingCrate();
        var owner = _owner.IsValid ? world.FindChar(_owner) : null;
        if (crate == null || owner == null)
            return;
        if (crate.Contents.Count == 0)
        {
            _movingCrate = Serial.Invalid;
            world.RemoveItem(crate);
            return;
        }
        var bank = owner.GetEquippedItem(Layer.BankBox);
        if (bank == null)
            return;
        bank.TryAddItem(crate);
        // Overflow crates made beside it travel with it.
        foreach (var extra in _overflowCrates)
            if (!extra.IsDeleted) bank.TryAddItem(extra);
        _overflowCrates.Clear();
    }

    private readonly List<Item> _overflowCrates = [];
}

/// <summary>
/// Multi definition registry. Loads from multi.mul files.
/// </summary>
public sealed class MultiRegistry
{
    private readonly Dictionary<ushort, MultiDef> _defs = [];

    public void Register(MultiDef def) => _defs[def.Id] = def;
    public MultiDef? Get(ushort id) => _defs.GetValueOrDefault(id);
    public int Count => _defs.Count;

    /// <summary>
    /// Load multi definitions from MapData's MultiReader.
    /// House multis typically start at 0x0 in multi.mul.
    /// Common house IDs: small stone (0x64), small plaster (0x66), etc.
    /// </summary>
    public int LoadFromMapData(MapData.MapDataManager mapData, int maxId = 0x3000)
    {
        int loaded = 0;
        for (int id = 0; id < maxId; id++)
        {
            var mulDef = mapData.GetMulti(id);
            if (mulDef == null || mulDef.Components.Length == 0) continue;

            var def = new MultiDef { Id = (ushort)id };
            foreach (var comp in mulDef.Components)
            {
                def.Components.Add(new MultiComponent
                {
                    TileId = comp.TileId,
                    DeltaX = comp.XOffset,
                    DeltaY = comp.YOffset,
                    DeltaZ = comp.ZOffset,
                    Visible = comp.IsVisible
                });
            }
            def.RecalcBounds();
            Register(def);
            loaded++;
        }
        return loaded;
    }

    /// <summary>Merge script [MULTIDEF] metadata (NAME / TYPE / BaseStorage / BaseVendors)
    /// onto the geometry loaded from multi.mul, keyed by the shared multi id. Source-X
    /// keeps both under one CItemBaseMulti; SphereNet had only the binary geometry, so
    /// placed structures used a blank name and a hardcoded storage default. Metadata for
    /// an id with no geometry is skipped (it cannot be placed). Returns the merge count.</summary>
    public int MergeScriptMetadata(ResourceHolder resources)
    {
        int merged = 0;
        foreach (var link in resources.GetAllResources())
        {
            if (link.Id.Type != Core.Enums.ResType.MultiDef || link.StoredKeys == null)
                continue;
            var def = Get((ushort)link.Id.Index);
            if (def == null)
                continue;

            foreach (var key in link.StoredKeys)
            {
                string arg = key.Arg.Trim();
                switch (key.Key.ToUpperInvariant())
                {
                    case "NAME":
                        if (arg.Length > 0) def.Name = arg;
                        break;
                    case "TYPE":
                        def.MultiTypeName = arg;
                        break;
                    case "BASESTORAGE":
                        if (int.TryParse(arg, out int st)) def.BaseStorage = st;
                        break;
                    case "BASEVENDORS":
                        if (int.TryParse(arg, out int bv)) def.BaseVendors = bv;
                        break;
                    case "REGIONFLAGS":
                        // Sphere scripts write these as leading-zero hex (02080 =
                        // Safe|NoBuild), so parse them the way every other flag field
                        // in the pack is parsed.
                        def.RegionFlags = (RegionFlag)Objects.ObjBase.ParseHexOrDecUInt(arg);
                        break;
                    case "MULTIREGION":
                        {
                            // "x1,y1,x2,y2[,map]", inclusive on every edge
                            // (SetMultiRegion, CItemBase.cpp:1936). Fewer than four
                            // numbers is not a rectangle and upstream ignores it.
                            var mr = arg.Split([',', ' ', '	'],
                                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (mr.Length >= 4 &&
                                short.TryParse(mr[0], out short rx1) &&
                                short.TryParse(mr[1], out short ry1) &&
                                short.TryParse(mr[2], out short rx2) &&
                                short.TryParse(mr[3], out short ry2))
                            {
                                def.ScriptRegion = (Math.Min(rx1, rx2), Math.Min(ry1, ry2),
                                                    Math.Max(rx1, rx2), Math.Max(ry1, ry2));
                            }
                        }
                        break;
                    case "SHIPSPEED":
                        {
                            // SHIPSPEED=period,tiles (period in tenths of a second).
                            var sp = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (sp.Length >= 2 &&
                                int.TryParse(sp[0], out int period) && period > 0 &&
                                int.TryParse(sp[1], out int tiles) && tiles > 0)
                            {
                                def.ShipSpeedPeriodTenths = period;
                                def.ShipSpeedTiles = tiles;
                            }
                        }
                        break;
                }
            }
            merged++;
        }
        return merged;
    }
}

/// <summary>
/// Housing engine: placement, deed, access control.
/// Maps to CItemMulti::Multi_Create flow in Source-X.
/// </summary>
public sealed class HousingEngine
{
    private readonly GameWorld _world;
    private readonly MultiRegistry _multiDefs;

    /// <summary>Source-X AUTOHOUSEKEYS: generate a key when a house is placed or
    /// changes owner. Off, the doors answer to the house privilege alone.</summary>
    public bool AutoHouseKeys { get; set; } = true;
    private readonly Dictionary<Serial, House> _houses = [];

    public int MaxHousesPerPlayer { get; set; } = 1;
    public int MaxHousesPerAccount { get; set; } = 1;

    /// <summary>Source-X @AddMulti notification for the owner's scripted
    /// CMultiStorage. SphereNet's native registry remains authoritative.</summary>
    /// <summary>A redeed has just put the deed in a player's pack. Upstream
    /// announces new container content to every client that has the container
    /// open (CItemContainer::ContentAdd -> CClient::addContents); without it the
    /// deed is on the server but the open backpack does not show it until it is
    /// closed and reopened.</summary>
    public Action<Character, Item>? OnDeedDelivered { get; set; }

    public Action<Character, Item, HousePriv>? OnAddMulti { get; set; }

    public HousingEngine(GameWorld world, MultiRegistry multiDefs)
    {
        _world = world;
        _multiDefs = multiDefs;
        world.ObjectDeleting += OnWorldObjectDeleting;
    }

    /// <summary>Multi item deleted OUTSIDE the redeed path (.nuke, .remove,
    /// script REMOVE). Source-X ~CItemMulti tears the whole structure down:
    /// keys removed, components deleted, region unrealized, erased from
    /// g_World.m_Multis — otherwise the registry keeps a ghost house that
    /// still counts against MaxHousesPerPlayer/Account. No deed is created
    /// (a destroyed house is not refunded). Engine-driven paths unregister
    /// before deleting the multi, so this only fires for external deletions.</summary>
    private void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        if (obj is not Item it) return;

        // A stone that dies takes its structure storage with it, and that storage
        // runs SetGuild(0) on every multi it lists (CMultiStorage destructor,
        // CItemMulti.cpp:3520).
        if (it.ItemType is ItemType.StoneGuild or ItemType.StoneTown && _guilds?.GetGuild(it.Uid) is { } dyingGuild)
            OnGuildRemoved(dyingGuild);

        if (!_houses.TryGetValue(it.Uid, out var house) || !ReferenceEquals(house.MultiItem, it))
            return;
        _houses.Remove(it.Uid);
        house.Engine = null;
        // The multi destructor's SetGuild(0) (CItemMulti.cpp:56): its own guild's
        // storage loses it.
        UnlinkGuild(house);
        // The multi destructor lets go of what it had locked down and secured, in
        // place (UnlockAllItems / Release, CItemMulti.cpp:88-107), and only FORGETS
        // its moving crate (SetMovingCrate(CUID()), :124): the crate stays where it
        // stands with whatever is in it.
        house.ReleaseAllHoldings();
        house.MovingCrate = Serial.Invalid;
        var owner = house.Owner.IsValid ? _world.FindChar(house.Owner) : null;
        // CItemMulti::Delete hands the multi back out of the owner's storage, which
        // runs @DelMulti (CItemMulti.cpp:146).
        if (owner is { IsPlayer: true }) OnDelMulti?.Invoke(owner, it);
        RemoveStructureKeys(owner, it.Uid);
        var ownerMemory = owner?.Memory_FindObjTypes(it.Uid, MemoryType.Guard);
        if (owner != null && ownerMemory != null)
            owner.Memory_ClearTypes(ownerMemory, MemoryType.Guard);
        foreach (var compUid in house.Components)
        {
            var comp = _world.FindItem(compUid);
            if (comp != null && !comp.IsDeleted)
                _world.RemoveItem(comp);
        }
        RemoveHouseRegion(house);
    }

    public House? GetHouse(Serial multiItemUid) =>
        _houses.GetValueOrDefault(multiItemUid);

    /// <summary>Register an already-placed multi item as a house instance,
    /// reading ownership from its HOUSE.OWNER tag. Called by the
    /// MULTICREATE script verb right after SERV.NEWITEM so the region
    /// tracker knows about the new house before the next save cycle.</summary>
    public House? RegisterExistingMulti(Item multiItem)
    {
        if (multiItem.ItemType is not (ItemType.Multi or ItemType.MultiCustom))
            return null;
        if (_houses.ContainsKey(multiItem.Uid))
            return _houses[multiItem.Uid];
        var house = CreateHouseFromTags(multiItem);
        if (house == null) return null;
        _houses[multiItem.Uid] = house;
        house.Engine = this;
        CreateHouseRegion(house);
        return house;
    }

    /// <summary>
    /// Place a new house at the given position.
    /// Returns null if placement is invalid.
    /// When <paramref name="customFoundation"/> is true the multi becomes a
    /// customizable foundation (ItemType.MultiCustom): no real component items
    /// are materialized — the client renders the foundation multi and the
    /// committed design (0xD8) itself, and server walk geometry comes from
    /// WalkCheck's virtual multi/design components.
    /// </summary>
    /// <summary>Fired before a house is placed (Source-X @HouseCheck). Args: the
    /// placing character and the chosen anchor point. Return true to VETO the
    /// placement (the engine's own NoBuild/footprint/terrain checks ran first).</summary>
    public static Func<Character, Point3D, bool>? OnHouseCheck { get; set; }

    public House? PlaceHouse(Character owner, ushort multiId, Point3D position,
        bool customFoundation = false, bool magic = false)
        => PlaceHouse(owner, multiId, position, out _, customFoundation, magic);

    /// <summary>Whether a multi id is a customizable foundation (script MULTIDEF
    /// TYPE=t_multi_custom). Lets deed handling detect custom houses from the resolved
    /// definition rather than only a CUSTOMHOUSE deed tag (B13).</summary>
    public bool IsCustomFoundation(ushort multiId) =>
        _multiDefs.Get(multiId)?.MultiTypeName.Equals("t_multi_custom", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Place a house, reporting the specific failure reason (B4).</summary>
    public House? PlaceHouse(Character owner, ushort multiId, Point3D position,
        out PlacementFailure failure, bool customFoundation = false, bool magic = false)
    {
        failure = PlacementFailure.None;

        if (MaxHousesPerPlayer >= 0 && GetHousesByOwner(owner.Uid).Count >= MaxHousesPerPlayer)
        { failure = PlacementFailure.PlayerLimitReached; return null; }

        if (MaxHousesPerAccount >= 0 && GetHouseCountForAccount(owner) >= MaxHousesPerAccount)
        { failure = PlacementFailure.AccountLimitReached; return null; }

        var def = _multiDefs.Get(multiId);
        if (def == null) { failure = PlacementFailure.MultiDefinitionMissing; return null; }
        var (mapWidth, mapHeight) = _world.MapData?.GetMapSize(position.Map) ?? (7168, 4096);
        if (position.X + def.MinX < 0 || position.Y + def.MinY < 0 ||
            position.X + def.MaxX >= mapWidth || position.Y + def.MaxY >= mapHeight)
        { failure = PlacementFailure.OutOfMap; return null; }

        // Check placement area
        if (!magic && !CanPlaceHouse(position, def))
        { failure = PlacementFailure.LocationBlocked; return null; }

        // @HouseCheck (Source-X) — a script may veto placement after the engine's
        // built-in checks pass (e.g. custom land claims / faction rules).
        if (OnHouseCheck != null && OnHouseCheck(owner, position))
        { failure = PlacementFailure.ScriptVeto; return null; }

        // Create multi item
        var multiItem = _world.CreateItem();
        multiItem.BaseId = multiId;
        multiItem.Name = def.Name;
        multiItem.ItemType = customFoundation ? ItemType.MultiCustom : ItemType.Multi;
        if (magic)
            multiItem.SetAttr(ObjAttributes.Magic);
        _world.PlaceItem(multiItem, position);

        // Create house instance
        var house = new House(multiItem) { Owner = owner.Uid };

        // Apply script [MULTIDEF] BaseStorage when defined, instead of the flat default
        // (small house 489, keep/castle larger, etc.).
        if (def.BaseStorage > 0)
            house.BaseStorage = def.BaseStorage;
        // BaseVendors was parsed off the [MULTIDEF] but never reached the placed
        // house, so the vendor budget was always zero and MAXVENDORS unreadable.
        if (def.BaseVendors > 0)
            house.BaseVendors = def.BaseVendors;

        if (customFoundation)
        {
            // Source-X CItemMultiCustom constructor: ResetStructure + CommitChanges,
            // so the committed design starts as the foundation's visible pieces.
            CustomHousingEngine.InitializeFoundationDesign(multiItem, def, _world.MapData);
        }
        else
        {
            // Materialize ONLY the invisible placeholder components
            // (flags == 0): doors, the house sign — the working server items.
            // The client draws every visible (flags != 0) wall/roof part
            // itself from its multi.mul, because the multi item is sent with
            // the multi data type (Source-X CItemMulti: components are
            // dynamic items, the walls are never materialized server-side).
            foreach (var comp in def.Components)
            {
                if (comp.Visible) continue;

                var compItem = _world.CreateItem();
                compItem.BaseId = comp.TileId;
                var compPos = new Point3D(
                    (short)(position.X + comp.DeltaX),
                    (short)(position.Y + comp.DeltaY),
                    (sbyte)(position.Z + comp.DeltaZ),
                    position.Map
                );
                // Source-X links every component back to the multi (m_uidLink);
                // the door lock code IS the house link, so the house key opens it.
                compItem.Link = multiItem.Uid;
                compItem.SetAttr(ObjAttributes.Move_Never);
                compItem.SetTag("HOUSE_UID", multiItem.Uid.Value.ToString());
                if (compItem.ItemType == ItemType.SignGump)
                    multiItem.Link = compItem.Uid;
                _world.PlaceItem(compItem, compPos);
                house.AddComponent(compItem.Uid);
            }
        }

        _houses[multiItem.Uid] = house;
        house.Engine = this;
        OnAddMulti?.Invoke(owner, multiItem, HousePriv.Owner);
        CreateHouseRegion(house);
        owner.Memory_AddObjTypes(multiItem.Uid, MemoryType.Guard);

        // Source-X Multi_Setup GenerateKey: a house key into the owner's pack
        // and a spare copy into the bank. There was previously NO key at all —
        // the "you have the key" messages could never be true for houses.
        CreateHouseKey(owner, multiItem, toBank: false);
        CreateHouseKey(owner, multiItem, toBank: true);
        return house;
    }

    /// <summary>Create one house key linked to the multi (TAG.LINK is the code
    /// the lock-matching uses; Link mirrors it for scripts).</summary>
    private void CreateHouseKey(Character owner, Item multiItem, bool toBank)
    {
        // AUTOHOUSEKEYS off means the shard runs on house privilege alone and hands
        // out no key at all (CItemMulti.cpp:417). The engine generated one either
        // way, so a shard that turned the setting off still got keys - and the
        // reference pack's own door script branches on the setting to decide whether
        // to do its own access check.
        if (!AutoHouseKeys)
            return;

        var key = _world.CreateItem();
        key.BaseId = 0x100F; // gold key
        key.ItemType = ItemType.Key;
        key.Name = "a house key";
        // AUTONEWBIEKEYS (CItemMulti.cpp:1107).
        if (Item.AutoNewbieKeys)
            key.SetAttr(ObjAttributes.Newbie);
        key.SetTag("LINK", multiItem.Uid.Value.ToString());
        key.Link = multiItem.Uid;

        var dest = toBank ? owner.GetEquippedItem(Layer.BankBox) : owner.Backpack;
        dest ??= owner.Backpack;
        if (dest == null || !dest.TryAddItem(key))
            _world.PlaceItemWithDecay(key, owner.Position);
    }

    /// <summary>Transfer through the engine so authority, ownership caps,
    /// persistent guard memory and structure keys change atomically.</summary>
    public bool TransferHouse(House house, Character requestor, Character newOwner)
    {
        if (!_houses.TryGetValue(house.MultiItem.Uid, out var registered) || registered != house)
            return false;
        if (requestor.PrivLevel < PrivLevel.GM && house.Owner != requestor.Uid)
            return false;
        if (!newOwner.IsPlayer || newOwner.Uid == house.Owner)
            return false;

        int playerCount = _houses.Values.Count(h => h != house && h.Owner == newOwner.Uid);
        if (MaxHousesPerPlayer >= 0 && playerCount >= MaxHousesPerPlayer)
            return false;
        int accountCount = GetHouseCountForAccount(newOwner, house);
        if (MaxHousesPerAccount >= 0 && accountCount >= MaxHousesPerAccount)
            return false;

        SetOwner(house, newOwner.Uid);
        CreateHouseKey(newOwner, house.MultiItem, toBank: false);
        CreateHouseKey(newOwner, house.MultiItem, toBank: true);
        return true;
    }

    /// <summary>Source-X CItemMulti::SetOwner (CItemMulti.cpp:651) - the operation
    /// the multi's OWNER key runs. The old owner loses the structure (@DelMulti, its
    /// keys and the guard memory); the new owner is struck from every other list of
    /// the house (RevokePrivs) and gains it (@AddMulti, guard memory). No caps and no
    /// privilege check: this is server authority, as upstream. No key is minted
    /// either - upstream's SetOwner does not, scripts use ADDKEY. An invalid uid just
    /// clears the owner. Capacity follows by itself: the per-player and per-account
    /// counts are read from the owners in the registry.</summary>
    public bool SetOwner(House house, Serial newOwnerUid)
    {
        if (!_houses.TryGetValue(house.MultiItem.Uid, out var registered) || registered != house)
            return false;
        if (newOwnerUid == house.Owner)
            return true;

        var multi = house.MultiItem;
        var oldOwner = house.Owner.IsValid ? _world.FindChar(house.Owner) : null;
        house.Owner = Serial.Invalid;
        if (oldOwner != null)
        {
            OnDelMulti?.Invoke(oldOwner, multi);
            RemoveStructureKeys(oldOwner, multi.Uid);
            var oldMemory = oldOwner.Memory_FindObjTypes(multi.Uid, MemoryType.Guard);
            if (oldMemory != null)
                oldOwner.Memory_ClearTypes(oldMemory, MemoryType.Guard);
        }
        if (!newOwnerUid.IsValid)
            return true;

        house.TransferOwnership(newOwnerUid);
        var newOwner = _world.FindChar(newOwnerUid);
        if (newOwner != null)
        {
            OnAddMulti?.Invoke(newOwner, multi, HousePriv.Owner);
            newOwner.Memory_AddObjTypes(multi.Uid, MemoryType.Guard);
        }
        return true;
    }

    /// <summary>The guild/town records structures are listed in (Source-X CItemStone
    /// multi storage). Set by the host; null leaves guild links out of it.</summary>
    public Guild.GuildManager? Guilds
    {
        get => _guilds;
        set
        {
            if (_guilds != null) _guilds.GuildRemoved -= OnGuildRemoved;
            _guilds = value;
            if (_guilds != null) _guilds.GuildRemoved += OnGuildRemoved;
        }
    }
    private Guild.GuildManager? _guilds;

    /// <summary>Source-X CItemMulti::SetGuild (CItemMulti.cpp:711): the old stone's
    /// storage loses the multi, the new one gains it. An invalid uid just clears it.
    /// Only a guild stone can be named (SHL_GUILD, CItemMulti.cpp:3105).</summary>
    public bool SetGuild(House house, Serial stoneUid)
    {
        if (stoneUid.IsValid && _world.FindItem(stoneUid) is not { ItemType: ItemType.StoneGuild })
            return false;
        UnlinkGuild(house);
        if (!stoneUid.IsValid)
            return true;
        house.GuildStone = stoneUid;
        _guilds?.GetGuild(stoneUid)?.AddHouse(house.MultiItem.Uid);
        return true;
    }

    private void UnlinkGuild(House house)
    {
        var old = house.GuildStone;
        house.GuildStone = Serial.Invalid;
        if (old.IsValid)
            _guilds?.GetGuild(old)?.DelMulti(house.MultiItem.Uid);
    }

    /// <summary>A guild record went away (disband, or its stone was deleted). Its
    /// storage runs SetGuild(0) on every house it lists (CMultiStorage destructor,
    /// CItemMulti.cpp:3533).</summary>
    private void OnGuildRemoved(Guild.GuildDef guild)
    {
        foreach (var uid in guild.Houses.ToList())
            if (_houses.GetValueOrDefault(uid) is { } house)
                UnlinkGuild(house);
    }

    private void RemoveStructureKeys(Character? owner, Serial structureUid)
    {
        if (owner == null) return;
        RemoveStructureKeys(owner.Backpack, structureUid);
        var bank = owner.GetEquippedItem(Layer.BankBox);
        if (bank != owner.Backpack)
            RemoveStructureKeys(bank, structureUid);
    }

    private void RemoveStructureKeys(Item? container, Serial structureUid)
    {
        if (container == null) return;
        foreach (var child in container.Contents.ToList())
        {
            RemoveStructureKeys(child, structureUid);
            if (child.ItemType is not (ItemType.Key or ItemType.Keyring) || child.Link != structureUid)
                continue;
            container.RemoveItem(child);
            _world.RemoveItem(child);
        }
    }

    /// <summary>Host hook: is another SHIP hull at this point? Wired to the
    /// ship engine so house placement can't overlap a docked hull.</summary>
    public Func<Point3D, bool>? IsShipAt { get; set; }

    /// <summary>Check if a house can be placed at the given position
    /// (Source-X CItemMulti::Multi_Create intensive loop).</summary>
    public bool CanPlaceHouse(Point3D position, MultiDef def)
    {
        var md = _world.MapData;

        // Source-X re-checks REGION_FLAG_NOBUILDING over the footprint plus a
        // +5-tile margin (CItemMulti.cpp:3404-3429) — not just the anchor.
        for (short mx = (short)(def.MinX - 5); mx <= def.MaxX + 5; mx++)
        {
            for (short my = (short)(def.MinY - 5); my <= def.MaxY + 5; my++)
            {
                var marginPos = new Point3D(
                    (short)(position.X + mx), (short)(position.Y + my),
                    position.Z, position.Map);
                var region = _world.FindRegion(marginPos);
                if (region != null && region.IsFlag(RegionFlag.NoBuild))
                    return false;
            }
        }

        for (short dx = def.MinX; dx <= def.MaxX; dx++)
        {
            for (short dy = def.MinY; dy <= def.MaxY; dy++)
            {
                var checkPos = new Point3D(
                    (short)(position.X + dx),
                    (short)(position.Y + dy),
                    position.Z,
                    position.Map
                );

                if (FindHouseAt(checkPos) != null)
                    return false;
                // A docked ship hull blocks the footprint too.
                if (IsShipAt != null && IsShipAt(checkPos))
                    return false;

                // Reject blocked terrain (water, mountains, impassable statics)
                // in the footprint — a house can't sit on un-walkable ground.
                if (md != null && !md.IsPassable(checkPos.Map, checkPos.X, checkPos.Y, checkPos.Z))
                    return false;

                // Source-X CItemMulti::Multi_Create — every footprint cell's surface
                // must be flat relative to the placement Z (abs(cellZ - z) > 4 rejects).
                // A house cannot straddle a slope/cliff.
                if (md != null)
                {
                    md.GetAverageZ(checkPos.Map, checkPos.X, checkPos.Y, out _, out int cellZ, out _);
                    if (Math.Abs(cellZ - position.Z) > 4)
                        return false;
                }

                // Living chars block the cell — GM staff pass through
                // (Source-X CItemMulti.cpp:3330). NOTE: range 1 + exact X/Y
                // filter; the old GetCharsInRange(pos, 0) returned nothing,
                // so the char check was silently a no-op.
                foreach (var ch in _world.GetCharsInRange(checkPos, 1))
                {
                    if (ch.X != checkPos.X || ch.Y != checkPos.Y) continue;
                    if (ch.IsDead || ch.PrivLevel >= PrivLevel.GM) continue;
                    return false;
                }

                // Blocking loose items reject the cell (Source-X rejects
                // CAN_I_BLOCK statics in the intensive loop).
                foreach (var it in _world.GetItemsInRange(checkPos, 1))
                {
                    if (it.X != checkPos.X || it.Y != checkPos.Y) continue;
                    if (it.IsDeleted || it.ContainedIn.IsValid) continue;
                    var idef = Definitions.DefinitionLoader.GetItemDef(it.BaseId);
                    if (idef != null && idef.Can.HasFlag(CanFlags.I_Block))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>Source-X @DelMulti (CItemMulti.cpp:3677): fired on the owner when a
    /// house leaves them, with the multi as ARGO1.</summary>
    public static Action<Character, Item>? OnDelMulti { get; set; }

    /// <summary>Remove a house (redeed or demolish).</summary>
    public Item? RemoveHouse(Serial multiItemUid, Character requestor)
    {
        if (!_houses.TryGetValue(multiItemUid, out var house))
            return null;

        if (house.GetPriv(requestor.Uid) != HousePriv.Owner &&
            requestor.PrivLevel < PrivLevel.GM)
            return null;

        return RedeedCore(house, requestor, moveToBank: false);
    }

    /// <summary>Script/verb-driven redeed (server authority — no priv gate):
    /// the FULL teardown, so the registry entry and the dynamic house region
    /// never leak. Source-X REDEED showMsg,moveToBank (CItemMulti.cpp:2388): SRC
    /// redeems, the owner (or SRC when there is none) receives the deed.</summary>
    public Item? RedeedFromScript(Serial multiItemUid, bool displayMessage = false,
        bool moveToBank = false, Character? source = null)
    {
        if (!_houses.TryGetValue(multiItemUid, out var house))
            return null;
        return RedeedCore(house, source, moveToBank);
    }

    /// <summary>The one redeed every route takes (client demolish, script REDEED,
    /// collapse): Source-X CItemMulti::Redeed (CItemMulti.cpp:1195) on the standing
    /// house. Its final Delete() reaches this engine's deletion handler, which does
    /// what upstream's Delete and destructor do - @DelMulti on the owner, keys, guard
    /// memory, SetGuild(0), the region.</summary>
    private Item? RedeedCore(House house, Character? source, bool moveToBank)
    {
        var deed = house.Redeed(_world, source, moveToBank);
        if (deed == null)
            return null;
        var recipient = (house.Owner.IsValid ? _world.FindChar(house.Owner) : null) ?? source;
        if (recipient?.Backpack != null && deed.ContainedIn == recipient.Backpack.Uid)
            OnDeedDelivered?.Invoke(recipient, deed);
        return deed;
    }
    /// <summary>Find the house that contains the given position.</summary>
    public House? FindHouseAt(Point3D pos)
    {
        foreach (var house in _houses.Values)
        {
            var mi = house.MultiItem;
            var def = _multiDefs.Get(mi.BaseId);
            if (def == null) continue;
            if (mi.MapIndex != pos.Map) continue;

            if (pos.X >= mi.X + def.MinX && pos.X <= mi.X + def.MaxX &&
                pos.Y >= mi.Y + def.MinY && pos.Y <= mi.Y + def.MaxY)
                return house;
        }
        return null;
    }

    /// <summary>Add a mutually-exclusive ban and move an occupant outside the
    /// footprint immediately; the movement gate prevents re-entry.</summary>
    public bool BanFromHouse(House house, Serial targetUid)
    {
        if (!_houses.TryGetValue(house.MultiItem.Uid, out var registered) || registered != house)
            return false;
        if (!house.AddBan(targetUid)) return false;
        var target = _world.FindChar(targetUid);
        if (target != null && FindHouseAt(target.Position) == house)
            EjectFromHouse(house, target);
        return true;
    }

    private void EjectFromHouse(House house, Character target)
    {
        var mi = house.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;
        var candidates = new[]
        {
            new Point3D(mi.X, (short)(mi.Y + def.MaxY + 1), mi.Z, mi.MapIndex),
            new Point3D((short)(mi.X + def.MaxX + 1), mi.Y, mi.Z, mi.MapIndex),
            new Point3D(mi.X, (short)(mi.Y + def.MinY - 1), mi.Z, mi.MapIndex),
            new Point3D((short)(mi.X + def.MinX - 1), mi.Y, mi.Z, mi.MapIndex),
        };
        var destination = candidates.FirstOrDefault(p =>
            _world.MapData == null || _world.MapData.IsPassable(p.Map, p.X, p.Y, p.Z));
        if (destination == default)
            destination = candidates[0];
        _world.MoveCharacter(target, destination);
    }

    /// <summary>Source-X CItemMulti::MultiRealizeRegion — give the house a dynamic
    /// world region matching its footprint, flagged REGION_FLAG_HOUSE and inheriting
    /// the containing region's flags (guarded / pvp / safe carry through, with House
    /// added). The region is the smallest-area one over the footprint, so FindRegion
    /// resolves to it inside the house. Static (houses never move), so it is created
    /// once on placement/load and torn down on collapse/redeed. Idempotent.</summary>
    private void CreateHouseRegion(House house)
    {
        if (house.RegionUid != 0) return; // already realized
        var region = RealizeMultiRegion(house.MultiItem);
        if (region != null)
            house.RegionUid = region.Uid;
    }

    /// <summary>Give a MULTI its region: the footprint its definition declares, the
    /// flags and events its own record carries, and the tags with them.
    ///
    /// The region belongs to the STRUCTURE, not to any ownership record of it -
    /// upstream realizes it whenever the multi is put in the world, owner or not
    /// (MultiRealizeRegion, CItemMulti.cpp:191/571). Hanging it off the house record
    /// meant a structure out of a classic save - which names no owner in the shape this
    /// engine reads - had no region at all: its Safe and NoBuild flags did nothing, its
    /// @Enter and @Step scripts never fired, and nothing inside it could be told apart
    /// from the open field around it.</summary>
    private Region? RealizeMultiRegion(Objects.Items.Item mi)
    {
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return null;

        var bounds = def.RegionBounds;
        short x1 = (short)(mi.X + bounds.MinX);
        short y1 = (short)(mi.Y + bounds.MinY);
        short x2 = (short)(mi.X + bounds.MaxX);
        short y2 = (short)(mi.Y + bounds.MaxY);
        var center = new Point3D((short)(mi.X), (short)(mi.Y), mi.Z, mi.MapIndex);

        // Parent = the region this footprint sits in BEFORE the house region exists.
        var parent = _world.FindRegion(center);

        var region = new Region
        {
            Name = string.IsNullOrEmpty(mi.Name) ? "house" : mi.Name,
            MapIndex = mi.MapIndex,
            // The definition's own REGIONFLAGS are the BASE the region starts from
            // (MultiRealizeRegion, CItemMulti.cpp:232) - a structure declared Safe or
            // NoBuild in its MULTIDEF used to lose that the moment it was placed.
            Flags = def.RegionFlags | RegionFlag.House | RegionFlag.InheritParentFlags,
            P = center,
        };
        region.AddRect(x1, y1, x2, y2);
        if (parent != null)
            region.InheritFromParent(parent);
        // The record's REGION.EVENTS / REGION.FLAGS / REGION.TAG.<name> lines are the
        // structure's own region state (SHL_REGION, CItemMulti.cpp:3011): its
        // @Enter/@Step scripts, its flags and the tags a script inside reads.
        MultiRegionRecord.Apply(mi, region);

        _world.AddRegion(region);
        return region;
    }

    /// <summary>The live region of a structure this engine realized - a house's, or
    /// that of a multi with no house record - or null.</summary>
    public Region? FindMultiRegion(Serial multiUid)
    {
        uint regionUid = _houses.TryGetValue(multiUid, out var house) ? house.RegionUid
            : _multiRegions.TryGetValue(multiUid, out uint loose) ? loose : 0;
        return regionUid != 0 ? _world.FindRegionByUid(regionUid) : null;
    }


    /// <summary>Tear down the dynamic region created by <see cref="CreateHouseRegion"/>.</summary>
    private void RemoveHouseRegion(House house)
    {
        if (house.RegionUid == 0) return;
        _world.RemoveRegion(house.RegionUid);
        house.RegionUid = 0;
    }

    public bool CanPickupHouseItem(Character actor, Item item)
    {
        if (actor.PrivLevel >= PrivLevel.GM)
            return true;

        var check = item;
        int depth = 0;
        while (check != null && depth < 16)
        {
            foreach (var house in _houses.Values)
            {
                if (house.IsLockedDown(check.Uid) || house.IsSecured(check.Uid))
                    return house.CanLockdown(actor.Uid);
            }
            if (!check.ContainedIn.IsValid) break;
            check = _world.FindItem(check.ContainedIn);
            depth++;
        }

        return true;
    }

    /// <summary>Find all houses owned by a character.</summary>
    public List<House> GetHousesByOwner(Serial ownerUid)
    {
        var result = new List<House>();
        foreach (var house in _houses.Values)
        {
            if (house.Owner == ownerUid)
                result.Add(house);
        }
        return result;
    }

    /// <summary>Count houses owned by any character on the owner's account.</summary>
    public int GetHouseCountForAccount(Character owner) => GetHouseCountForAccount(owner, null);

    private int GetHouseCountForAccount(Character owner, House? exclude)
    {
        var account = Character.ResolveAccountForChar?.Invoke(owner.Uid);
        if (account == null)
            return _houses.Values.Count(h => h != exclude && h.Owner == owner.Uid);

        var accountChars = new HashSet<Serial>();
        for (int i = 0; i < 7; i++)
        {
            var uid = account.GetCharSlot(i);
            if (uid.IsValid)
                accountChars.Add(uid);
        }

        int count = 0;
        foreach (var house in _houses.Values)
        {
            if (house != exclude && accountChars.Contains(house.Owner))
                count++;
        }
        return count;
    }

    /// <summary>Get all registered houses.</summary>
    public IEnumerable<House> AllHouses => _houses.Values;

    /// <summary>Total house count.</summary>
    public int HouseCount => _houses.Count;

    public MultiRegistry MultiDefs => _multiDefs;

    // --- Decay System ---

    /// <summary>Decay stage interval in ms; 0 (the default) turns house decay off.
    ///
    /// Source-X has no house decay: the multi's timer is never armed
    /// ("// ??? SetTimeout( GetDecayTime()); house decay ?", CItemMulti.cpp:389).
    /// A 24-hour default here wore every house down and redeeded it after six
    /// days without its owner, legacy imports included.</summary>
    public long DecayStageIntervalMs { get; set; }

    /// <summary>
    /// Tick house decay. Called periodically from game loop.
    /// Returns list of houses that collapsed (IDOC → destroyed).
    /// </summary>
    public List<House> OnTickDecay()
    {
        if (DecayStageIntervalMs <= 0)
            return [];
        long now = Environment.TickCount64;
        var collapsed = new List<House>();

        foreach (var house in _houses.Values)
        {
            long elapsed = now - house.LastRefreshTick;
            int stages = (int)(elapsed / DecayStageIntervalMs);

            var newStage = stages switch
            {
                0 => HouseDecayStage.LikeNew,
                1 => HouseDecayStage.SlightlyWorn,
                2 => HouseDecayStage.SomewhatWorn,
                3 => HouseDecayStage.FairlyWorn,
                4 => HouseDecayStage.GreatlyWorn,
                _ => HouseDecayStage.InDangerOfCollapsing
            };

            house.DecayStage = newStage;

            // IDOC exceeded — collapse after one more interval
            if (stages >= 6)
                collapsed.Add(house);
        }

        // Remove collapsed houses
        foreach (var house in collapsed)
            RedeedCore(house, source: null, moveToBank: false);

        return collapsed;
    }

    /// <summary>Refresh a house (owner enters). Resets decay timer.</summary>
    public void RefreshHouse(House house)
    {
        house.Refresh();
    }

    /// <summary>
    /// Called when a character enters a house area.
    /// Auto-refreshes if the character is the owner.
    /// </summary>
    public void OnCharacterEnterHouse(Character ch, House house)
    {
        if (ch.Uid == house.Owner)
            house.Refresh();
    }

    // --- Save/Load via item TAGs ---

    /// <summary>The native per-house tags older SphereNet saves carry. They are still
    /// read on load; the next save replaces them with the Source-X keys.</summary>
    private static readonly string[] LegacyHouseTags =
    [
        "HOUSE.OWNER", "HOUSE.OWNER_UUID", "HOUSE.TYPE", "HOUSE.STORAGE", "HOUSE.BASEVENDORS",
        "HOUSE.INCREASEDSTORAGE", "HOUSE.LOCKDOWNSPERCENT", "HOUSE.GUILD", "HOUSE.MOVINGCRATE",
        "HOUSE.COOWNERS", "HOUSE.FRIENDS", "HOUSE.BANS", "HOUSE.ACCESS", "HOUSE.VENDORS",
        "HOUSE.LOCKDOWNS", "HOUSE.SECURE", "HOUSE.COMPONENTS",
    ];

    /// <summary>
    /// Put each house's state on its multi item in the shape Source-X writes it
    /// (CItemMulti::r_Write, CItemMulti.cpp:2558): GUILD, OWNER, HOUSETYPE, one
    /// ADDCOOWNER / ADDFRIEND / ADDACCESS / ADDBAN / ADDCOMP / SECURE / LOCKITEM /
    /// ADDVENDOR line per uid, LOCKDOWNSPERCENT, MOVINGCRATE, BASEVENDORS, BASESTORAGE,
    /// INCREASEDSTORAGE - each only when set, as upstream. The world saver writes these
    /// keys out under their own names. Called before world save.
    /// </summary>
    public void SerializeAllToTags()
    {
        static string Hex(Serial s) => $"0{s.Value:x}";
        static void SetOrRemove(Item item, string key, string? value)
        {
            if (value != null) item.SetTag(key, value);
            else item.RemoveTag(key);
        }
        static string? List(IEnumerable<Serial> uids)
        {
            var parts = uids.Where(u => u.NamesAnObject).Select(Hex).ToList();
            return parts.Count > 0 ? string.Join(",", parts) : null;
        }

        foreach (var (_, house) in _houses)
        {
            var item = house.MultiItem;
            foreach (var legacy in LegacyHouseTags)
                item.RemoveTag(legacy);

            SetOrRemove(item, "GUILD", house.GuildStone.NamesAnObject ? Hex(house.GuildStone) : null);
            SetOrRemove(item, "OWNER", house.Owner.NamesAnObject ? Hex(house.Owner) : null);
            SetOrRemove(item, "HOUSETYPE", house.Type != HouseType.Private ? $"0{(byte)house.Type:x}" : null);
            SetOrRemove(item, "ADDCOOWNER", List(house.CoOwners));
            SetOrRemove(item, "ADDFRIEND", List(house.Friends));
            SetOrRemove(item, "ADDACCESS", List(house.AccessList));
            SetOrRemove(item, "ADDBAN", List(house.Bans));
            SetOrRemove(item, "ADDCOMP", List(house.Components));
            SetOrRemove(item, "SECURE", List(house.SecureContainers));
            SetOrRemove(item, "LOCKITEM", List(house.Lockdowns));
            SetOrRemove(item, "LOCKDOWNSPERCENT", house.LockdownsPercent != 0 ? house.LockdownsPercent.ToString() : null);
            SetOrRemove(item, "MOVINGCRATE", house.ResolveMovingCrate() is { } crate ? Hex(crate.Uid) : null);
            SetOrRemove(item, "ADDVENDOR", List(house.Vendors));
            SetOrRemove(item, "BASEVENDORS", house.BaseVendors != 0 ? house.BaseVendors.ToString() : null);
            SetOrRemove(item, "BASESTORAGE", house.BaseStorage != 0 ? house.BaseStorage.ToString() : null);
            SetOrRemove(item, "INCREASEDSTORAGE", house.IncreasedStorage != 0 ? house.IncreasedStorage.ToString() : null);

            // House decay is a SphereNet option Source-X does not have (its multis
            // never decay, CItemMulti.cpp:389). Only a shard that turned it on keeps
            // its decay clock, as ordinary tags.
            if (DecayStageIntervalMs > 0)
            {
                item.SetTag("HOUSE.DECAY_STAGE", ((byte)house.DecayStage).ToString());
                long elapsed = Environment.TickCount64 - house.LastRefreshTick;
                item.SetTag("HOUSE.DECAY_ELAPSED", Math.Max(0, elapsed).ToString());
            }
            else
            {
                item.RemoveTag("HOUSE.DECAY_STAGE");
                item.RemoveTag("HOUSE.DECAY_ELAPSED");
            }

            // The LIVE region goes into the record (CItemMulti::r_Write ->
            // CRegion::r_WriteBody), not whatever the item happened to carry.
            if (FindMultiRegion(item.Uid) is { } region)
                MultiRegionRecord.Store(item, region);
        }

        // A structure with no house record has a region of its own all the same.
        foreach (var (multiUid, regionUid) in _multiRegions)
        {
            if (_world.FindItem(multiUid) is { } loose && _world.FindRegionByUid(regionUid) is { } looseRegion)
                MultiRegionRecord.Store(loose, looseRegion);
        }
    }
    /// <summary>
    /// Rebuild house instances from multi items after world load.
    /// Scans all items of type Multi and reads their HOUSE.* TAGs.
    /// </summary>
    public void DeserializeFromWorld()
    {
        foreach (var existing in _houses.Values.ToList())
            RemoveHouseRegion(existing);
        _houses.Clear();
        foreach (uint regionUid in _multiRegions.Values)
            _world.RemoveRegion(regionUid);
        _multiRegions.Clear();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not Item item) continue;
            // Both classic (Multi) and customizable-foundation (MultiCustom)
            // houses must be rebuilt — PlaceHouse stamps custom foundations as
            // MultiCustom, so reading only Multi dropped every custom house from
            // the registry after a restart (it then decayed/escaped management).
            if (item.ItemType is not (ItemType.Multi or ItemType.MultiCustom)) continue;
            var house = CreateHouseFromTags(item);
            if (house == null)
            {
                // No ownership record - a structure out of a classic save, which writes
                // its owner in its own script's shape rather than in the tag this
                // engine keeps. It is still a structure: its region is realized from
                // the multi itself, exactly as upstream does when the multi is put in
                // the world (CItemMulti.cpp:571).
                var loose = RealizeMultiRegion(item);
                if (loose != null)
                    _multiRegions[item.Uid] = loose.Uid;
                continue;
            }

            _houses[item.Uid] = house;
            house.Engine = this;
            CreateHouseRegion(house);
        }
    }

    /// <summary>Regions realized for multis that carry no house record of their own.
    /// Kept so a second rebuild replaces them rather than stacking a new region on top
    /// of the old one.</summary>
    private readonly Dictionary<Serial, uint> _multiRegions = [];

    /// <summary>Rebuild a house from its multi item. Every multi is a house, owned or
    /// not, as every Source-X multi is a CItemMulti. The keys are the ones Source-X
    /// writes (CItemMulti::r_Write, CItemMulti.cpp:2558) - kept by the item loader as
    /// tags of the same name - with the native HOUSE.* tags of older SphereNet saves
    /// read where those are missing. A value the record does not carry comes from the
    /// definition, as upstream's constructor takes it (CItemMulti.cpp:38).</summary>
    private House? CreateHouseFromTags(Item item)
    {
        uint ownerVal = 0;
        if (item.TryGetTag("OWNER", out string? ownerStr) || item.TryGetTag("HOUSE.OWNER", out ownerStr))
            ownerVal = ParseHexSerial(ownerStr);
        var ownerUid = new Serial(ownerVal);
        var house = new House(item) { Owner = ownerUid.NamesAnObject ? ownerUid : Serial.Invalid };
        var def = _multiDefs.Get(item.BaseId);

        if (TryTagInt(item, "HOUSETYPE", "HOUSE.TYPE", out int ht) && ht is >= 0 and <= byte.MaxValue &&
            Enum.IsDefined(typeof(HouseType), (byte)ht))
            house.Type = (HouseType)ht;

        if (TryTagInt(item, "BASESTORAGE", "HOUSE.STORAGE", out int stor) && stor >= 0)
            house.BaseStorage = stor;
        else if (def is { BaseStorage: > 0 })
            house.BaseStorage = def.BaseStorage;
        if (TryTagInt(item, "BASEVENDORS", "HOUSE.BASEVENDORS", out int bv) && bv >= 0)
            house.BaseVendors = bv;
        else if (def is { BaseVendors: > 0 })
            house.BaseVendors = def.BaseVendors;
        if (TryTagInt(item, "INCREASEDSTORAGE", "HOUSE.INCREASEDSTORAGE", out int inc))
            house.IncreasedStorage = inc;
        if (TryTagInt(item, "LOCKDOWNSPERCENT", "HOUSE.LOCKDOWNSPERCENT", out int lp))
            house.LockdownsPercent = lp;
        RestoreGuildLink(item, house);

        // The crate item is an ordinary world item and loads on its own; the house
        // just re-adopts it. ResolveMovingCrate drops the link if the item is gone,
        // so a crate that was emptied and deleted does not come back as a ghost uid.
        if (item.TryGetTag("MOVINGCRATE", out string? classicCrate))
        {
            // SHL_MOVINGCRATE (CItemMulti.cpp:3029): 1 means "make one".
            uint crateUid = ParseHexSerial(classicCrate);
            if (crateUid == 1)
                house.GetMovingCrate(create: true);
            else if (crateUid != 0 && _world.FindItem(new Serial(crateUid)) is { IsDeleted: false } crate)
                house.AssignMovingCrate(crate);
        }
        else if (item.TryGetTag("HOUSE.MOVINGCRATE", out string? crateStr))
            house.MovingCrate = new Serial(ParseHexSerial(crateStr));
        if (item.TryGetTag("HOUSE.DECAY_STAGE", out string? dsStr) && byte.TryParse(dsStr, out byte ds) &&
            ds <= (byte)HouseDecayStage.InDangerOfCollapsing)
            house.DecayStage = (HouseDecayStage)ds;
        if (item.TryGetTag("HOUSE.DECAY_ELAPSED", out string? elStr) && long.TryParse(elStr, out long el) && el > 0)
            house.LastRefreshTick = Environment.TickCount64 - el;

        // Native lists first, then the classic one-uid-per-line keys
        // (ADDCOOWNER / ADDFRIEND / ... , CItemMulti.cpp:2580-2682).
        ParseSerialList(item, "HOUSE.COOWNERS", uid => house.AddCoOwner(uid));
        ParseSerialList(item, "ADDCOOWNER", uid => house.AddCoOwner(uid));
        ParseSerialList(item, "HOUSE.FRIENDS", uid => house.AddFriend(uid));
        ParseSerialList(item, "ADDFRIEND", uid => house.AddFriend(uid));
        ParseSerialList(item, "HOUSE.ACCESS", uid => house.AddAccess(uid));
        ParseSerialList(item, "ADDACCESS", uid => house.AddAccess(uid));
        ParseSerialList(item, "HOUSE.VENDORS", uid => house.AddVendor(uid));
        ParseSerialList(item, "ADDVENDOR", uid => house.AddVendor(uid));
        ParseSerialList(item, "HOUSE.BANS", uid => house.AddBan(uid));
        ParseSerialList(item, "ADDBAN", uid => house.AddBan(uid));

        void RestoreLockdown(Serial uid, bool classic)
        {
            var locked = _world.FindItem(uid);
            if (locked == null || locked.IsDeleted) return;
            // SHL_LOCKITEM takes anything but a container (CItemMulti.cpp:3190).
            if (classic && locked.ItemType is ItemType.Container or ItemType.ContainerLocked) return;
            house.LockdownForLoad(uid);
            locked.SetAttr(ObjAttributes.LockedDown);
            locked.Link = item.Uid;
        }
        void RestoreSecure(Serial uid, bool classic)
        {
            var secure = _world.FindItem(uid);
            if (secure == null || secure.IsDeleted) return;
            // SHL_SECURE takes containers only (CItemMulti.cpp:3202).
            if (classic && secure.ItemType is not (ItemType.Container or ItemType.ContainerLocked)) return;
            house.SecureForLoad(uid);
            secure.SetAttr(ObjAttributes.Secure);
            secure.Link = item.Uid;
        }
        ParseSerialList(item, "HOUSE.LOCKDOWNS", uid => RestoreLockdown(uid, classic: false));
        ParseSerialList(item, "LOCKITEM", uid => RestoreLockdown(uid, classic: true));
        ParseSerialList(item, "HOUSE.SECURE", uid => RestoreSecure(uid, classic: false));
        ParseSerialList(item, "SECURE", uid => RestoreSecure(uid, classic: true));

        void RestoreComponent(Serial uid)
        {
            var component = _world.FindItem(uid);
            if (component == null) return;
            component.Link = item.Uid;
            component.SetAttr(ObjAttributes.Move_Never);
            component.SetTag("HOUSE_UID", item.Uid.Value.ToString());
            if (component.ItemType == ItemType.SignGump && !item.Link.IsValid)
                item.Link = component.Uid;
            house.AddComponent(uid);
        }
        ParseSerialList(item, "HOUSE.COMPONENTS", RestoreComponent);
        ParseSerialList(item, "ADDCOMP", RestoreComponent);

        AdoptClassicFixtures(item, house);

        return house;
    }

    /// <summary>HOUSE.GUILD (native) or GUILD (classic, CItemMulti.cpp:2566). The
    /// classic key only names a guild stone (SHL_GUILD, :3105). With the guild
    /// records available the link is made from both ends, and a link to a guild that
    /// no longer exists is dropped rather than restored.</summary>
    private void RestoreGuildLink(Item item, House house)
    {
        bool classic = true;
        if (!item.TryGetTag("GUILD", out string? guildStr))
        {
            if (!item.TryGetTag("HOUSE.GUILD", out guildStr))
                return;
            classic = false;
        }
        var stone = new Serial(ParseHexSerial(guildStr));
        if (!stone.NamesAnObject)
            return;
        if (classic && _world.FindItem(stone) is not { IsDeleted: false, ItemType: ItemType.StoneGuild })
            return;
        if (_guilds != null)
        {
            var guild = _guilds.GetGuild(stone);
            if (guild == null)
                return;
            guild.AddHouse(item.Uid);
        }
        house.GuildStone = stone;
    }

    /// <summary>A classic custom house lists the doors and teleporters its commit
    /// made as ordinary components, each tagged FIXTURE=&lt;multi uid&gt;
    /// (CommitChanges, CItemMultiCustom.cpp:382). The custom-house engine tracks
    /// them in COMMIT_FIXTURES - the list it hides from the rendered design and
    /// replaces on the next commit - so they are adopted into it here, or the next
    /// commit would stand a second door beside every old one.</summary>
    private void AdoptClassicFixtures(Item multi, House house)
    {
        const string FixturesTag = "COMMIT_FIXTURES";
        if (multi.ItemType != ItemType.MultiCustom || multi.TryGetTag(FixturesTag, out _))
            return;
        var fixtures = new List<string>();
        foreach (var uid in house.Components)
        {
            if (_world.FindItem(uid) is not { } comp ||
                !comp.TryGetTag("FIXTURE", out string? owner) ||
                ParseHexSerial(owner) != multi.Uid.Value)
                continue;
            fixtures.Add(uid.Value.ToString());
        }
        if (fixtures.Count > 0)
            multi.SetTag(FixturesTag, string.Join(',', fixtures));
    }

    /// <summary>Read a native tag, else its classic counterpart, as a Sphere number.</summary>
    private static bool TryTagInt(Item item, string nativeKey, string classicKey, out int value)
    {
        value = 0;
        if (!item.TryGetTag(nativeKey, out string? raw) && !item.TryGetTag(classicKey, out raw))
            return false;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (int.TryParse(raw.Trim(), out value))
            return true;
        if (ScriptNumber.TryParseToken(raw.Trim(), out long parsed) && parsed is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)parsed;
            return true;
        }
        return false;
    }

    private static uint ParseHexSerial(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0;
        str = str.Trim();
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || str.StartsWith('0'))
        {
            str = str.TrimStart('0').TrimStart('x', 'X');
            if (uint.TryParse(str, System.Globalization.NumberStyles.HexNumber, null, out uint val))
                return val;
        }
        if (uint.TryParse(str, out uint dec)) return dec;
        return 0;
    }

    private static void ParseSerialList(Item item, string tagName, Action<Serial> action)
    {
        if (!item.TryGetTag(tagName, out string? listStr) || string.IsNullOrWhiteSpace(listStr))
            return;
        foreach (var part in listStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint val = ParseHexSerial(part);
            if (val != 0) action(new Serial(val));
        }
    }
}
