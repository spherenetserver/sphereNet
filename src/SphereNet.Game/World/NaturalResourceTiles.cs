using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;

namespace SphereNet.Game.World;

/// <summary>What TYPE a map tile is, the way Source-X asks it before a natural
/// resource is looked for (CWorldMap::CheckNaturalResource fTest,
/// CWorldMap.cpp:40-53). A dynamic item answers by its TYPE, a static by the TYPE
/// its ITEMDEF declares, and the land by the [TYPEDEF] whose TERRAIN= ranges take
/// in the land tile id (CWorldMap::GetTerrainItemType). No tile names are read:
/// which rocks, trees and waters exist is the script pack's to say.</summary>
public static class NaturalResourceTiles
{
    /// <summary>Source-X RESOURCE_Z_CHECK used by IsTypeNear_Top.</summary>
    private const int ResourceZCheck = 8;

    private static readonly object _cacheLock = new();
    private static object? _terrainTypesFor;
    private static readonly Dictionary<ItemType, List<(int Lo, int Hi)>> _terrainTypes = [];

    /// <summary>Source-X CWorldMap::IsTypeNear_Top(pt, type, 0) (CWorldMap.cpp:
    /// 340-632) at one tile: of the dynamic items, the statics and the land there
    /// that sit within 8 Z of the point, the highest top has to be of the type. On
    /// equal heights the typed element wins. Water is tested this way
    /// (CWorldMap.cpp:44).</summary>
    public static bool IsTypeNearTop(GameWorld world, Point3D pt, ItemType type)
    {
        var mapData = world.MapData;
        int bestZ = int.MinValue;      // the highest top of any kind
        int bestTypedZ = int.MinValue; // the highest top that is of the type

        void Consider(int topZ, int zForCheck, bool typed)
        {
            if (Math.Abs(zForCheck - pt.Z) > ResourceZCheck)
                return;
            if (topZ > bestZ) bestZ = topZ;
            if (typed && topZ > bestTypedZ) bestTypedZ = topZ;
        }

        foreach (var item in world.GetItemsInRange(pt, 0))
        {
            if (item.IsDeleted || !item.IsOnGround || item.X != pt.X || item.Y != pt.Y)
                continue;
            int height = mapData?.GetItemTileData(item.DispIdFull).Height ?? 0;
            int top = Math.Min(item.Z + height, 127);
            Consider(top, top, item.ItemType == type);
        }
        if (mapData != null)
        {
            foreach (var s in mapData.GetStatics(pt.Map, pt.X, pt.Y))
            {
                int top = Math.Min(s.Z + mapData.GetItemTileData(s.TileId).Height, 127);
                Consider(top, s.Z, DefinitionLoader.GetItemDef(s.TileId)?.Type == type);
            }
            var cell = mapData.GetTerrainTile(pt.Map, pt.X, pt.Y);
            Consider(cell.Z, cell.Z, TerrainIsType(cell.TileId, type));
        }
        return bestTypedZ != int.MinValue && bestTypedZ >= bestZ;
    }

    /// <summary>Source-X CWorldMap::IsItemTypeNear(pt, type, 0, false) ->
    /// FindItemTypeNearby (CWorldMap.cpp:654-760) at one tile: a dynamic item of the
    /// type (its own TYPE or its definition's), a land tile of the type, or a static
    /// whose ITEMDEF is of the type. There is no Z test - rock and tree are tested
    /// this way (CWorldMap.cpp:49).</summary>
    public static bool IsItemTypeAt(GameWorld world, Point3D pt, ItemType type)
    {
        foreach (var item in world.GetItemsInRange(pt, 0))
        {
            if (item.IsDeleted || !item.IsOnGround || item.X != pt.X || item.Y != pt.Y)
                continue;
            if (item.ItemType == type || DefinitionLoader.GetItemDef(item.BaseId)?.Type == type)
                return true;
        }

        var mapData = world.MapData;
        if (mapData == null)
            return false;
        if (TerrainIsType(mapData.GetTerrainTile(pt.Map, pt.X, pt.Y).TileId, type))
            return true;
        foreach (var s in mapData.GetStatics(pt.Map, pt.X, pt.Y))
        {
            if (DefinitionLoader.GetItemDef(s.TileId)?.Type == type)
                return true;
        }
        return false;
    }

    /// <summary>Source-X CWorldMap::GetTerrainItemType: the [TYPEDEF] whose
    /// TERRAIN=lo hi lines (CItemTypeDef::r_LoadVal, CItemTypeDef.cpp:21-70) take
    /// in this land tile id. Read from the loaded script pack, cached per pack.</summary>
    public static bool TerrainIsType(int landTileId, ItemType type)
    {
        var resources = DefinitionLoader.StaticResources;
        if (resources == null)
            return false;

        List<(int Lo, int Hi)>? ranges;
        lock (_cacheLock)
        {
            if (!ReferenceEquals(_terrainTypesFor, resources))
            {
                _terrainTypes.Clear();
                _terrainTypesFor = resources;
            }
            if (!_terrainTypes.TryGetValue(type, out ranges))
            {
                ranges = LoadTerrainRanges(resources, type);
                _terrainTypes[type] = ranges;
            }
        }
        foreach (var (lo, hi) in ranges)
            if (landTileId >= lo && landTileId <= hi)
                return true;
        return false;
    }

    private static object? _terrainTableFor;
    private static ItemType[]? _terrainTable;

    /// <summary>Source-X CWorldMap::GetTerrainItemType (CWorldMap.cpp:204-221): the
    /// TYPE whose [TYPEDEF] TERRAIN= ranges claim this land tile id, IT_NORMAL when
    /// none does (or no script pack is loaded). Upstream fills one id -> type table
    /// as the TERRAIN lines load (CItemTypeDef.cpp:65), so a later claim replaces an
    /// earlier one; the table here is built once per loaded pack and then read
    /// without locking or allocating.</summary>
    public static ItemType TerrainItemType(int landTileId)
    {
        var resources = DefinitionLoader.StaticResources;
        if (resources == null || landTileId < 0)
            return ItemType.Normal;

        var table = Volatile.Read(ref _terrainTable);
        if (table == null || !ReferenceEquals(Volatile.Read(ref _terrainTableFor), resources))
        {
            lock (_cacheLock)
            {
                if (_terrainTable == null || !ReferenceEquals(_terrainTableFor, resources))
                {
                    _terrainTable = BuildTerrainTable(resources);
                    Volatile.Write(ref _terrainTableFor, resources);
                }
                table = _terrainTable;
            }
        }
        return landTileId < table.Length ? table[landTileId] : ItemType.Normal;
    }

    private static ItemType[] BuildTerrainTable(SphereNet.Scripting.Resources.ResourceHolder resources)
    {
        var table = new ItemType[0x4000]; // land tile ids; default IT_NORMAL (0)
        foreach (var candidate in resources.GetAllResources())
        {
            if (candidate.Id.Type != ResType.TypeDef || candidate.StoredKeys == null)
                continue;
            int typeIndex = candidate.Id.Index;
            if (!string.IsNullOrEmpty(candidate.DefName))
            {
                var named = resources.ResolveDefName(candidate.DefName);
                if (named.Type == ResType.TypeDef)
                    typeIndex = named.Index;
            }
            foreach (var key in candidate.StoredKeys)
            {
                if (!key.Key.StartsWith("TERRAIN", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = key.Arg.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !ScriptNumber.TryParseToken(parts[0], out long lo) || lo < 0)
                    continue;
                long hi = lo;
                if (parts.Length > 1 && (!ScriptNumber.TryParseToken(parts[1], out hi) || hi < 0))
                    continue;
                long from = Math.Min(lo, hi), to = Math.Min(Math.Max(lo, hi), table.Length - 1);
                for (long id = from; id <= to; id++)
                    table[id] = (ItemType)typeIndex;
            }
        }
        return table;
    }

    private static List<(int Lo, int Hi)> LoadTerrainRanges(
        SphereNet.Scripting.Resources.ResourceHolder resources, ItemType type)
    {
        var ranges = new List<(int Lo, int Hi)>();
        var link = resources.GetResource(ResType.TypeDef, (int)type);
        if (link == null)
        {
            // [TYPEDEF t_grass] read before the [TYPEDEFS] line naming t_grass 97
            // was filed under its name: find it by what that name means now.
            foreach (var candidate in resources.GetAllResources())
            {
                if (candidate.Id.Type != ResType.TypeDef || string.IsNullOrEmpty(candidate.DefName))
                    continue;
                var named = resources.ResolveDefName(candidate.DefName);
                if (named.Type == ResType.TypeDef && named.Index == (int)type)
                {
                    link = candidate;
                    break;
                }
            }
        }
        if (link?.StoredKeys != null)
        {
            foreach (var key in link.StoredKeys)
            {
                if (!key.Key.StartsWith("TERRAIN", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = key.Arg.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !ScriptNumber.TryParseToken(parts[0], out long lo) || lo < 0)
                    continue;
                long hi = lo;
                if (parts.Length > 1 && (!ScriptNumber.TryParseToken(parts[1], out hi) || hi < 0))
                    continue;
                ranges.Add(((int)Math.Min(lo, hi), (int)Math.Max(lo, hi)));
            }
        }
        return ranges;
    }
}
