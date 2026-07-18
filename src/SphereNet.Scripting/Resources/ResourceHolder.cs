using Microsoft.Extensions.Logging;
using SphereNet.Core.Collections;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Central resource registry. Maps to CResourceHolder + CServerConfig.LoadResourceSection in Source-X.
/// Manages all loaded script resources indexed by ResourceId.
/// </summary>
public sealed class ResourceHolder
{
    private readonly SortedResourceHash<ResourceLink> _resources = new();
    private readonly Dictionary<string, ResourceId> _defNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceScript> _scriptFiles = [];
    private readonly Dictionary<string, string> _defMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TeleporterEntry> _teleporters = [];
    private readonly List<StartEntry> _starts = [];
    private readonly List<StartGoldEntry> _startGold = [];
    private readonly List<MoongateEntry> _moongates = [];
    private readonly Dictionary<string, (string FilePath, List<string> Lines)> _dialogTextCache = new(StringComparer.OrdinalIgnoreCase);

    // Load-time section caches for the interactive gump/menu surfaces. The
    // dialog/menu open paths used to re-open and re-parse EVERY script file on
    // EVERY gump open, button click and menu request (a ~400ms main-loop stall
    // per click on a real pack — worst when the id does not exist at all, e.g.
    // the native-fallback d_helppage). Kept in sync by the same load/resync
    // pipeline as _dialogTextCache.
    private readonly Dictionary<string, (string FilePath, ScriptSection Section)> _dialogLayoutCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string FilePath, ScriptSection Section)> _dialogButtonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string FilePath, ScriptSection Section)> _menuSectionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<ScriptKey>> _plevelCommands = [];
    private readonly List<string> _obsceneWords = [];
    private readonly List<string> _fameTitles = [];
    private readonly List<string> _karmaTitles = [];
    private readonly List<int> _notoKarmaLevels = [];
    private readonly List<int> _notoFameLevels = [];
    private readonly List<string> _notoTitles = [];
    private readonly List<string> _runes = [];
    private readonly List<KeyValuePair<string, string>> _scriptGlobalVars = [];
    private readonly List<(string Name, List<string> Elements)> _scriptGlobalLists = [];
    private readonly List<string> _resourceListNames = [];
    private readonly List<string> _pendingResourceFiles = [];
    private readonly HashSet<string> _knownResourceFiles = new(StringComparer.OrdinalIgnoreCase);
    // Source-X FindResourceFile dedups by file TITLE (basename), not path —
    // a same-named file in another directory is treated as already loaded.
    private readonly HashSet<string> _knownResourceTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public IReadOnlyList<(Point3D Src, Point3D Dest, string Name)> Teleporters =>
        _teleporters.Select(t => (t.Src, t.Dest, t.Name)).ToList();
    public IReadOnlyList<StartEntry> Starts => _starts;
    public IReadOnlyList<StartGoldEntry> StartGold => _startGold;
    public IReadOnlyList<MoongateEntry> Moongates => _moongates;

    public string ScpBaseDir { get; set; } = "";

    private sealed record TeleporterEntry(Point3D Src, Point3D Dest, string Name, string FilePath);
    public sealed record StartEntry(string Name, Point3D Point);
    public sealed record StartGoldEntry(string Name, int Amount);
    public sealed record MoongateEntry(string Name, Point3D Point);

    public ResourceHolder(ILogger<ResourceHolder> logger)
    {
        _logger = logger;
        // Let definition parsers resolve symbolic constants (e.g. CHARDEF
        // CAN=MT_RUN|MT_WALK) against the script's own [DEFNAME] tables instead
        // of a hardcoded map. Falls back to the built-in map when unresolved.
        CharDef.DefNameResolver = name => TryResolveDefNameValue(name, out var v) ? v : null;
        ItemDef.DefNameResolver = name => TryResolveDefNameValue(name, out var v) ? v : null;
        CharDef.SpellNameResolver = name =>
        {
            var rid = ResolveDefName(name);
            return rid.IsValid && rid.Type == ResType.SpellDef ? rid.Index : null;
        };
    }

    /// <summary>
    /// Map a section name to its RES_TYPE.
    /// </summary>
    public static ResType SectionToResType(string sectionName) => sectionName.ToUpperInvariant() switch
    {
        "ITEMDEF" => ResType.ItemDef,
        "CHARDEF" => ResType.CharDef,
        "SPELL" => ResType.SpellDef,
        "SKILL" or "SKILLDEF" => ResType.SkillDef,
        "TEMPLATE" => ResType.Template,
        "TYPEDEF" => ResType.TypeDef,
        "DIALOG" => ResType.Dialog,
        "EVENTS" => ResType.Events,
        "FUNCTION" => ResType.Function,
        "DEFNAME" or "DEFNAMES" => ResType.DefName,
        "RESDEFNAME" => ResType.DefName,
        "REGIONTYPE" => ResType.RegionType,
        "REGIONRESOURCE" => ResType.RegionResource,
        "AREADEF" or "AREA" => ResType.Area,
        "ROOMDEF" or "ROOM" => ResType.RoomDef,
        "MULTIDEF" or "MULTI" => ResType.MultiDef,
        "SKILLCLASS" => ResType.SkillClass,
        "SKILLMENU" => ResType.SkillMenu,
        "MENU" => ResType.Menu,
        "SPHERE" => ResType.Sphere,
        "SCROLL" => ResType.Scroll,
        "BOOK" => ResType.Book,
        "TIP" => ResType.Tip,
        "SPEECH" => ResType.Speech,
        "NEWBIE" => ResType.NewBie,
        "PLEVEL" => ResType.PlevelCfg,
        "NAMES" => ResType.Names,
        "OBSCENE" => ResType.Obscene,
        "WEBPAGE" => ResType.WebPage,
        "RESOURCELIST" or "RESOURCES" => ResType.ResourceList,
        "SERVERS" => ResType.ServerConfig,
        "BLOCKIP" => ResType.ServerConfig,
        "SPAWN" => ResType.Spawn,
        "COMMENT" => ResType.Comment,
        "ADVANCE" or "FAME" or "KARMA" or "NOTOTITLES" or "RUNES" or "DEFMESSAGE" => ResType.Sphere,
        "TYPEDEFS" => ResType.Sphere,
        // Script-declared globals (Source-X RES_WORLDVARS / RES_WORLDLISTS and
        // their new-style GLOBALS / LIST synonyms) — handled by name in
        // TryLoadSpecialSection; mapped here so they never hit Unknown.
        "GLOBALS" or "WORLDVARS" or "LIST" or "WORLDLISTS" => ResType.Sphere,
        // Recognized Source-X section types with no SphereNet engine consumer
        // yet. Counted and skipped instead of warning as Unknown: world-save
        // blocks (WORLDCHAR/WORLDITEM/SECTOR/GMPAGE/TIMERF and the WC/WI/WS
        // aliases) are loaded by the Persistence layer from save files, and
        // ACCOUNT blocks by AccountPersistence.
        "CHAMPION" => ResType.Champion,
        "SPHERECRYPT" or "KRDIALOGLIST" or "ACCOUNT" or "GMPAGE" or "SECTOR"
            or "WORLDCHAR" or "WORLDITEM" or "WC" or "WI" or "WORLDSCRIPT" or "WS"
            or "TIMERF" or "STAT" => ResType.Sphere,
        "STARTS" or "STARTSGOLD" or "STARTGOLD" or "MOONGATES" or "TELEPORTERS" => ResType.WorldScript,
        _ => ResType.Unknown
    };

    /// <summary>
    /// Resource types that use numeric hex IDs (body ID / item ID / spell number).
    /// All other types use string names that get auto-hashed.
    /// </summary>
    private static bool IsNumericIdType(ResType t) => t is
        ResType.ItemDef or ResType.CharDef or ResType.SpellDef or ResType.SkillDef or ResType.MultiDef
        or ResType.PlevelCfg;

    /// <summary>
    /// Definition types whose keys should be retained on the ResourceLink
    /// for fast access by DefinitionLoader (avoids re-reading script files).
    /// </summary>
    private static bool IsDefinitionType(ResType t) => t is
        ResType.ItemDef or ResType.CharDef or ResType.SpellDef or ResType.SkillClass or ResType.SkillDef
        or ResType.Names or ResType.Speech or ResType.Template or ResType.RegionResource or ResType.RegionType
        or ResType.NewBie or ResType.Events or ResType.TypeDef or ResType.Function or ResType.Dialog
        or ResType.MultiDef or ResType.SkillMenu
        // Source-X keeps these as CResourceLink sections readable at runtime
        // (BOOK pages, MENU choices, SCROLL text, TIP entries, CHAMPION defs).
        or ResType.Book or ResType.Menu or ResType.Scroll or ResType.Tip
        or ResType.Champion;

    /// <summary>
    /// Load all sections from a script file.
    /// </summary>
    public int LoadResourceFile(string filePath)
    {
        string fullPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(ScpBaseDir, filePath);

        var resScript = new ResourceScript(fullPath);
        _scriptFiles.Add(resScript);
        try
        {
            _knownResourceFiles.Add(Path.GetFullPath(fullPath));
            _knownResourceTitles.Add(Path.GetFileName(fullPath));
        }
        catch { /* invalid path chars */ }

        ScriptFile file;
        try
        {
            file = resScript.Open();
            file.Diagnostic = message => _logger.LogWarning("{Message}", message);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Script file not found: {Path}", fullPath);
            return 0;
        }

        try
        {
            return LoadResourcesFromFile(file, fullPath);
        }
        finally
        {
            resScript.Close();
        }
    }

    private int LoadResourcesFromFile(ScriptFile file, string filePath)
    {
        int count = 0;
        var sections = file.ReadAllSections();

        foreach (var section in sections)
        {
            if (TryLoadSpecialSection(section))
            {
                count++;
                continue;
            }

            ResType resType = SectionToResType(section.Name);

            if (resType == ResType.DefName)
            {
                LoadDefNames(section);
                count++;
                continue;
            }

            if (resType == ResType.WorldScript)
            {
                string sectionUpper = section.Name.ToUpperInvariant();
                if (sectionUpper == "TELEPORTERS")
                    LoadTeleporters(section, filePath);
                else if (sectionUpper == "STARTS")
                    LoadStarts(section);
                else if (sectionUpper is "STARTSGOLD" or "STARTGOLD")
                    LoadStartGold(section);
                else if (sectionUpper == "MOONGATES")
                    LoadMoongates(section);
                count++;
                continue;
            }

            if (resType == ResType.Sphere || resType == ResType.ServerConfig ||
                resType == ResType.ResourceList || resType == ResType.Comment)
            {
                count++;
                continue;
            }

            if (resType == ResType.Unknown)
            {
                _logger.LogWarning("Unknown section [{Name}] in {File}:{Line}",
                    section.Name, filePath, section.Context.LineNumber);
                continue;
            }

            string rawArg = section.Argument;

            // NEWBIE race variants: "[NEWBIE MALE_DEFAULT ELF]" is a separate
            // template from "[NEWBIE MALE_DEFAULT]". Keying both by the first
            // token made the (usually empty) ELF/GARG variants overwrite the
            // human base section — fresh characters spawned naked because
            // MALE_DEFAULT resolved to the empty GARG section. Fold the race
            // suffix into the identity token instead. BOOK gets the same fold:
            // "[BOOK truth 1]" pages are distinct sections keyed name_page.
            if (resType is ResType.NewBie or ResType.Book && rawArg.Contains(' '))
                rawArg = rawArg.Trim().Replace('\t', '_').Replace(' ', '_');

            if (resType == ResType.Dialog)
            {
                var argParts = rawArg.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (argParts.Length >= 2 && argParts[1].Equals("TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    var textLines = new List<string>();
                    foreach (var key in section.Keys)
                        textLines.Add(key.RawLine.TrimEnd());
                    _dialogTextCache[argParts[0]] = (filePath, textLines);
                    count++;
                    continue;
                }
                CacheDialogSection(argParts, filePath, section);
            }
            else if (resType == ResType.Menu)
            {
                CacheMenuSection(rawArg, filePath, section);
            }

            int index = ParseResourceIndex(rawArg, resType);
            if (index < 0) continue;

            if (resType == ResType.PlevelCfg)
                LoadPlevelCommands(index, section);

            var rid = new ResourceId(resType, index);

            // SPAWN sections get parsed directly into SpawnGroupDef
            if (resType == ResType.Spawn)
            {
                var spawnDef = new SpawnGroupDef(rid)
                {
                    ScriptFilePath = filePath,
                    ScriptLineNumber = section.Context.LineNumber
                };

                foreach (var key in section.Keys)
                    spawnDef.LoadFromKey(key.Key, key.Arg);

                string spawnName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(spawnName))
                {
                    spawnDef.DefName = spawnName;
                    _defNames[spawnName] = rid;
                }

                _resources.Add(rid, spawnDef);

                if (!string.IsNullOrEmpty(spawnDef.DefName))
                    _defNames[spawnDef.DefName] = rid;

                count++;
                continue;
            }

            var link = new ResourceLink(rid)
            {
                ScriptFilePath = filePath,
                ScriptLineNumber = section.Context.LineNumber,
                HeaderArgument = rawArg
            };

            link.ScanSection(section, retainKeys: IsDefinitionType(resType));

            // For string-named resources, auto-register the name as a DEFNAME
            if (!IsNumericIdType(resType) && !string.IsNullOrEmpty(rawArg))
            {
                string defName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(defName))
                {
                    link.DefName = defName;
                    _defNames[defName] = rid;
                }
            }

            _resources.Add(rid, link);

            if (!string.IsNullOrEmpty(link.DefName))
                _defNames[link.DefName] = rid;

            count++;
        }

        _logger.LogInformation("Loaded {File}: {Count} sections", Path.GetFileName(filePath), count);
        return count;
    }

    /// <summary>The section line the way Source-X ReadKey sees it — the whole
    /// line verbatim, preserving the comma/space separator that the Key/Arg
    /// split would otherwise lose.</summary>
    private static string VerbatimLine(ScriptKey key) => key.RawLine;

    /// <summary>
    /// Name-dispatched sections that fill dedicated tables instead of becoming
    /// ResourceLinks — the counterpart of the special cases at the top of
    /// Source-X CServerConfig::LoadResourceSection. Shared by the initial load
    /// and the ReSync reload path. Returns true when the section was consumed.
    /// </summary>
    private bool TryLoadSpecialSection(ScriptSection section)
    {
        switch (section.Name.ToUpperInvariant())
        {
            case "DEFMESSAGE":
                LoadDefMessages(section);
                return true;
            case "ADVANCE":
                LoadStatAdvance(section);
                return true;
            case "FAME":
                LoadTitleLines(section, _fameTitles);
                return true;
            case "KARMA":
                LoadTitleLines(section, _karmaTitles);
                return true;
            case "NOTOTITLES":
                LoadNotoTitles(section);
                return true;
            case "RUNES":
                // Full names of the magic runes, indexed by letter A..Z
                // (Source-X RES_RUNES / GetRune).
                _runes.Clear();
                foreach (var key in section.Keys)
                    _runes.Add(VerbatimLine(key));
                return true;
            case "OBSCENE":
                foreach (var key in section.Keys)
                {
                    string word = VerbatimLine(key).Trim();
                    if (word.Length > 0)
                        _obsceneWords.Add(word);
                }
                return true;
            case "GLOBALS":
            case "WORLDVARS":
                LoadScriptGlobalVars(section);
                return true;
            case "LIST":
            case "WORLDLISTS":
                LoadScriptGlobalList(section);
                return true;
            case "TYPEDEFS":
                LoadTypeDefsBulk(section);
                return true;
            case "RESOURCES":
                QueueResourceFiles(section);
                return true;
            case "RESOURCELIST":
                // Source-X RES_RESOURCELIST: names usable by DEFLIST etc.
                foreach (var key in section.Keys)
                {
                    string name = VerbatimLine(key).Trim();
                    if (name.Length > 0)
                        _resourceListNames.Add(name);
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>[FAME]/[KARMA] title lists — one title per line; a line starting
    /// with '&lt;' is an intentionally empty slot (Source-X RES_FAME/RES_KARMA).</summary>
    private void LoadTitleLines(ScriptSection section, List<string> target)
    {
        target.Clear();
        foreach (var key in section.Keys)
        {
            string line = VerbatimLine(key).Trim();
            target.Add(line.StartsWith('<') ? "" : line);
        }
    }

    /// <summary>[NOTOTITLES]: first line = karma thresholds, second line = fame
    /// thresholds, remaining lines = (karma+1)*(fame+1) titles ("male,female").</summary>
    private void LoadNotoTitles(ScriptSection section)
    {
        _notoKarmaLevels.Clear();
        _notoFameLevels.Clear();
        _notoTitles.Clear();

        static void ParseLevels(string line, List<int> target)
        {
            // Source-X Str_ParseCmds default separators: "=, \t"
            foreach (var tok in line.Split([' ', ',', '\t', '='], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(tok, out int v))
                    target.Add(v);
            }
        }

        int lineIdx = 0;
        foreach (var key in section.Keys)
        {
            string line = VerbatimLine(key).Trim();
            if (lineIdx == 0)
                ParseLevels(line, _notoKarmaLevels);
            else if (lineIdx == 1)
                ParseLevels(line, _notoFameLevels);
            else
                _notoTitles.Add(line.StartsWith('<') ? "" : line);
            lineIdx++;
        }

        int expected = (_notoKarmaLevels.Count + 1) * (_notoFameLevels.Count + 1);
        if (_notoTitles.Count > 0 && _notoTitles.Count != expected)
            _logger.LogWarning("Expected {Expected} titles in NOTOTITLES section but found {Found}",
                expected, _notoTitles.Count);
    }

    /// <summary>[GLOBALS]/[WORLDVARS]: key=value pairs collected for the host to
    /// seed the world's global VARs at boot. A legacy "VAR." key prefix is
    /// stripped (Source-X RES_WORLDVARS backward compatibility).</summary>
    private void LoadScriptGlobalVars(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (string.IsNullOrEmpty(key.Key))
                continue;
            // Prefix match is case-sensitive like Source-X's strstr (though we
            // sanely anchor it to the start instead of replicating the
            // anywhere-match-then-skip-4-from-start quirk).
            string name = key.Key.StartsWith("VAR.", StringComparison.Ordinal)
                ? key.Key[4..]
                : key.Key;
            if (name.Length > 0)
                _scriptGlobalVars.Add(new KeyValuePair<string, string>(name, ScriptKey.StripQuotePair(key.Arg)));
        }
    }

    /// <summary>[LIST name]/[WORLDLISTS name]: element per line — Source-X
    /// CListDefCont::r_LoadVal reads the value part ("ELEM=x"); bare lines
    /// are accepted as the element itself.</summary>
    private void LoadScriptGlobalList(ScriptSection section)
    {
        string name = section.Argument.Split(' ', 2)[0].Trim();
        if (string.IsNullOrEmpty(name))
        {
            _logger.LogWarning("[LIST] section without a list name skipped");
            return;
        }

        // Source-X AddList returns the EXISTING list when the name was already
        // declared — a second [LIST name] block appends to it.
        int existing = _scriptGlobalLists.FindIndex(l =>
            l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        List<string> elements;
        if (existing >= 0)
        {
            elements = _scriptGlobalLists[existing].Elements;
        }
        else
        {
            elements = [];
            _scriptGlobalLists.Add((name, elements));
        }

        foreach (var key in section.Keys)
        {
            string element = ScriptKey.StripQuotePair(key.HasArg ? key.Arg : key.Key);
            if (!string.IsNullOrEmpty(element))
                elements.Add(element);
        }
    }

    /// <summary>[TYPEDEFS] bulk block: each line "NAME value" declares an item
    /// type name → numeric index (Source-X RES_TYPEDEFS).</summary>
    private void LoadTypeDefsBulk(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (string.IsNullOrEmpty(key.Key) || !key.HasArg)
                continue;
            if (ScriptKey.TryParseNumber(key.Arg.AsSpan(), out long val))
            {
                _defNames[key.Key] = new ResourceId(ResType.TypeDef, (int)val);
                // Also expose the numeric value through the DEF text table so
                // <DEF.t_xxx> and the numeric DefNameResolver (which only
                // accepts ResType.DefName ids) can resolve bulk-declared
                // type names — Source-X registers them in m_VarResDefs.
                _defTexts[key.Key] = val.ToString();
            }
            else
            {
                _logger.LogWarning("[TYPEDEFS] entry '{Name}' has non-numeric value '{Value}'", key.Key, key.Arg);
            }
        }
    }

    /// <summary>[RESOURCES] section found inside a script file — queue the
    /// referenced files for loading after the current pass, mirroring Source-X
    /// AddResourceFile/AddResourceDir (append to the end of the load list,
    /// dedup, refuse spheretables, resolve relative to the script root).</summary>
    private void QueueResourceFiles(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            string entry = VerbatimLine(key).Trim();
            if (entry.Length == 0)
                continue;

            bool isDirectory = entry.EndsWith('/') || entry.EndsWith('\\');
            if (isDirectory)
            {
                string dirPath = Path.IsPathRooted(entry) ? entry : Path.Combine(ScpBaseDir, entry);
                if (!Directory.Exists(dirPath))
                {
                    _logger.LogWarning("[RESOURCES] directory not found: {Dir}", entry);
                    continue;
                }
                // Source-X AddResourceDir sorts by case-SENSITIVE filename.
                foreach (var scp in Directory.EnumerateFiles(dirPath, "*.scp", SearchOption.TopDirectoryOnly)
                             .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    QueueSingleResourceFile(scp);
                continue;
            }

            QueueSingleResourceFile(entry);
        }
    }

    private void QueueSingleResourceFile(string entry)
    {
        // Source-X AddResourceFile explicitly refuses to re-add spheretables*.
        string title = Path.GetFileNameWithoutExtension(entry);
        if (title.StartsWith("spheretables", StringComparison.OrdinalIgnoreCase))
            return;

        if (!Path.HasExtension(entry))
            entry += ".scp";

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(ScpBaseDir, entry));
        }
        catch
        {
            _logger.LogWarning("[RESOURCES] invalid path skipped: {Entry}", entry);
            return;
        }

        // Only allow files under the script root (same guard as the manifest).
        if (!string.IsNullOrEmpty(ScpBaseDir))
        {
            string root = Path.GetFullPath(ScpBaseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[RESOURCES] entry outside script root skipped: {Entry}", entry);
                return;
            }
        }

        // Source-X FindResourceFile matches titles, not paths — a file with
        // the same basename anywhere counts as already scheduled.
        if (_knownResourceFiles.Contains(full) ||
            _knownResourceTitles.Contains(Path.GetFileName(full)))
            return;

        if (!File.Exists(full))
        {
            _logger.LogWarning("[RESOURCES] file not found: {Entry}", entry);
            return;
        }

        _knownResourceFiles.Add(full);
        _knownResourceTitles.Add(Path.GetFileName(full));
        _pendingResourceFiles.Add(full);
    }

    private void LoadDefNames(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (!string.IsNullOrEmpty(key.Key) && key.HasArg)
            {
                // Source-X reads the value via GetArgStr → surrounding quote
                // pair stripped, so DEFNAME foo "bar" stores bar and a quoted
                // numeric ("5") still parses as a number.
                string value = ScriptKey.StripQuotePair(key.Arg);
                if (ScriptKey.TryParseNumber(value.AsSpan(), out long val))
                {
                    _defNames[key.Key] = new ResourceId(ResType.DefName, (int)val);
                }
                else
                {
                    _defTexts[key.Key] = value;
                }
            }
        }
    }

    private void LoadDefMessages(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Key))
                continue;
            _defMessages[key.Key.Trim()] = key.Arg ?? "";
        }
    }

    private int ParseResourceIndex(string arg, ResType resType)
    {
        if (string.IsNullOrEmpty(arg))
            return -1;

        // Numeric ID types (ITEMDEF, CHARDEF, SPELL, SKILL, MULTIDEF) use the
        // legacy script number rule (reference Exp_GetVal): "0x.." and
        // leading-zero forms are hex, anything else is decimal. Real packs
        // write itemdef/chardef ids with a leading zero ("0eed") and
        // skill/spell ids as plain decimal ("[SKILL 40]", "[SPELL 44]");
        // parsing everything as hex shifted every skill/spell definition
        // with index >= 10 onto the wrong slot.
        if (IsNumericIdType(resType))
        {
            var cleanName = arg.Split(' ', 2)[0].Trim();
            var span = cleanName.AsSpan();

            bool isHex = false;
            if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
            {
                span = span[2..];
                isHex = true;
            }
            else if (span.Length > 1 && span[0] == '0')
            {
                isHex = true;
            }

            if (isHex)
            {
                if (long.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
                    return (int)hexVal;
            }
            else if (long.TryParse(span, out long decVal))
            {
                return (int)decVal;
            }

            // Try as DEFNAME for numeric types too (e.g. [CHARDEF c_guard] where c_guard was in DEFNAME)
            if (_defNames.TryGetValue(cleanName, out var rid))
                return rid.Index;

            // For CHARDEF/ITEMDEF with string names, auto-assign an index and register
            if (!string.IsNullOrEmpty(cleanName))
            {
                int hash = GenerateStringHash(cleanName, resType);
                _defNames[cleanName] = new ResourceId(resType, hash);
                return hash;
            }

            _logger.LogWarning("Cannot resolve resource index '{Arg}' for {Type}", arg, resType);
            return -1;
        }

        // For string-named types (FUNCTION, DIALOG, EVENTS, TYPEDEF, etc.)
        // Take only the first word as the name (e.g. "r_default_grass t_grass" → "r_default_grass")
        string name = arg.Split(' ', 2)[0].Trim();
        if (string.IsNullOrEmpty(name))
            return -1;

        // If already registered, return existing
        if (_defNames.TryGetValue(name, out var existingRid) && existingRid.Type == resType)
            return existingRid.Index;

        // Generate stable hash from name + type
        int index = GenerateStringHash(name, resType);
        _defNames[name] = new ResourceId(resType, index);
        return index;
    }

    /// <summary>
    /// Generate a stable hash for a string name, packed into 24-bit range (0x000001-0xFFFFFF).
    /// Uses FNV-1a for good distribution and collision avoidance.
    /// </summary>
    private static int GenerateStringHash(string name, ResType resType)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)resType) * 16777619u;
            foreach (char c in name)
            {
                hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
            }
            // Fit into 24-bit positive range (ResourceId.Index max), avoid 0
            int result = (int)(hash & 0x00FFFFFF);
            return result == 0 ? 1 : result;
        }
    }

    // --- Public accessors ---

    public ResourceLink? GetResource(ResourceId rid) => _resources.Get(rid);

    public ResourceLink? GetResource(ResType type, int index) =>
        _resources.Get(new ResourceId(type, index));

    public ResourceId ResolveDefName(string name)
    {
        return _defNames.TryGetValue(name, out var rid) ? rid : ResourceId.Invalid;
    }

    /// <summary>Collect the suffix of every [FUNCTION] defname starting with
    /// <paramref name="prefix"/> (case-insensitive) into <paramref name="output"/>.
    /// Lets dispatch hot paths precompute which f_onchar_*/f_onitem_* fallbacks
    /// exist instead of building and resolving a candidate name per trigger fire.</summary>
    public void CollectFunctionDefNameSuffixes(string prefix, ISet<string> output)
    {
        foreach (var kv in _defNames)
        {
            if (kv.Value.Type == ResType.Function &&
                kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                output.Add(kv.Key[prefix.Length..]);
        }
    }

    /// <summary>Resolve a numeric DEFNAME constant (e.g. a can_flags MT_* name)
    /// to its script-defined integer value. Numeric defnames are stored with the
    /// value in the ResourceId index by <see cref="LoadDefNames"/>, so we read it
    /// back here. Returns false for unknown names and for string-valued defnames.</summary>
    public bool TryResolveDefNameValue(string name, out long value)
    {
        if (!string.IsNullOrEmpty(name) &&
            _defNames.TryGetValue(name, out var rid) && rid.Type == ResType.DefName)
        {
            value = rid.Index;
            return true;
        }
        value = 0;
        return false;
    }

    public bool RegisterDefName(string name, ResourceId rid)
    {
        if (_defNames.ContainsKey(name))
            return false;
        _defNames[name] = rid;
        return true;
    }

    public void ReplaceResource(ResourceId rid, ResourceLink newLink)
    {
        _resources.Replace(rid, newLink);
        if (!string.IsNullOrEmpty(newLink.DefName))
            _defNames[newLink.DefName] = rid;
    }

    private static readonly Random _rng = new();

    /// <summary>
    /// Get a random name from a [NAMES xxx] section.
    /// Used to resolve #NAMES_HUMANMALE etc. in NPC names.
    /// </summary>
    public string? GetRandomName(string namesId)
    {
        // NAMES sections use string-hashed IDs
        int hash = GenerateStringHash(namesId, ResType.Names);
        var link = _resources.Get(new ResourceId(ResType.Names, hash));
        if (link?.StoredKeys == null || link.StoredKeys.Count == 0)
            return null;

        // StoredKeys contains the name entries (first line might be count, skip it).
        // Use the raw line — multi-word names ("John Smith") would otherwise be
        // truncated at the Key/Arg separator.
        var names = new List<string>();
        foreach (var key in link.StoredKeys)
        {
            string entry = key.RawLine.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            // Skip numeric-only lines (count header)
            if (int.TryParse(entry, out _)) continue;
            names.Add(entry);
        }

        if (names.Count == 0) return null;
        return names[_rng.Next(names.Count)];
    }

    /// <summary>
    /// Resolve #NAMES_xxx placeholders in a string.
    /// e.g. "#NAMES_HUMANMALE the Banker" → "Aaron the Banker"
    /// </summary>
    public string ResolveNamesInString(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('#'))
            return input;

        var sb = new System.Text.StringBuilder(input.Length);
        int pos = 0;
        while (pos < input.Length)
        {
            int idx = input.IndexOf('#', pos);
            if (idx < 0)
            {
                sb.Append(input, pos, input.Length - pos);
                break;
            }

            sb.Append(input, pos, idx - pos);

            int end = idx + 1;
            while (end < input.Length && input[end] != ' ' && input[end] != ',')
                end++;

            string token = input[(idx + 1)..end];
            string? replacement = GetRandomName(token);
            if (replacement != null)
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(input, idx, end - idx);
            }
            pos = end;
        }

        return sb.ToString();
    }

    // --- Special-section data (Source-X CServerConfig tables) ---

    /// <summary>[OBSCENE] words. Matching mirrors Source-X IsObscene: each word
    /// is wrapped as *word* and wildcard-matched case-insensitively.</summary>
    public IReadOnlyList<string> ObsceneWords => _obsceneWords;

    public bool IsObscene(string text)
    {
        if (string.IsNullOrEmpty(text) || _obsceneWords.Count == 0)
            return false;
        foreach (var word in _obsceneWords)
        {
            if (ObsceneWildcardMatch("*" + word + "*", text))
                return true;
        }
        return false;
    }

    private static bool ObsceneWildcardMatch(string pattern, string input)
    {
        int p = 0, i = 0, starP = -1, starI = -1;
        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i])))
            { p++; i++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { starP = p++; starI = i; }
            else if (starP >= 0)
            { p = starP + 1; i = ++starI; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public IReadOnlyList<string> FameTitles => _fameTitles;
    public IReadOnlyList<string> KarmaTitles => _karmaTitles;
    public IReadOnlyList<int> NotoKarmaLevels => _notoKarmaLevels;
    public IReadOnlyList<int> NotoFameLevels => _notoFameLevels;
    public IReadOnlyList<string> NotoTitles => _notoTitles;

    /// <summary>Source-X CChar::Noto_GetLevel + CServerConfig::GetNotoTitle —
    /// map karma/fame to a title slot, honoring the "male,female" split.</summary>
    public string GetNotoTitle(int karma, int fame, bool female)
    {
        int i = 0;
        while (i < _notoKarmaLevels.Count && karma < _notoKarmaLevels[i]) i++;
        int j = 0;
        while (j < _notoFameLevels.Count && fame > _notoFameLevels[j]) j++;

        int level = i * (_notoFameLevels.Count + 1) + j;
        if (level < 0 || level >= _notoTitles.Count)
            return "";

        string title = _notoTitles[level];
        int comma = title.IndexOf(',');
        if (comma < 0)
            return title;
        return female ? title[(comma + 1)..] : title[..comma];
    }

    /// <summary>[RUNES] magic rune words, indexed by letter (Source-X GetRune).</summary>
    public IReadOnlyList<string> Runes => _runes;

    public string GetRune(char ch)
    {
        int index = char.ToUpperInvariant(ch) - 'A';
        if (index < 0 || index >= _runes.Count)
            return "?";
        return _runes[index];
    }

    /// <summary>Script-declared global VARs from [GLOBALS]/[WORLDVARS] — the
    /// host applies these to the world at boot (before the save loads so a
    /// saved value wins).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ScriptGlobalVars => _scriptGlobalVars;

    /// <summary>Script-declared global LISTs from [LIST name]/[WORLDLISTS name].</summary>
    public IReadOnlyList<(string Name, List<string> Elements)> ScriptGlobalLists => _scriptGlobalLists;

    /// <summary>[RESOURCELIST] entries (Source-X m_ResourceList, used by DEFLIST).</summary>
    public IReadOnlyList<string> ResourceListNames => _resourceListNames;

    /// <summary>Mark a file as already scheduled for loading so a nested
    /// [RESOURCES] reference to it is not queued twice. The host registers the
    /// full manifest before loading starts.</summary>
    public void RegisterKnownResourceFile(string path)
    {
        try
        {
            _knownResourceFiles.Add(Path.GetFullPath(path));
            _knownResourceTitles.Add(Path.GetFileName(path));
        }
        catch { /* invalid path */ }
    }

    /// <summary>Files queued by nested [RESOURCES] sections, in discovery
    /// order. Draining clears the queue; loading a drained file may queue more.</summary>
    public IReadOnlyList<string> PendingResourceFiles => _pendingResourceFiles;

    public List<string> DrainPendingResourceFiles()
    {
        var drained = new List<string>(_pendingResourceFiles);
        _pendingResourceFiles.Clear();
        return drained;
    }

    /// <summary>Fetch a stored [BOOK name page] section. Pages are stored with
    /// the page folded into the identity token ("name_page"); the pageless
    /// header form is the title section.</summary>
    public ResourceLink? GetBookSection(string bookName, int page)
    {
        if (string.IsNullOrEmpty(bookName))
            return null;
        var rid = ResolveDefName($"{bookName}_{page}");
        if (!rid.IsValid || rid.Type != ResType.Book)
        {
            if (page > 0)
                return null;
            // pageless header form = the title section
            rid = ResolveDefName(bookName);
            if (!rid.IsValid || rid.Type != ResType.Book)
                return null;
        }
        return _resources.Get(rid);
    }

    public IEnumerable<ResourceLink> GetAllResources() => _resources.GetAll();
    public int ResourceCount => _resources.Count;
    public int DefNameCount => _defNames.Count;
    public bool TryGetDefMessage(string key, out string value) => _defMessages.TryGetValue(key, out value!);
    public IReadOnlyDictionary<string, string> GetAllDefMessages() => _defMessages;
    public bool TryGetDefValue(string key, out string value) => _defTexts.TryGetValue(key, out value!);

    public List<string> GetDialogTextLines(string dialogId) =>
        _dialogTextCache.TryGetValue(dialogId, out var entry) ? entry.Lines : [];

    private void CacheDialogSection(string[] argParts, string filePath, ScriptSection section)
    {
        if (argParts.Length == 0) return;
        string id = argParts[0].Trim();
        if (id.Length == 0) return;
        if (argParts.Length >= 2 && argParts[1].Equals("BUTTON", StringComparison.OrdinalIgnoreCase))
            _dialogButtonCache[id] = (filePath, section);
        else if (argParts.Length == 1)
            _dialogLayoutCache[id] = (filePath, section);
    }

    private void CacheMenuSection(string rawArg, string filePath, ScriptSection section)
    {
        string name = rawArg.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "";
        if (name.Length > 0)
            _menuSectionCache[name] = (filePath, section);
    }

    /// <summary>O(1) lookup of the retained [DIALOG id] layout section — the
    /// per-open full-pack file scan this replaces was a ~400ms stall.</summary>
    public bool TryGetDialogLayout(string dialogId, out ScriptSection section)
    {
        if (_dialogLayoutCache.TryGetValue(dialogId, out var entry))
        {
            section = entry.Section;
            return true;
        }
        section = null!;
        return false;
    }

    /// <summary>O(1) lookup of the retained [DIALOG id BUTTON] section.</summary>
    public bool TryGetDialogButton(string dialogId, out ScriptSection section)
    {
        if (_dialogButtonCache.TryGetValue(dialogId, out var entry))
        {
            section = entry.Section;
            return true;
        }
        section = null!;
        return false;
    }

    /// <summary>O(1) lookup of the retained [MENU name] section.</summary>
    public bool TryGetMenuSection(string menuDefname, out ScriptSection section)
    {
        if (_menuSectionCache.TryGetValue(menuDefname, out var entry))
        {
            section = entry.Section;
            return true;
        }
        section = null!;
        return false;
    }

    public IReadOnlyList<ResourceScript> ScriptFiles => _scriptFiles;

    /// <summary>
    /// Source-X merges every [PLEVEL n] block into the command list for that
    /// level. Keeping the original ScriptKey objects also preserves source
    /// locations for diagnostics and selective resync.
    /// </summary>
    public IEnumerable<(int Level, IReadOnlyList<ScriptKey> Commands)> GetPlevelCommandSections()
    {
        foreach (var entry in _plevelCommands.OrderBy(pair => pair.Key))
            yield return (entry.Key, entry.Value);
    }

    /// <summary>
    /// Log a summary of loaded resources by type.
    /// </summary>
    public void LogResourceSummary()
    {
        var counts = new Dictionary<ResType, int>();
        foreach (var link in _resources.GetAll())
        {
            var t = link.Id.Type;
            counts.TryGetValue(t, out int c);
            counts[t] = c + 1;
        }

        _logger.LogInformation("Resource summary: {Total} resources, {DefNames} defnames",
            _resources.Count, _defNames.Count);
        foreach (var (type, count) in counts.OrderByDescending(x => x.Value))
        {
            _logger.LogInformation("  {Type}: {Count}", type, count);
        }
    }

    /// <summary>
    /// ReSync: reload all script files that changed on disk since last load.
    /// Maps to CServerConfig::Resync in Source-X.
    /// Returns the number of files reloaded.
    /// </summary>
    public int Resync()
    {
        int reloaded = 0;
        // Important: clear ScriptFile static cache BEFORE reopening files.
        // Otherwise ReSync may parse stale in-memory lines and miss
        // function/section renames (e.g. [FUNCTION spawn] -> spawn2).
        ScriptFile.ClearFileCache();

        foreach (var script in _scriptFiles)
        {
            if (!script.NeedsReSync())
                continue;

            _logger.LogInformation("ReSync: reloading {File}", script.FilePath);

            try
            {
                var file = script.Open();
                try
                {
                    PurgeResourcesFromFile(script.FilePath);
                    ReloadResourcesFromFile(file, script.FilePath);
                    reloaded++;
                }
                finally
                {
                    script.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReSync failed for {File}: {Error}", script.FilePath, ex.Message);
            }
        }

        return reloaded;
    }

    /// <summary>
    /// Full resync: reload ALL script files regardless of modification time.
    /// </summary>
    public int ResyncAll()
    {
        int reloaded = 0;
        _dialogTextCache.Clear();
        _dialogLayoutCache.Clear();
        _dialogButtonCache.Clear();
        _menuSectionCache.Clear();
        // Force fresh disk reads for all script files.
        ScriptFile.ClearFileCache();

        foreach (var script in _scriptFiles)
        {
            _logger.LogInformation("ReSync: reloading {File}", script.FilePath);

            try
            {
                var file = script.Open();
                try
                {
                    PurgeResourcesFromFile(script.FilePath);
                    ReloadResourcesFromFile(file, script.FilePath);
                    reloaded++;
                }
                finally
                {
                    script.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReSync failed for {File}: {Error}", script.FilePath, ex.Message);
            }
        }

        return reloaded;
    }

    private void PurgeResourcesFromFile(string filePath)
    {
        string normalized = Path.GetFullPath(filePath);
        foreach (int level in _plevelCommands.Keys.ToArray())
        {
            _plevelCommands[level].RemoveAll(key =>
                !string.IsNullOrEmpty(key.SourceFile) &&
                string.Equals(Path.GetFullPath(key.SourceFile), normalized, StringComparison.OrdinalIgnoreCase));
            if (_plevelCommands[level].Count == 0)
                _plevelCommands.Remove(level);
        }

        var toRemove = _resources.GetAll()
            .Where(link => !string.IsNullOrEmpty(link.ScriptFilePath) &&
                           string.Equals(Path.GetFullPath(link.ScriptFilePath!), normalized,
                               StringComparison.OrdinalIgnoreCase))
            .Select(link => link.Id)
            .ToList();

        if (toRemove.Count == 0)
            return;

        foreach (var rid in toRemove)
            _resources.Remove(rid);

        // Keep defname table in sync with removed resources so renamed/removed
        // sections disappear after Resync (e.g. [FUNCTION spawn] -> spawn2).
        var removedSet = toRemove.ToHashSet();
        var staleDefNames = _defNames
            .Where(kvp => removedSet.Contains(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleDefNames)
            _defNames.Remove(key);

        _teleporters.RemoveAll(t => string.Equals(Path.GetFullPath(t.FilePath), normalized,
            StringComparison.OrdinalIgnoreCase));

        var staleDialogs = _dialogTextCache
            .Where(kvp => string.Equals(Path.GetFullPath(kvp.Value.FilePath), normalized,
                StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleDialogs)
            _dialogTextCache.Remove(key);

        PurgeSectionCache(_dialogLayoutCache, normalized);
        PurgeSectionCache(_dialogButtonCache, normalized);
        PurgeSectionCache(_menuSectionCache, normalized);
    }

    private static void PurgeSectionCache(
        Dictionary<string, (string FilePath, ScriptSection Section)> cache, string normalizedPath)
    {
        var stale = cache
            .Where(kvp => string.Equals(Path.GetFullPath(kvp.Value.FilePath), normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in stale)
            cache.Remove(key);
    }

    private void ReloadResourcesFromFile(ScriptFile file, string filePath)
    {
        var sections = file.ReadAllSections();

        foreach (var section in sections)
        {
            if (TryLoadSpecialSection(section))
                continue;

            ResType resType = SectionToResType(section.Name);

            if (resType == ResType.DefName)
            {
                LoadDefNames(section);
                continue;
            }

            if (resType == ResType.WorldScript)
            {
                string sectionUpper = section.Name.ToUpperInvariant();
                if (sectionUpper == "TELEPORTERS")
                    LoadTeleporters(section, filePath);
                else if (sectionUpper == "STARTS")
                    LoadStarts(section);
                else if (sectionUpper is "STARTSGOLD" or "STARTGOLD")
                    LoadStartGold(section);
                else if (sectionUpper == "MOONGATES")
                    LoadMoongates(section);
                continue;
            }

            if (resType == ResType.Unknown || resType == ResType.Sphere ||
                resType == ResType.ServerConfig || resType == ResType.ResourceList ||
                resType == ResType.Comment)
                continue;

            string rawArg = section.Argument;

            // NEWBIE race variants: "[NEWBIE MALE_DEFAULT ELF]" is a separate
            // template from "[NEWBIE MALE_DEFAULT]". Keying both by the first
            // token made the (usually empty) ELF/GARG variants overwrite the
            // human base section — fresh characters spawned naked because
            // MALE_DEFAULT resolved to the empty GARG section. Fold the race
            // suffix into the identity token instead. BOOK pages likewise.
            if (resType is ResType.NewBie or ResType.Book && rawArg.Contains(' '))
                rawArg = rawArg.Trim().Replace('\t', '_').Replace(' ', '_');

            if (resType == ResType.Dialog)
            {
                var argParts = rawArg.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (argParts.Length >= 2 && argParts[1].Equals("TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    var textLines = new List<string>();
                    foreach (var key in section.Keys)
                        textLines.Add(key.RawLine.TrimEnd());
                    _dialogTextCache[argParts[0]] = (filePath, textLines);
                    continue;
                }
                CacheDialogSection(argParts, filePath, section);
            }
            else if (resType == ResType.Menu)
            {
                CacheMenuSection(rawArg, filePath, section);
            }

            int index = ParseResourceIndex(rawArg, resType);
            if (index < 0) continue;

            if (resType == ResType.PlevelCfg)
                LoadPlevelCommands(index, section);

            var rid = new ResourceId(resType, index);

            if (resType == ResType.Spawn)
            {
                var spawnDef = new SpawnGroupDef(rid)
                {
                    ScriptFilePath = filePath,
                    ScriptLineNumber = section.Context.LineNumber
                };

                foreach (var key in section.Keys)
                    spawnDef.LoadFromKey(key.Key, key.Arg);

                string spawnName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(spawnName))
                {
                    spawnDef.DefName = spawnName;
                    _defNames[spawnName] = rid;
                }

                _resources.Replace(rid, spawnDef);

                if (!string.IsNullOrEmpty(spawnDef.DefName))
                    _defNames[spawnDef.DefName] = rid;
                continue;
            }

            var link = new ResourceLink(rid)
            {
                ScriptFilePath = filePath,
                ScriptLineNumber = section.Context.LineNumber,
                HeaderArgument = rawArg
            };

            link.ScanSection(section, retainKeys: IsDefinitionType(resType));

            if (!IsNumericIdType(resType) && !string.IsNullOrEmpty(rawArg))
            {
                string defName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(defName))
                {
                    link.DefName = defName;
                    _defNames[defName] = rid;
                }
            }

            _resources.Replace(rid, link);

            if (!string.IsNullOrEmpty(link.DefName))
                _defNames[link.DefName] = rid;
        }
    }

    private void LoadTeleporters(ScriptSection section, string filePath)
    {
        foreach (var key in section.Keys)
        {
            // Format: srcX,srcY,srcZ,srcMap=destX,destY,destZ,destMap=name
            // Parse from the raw line — the Key/Arg split may have broken the
            // coordinate list at a comma.
            var segments = key.RawLine.Split('=', 3);
            if (segments.Length < 2) continue;

            var src = ParseTeleportPoint(segments[0]);
            if (src.X == 0 && src.Y == 0) continue;

            var dest = ParseTeleportPoint(segments[1]);
            if (dest.X == 0 && dest.Y == 0) continue;

            string name = segments.Length >= 3 ? segments[2].Trim() : "";
            _teleporters.Add(new TeleporterEntry(src, dest, name, filePath));
        }

        _logger.LogInformation("Loaded {Count} teleporters", _teleporters.Count);
    }

    private void LoadPlevelCommands(int level, ScriptSection section)
    {
        if (!_plevelCommands.TryGetValue(level, out var commands))
        {
            commands = [];
            _plevelCommands[level] = commands;
        }

        foreach (var key in section.Keys)
        {
            if (!commands.Any(existing => existing.Key.Equals(key.Key, StringComparison.OrdinalIgnoreCase)))
                commands.Add(key);
        }
    }

    private void LoadStarts(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            // "Name=x,y,z,map" or a bare "x,y,z,map" line — split on '=' from
            // the raw line (comma is a Key/Arg separator now).
            ScriptKey.TrySplitOnEquals(key.RawLine, out string name, out string value);
            var point = ParseTeleportPoint(value.Trim());
            if (point.X == 0 && point.Y == 0)
                continue;
            _starts.Add(new StartEntry(name.Trim(), point));
        }
        _logger.LogInformation("Loaded {Count} start locations", _starts.Count);
    }

    /// <summary>Stat advance-rate curves from [ADVANCE] (reference
    /// RES_ADVANCE): STR/DEX/INT, each a "uses per +1" curve across the
    /// stat's progress toward the skill's STAT_* target. Index order
    /// follows the engine convention 0=Str, 1=Dex, 2=Int.</summary>
    public Definitions.ValueCurve[] StatAdvance { get; } =
        [Definitions.ValueCurve.Empty, Definitions.ValueCurve.Empty, Definitions.ValueCurve.Empty];

    private void LoadStatAdvance(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            int idx = key.Key.Trim().ToUpperInvariant() switch
            {
                "STR" => 0,
                "DEX" => 1,
                "INT" => 2,
                _ => -1,
            };
            if (idx >= 0)
                StatAdvance[idx] = Definitions.ValueCurve.Parse(key.Arg);
        }
    }

    private void LoadStartGold(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            // [STARTSGOLD] entries are bare amounts ("10000"), parallel to the
            // [STARTS] city list. (A "name=amount" form is still honoured for
            // named pools.) Parse from the raw line so bare entries survive
            // the Key/Arg split.
            bool named = ScriptKey.TrySplitOnEquals(key.RawLine, out string name, out string amountText);
            if (!named) name = amountText;
            if (int.TryParse(amountText.Trim(), out int amount))
                _startGold.Add(new StartGoldEntry(name.Trim(), amount));
        }
        _logger.LogInformation("Loaded {Count} start gold entries", _startGold.Count);
    }

    private void LoadMoongates(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            // "Name=x,y,z,map" or bare "x,y,z,map" — same raw-line rule as
            // [STARTS].
            ScriptKey.TrySplitOnEquals(key.RawLine, out string name, out string value);
            var point = ParseTeleportPoint(value.Trim());
            if (point.X == 0 && point.Y == 0)
                continue;
            _moongates.Add(new MoongateEntry(name.Trim(), point));
        }
        _logger.LogInformation("Loaded {Count} moongates", _moongates.Count);
    }

    private static Point3D ParseTeleportPoint(string s)
    {
        var parts = s.Split(',');
        if (parts.Length < 3) return default;
        if (!short.TryParse(parts[0].Trim(), out short x)) return default;
        if (!short.TryParse(parts[1].Trim(), out short y)) return default;
        if (!sbyte.TryParse(parts[2].Trim(), out sbyte z)) return default;
        byte map = 0;
        if (parts.Length >= 4)
            byte.TryParse(parts[3].Trim(), out map);
        return new Point3D(x, y, z, map);
    }
}
