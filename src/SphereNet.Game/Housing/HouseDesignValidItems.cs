namespace SphereNet.Game.Housing;

/// <summary>
/// Custom-house design tile validity (Source-X CItemMultiCustom::IsValidItem /
/// LoadValidItems / ValidItemsContainer). A designing player sends raw tile
/// graphics in the 0xD7 stream; without this gate a crafted packet could place
/// any graphic (multi bodies, blocking statics, unrenderable ids) into a house.
///
/// Source-X's ultimate whitelist comes from the client's house-design CSVs
/// (doors/walls/floors/roof/stairs). Those data files are not guaranteed to be
/// present, so this port always enforces the structural range check (which
/// blocks the exploit) and, when a whitelist has been registered, additionally
/// restricts non-GM placement to those known pieces.
/// </summary>
public static class HouseDesignValidItems
{
    /// <summary>Classic ITEMID_MULTI boundary — house-design pieces (walls,
    /// floors, doors, roofs, stairs) are all static graphics below this; a
    /// graphic at or above it is a multi body and never a valid design tile.</summary>
    public const ushort ItemIdMulti = 0x4000;

    // Optional whitelist (Source-X ValidItemsContainer). Empty = range-only
    // enforcement so custom housing works without the client CSVs.
    private static readonly HashSet<ushort> _whitelist = [];
    private static readonly object _lock = new();

    /// <summary>Register known-valid design piece graphics (e.g. loaded from a
    /// house-design data file). Once any are registered, non-GM placement is
    /// restricted to the whitelist.</summary>
    public static void RegisterValidItems(IEnumerable<ushort> ids)
    {
        lock (_lock)
            foreach (var id in ids)
                if (id is > 0 and < ItemIdMulti)
                    _whitelist.Add(id);
    }

    public static void ClearValidItems()
    {
        lock (_lock)
            _whitelist.Clear();
    }

    public static int WhitelistCount
    {
        get { lock (_lock) return _whitelist.Count; }
    }

    /// <summary>Source-X IsValidItem (non-multi branch): the graphic must be a
    /// real static item; the range check applies to everyone (GMs included),
    /// then GMs bypass the whitelist while ordinary designers must match it.</summary>
    public static bool IsValidBuildTile(ushort id, bool isGm)
    {
        if (id == 0 || id >= ItemIdMulti)
            return false;
        if (isGm)
            return true;
        lock (_lock)
            return _whitelist.Count == 0 || _whitelist.Contains(id);
    }
}
