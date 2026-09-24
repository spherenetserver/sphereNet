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
        {
            _whitelist.Clear();
            _stairMultis.Clear();
        }
    }

    // Staircase multis a designer may place (stairs.txt Multi* columns).
    private static readonly HashSet<ushort> _stairMultis = [];

    /// <summary>The house-design files and the columns holding piece graphics
    /// (Source-X CItemMultiCustom::LoadValidItems, CItemMultiCustom.cpp:1939).</summary>
    private static readonly (string File, string[] Columns)[] PieceFiles =
    [
        ("doors.txt", ["Piece1", "Piece2", "Piece3", "Piece4", "Piece5", "Piece6", "Piece7", "Piece8"]),
        ("misc.txt", ["Piece1", "Piece2", "Piece3", "Piece4", "Piece5", "Piece6", "Piece7", "Piece8"]),
        ("floors.txt", ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "F13", "F14", "F15", "F16"]),
        ("teleprts.txt", ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "F13", "F14", "F15", "F16"]),
        ("roof.txt", ["North", "East", "South", "West", "NSCrosspiece", "EWCrosspiece", "NDent", "EDent", "SDent", "WDent", "NTPiece", "ETPiece", "STPiece", "WTPiece", "XPiece", "Extra Piece"]),
        ("walls.txt", ["South1", "South2", "South3", "Corner", "East1", "East2", "East3", "Post", "WindowS", "AltWindowS", "WindowE", "AltWindowE", "SecondAltWindowS", "SecondAltWindowE"]),
        ("stairs.txt", ["Block", "North", "East", "South", "West", "Squared1", "Squared2", "Rounded1", "Rounded2"]),
    ];

    private static readonly string[] StairMultiColumns = ["MultiNorth", "MultiEast", "MultiSouth", "MultiWest"];

    /// <summary>Read the client's house-design files from <paramref name="directory"/>.
    /// Tab-separated, first row the column types, second the column names, then one
    /// row per design set (CSVFile::_Open, CSVFile.cpp:50). Returns how many files
    /// were found; with none, placement stays range-checked only.</summary>
    public static int LoadFromDirectory(string directory)
    {
        int files = 0;
        var pieces = new List<ushort>();
        var stairs = new List<ushort>();
        foreach (var (file, columns) in PieceFiles)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            files++;
            ReadColumns(path, columns, pieces);
            if (file == "stairs.txt")
                ReadColumns(path, StairMultiColumns, stairs);
        }
        RegisterValidItems(pieces);
        lock (_lock)
            foreach (var id in stairs)
                if (id is > 0 and < ItemIdMulti)
                    _stairMultis.Add(id);
        return files;
    }

    private static void ReadColumns(string path, string[] columns, List<ushort> into)
    {
        var rows = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (rows.Count < 2) return;
        var names = rows[1].Split('\t');
        var wanted = new List<int>();
        for (int i = 0; i < names.Length; i++)
            if (columns.Contains(names[i].Trim(), StringComparer.OrdinalIgnoreCase))
                wanted.Add(i);
        for (int r = 2; r < rows.Count; r++)
        {
            var cells = rows[r].Split('\t');
            foreach (int c in wanted)
            {
                if (c >= cells.Length) continue;
                string cell = cells[c].Trim();
                if (cell.Length == 0 || !char.IsDigit(cell[0])) continue;
                if (ushort.TryParse(cell, out ushort id) && id is > 0 and < ItemIdMulti)
                    into.Add(id);
            }
        }
    }

    /// <summary>Source-X IsValidItem, multi branch: a staircase the designer may
    /// place. GMs place any; with no stairs.txt loaded, any multi passes.</summary>
    public static bool IsValidStairMulti(ushort multiId, bool isGm)
    {
        if (multiId == 0 || multiId >= ItemIdMulti)
            return false;
        if (isGm)
            return true;
        lock (_lock)
            return _stairMultis.Count == 0 || _stairMultis.Contains(multiId);
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
