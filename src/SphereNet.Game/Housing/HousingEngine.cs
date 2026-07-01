using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;

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
    public List<MultiComponent> Components { get; } = [];

    // Bounding rect
    public short MinX { get; set; }
    public short MinY { get; set; }
    public short MaxX { get; set; }
    public short MaxY { get; set; }

    public void RecalcBounds()
    {
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

    private int _baseStorage = 400;
    private int _lockdownsPercent = 50;

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
    public int MaxLockdowns => _baseStorage * _lockdownsPercent / 100;
    public int MaxSecure => _baseStorage - MaxLockdowns;
    public IReadOnlyList<Serial> Components => _components;
    public int BaseStorage { get => _baseStorage; set => _baseStorage = value; }
    public long LastRefreshTick { get => _lastRefreshTick; set => _lastRefreshTick = value; }
    public HouseDecayStage DecayStage { get => _decayStage; set => _decayStage = value; }
    public uint RegionUid { get => _regionUid; set => _regionUid = value; }
    public IReadOnlyCollection<Serial> CoOwners => _coOwners;
    public IReadOnlyCollection<Serial> Friends => _friends;
    public IReadOnlyCollection<Serial> Bans => _bans;
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
        if (_coOwners.Contains(charUid)) return HousePriv.CoOwner;
        if (_friends.Contains(charUid)) return HousePriv.Friend;
        if (_bans.Contains(charUid)) return HousePriv.Ban;
        if (_accessList.Contains(charUid)) return HousePriv.AccessOnly;
        return HousePriv.None;
    }

    public bool AddCoOwner(Serial uid) => _coOwners.Add(uid);
    public bool RemoveCoOwner(Serial uid) => _coOwners.Remove(uid);
    public bool AddFriend(Serial uid) => _friends.Add(uid);
    public bool RemoveFriend(Serial uid) => _friends.Remove(uid);
    public bool AddBan(Serial uid) => _bans.Add(uid);
    public bool RemoveBan(Serial uid) => _bans.Remove(uid);
    public bool AddAccess(Serial uid) => _accessList.Add(uid);
    public bool RemoveAccess(Serial uid) => _accessList.Remove(uid);

    public bool CanAccess(Serial charUid)
    {
        var priv = GetPriv(charUid);
        return priv != HousePriv.Ban && priv != HousePriv.None;
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
        if (_lockdowns.Count >= MaxLockdowns) return false;
        _lockdowns.Add(itemUid);
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        if (item != null)
        {
            item.SetAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
            item.Link = _multiItem.Uid; // Source-X m_uidLink → the multi
        }
        return true;
    }

    /// <summary>Release a locked down item (Source-X UnlockItem).</summary>
    public bool ReleaseLockdown(Serial itemUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_lockdowns.Remove(itemUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        item?.ClearAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
        return true;
    }

    /// <summary>Secure a container in the house (Source-X Secure: ATTR_SECURE
    /// on the container + link to the multi).</summary>
    public bool SecureContainer(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (_secureContainers.Count >= MaxSecure) return false;
        if (_secureContainers.Contains(containerUid)) return false;
        _secureContainers.Add(containerUid);
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        if (item != null)
        {
            item.SetAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
            item.Link = _multiItem.Uid;
        }
        return true;
    }

    /// <summary>Release a secured container.</summary>
    public bool ReleaseSecure(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_secureContainers.Remove(containerUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        item?.ClearAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
        return true;
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
    public void AddComponent(Serial uid) => _components.Add(uid);

    /// <summary>Add a vendor NPC to the house.</summary>
    public void AddVendor(Serial uid) => _vendors.Add(uid);
    public bool RemoveVendor(Serial uid) => _vendors.Remove(uid);
    public IReadOnlyList<Serial> Vendors => _vendors;

    /// <summary>Transfer ownership to another character.</summary>
    public void TransferOwnership(Serial newOwnerUid)
    {
        _coOwners.Remove(newOwnerUid);
        _friends.Remove(newOwnerUid);
        _owner = newOwnerUid;
        Refresh();
    }

    /// <summary>Refresh the house (reset decay timer).</summary>
    public void Refresh()
    {
        _lastRefreshTick = Environment.TickCount64;
        _decayStage = HouseDecayStage.LikeNew;
    }

    /// <summary>Fired when a house is converted back to a deed (Source-X @Redeed).
    /// Arg: the freshly-created deed item.</summary>
    public static Action<Item>? OnRedeed { get; set; }

    /// <summary>Transfer ownership (deed back). Preserves multi UUID in deed tag.</summary>
    public Item? Redeed(GameWorld world)
    {
        if (_redeeded) return null;
        _redeeded = true;
        var deed = world.CreateItem();
        deed.BaseId = 0x14F0; // ITEMID_DEED1
        deed.Name = _multiItem.Name + " deed";
        deed.SetTag("HOUSE_MULTI_UUID", _multiItem.Uuid.ToString("D"));
        deed.SetTag("HOUSE_MULTI_BASEID", _multiItem.BaseId.ToString());
        // @Redeed (Source-X) — the house is now a deed item.
        OnRedeed?.Invoke(deed);

        // Remove all component items (the structure itself)
        foreach (var compUid in _components)
        {
            var item = world.FindItem(compUid);
            if (item != null)
                world.RemoveItem(item);
        }
        _components.Clear();

        // Source-X collapse: locked-down / secured PLAYER items are NOT structure
        // components (must not be deleted) and must NOT be scattered on the ground
        // to decay individually (item loss). They go into a MOVING CRATE — each
        // item is reparented into the crate (a secured container moves whole), and
        // the crate is delivered to the owner's bank box when reachable, otherwise
        // dropped on the house tile. Reparenting (not copying) means no dupe.
        var protectedUids = _lockdowns.Concat(_secureContainers).ToList();

        // Source-X TransferAllItemsToMovingCrate(TRANSFER_ALL): LOOSE items left
        // inside the house footprint also go to the crate — previously they were
        // orphaned on the ground (lost their house link, decayed away).
        var footprint = _regionUid != 0 ? world.FindRegionByUid(_regionUid) : null;
        if (footprint != null)
        {
            var protectedSet = new HashSet<Serial>(protectedUids);
            foreach (var rect in footprint.Rects)
            {
                int range = Math.Max(rect.X2 - rect.X1, rect.Y2 - rect.Y1) / 2 + 1;
                var center = new Point3D(
                    (short)((rect.X1 + rect.X2) / 2), (short)((rect.Y1 + rect.Y2) / 2),
                    _multiItem.Z, _multiItem.MapIndex);
                foreach (var loose in world.GetItemsInRange(center, range).ToList())
                {
                    if (loose.IsDeleted || !loose.IsOnGround) continue;
                    if (loose == _multiItem || _components.Contains(loose.Uid)) continue;
                    if (loose.IsAttr(Core.Enums.ObjAttributes.Static) ||
                        loose.IsAttr(Core.Enums.ObjAttributes.Move_Never)) continue;
                    if (!footprint.Contains(loose.Position)) continue;
                    protectedSet.Add(loose.Uid);
                }
            }
            protectedUids = protectedSet.ToList();
        }

        if (protectedUids.Count > 0)
        {
            var crate = world.CreateItem();
            crate.BaseId = 0x0E3D; // ITEMID_CRATE1 (wooden crate)
            crate.ItemType = Core.Enums.ItemType.Container;
            crate.Name = "a moving crate";

            foreach (var protUid in protectedUids)
            {
                var item = world.FindItem(protUid);
                if (item == null || item.IsDeleted) continue;
                // Detach from its current container or ground sector, then place it
                // in the crate (a fresh slot). Contents of a secured container ride
                // along inside it.
                if (item.ContainedIn.IsValid)
                    world.FindItem(item.ContainedIn)?.RemoveItem(item);
                else
                    world.HideFromSector(item);
                item.Position = new Point3D(0, 0, 0, crate.MapIndex);
                crate.AddItem(item);
                item.DecayTime = 0; // protected inside the crate
            }

            var owner = _owner.IsValid ? world.FindChar(_owner) : null;
            var bank = owner?.GetEquippedItem(Core.Enums.Layer.BankBox);
            if (bank != null)
                bank.AddItem(crate);
            else
                world.PlaceItemWithDecay(crate, _multiItem.Position);
        }
        _lockdowns.Clear();
        _secureContainers.Clear();

        // Remove the multi item itself
        world.RemoveItem(_multiItem);

        return deed;
    }
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
}

/// <summary>
/// Housing engine: placement, deed, access control.
/// Maps to CItemMulti::Multi_Create flow in Source-X.
/// </summary>
public sealed class HousingEngine
{
    private readonly GameWorld _world;
    private readonly MultiRegistry _multiDefs;
    private readonly Dictionary<Serial, House> _houses = [];

    public int MaxHousesPerPlayer { get; set; } = 1;
    public int MaxHousesPerAccount { get; set; } = 1;

    public HousingEngine(GameWorld world, MultiRegistry multiDefs)
    {
        _world = world;
        _multiDefs = multiDefs;
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
        if (!multiItem.TryGetTag("HOUSE.OWNER", out string? ownerStr) || string.IsNullOrEmpty(ownerStr))
            return null;
        uint ownerVal = ParseHexSerial(ownerStr);
        if (ownerVal == 0) return null;

        var house = new House(multiItem) { Owner = new Serial(ownerVal) };
        _houses[multiItem.Uid] = house;
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

    public House? PlaceHouse(Character owner, ushort multiId, Point3D position, bool customFoundation = false)
    {
        if (MaxHousesPerPlayer >= 0 && GetHousesByOwner(owner.Uid).Count >= MaxHousesPerPlayer)
            return null;

        if (MaxHousesPerAccount >= 0 && GetHouseCountForAccount(owner) >= MaxHousesPerAccount)
            return null;

        var def = _multiDefs.Get(multiId);
        if (def == null) return null;

        // Check placement area
        if (!CanPlaceHouse(position, def))
            return null;

        // @HouseCheck (Source-X) — a script may veto placement after the engine's
        // built-in checks pass (e.g. custom land claims / faction rules).
        if (OnHouseCheck != null && OnHouseCheck(owner, position))
            return null;

        // Create multi item
        var multiItem = _world.CreateItem();
        multiItem.BaseId = multiId;
        multiItem.Name = def.Name;
        multiItem.ItemType = customFoundation ? ItemType.MultiCustom : ItemType.Multi;
        _world.PlaceItem(multiItem, position);

        // Create house instance
        var house = new House(multiItem) { Owner = owner.Uid };

        if (customFoundation)
        {
            // Empty committed design at revision 1 — clients that query the
            // design (0xBF 0x1E) get a valid, empty 0xD8 stream.
            multiItem.Tags.Set(HouseDesign.RevisionTag, "1");
        }
        else
        {
            // Generate components
            foreach (var comp in def.Components)
            {
                if (!comp.Visible) continue;

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
                _world.PlaceItem(compItem, compPos);
                house.AddComponent(compItem.Uid);
            }
        }

        _houses[multiItem.Uid] = house;
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
        var key = _world.CreateItem();
        key.BaseId = 0x100F; // gold key
        key.ItemType = ItemType.Key;
        key.Name = "a house key";
        key.SetTag("LINK", multiItem.Uid.Value.ToString());
        key.Link = multiItem.Uid;

        var dest = toBank ? owner.GetEquippedItem(Layer.BankBox) : owner.Backpack;
        dest ??= owner.Backpack;
        if (dest != null)
            dest.AddItem(key);
        else
            _world.PlaceItemWithDecay(key, owner.Position);
    }

    /// <summary>Check if a house can be placed at the given position.</summary>
    public bool CanPlaceHouse(Point3D position, MultiDef def)
    {
        // Check for NOBUILDING region
        var region = _world.FindRegion(position);
        if (region != null && region.IsFlag(RegionFlag.NoBuild))
            return false;

        // Check for existing structures in the footprint
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

                // Reject blocked terrain (water, mountains, impassable statics)
                // in the footprint — a house can't sit on un-walkable ground.
                var md = _world.MapData;
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

                foreach (var ch in _world.GetCharsInRange(checkPos, 0))
                {
                    if (!ch.IsDead) return false;
                }
            }
        }

        return true;
    }

    /// <summary>Remove a house (redeed or demolish).</summary>
    public Item? RemoveHouse(Serial multiItemUid, Character requestor)
    {
        if (!_houses.TryGetValue(multiItemUid, out var house))
            return null;

        if (house.GetPriv(requestor.Uid) != HousePriv.Owner &&
            requestor.PrivLevel < PrivLevel.GM)
            return null;

        RemoveHouseRegion(house);
        var deed = house.Redeed(_world);
        _houses.Remove(multiItemUid);
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

    /// <summary>Source-X CItemMulti::MultiRealizeRegion — give the house a dynamic
    /// world region matching its footprint, flagged REGION_FLAG_HOUSE and inheriting
    /// the containing region's flags (guarded / pvp / safe carry through, with House
    /// added). The region is the smallest-area one over the footprint, so FindRegion
    /// resolves to it inside the house. Static (houses never move), so it is created
    /// once on placement/load and torn down on collapse/redeed. Idempotent.</summary>
    private void CreateHouseRegion(House house)
    {
        if (house.RegionUid != 0) return; // already realized
        var mi = house.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        short x1 = (short)(mi.X + def.MinX);
        short y1 = (short)(mi.Y + def.MinY);
        short x2 = (short)(mi.X + def.MaxX);
        short y2 = (short)(mi.Y + def.MaxY);
        var center = new Point3D((short)(mi.X), (short)(mi.Y), mi.Z, mi.MapIndex);

        // Parent = the region this footprint sits in BEFORE the house region exists.
        var parent = _world.FindRegion(center);

        var region = new Region
        {
            Name = string.IsNullOrEmpty(mi.Name) ? "house" : mi.Name,
            MapIndex = mi.MapIndex,
            Flags = RegionFlag.House | RegionFlag.InheritParentFlags,
            P = center,
        };
        region.AddRect(x1, y1, x2, y2);
        if (parent != null)
            region.InheritFromParent(parent);

        _world.AddRegion(region);
        house.RegionUid = region.Uid;
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
    public int GetHouseCountForAccount(Character owner)
    {
        var account = Character.ResolveAccountForChar?.Invoke(owner.Uid);
        if (account == null)
            return GetHousesByOwner(owner.Uid).Count;

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
            if (accountChars.Contains(house.Owner))
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

    /// <summary>Decay stage interval in ms (default: 24 hours real time).</summary>
    public long DecayStageIntervalMs { get; set; } = 24L * 60 * 60 * 1000;

    /// <summary>
    /// Tick house decay. Called periodically from game loop.
    /// Returns list of houses that collapsed (IDOC → destroyed).
    /// </summary>
    public List<House> OnTickDecay()
    {
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
        {
            RemoveHouseRegion(house);
            var deed = house.Redeed(_world);
            if (deed != null && house.Owner.IsValid)
            {
                var owner = _world.FindChar(house.Owner);
                if (owner?.Backpack != null)
                    owner.Backpack.AddItem(deed);
                else
                    _world.PlaceItem(deed, house.MultiItem.Position);
            }
            _houses.Remove(house.MultiItem.Uid);
        }

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

    /// <summary>
    /// Serialize house metadata to the multi item's TAGs for persistence.
    /// Called before world save.
    /// </summary>
    public void SerializeAllToTags()
    {
        foreach (var (uid, house) in _houses)
        {
            var item = house.MultiItem;
            item.SetTag("HOUSE.OWNER", $"0{house.Owner.Value:X}");
            var ownerObj = _world.FindObject(house.Owner);
            if (ownerObj != null)
                item.SetTag("HOUSE.OWNER_UUID", ownerObj.Uuid.ToString("D"));
            item.SetTag("HOUSE.TYPE", ((byte)house.Type).ToString());
            item.SetTag("HOUSE.STORAGE", house.BaseStorage.ToString());
            item.SetTag("HOUSE.DECAY_STAGE", ((byte)house.DecayStage).ToString());
            long elapsed = Environment.TickCount64 - house.LastRefreshTick;
            item.SetTag("HOUSE.DECAY_ELAPSED", Math.Max(0, elapsed).ToString());

            if (house.CoOwners.Count > 0)
                item.SetTag("HOUSE.COOWNERS", string.Join(",", house.CoOwners.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.COOWNERS");
            if (house.Friends.Count > 0)
                item.SetTag("HOUSE.FRIENDS", string.Join(",", house.Friends.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.FRIENDS");
            if (house.Bans.Count > 0)
                item.SetTag("HOUSE.BANS", string.Join(",", house.Bans.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.BANS");
            if (house.Lockdowns.Count > 0)
                item.SetTag("HOUSE.LOCKDOWNS", string.Join(",", house.Lockdowns.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.LOCKDOWNS");
            if (house.SecureContainers.Count > 0)
                item.SetTag("HOUSE.SECURE", string.Join(",", house.SecureContainers.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.SECURE");
            if (house.Components.Count > 0)
                item.SetTag("HOUSE.COMPONENTS", string.Join(",", house.Components.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.COMPONENTS");
        }
    }

    /// <summary>
    /// Rebuild house instances from multi items after world load.
    /// Scans all items of type Multi and reads their HOUSE.* TAGs.
    /// </summary>
    public void DeserializeFromWorld()
    {
        _houses.Clear();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not Item item) continue;
            // Both classic (Multi) and customizable-foundation (MultiCustom)
            // houses must be rebuilt — PlaceHouse stamps custom foundations as
            // MultiCustom, so reading only Multi dropped every custom house from
            // the registry after a restart (it then decayed/escaped management).
            if (item.ItemType is not (ItemType.Multi or ItemType.MultiCustom)) continue;
            if (!item.TryGetTag("HOUSE.OWNER", out string? ownerStr)) continue;

            uint ownerVal = ParseHexSerial(ownerStr);
            if (ownerVal == 0) continue;

            var house = new House(item) { Owner = new Serial(ownerVal) };

            if (item.TryGetTag("HOUSE.TYPE", out string? typeStr) && byte.TryParse(typeStr, out byte ht))
                house.Type = (HouseType)ht;
            if (item.TryGetTag("HOUSE.STORAGE", out string? storStr) && int.TryParse(storStr, out int stor))
                house.BaseStorage = stor;
            if (item.TryGetTag("HOUSE.DECAY_STAGE", out string? dsStr) && byte.TryParse(dsStr, out byte ds))
                house.DecayStage = (HouseDecayStage)ds;
            if (item.TryGetTag("HOUSE.DECAY_ELAPSED", out string? elStr) && long.TryParse(elStr, out long el) && el > 0)
                house.LastRefreshTick = Environment.TickCount64 - el;

            ParseSerialList(item, "HOUSE.COOWNERS", uid => house.AddCoOwner(uid));
            ParseSerialList(item, "HOUSE.FRIENDS", uid => house.AddFriend(uid));
            ParseSerialList(item, "HOUSE.BANS", uid => house.AddBan(uid));
            ParseSerialList(item, "HOUSE.LOCKDOWNS", uid => house.LockdownForLoad(uid));
            ParseSerialList(item, "HOUSE.SECURE", uid => house.SecureForLoad(uid));
            ParseSerialList(item, "HOUSE.COMPONENTS", uid => house.AddComponent(uid));

            _houses[item.Uid] = house;
            CreateHouseRegion(house);
        }
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
