using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;

namespace SphereNet.Game.Ships;

/// <summary>
/// Ship engine: placement, movement, rotation, save/load.
/// Maps to CCMultiMovable in Source-X CCMultiMovable.cpp and CItemShip in CItemShip.cpp.
/// </summary>
public sealed class ShipEngine
{
    private readonly GameWorld _world;
    private readonly MultiRegistry _multiDefs;
    private readonly MapDataManager? _mapData;
    private readonly Dictionary<Serial, Ship> _ships = [];

    public ShipEngine(GameWorld world, MultiRegistry multiDefs, MapDataManager? mapData)
    {
        _world = world;
        _multiDefs = multiDefs;
        _mapData = mapData;
    }

    public Ship? GetShip(Serial multiItemUid) =>
        _ships.GetValueOrDefault(multiItemUid);

    public int ShipCount => _ships.Count;
    public IEnumerable<Ship> AllShips => _ships.Values;

    /// <summary>Source-X CItemShip::Speak hook. Args: (Ship ship, string text).
    /// Program.cs delivers the tillerman line to nearby clients as a unicode
    /// speech packet so confirmations like "Aye, captain!" or "I cannot turn
    /// in such turbulent water" appear above the ship.</summary>
    public Action<Ship, string>? OnTillerSpeak { get; set; }

    /// <summary>Called after a ship moves so Program.cs can broadcast 0xF6.</summary>
    public Action<Ship>? OnShipMoved { get; set; }

    /// <summary>Called when a ship stops (Source-X @ShipStop fire site).</summary>
    public Action<Ship>? OnShipStopped { get; set; }

    /// <summary>Called when a ship turns to a new facing (Source-X @ShipTurn).</summary>
    public Action<Ship>? OnShipTurned { get; set; }

    /// <summary>Fired before a ship move crosses a region boundary (Source-X
    /// CCMultiMovable region check — item @RegionLeave/@RegionEnter live on
    /// movable multis, not on ordinary items). Args: ship, region being left
    /// (null = wilderness), region being entered (null = wilderness). Return
    /// false to block the move (script RETURN 1). Installed only when one of
    /// the two triggers is hooked, so unhooked shards pay a null check.</summary>
    public Func<Ship, World.Regions.Region?, World.Regions.Region?, bool>? OnShipRegionChange { get; set; }

    private void TillerSpeak(Ship ship, string key) =>
        OnTillerSpeak?.Invoke(ship, SphereNet.Game.Messages.ServerMessages.Get(key));

    // =====================================================================
    // Placement
    // =====================================================================

    /// <summary>
    /// Place a new ship at the given position.
    /// Returns null if placement is invalid.
    /// </summary>
    public Ship? PlaceShip(Character owner, ushort multiId, Point3D pos, Direction facing)
    {
        var def = _multiDefs.Get(multiId);
        if (def == null) return null;

        if (!CanPlaceShip(pos, def))
            return null;

        // Create multi item
        var multiItem = _world.CreateItem();
        multiItem.BaseId = multiId;
        multiItem.Name = def.Name;
        multiItem.ItemType = ItemType.Ship;
        _world.PlaceItem(multiItem, pos);

        var ship = new Ship(multiItem)
        {
            Owner = owner.Uid,
            DirFace = Normalize4Dir(facing),
            Anchored = true,
        };

        // Generate component items from multi definition
        foreach (var comp in def.Components)
        {
            if (!comp.Visible) continue;

            var compItem = _world.CreateItem();
            compItem.BaseId = comp.TileId;

            // Determine item type from tile ID
            compItem.ItemType = ClassifyShipComponent(comp.TileId);

            var compPos = new Point3D(
                (short)(pos.X + comp.DeltaX),
                (short)(pos.Y + comp.DeltaY),
                (sbyte)(pos.Z + comp.DeltaZ),
                pos.Map
            );
            _world.PlaceItem(compItem, compPos);
            ship.AddComponent(compItem);
        }

        _ships[multiItem.Uid] = ship;
        CreateShipRegion(ship);
        return ship;
    }

    /// <summary>Check if a ship can be placed at position (all footprint must be water).</summary>
    public bool CanPlaceShip(Point3D pos, MultiDef def)
    {
        if (_mapData == null) return true; // No map data = allow

        for (short dx = def.MinX; dx <= def.MaxX; dx++)
        {
            for (short dy = def.MinY; dy <= def.MaxY; dy++)
            {
                if (!IsWaterAt(pos.Map, (short)(pos.X + dx), (short)(pos.Y + dy)))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Remove a ship and convert back to deed.</summary>
    public Item? RemoveShip(Serial multiItemUid, Character requestor)
    {
        if (!_ships.TryGetValue(multiItemUid, out var ship))
            return null;

        if (ship.Owner != requestor.Uid && requestor.PrivLevel < PrivLevel.GM)
            return null;

        // Create deed — preserve ship UUID for identity continuity
        var deed = _world.CreateItem();
        deed.BaseId = 0x14F0; // ITEMID_DEED1
        deed.Name = ship.MultiItem.Name + " deed";
        deed.SetTag("SHIP_MULTI_UUID", ship.MultiItem.Uuid.ToString("D"));
        deed.SetTag("SHIP_MULTI_BASEID", ship.MultiItem.BaseId.ToString());

        // Remove all component items
        foreach (var compUid in ship.Components)
        {
            var item = _world.FindItem(compUid);
            if (item != null) _world.RemoveItem(item);
        }

        RemoveShipRegion(ship);
        _world.RemoveItem(ship.MultiItem);
        _ships.Remove(multiItemUid);
        return deed;
    }

    // =====================================================================
    // Movement — Source: CCMultiMovable.cpp
    // =====================================================================

    /// <summary>
    /// Set movement direction and type.
    /// Source: CCMultiMovable.cpp:60-105
    /// </summary>
    public bool SetMoveDir(Ship ship, Direction dir, ShipMovementType moveType)
    {
        if (ship.Anchored)
            return false;

        // Ship MOVEMENT keeps all 8 directions (a ship sails diagonally for the
        // SHIPFORELEFT/BACKRIGHT/etc. commands); only the FACING is 4-direction.
        // Normalizing the move direction here collapsed every diagonal command to
        // a cardinal one.
        ship.DirMove = dir;
        ship.MovementType = moveType;

        if (moveType == ShipMovementType.Stop)
        {
            ship.NextMoveTick = 0;
            return true;
        }

        ship.NextMoveTick = Environment.TickCount64 + ship.SpeedPeriod;
        return true;
    }

    /// <summary>
    /// Move ship in direction by given distance.
    /// Source: CCMultiMovable.cpp:654-871
    /// </summary>
    public bool Move(Ship ship, Direction dir, int distance = 1)
    {
        if (ship.Anchored) return false;

        // Keep the full 8-direction move (GetDirDelta yields diagonal deltas and
        // CanMoveShipTo/MoveDelta handle any dx/dy) so diagonal sailing works.
        GetDirDelta(dir, out short dx, out short dy);

        for (int i = 0; i < distance; i++)
        {
            short newX = (short)(ship.MultiItem.X + dx);
            short newY = (short)(ship.MultiItem.Y + dy);

            // Leading-edge water check in the move direction (Source-X)
            if (!CanMoveShipTo(ship, newX, newY, dx, dy))
                return false;

            MoveDelta(ship, dx, dy, 0);
        }
        return true;
    }

    /// <summary>
    /// Rotate ship to new facing direction. Changes multi ID by direction offset.
    /// Source: CCMultiMovable.cpp:498-652
    /// </summary>
    public bool Face(Ship ship, Direction newFacing)
    {
        newFacing = Normalize4Dir(newFacing);
        var oldFacing = ship.DirFace;
        if (newFacing == oldFacing) return true;

        // Calculate rotation
        int oldIdx = DirTo4Index(oldFacing);
        int newIdx = DirTo4Index(newFacing);
        int rotSteps = ((newIdx - oldIdx) + 4) % 4; // 1=right90, 2=180, 3=left90

        ushort baseId = (ushort)(ship.MultiItem.BaseId & ~3);
        ushort newBaseId = (ushort)(baseId | newIdx);

        // Preflight the rotated footprint (Source-X CCMultiMovable turn fit-check):
        // if the new facing's bounding rect would not fit (any non-water tile),
        // abort the turn BEFORE mutating anything — otherwise the ship is left
        // half-rotated with no rollback. Magic ships turn anywhere.
        if (!ship.MultiItem.IsAttr(ObjAttributes.Magic))
        {
            var newDef = _multiDefs.Get(newBaseId);
            if (newDef != null && !CanPlaceShip(ship.MultiItem.Position, newDef))
                return false;
        }

        // Update multi item ID: baseId & ~3 | newIdx
        ship.MultiItem.BaseId = newBaseId;

        // Rotate component positions around the multi center
        short cx = ship.MultiItem.X;
        short cy = ship.MultiItem.Y;

        foreach (var compUid in ship.Components)
        {
            var item = _world.FindItem(compUid);
            if (item == null) continue;

            short rx = (short)(item.X - cx);
            short ry = (short)(item.Y - cy);
            RotatePoint(ref rx, ref ry, rotSteps);

            var p = item.Position;
            _world.PlaceItem(item, new Point3D((short)(cx + rx), (short)(cy + ry), p.Z, p.Map));
        }

        // Rotate characters on deck
        foreach (var ch in ListDeckCharacters(ship))
        {
            short rx = (short)(ch.X - cx);
            short ry = (short)(ch.Y - cy);
            RotatePoint(ref rx, ref ry, rotSteps);

            var p = ch.Position;
            _world.MoveCharacter(ch, new Point3D((short)(cx + rx), (short)(cy + ry), p.Z, p.Map), fireRegionEvents: false);
            ch.Direction = newFacing;
        }

        // Rotate loose items on deck
        foreach (var item in ListDeckItems(ship))
        {
            if (ship.Components.Contains(item.Uid)) continue;

            short rx = (short)(item.X - cx);
            short ry = (short)(item.Y - cy);
            RotatePoint(ref rx, ref ry, rotSteps);

            var p = item.Position;
            _world.PlaceItem(item, new Point3D((short)(cx + rx), (short)(cy + ry), p.Z, p.Map));
        }

        // The rotated hull has a new footprint shape — re-rect the ship region.
        UpdateShipRegion(ship);

        ship.DirFace = newFacing;
        OnShipTurned?.Invoke(ship);
        return true;
    }

    /// <summary>Stop ship movement.</summary>
    public void Stop(Ship ship)
    {
        bool wasMoving = ship.MovementType != ShipMovementType.Stop;
        ship.MovementType = ShipMovementType.Stop;
        ship.NextMoveTick = 0;
        if (wasMoving)
            OnShipStopped?.Invoke(ship);
    }

    /// <summary>
    /// Ship tick — called from game loop. Moves ship if speed period elapsed.
    /// Source: CCMultiMovable.cpp:881-905
    /// </summary>
    public void OnShipTick(Ship ship)
    {
        if (ship.MovementType == ShipMovementType.Stop) return;
        if (ship.Anchored) return;

        long now = Environment.TickCount64;
        if (now < ship.NextMoveTick) return;

        if (!Move(ship, ship.DirMove, ship.SpeedTiles))
        {
            Stop(ship);
            return;
        }

        if (ship.MovementType == ShipMovementType.OneTile)
        {
            Stop(ship);
            return;
        }

        ship.NextMoveTick = now + ship.SpeedPeriod;
    }

    /// <summary>Tick all active ships.</summary>
    public void OnTickAll()
    {
        foreach (var ship in _ships.Values)
            OnShipTick(ship);
    }

    // =====================================================================
    // Ship Commands (called from Item.TryExecuteCommand)
    // =====================================================================

    /// <summary>
    /// Execute a ship command. Uses Source-X DirMoveChange offsets:
    /// GetDirTurn(DirFace, offset) where dir is 8-directional (0-7).
    /// FORE=0, FORERIGHT=+1, DRIFTRIGHT=+2, BACKRIGHT=+3,
    /// BACK=+4, BACKLEFT=-3, DRIFTLEFT=-2, FORELEFT=-1
    /// Source: CCMultiMovable.cpp:976-1277
    /// </summary>
    public bool ExecuteCommand(Ship ship, string command, string args)
    {
        var dirFace = ship.DirFace;

        switch (command)
        {
            // --- Movement commands (DirMoveChange via dodirmovechange) ---
            case "SHIPFORE":
                return DirMoveChange(ship, dirFace, 0);
            case "SHIPFORELEFT":
                return DirMoveChange(ship, dirFace, -1);
            case "SHIPFORERIGHT":
                return DirMoveChange(ship, dirFace, 1);
            case "SHIPDRIFTLEFT":
                return DirMoveChange(ship, dirFace, -2);
            case "SHIPDRIFTRIGHT":
                return DirMoveChange(ship, dirFace, 2);
            case "SHIPBACK":
                return DirMoveChange(ship, dirFace, 4);
            case "SHIPBACKLEFT":
                return DirMoveChange(ship, dirFace, -3);
            case "SHIPBACKRIGHT":
                return DirMoveChange(ship, dirFace, 3);

            // --- Aliases for drift (SHIPLEFT/SHIPRIGHT = SHIPDRIFTLEFT/RIGHT) ---
            case "SHIPLEFT":
                return DirMoveChange(ship, dirFace, -2);
            case "SHIPRIGHT":
                return DirMoveChange(ship, dirFace, 2);

            // --- Turn commands (anchor check, Face call) ---
            case "SHIPTURNLEFT":
                if (ship.Anchored) return true;
                return Face(ship, GetDirTurn(dirFace, -2));
            case "SHIPTURNRIGHT":
                if (ship.Anchored) return true;
                return Face(ship, GetDirTurn(dirFace, 2));
            case "SHIPTURNAROUND":
            case "SHIPTURN":
                if (ship.Anchored) return true;
                return Face(ship, GetDirTurn(dirFace, 4));

            // --- Anchor ---
            case "SHIPANCHORDROP":
                if (ship.Anchored)
                {
                    TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsAllDown);
                    return true;
                }
                ship.Anchored = true;
                Stop(ship);
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsDown);
                return true;
            case "SHIPANCHORRAISE":
                if (!ship.Anchored)
                {
                    TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsAllUp);
                    return true;
                }
                ship.Anchored = false;
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerReply1);
                return true;

            // --- Stop ---
            case "SHIPSTOP":
                Stop(ship);
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerStopped);
                return true;

            // --- Direct move/face ---
            case "SHIPMOVE":
            {
                if (!TryParseDir(args, out var moveDir)) return false;
                ship.DirMove = moveDir;
                Move(ship, moveDir, ship.SpeedTiles);
                return true;
            }
            case "SHIPFACE":
            {
                if (!TryParseDir(args, out var faceDir)) return false;
                return Face(ship, faceDir);
            }

            // --- Gate (teleport) ---
            case "SHIPGATE":
                return HandleShipGate(ship, args);

            // --- Vertical movement (requires ATTR_MAGIC) ---
            case "SHIPUP":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return MoveDelta(ship, 0, 0, 16); // PLAYER_HEIGHT = 16
            case "SHIPDOWN":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return MoveDelta(ship, 0, 0, -16);

            // --- Land (return to ground level, requires ATTR_MAGIC) ---
            case "SHIPLAND":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return HandleShipLand(ship);

            // --- Pilot (Source-X SetPilot): the assigned pilot steers via the
            // 0xBF 0x33 wheel-move packet; empty arg releases the wheel. ---
            case "SETPILOT":
            {
                string pilotArg = args.Trim();
                if (pilotArg.Length == 0)
                {
                    ship.Pilot = Serial.Invalid;
                    return true;
                }
                if (pilotArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) pilotArg = pilotArg[2..];
                else if (pilotArg.StartsWith('0') && pilotArg.Length > 1) pilotArg = pilotArg[1..];
                if (uint.TryParse(pilotArg, System.Globalization.NumberStyles.HexNumber, null, out uint pilotUid))
                    ship.Pilot = new Serial(pilotUid);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Source-X dodirmovechange pattern: anchor check + SetMoveDir with rotated direction.
    /// </summary>
    private bool DirMoveChange(Ship ship, Direction dirFace, int offset)
    {
        if (ship.Anchored)
        {
            // Source-X CItemShip::OnSpeak: tillerman complains when player
            // requests motion with the anchor down.
            TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsDown);
            return true;
        }
        return SetMoveDir(ship, GetDirTurn(dirFace, offset), ShipMovementType.Normal);
    }

    /// <summary>
    /// SHIPLAND: return to ground level. Source: CCMultiMovable.cpp:1219-1242
    /// </summary>
    private bool HandleShipLand(Ship ship)
    {
        if (_mapData == null) return false;
        var mi = ship.MultiItem;
        sbyte groundZ = _mapData.GetEffectiveZ(mi.MapIndex, mi.X, mi.Y);
        sbyte dz = (sbyte)(groundZ - mi.Z);
        if (dz == 0) return false;
        return MoveDelta(ship, 0, 0, dz);
    }

    // =====================================================================
    // Helpers — Source: CCMultiMovable.cpp
    // =====================================================================

    /// <summary>
    /// List all objects on ship: multi + components + deck chars/items.
    /// Source: CCMultiMovable.cpp:129-210
    /// </summary>
    public List<ObjBase> ListShipObjects(Ship ship)
    {
        var result = new List<ObjBase> { ship.MultiItem };

        foreach (var uid in ship.Components)
        {
            var item = _world.FindItem(uid);
            if (item != null) result.Add(item);
        }

        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def != null)
        {
            int range = Math.Max(Math.Abs(def.MaxX - def.MinX), Math.Abs(def.MaxY - def.MinY)) / 2 + 1;
            foreach (var obj in _world.GetObjectsInRange(ship.MultiItem.Position, range))
            {
                if (obj == ship.MultiItem) continue;
                if (obj is Item it && ship.Components.Contains(it.Uid)) continue;
                if (IsOnDeck(ship, def, obj))
                    result.Add(obj);
            }
        }

        return result;
    }

    private List<Character> ListDeckCharacters(Ship ship)
    {
        var result = new List<Character>();
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return result;

        int range = Math.Max(Math.Abs(def.MaxX - def.MinX), Math.Abs(def.MaxY - def.MinY)) / 2 + 1;
        foreach (var ch in _world.GetCharsInRange(ship.MultiItem.Position, range))
        {
            if (IsOnDeck(ship, def, ch))
                result.Add(ch);
        }
        return result;
    }

    private List<Item> ListDeckItems(Ship ship)
    {
        var result = new List<Item>();
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return result;

        int range = Math.Max(Math.Abs(def.MaxX - def.MinX), Math.Abs(def.MaxY - def.MinY)) / 2 + 1;
        foreach (var item in _world.GetItemsInRange(ship.MultiItem.Position, range))
        {
            if (item == ship.MultiItem) continue;
            if (item.ContainedIn.IsValid) continue;
            if (IsOnDeck(ship, def, item))
                result.Add(item);
        }
        return result;
    }

    private bool IsOnDeck(Ship ship, MultiDef def, ObjBase obj)
    {
        if (obj.MapIndex != ship.MultiItem.MapIndex) return false;
        short dx = (short)(obj.X - ship.MultiItem.X);
        short dy = (short)(obj.Y - ship.MultiItem.Y);
        if (dx < def.MinX || dx > def.MaxX || dy < def.MinY || dy > def.MaxY)
            return false;

        // Source-X CCMultiMovable::ListObjs also gates on Z: an object must sit
        // near the deck plane to ride the ship. Without this, anything in the XY
        // footprint at any height (a swimmer below the hull, a bird overhead, an
        // item on a bridge above) was dragged along. Use a generous window around
        // the ship anchor Z so genuine deck objects are never left behind.
        int zdiff = obj.Z - ship.MultiItem.Z;
        return zdiff >= -2 && zdiff <= 20;
    }

    /// <summary>
    /// Check if terrain at position is water.
    /// Source: CCMultiMovable.cpp:481-496
    /// </summary>
    public bool IsWaterAt(int mapId, short x, short y)
    {
        if (_mapData == null) return true;
        var terrain = _mapData.GetTerrainTile(mapId, x, y);
        var landData = _mapData.GetLandTileData(terrain.TileId);
        return landData.IsWet;
    }

    /// <summary>
    /// Source-X CanMoveTo (GetHeightPoint2 with CAN_I_WATER): the cell must be
    /// pure sailable water — water terrain AND no blocking STATIC (dock, rock,
    /// bridge piling) AND no other ship's hull occupying it. The old land-only
    /// check let ships sail straight through docks and other vessels.
    /// </summary>
    public bool CanSailInto(Ship ship, int mapId, short x, short y, sbyte z)
    {
        if (!IsWaterAt(mapId, x, y))
            return false;

        // Blocking statics near the water plane (a bridge far above is fine).
        if (_mapData != null)
        {
            foreach (var st in _mapData.GetStatics(mapId, x, y))
            {
                var td = _mapData.GetItemTileData(st.TileId);
                bool blocks = (td.Flags & SphereNet.MapData.Tiles.TileFlag.Impassable) != 0;
                bool wetStatic = (td.Flags & SphereNet.MapData.Tiles.TileFlag.Wet) != 0;
                if (blocks && !wetStatic && Math.Abs(st.Z - z) <= 10)
                    return false;
            }
        }

        // Another ship's hull in the cell blocks (Source-X: multis participate
        // in GetHeightPoint2's block flags).
        foreach (var other in AllShips)
        {
            if (other == ship) continue;
            var odef = _multiDefs.Get(other.MultiItem.BaseId);
            if (odef == null) continue;
            if (other.MultiItem.MapIndex != mapId) continue;
            int rdx = x - other.MultiItem.X;
            int rdy = y - other.MultiItem.Y;
            if (rdx >= odef.MinX && rdx <= odef.MaxX && rdy >= odef.MinY && rdy <= odef.MaxY)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Move all ship objects by delta.
    /// Source: CCMultiMovable.cpp:274-421
    /// </summary>
    public bool MoveDelta(Ship ship, short dx, short dy, sbyte dz)
    {
        // Region boundary check BEFORE anything moves (Source-X order): the
        // multi's anchor decides the region; @RegionLeave/@RegionEnter fire on
        // the multi item and RETURN 1 cancels the whole step.
        var regionHook = OnShipRegionChange;
        if (regionHook != null)
        {
            var anchor = ship.MultiItem.Position;
            var newAnchor = new Point3D(
                (short)(anchor.X + dx), (short)(anchor.Y + dy),
                (sbyte)(anchor.Z + dz), anchor.Map);
            // Resolve the BACKGROUND region the hull sits in (excluding the ship's
            // own region, which now wins FindRegion by smallest area) so a sail
            // across a sea/harbour boundary still fires @RegionLeave/@RegionEnter.
            var shipRegion = ship.RegionUid != 0 ? _world.FindRegionByUid(ship.RegionUid) : null;
            var oldRegion = shipRegion != null ? _world.FindParentRegion(shipRegion, anchor) : _world.FindRegion(anchor);
            var newRegion = shipRegion != null ? _world.FindParentRegion(shipRegion, newAnchor) : _world.FindRegion(newAnchor);
            if (!ReferenceEquals(oldRegion, newRegion) && !regionHook(ship, oldRegion, newRegion))
                return false;
        }

        // Collect deck objects before moving (positions change during move)
        var deckChars = ListDeckCharacters(ship);
        var deckItems = ListDeckItems(ship);

        // Move multi item
        var mi = ship.MultiItem;
        var miPos = mi.Position;
        _world.PlaceItem(mi, new Point3D(
            (short)(miPos.X + dx), (short)(miPos.Y + dy),
            (sbyte)(miPos.Z + dz), miPos.Map));

        // Move components
        foreach (var uid in ship.Components)
        {
            var item = _world.FindItem(uid);
            if (item == null) continue;
            var p = item.Position;
            _world.PlaceItem(item, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), p.Map));
        }

        // Move deck characters. The ship's region moves WITH them, so suppress
        // region enter/exit — they are not crossing a boundary, the boundary
        // travels with the hull.
        foreach (var ch in deckChars)
        {
            var p = ch.Position;
            _world.MoveCharacter(ch, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), p.Map), fireRegionEvents: false);
        }

        // Move loose items on deck (not in containers, not components)
        foreach (var item in deckItems)
        {
            if (ship.Components.Contains(item.Uid)) continue;
            var p = item.Position;
            _world.PlaceItem(item, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), p.Map));
        }

        // Re-position the ship's region to follow the hull to its new footprint.
        UpdateShipRegion(ship);

        OnShipMoved?.Invoke(ship);
        return true;
    }

    private bool CanMoveShipTo(Ship ship, short newX, short newY, short dx, short dy)
    {
        // ATTR_MAGIC ships can fly over land (Source-X CanMoveTo)
        if (ship.MultiItem.IsAttr(ObjAttributes.Magic))
            return true;

        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return false;

        byte map = ship.MultiItem.MapIndex;
        int sx = Math.Sign(dx);
        int sy = Math.Sign(dy);

        // Source-X CCMultiMovable::Move tests only the LEADING EDGE in the move
        // direction (both perpendicular edges for a diagonal) — the trailing cells
        // the ship already occupies are water by definition. Each leading tile must
        // be water (CanMoveTo); any non-water tile stops the ship.
        sbyte shipZ = ship.MultiItem.Position.Z;
        if (sy != 0) // leading N/S row
        {
            short ly = (short)(newY + (sy > 0 ? def.MaxY : def.MinY));
            for (short lx = (short)(newX + def.MinX); lx <= newX + def.MaxX; lx++)
                if (!CanSailInto(ship, map, lx, ly, shipZ))
                    return false;
        }
        if (sx != 0) // leading E/W column
        {
            short lx = (short)(newX + (sx > 0 ? def.MaxX : def.MinX));
            for (short ly = (short)(newY + def.MinY); ly <= newY + def.MaxY; ly++)
                if (!CanSailInto(ship, map, lx, ly, shipZ))
                    return false;
        }
        return true;
    }

    // =====================================================================
    // Dynamic ship region (Source-X CItemMulti::MultiRealizeRegion + CRegionWorld
    // repositioning as the multi moves). A ship gets a world region matching its
    // hull footprint, flagged Ship and inheriting the surrounding region's flags
    // (a guarded harbour / no-pvp zone carries through, with Ship added). Because
    // the ship sails between regions, inheritance is RECOMPUTED every move — reset
    // to base flags, then re-inherit from the current parent — never accumulated.
    // =====================================================================

    /// <summary>Realize the ship's region once (placement / load). Idempotent.</summary>
    private void CreateShipRegion(Ship ship)
    {
        if (ship.RegionUid != 0) return;
        var mi = ship.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        var region = new Region
        {
            Name = string.IsNullOrEmpty(mi.Name) ? "ship" : mi.Name,
            MapIndex = mi.MapIndex,
            Flags = RegionFlag.Ship | RegionFlag.InheritParentFlags,
        };
        region.AddRect(
            (short)(mi.X + def.MinX), (short)(mi.Y + def.MinY),
            (short)(mi.X + def.MaxX), (short)(mi.Y + def.MaxY));
        _world.AddRegion(region);
        ship.RegionUid = region.Uid;
        UpdateShipRegion(ship); // set P + inherit from current surroundings
    }

    /// <summary>Re-position the ship region to the current hull footprint and
    /// recompute its inherited flags. Called after every move / turn.</summary>
    private void UpdateShipRegion(Ship ship)
    {
        if (ship.RegionUid == 0) return;
        var region = _world.FindRegionByUid(ship.RegionUid);
        if (region == null) return;
        var mi = ship.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        region.MapIndex = mi.MapIndex;
        region.SetFootprint(
            (short)(mi.X + def.MinX), (short)(mi.Y + def.MinY),
            (short)(mi.X + def.MaxX), (short)(mi.Y + def.MaxY));
        var center = new Point3D(mi.X, mi.Y, mi.Z, mi.MapIndex);
        region.P = center;

        // Reset to base flags, then re-inherit from the region the hull now sits
        // in. Flags-only (like houses) so the deck never double-fires @Enter/@Exit.
        region.Flags = RegionFlag.Ship | RegionFlag.InheritParentFlags;
        var parent = _world.FindParentRegion(region, center);
        if (parent != null)
            region.InheritFromParent(parent);

        _world.InvalidateRegionCache();
    }

    /// <summary>Tear down the ship's region (dry-dock / decommission).</summary>
    private void RemoveShipRegion(Ship ship)
    {
        if (ship.RegionUid == 0) return;
        _world.RemoveRegion(ship.RegionUid);
        ship.RegionUid = 0;
    }

    /// <summary>The ship whose dynamic region contains <paramref name="pt"/>, or null.
    /// Deck membership is resolved through the Ship-flag region (region-bound, so it
    /// matches exactly the footprint the engine maintains) rather than a separate
    /// bounding-box test. Used by the boarding gate and eject.</summary>
    public Ship? FindShipAt(Point3D pt)
    {
        var region = _world.FindRegion(pt);
        if (region == null || !region.IsFlag(RegionFlag.Ship)) return null;
        foreach (var ship in _ships.Values)
            if (ship.RegionUid == region.Uid)
                return ship;
        return null;
    }

    /// <summary>Ban a player from the ship and, if they are currently aboard, eject
    /// them. The boarding gate (CanBoard) then keeps them off. Owner-proof.</summary>
    public bool BanFromShip(Ship ship, Serial uid)
    {
        if (uid == ship.Owner) return false; // never ban the owner
        bool added = ship.AddBan(uid);
        // Eject any matching character already standing on the deck.
        foreach (var ch in ListDeckCharacters(ship))
            if (ch.Uid == uid)
                EjectFromShip(ship, ch);
        return added;
    }

    /// <summary>Lift a ship ban.</summary>
    public bool UnbanFromShip(Ship ship, Serial uid) => ship.RemoveBan(uid);

    /// <summary>Put a character off the ship onto the tile just beyond the starboard
    /// edge of the hull (Source-X eject drops the target at the multi's edge). The
    /// move fires region enter/exit normally — they really are leaving the ship.</summary>
    public void EjectFromShip(Ship ship, Character ch)
    {
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        var mi = ship.MultiItem;
        short offX = def != null ? (short)(mi.X + def.MaxX + 1) : (short)(mi.X + 1);
        _world.MoveCharacter(ch, new Point3D(offX, ch.Y, ch.Z, mi.MapIndex));
    }

    private bool HandleShipGate(Ship ship, string args)
    {
        // SHIPGATE x,y,z,map — teleport entire ship
        if (!Point3D.TryParse(args.Trim(), out var dest))
            return false;

        short dx = (short)(dest.X - ship.MultiItem.X);
        short dy = (short)(dest.Y - ship.MultiItem.Y);
        sbyte dz = (sbyte)(dest.Z - ship.MultiItem.Z);

        return MoveDelta(ship, dx, dy, dz);
    }

    // =====================================================================
    // Save/Load (TAG-based, House pattern)
    // =====================================================================

    /// <summary>Serialize all ship metadata to item TAGs for persistence.</summary>
    public void SerializeAllToTags()
    {
        foreach (var (_, ship) in _ships)
        {
            var item = ship.MultiItem;
            item.SetTag("SHIP.OWNER", $"0{ship.Owner.Value:X}");
            var ownerObj = _world.FindObject(ship.Owner);
            if (ownerObj != null)
                item.SetTag("SHIP.OWNER_UUID", ownerObj.Uuid.ToString("D"));
            item.SetTag("SHIP.ANCHORED", ship.Anchored ? "1" : "0");
            item.SetTag("SHIP.DIRFACE", ((byte)ship.DirFace).ToString());
            item.SetTag("SHIP.SPEEDPERIOD", ship.SpeedPeriod.ToString());
            item.SetTag("SHIP.SPEEDTILES", ship.SpeedTiles.ToString());
            item.SetTag("SHIP.SPEEDMODE", ((byte)ship.SpeedMode).ToString());

            if (ship.Pilot.IsValid)
                item.SetTag("SHIP.PILOT", $"0{ship.Pilot.Value:X}");

            if (ship.Components.Count > 0)
                item.SetTag("SHIP.COMPONENTS", string.Join(",", ship.Components.Select(s => $"0{s.Value:X}")));

            if (ship.Bans.Count > 0)
                item.SetTag("SHIP.BANS", string.Join(",", ship.Bans.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("SHIP.BANS");
        }
    }

    /// <summary>
    /// Rebuild ship instances from IT_SHIP items after world load.
    /// </summary>
    public void DeserializeFromWorld()
    {
        _ships.Clear();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not Item item) continue;
            if (item.ItemType != ItemType.Ship) continue;
            if (!item.TryGetTag("SHIP.OWNER", out string? ownerStr)) continue;

            uint ownerVal = ParseHexSerial(ownerStr);
            if (ownerVal == 0) continue;

            var ship = new Ship(item) { Owner = new Serial(ownerVal) };

            if (item.TryGetTag("SHIP.ANCHORED", out string? ancStr))
                ship.Anchored = ancStr == "1";
            if (item.TryGetTag("SHIP.DIRFACE", out string? dirStr) && byte.TryParse(dirStr, out byte df))
                ship.DirFace = (Direction)df;
            if (item.TryGetTag("SHIP.SPEEDPERIOD", out string? spStr) && ushort.TryParse(spStr, out ushort sp))
                ship.SpeedPeriod = sp;
            if (item.TryGetTag("SHIP.SPEEDTILES", out string? stStr) && byte.TryParse(stStr, out byte st))
                ship.SpeedTiles = st;
            if (item.TryGetTag("SHIP.SPEEDMODE", out string? smStr) && byte.TryParse(smStr, out byte sm))
                ship.SpeedMode = (ShipSpeedMode)sm;
            if (item.TryGetTag("SHIP.PILOT", out string? pilotStr))
                ship.Pilot = new Serial(ParseHexSerial(pilotStr));

            // Rebuild component list and categorize
            if (item.TryGetTag("SHIP.COMPONENTS", out string? compStr) && !string.IsNullOrWhiteSpace(compStr))
            {
                foreach (var part in compStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    uint val = ParseHexSerial(part);
                    if (val == 0) continue;
                    var compItem = _world.FindItem(new Serial(val));
                    if (compItem != null)
                        ship.AddComponent(compItem);
                    else
                        ship.AddComponentUid(new Serial(val));
                }
            }

            if (item.TryGetTag("SHIP.BANS", out string? bansStr) && !string.IsNullOrWhiteSpace(bansStr))
            {
                foreach (var part in bansStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    uint val = ParseHexSerial(part);
                    if (val != 0) ship.AddBan(new Serial(val));
                }
            }

            _ships[item.Uid] = ship;
            CreateShipRegion(ship);
        }
    }

    // =====================================================================
    // Direction Helpers
    // =====================================================================

    /// <summary>Normalize to 4-direction (N/E/S/W).</summary>
    private static Direction Normalize4Dir(Direction dir)
    {
        return ((byte)dir & 0x07) switch
        {
            0 or 1 => Direction.North,
            2 or 3 => Direction.East,
            4 or 5 => Direction.South,
            _ => Direction.West,
        };
    }

    /// <summary>Convert 4-direction to 0-3 index (N=0, E=1, S=2, W=3).</summary>
    private static int DirTo4Index(Direction dir)
    {
        return dir switch
        {
            Direction.North => 0,
            Direction.East => 1,
            Direction.South => 2,
            Direction.West => 3,
            _ => 0,
        };
    }

    /// <summary>4-direction from index.</summary>
    private static Direction IndexTo4Dir(int idx)
    {
        return (idx & 3) switch
        {
            0 => Direction.North,
            1 => Direction.East,
            2 => Direction.South,
            3 => Direction.West,
            _ => Direction.North,
        };
    }

    /// <summary>Rotate a 4-direction by steps (1=clockwise 90°).</summary>
    private static Direction RotateDir4(Direction dir, int steps)
    {
        int idx = DirTo4Index(dir);
        return IndexTo4Dir((idx + steps) % 4);
    }

    /// <summary>
    /// 8-directional turn. Maps to GetDirTurn in Source-X.
    /// offset: +2 = right 90°, -2 = left 90°, +4 = 180°, ±1 = 45° diagonal
    /// </summary>
    private static Direction GetDirTurn(Direction dir, int offset)
    {
        return (Direction)(((byte)dir + offset + 8) % 8);
    }

    private static Direction OppositeDir(Direction dir)
    {
        return GetDirTurn(dir, 4);
    }

    /// <summary>Try parse direction from string (name or numeric).</summary>
    private static bool TryParseDir(string args, out Direction dir)
    {
        var s = args.Trim();
        if (Enum.TryParse(s, true, out dir))
            return true;
        if (byte.TryParse(s, out byte b) && b < 8)
        {
            dir = (Direction)b;
            return true;
        }
        dir = Direction.North;
        return false;
    }

    private static void GetDirDelta(Direction dir, out short dx, out short dy)
    {
        dx = dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }

    /// <summary>
    /// Rotate point around origin by rotation steps.
    /// Source: CCMultiMovable.cpp — right90 (x,y)→(-y,x), left90 (x,y)→(y,-x), 180° (x,y)→(-x,-y)
    /// </summary>
    private static void RotatePoint(ref short x, ref short y, int rotSteps)
    {
        for (int i = 0; i < rotSteps; i++)
        {
            short ox = x;
            x = (short)(-y);
            y = ox;
        }
    }

    /// <summary>
    /// Classify a ship component tile ID to an ItemType.
    /// Common ship component IDs in UO.
    /// </summary>
    private static ItemType ClassifyShipComponent(ushort tileId)
    {
        // Source-X: a ship component's category comes from its ITEMDEF TYPE
        // (t_ship_tiller / t_ship_plank / t_ship_side / t_ship_hold, …). Honor
        // that when the tile's itemdef carries a ship-component type; otherwise
        // it's deck/other. Without this every component was ShipOther, so the
        // tiller (steering), hold (cargo) and planks (boarding) never worked on a
        // freshly-placed ship.
        var def = Definitions.DefinitionLoader.GetItemDef(tileId);
        if (def != null)
        {
            switch (def.Type)
            {
                case ItemType.ShipTiller:
                case ItemType.ShipPlank:
                case ItemType.ShipSide:
                case ItemType.ShipSideLocked:
                case ItemType.ShipHold:
                case ItemType.ShipHoldLock:
                    return def.Type;
            }
        }
        return ItemType.ShipOther;
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
}
