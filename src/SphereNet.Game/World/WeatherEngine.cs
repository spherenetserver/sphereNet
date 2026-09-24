using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Core.Configuration;
using SphereNet.Game.World.Sectors;

namespace SphereNet.Game.World;

/// <summary>
/// Weather type. Maps to WEATHER_TYPE in Source-X (sphereproto.h:514).
/// </summary>
public enum WeatherType : byte
{
    None = 0xFF,
    Rain = 0x00,
    Storm = 0x01,
    Snow = 0x02,
    /// <summary>WEATHER_CLOUDY: overcast, no precipitation (sphereproto.h:521).</summary>
    Cloudy = 0x03,
}

/// <summary>
/// Season type. Maps to SEASON_TYPE in Source-X.
/// </summary>
public enum SeasonType : byte
{
    Spring = 0,
    Summer = 1,
    Fall = 2,
    Winter = 3,
    Desolation = 4,
}

/// <summary>
/// Weather and season engine. Weather belongs to the SECTOR, as in Source-X
/// (CSectorEnviron::m_Weather): the sector stores it, publishes a change to everyone
/// standing in it (CSector::SetWeather) and recalculates it from its own RAINCHANCE /
/// COLDCHANCE on its periodic tick (CSector::_OnTick, GetWeatherCalc). The global season
/// cycles here too (CWorld::GetSeason).
/// </summary>
public sealed class WeatherEngine
{
    /// <summary>An awake sector ticks every 30 seconds (SECTOR_TICKING_PERIOD,
    /// CSector.cpp:20).</summary>
    public const long SectorTickPeriodMs = 30_000;

    /// <summary>Temperature byte Source-X puts in every weather packet
    /// (addWeather, CClientMsg.cpp:538).</summary>
    public const byte PacketTemperature = 0x10;

    private readonly GameWorld _world;
    private readonly Random _rand = new();

    // Next due sector tick per sector. Entries are made for sectors a player has stood
    // in; the set of sectors is fixed, so this never grows past the map.
    private readonly Dictionary<Sector, long> _nextSectorTick = [];

    // Reusable scratch set for OnTick's active-sector pass; allocated once to avoid
    // per-tick GC churn when 500+ players are online.
    private readonly HashSet<Sector> _activeSectorsScratch = [];

    // Global season
    private SeasonType _currentSeason = SeasonType.Spring;
    private long _lastSeasonChangeTick;
    private SeasonMode _seasonMode = SeasonMode.Auto;

    /// <summary>Season change interval in milliseconds (default: 30 minutes).</summary>
    public int SeasonChangeInterval { get; set; } = 30 * 60 * 1000;

    public SeasonType CurrentSeason => _currentSeason;
    public SeasonMode CurrentSeasonMode => _seasonMode;

    public WeatherEngine(GameWorld world)
    {
        _world = world;
        _world.CurrentSeason = (byte)_currentSeason;
        _lastSeasonChangeTick = Environment.TickCount64;
    }

    public void Configure(SeasonMode mode, SeasonType defaultSeason, int intervalMs)
    {
        _seasonMode = mode;
        SeasonChangeInterval = Math.Max(0, intervalMs);
        SetSeason(defaultSeason, resetCycleTimer: true);
    }

    public bool SetSeason(SeasonType season, bool resetCycleTimer = true)
    {
        bool changed = _currentSeason != season || _world.CurrentSeason != (byte)season;
        _currentSeason = season;
        _world.CurrentSeason = (byte)season;
        if (resetCycleTimer)
            _lastSeasonChangeTick = Environment.TickCount64;
        return changed;
    }

    /// <summary>Is there no weather at all (ini NOWEATHER)? Upstream answers DRY for
    /// every sector when this is set and never rolls for precipitation
    /// (GetWeatherCalc, CSector.cpp:861); its own default is that there is none. The
    /// host sets it from the configuration.</summary>
    public static bool NoWeather { get; set; } = true;

    /// <summary>The weather at a point: the weather of the sector it lies in
    /// (CSector::GetWeather), with the intensity and temperature Source-X puts in the
    /// packet (addWeather, CClientMsg.cpp:538).
    ///
    /// NOWEATHER is deliberately NOT read here: upstream keeps whatever weather was SET
    /// on a sector and answers with it; what the flag stops is the rolling of new
    /// weather and the telling of clients (addWeather, CClientMsg.cpp:526). Hiding the
    /// stored value here would have made a script's own weather unreadable.</summary>
    public (WeatherType Type, byte Intensity, byte Temperature) GetWeatherAt(Point3D pt)
    {
        var sector = _world.GetSector(pt);
        var type = sector == null ? WeatherType.None : (WeatherType)sector.Weather;
        return (type, PacketIntensity(type), PacketTemperature);
    }

    /// <summary>Source-X sends a fresh g_Rand.GetVal2(10, 70) with every weather packet
    /// (addWeather, CClientMsg.cpp:538). Dry weather carries none.</summary>
    public static byte PacketIntensity(WeatherType type) =>
        type == WeatherType.None ? (byte)0 : (byte)Random.Shared.Next(10, 71);

    /// <summary>Source-X CSector::GetWeatherCalc (CSector.cpp:856): no weather
    /// underground or on a NOWEATHER shard; otherwise the sector's RAINCHANCE decides
    /// precipitation, its COLDCHANCE whether that falls as snow, and a near miss on the
    /// rain roll leaves it cloudy.</summary>
    public WeatherType GetWeatherCalc(Sector sector)
    {
        if (NoWeather || sector.IsDungeon?.Invoke() == true)
            return WeatherType.None;

        int rain = sector.RainChance;
        int cold = sector.ColdChance;
        int roll = _rand.Next(100);
        if (roll < rain)
        {
            if (cold > 0 && _rand.Next(100) <= cold)
                return WeatherType.Snow;
            return WeatherType.Rain;
        }
        if (roll / 2 < rain)
            return WeatherType.Cloudy;
        return WeatherType.None;
    }

    /// <summary>The weather half of one sector tick (CSector::_OnTick,
    /// CSector.cpp:1246): one time in thirty the weather is recalculated, and a change
    /// is published by the sector.</summary>
    internal void OnSectorTick(Sector sector)
    {
        if (_rand.Next(30) != 0) return; // change less often
        sector.SetWeather((byte)GetWeatherCalc(sector));
    }

    /// <summary>
    /// Periodic weather update. Called from game tick.
    /// Returns true if season changed.
    /// </summary>
    public bool OnTick()
    {
        long now = Environment.TickCount64;

        // The sectors players stand in are the awake ones worth ticking. Each one
        // runs Source-X's sector tick every SECTOR_TICKING_PERIOD, and on it recalculates
        // its weather one time in thirty (CSector::_OnTick, CSector.cpp:1247). The
        // result is published by the sector itself (SetWeather), which is what tells
        // the players standing there and fires @EnvironChange.
        _activeSectorsScratch.Clear();
        foreach (var player in _world.OnlinePlayerSet)
        {
            if (player.IsDeleted || !player.IsOnline) continue;
            var sector = _world.GetSector(player.Position);
            if (sector != null) _activeSectorsScratch.Add(sector);
        }
        foreach (var sector in _activeSectorsScratch)
        {
            if (!_nextSectorTick.TryGetValue(sector, out long due))
            {
                _nextSectorTick[sector] = now + SectorTickPeriodMs;
                continue;
            }
            if (now < due) continue;
            _nextSectorTick[sector] = now + SectorTickPeriodMs;
            OnSectorTick(sector);
        }
        _activeSectorsScratch.Clear();

        if (_seasonMode != SeasonMode.Auto || SeasonChangeInterval <= 0)
            return false;

        if (now - _lastSeasonChangeTick < SeasonChangeInterval)
            return false;

        var nextSeason = (SeasonType)(((int)_currentSeason + 1) % 4);
        return SetSeason(nextSeason, resetCycleTimer: true);
    }

    /// <summary>
    /// Get the appropriate light level for a position (considers dungeon/underground).
    /// </summary>
    public byte GetLightLevel(Point3D pos)
    {
        return _world.GetLightLevel(pos);
    }

    /// <summary>
    /// Get music ID for a region. Returns 0 if no music assigned.
    /// </summary>
    public ushort GetRegionMusic(Regions.Region? region)
    {
        if (region == null) return 0;

        // Check region TAG for music
        if (region.TryGetTag("MUSIC", out string? musicStr) && ushort.TryParse(musicStr, out ushort musicId))
            return musicId;

        return 0;
    }
}
