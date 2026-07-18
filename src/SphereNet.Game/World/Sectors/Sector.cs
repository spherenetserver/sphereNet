using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.World.Sectors;

/// <summary>
/// A geographic sector of the world. Maps to CSector in Source-X.
/// World is divided into sectors (default 64x64 tiles each).
/// Each sector tracks its items, characters, and timed objects.
/// Implements IScriptObj for Sphere script property access.
/// </summary>
public sealed class Sector : IScriptObj
{
    public const int SectorSize = 64;

    private readonly int _x, _y, _cols;
    private readonly byte _mapIndex;
    private readonly List<Character> _characters = [];
    private readonly List<Character> _onlinePlayers = [];
    private readonly List<Item> _items = [];

    // Weather/environment per-sector (Source-X CSector)
    private byte _weather;      // 0=dry, 1=rain, 2=snow
    private byte _season;       // 0=spring, 1=summer, 2=fall, 3=winter, 4=desolation
    private byte _light = 0;    // 0=bright, 30=dark (0 = use global)
    private short _rainChance = 15;
    private short _coldChance = 5;
    private bool _isSleeping;
    private SectorFlag _flags;

    /// <summary>Milliseconds a sector must sit clientless before it may sleep
    /// (Source-X g_Cfg._iSectorSleepDelay, default 10 minutes). 0 disables
    /// sleeping entirely.</summary>
    public static long SleepDelayMs { get; set; } = 10L * 60 * 1000;

    private static readonly byte[] TrammelPhaseBrightness = [0, 0, 1, 1, 2, 1, 1, 0];
    private static readonly byte[] FeluccaPhaseBrightness = [0, 1, 3, 4, 6, 4, 3, 1];

    private Dictionary<int, (int Remaining, long RegenTick)>? _resourcePools;

    public int SectorX => _x;
    public int SectorY => _y;
    public byte MapIndex => _mapIndex;
    public int Number => _y * _cols + _x; // sector index = row * (map columns) + column

    public IReadOnlyList<Character> Characters => _characters;
    public IReadOnlyList<Character> OnlinePlayers => _onlinePlayers;
    public IReadOnlyList<Item> Items => _items;

    public int CharacterCount => _characters.Count;
    public int ItemCount => _items.Count;
    public int ClientCount => _characters.Count(c => c.IsPlayer && c.IsOnline);
    public bool IsEmpty => _characters.Count == 0 && _items.Count == 0;

    public byte Weather { get => _weather; set => _weather = value; }
    public byte Season { get => _season; set => _season = value; }
    public byte Light { get => _light; set => _light = value; }
    public short RainChance { get => _rainChance; set => _rainChance = value; }
    public short ColdChance { get => _coldChance; set => _coldChance = value; }
    public bool IsSleeping { get => _isSleeping; set => _isSleeping = value; }

    /// <summary>Sector behaviour flags (Source-X SECF_*). Setting the NoSleep
    /// bit notifies the host so an always-awake sector stays in the tick set.</summary>
    public SectorFlag Flags
    {
        get => _flags;
        set
        {
            bool wasNoSleep = _flags.HasFlag(SectorFlag.NoSleep);
            _flags = value;
            bool isNoSleep = _flags.HasFlag(SectorFlag.NoSleep);
            if (wasNoSleep != isNoSleep)
                OnNoSleepChanged?.Invoke(this, isNoSleep);
        }
    }

    /// <summary>Last time (ms) a client was present in this sector — the sleep
    /// timeout is measured from here (Source-X GetLastClientTime).</summary>
    public long LastClientTimeMs { get; set; }

    /// <summary>Stamp the last-client time (method form so callers can use it
    /// through a null-conditional sector reference).</summary>
    public void SetLastClientTime(long nowMs) => LastClientTimeMs = nowMs;

    /// <summary>Host bridge: resolve an adjacent sector by absolute sector
    /// coordinates (Source-X CSector::_GetAdjacentSector), or null off-map.</summary>
    public Func<int, int, Sector?>? GetAdjacentSector { get; set; }

    /// <summary>Host bridge fired when the NoSleep flag toggles so the world can
    /// keep an always-awake sector in its tick set.</summary>
    public Action<Sector, bool>? OnNoSleepChanged { get; set; }

    /// <summary>Callback for world time queries (WorldHour, WorldMinute).</summary>
    public Func<(int Hour, int Minute)>? GetWorldTime { get; set; }
    /// <summary>Full game-world minute counter used for local time and moon phases.</summary>
    public Func<long>? GetWorldMinutes { get; set; }
    /// <summary>Configured surface/dungeon light targets: day, night, dungeon.</summary>
    public Func<(int Day, int Night, int Dungeon)>? GetLightSettings { get; set; }
    /// <summary>Whether this sector's representative point is underground.</summary>
    public Func<bool>? IsDungeon { get; set; }
    /// <summary>Host bridge for global-light packets sent by LightFlash.</summary>
    public Action<Character, byte>? SendLight { get; set; }

    public Sector(int x, int y, byte mapIndex, int cols)
    {
        _x = x;
        _y = y;
        _mapIndex = mapIndex;
        _cols = Math.Max(1, cols); // map sector columns (width / SectorSize)
    }

    public void AddCharacter(Character ch)
    {
        if (!_characters.Contains(ch))
            _characters.Add(ch);
    }

    public void RemoveCharacter(Character ch) => _characters.Remove(ch);

    public void AddOnlinePlayer(Character ch)
    {
        if (!_onlinePlayers.Contains(ch))
            _onlinePlayers.Add(ch);
    }

    public void RemoveOnlinePlayer(Character ch) => _onlinePlayers.Remove(ch);

    // Source-X CSector::m_ListenItems: count of ground items in this sector that
    // can hear speech, maintained on add/remove/type-change so the per-utterance
    // speech path can skip the item scan entirely when nothing here listens
    // (CClientEvent.cpp:1883 `if (pSector->HasListenItems())`). Source-X counts
    // only comm crystals (multis hear via the speaker's region); SphereNet routes
    // multi/ship speech through the same ground scan, so multis count too.
    private int _listenItems;

    public bool HasListenItems => _listenItems > 0;

    internal static bool IsListenItemType(SphereNet.Core.Enums.ItemType type) => type is
        SphereNet.Core.Enums.ItemType.CommCrystal or
        SphereNet.Core.Enums.ItemType.Multi or
        SphereNet.Core.Enums.ItemType.MultiCustom or
        SphereNet.Core.Enums.ItemType.Ship;

    public void AddItem(Item item)
    {
        if (_items.Contains(item))
            return;
        _items.Add(item);
        if (IsListenItemType(item.ItemType))
            _listenItems++;
    }

    public void RemoveItem(Item item)
    {
        if (_items.Remove(item) && _listenItems > 0 && IsListenItemType(item.ItemType))
            _listenItems--;
    }

    /// <summary>A ground item in this sector changed its effective TYPE (script
    /// SetType). Rebalances the listen count; ignores items not actually in this
    /// sector (a type set before placement must not touch any counter).</summary>
    internal void OnItemTypeChanged(Item item, SphereNet.Core.Enums.ItemType oldType,
        SphereNet.Core.Enums.ItemType newType)
    {
        bool was = IsListenItemType(oldType), now = IsListenItemType(newType);
        if (was == now || !_items.Contains(item))
            return;
        if (now) _listenItems++;
        else if (_listenItems > 0) _listenItems--;
    }

    /// <summary>Source-X CSector::GetLocalTime. A complete 24-hour offset is
    /// distributed across the map's sector columns.</summary>
    public int GetLocalTime()
    {
        var fallback = GetWorldTime?.Invoke() ?? (12, 0);
        long worldMinutes = GetWorldMinutes?.Invoke() ?? fallback.Item1 * 60L + fallback.Item2;
        long local = worldMinutes + (long)_x * 24 * 60 / _cols;
        int result = (int)(local % (24 * 60));
        return result < 0 ? result + 24 * 60 : result;
    }

    /// <summary>Source-X CWorldGameTime::GetMoonPhase.</summary>
    public static int GetMoonPhase(long worldMinutes, bool felucca)
    {
        int period = felucca ? 840 : 105;
        long cycle = worldMinutes % period;
        if (cycle < 0) cycle += period;
        return (int)(cycle * 8 / period);
    }

    /// <summary>Source-X CSector::IsMoonVisible moonrise/moonset table.</summary>
    public static bool IsMoonVisible(int phase, int localTime)
    {
        localTime %= 24 * 60;
        if (localTime < 0) localTime += 24 * 60;
        return phase switch
        {
            0 => localTime > 360 && localTime < 1080,
            1 => localTime > 540 && localTime < 1270,
            2 => localTime > 720,
            3 => localTime < 180 || localTime > 900,
            4 => localTime < 360 || localTime > 1080,
            5 => localTime < 540 || localTime > 1270,
            6 => localTime < 720,
            7 => localTime > 180 && localTime < 900,
            _ => false,
        };
    }

    /// <summary>Source-X CSector::GetLightCalc including local time, clouds and
    /// the Trammel/Felucca moon brightness tables.</summary>
    public byte GetLightCalc(bool quickSet = true, bool? dungeonOverride = null)
    {
        var settings = GetLightSettings?.Invoke() ?? (0, 25, 27);
        if ((dungeonOverride ?? IsDungeon?.Invoke()) == true)
            return (byte)Math.Clamp(settings.Item3, 0, 30);

        int localTime = GetLocalTime();
        int hour = localTime / 60;
        bool night = hour < 6 || hour > 20;
        int target = Math.Clamp(night ? settings.Item2 : settings.Item1, 0, 30);

        if (_weather != 0)
            target = Math.Min(30, target + (night ? Random.Shared.Next(1, 3) : Random.Shared.Next(1, 5)));

        if (night)
        {
            long worldMinutes = GetWorldMinutes?.Invoke() ?? localTime;
            int trammel = GetMoonPhase(worldMinutes, felucca: false);
            if (IsMoonVisible(trammel, localTime))
                target = Math.Max(0, target - TrammelPhaseBrightness[trammel]);

            int felucca = GetMoonPhase(worldMinutes, felucca: true);
            if (IsMoonVisible(felucca, localTime))
                target = Math.Max(0, target - FeluccaPhaseBrightness[felucca]);
        }

        if (quickSet || _light == target)
            return (byte)target;
        return _light > target ? (byte)Math.Max(0, _light - 1) : (byte)Math.Min(30, _light + 1);
    }

    /// <summary>Advance the stored sector light one Source-X transition step.</summary>
    public bool RefreshLight()
    {
        byte next = GetLightCalc(quickSet: false);
        if (next == _light) return false;
        _light = next;
        return true;
    }

    /// <summary>Source-X CSector::LightFlash: briefly send full brightness and
    /// then restore calculated sector light for active living players without
    /// Night Sight.</summary>
    public void LightFlash()
    {
        byte normal = GetLightCalc();
        foreach (var character in _characters)
        {
            if (!character.IsPlayer || !character.IsOnline || character.IsDead ||
                character.IsStatFlag(StatFlag.NightSight))
                continue;
            SendLight?.Invoke(character, 0);
            SendLight?.Invoke(character, normal);
        }
    }

    public int GetResourceAmount(int resDefIndex, int amountMax, int regenSeconds)
    {
        _resourcePools ??= [];
        long now = Environment.TickCount64;

        if (!_resourcePools.TryGetValue(resDefIndex, out var pool))
        {
            _resourcePools[resDefIndex] = (amountMax, now);
            return amountMax;
        }

        if (regenSeconds > 0 && pool.Remaining <= 0)
        {
            long regenMs = regenSeconds * 1000L;
            if (now - pool.RegenTick >= regenMs)
            {
                _resourcePools[resDefIndex] = (amountMax, now);
                return amountMax;
            }
        }

        return pool.Remaining;
    }

    public void ConsumeResource(int resDefIndex, int amount)
    {
        if (_resourcePools == null || !_resourcePools.TryGetValue(resDefIndex, out var pool))
            return;
        int newRemaining = Math.Max(0, pool.Remaining - amount);
        long regenTick = newRemaining <= 0 ? Environment.TickCount64 : pool.RegenTick;
        _resourcePools[resDefIndex] = (newRemaining, regenTick);
    }

    // 8-neighbour offsets (Source-X DIR_QTY sweep order is irrelevant here).
    private static readonly (int Dx, int Dy)[] AdjacentOffsets =
        [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    /// <summary>
    /// Source-X CSector::_CanSleep. A sector may sleep only when sleeping is
    /// enabled and not vetoed by SECF_NoSleep, no client is inside, and either
    /// SECF_InstaSleep is set or (optionally) every adjacent sector could also
    /// sleep and the clientless timeout has elapsed. The adjacency sweep keeps
    /// the ring around an active sector awake so a player never walks into a
    /// cold sector.
    /// </summary>
    public bool CanSleep(long nowMs, bool checkAdjacents = true)
    {
        if (SleepDelayMs == 0 || _flags.HasFlag(SectorFlag.NoSleep))
            return false;
        if (ClientCount > 0)
            return false;
        if (_flags.HasFlag(SectorFlag.InstaSleep))
            return true;

        if (checkAdjacents && GetAdjacentSector != null)
        {
            foreach (var (dx, dy) in AdjacentOffsets)
            {
                var adjacent = GetAdjacentSector(_x + dx, _y + dy);
                // Non-recursive check on neighbours (fCheckAdjacents = false)
                // so the sweep can't loop back through this sector.
                if (adjacent != null && !adjacent.CanSleep(nowMs, checkAdjacents: false))
                    return false;
            }
        }

        return nowMs - LastClientTimeMs > SleepDelayMs;
    }

    /// <summary>Get all objects within a range from a point inside this sector.
    /// Safe for concurrent reads when no writes are in progress (multicore compute phase).</summary>
    public IEnumerable<ObjBase> GetObjectsInRange(Point3D center, int range)
    {
        // Index-based iteration avoids ToArray allocation. Safe during the
        // parallel compute phase because sector mutations only happen in
        // the sequential tick/apply phases.
        for (int i = _characters.Count - 1; i >= 0; i--)
        {
            if (i >= _characters.Count) continue;
            var ch = _characters[i];
            if (!ch.IsDeleted && center.GetDistanceTo(ch.Position) <= range)
                yield return ch;
        }
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (i >= _items.Count) continue;
            var item = _items[i];
            if (!item.IsDeleted && item.IsOnGround && center.GetDistanceTo(item.Position) <= range)
                yield return item;
        }
    }

    /// <summary>
    /// Tick all objects in this sector. Characters always tick for regen.
    /// <para>
    /// <b>THREAD-SAFETY:</b> When called from <c>GameWorld.OnTickParallel</c>, multiple
    /// sectors may tick concurrently. <c>Character.OnTick()</c> and <c>Item.OnTick()</c>
    /// MUST NOT call <c>GameWorld.MoveCharacter</c> or any method that modifies sector
    /// lists (<c>_characters</c>, <c>_items</c>). Cross-sector mutations must be deferred
    /// to sequential phases.
    /// </para>
    /// </summary>
    /// <param name="currentTime">Current tick timestamp (currently unused, reserved for future use).</param>
    public void OnTick(long currentTime)
    {
        for (int i = _characters.Count - 1; i >= 0; i--)
        {
            var ch = _characters[i];
            if (ch.IsDeleted) { _characters.RemoveAt(i); continue; }
            if (!ch.IsSleeping)
                ch.OnTick();
        }

        TickItems();
    }

    /// <summary>
    /// Lightweight maintenance tick for sleeping sectors.
    /// Only processes item timers (decay, spawn, TIMER) — no character AI.
    /// Called periodically by GameWorld to keep timers alive in empty areas.
    /// </summary>
    public void OnMaintenanceTick()
    {
        TickItems();
    }

    private void TickItems()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.IsDeleted) { _items.RemoveAt(i); continue; }
            if (!item.IsSleeping)
            {
                if (!item.OnTick())
                    _items.RemoveAt(i);
            }
        }
    }

    // ==================== IScriptObj Implementation ====================

    public string GetName() => $"Sector({_x},{_y},{_mapIndex})";

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "NUMBER":
                value = Number.ToString();
                return true;
            case "CLIENTS":
                value = ClientCount.ToString();
                return true;
            case "COMPLEXITY":
                value = CharacterCount.ToString();
                return true;
            case "COMPLEXITY.HIGH":
                value = CharacterCount < 5 ? "1" : "0";
                return true;
            case "COMPLEXITY.MEDIUM":
                value = CharacterCount < 10 ? "1" : "0";
                return true;
            case "COMPLEXITY.LOW":
                value = CharacterCount >= 10 ? "1" : "0";
                return true;
            case "ITEMCOUNT":
                value = ItemCount.ToString();
                return true;
            case "WEATHER":
                value = _weather.ToString();
                return true;
            case "SEASON":
                value = _season.ToString();
                return true;
            case "LIGHT":
                value = _light.ToString();
                return true;
            case "RAINCHANCE":
                value = _rainChance.ToString();
                return true;
            case "COLDCHANCE":
                value = _coldChance.ToString();
                return true;
            case "ISSLEEPING":
                value = _isSleeping ? "1" : "0";
                return true;
            case "CANSLEEP":
                value = CanSleep(Environment.TickCount64) ? "1" : "0";
                return true;
            case "FLAGS":
                value = ((uint)_flags).ToString();
                return true;
            case "NOSLEEP":
                value = _flags.HasFlag(SectorFlag.NoSleep) ? "1" : "0";
                return true;
            case "INSTASLEEP":
                value = _flags.HasFlag(SectorFlag.InstaSleep) ? "1" : "0";
                return true;
            case "ISDARK":
            {
                value = (GetLightCalc() > 6) ? "1" : "0";
                return true;
            }
            case "ISNIGHTTIME":
            {
                int localTime = GetLocalTime();
                value = (localTime < 7 * 60 || localTime > 21 * 60) ? "1" : "0";
                return true;
            }
            case "LOCALTIME":
            {
                int localTime = GetLocalTime();
                int hour = localTime / 60;
                int minute = localTime % 60;
                string period = hour switch
                {
                    >= 5 and < 7 => "dawn",
                    >= 7 and < 12 => "morning",
                    12 => "noon",
                    >= 13 and < 17 => "afternoon",
                    >= 17 and < 20 => "evening",
                    >= 20 and < 22 => "dusk",
                    _ => "night"
                };
                value = $"{hour:D2}:{minute:D2} ({period})";
                return true;
            }
            case "LOCALTOD":
            {
                value = GetLocalTime().ToString();
                return true;
            }
            default:
                return false;
        }
    }

    public bool TrySetProperty(string key, string val)
    {
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "WEATHER":
                if (byte.TryParse(val, out byte w)) { _weather = w; return true; }
                return false;
            case "SEASON":
                if (byte.TryParse(val, out byte s)) { _season = s; return true; }
                return false;
            case "LIGHT":
                if (byte.TryParse(val, out byte l)) { _light = l; return true; }
                return false;
            case "RAINCHANCE":
                if (short.TryParse(val, out short rc)) { _rainChance = rc; return true; }
                return false;
            case "COLDCHANCE":
                if (short.TryParse(val, out short cc)) { _coldChance = cc; return true; }
                return false;
            case "FLAGS":
                if (TryParseUInt(val, out uint fv)) { Flags = (SectorFlag)fv; return true; }
                return false;
            case "NOSLEEP":
                Flags = ParseBool(val) ? _flags | SectorFlag.NoSleep : _flags & ~SectorFlag.NoSleep;
                return true;
            case "INSTASLEEP":
                Flags = ParseBool(val) ? _flags | SectorFlag.InstaSleep : _flags & ~SectorFlag.InstaSleep;
                return true;
            default:
                return false;
        }
    }

    private static bool ParseBool(string v) =>
        v is "1" or "true" or "TRUE" || (int.TryParse(v, out int n) && n != 0);

    private static bool TryParseUInt(string v, out uint result)
    {
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(v, out result);
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "DRY":
                _weather = 0;
                return true;
            case "RAIN":
                _weather = 1;
                return true;
            case "SNOW":
                _weather = 2;
                return true;
            case "ALLCHARS":
                // Execute command on all characters — handled by caller via iteration
                return true;
            case "ALLCHARSIDLE":
                // Execute command on all idle (offline) characters — handled by caller
                return true;
            case "ALLCLIENTS":
                // Execute command on all connected players — handled by caller
                return true;
            case "ALLITEMS":
                // Execute command on all items — handled by caller
                return true;
            case "RESPAWN":
                for (int i = _characters.Count - 1; i >= 0; i--)
                {
                    var ch = _characters[i];
                    if (!ch.IsPlayer && ch.IsDead)
                    {
                        if (Character.OnLifecycleResurrect != null) Character.OnLifecycleResurrect(ch);
                        else ch.Resurrect();
                    }
                }
                return true;
            case "RESTOCK":
                // Restock NPCs — trigger via callback
                return true;
            default:
                return false;
        }
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
        => TriggerResult.Default;

}
