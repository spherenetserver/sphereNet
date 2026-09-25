// Steps and routing: MoveToward, A* cache, tile checks, wander, doors.
// Decomposed from the former single-file NpcAI.cs (see NpcAI.cs core).
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

public sealed partial class NpcAI
{
    // Cached paths per NPC UID — avoids recalculating every tick
    private readonly Dictionary<uint, List<Point3D>> _pathCache = [];

    private readonly Dictionary<uint, int> _pathIndex = [];

    private readonly Dictionary<uint, long> _pathTime = [];

    // Destination associated with the cached path/backoff. A path toward an
    // old combat target must never be reused after the target changes or moves
    // materially.
    private readonly Dictionary<uint, Point3D> _pathGoal = [];

    private const long PathCacheMaxAge = 10_000;

    // Earliest time A* may be recomputed for this NPC. Survives path-cache
    // drops (unlike _pathTime) so a churning crowd — where the cached step is
    // invalidated almost every tick — cannot trigger a full A* recompute every
    // tick. A successful compute allows the next one after PathThrottleMs; a
    // FAILED compute (unreachable target — the search burns the whole node
    // budget before giving up) backs off for PathFailBackoffMs instead. The
    // direct-step fast path above A* still runs every action, so the NPC
    // resumes immediately once the straight line opens.
    private readonly Dictionary<uint, long> _nextPathfindMs = [];

    private const long PathThrottleMs = 750;

    private const long PathFailBackoffMs = 5_000;

    // NPC combat-chase A* node budget. Far below the full Pathfinder cap (which
    // is sized for player half-continent .walk): a creature only needs to route
    // around local obstacles toward a target in sight. Each explored node costs
    // ~35µs (per-tile map/static/object walkability checks), so an unreachable
    // target burns the WHOLE budget in the single-threaded apply phase — at
    // 2000 nodes that was a 40-75ms main-loop stall (visible as a ~160ms ping
    // spike). 500 nodes bounds the worst case to ~17ms and still covers any
    // realistic local detour.
    private const int NpcPathMaxNodes = 500;

    // Source-X NPC pathfinding eligibility (CCharNPCAct.cpp NPC_Pathfinding):
    // the pathfinder is a fixed 28x28 local box (MAX_NPC_PATH_STORAGE_SIZE =
    // UO_MAP_VIEW_SIGHT*2), so a target at dist >= 14 is "too far... too slow"
    // (:2432) and an adjacent one (dist < 2, :2434) needs no route — both take
    // direct/side steps instead. The same radius bounds the A* search box, so
    // an unreachable target in an enclosed space exhausts the box, not the
    // whole node budget.
    private const int NpcPathMinDist = 2;

    private const int NpcPathMaxDist = 14;

    private long _lastPathPurge;

    public void PurgeStalePaths()
    {
        long now = Environment.TickCount64;
        if (now - _lastPathPurge < 30_000) return;
        _lastPathPurge = now;

        List<uint>? stale = null;
        foreach (var uid in _pathCache.Keys)
        {
            var obj = _world.FindObject(new Core.Types.Serial(uid));
            bool expired = _pathTime.TryGetValue(uid, out long t) && now - t > PathCacheMaxAge;
            if (obj is not Character ch || ch.IsDeleted || ch.IsDead || expired)
                (stale ??= []).Add(uid);
        }
        if (stale != null)
        {
            foreach (var uid in stale)
            {
                _pathCache.Remove(uid);
                _pathIndex.Remove(uid);
                _pathTime.Remove(uid);
                _pathGoal.Remove(uid);
                _losFailCounts.Remove(uid);
                _nextPathfindMs.Remove(uid);
                _lastAttackNotify.Remove(uid);
                _nextItemScan.Remove(uid);
            }
        }

        // Throttle timestamps can outlive the path cache (they survive path
        // drops by design), so sweep them for gone NPCs here as well.
        if (_nextPathfindMs.Count > 0)
        {
            List<uint>? stalePf = null;
            foreach (var uid in _nextPathfindMs.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue;
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (stalePf ??= []).Add(uid);
            }
            if (stalePf != null)
                foreach (var uid in stalePf)
                {
                    _nextPathfindMs.Remove(uid);
                    _pathGoal.Remove(uid);
                }
        }

        // LOS-fail counters can accumulate for NPCs that never built a path
        // cache entry (target keeps moving but the straight line stays open),
        // so the path-cache sweep above never reaches them. Purge them here too.
        if (_losFailCounts.Count > 0)
        {
            List<uint>? staleLos = null;
            foreach (var uid in _losFailCounts.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleLos ??= []).Add(uid);
            }
            if (staleLos != null)
                foreach (var uid in staleLos)
                    _losFailCounts.Remove(uid);
        }

        // Item-scan timers accumulate for every acting NPC regardless of the
        // path cache — sweep them for gone NPCs like the other per-uid state.
        if (_nextItemScan.Count > 0)
        {
            List<uint>? staleScan = null;
            foreach (var uid in _nextItemScan.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleScan ??= []).Add(uid);
            }
            if (staleScan != null)
                foreach (var uid in staleScan)
                    _nextItemScan.Remove(uid);
        }

        // Attack-notify latches outlive the path cache the same way (an NPC
        // can fight without ever pathfinding), so sweep them for gone NPCs.
        if (_lastAttackNotify.Count > 0)
        {
            List<uint>? staleNotify = null;
            foreach (var uid in _lastAttackNotify.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleNotify ??= []).Add(uid);
            }
            if (staleNotify != null)
                foreach (var uid in staleNotify)
                    _lastAttackNotify.Remove(uid);
        }

        // Idle activity, walk-home failures and talk state: runtime-only per-uid
        // entries that outlive the NPC they belong to unless swept here.
        SweepGone(_idleMode);
        SweepGone(_homeStepFailures);
        SweepGone(_talkState);
    }

    private void SweepGone<T>(Dictionary<uint, T> map)
    {
        if (map.Count == 0) return;
        List<uint>? gone = null;
        foreach (var uid in map.Keys)
        {
            var obj = _world.FindObject(new Core.Types.Serial(uid));
            if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                (gone ??= []).Add(uid);
        }
        if (gone != null)
            foreach (var uid in gone)
                map.Remove(uid);
    }

    /// <summary>Idle fidget animation hook — wired by the server to the
    /// body-aware animation broadcast. Only used with NPCAIEXTRAS IdleFlavor.</summary>
    public Action<Character>? OnNpcFidget { get; set; }

    // --- Cadence ---

    /// <summary>The re-tick a tick leaves when it took no step.
    ///
    /// Source-X NPC_OnTickAction (CCharNPCAct.cpp:2385-2394): once the action timer
    /// has expired and the NPC is not swinging, the next brain tick is
    /// 1 + rand((150-DEX)/4 .. (150-DEX)/2) tenths of a second. A fighting creature
    /// is re-ticked by its swing timer there; this engine has no such timer on the
    /// NPC, so a fighter keeps the run-step pace it had, which is the pace the
    /// reference's own chase steps run at.</summary>
    internal int ComputeRetickDelayMs(Character npc)
    {
        bool fighting = npc.FightTarget.IsValid ||
            (npc.NpcMaster.IsValid && npc.PetAIMode == PetAIMode.Attack);
        if (fighting)
        {
            int dex = npc.Dex;
            if (npc.NpcMaster.IsValid && dex < 75) dex = 75;
            int moveRate = ResolveMoveRate(npc);
            int range = Math.Max(0, 100 - dex * moveRate / 100) / 5;
            return Math.Clamp(250 + _rand.Next(range + 1) * 100, 100, 5000);
        }

        int timeout = Math.Max(0, (150 - npc.Dex) / 2);
        if (timeout > 1)
            timeout = _rand.Next(timeout / 2, timeout + 1);   // GetVal2Fast is inclusive
        return (1 + timeout) * 100;
    }

    /// <summary>CHARDEF MOVERATE (Source-X CCharBase::m_iMoveRate, from the ini
    /// MOVERATE when the definition sets none).</summary>
    private static int ResolveMoveRate(Character npc)
    {
        var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
        return charDef != null && charDef.MoveRate > 0
            ? charDef.MoveRate
            : SphereNet.Scripting.Definitions.CharDef.DefaultMoveRate;
    }

    /// <summary>Read a numeric TAG the way Source-X GetKey/GetValNum reads a key:
    /// present or not, and its value as hex (0x.. / leading 0) or decimal.</summary>
    private static bool TryGetTagNum(Character npc, string key, out long value)
    {
        value = 0;
        if (!npc.TryGetTag(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        raw = raw.Trim();
        bool neg = raw.StartsWith('-');
        if (neg) raw = raw[1..];
        bool ok;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ok = long.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        else if (raw.Length > 1 && raw[0] == '0')
            ok = long.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out value);
        else
            ok = long.TryParse(raw, out value);
        if (neg) value = -value;
        return ok;
    }

    /// <summary>Source-X g_Rand.GetValFast(n): 0..n-1, and 0 for n &lt;= 0.</summary>
    private static int RandVal(int n) => n <= 0 ? 0 : _rand.Next(n);

    /// <summary>The delay after one NPC step - Source-X NPC_WalkToPoint "Speed
    /// counting" (CCharNPCAct.cpp:610-693), chosen by whether THIS step runs.
    ///
    /// Old style (TAG.OVERRIDE.MOVESTYLE=1 or OF_NPCMovementOldStyle): 1/4 s plus
    /// rand((100-DEX)/5) tenths running, 1 s plus rand((100-DEX)/3) tenths walking,
    /// scaled by OVERRIDE.MOVERATE (else the CHARDEF MOVERATE) and not clamped.
    /// New style: OVERRIDE.MOVEDELAY is the walking delay itself (halved running,
    /// halved mounted, quartered running mounted); otherwise the DEX formula with the
    /// move rate folded into DEX. The new style is clamped to 100 ms .. 5 s. A pet's
    /// DEX counts as at least 75 when it runs.</summary>
    internal int ComputeStepDelayMs(Character npc, bool run)
    {
        int dex = npc.Dex;
        bool pet = npc.NpcMaster.IsValid || npc.IsStatFlag(StatFlag.Pet);
        bool oldStyle = (TryGetTagNum(npc, "OVERRIDE.MOVESTYLE", out long style) && style == 1) ||
            (((OptionFlags)(uint)_config.OptionFlags) & OptionFlags.NpcMovementOldStyle) != 0;

        long tick;
        if (oldStyle)
        {
            if (run)
            {
                if (pet && dex < 75) dex = 75;
                tick = 250 + RandVal((100 - dex) / 5) * 100L;
            }
            else
            {
                tick = 1000 + RandVal((100 - dex) / 3) * 100L;
            }

            if (TryGetTagNum(npc, "OVERRIDE.MOVERATE", out long rate))
                tick = tick * Math.Max(1, rate) / 100;
            else
                tick = tick * ResolveMoveRate(npc) / 100;
            return (int)Math.Clamp(tick, 1, int.MaxValue);
        }

        if (TryGetTagNum(npc, "OVERRIDE.MOVEDELAY", out long moveDelay))
        {
            tick = moveDelay;
            if (npc.IsStatFlag(StatFlag.OnHorse) || npc.IsStatFlag(StatFlag.Hovering))
                tick /= run ? 4 : 2;
            else if (run)
                tick /= 2;
        }
        else
        {
            long rate = TryGetTagNum(npc, "OVERRIDE.MOVERATE", out long r) ? r : ResolveMoveRate(npc);
            if (run)
            {
                if (pet && dex < 75) dex = 75;
                tick = 250 + RandVal((int)(100 - dex * rate / 100) / 5) * 100L;
            }
            else
            {
                tick = 1000 + RandVal((int)(100 - dex * rate / 100) / 3) * 100L;
            }
        }

        return (int)Math.Clamp(tick, 100, 5000);
    }

    /// <summary>Set the move delay after a step the NPC just took.</summary>
    private void ApplyStepDelay(Character npc, bool run)
    {
        npc.NextNpcActionTime = Environment.TickCount64 + ComputeStepDelayMs(npc, run);
        _stepDelayAppliedFor = npc.Uid.Value;
    }

    /// <summary>Whether a step may run. Source-X demotes a run to a walk when the
    /// creature cannot run or fly, or has one stamina point or less
    /// (NPC_WalkToPoint, CCharNPCAct.cpp:591).</summary>
    private static bool CanRunNow(Character npc) =>
        (CharDefHelper.GetCanFlags(npc) & (CanFlags.C_Run | CanFlags.C_Fly)) != 0 && npc.Stam > 1;

    /// <summary>Open an unlocked closed door for an NPC (state flip +
    /// observer broadcast — wired by the server). Returns true when the
    /// door ended up open so the blocked step can be re-validated.</summary>
    public Func<Character, Item, bool>? OnNpcOpenDoor { get; set; }

    /// <summary>NPC_AI_MOVEOBSTACLES: try to shift a movable, blocking item on
    /// <paramref name="blocked"/> onto the NPC's own tile (Source-X moves it to
    /// the char's position). Gated on the flag, CAN_C_USEHANDS and the Source-X
    /// smartness roll (INT &gt; rand(100)). Returns true when an item moved.</summary>
    internal bool TryClearObstacle(Character npc, Point3D blocked)
    {
        if (!GetNpcFlags(npc).HasFlag(NpcAIFlags.MoveObstacles))
            return false;
        var can = CharDefHelper.GetCanFlags(npc);
        if (!can.HasFlag(CanFlags.C_UseHands))
            return false;
        if (npc.Int <= _rand.Next(100))
            return false;

        foreach (var item in _world.GetItemsInRange(blocked, 1))
        {
            if (item.IsDeleted || item.ContainedIn.IsValid) continue;
            if (item.X != blocked.X || item.Y != blocked.Y) continue;
            var def = DefinitionLoader.GetItemDef(item.BaseId);
            if (def == null || !def.Can.HasFlag(CanFlags.I_Block)) continue; // only actual blockers
            // Source-X measures from the item's TOP (GetTopZ = base + height).
            int topZ = item.Z + (_world.MapData?.GetItemTileData(item.DispIdFull).Height ?? 0);
            if (Math.Abs(topZ - npc.Z) > 3) continue;
            if ((item.Attributes & (ObjAttributes.Move_Never | ObjAttributes.LockedDown |
                ObjAttributes.Secure | ObjAttributes.Static)) != 0) continue;

            // PlaceItem updates sector membership. Assigning Position directly
            // leaves the item in its old sector when this crosses a boundary,
            // corrupting range queries and walkability checks.
            if (!_world.PlaceItem(item, npc.Position))
                continue;
            OnNpcMovedItem?.Invoke(npc, item);
            return true;
        }
        return false;
    }

    // --- Movement helpers ---

    private bool CanNpcEnterTile(Character npc, Point3D pos)
    {
        if (!CheckWalkHere(npc, pos))
            return false;

        var mapData = _world.MapData;
        if (mapData == null) return true;

        if (StandsOnWater(mapData, pos))
        {
            bool canSwim = (CharDefHelper.GetCanFlags(npc) & CanFlags.C_Swim) != 0;
            if (!canSwim) return false;
        }

        return true;
    }

    /// <summary>Source-X NPC_CheckWalkHere (CCharNPCStatus.cpp:544): does the NPC
    /// want to step here at all?
    ///
    /// A guard that is not at war keeps inside guarded ground unless
    /// OF_GuardOutsideGuardedArea is set. On the tile, the first item within five Z
    /// of the step that is one of these decides: a web stops everything but a giant
    /// spider, fire stops everything that is not fire immune, and traps, moongates
    /// and telepads stop everyone. A script-made field that only carries a flat
    /// FIELD_DAMAGE counts as fire, as it burns like one in this engine.</summary>
    internal bool CheckWalkHere(Character npc, Point3D pos)
    {
        if (pos.X < 0 || pos.Y < 0)
            return true;

        if (npc.NpcBrain == NpcBrainType.Guard && !npc.IsStatFlag(StatFlag.War) &&
            (((OptionFlags)(uint)_config.OptionFlags) & OptionFlags.GuardOutsideGuardedArea) == 0)
        {
            var here = _world.FindRegion(npc.Position);
            if (here != null && here.IsGuarded)
            {
                var there = _world.FindRegion(pos);
                if (there == null || !there.IsGuarded)
                    return false;
            }
        }

        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (item.IsDeleted || item.X != pos.X || item.Y != pos.Y)
                continue;
            int topZ = item.Z + Math.Max(0, (int)item.DefHeight);
            if (Math.Abs(topZ - pos.Z) > 5)
                continue;

            switch (item.ItemType)
            {
                case ItemType.Web:
                    return npc.BodyId == GiantSpiderBody;
                case ItemType.Fire:
                    return (CharDefHelper.GetCanFlags(npc) & CanFlags.C_FireImmune) != 0;
                case ItemType.Trap:
                case ItemType.TrapActive:
                case ItemType.Moongate:
                case ItemType.Telepad:
                    return false;
            }
            if (item.TryGetTag("FIELD_DAMAGE", out _))
                return (CharDefHelper.GetCanFlags(npc) & CanFlags.C_FireImmune) != 0;
        }
        return true;
    }

    /// <summary>The part of the decision the shared walk check does NOT answer: whether
    /// the tile is dangerous or wants swimming, and whether somebody is already standing
    /// there.
    ///
    /// Split out because once CheckMovement has approved a step, re-running the cruder
    /// land-level tests over it only takes the approval away again - a dry deck laid
    /// over water is impassable ground by that measure, which kept a landlocked
    /// creature off its own pier even once the swim rule itself was corrected.</summary>
    private bool CanNpcOccupy(Character npc, Point3D pos, bool checkChars = true)
    {
        if (pos.X < 0 || pos.Y < 0)
            return false;

        if (!CanNpcEnterTile(npc, pos))
            return false;

        if (CharDefHelper.CanPassWalls(npc))
            return true;

        foreach (var other in checkChars ? _world.GetCharsInRange(pos, 0) : [])
        {
            if (other == npc || other.IsDeleted || other.IsDead)
                continue;
            if (other.MapIndex != pos.Map || other.X != pos.X || other.Y != pos.Y)
                continue;
            if ((other.IsStatFlag(StatFlag.Hidden) || other.IsStatFlag(StatFlag.Invisible))
                && other.PrivLevel >= PrivLevel.Counsel)
                continue;
            if (!SharesHeightWith(other, pos.Z))
                continue;
            return false;
        }

        return true;
    }

    private bool CanNpcMoveTo(Character npc, Point3D pos, bool checkChars = true)
    {
        if (!CanNpcMove(npc)) return false;
        if (!CanNpcOccupy(npc, pos, checkChars))
            return false;

        if (!CharDefHelper.CanPassWalls(npc))
        {
            var mapData = _world.MapData;
            if (mapData != null)
            {
                // A real one-tile step goes through the shared walk check, as Source-X
                // routes every NPC step through CanMoveWalkTo -> CheckValidMove
                // (CCharStatus.cpp:1978). The standing-surface probe below only asks
                // whether there is ground with headroom: a wall or fence standing ON
                // that ground (same Z as the floor) never registered as overhead, so
                // wandering, side-stepping and fleeing creatures walked straight
                // through stable fences - and cut diagonally between two of them,
                // since the corner rule lives in the walk check as well (:1991).
                int stepDx = pos.X - npc.X, stepDy = pos.Y - npc.Y;
                if (pos.Map == npc.MapIndex && Math.Max(Math.Abs(stepDx), Math.Abs(stepDy)) == 1)
                {
                    var stepDir = (stepDx, stepDy) switch
                    {
                        (0, -1) => Direction.North,
                        (1, -1) => Direction.NorthEast,
                        (1, 0) => Direction.East,
                        (1, 1) => Direction.SouthEast,
                        (0, 1) => Direction.South,
                        (-1, 1) => Direction.SouthWest,
                        (-1, 0) => Direction.West,
                        _ => Direction.NorthWest,
                    };
                    if (!_world.Standing.CheckMovement(npc, npc.Position, stepDir, out _))
                        return false;
                }

                var stand = _world.Standing.ResolveStandingSurface(npc, pos.Map, pos.X, pos.Y, pos.Z,
                    WalkCheck.StandingPolicy.Settle);
                if (!stand.Found || stand.Z != pos.Z) return false;
            }

            foreach (var item in _world.GetItemsInRange(pos, 0))
            {
                bool passDoor = CharDefHelper.CanPassDoors(npc) &&
                    item.ItemType is ItemType.Door or ItemType.DoorLocked or ItemType.DoorOpen;
                if (!passDoor && item.IsStaticBlock && BlocksAtHeight(item, pos.Z))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Whether a step landing at <paramref name="pos"/> would put the creature
    /// ON the water, rather than on something dry above it.
    ///
    /// Source-X resolves the surface first and only then asks for the ability that
    /// surface needs - CAN_C_SWIM for water, CAN_C_WALK for a platform
    /// (CCharStatus.cpp:1812/1858). SphereNet asked the raw land tile, so a dry jetty,
    /// bridge or deck laid over water demanded that the creature be able to swim, and
    /// a landlocked NPC could not walk across its own pier.</summary>
    internal static bool StandsOnWater(MapData.MapDataManager mapData, Point3D pos)
    {
        var terrain = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
        if (!mapData.GetLandTileData(terrain.TileId).IsWet)
            return false;

        // Above the water line something else is holding the creature up.
        return pos.Z <= terrain.Z;
    }

    /// <summary>Roughly a character's own height, as the shared walk check measures
    /// it (WalkCheck.PersonHeight). Used to decide whether something occupies the same
    /// vertical space as a creature standing on a tile.</summary>
    private const int NpcPersonHeight = 16;

    /// <summary>Whether a blocking item's own vertical extent reaches a creature
    /// standing at <paramref name="standZ"/>. Source-X hands the item's Z and height to
    /// CheckTile_Item, which separates what lies below the floor from what is a ceiling
    /// overhead (CServerMap.cpp:178). SphereNet decided from the item's TYPE alone, so a
    /// wall or a door on another storey closed off the ground underneath it.</summary>
    internal static bool BlocksAtHeight(Item item, int standZ)
    {
        int top = item.Z + Math.Max(1, (int)item.DefHeight);
        return top > standZ && item.Z < standZ + NpcPersonHeight;
    }

    /// <summary>Whether another character stands close enough in Z to be in the way.
    /// Source-X skips anyone more than five Z from the step's destination
    /// (ShoveCharAtPosition, CCharAct.cpp:4622) - someone on the floor above is not
    /// blocking the floor below. SphereNet compared X and Y only.</summary>
    internal static bool SharesHeightWith(Character other, int standZ) =>
        Math.Abs(other.Z - standZ) <= 5;

    // --- Idle: NPC_Act_Idle / NPC_Act_Wander / NPC_Act_GoHome ---

    /// <summary>The idle activity an NPC is in - Source-X keeps it in the action slot
    /// as NPCACT_WANDER / NPCACT_GO_HOME. Runtime state only.</summary>
    internal enum IdleMode : byte
    {
        None = 0,
        Wander = 1,
        GoHome = 2,
    }

    private readonly Dictionary<uint, IdleMode> _idleMode = [];

    /// <summary>Failed steps on the way home, for NPCAIEXTRAS ReturnHome.</summary>
    private readonly Dictionary<uint, int> _homeStepFailures = [];

    /// <summary>How many blocked steps home NPCAIEXTRAS ReturnHome tolerates before it
    /// puts the creature back.</summary>
    internal const int ReturnHomeFailedSteps = 10;

    /// <summary>Test seam: the idle activity an NPC is in.</summary>
    internal IdleMode GetIdleMode(Character npc)
    {
        var act = (NpcAction)npc.Action;
        if (act == NpcAction.Wander) return IdleMode.Wander;
        if (act == NpcAction.GoHome) return IdleMode.GoHome;
        return _idleMode.GetValueOrDefault(npc.Uid.Value);
    }

    internal void SetIdleMode(Character npc, IdleMode mode)
    {
        uint uid = npc.Uid.Value;
        if (mode == IdleMode.None) _idleMode.Remove(uid);
        else _idleMode[uid] = mode;
        if (mode != IdleMode.GoHome) _homeStepFailures.Remove(uid);

        // A script's ACTION=NPCACT_WANDER / NPCACT_GO_HOME ends with the activity.
        var act = (NpcAction)npc.Action;
        if ((act == NpcAction.Wander && mode != IdleMode.Wander) ||
            (act == NpcAction.GoHome && mode != IdleMode.GoHome))
            npc.Action = SkillType.None;
    }

    /// <summary>@NPCActWander arguments (CCharNPCAct.cpp:1269-1278): ARGN1 = stop
    /// wandering, ARGN2 = return home, both seeded and read back.</summary>
    public sealed class WanderTriggerArgs
    {
        public int Stop { get; set; }
        public int ReturnHome { get; set; }
    }

    /// <summary>@NPCSpecialAction (NPC_Act_Idle, CCharNPCAct.cpp:1989): no
    /// arguments; RETURN 1 skips the hardcoded special.</summary>
    public Func<Character, bool>? OnNpcSpecialAction { get; set; }

    /// <summary>What the brains call when they have nothing else to do.</summary>
    private void Wander(Character npc) => ActIdle(npc);

    /// <summary>Same entry as <see cref="Wander"/>; the home leash lives in the wander
    /// step and in go-home, as in Source-X.</summary>
    private void WanderHome(Character npc) => ActIdle(npc);

    /// <summary>Run the idle activity the NPC is in, or pick a new one.</summary>
    private void ActIdle(Character npc)
    {
        if (npc.IsDead || npc.IsDeleted)
            return;

        switch (GetIdleMode(npc))
        {
            case IdleMode.Wander:
                ActWander(npc);
                return;
            case IdleMode.GoHome:
                ActGoHome(npc);
                return;
        }

        ActIdleChoose(npc);
    }

    /// <summary>The tail of Source-X NPC_Act_Idle (CCharNPCAct.cpp:1974-2057), what an
    /// NPC does once it found nothing interesting: a guard off its guarded ground goes
    /// home; at full stamina one time in three a creature takes its special action; one
    /// time in fifteen it heads home; a hider hides now and then; a jumpy creature
    /// (rand(100-DEX) &lt; 25) starts to wander; anything else stands still for one or
    /// two seconds.</summary>
    private void ActIdleChoose(Character npc)
    {
        // NPCAIEXTRAS IdleFlavor: the fidget animation (not in Source-X).
        if (HasExtra(npc, NpcAiExtraFlags.IdleFlavor) && _rand.Next(8) == 0)
        {
            OnNpcFidget?.Invoke(npc);
            return;
        }

        bool hasHome = TryResolveHome(npc, out _, out _);
        var region = _world.FindRegion(npc.Position);
        bool guardedHere = region != null && region.IsGuarded;

        if ((((OptionFlags)(uint)_config.OptionFlags) & OptionFlags.GuardOutsideGuardedArea) == 0 &&
            npc.NpcBrain == NpcBrainType.Guard && !guardedHere && hasHome)
        {
            SetIdleMode(npc, IdleMode.GoHome);
            return;
        }

        // Specific creature random actions (:1987): current stamina at least the DEX.
        if (npc.Stam >= npc.Dex && _rand.Next(3) == 0 && TryNpcSpecialAction(npc))
            return;

        if (hasHome && _rand.Next(15) == 0)
        {
            SetIdleMode(npc, IdleMode.GoHome);
            return;
        }

        int hiding = npc.GetSkill(SkillType.Hiding);
        if (hiding > 30 && RandVal(15 - hiding / 100) == 0 && !guardedHere &&
            !npc.IsStatFlag(StatFlag.Hidden))
        {
            Character.OnScriptSkillUse?.Invoke(npc, SkillType.Hiding);
            return;
        }

        if (RandVal(100 - npc.Dex) < 25)
        {
            SetIdleMode(npc, IdleMode.Wander);
            return;
        }

        // Just stand here for a bit (_SetTimeoutS(1 + rand(2))).
        npc.NextNpcActionTime = Environment.TickCount64 + 1000L * (1 + _rand.Next(2));
    }

    /// <summary>The special-action half of NPC_Act_Idle (:1989-2024): @NPCSpecialAction
    /// first (RETURN 1 skips the rest), then a fire elemental with no fire under it
    /// lays fire, and a creature webs by the OVERRIDE.SPIDERWEB rule. True when the
    /// idle pass ends here.</summary>
    private bool TryNpcSpecialAction(Character npc)
    {
        if (OnNpcSpecialAction != null && OnNpcSpecialAction(npc))
            return true;

        if (npc.BodyId == FireElementalBody)
        {
            if (IsItemTypeAt(npc.Position, ItemType.Fire))
                return false;
            ActStartSpecial(npc, fire: true);
            return true;
        }

        // OVERRIDE.SPIDERWEB inverts the body check: with the key present a creature
        // that is NOT a giant spider webs, without it only a giant spider does. The
        // value is never read.
        bool isSpider = npc.BodyId == GiantSpiderBody;
        bool webs = npc.TryGetTag("OVERRIDE.SPIDERWEB", out _) ? !isSpider : isSpider;
        if (!webs)
            return false;
        ActStartSpecial(npc, fire: false);
        return true;
    }

    private bool IsItemTypeAt(Point3D pos, ItemType type)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (!item.IsDeleted && item.X == pos.X && item.Y == pos.Y && item.ItemType == type)
                return true;
        }
        return false;
    }

    /// <summary>Source-X NPC_Act_Wander (CCharNPCAct.cpp:1224-1289).
    ///
    /// One roll decides it all: the creature stops wandering when
    /// rand % (7 + stamina/30) is zero; the look-around chance is
    /// OVERRIDE.LOOKAROUNDCHANCE (else NPCWANDERLOOKAROUNDCHANCE) raised to at least
    /// 100, exactly as the reference writes maximum(chance, 100), so the look-around
    /// branch never fires from a wander step there either. The step goes one tile along
    /// the current facing turned by -1/0/+1; past HOMEDIST from home it asks to go
    /// home instead. @NPCActWander sees both answers as ARGN1/ARGN2 and may change
    /// them; RETURN 1 ends the step.</summary>
    private void ActWander(Character npc)
    {
        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_NonMover) != 0)
            return;

        int roll = _rand.Next(100);
        int stop = roll % (7 + Math.Max(0, (int)npc.Stam) / 30) == 0 ? 1 : 0;

        long lookChance = TryGetTagNum(npc, "OVERRIDE.LOOKAROUNDCHANCE", out long lc)
            ? lc : _config.NpcWanderLookAroundChance;
        lookChance = Math.Max(lookChance, 100);
        // roll >= lookChance is never true: roll is 0..99. The brains ran their own
        // look-around before they came here.

        int face = (int)npc.Direction & 0x07;
        var dir = (Direction)((face + 1 - roll % 3 + 8) % 8);
        GetDirectionDelta(dir, out short dx, out short dy);
        var target = new Point3D((short)(npc.X + dx), (short)(npc.Y + dy), npc.Z, npc.MapIndex);

        int returnHome = 0;
        if (TryResolveHome(npc, out var home, out int homeDist) && homeDist != 0 &&
            DistOrMax(target, home) > homeDist)
            returnHome = 1;

        if (OnNpcActWander != null)
        {
            var args = new WanderTriggerArgs { Stop = stop, ReturnHome = returnHome };
            if (OnNpcActWander(npc, args))
                return;
            stop = args.Stop;
            returnHome = args.ReturnHome;
        }

        if (stop != 0)
        {
            SetIdleMode(npc, IdleMode.None);
            return;
        }
        if (returnHome != 0)
        {
            SetIdleMode(npc, IdleMode.GoHome);
            return;
        }

        MoveToward(npc, target);
    }

    /// <summary>Source-X GetDist: another map (or no point) is INT16_MAX away.</summary>
    private static int DistOrMax(Point3D a, Point3D b) =>
        a.Map == b.Map ? a.GetDistanceTo(b) : short.MaxValue;

    /// <summary>Source-X NPC_Act_GoHome (CCharNPCAct.cpp:1496-1572).
    ///
    /// A guard whose post is on guarded ground and who stands off it is teleported
    /// home; a guard whose post is not guarded is removed unless
    /// OF_GuardOutsideGuardedArea allows it. Anyone else walks home until closer than
    /// HOMEDIST (a HOMEDIST of 0 walks all the way). LOSTNPCTELEPORT puts back a
    /// creature farther than both it and HOMEDIST, @NPCLostTeleport (ARGN1 = the
    /// distance) able to refuse.</summary>
    private void ActGoHome(Character npc)
    {
        if (!TryResolveHome(npc, out var home, out int homeDist))
        {
            SetIdleMode(npc, IdleMode.None);
            return;
        }

        if (npc.NpcBrain == NpcBrainType.Guard)
        {
            var homeRegion = _world.FindRegion(home);
            if (homeRegion != null && homeRegion.IsGuarded)
            {
                var hereRegion = _world.FindRegion(npc.Position);
                if (hereRegion == null || !hereRegion.IsGuarded)
                {
                    if (TeleportNpc(npc, home))
                    {
                        SetIdleMode(npc, IdleMode.None);
                        return;
                    }
                }
            }
            else if ((((OptionFlags)(uint)_config.OptionFlags) & OptionFlags.GuardOutsideGuardedArea) == 0)
            {
                // "Guard has no guard post! Removing it." (:1526)
                _world.DeleteObject(npc);
                return;
            }
        }

        int curDist = DistOrMax(npc.Position, home);
        if (curDist < homeDist)
        {
            SetIdleMode(npc, IdleMode.None);
            return;
        }

        if (LostNpcTeleport > 0 && curDist > LostNpcTeleport && curDist > homeDist &&
            Character.OnNpcLostTeleport?.Invoke(npc, curDist) != true)
        {
            TeleportNpc(npc, home);
        }

        int result = MoveToward(npc, home);
        if (result == 0)
        {
            SetIdleMode(npc, IdleMode.None);
            return;
        }

        // NPCAIEXTRAS ReturnHome: a walk home that keeps failing ends in a teleport.
        uint uid = npc.Uid.Value;
        if (result == 2 && HasExtra(npc, NpcAiExtraFlags.ReturnHome))
        {
            int fails = _homeStepFailures.GetValueOrDefault(uid) + 1;
            if (fails >= ReturnHomeFailedSteps)
            {
                _homeStepFailures.Remove(uid);
                if (SendHome(npc, home, DistOrMax(npc.Position, home)))
                    SetIdleMode(npc, IdleMode.None);
            }
            else
            {
                _homeStepFailures[uid] = fails;
            }
        }
        else if (result == 1)
        {
            _homeStepFailures.Remove(uid);
        }
    }

    private bool TeleportNpc(Character npc, Point3D dest)
    {
        if (dest.X < 0 || dest.Y < 0 || _world.GetSector(dest) == null)
            return false;
        if (!_world.MoveCharacter(npc, dest))
            return false;
        OnNpcTeleport?.Invoke(npc);
        return true;
    }

    /// <summary>NPCAIEXTRAS ReturnHome: put the creature back at home, through
    /// @NPCLostTeleport so a script can refuse (Character.OnNpcLostTeleport).</summary>
    private bool SendHome(Character npc, Point3D home, int dist)
    {
        if (Character.OnNpcLostTeleport?.Invoke(npc, dist) == true)
            return false;
        if (!TeleportNpc(npc, home))
            return false;
        uint uid = npc.Uid.Value;
        _idleMode.Remove(uid);
        _homeStepFailures.Remove(uid);
        return true;
    }

    /// <summary>NPCAIEXTRAS ReturnHome, the parked half: an NPC whose sector is
    /// inactive, that is nobody's pet, has a home and stands more than HOMEDIST + 5
    /// from it, is sent home.</summary>
    private void TryReturnHomeWhileParked(Character npc)
    {
        if (npc.NpcMaster.IsValid || npc.OwnerSerial.IsValid)
            return;
        if (!TryResolveHome(npc, out var home, out int homeDist))
            return;
        int dist = DistOrMax(npc.Position, home);
        if (dist <= homeDist + 5)
            return;
        SendHome(npc, home, dist);
    }

    /// <summary>Source-X NPC_WalkToPoint blocked-step fallback: every mover —
    /// regardless of INT or the PATH flag — gets a ~70% chance to sidestep by
    /// turning ±1..±4 directions off the blocked heading and taking that tile
    /// (CCharNPCAct.cpp:497-519). The step it takes gets the move delay.</summary>
    private bool TrySideStep(Character npc, Direction dir, bool run = false)
    {
        int roll = _rand.Next(100);
        if (roll < 30)
            return false;
        int diff = roll < 35 ? 4 : roll < 40 ? 3 : roll < 65 ? 2 : 1;
        if ((roll & 1) != 0)
            diff = -diff;
        var sideDir = (Direction)((((int)dir & 0x07) + diff + 8) % 8);
        if (!TryNpcStep(npc, sideDir, out var pos))
            return false;
        npc.Direction = run ? sideDir | Direction.Running : sideDir;
        _world.MoveCharacter(npc, pos);
        ApplyStepDelay(npc, run);
        return true;
    }

    /// <summary>Home from Character.Home field; legacy TAG.HOME_* fallback.</summary>
    /// <summary>NPC step Z via the shared standing resolver (audit design):
    /// GetEffectiveZ saw neither multis, dynamics nor custom houses, so an
    /// NPC walking a ship deck or house floor drifted onto the terrain
    /// underneath the structure.</summary>
    private sbyte ResolveNpcStepZ(Character npc, int x, int y) =>
        TryResolveNpcStepZ(npc, x, y, out sbyte z) ? z : npc.Z;

    /// <summary>The height the NPC would actually stand at on this tile, and whether a
    /// surface was found at all. A cached A* step carries only the search's cheap
    /// approximation (Pathfinder uses MapData.GetEffectiveZ, which sees neither multis
    /// nor dynamics), so applying its Z verbatim walked an NPC off a ship deck or house
    /// floor onto the terrain underneath. Source-X resolves the surface at the moment
    /// of the step instead (CheckValidMove, CCharStatus.cpp:1972).</summary>
    private bool TryResolveNpcStepZ(Character npc, int x, int y, out sbyte z)
    {
        if (_world.Standing.ResolveStandingSurface(npc, npc.MapIndex, x, y, npc.Z,
                WalkCheck.StandingPolicy.Settle) is { Found: true } stand)
        {
            z = stand.Z;
            return true;
        }

        z = npc.Z;
        return false;
    }

    /// <summary>Whether the NPC may take this single step. On top of the destination
    /// tile itself, a DIAGONAL step needs both of the orthogonal tiles beside it:
    /// Source-X tests them from the old point before moving (CheckValidMove,
    /// CCharStatus.cpp:1988) so a creature cannot cut through the corner where two
    /// walls meet. The reference skips that test while pathfinding (fPathFinding) and
    /// applies it to the real step, which is why the search alone was never the place
    /// for it - the direct step never runs A* at all.</summary>
    /// <summary>Whether the NPC is physically able to walk at all right now.
    ///
    /// Source-X asks this at the real step, through CanMoveWalkTo -> CanMove
    /// (CCharAct.cpp:4716/4571): a GM is exempt, a frozen or stoned creature cannot
    /// move (OnFreezeCheck, :4525), and a living one out of stamina cannot either. The
    /// reference reads the CURRENT stamina pool there - Stat_GetVal(STAT_DEX) - not the
    /// base dexterity.
    ///
    /// It belongs at the step rather than at the decision: a freeze landing between the
    /// two used to leave the walk running anyway. NpcAI.OnTickAction's own comment said
    /// "living, non-frozen NPC" while nothing checked it; combat had the test, movement
    /// did not.</summary>
    private static bool CanNpcMove(Character npc)
    {
        var can = CharDefHelper.GetCanFlags(npc);
        if ((can & (CanFlags.C_NonMover | CanFlags.C_Statue)) != 0 ||
            ((can & (CanFlags.C_Walk | CanFlags.C_Swim | CanFlags.C_Fly | CanFlags.C_Hover | CanFlags.C_PassWalls)) == 0 &&
             !npc.IsStatFlag(StatFlag.Hovering)))
            return false;
        if (npc.PrivLevel >= PrivLevel.GM)
            return true;
        if (npc.IsStatFlag(StatFlag.Freeze) || npc.IsStatFlag(StatFlag.Stone))
            return false;
        // Only where a stamina pool actually exists. A creature with no MaxStam has no
        // stamina model at all - the Dex setter raises the ceiling but never fills the
        // pool - and reading its empty pool as exhaustion would leave it standing
        // still forever rather than tiring it out.
        return npc.IsDead || npc.MaxStam <= 0 || npc.Stam > 0;
    }

    /// <summary>Work out the step the NPC would take in <paramref name="dir"/>, and
    /// where it would land.
    ///
    /// The geometry comes from the shared walk check - the very one a player's step
    /// goes through - which knows tile heights, ceilings, climb limits, the diagonal
    /// double-edge rule and the surface to land on. The NPC path used to answer from
    /// item TYPE alone, so it walked into an impassable object that happened not to be
    /// a Wall or Door, and under a ceiling too low to fit; the height it moved to was
    /// whatever a separate resolver guessed. Source-X routes the real NPC step through
    /// CanMoveWalkTo -> CheckValidMove for exactly this reason
    /// (NPC_WalkToPoint, CCharNPCAct.cpp:493).
    ///
    /// Without map data there is no geometry to consult - the shape a unit-test world
    /// has - so the older tile checks stand in, with the diagonal corner rule applied
    /// on its own.</summary>
    private bool TryNpcStep(Character npc, Direction dir, out Point3D dest)
    {
        var plain = dir & ~Direction.Running;
        GetDirectionDelta(plain, out short dx, out short dy);
        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        dest = default;

        if (_world.MapData != null)
        {
            if (!_world.Standing.CheckMovement(npc, npc.Position, plain, out int landZ))
                return false;

            dest = new Point3D(nx, ny, (sbyte)landZ, npc.MapIndex);
            return CanNpcOccupy(npc, dest);
        }

        sbyte fallbackZ = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(fallbackZ - npc.Z) > 12)
            return false;

        dest = new Point3D(nx, ny, fallbackZ, npc.MapIndex);
        return CanNpcStepTo(npc, plain, dest);
    }

    private bool CanNpcStepTo(Character npc, Direction dir, Point3D dest)
    {
        if (!CanNpcMoveTo(npc, dest))
            return false;

        var plain = dir & ~Direction.Running;
        if (((byte)plain & 1) == 0)
            return true;    // an orthogonal step has no corner to cut

        foreach (var side in new[]
        {
            (Direction)(((byte)plain + 7) % 8),   // first orthogonal
            (Direction)(((byte)plain + 1) % 8),   // second
        })
        {
            GetDirectionDelta(side, out short sdx, out short sdy);
            short sx = (short)(npc.X + sdx);
            short sy = (short)(npc.Y + sdy);
            // Terrain and structures only. Source-X tests the side tiles with
            // CheckValidMove, which knows nothing about characters - blocking mobiles
            // are weighed against the DESTINATION alone (CanMoveWalkTo's fCheckChars).
            // Counting them here would stop a creature walking diagonally past anyone
            // standing beside it.
            var sidePos = new Point3D(sx, sy, ResolveNpcStepZ(npc, sx, sy), npc.MapIndex);
            if (!CanNpcMoveTo(npc, sidePos, checkChars: false))
                return false;
        }

        return true;
    }

    private static bool TryResolveHome(Character npc, out Point3D home, out int wanderDist)
    {
        // Source-X default m_Home_Dist_Wander = INT16_MAX ("as far as I
        // want") — a home point is only a leash when HOMEDIST is scripted.
        // The old default of 10 confined every homed spawn to a small box.
        // An explicit 0 is kept as 0, as upstream keeps it: the wander step then has
        // no leash (:1263) and a walk home goes all the way (:1541).
        wanderDist = Math.Max(0, (int)npc.HomeDist);
        if (npc.Home.X != 0 || npc.Home.Y != 0)
        {
            home = npc.Home;
            return true;
        }

        if (npc.TryGetTag("HOME_X", out string? hx) && npc.TryGetTag("HOME_Y", out string? hy) &&
            short.TryParse(hx, out short homeX) && short.TryParse(hy, out short homeY))
        {
            sbyte homeZ = npc.Z;
            if (npc.TryGetTag("HOME_Z", out string? hz) &&
                sbyte.TryParse(hz, out sbyte parsedZ))
                homeZ = parsedZ;
            byte homeMap = npc.MapIndex;
            if (npc.TryGetTag("HOME_MAP", out string? hm) &&
                byte.TryParse(hm, out byte parsedMap))
                homeMap = parsedMap;
            home = new Point3D(homeX, homeY, homeZ, homeMap);
            if (npc.TryGetTag("HOME_DIST", out string? hdStr) &&
                int.TryParse(hdStr, out int hd) && hd > 0)
                wanderDist = Math.Clamp(hd, 1, short.MaxValue);
            return true;
        }

        home = default;
        return false;
    }

    private void DropPath(uint uid)
    {
        _pathCache.Remove(uid);
        _pathIndex.Remove(uid);
        _pathTime.Remove(uid);
    }

    /// <summary>Source-X NPC_WalkToPoint (CCharNPCAct.cpp:418-696): one step toward
    /// <paramref name="target"/>. Returns 0 when already there (or a non-mover),
    /// 1 when a step was taken - or, while a route is in use, when the NPC waits a
    /// moment before trying again - and 2 when the step cannot be taken now.
    ///
    /// With NPC_AI_PATH and INT 30+ a stored route is walked first, even when the
    /// straight line is open, and a route toward a point the target has since left
    /// is searched again with a chance of INT/300 (NPC_Pathfinding, :2424/:2448);
    /// otherwise the old route keeps being walked. A blocked step tries a door, a
    /// movable obstacle, a fresh route (the search runs off the serial path for
    /// chases, see TryPrestagePathfind, and here only once the step is blocked, under
    /// the engine's per-NPC throttle), then the reference's side-step.</summary>
    private int MoveToward(Character npc, Point3D target, bool run = false)
    {
        if ((CharDefHelper.GetCanFlags(npc) & CanFlags.C_NonMover) != 0)
            return 0;
        if (target.Map != npc.MapIndex || (target.X == npc.X && target.Y == npc.Y))
            return 0;
        run = run && CanRunNow(npc);

        // Physically able to walk at all? Read at the step, not at the decision.
        if (!CanNpcMove(npc))
            return 2;

        uint uid = npc.Uid.Value;
        var npcFlags = GetNpcFlags(npc);
        int effInt = npcFlags.HasFlag(NpcAIFlags.AlwaysInt) ? 300 : npc.Int;
        bool smartPath = npcFlags.HasFlag(NpcAIFlags.Path) && effInt >= 30;
        int targetDist = npc.Position.GetDistanceTo(target);
        bool pathEligible = smartPath && targetDist >= NpcPathMinDist && targetDist < NpcPathMaxDist;

        var dir = npc.Position.GetDirectionTo(target);
        bool usePath = false;

        if (smartPath)
        {
            if (_pathGoal.TryGetValue(uid, out var oldGoal) &&
                (oldGoal.Map != target.Map || oldGoal.X != target.X || oldGoal.Y != target.Y))
            {
                if (!_pathCache.ContainsKey(uid))
                {
                    // Only a throttle / failed-search backoff toward an old point: a
                    // destination that moved materially gets its search back at once
                    // (the backoff stays for a target a step or two off, so an
                    // unreachable chase cannot search every tick).
                    if (oldGoal.Map != target.Map || oldGoal.GetDistanceTo(target) > 2)
                    {
                        _pathGoal.Remove(uid);
                        _nextPathfindMs.Remove(uid);
                    }
                }
                else if (pathEligible && _rand.Next(300) <= effInt)
                {
                    // A route toward somewhere else: search again with a chance of
                    // INT/300, else keep walking the old one.
                    DropPath(uid);
                    _pathGoal.Remove(uid);
                    _nextPathfindMs.Remove(uid);
                }
            }

            if (_pathCache.TryGetValue(uid, out var cached) && cached.Count > 0)
            {
                int idx = _pathIndex.GetValueOrDefault(uid, 0);
                if (idx < cached.Count && cached[idx].Map == npc.MapIndex &&
                    npc.Position.GetDistanceTo(cached[idx]) == 1)
                {
                    // Head along the route and shift the step out (:474-485).
                    dir = npc.Position.GetDirectionTo(cached[idx]);
                    _pathIndex[uid] = idx + 1;
                    usePath = true;
                }
                else
                {
                    // Exhausted, or the next step is no longer one tile away: the
                    // stored route has become invalid (:464-470).
                    DropPath(uid);
                }
            }
        }

        // The tile the step aims at, for the door/obstacle lookups below; the height
        // the NPC would actually land on is settled by TryNpcStep.
        GetDirectionDelta(dir, out short dx, out short dy);
        var stepTile = new Point3D((short)(npc.X + dx), (short)(npc.Y + dy), npc.Z, npc.MapIndex);

        bool blocked = !TryNpcStep(npc, dir, out var dest);

        // Reference parity (NPC door handling in the idle look-at path): a
        // blocked adjacent step may just be a closed door — try to open it
        // (50% per attempt, like the reference) and re-check the tile.
        if (blocked && OnNpcOpenDoor != null &&
            (CharDefHelper.GetCanFlags(npc) & CanFlags.C_UseHands) != 0 && _rand.Next(2) == 0)
        {
            var door = FindClosedDoorAt(stepTile);
            if (door != null && OnNpcOpenDoor(npc, door))
                blocked = !TryNpcStep(npc, dir, out dest);
        }

        // NPC_AI_MOVEOBSTACLES (:525): shift a movable blocking item out of the way.
        if (blocked && TryClearObstacle(npc, stepTile))
            blocked = !TryNpcStep(npc, dir, out dest);

        if (blocked && usePath)
        {
            // The route's step is shut; the route is spent (:464 drops it next time).
            DropPath(uid);
        }

        if (blocked && !usePath && pathEligible && !_pathCache.ContainsKey(uid))
        {
            // No route yet and the straight line is shut: search one now, under the
            // per-NPC throttle, and take its first step.
            var route = ComputeRouteSerial(npc, target, uid);
            if (route != null && route.Count > 0 && route[0].Map == npc.MapIndex &&
                npc.Position.GetDistanceTo(route[0]) == 1)
            {
                var routeDir = npc.Position.GetDirectionTo(route[0]);
                if (TryNpcStep(npc, routeDir, out var routeDest))
                {
                    _pathIndex[uid] = 1;
                    dir = routeDir;
                    dest = routeDest;
                    blocked = false;
                    usePath = true;
                }
                else
                {
                    DropPath(uid);
                }
            }
            else if (route != null)
            {
                usePath = true;   // a route exists: keep looking for a way (:503)
            }
        }

        if (blocked)
        {
            if (TrySideStep(npc, dir, run))
                return 1;

            npc.Direction = dir;
            if (usePath || _pathCache.ContainsKey(uid))
            {
                // Whilst pathfinding keep trying new ways: wait a moment (:505/:577).
                npc.NextNpcActionTime = Math.Min(npc.NextNpcActionTime, Environment.TickCount64 + 500);
                return 1;
            }
            return 2;
        }

        npc.Direction = run ? dir | Direction.Running : dir;
        _world.MoveCharacter(npc, dest);
        ApplyStepDelay(npc, run);
        if (!usePath)
        {
            // Walking the straight line: nothing stored to keep, and the next blocked
            // step may search at once.
            DropPath(uid);
            _pathGoal.Remove(uid);
            _nextPathfindMs.Remove(uid);
        }
        return 1;
    }

    /// <summary>Serial A* search toward <paramref name="target"/>, throttled per NPC
    /// (a success allows the next one after PathThrottleMs, a failure backs off for
    /// PathFailBackoffMs). Stores and returns the route; null when the throttle is
    /// closed or the search failed.</summary>
    private List<Point3D>? ComputeRouteSerial(Character npc, Point3D target, uint uid)
    {
        long nowMs = Environment.TickCount64;
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && nowMs < nextPf)
            return null;

        var npcCanFlags = CharDefHelper.GetCanFlags(npc);
        var path = _pathfinder.FindPath(npc.Position, target, npc.MapIndex, npcCanFlags, npc,
            NpcPathMaxNodes, NpcPathMaxDist);
        if (path == null || path.Count == 0)
        {
            _nextPathfindMs[uid] = nowMs + PathFailBackoffMs;
            _pathGoal[uid] = target;
            return null;
        }
        _nextPathfindMs[uid] = nowMs + PathThrottleMs;
        _pathCache[uid] = path;
        _pathIndex[uid] = 0;
        _pathTime[uid] = nowMs;
        _pathGoal[uid] = target;
        return path;
    }

    // --- N2: parallel-phase pathfind prestage ---

    // Per-tick A* budget consumed by the prestage. On a low-core box the
    // parallel build phase cannot absorb many 500-node searches (a chase burst
    // showed up live as npc_build=130ms); over-budget NPCs take a short defer
    // instead — the serial side sees the closed throttle window and just faces
    // the target until their turn comes.
    private const long PathDeferMs = 150;

    /// <summary>
    /// Who may run a prestage A* this tick, decided in the SERIAL phase.
    ///
    /// The budget used to be a counter the parallel workers raced to decrement, so the
    /// winners were whoever reached it first: the same world and the same tick could
    /// hand the searches to different creatures on two runs, and the losers are not
    /// merely skipped - they take a 150ms path defer, which is state. Sorting the
    /// decisions before Apply cannot undo that, so "deterministic" described the ORDER
    /// of application and not the WORLD it produced (review finding B5).
    ///
    /// Read-only during the parallel phase, which is what makes it safe to share.
    /// </summary>
    private readonly HashSet<uint> _pathfindAdmitted = [];

    /// <summary>Whether a budget was armed for this tick at all. Un-armed means
    /// UNLIMITED, which is what the old counter's int.MaxValue default meant: a caller
    /// that builds decisions without arming a budget should get the searches it asks
    /// for, not silence. Degrading to "nobody may search" would be the quiet kind of
    /// failure - creatures stop pathing and nothing says why.</summary>
    private bool _pathfindBudgetArmed;

    /// <summary>Pick this tick's prestage A* winners, in a stable and fair order.
    ///
    /// Stable: the snapshot's own order, so the choice does not depend on how many
    /// workers happen to be running. Fair: the starting point rotates with the tick, so
    /// the creatures at the front of the list do not take every search forever while
    /// the ones behind them are deferred indefinitely - which a fixed order would do,
    /// and which the racing counter avoided only by being unpredictable.
    ///
    /// Admission is a CAP, not a quota: a winner whose direct step turns out to be open
    /// needs no search and simply does not spend it. Deciding that here would mean
    /// doing the map work this phase exists to keep off the serial thread.</summary>
    public void BeginTickPathfindBudget(int budget, IReadOnlyList<Character> npcs, long tickNumber)
    {
        _pathfindAdmitted.Clear();
        _pathfindBudgetArmed = true;
        if (budget <= 0 || npcs.Count == 0)
            return;

        int start = npcs.Count > 0 ? (int)(uint)(tickNumber % npcs.Count) : 0;
        for (int i = 0; i < npcs.Count && _pathfindAdmitted.Count < budget; i++)
        {
            var npc = npcs[(start + i) % npcs.Count];
            if (WantsPrestagePathfind(npc))
                _pathfindAdmitted.Add(npc.Uid.Value);
        }
    }

    /// <summary>The cheap half of the prestage test: does this creature even want a
    /// search? Dictionary and field reads only - the map probe and the search itself
    /// stay in the parallel phase.</summary>
    private bool WantsPrestagePathfind(Character npc)
    {
        var target = ResolveChaseTarget(npc);
        if (target == null)
            return false;

        int dist = npc.Position.GetDistanceTo(target.Position);
        if (dist < NpcPathMinDist || dist >= NpcPathMaxDist)
            return false;

        var npcFlags = GetNpcFlags(npc);
        if (!npcFlags.HasFlag(NpcAIFlags.Path))
            return false;
        if ((npcFlags.HasFlag(NpcAIFlags.AlwaysInt) ? 300 : npc.Int) < 30)
            return false;

        uint uid = npc.Uid.Value;
        if (_pathCache.TryGetValue(uid, out var cachedPath) && cachedPath.Count > 0)
            return false;
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && Environment.TickCount64 < nextPf)
            return false;

        return true;
    }

    /// <summary>The goal the serial brain will walk toward this tick: the fight target,
    /// else the master for a following pet. Other MoveToward callers (wander-home
    /// leash, corpse looting, investigate) stay serial-computed.</summary>
    private Character? ResolveChaseTarget(Character npc)
    {
        Character? target = null;
        if (npc.FightTarget.IsValid)
            target = _world.FindChar(npc.FightTarget);
        else if (npc.NpcMaster.IsValid &&
                 npc.PetAIMode is PetAIMode.Follow or PetAIMode.Come or PetAIMode.Guard)
            target = _world.FindChar(npc.NpcMaster);
        if (target == null || target.IsDeleted || target.IsDead || target.MapIndex != npc.MapIndex)
            return null;
        return target;
    }

    /// <summary>Test seam: the uids admitted for this tick's prestage searches.</summary>
    internal IReadOnlyCollection<uint> AdmittedPathfinders => _pathfindAdmitted;

    /// <summary>Read-only mirror of the conditions under which the serial
    /// <see cref="MoveToward"/> would run a full A* this tick for a combat/pet
    /// chase. When they hold, runs the search HERE (parallel build phase —
    /// Pathfinder scratch state is thread-static, world reads follow the
    /// parallel-compute-phase safety contract) and returns the result to be
    /// carried on the <see cref="NpcDecision"/>. Mutates nothing.</summary>
    private (List<Point3D>? Path, Point3D Goal, bool Ran, bool Deferred) TryPrestagePathfind(Character npc)
    {
        var target = ResolveChaseTarget(npc);
        if (target == null)
            return (null, default, false, false);

        Point3D goal = target.Position;
        int dist = npc.Position.GetDistanceTo(goal);
        if (dist < NpcPathMinDist || dist >= NpcPathMaxDist)
            return (null, default, false, false);

        var npcFlags = GetNpcFlags(npc);
        if (!npcFlags.HasFlag(NpcAIFlags.Path))
            return (null, default, false, false);
        int effInt = npcFlags.HasFlag(NpcAIFlags.AlwaysInt) ? 300 : npc.Int;
        if (effInt < 30)
            return (null, default, false, false);

        // Source-X routes whenever the NPC has no stored route (NPC_Pathfinding,
        // CCharNPCAct.cpp:2448 - "always search if this is a first step"), whether
        // or not the straight line happens to be open; the search runs here, off the
        // serial path, under the per-tick budget.
        uint uid = npc.Uid.Value;
        // A stored route is the serial side's to keep or drop (the INT/300 re-search
        // roll lives in MoveToward).
        if (_pathCache.TryGetValue(uid, out var cachedPath) && cachedPath.Count > 0)
            return (null, default, false, false);
        // Throttle/backoff window still closed → serial won't recompute either.
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) &&
            Environment.TickCount64 < nextPf)
            return (null, default, false, false);

        // Per-tick A* budget: over-budget chasers take a short defer instead of
        // stacking 500-node searches into one build phase. Who is admitted was decided
        // serially before the fan-out, so it does not depend on which worker got here
        // first (review finding B5).
        if (_pathfindBudgetArmed && !_pathfindAdmitted.Contains(uid))
            return (null, goal, false, true);

        var npcCanFlags = CharDefHelper.GetCanFlags(npc);
        var path = _pathfinder.FindPath(npc.Position, goal, npc.MapIndex, npcCanFlags, npc,
            NpcPathMaxNodes, NpcPathMaxDist);
        return (path, goal, true, false);
    }

    /// <summary>Serial-phase merge of a parallel prestage result into the path
    /// cache, applied only when the serial state still calls for a recompute
    /// (no fresh path, throttle open) — the serial side stays the source of
    /// truth. A failed search records the same fail-backoff the serial compute
    /// would have, so the ~17ms unreachable burn never hits the apply phase.
    /// The cached step is still re-validated by MoveToward before use.</summary>
    private void SeedPrestagedPath(Character npc, in NpcDecision decision)
    {
        uint uid = npc.Uid.Value;
        long nowMs = Environment.TickCount64;

        if (_pathCache.TryGetValue(uid, out var existing) && existing.Count > 0)
            return;
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && nowMs < nextPf)
            return;

        // Over-budget defer: close the serial window briefly so this tick's
        // apply phase doesn't run the search the budget just refused.
        if (decision.PrestageDeferred)
        {
            _nextPathfindMs[uid] = nowMs + PathDeferMs;
            _pathGoal[uid] = decision.PrestageGoal;
            return;
        }

        if (decision.PrestagedPath is { Count: > 0 } path)
        {
            _pathCache[uid] = path;
            _pathIndex[uid] = 0;
            _pathTime[uid] = nowMs;
            _pathGoal[uid] = decision.PrestageGoal;
            _nextPathfindMs[uid] = nowMs + PathThrottleMs;
        }
        else
        {
            _nextPathfindMs[uid] = nowMs + PathFailBackoffMs;
            _pathGoal[uid] = decision.PrestageGoal;
        }
    }

    /// <summary>Find a closed, unlocked door item on the given tile
    /// (within a reasonable Z window). Internal for tests.</summary>
    internal Item? FindClosedDoorAt(Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (item.IsDeleted) continue;
            if (Math.Abs(item.Z - pos.Z) > 15) continue;
            if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
            if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) continue;
            if (item.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1") continue;
            return item;
        }
        return null;
    }

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
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

    private static int DeterministicJitter(uint uid, long nowTick, int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        unchecked
        {
            uint mixed = uid * 2654435761u ^ (uint)nowTick;
            return (int)(mixed % (uint)maxExclusive);
        }
    }

}
