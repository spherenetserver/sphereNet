using SphereNet.MapData.Map;
using SphereNet.MapData.Multi;
using SphereNet.MapData.Tiles;

namespace SphereNet.MapData;

/// <summary>
/// Central map data manager. Caches block reads and provides unified access
/// to terrain, statics, tiledata, and multis.
/// Maps to CUOInstall/CMapList map data access in Source-X.
/// </summary>
public sealed class MapDataManager : IDisposable
{
    private TileDataReader? _tileData;
    private readonly Dictionary<int, MapReader> _mapReaders = [];
    private readonly Dictionary<int, UopMapReader> _uopMapReaders = [];
    private readonly Dictionary<int, StaticReader> _staticReaders = [];
    private MultiReader? _multiReader;

    private readonly string _mulPath;

    public MapDataManager(string mulPath)
    {
        _mulPath = mulPath;
    }

    public void Load()
    {
        // Required: tiledata.mul — without it, static/item flag lookups (Impassable,
        // Surface, Wall, etc.) return defaults and walk-collision silently breaks.
        string tilePath = Path.Combine(_mulPath, "tiledata.mul");
        if (!File.Exists(tilePath))
            throw new FileNotFoundException(
                $"Required UO client file missing: {tilePath}. " +
                "tiledata.mul is mandatory for item flags (collision, surfaces, walls). " +
                "Copy it from your UO client install into the muls directory.",
                tilePath);
        _tileData = new TileDataReader(tilePath);
        _tileData.Load();

        // Required: multi.idx + multi.mul — houses/boats won't render or collide
        // without these. Source-X treats them as mandatory (CUOInstall).
        string multiIdxPath = Path.Combine(_mulPath, "multi.idx");
        string multiMulPath = Path.Combine(_mulPath, "multi.mul");
        if (!File.Exists(multiIdxPath) || !File.Exists(multiMulPath))
            throw new FileNotFoundException(
                $"Required UO client files missing: multi.idx and/or multi.mul in {_mulPath}. " +
                "Copy them from your UO client install.",
                File.Exists(multiIdxPath) ? multiMulPath : multiIdxPath);
        _multiReader = new MultiReader(multiIdxPath, multiMulPath);
    }

    /// <summary>
    /// Initialize a specific map (call after Load for each map defined in sphere.ini).
    /// </summary>
    /// <summary>Called when a map file is loaded (for logging).</summary>
    public event Action<int, string>? OnMapFileLoaded;
    public event Action<int, string, long, DateTime>? OnMapFileLoadedDetailed;

    public void InitMap(int mapId, int width, int height)
    {
        string mapPath = Path.Combine(_mulPath, $"map{mapId}.mul");
        string idxPath = Path.Combine(_mulPath, $"staidx{mapId}.mul");
        string statPath = Path.Combine(_mulPath, $"statics{mapId}.mul");

        // Required: terrain — prefer UOP (modern client) over MUL (legacy).
        // Without this, GetTerrainTile returns default (tile 0) everywhere.
        string? uopPath = FindUopMap(mapId);
        if (uopPath != null)
        {
            _uopMapReaders[mapId] = new UopMapReader(uopPath, width, height);
            NotifyMapFileLoaded(mapId, uopPath);
        }
        else if (File.Exists(mapPath))
        {
            _mapReaders[mapId] = new MapReader(mapPath, width, height);
            NotifyMapFileLoaded(mapId, mapPath);
        }
        else
        {
            throw new FileNotFoundException(
                $"Required UO terrain file missing for map {mapId}: " +
                $"neither map{mapId}LegacyMUL.uop / map{mapId}xLegacyMUL.uop " +
                $"nor {mapPath} found in {_mulPath}. Copy from your UO client install.",
                mapPath);
        }

        // Required: staidxN.mul + staticsN.mul — without these, walk collision
        // against walls/buildings/trees is disabled (GetStatics returns empty).
        if (!File.Exists(idxPath) || !File.Exists(statPath))
            throw new FileNotFoundException(
                $"Required UO static files missing for map {mapId}: " +
                $"staidx{mapId}.mul and/or statics{mapId}.mul in {_mulPath}. " +
                "Without these, players can walk through walls. " +
                "Copy from your UO client install.",
                File.Exists(idxPath) ? statPath : idxPath);
        _staticReaders[mapId] = new StaticReader(idxPath, statPath, width, height);
        NotifyMapFileLoaded(mapId, idxPath);
        NotifyMapFileLoaded(mapId, statPath);
    }

    private void NotifyMapFileLoaded(int mapId, string path)
    {
        OnMapFileLoaded?.Invoke(mapId, path);
        var info = new FileInfo(path);
        OnMapFileLoadedDetailed?.Invoke(mapId, path, info.Length, info.LastWriteTimeUtc);
    }

    private string? FindUopMap(int mapId)
    {
        // Prefer the 'x' (patched/updated) variant — matches modern client terrain
        string xName = $"map{mapId}xLegacyMUL.uop";
        string xPath = Path.Combine(_mulPath, xName);
        if (File.Exists(xPath)) return xPath;

        string name = $"map{mapId}LegacyMUL.uop";
        string path = Path.Combine(_mulPath, name);
        if (File.Exists(path)) return path;

        return null;
    }

    public (int Width, int Height) GetMapSize(int mapId)
    {
        if (_mapReaders.TryGetValue(mapId, out var reader))
            return (reader.Width, reader.Height);
        if (_uopMapReaders.TryGetValue(mapId, out var uopReader))
            return (uopReader.Width, uopReader.Height);
        return (7168, 4096);
    }

    public MapCell GetTerrainTile(int mapId, int x, int y)
    {
        if (_mapReaders.TryGetValue(mapId, out var reader))
            return reader.GetCell(x, y);
        if (_uopMapReaders.TryGetValue(mapId, out var uopReader))
            return uopReader.GetCell(x, y);
        return default;
    }

    public StaticItem[] GetStatics(int mapId, int x, int y)
    {
        if (_staticReaders.TryGetValue(mapId, out var reader))
            return reader.GetStatics(x, y);
        return [];
    }

    public void ForEachStatic(int mapId, int x, int y, Action<StaticItem> action)
    {
        if (_staticReaders.TryGetValue(mapId, out var reader))
            reader.ForEachStatic(x, y, action);
    }

    public bool AnyStatic(int mapId, int x, int y, Func<StaticItem, bool> predicate) =>
        _staticReaders.TryGetValue(mapId, out var reader) && reader.AnyStatic(x, y, predicate);

    public ReadOnlySpan<StaticItem> GetStaticBlock(int mapId, int x, int y, out int offX, out int offY)
    {
        offX = x % MapBlock.BlockSize;
        offY = y % MapBlock.BlockSize;
        if (_staticReaders.TryGetValue(mapId, out var reader))
            return reader.ReadBlock(x / MapBlock.BlockSize, y / MapBlock.BlockSize);
        return [];
    }

    public LandTileData GetLandTileData(int tileId) =>
        _tileData?.GetLandTile(tileId) ?? default;

    public ItemTileData GetItemTileData(int tileId) =>
        _tileData?.GetItemTile(tileId) ?? default;

    public MultiDef? GetMulti(int multiId) =>
        _multiReader?.GetMulti(multiId);

    /// <summary>
    /// Get effective Z height at a world coordinate (terrain + statics).
    /// Without a reference Z, returns terrain height only (safe default).
    /// Prefer the overload with currentZ for movement.
    /// </summary>
    public sbyte GetEffectiveZ(int mapId, int x, int y)
    {
        return GetTerrainTile(mapId, x, y).Z;
    }

    /// <summary>
    /// Get effective Z height at a world coordinate considering the character's
    /// current Z. Picks the closest walkable surface at or near the character's
    /// level, not the highest one (important for multi-story buildings).
    /// </summary>
    public sbyte GetEffectiveZ(int mapId, int x, int y, sbyte currentZ)
    {
        // Source-X / UO walk-climb limit: a character can only step onto a
        // surface whose top is within MaxClimbHeight units of its current Z.
        // Without this cap, GetEffectiveZ happily returns a rooftop 20 units
        // above the player while it's running along the ground — the resulting
        // Z jump fails MovementEngine's climb check and every step produces a
        // 0x21 MoveReject ("square-jumping" / stutter).
        const int MaxClimbHeight = 12;

        var terrain = GetTerrainTile(mapId, x, y);
        sbyte bestZ = terrain.Z;
        int bestDist = Math.Abs(currentZ - bestZ);

        if (_staticReaders.TryGetValue(mapId, out var staticReader))
        {
            var staticBlock = staticReader.ReadBlock(x / MapBlock.BlockSize, y / MapBlock.BlockSize);
            int offX = x % MapBlock.BlockSize;
            int offY = y % MapBlock.BlockSize;
            foreach (var s in staticBlock)
            {
                if (s.XOffset != offX || s.YOffset != offY)
                    continue;

                var data = GetItemTileData(s.TileId);
                if (data.IsSurface || data.IsBridge)
                {
                    sbyte topZ = (sbyte)(s.Z + data.CalcHeight);
                    int dist = Math.Abs(currentZ - topZ);
                    if (dist > MaxClimbHeight) continue; // unreachable in one step
                    if (dist < bestDist)
                    {
                        bestZ = topZ;
                        bestDist = dist;
                    }
                }
            }
        }

        return bestZ;
    }

    public (sbyte EffectiveZ, bool Passable) GetEffectiveZAndPassable(int mapId, int x, int y, sbyte currentZ, int z)
    {
        const int MaxClimbHeight = 12;
        var terrain = GetTerrainTile(mapId, x, y);
        sbyte bestZ = terrain.Z;
        int bestDist = Math.Abs(currentZ - bestZ);
        bool blocks = false;

        var landData = GetLandTileData(terrain.TileId);
        bool wetLand = landData.IsWet;
        bool hasWetSurface = false;

        var staticBlock = GetStaticBlock(mapId, x, y, out int offX, out int offY);
        foreach (var s in staticBlock)
        {
            if (s.XOffset != offX || s.YOffset != offY)
                continue;

            var data = GetItemTileData(s.TileId);
            if (data.IsSurface || data.IsBridge)
            {
                sbyte topZ = (sbyte)(s.Z + data.CalcHeight);
                int dist = Math.Abs(currentZ - topZ);
                if (dist <= MaxClimbHeight && dist < bestDist)
                {
                    bestZ = topZ;
                    bestDist = dist;
                }

                if (wetLand && z >= s.Z && z <= s.Z + data.CalcHeight + 2)
                    hasWetSurface = true;
            }

            if (data.IsImpassable && z >= s.Z && z < s.Z + data.Height)
                blocks = true;
        }

        if (wetLand && !hasWetSurface)
            blocks = true;

        return (bestZ, !blocks);
    }

    /// <summary>Compute the 4-corner average / low / top land Z for the tile
    /// footprint at (x, y). ClassicUO only "stretches" land tiles that have a
    /// valid texmap entry (TextureId != 0). Tiles without a texture are treated
    /// as flat — all three outputs equal the raw tile Z. Matching this avoids Z
    /// mismatches between server and client on untextured slopes.</summary>
    public void GetAverageZ(int mapId, int x, int y, out int low, out int average, out int top)
    {
        var landTile = GetTerrainTile(mapId, x, y);
        var landData = GetLandTileData(landTile.TileId);

        if (landData.TextureId == 0)
        {
            low = average = top = landTile.Z;
            return;
        }

        int zTop = landTile.Z;
        int zLeft = GetTerrainTile(mapId, x, y + 1).Z;
        int zRight = GetTerrainTile(mapId, x + 1, y).Z;
        int zBottom = GetTerrainTile(mapId, x + 1, y + 1).Z;

        low = zTop;
        if (zLeft < low) low = zLeft;
        if (zRight < low) low = zRight;
        if (zBottom < low) low = zBottom;

        top = zTop;
        if (zLeft > top) top = zLeft;
        if (zRight > top) top = zRight;
        if (zBottom > top) top = zBottom;

        average = Math.Abs(zTop - zBottom) > Math.Abs(zLeft - zRight)
            ? FloorAverage(zLeft, zRight)
            : FloorAverage(zTop, zBottom);
    }

    private static int FloorAverage(int a, int b)
    {
        int v = a + b;
        if (v < 0) v--;
        return v / 2;
    }

    /// <summary>Direction-specific land Z for the CalculateMinMaxZ pre-filter.
    /// Cardinal directions return the average of two edge corners;
    /// diagonal directions return the single corner in that direction.
    /// Matches ClassicUO Land.CalculateCurrentAverageZ. Only meaningful for
    /// tiles with a valid texture (stretched); untextured tiles return the
    /// raw tile Z.</summary>
    public int GetDirectionalLandZ(int mapId, int x, int y, int direction)
    {
        var landTile = GetTerrainTile(mapId, x, y);
        var landData = GetLandTileData(landTile.TileId);

        if (landData.TextureId == 0)
            return landTile.Z;

        int zNW = landTile.Z;
        int zNE = GetTerrainTile(mapId, x + 1, y).Z;
        int zSE = GetTerrainTile(mapId, x + 1, y + 1).Z;
        int zSW = GetTerrainTile(mapId, x, y + 1).Z;

        int idx = (((direction >> 1) + 1) & 3);
        int z1 = idx switch { 1 => zNE, 2 => zSE, 3 => zSW, _ => zNW };

        if ((direction & 1) != 0)
            return z1;

        int idx2 = direction >> 1;
        int z2 = idx2 switch { 1 => zNE, 2 => zSE, 3 => zSW, _ => zNW };
        return (z1 + z2) >> 1;
    }

    /// <summary>Matches ServUO LandTile.Ignored — void/no-draw tiles that the
    /// movement algorithm treats as absent land.</summary>
    public static bool IsLandIgnored(ushort tileId) =>
        tileId == 2 || tileId == 0x1DB || (tileId >= 0x1AE && tileId <= 0x1B5);

    /// <summary>
    /// Check if a world coordinate is passable (not blocked by impassable statics or water).
    /// </summary>
    public bool IsPassable(int mapId, int x, int y, int z)
    {
        // Check terrain (land) tile — water tiles are not walkable
        // unless there is a walkable surface/bridge static above (e.g. dock, bridge, ship)
        var terrain = GetTerrainTile(mapId, x, y);
        var landData = GetLandTileData(terrain.TileId);
        if (landData.IsWet)
        {
            bool hasSurface = false;
            hasSurface = AnyStatic(mapId, x, y, s =>
            {
                var sd = GetItemTileData(s.TileId);
                return (sd.IsSurface || sd.IsBridge) && z >= s.Z && z <= s.Z + sd.CalcHeight + 2;
            });
            if (!hasSurface)
                return false;
        }

        if (AnyStatic(mapId, x, y, s =>
        {
            var data = GetItemTileData(s.TileId);
            return data.IsImpassable && z >= s.Z && z < s.Z + data.Height;
        }))
            return false;
        return true;
    }

    public void Dispose()
    {
        _tileData?.Dispose();
        _multiReader?.Dispose();
        foreach (var r in _mapReaders.Values) r.Dispose();
        foreach (var r in _uopMapReaders.Values) r.Dispose();
        foreach (var r in _staticReaders.Values) r.Dispose();
    }
}
