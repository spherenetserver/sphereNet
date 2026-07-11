using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Components;

/// <summary>
/// Spawn point component for IT_SPAWN_CHAR items.
/// Maps to CCSpawn in Source-X. Periodically creates NPCs within range.
/// Supports both single chardef (MORE1 = body ID) and spawn groups (MORE1 → SPAWN defname).
/// </summary>
public sealed class SpawnComponent
{
    private readonly Item _spawnItem;
    private readonly GameWorld _world;
    private readonly List<Serial> _spawnedUids = [];

    private ushort _charDefId;
    private SpawnGroupDef? _spawnGroup;
    private int _maxCount = 1;
    private int _spawnRange = 15;
    private int _minDelaySec = 900;   // Source-X default: 15 min
    private int _maxDelaySec = 1800;  // Source-X default: 30 min
    private long _nextSpawnTick;
    private bool _stopped;
    private bool _killingChildren;
    private readonly Random _rand = new();
    private ResourceHolder? _resources;

    /// <summary>Trigger dispatch delegate wired from Program.cs.
    /// Fires @PreSpawn, @Spawn, @AddObj, @DelObj on the spawn item.</summary>
    public static Func<Item, ItemTrigger, SpawnTriggerArgs, TriggerResult>? OnSpawnTrigger;

    private const int MaxSpawnLimit = 250;

    public int CurrentCount => _spawnedUids.Count;

    /// <summary>Source-X IT_SPAWN_CHAMPION: bypass the amount cap and never
    /// pause the timer (CCSpawn special-cases the champion type throughout).</summary>
    public bool IsChampion { get; set; }

    public int MaxCount
    {
        get => _maxCount;
        set
        {
            _maxCount = Math.Clamp(value, 1, MaxSpawnLimit);
            _spawnItem.Amount = (ushort)_maxCount;
        }
    }
    public ushort CharDefId { get => _charDefId; set => _charDefId = value; }
    public int SpawnRange { get => _spawnRange; set => _spawnRange = value; }
    public SpawnGroupDef? SpawnGroup { get => _spawnGroup; set => _spawnGroup = value; }
    public IReadOnlyList<Serial> SpawnedUids => _spawnedUids;
    public bool IsStopped => _stopped;

    public SpawnComponent(Item spawnItem, GameWorld world)
    {
        _spawnItem = spawnItem;
        _world = world;
        SetNextSpawnTime();
    }

    /// <summary>Called each tick from the item's OnTick.</summary>
    public void OnTick(long currentTick)
    {
        if (_stopped) return;

        int prevCount = _spawnedUids.Count;
        CleanupDead();

        // NPC died → re-enable timer if it was paused at max
        if (_spawnedUids.Count < prevCount && _spawnedUids.Count < _maxCount)
        {
            if (_nextSpawnTick < 0)
                SetNextSpawnTime();
        }

        if (_nextSpawnTick < 0) return; // paused at max count
        if (currentTick < _nextSpawnTick) return;
        // Source-X IT_SPAWN_CHAMPION: a champion spawner ignores the amount
        // cap and never pauses its timer — the wave keeps coming.
        if (!IsChampion && _spawnedUids.Count >= _maxCount)
        {
            PauseTimer();
            return;
        }
        if (_charDefId == 0 && _spawnGroup == null)
        {
            // Misconfigured spawner (no def/group): reschedule so it doesn't
            // re-enter and bail on every item tick of the active sector.
            SetNextSpawnTime();
            return;
        }

        SpawnOne();

        if (!IsChampion && _spawnedUids.Count >= _maxCount)
            PauseTimer();
        else
            SetNextSpawnTime();
    }

    private void SpawnOne()
    {
        ushort bodyId = _charDefId;
        int defIndex = bodyId;

        if (_spawnGroup != null)
        {
            string? memberName = _spawnGroup.SelectRandomMember(_rand);
            if (string.IsNullOrEmpty(memberName))
                return;

            if (_resources != null)
            {
                var rid = _resources.ResolveDefName(memberName);
                if (rid.IsValid && rid.Type == ResType.CharDef)
                {
                    defIndex = rid.Index;
                    bodyId = (ushort)Math.Clamp(defIndex, 0, ushort.MaxValue);
                }
                else
                    return;
            }
            else
                return;
        }
        else if (_charDefId == 0)
        {
            // tag.spawn_array fallback: comma-separated chardef names used by
            // typedef @Timer scripts (e.g. t_custom_spawner_char).
            string? spawnArray = _spawnItem.Tags.Get("spawn_array");
            if (!string.IsNullOrEmpty(spawnArray) && _resources != null)
            {
                var entries = spawnArray.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (entries.Length > 0)
                {
                    string pick = entries[_rand.Next(entries.Length)];
                    var rid = _resources.ResolveDefName(pick);
                    if (rid.IsValid && rid.Type == ResType.CharDef)
                    {
                        defIndex = rid.Index;
                        bodyId = (ushort)Math.Clamp(defIndex, 0, ushort.MaxValue);
                    }
                    else
                        return;
                }
                else
                    return;
            }
            else
                return;
        }

        // @PreSpawn — script can override spawn ID or abort (return TRUE)
        if (OnSpawnTrigger != null)
        {
            var preArgs = new SpawnTriggerArgs { SpawnDefIndex = defIndex };
            var result = OnSpawnTrigger(_spawnItem, ItemTrigger.PreSpawn, preArgs);
            if (result == TriggerResult.True)
                return; // script aborted spawn
            if (preArgs.SpawnDefIndex != defIndex)
            {
                defIndex = preArgs.SpawnDefIndex;
                bodyId = (ushort)Math.Clamp(defIndex, 0, ushort.MaxValue);
            }
        }

        var ch = _world.CreateCharacter();
        ch.BaseId = bodyId;
        ch.BodyId = bodyId;
        ch.CharDefIndex = defIndex;
        ch.IsPlayer = false;

        var charDef = DefinitionLoader.GetCharDef(defIndex);
        if (charDef != null)
        {
            if (charDef.DispIndex > 0)
            {
                ch.BodyId = charDef.DispIndex;
                ch.BaseId = charDef.DispIndex;
            }
            ch.OBody = ch.BodyId;

            if (!string.IsNullOrWhiteSpace(charDef.Name))
            {
                if (charDef.Name.Contains("#NAMES_", StringComparison.OrdinalIgnoreCase))
                    ch.Name = DefinitionLoader.ResolveNames(charDef.Name);
                else
                    ch.Name = charDef.Name;
            }
            else
                ch.Name = $"Spawn_{bodyId:X}";

            int strVal = RandomRange(charDef.StrMin, charDef.StrMax);
            int dexVal = RandomRange(charDef.DexMin, charDef.DexMax);
            int intVal = RandomRange(charDef.IntMin, charDef.IntMax);

            ch.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
            ch.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
            ch.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);

            int hitsVal = charDef.HitsMax > 0
                ? RandomRange(charDef.HitsMin, charDef.HitsMax)
                : Math.Max(1, strVal);
            ch.MaxHits = (short)Math.Clamp(hitsVal, 1, short.MaxValue);
            ch.Hits = ch.MaxHits;
            ch.MaxMana = ch.Int;
            ch.Mana = ch.Int;
            ch.MaxStam = ch.Dex;
            ch.Stam = ch.Dex;

            if (charDef.NpcBrain != NpcBrainType.None)
                ch.NpcBrain = charDef.NpcBrain;

            if (charDef.MaxFood > 0)
            {
                ch.Food = charDef.MaxFood;
                ch.SetTag("MAXFOOD", charDef.MaxFood.ToString());
            }

            if (charDef.DamPhysical != 0) ch.DamPhysical = charDef.DamPhysical;
            else if (charDef.DamFire != 0 || charDef.DamCold != 0 || charDef.DamPoison != 0 || charDef.DamEnergy != 0)
                // Source-X OnTakeDamage: an unset DAMPHYSICAL is the remainder the
                // elemental percents leave of 100 (same rule as the packet-helper
                // NPC path), NOT the 100 default a pure-physical char keeps.
                ch.DamPhysical = (short)Math.Max(0, 100 - charDef.DamFire - charDef.DamCold - charDef.DamPoison - charDef.DamEnergy);
            if (charDef.DamFire != 0) ch.DamFire = charDef.DamFire;
            if (charDef.DamCold != 0) ch.DamCold = charDef.DamCold;
            if (charDef.DamPoison != 0) ch.DamPoison = charDef.DamPoison;
            if (charDef.DamEnergy != 0) ch.DamEnergy = charDef.DamEnergy;

            CharDefHelper.ApplyNpcDefinitionSkills(ch, charDef);
            CharDefHelper.ApplyNpcDefinitionTags(ch, charDef);
        }
        else
        {
            ch.Name = $"Spawn_{bodyId:X}";
            ch.OBody = bodyId;
            ch.Str = 50; ch.Dex = 50; ch.Int = 20;
            ch.MaxHits = 50; ch.MaxMana = 20; ch.MaxStam = 50;
            ch.Hits = 50; ch.Mana = 20; ch.Stam = 50;
        }

        if (ch.NpcBrain == NpcBrainType.None)
            ch.NpcBrain = NpcBrainType.Monster;

        ch.SetStatFlag(StatFlag.Spawned);

        ch.Home = new Point3D(_spawnItem.X, _spawnItem.Y, _spawnItem.Z, _spawnItem.MapIndex);
        ch.HomeDist = (short)_spawnRange;
        ch.SetTag("SPAWNITEM", $"0{_spawnItem.Uid.Value:x8}");

        // @Spawn — script can modify NPC, set its position, or abort
        // (return TRUE → delete NPC). Capture the position first so we can tell
        // whether the script chose an explicit spawn point.
        Point3D posBefore = ch.Position;
        if (OnSpawnTrigger != null)
        {
            var spawnArgs = new SpawnTriggerArgs { SpawnedChar = ch };
            var result = OnSpawnTrigger(_spawnItem, ItemTrigger.Spawn, spawnArgs);
            if (result == TriggerResult.True)
            {
                _world.DeleteObject(ch);
                ch.Delete();
                return;
            }
        }

        // Source-X CCSpawn: if @Spawn gave the NPC a valid point, keep it; only
        // pick a random position when the script did not place it explicitly.
        bool scriptPlaced = (ch.Position.X != posBefore.X || ch.Position.Y != posBefore.Y
            || ch.Position.Z != posBefore.Z || ch.Position.Map != posBefore.Map)
            && (ch.Position.X != 0 || ch.Position.Y != 0);
        Point3D pos = scriptPlaced ? ch.Position : FindSpawnPosition(charDef);
        ch.SetTag("SPAWN_POINT_UUID", _spawnItem.Uuid.ToString("D"));
        if (!_world.PlaceCharacter(ch, pos))
        {
            // Placement refused (out of bounds) — delete instead of leaving an
            // orphan NPC with no sector (Source-X deletes on MoveNear/MoveTo fail).
            _world.DeleteObject(ch);
            ch.Delete();
            return;
        }
        _spawnedUids.Add(ch.Uid);

        // @AddObj — notify script that NPC was registered
        OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.AddObj, new SpawnTriggerArgs { SpawnedChar = ch });

        _world.OnNpcSpawned?.Invoke(ch);
    }

    private Point3D FindSpawnPosition(CharDef? charDef)
    {
        var mapData = _world.MapData;
        bool canSwim = charDef != null && (charDef.Can & CanFlags.C_Swim) != 0;

        for (int attempt = 0; attempt < 25; attempt++)
        {
            short dx = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
            short dy = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
            short px = (short)(_spawnItem.X + dx);
            short py = (short)(_spawnItem.Y + dy);
            sbyte pz = _spawnItem.Z;

            if (mapData != null)
            {
                pz = mapData.GetEffectiveZ(_spawnItem.MapIndex, px, py, _spawnItem.Z);
                if (!mapData.IsPassable(_spawnItem.MapIndex, px, py, pz))
                    continue;
                var terrain = mapData.GetTerrainTile(_spawnItem.MapIndex, px, py);
                var landData = mapData.GetLandTileData(terrain.TileId);
                if (landData.IsWet && !canSwim)
                    continue;
            }

            return new Point3D(px, py, pz, _spawnItem.MapIndex);
        }

        return new Point3D(_spawnItem.X, _spawnItem.Y, _spawnItem.Z, _spawnItem.MapIndex);
    }

    private int RandomRange(int min, int max)
    {
        if (max <= 0) return Math.Max(1, min);
        if (min >= max) return Math.Max(1, min);
        return _rand.Next(min, max + 1);
    }

    public void CleanupDead()
    {
        if (_killingChildren) return;
        _spawnedUids.RemoveAll(uid =>
        {
            var ch = _world.FindChar(uid);
            if (ch == null || ch.IsDeleted || ch.IsDead)
            {
                if (ch != null)
                    FireDelObj(ch);
                return true;
            }
            return false;
        });
    }

    private void FireDelObj(Character ch)
    {
        if (_killingChildren) return;
        ch.ClearStatFlag(StatFlag.Spawned);
        OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.DelObj, new SpawnTriggerArgs { SpawnedChar = ch });
    }

    private void SetNextSpawnTime()
    {
        int delaySec = _rand.Next(_minDelaySec, _maxDelaySec + 1);
        _nextSpawnTick = Environment.TickCount64 + delaySec * 1000L;
        _spawnItem.SetTimeout(_nextSpawnTick);
    }

    private void PauseTimer()
    {
        _nextSpawnTick = -1;
        _spawnItem.SetTimeout(-1);
    }

    /// <summary>Remove all spawned creatures from the world.</summary>
    public void KillAll()
    {
        _killingChildren = true;
        foreach (var uid in _spawnedUids)
        {
            var ch = _world.FindChar(uid);
            if (ch == null || ch.IsDeleted) continue;
            ch.ClearStatFlag(StatFlag.Spawned);
            OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.DelObj,
                new SpawnTriggerArgs { SpawnedChar = ch, SpawnDefIndex = ch.CharDefIndex });
            if (!ch.IsDead)
                ch.Kill();
            _world.DeleteObject(ch);
            ch.Delete();
        }
        _spawnedUids.Clear();
        _killingChildren = false;
    }

    /// <summary>Remove a specific spawned NPC by UID (DELOBJ verb).</summary>
    public void DelObj(Serial uid)
    {
        var ch = _world.FindChar(uid);
        if (ch != null)
        {
            FireDelObj(ch);
            if (!ch.IsDead) ch.Kill();
            _world.DeleteObject(ch);
            ch.Delete();
        }
        _spawnedUids.Remove(uid);

        if (_spawnedUids.Count < _maxCount && _nextSpawnTick < 0 && !_stopped)
            SetNextSpawnTime();
    }

    /// <summary>Source-X RESET verb: kill all + immediate respawn.</summary>
    public void Reset()
    {
        KillAll();
        _stopped = false;
        ForceSpawn();
    }

    /// <summary>World-level RESPAWN: top this spawner straight up to its max now,
    /// independent of sector sleep (admin/console/IPC RESPAWN command).</summary>
    public void RespawnNow()
    {
        if (_stopped) return;
        CleanupDead();
        if (_charDefId == 0 && _spawnGroup == null) return;
        int guard = 0;
        while (_spawnedUids.Count < _maxCount && guard++ < _maxCount + 8)
            SpawnOne();
        if (_spawnedUids.Count >= _maxCount)
            PauseTimer();
        else
            SetNextSpawnTime();
    }

    /// <summary>Source-X START verb: resume spawning.</summary>
    public void Start()
    {
        _stopped = false;
        ForceSpawn();
    }

    /// <summary>Source-X STOP verb: kill all + disable timer permanently.</summary>
    public void Stop()
    {
        KillAll();
        _stopped = true;
        PauseTimer();
    }

    /// <summary>
    /// Resolve MORE1 value as either a spawn group defname or a single chardef ID.
    /// Called during item initialization/load.
    /// </summary>
    public void SetFromMore1(uint more1, ResourceHolder resources)
    {
        _resources = resources;

        foreach (var res in resources.GetAllResources())
        {
            if (res.Id.Type == ResType.Spawn && res is SpawnGroupDef sgd)
            {
                if (!string.IsNullOrEmpty(sgd.DefName))
                {
                    var spawnRid = resources.ResolveDefName(sgd.DefName);
                    if (spawnRid.IsValid && (uint)spawnRid.Index == more1)
                    {
                        _spawnGroup = sgd;
                        return;
                    }
                }
            }
        }

        _charDefId = (ushort)(more1 & 0xFFFF);
    }

    /// <summary>
    /// Get the spawn definition name (group defname or chardef hex).
    /// </summary>
    public string GetSpawnDefName()
    {
        if (_spawnGroup != null && !string.IsNullOrEmpty(_spawnGroup.DefName))
            return _spawnGroup.DefName;
        if (_charDefId > 0)
        {
            var cdef = DefinitionLoader.GetCharDef(_charDefId);
            if (cdef != null && !string.IsNullOrEmpty(cdef.DefName))
                return cdef.DefName;
            return $"0{_charDefId:X}";
        }
        return "";
    }

    /// <summary>
    /// Resolve a Sphere SPAWNID defname (e.g. "spawn_Mages", "c_horse")
    /// as either a spawn group or a single chardef.
    /// </summary>
    public void SetFromDefName(string spawnId, ResourceHolder resources)
    {
        _resources = resources;

        var rid = resources.ResolveDefName(spawnId);
        if (rid.IsValid)
        {
            if (rid.Type == ResType.Spawn)
            {
                var sgd = resources.GetResource(rid) as SpawnGroupDef;
                if (sgd != null)
                {
                    _spawnGroup = sgd;
                    return;
                }
            }
            if (rid.Type == ResType.CharDef)
            {
                _charDefId = (ushort)Math.Clamp(rid.Index, 0, ushort.MaxValue);
                _spawnItem.More1 = _charDefId;
                return;
            }
        }

        if (uint.TryParse(spawnId, System.Globalization.NumberStyles.HexNumber, null, out uint raw))
        {
            _charDefId = (ushort)(raw & 0xFFFF);
            _spawnItem.More1 = _charDefId;
        }
    }

    public void SetDelay(int minMinutes, int maxMinutes)
    {
        _minDelaySec = Math.Max(1, minMinutes) * 60;
        _maxDelaySec = Math.Max(_minDelaySec, maxMinutes * 60);
        SyncMorePToItem();
    }

    private void SyncMorePToItem()
    {
        int minMin = _minDelaySec / 60;
        int maxMin = _maxDelaySec / 60;
        var mp = _spawnItem.MoreP;
        _spawnItem.MoreP = new Core.Types.Point3D((short)minMin, (short)maxMin, (sbyte)Math.Clamp(_spawnRange, 0, 127), mp.Map);
    }

    public void RegisterExisting(Serial uid)
    {
        if (!_spawnedUids.Contains(uid))
            _spawnedUids.Add(uid);
    }

    /// <summary>Force an immediate spawn tick (for SPAWNRESET).</summary>
    public void ForceSpawn()
    {
        _nextSpawnTick = 0;
    }

    /// <summary>
    /// Read spawn timing from item's MOREP (Source-X parity).
    /// MOREP.X = min spawn time (minutes), MOREP.Y = max spawn time (minutes),
    /// MOREP.Z = home distance (tiles).
    /// </summary>
    public void ApplyMoreP()
    {
        var mp = _spawnItem.MoreP;
        if (mp.X > 0 || mp.Y > 0)
        {
            int minMin = Math.Max(1, (int)mp.X);
            int maxMin = Math.Max(minMin, mp.Y > 0 ? (int)mp.Y : minMin);
            SetDelay(minMin, maxMin);
        }
        if (mp.Z > 0)
            _spawnRange = mp.Z;
    }

    /// <summary>Reset the spawn timer using current delay values.</summary>
    public void ResetTimer(long preservedTimeoutMs = 0)
    {
        if (_spawnedUids.Count >= _maxCount)
            PauseTimer();
        else if (preservedTimeoutMs > Environment.TickCount64)
        {
            _nextSpawnTick = preservedTimeoutMs;
            _spawnItem.SetTimeout(_nextSpawnTick);
        }
        else
            SetNextSpawnTime();
    }

    /// <summary>Check if any spawned NPCs are still alive.</summary>
    public bool HasAliveSpawns()
    {
        CleanupDead();
        return _spawnedUids.Count > 0;
    }

    /// <summary>Access a spawned object by index (Source-X spawn.AT(n)).</summary>
    public Character? GetSpawnedAt(int index)
    {
        if (index < 0 || index >= _spawnedUids.Count) return null;
        return _world.FindChar(_spawnedUids[index]);
    }
}

/// <summary>Trigger args specific to spawn events.</summary>
public sealed class SpawnTriggerArgs
{
    public Character? SpawnedChar { get; set; }
    public Item? SpawnedItem { get; set; }
    public int SpawnDefIndex { get; set; }
}

/// <summary>
/// Item spawn component for IT_SPAWN_ITEM items.
/// Periodically creates items within range.
/// </summary>
public sealed class ItemSpawnComponent
{
    private readonly Item _spawnItem;
    private readonly GameWorld _world;
    private readonly List<Serial> _spawnedUids = [];
    private readonly Random _rand = new();

    private ushort _itemDefId;
    private int _maxCount = 1;
    private int _spawnRange = 2;
    private int _pile = 1;
    private long _nextSpawnTick;
    private int _minDelaySec = 60;
    private int _maxDelaySec = 300;

    private const int MaxSpawnLimit = 250;

    public ushort ItemDefId { get => _itemDefId; set => _itemDefId = value; }
    public int CurrentCount
    {
        get
        {
            CleanupDeleted();
            return _spawnedUids.Count;
        }
    }
    public int MaxCount
    {
        get => _maxCount;
        set
        {
            _maxCount = Math.Clamp(value, 1, MaxSpawnLimit);
            _spawnItem.Amount = (ushort)_maxCount;
        }
    }
    /// <summary>Source-X PILE: max items per spawn interval for stackable items.</summary>
    public int Pile { get => _pile; set => _pile = Math.Max(1, value); }

    /// <summary>Source-X MAXDIST: max scatter distance from the spawn point.</summary>
    public int SpawnRange { get => _spawnRange; set => _spawnRange = Math.Max(0, value); }

    /// <summary>Source-X TIMELO/TIMEHI: respawn interval in minutes, converted to
    /// the seconds the tick scheduler uses (parity with the char spawner).</summary>
    public void SetDelay(int minMinutes, int maxMinutes)
    {
        _minDelaySec = Math.Max(1, minMinutes) * 60;
        _maxDelaySec = Math.Max(_minDelaySec, Math.Max(minMinutes, maxMinutes) * 60);
    }

    public ItemSpawnComponent(Item spawnItem, GameWorld world)
    {
        _spawnItem = spawnItem;
        _world = world;
    }

    public void OnTick(long currentTick)
    {
        CleanupDeleted();

        if (currentTick < _nextSpawnTick) return;
        if (_spawnedUids.Count >= _maxCount) return;
        if (_itemDefId == 0) return;

        SpawnOneItem();
    }

    /// <summary>World-level RESPAWN: top this item spawner up to its max now,
    /// independent of sector sleep (admin/console/IPC RESPAWN command).</summary>
    public void RespawnNow()
    {
        CleanupDeleted();
        if (_itemDefId == 0) return;
        int guard = 0;
        while (_spawnedUids.Count < _maxCount && guard++ < _maxCount + 8)
            SpawnOneItem();
    }

    private void SpawnOneItem()
    {
        int defIndex = _itemDefId;
        if (SpawnComponent.OnSpawnTrigger != null)
        {
            var preArgs = new SpawnTriggerArgs { SpawnDefIndex = defIndex };
            if (SpawnComponent.OnSpawnTrigger(_spawnItem, ItemTrigger.PreSpawn, preArgs) == TriggerResult.True)
            {
                SetNextSpawnTime();
                return;
            }
            defIndex = preArgs.SpawnDefIndex;
        }

        if (defIndex <= 0 || defIndex > ushort.MaxValue)
        {
            SetNextSpawnTime();
            return;
        }

        var item = _world.CreateItem();
        var idef = DefinitionLoader.GetItemDef(defIndex);
        if (!ItemDefHelper.ApplyInstanceMetadata(item, defIndex))
        {
            if (defIndex is <= 0 or > ushort.MaxValue)
            {
                _world.RemoveItem(item);
                SetNextSpawnTime();
                return;
            }
            item.BaseId = (ushort)defIndex;
        }
        if (idef != null && !string.IsNullOrEmpty(idef.Name))
            item.Name = idef.Name;
        else
            item.Name = $"Spawned_{defIndex:X}";

        if (_pile > 1)
            item.Amount = (ushort)Math.Max(1, _rand.Next(1, _pile + 1));

        short dx = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        short dy = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        var pos = new Point3D(
            (short)(_spawnItem.X + dx),
            (short)(_spawnItem.Y + dy),
            _spawnItem.Z,
            _spawnItem.MapIndex
        );

        // Reschedule regardless of placement outcome so a spawner that keeps
        // rolling out-of-bounds points doesn't retry every tick. Interval honors
        // the TIMELO/TIMEHI override (defaults to the 60-300s legacy window).
        SetNextSpawnTime();

        item.SetTag("SPAWN_POINT_UUID", _spawnItem.Uuid.ToString("D"));
        if (SpawnComponent.OnSpawnTrigger != null)
        {
            var spawnArgs = new SpawnTriggerArgs { SpawnedItem = item, SpawnDefIndex = defIndex };
            if (SpawnComponent.OnSpawnTrigger(_spawnItem, ItemTrigger.Spawn, spawnArgs) == TriggerResult.True)
            {
                _world.DeleteObject(item);
                item.Delete();
                return;
            }
        }
        if (!_world.PlaceItem(item, pos))
        {
            // Out-of-bounds placement — delete instead of leaving an orphan item
            // with no sector (Source-X deletes on placement failure).
            _world.DeleteObject(item);
            item.Delete();
            return;
        }
        _spawnedUids.Add(item.Uid);
        SpawnComponent.OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.AddObj,
            new SpawnTriggerArgs { SpawnedItem = item, SpawnDefIndex = defIndex });
    }

    public void ForceSpawn() => _nextSpawnTick = 0;

    /// <summary>Detach a spawned item without deleting it (Source-X DelObj).</summary>
    public void DelObj(Serial uid)
    {
        if (!_spawnedUids.Remove(uid)) return;
        var item = _world.FindItem(uid);
        SpawnComponent.OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.DelObj,
            new SpawnTriggerArgs { SpawnedItem = item, SpawnDefIndex = _itemDefId });
        if (item != null)
            item.RemoveTag("SPAWN_POINT_UUID");
        if (_nextSpawnTick <= 0)
            SetNextSpawnTime();
    }

    public void KillAll()
    {
        foreach (var uid in _spawnedUids.ToArray())
        {
            var item = _world.FindItem(uid);
            SpawnComponent.OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.DelObj,
                new SpawnTriggerArgs { SpawnedItem = item, SpawnDefIndex = _itemDefId });
            if (item == null || item.IsDeleted) continue;
            item.RemoveTag("SPAWN_POINT_UUID");
            _world.DeleteObject(item);
            item.Delete();
        }
        _spawnedUids.Clear();
    }

    public void ResetTimer(long preservedTimeoutMs = 0)
    {
        if (preservedTimeoutMs > Environment.TickCount64)
        {
            _nextSpawnTick = preservedTimeoutMs;
            _spawnItem.SetTimeout(_nextSpawnTick);
            return;
        }

        _nextSpawnTick = Environment.TickCount64 + _rand.Next(5, 30) * 1000;
        _spawnItem.SetTimeout(_nextSpawnTick);
    }

    private void SetNextSpawnTime()
    {
        _nextSpawnTick = Environment.TickCount64 + _rand.Next(_minDelaySec, _maxDelaySec + 1) * 1000;
        _spawnItem.SetTimeout(_nextSpawnTick);
    }

    private void CleanupDeleted()
    {
        _spawnedUids.RemoveAll(uid =>
        {
            var item = _world.FindItem(uid);
            bool deleted = item == null || item.IsDeleted;
            if (deleted)
                SpawnComponent.OnSpawnTrigger?.Invoke(_spawnItem, ItemTrigger.DelObj,
                    new SpawnTriggerArgs { SpawnedItem = item, SpawnDefIndex = _itemDefId });
            return deleted;
        });
    }
}
