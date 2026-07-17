using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>
/// Loads game definitions from parsed script resources.
/// Maps to CServerConfig resource loading in Source-X.
/// Reads [SPELL], [SKILLDEF], [ITEMDEF], [CHARDEF] sections and populates registries.
/// </summary>
public sealed class DefinitionLoader
{
    /// <summary>Optional diagnostic sink — wired by host (Program.cs)
    /// so script-load tracing reaches the central logger without
    /// every loader needing an ILogger field.</summary>
    public static Action<string>? Diagnostic { get; set; }

    private readonly ResourceHolder _resources;
    private readonly SpellRegistry _spells;

    // Definition registries — accessible for runtime lookups
    private static readonly Dictionary<int, CharDef> _charDefs = new();
    private static readonly Dictionary<int, ItemDef> _itemDefs = new();
    private static readonly Dictionary<int, SkillClassDef> _skillClassDefs = new();
    private static readonly Dictionary<int, RegionResourceDef> _regionResourceDefs = new();
    private static readonly Dictionary<int, RegionTypeDef> _regionTypeDefs = new();
    private static readonly Dictionary<int, SkillDef> _skillDefs = new();
    private static readonly Dictionary<int, TemplateDef> _templateDefs = new();

    /// <summary>Look up a TEMPLATE by numeric resource index.</summary>
    public static TemplateDef? GetTemplateDef(int id) => _templateDefs.GetValueOrDefault(id);

    /// <summary>Look up a TEMPLATE by defname. Used by the NPC equip
    /// pipeline when resolving random_* pools.</summary>
    public static TemplateDef? GetTemplateDef(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname) || _resourcesStatic == null)
            return null;
        var rid = _resourcesStatic.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.Template
            ? GetTemplateDef(rid.Index)
            : null;
    }

    public int SpellsLoaded { get; private set; }
    public int ItemDefsLoaded { get; private set; }
    public int CharDefsLoaded { get; private set; }
    public int RegionResourceDefsLoaded { get; private set; }
    public int RegionTypeDefsLoaded { get; private set; }
    public int SkillDefsLoaded { get; private set; }

    public DefinitionLoader(ResourceHolder resources, SpellRegistry spells)
    {
        _resources = resources;
        _spells = spells;
    }

    public static CharDef? GetCharDef(int baseId) => _charDefs.GetValueOrDefault(baseId);
    public static ItemDef? GetItemDef(int baseId) => _itemDefs.GetValueOrDefault(baseId);
    public static IEnumerable<KeyValuePair<int, CharDef>> AllCharDefs => _charDefs;
    public static IEnumerable<KeyValuePair<int, ItemDef>> AllItemDefs => _itemDefs;
    public static RegionResourceDef? GetRegionResourceDef(int id) => _regionResourceDefs.GetValueOrDefault(id);
    public static RegionTypeDef? GetRegionTypeDef(int id) => _regionTypeDefs.GetValueOrDefault(id);

    /// <summary>Find first REGIONTYPE matching an ItemTypeFilter (e.g. "t_rock", "t_water", "t_tree").</summary>
    public static RegionTypeDef? FindRegionTypeByFilter(string typeFilter)
    {
        foreach (var kv in _regionTypeDefs)
        {
            if (kv.Value.ItemTypeFilter != null &&
                kv.Value.ItemTypeFilter.Equals(typeFilter, StringComparison.OrdinalIgnoreCase) &&
                kv.Value.Resources.Count > 0)
                return kv.Value;
        }
        return null;
    }
    public static SkillDef? GetSkillDef(int skillIndex) => _skillDefs.GetValueOrDefault(skillIndex);

    /// <summary>Register a skill def directly. Test harnesses use this to seed
    /// ADV_RATE curves — skill gain follows the curve strictly (no curve = no
    /// gain, Source-X GetChancePercent), so gain tests must provide one.</summary>
    public static void SetSkillDef(int skillIndex, SkillDef def) => _skillDefs[skillIndex] = def;
    public static SkillDef? GetSkillDef(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var rid = _resourcesStatic?.ResolveDefName(name.Trim()) ?? ResourceId.Invalid;
        return rid.IsValid && rid.Type == ResType.SkillDef ? GetSkillDef(rid.Index) : null;
    }

    /// <summary>Resolve #NAMES_xxx placeholders in a string using loaded [NAMES] resources.</summary>
    public static string ResolveNames(string input) =>
        _resourcesStatic?.ResolveNamesInString(input) ?? input;

    /// <summary>Resolve a defname text value (e.g. "colors_skin" → "{1002 1058}").</summary>
    public static bool TryGetDefValue(string name, out string value)
    {
        value = "";
        return _resourcesStatic?.TryGetDefValue(name, out value) ?? false;
    }
    public static SkillClassDef? GetSkillClassDef(int id) => _skillClassDefs.GetValueOrDefault(id);
    public static SkillClassDef? GetSkillClassDef(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var rid = _resourcesStatic?.ResolveDefName(name.Trim()) ?? ResourceId.Invalid;
        return rid.IsValid && rid.Type == ResType.SkillClass ? GetSkillClassDef(rid.Index) : null;
    }

    private static ResourceHolder? _resourcesStatic;

    /// <summary>Static accessor used by systems that only have access
    /// to the DefinitionLoader surface (e.g. Character template
    /// resolution without a GameClient reference).</summary>
    public static ResourceHolder? StaticResources => _resourcesStatic;

    /// <summary>Clear process-wide definition registries between isolated unit
    /// tests. Production reloads continue to use <see cref="LoadAll"/>.</summary>
    internal static void ResetForTests()
    {
        _charDefs.Clear();
        _itemDefs.Clear();
        _skillClassDefs.Clear();
        _regionResourceDefs.Clear();
        _regionTypeDefs.Clear();
        _skillDefs.Clear();
        _templateDefs.Clear();
        _resourcesStatic = null;
        Diagnostic = null;
    }

    /// <summary>Load all definitions from parsed resources.</summary>
    public void LoadAll()
    {
        _resourcesStatic = _resources;
        SphereNet.Scripting.Definitions.UnknownKeyDiagnostics.Clear();
        ClearRegistries();
        _spells.Clear();
        ResetCounters();

        foreach (var link in _resources.GetAllResources())
        {
            switch (link.Id.Type)
            {
                case ResType.SpellDef:
                    LoadSpellDef(link);
                    break;
                case ResType.ItemDef:
                    LoadItemDef(link);
                    break;
                case ResType.CharDef:
                    LoadCharDef(link);
                    break;
                case ResType.SkillClass:
                    LoadSkillClassDef(link);
                    break;
                case ResType.RegionResource:
                    LoadRegionResourceDef(link);
                    break;
                case ResType.RegionType:
                    LoadRegionTypeDef(link);
                    break;
                case ResType.SkillDef:
                    LoadSkillDef(link);
                    break;
                case ResType.Template:
                    LoadTemplateDef(link);
                    break;
            }
        }

        ResolveItemDefReferences();
        ResolveDupeItemInheritance();
        ResolveRegionResourceReapDefNames();

        Skills.SkillEngine.StatAdvCurves = _resources.StatAdvance;

        // Script-pack visibility: report the def keys no parser recognized —
        // they used to vanish silently, hiding real-pack property loss.
        if (SphereNet.Scripting.Definitions.UnknownKeyDiagnostics.TotalDropped > 0)
            Diagnostic?.Invoke(
                $"[defs] {SphereNet.Scripting.Definitions.UnknownKeyDiagnostics.TotalDropped} unrecognized def keys dropped; top: " +
                string.Join(", ", SphereNet.Scripting.Definitions.UnknownKeyDiagnostics.Summary(10)));
    }

    private void ResetCounters()
    {
        SpellsLoaded = 0;
        ItemDefsLoaded = 0;
        CharDefsLoaded = 0;
        RegionResourceDefsLoaded = 0;
        RegionTypeDefsLoaded = 0;
        SkillDefsLoaded = 0;
    }

    private static void ClearRegistries()
    {
        _charDefs.Clear();
        _itemDefs.Clear();
        _skillClassDefs.Clear();
        _regionResourceDefs.Clear();
        _regionTypeDefs.Clear();
        _skillDefs.Clear();
        _templateDefs.Clear();
    }

    private void ResolveRegionResourceReapDefNames()
    {
        foreach (var kv in _regionResourceDefs)
        {
            var def = kv.Value;
            if (def.Reap != 0 || string.IsNullOrEmpty(def.ReapRaw))
                continue;

            ushort dispId = TemplateEngine.ResolveDispId(_resources, def.ReapRaw);
            if (dispId != 0)
                def.Reap = dispId;
        }
    }

    /// <summary>Parse a <c>[TEMPLATE name]</c> block. Bodies use:
    /// <c>ID=itemdef</c> / <c>ITEMID=itemdef[,weight]</c> → weighted
    /// random pool; <c>ITEM=itemdef[,amount]</c> → sequential spawn list
    /// (used by VENDOR_S_* / VENDOR_B_* restock templates).</summary>
    private void LoadTemplateDef(ResourceLink link)
    {
        var def = new TemplateDef(link.Id);
        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0)
        {
            // Some script loaders only stash a position pointer and
            // expect callers to re-open the file. If StoredKeys is empty
            // the template body never reaches our parser, so the
            // TemplateDef stays empty and vendor restock yields nothing.
            // Log it so we can tell the two failure modes apart.
            if (!string.IsNullOrEmpty(link.DefName))
                Diagnostic?.Invoke(
                    $"[tpl_load] '{link.DefName}' StoredKeys empty; template will yield 0 entries");
            _templateDefs[link.Id.Index] = def;
            return;
        }

        bool insideTrigger = false;
        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (key.HasArg && key.Arg.StartsWith('@'))
                    insideTrigger = true;
                continue;
            }
            if (insideTrigger)
                continue;
            string upper = key.Key.ToUpperInvariant();
            if (upper == "ID" || upper == "ITEMID")
            {
                // ID=foo            → weight 1
                // ITEMID=foo,5      → weight 5
                var parts = key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                int weight = 1;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int w) && w > 0)
                    weight = w;
                def.RandomEntries.Add(new TemplateEntry { DefName = parts[0], Weight = weight });
            }
            else if (upper == "ITEM" || upper == "ITEMNEWBIE")
            {
                // ITEM=foo[,amount[,Rchance]] — Source-X CItem::CreateHeader.
                // The raw args are kept verbatim: loot templates carry "{600 750}"
                // dice amounts and "R5" 1-in-5 chance tokens that a plain int
                // parse silently dropped.
                var parts = key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                int amount = 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int a) && a > 0)
                    amount = a;
                def.ItemEntries.Add(new TemplateEntry
                {
                    DefName = parts[0],
                    Amount = amount,
                    RawArgs = parts.Length >= 2 ? parts[1..] : []
                });
            }
            else if (upper == "CONTAINER")
            {
                // CONTAINER=i_bag — following ITEM rows spawn inside this
                // container (Source-X ReadTemplate ITC_CONTAINER).
                var parts = key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                def.ItemEntries.Add(new TemplateEntry { DefName = parts[0], IsContainer = true });
            }
            else if (upper == "SELL" || upper == "BUY")
            {
                // Source-X parity: VENDOR_S_*/VENDOR_B_* vendor templates
                // store their stock as a series of "SELL=defname,{min max}"
                // (or "BUY=...") lines instead of plain ITEM=. The numeric
                // arg is "{lo hi}" — a Sphere dice expression. Strip the
                // braces so EnumerateSequential / PopulateVendorStock get
                // a clean "lo hi" pair, then take the upper bound as the
                // restock amount (matches CCharNPC::NPC_OnTrigRestock,
                // which spawns up to the high end of the dice range).
                var parts = key.Arg.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || string.IsNullOrEmpty(parts[0])) continue;
                int amount = 1;
                if (parts.Length >= 2)
                {
                    string raw = parts[1].Trim().TrimStart('{').TrimEnd('}').Trim();
                    var range = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (range.Length == 1 && int.TryParse(range[0], out int single) && single > 0)
                        amount = single;
                    else if (range.Length >= 2 && int.TryParse(range[^1], out int hi) && hi > 0)
                        amount = hi;
                }
                def.ItemEntries.Add(new TemplateEntry { DefName = parts[0], Amount = amount });
            }
            else if (upper == "DEFNAME")
            {
                // Some templates rename themselves — register alias.
                _resources.RegisterDefName(key.Arg.Trim(), link.Id);
            }
        }

        _templateDefs[link.Id.Index] = def;
    }

    private void LoadSkillClassDef(ResourceLink link)
    {
        var def = new SkillClassDef(link.Id);

        var keys = link.StoredKeys;
        if (keys != null && keys.Count > 0)
        {
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                    continue;
                def.LoadFromKey(key.Key, key.Arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _skillClassDefs[link.Id.Index] = def;
    }

    private void LoadCharDef(ResourceLink link)
    {
        var def = new CharDef(link.Id)
        {
            HeaderArgument = link.HeaderArgument
        };

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { CharDefsLoaded++; return; }

        // Walk the section, but stop at the first ON=@... trigger header so
        // the body keys (which belong to that trigger and run dynamically
        // via TriggerRunner) don't bleed into the static CharDef state.
        // Sphere scripts put NPC=brain_vendor inside ON=@Create — that line is
        // applied at spawn time by the trigger interpreter against the
        // Character instance, NOT against the shared CharDef template.
        bool insideTrigger = false;
        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (key.HasArg && key.Arg.StartsWith('@'))
                    insideTrigger = true;
                continue;
            }
            if (insideTrigger)
                continue;

            // CAN uses pipe-separated defnames (e.g. MT_WALK|MT_FLY|MT_FIRE_IMMUNE)
            if (key.Key.Equals("CAN", StringComparison.OrdinalIgnoreCase) && key.Arg.Contains('|'))
            {
                def.Can = (CanFlags)ResolvePipeFlags(key.Arg);
                continue;
            }

            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrEmpty(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);
        foreach (string alias in def.Aliases)
            _resources.RegisterDefName(alias, link.Id);

        if (_charDefs.TryGetValue(link.Id.Index, out var existing))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[chardef_collision] index={link.Id.Index:X} previous='{existing.DefName}' brain={existing.NpcBrain} new='{def.DefName}' brain={def.NpcBrain}");
        }

        _charDefs[link.Id.Index] = def;
        CharDefsLoaded++;
    }

    private void LoadItemDef(ResourceLink link)
    {
        var def = new ItemDef(link.Id);

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { ItemDefsLoaded++; return; }

        // Definition properties end at the first @trigger. Trigger bodies act
        // on item instances at runtime and must never mutate the shared base
        // definition (e.g. random NAME/DISPID assignments in @Create/@Timer).
        bool insideTrigger = false;
        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                if (key.HasArg && key.Arg.StartsWith('@'))
                    insideTrigger = true;
                continue;
            }
            if (insideTrigger)
                continue;

            // ID / DISPID accept both a numeric graphic ("0x0F6C") AND a
            // defname reference to another ITEMDEF ("i_moongate_blue") —
            // standard Sphere/Source-X form for "copy the graphic from
            // another template". ItemDef.LoadFromKey on its own only
            // parses numerics, so resolve the defname case here before
            // delegating and leave DispIndex at 0 only for truly unknown
            // values.
            if (key.Key.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
                key.Key.Equals("DISPID", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHex(key.Arg, out ushort idHex))
                {
                    def.DispIndex = idHex;
                }
                else
                {
                    var rid = _resources.ResolveDefName(key.Arg.Trim());
                    if (rid.IsValid && rid.Type == ResType.ItemDef)
                    {
                        def.DisplayIdRef = key.Arg.Trim();
                        // Two paths: referenced itemdef already loaded →
                        // inherit its DispIndex; or it's a forward/pending
                        // reference → fall back to its index (which for
                        // pure-graphic itemdefs [ITEMDEF 0f6c] equals the
                        // graphic hex). If the referenced def loaded later
                        // with a different DispIndex, GetItemDispId()
                        // resolves it at access time.
                        if (_itemDefs.TryGetValue(rid.Index, out var refDef) && refDef.DispIndex != 0)
                            def.DispIndex = refDef.DispIndex;
                        else
                            def.DispIndex = (ushort)rid.Index;
                    }
                }
                continue;
            }

            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrEmpty(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _itemDefs[link.Id.Index] = def;
        ItemDefsLoaded++;
    }

    private void LoadSpellDef(ResourceLink link)
    {
        var def = new Magic.SpellDef { Id = (SpellType)(ushort)link.Id.Index };

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) return;

        foreach (var key in keys)
        {
            switch (key.Key.ToUpperInvariant())
            {
                case "NAME": def.Name = key.Arg; break;
                case "FLAGS":
                    def.Flags = ParseSpellFlags(key.Arg);
                    break;
                case "MANAUSE":
                    if (ushort.TryParse(key.Arg, out ushort m)) def.ManaCost = m;
                    break;
                case "TITHINGUSE":
                    if (ushort.TryParse(key.Arg, out ushort ti)) def.TithingCost = ti;
                    break;
                case "SOUND":
                    if (int.TryParse(key.Arg, out int s))
                        def.Sound = s;
                    else
                    {
                        var srid = _resourcesStatic?.ResolveDefName(key.Arg.Trim()) ?? ResourceId.Invalid;
                        if (srid.IsValid) def.Sound = srid.Index;
                    }
                    break;
                case "RUNES":
                    def.Runes = key.Arg.StartsWith('.') ? key.Arg[1..] : key.Arg;
                    break;
                case "PROMPT_MSG": def.TargetPrompt = key.Arg; break;
                // RUNE_ITEM/SCROLL_ITEM/EFFECT_ID name a display graphic. A defname
                // must resolve through the ItemDef's DispIndex (DisplayIdRef chain),
                // NOT (ushort)rid.Index: a name-keyed [ITEMDEF i_rune_xxx] hashes to
                // a synthetic 24-bit index whose low 16 bits are garbage, so the raw
                // cast gave every necro/AOS spell a corrupt rune/scroll/fx graphic.
                case "EFFECT_ID":
                    if (TryParseHex(key.Arg, out ushort eid)) def.EffectId = eid;
                    else { uint g = ResolveItemReferenceValue(key.Arg.Trim()); if (g != 0) def.EffectId = (ushort)g; }
                    break;
                case "RUNE_ITEM":
                    if (TryParseHex(key.Arg, out ushort ri)) def.RuneItemId = ri;
                    else { uint g = ResolveItemReferenceValue(key.Arg.Trim()); if (g != 0) def.RuneItemId = (ushort)g; }
                    break;
                case "SCROLL_ITEM":
                    if (TryParseHex(key.Arg, out ushort si)) def.ScrollItemId = si;
                    else { uint g = ResolveItemReferenceValue(key.Arg.Trim()); if (g != 0) def.ScrollItemId = (ushort)g; }
                    break;
                case "CAST_TIME":
                {
                    // Curve in seconds across skill 0-100.0; stored as tenths.
                    // Single value = constant; "A,B" = endpoints.
                    var ctParts = key.Arg.Split(',', 2, StringSplitOptions.TrimEntries);
                    def.CastTimeBase = ParseSecondsToTenths(ctParts[0]);
                    def.CastTimeScale = ctParts.Length > 1 ? ParseSecondsToTenths(ctParts[1]) : 0;
                    break;
                }
                case "EFFECT":
                    ParseCurve(key.Arg, out int eb, out int es);
                    def.EffectBase = eb; def.EffectScale = es;
                    break;
                case "DURATION":
                    // Sphere script writes DURATION in seconds; internal
                    // storage is tenths of a second to match CAST_TIME
                    // (which is also scaled x10). Multiply after
                    // ParseCurve so "3*60" becomes 1800 tenths = 180s.
                    ParseCurve(key.Arg, out int db, out int ds);
                    def.DurationBase = db * 10; def.DurationScale = ds * 10;
                    break;
                case "INTERRUPT":
                {
                    // Per-mille curve with legacy fixed-point values
                    // ("100.0,100.0" → 1000,1000 = always disturbable).
                    var icParts = key.Arg.Split(',', 2, StringSplitOptions.TrimEntries);
                    def.InterruptBase = SphereNet.Scripting.Definitions.ValueCurve.ParseSphereNumber(icParts[0]);
                    def.InterruptScale = icParts.Length > 1
                        ? SphereNet.Scripting.Definitions.ValueCurve.ParseSphereNumber(icParts[1])
                        : 0;
                    break;
                }
                case "LAYER":
                    if (TryResolveByteValue(key.Arg, out byte ly)) def.Layer = (Layer)ly;
                    break;
                case "GROUP":
                    if (ulong.TryParse(key.Arg, out ulong gr)) def.Group = gr;
                    break;
                case "RESOURCES":
                    ParseReagentList(key.Arg, def.Reagents);
                    break;
                case "SKILLREQ":
                    ParseSkillReqList(key.Arg, def.SkillReq);
                    break;
            }
        }

        _spells.Register(def);
        SpellsLoaded++;
    }

    private void LoadRegionResourceDef(ResourceLink link)
    {
        var def = new RegionResourceDef(link.Id);

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { RegionResourceDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _regionResourceDefs[link.Id.Index] = def;
        RegionResourceDefsLoaded++;
    }

    private void LoadRegionTypeDef(ResourceLink link)
    {
        var def = new RegionTypeDef(link.Id);

        // Parse item type filter from header if present (e.g. [REGIONTYPE defname t_rock]).
        // DEFNAME inside the body can overwrite link.DefName, so use the original header.
        if (!string.IsNullOrWhiteSpace(link.HeaderArgument))
        {
            var headerParts = link.HeaderArgument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (headerParts.Length >= 2)
                def.ItemTypeFilter = headerParts[1].Trim();
        }

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { RegionTypeDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _regionTypeDefs[link.Id.Index] = def;
        RegionTypeDefsLoaded++;
    }

    private void LoadSkillDef(ResourceLink link)
    {
        var def = new SkillDef(link.Id);

        var keys = link.StoredKeys;
        if (keys != null && keys.Count > 0)
        {
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                    continue;
                def.LoadFromKey(key.Key, key.Arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _skillDefs[link.Id.Index] = def;
        SkillDefsLoaded++;
    }

    private static int ParseSecondsToTenths(string s)
    {
        if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double d))
            return (int)Math.Round(d * 10);
        return EvalCurveTerm(s) * 10;
    }

    private static void ParseCurve(string val, out int baseVal, out int scale)
    {
        baseVal = 0; scale = 0;
        var parts = val.Split(',');
        if (parts.Length >= 1) baseVal = EvalCurveTerm(parts[0]);
        if (parts.Length >= 2) scale = EvalCurveTerm(parts[1]);
    }

    /// <summary>Evaluate a single Sphere-script curve term like "180",
    /// "3*60.0", "1.5*60", "0.5". Supports integer/decimal literals and
    /// chained <c>*</c>/<c>/</c> operators. The return is truncated to
    /// int — the DURATION / EFFECT curves only need integer precision.
    /// Without this, <c>int.TryParse</c> rejected anything with a
    /// <c>*</c> or <c>.</c>, leaving DurationBase/Scale at 0 and every
    /// timed spell effect expired the same tick it was applied.</summary>
    private static int EvalCurveTerm(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return 0;
        double acc = 1.0;
        bool first = true;
        char op = '*';
        int i = 0;
        while (i < expr.Length)
        {
            while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
            int start = i;
            while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.' || expr[i] == '-' || expr[i] == '+'))
                i++;
            if (start == i) break;
            if (!double.TryParse(expr.AsSpan(start, i - start),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double n))
                return 0;
            if (first) { acc = n; first = false; }
            else if (op == '*') acc *= n;
            else if (op == '/') acc = n == 0 ? 0 : acc / n;
            while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
            if (i >= expr.Length) break;
            char c = expr[i];
            if (c == '*' || c == '/') { op = c; i++; }
            else break;
        }
        if (double.IsNaN(acc) || double.IsInfinity(acc)) return 0;
        return (int)acc;
    }

    private static bool TryParseHex(string val, out ushort result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1)
            return ushort.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return ushort.TryParse(val, out result);
    }

    /// <summary>Parse "amount resDefName, amount resDefName, ..." into reagent dictionary.</summary>
    private void ParseReagentList(string val, Dictionary<ushort, int> dict)
    {
        var parts = val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            // Format: "amount defname" or "defname amount" or just "defname"
            var tokens = part.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            int amount = 1;
            string name;
            if (int.TryParse(tokens[0], out int a))
            {
                amount = a;
                name = tokens.Length > 1 ? tokens[1] : "";
            }
            else
            {
                name = tokens[0];
                if (tokens.Length > 1) int.TryParse(tokens[1], out amount);
            }

            if (string.IsNullOrEmpty(name)) continue;

            // Resolve defname to item ID
            var rid = _resourcesStatic?.ResolveDefName(name) ?? ResourceId.Invalid;
            ushort itemId = rid.IsValid ? (ushort)rid.Index : (ushort)0;
            if (itemId == 0 && TryParseHex(name, out ushort hex))
                itemId = hex;
            if (itemId != 0)
                dict[itemId] = amount;
        }
    }

    /// <summary>Parse "skillName minValue, ..." into skill requirement dictionary.</summary>
    private void ParseSkillReqList(string val, Dictionary<SkillType, int> dict)
    {
        var parts = val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var tokens = part.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            // Format: "SKILL value" (e.g. "MAGERY 500")
            string skillName = tokens[0];
            if (!int.TryParse(tokens[1], out int minVal)) continue;

            // Try resolve as defname first, then as enum
            var rid = _resourcesStatic?.ResolveDefName(skillName) ?? ResourceId.Invalid;
            if (rid.IsValid && rid.Type == ResType.SkillDef)
            {
                dict[(SkillType)rid.Index] = minVal;
            }
            else if (Enum.TryParse<SkillType>(skillName, true, out var st))
            {
                dict[st] = minVal;
            }
        }
    }

    /// <summary>
    /// Parse spell flags from script: "spellflag_targ_char|spellflag_good|0x100" etc.
    /// Script-first: resolves defnames from [DEFNAME spell_flags] sections first,
    /// then falls back to numeric/hex parsing.
    /// </summary>
    private SpellFlag ParseSpellFlags(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return SpellFlag.None;

        // Try plain numeric first (single value, no pipes)
        val = val.Trim();
        if (!val.Contains('|'))
        {
            if (TryParseHexUlong(val, out ulong single))
                return (SpellFlag)single;
        }

        // Pipe-separated tokens: resolve each via defname or numeric
        SpellFlag result = SpellFlag.None;
        foreach (var token in val.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // 1) Try defname resolve first (script-first approach)
            var rid = _resourcesStatic?.ResolveDefName(token) ?? ResourceId.Invalid;
            if (rid.IsValid)
            {
                result |= (SpellFlag)(ulong)rid.Index;
                continue;
            }

            // 2) Try numeric / hex
            if (TryParseHexUlong(token, out ulong n))
            {
                result |= (SpellFlag)n;
                continue;
            }

            // 3) Built-in constant names. SPELLFLAG_* are engine constants in
            // the reference (game_macros.h), not script defnames — most packs
            // never declare them in a [DEFNAME] block, and without this
            // fallback every spell loads with Flags=None: no target cursor
            // (everything self-casts), no field/area/bolt behavior.
            if (TryParseSpellFlagName(token, out SpellFlag named))
                result |= named;
        }
        return result;
    }

    /// <summary>Reference SPELLFLAG_* constant names → engine flags
    /// (values match game_macros.h one-to-one).</summary>
    private static bool TryParseSpellFlagName(string token, out SpellFlag flag)
    {
        flag = token.Trim().ToUpperInvariant() switch
        {
            "SPELLFLAG_DIR_ANIM" => SpellFlag.DirAnim,
            "SPELLFLAG_TARG_ITEM" => SpellFlag.TargItem,
            "SPELLFLAG_TARG_CHAR" => SpellFlag.TargChar,
            "SPELLFLAG_TARG_OBJ" => SpellFlag.TargObj,
            "SPELLFLAG_TARG_XYZ" => SpellFlag.TargXYZ,
            "SPELLFLAG_HARM" => SpellFlag.Harm,
            "SPELLFLAG_FX_BOLT" => SpellFlag.FxBolt,
            "SPELLFLAG_FX_TARG" => SpellFlag.FxTarg,
            "SPELLFLAG_FIELD" => SpellFlag.Field,
            "SPELLFLAG_SUMMON" => SpellFlag.Summon,
            "SPELLFLAG_GOOD" => SpellFlag.Good,
            "SPELLFLAG_RESIST" => SpellFlag.Resist,
            "SPELLFLAG_TARG_NOSELF" => SpellFlag.TargNoSelf,
            "SPELLFLAG_FREEZEONCAST" => SpellFlag.FreezeOnCast,
            "SPELLFLAG_FIELD_RANDOMDECAY" => SpellFlag.FieldRandomDecay,
            "SPELLFLAG_NO_ELEMENTALENGINE" => SpellFlag.NoElementalEngine,
            "SPELLFLAG_DISABLED" => SpellFlag.Disabled,
            "SPELLFLAG_SCRIPTED" => SpellFlag.Scripted,
            "SPELLFLAG_PLAYERONLY" => SpellFlag.PlayerOnly,
            "SPELLFLAG_NOUNPARALYZE" => SpellFlag.NoUnparalyze,
            "SPELLFLAG_NO_CASTANIM" => SpellFlag.NoCastAnim,
            "SPELLFLAG_TARG_NO_PLAYER" => SpellFlag.TargNoPlayer,
            "SPELLFLAG_TARG_NO_NPC" => SpellFlag.TargNoNPC,
            "SPELLFLAG_NOPRECAST" => SpellFlag.NoPrecast,
            "SPELLFLAG_NOFREEZEONCAST" => SpellFlag.NoFreezeOnCast,
            "SPELLFLAG_AREA" => SpellFlag.Area,
            "SPELLFLAG_POLY" => SpellFlag.Poly,
            "SPELLFLAG_TARG_DEAD" => SpellFlag.TargDead,
            "SPELLFLAG_DAMAGE" => SpellFlag.Damage,
            "SPELLFLAG_BLESS" => SpellFlag.Bless,
            "SPELLFLAG_CURSE" => SpellFlag.Curse,
            "SPELLFLAG_HEAL" => SpellFlag.Heal,
            "SPELLFLAG_TICK" => SpellFlag.Tick,
            _ => SpellFlag.None,
        };
        return flag != SpellFlag.None;
    }

    private static bool TryParseHexUlong(string val, out ulong result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        // Sphere scripts use bare hex (e.g. "00000004") — try hex if all chars are hex digits
        if (val.Length >= 2 && val[0] == '0')
            return ulong.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out result);
        return ulong.TryParse(val, out result);
    }

    private uint ResolvePipeFlags(string value)
    {
        if (!value.Contains('|'))
        {
            string trimmed = value.Trim();
            var rid = _resources.ResolveDefName(trimmed);
            if (rid.IsValid) return (uint)rid.Index;
            if (TryParseHex(trimmed, out ushort single)) return single;
            if (uint.TryParse(trimmed, out uint dec)) return dec;
            return 0;
        }

        uint result = 0;
        foreach (var part in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var rid = _resources.ResolveDefName(part);
            if (rid.IsValid)
                result |= (uint)rid.Index;
            else if (TryParseHex(part, out ushort hex))
                result |= hex;
            else if (uint.TryParse(part, out uint num))
                result |= num;
        }
        return result;
    }

    private bool TryResolveByteValue(string value, out byte result)
    {
        string token = value.Trim();
        if (byte.TryParse(token, out result))
            return true;
        if (_resources.TryResolveDefNameValue(token, out long resolved) && resolved is >= 0 and <= byte.MaxValue)
        {
            result = (byte)resolved;
            return true;
        }
        result = 0;
        return false;
    }

    // Source-X DUPEITEM makes an itemdef a duplicate of another: it shares the
    // referenced item's CItemBase for every property it does not override, only
    // the graphic differs. A bare "[ITEMDEF 0dc0] DUPEITEM=0dbf" (the flip half
    // of a fishing pole, chair, etc.) therefore has no TYPE/LAYER/TDATA of its
    // own — those must come from the master, or the dupe reads as a plain
    // t_normal item and type-driven behavior (double-click use, equip) breaks.
    //
    // ID=<defname> gets the same TYPE inheritance: Source-X's IBC_ID resolves the
    // referenced graphic to an existing typed base and dupes it (IsDupedItem /
    // DUPELIST), so a child written as "[ITEMDEF i_deed_house] ID=i_deed" with no
    // TYPE of its own must still inherit TYPE=t_deed — otherwise it stays t_normal
    // and never reaches the deed handler (house/ship placement never starts).
    private void ResolveDupeItemInheritance()
    {
        // Bounded fixpoint so DUPEITEM / ID= chains (a->b->c) settle regardless of
        // load order without risking an infinite loop on a malformed cycle.
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (var def in _itemDefs.Values)
            {
                // DUPEITEM: full base sharing for unset TYPE / LAYER / TDATA.
                if (def.DupItemId != 0 &&
                    _itemDefs.TryGetValue(def.DupItemId, out var parent) && parent != def)
                {
                    if (def.Type == ItemType.Normal && parent.Type != ItemType.Normal)
                    {
                        def.Type = parent.Type;
                        changed = true;
                    }
                    if (def.Layer == Layer.None && parent.Layer != Layer.None)
                    {
                        def.Layer = parent.Layer;
                        changed = true;
                    }
                    if (def.TData1 == 0 && def.TData2 == 0 && def.TData3 == 0 && def.TData4 == 0 &&
                        (parent.TData1 != 0 || parent.TData2 != 0 || parent.TData3 != 0 || parent.TData4 != 0))
                    {
                        def.TData1 = parent.TData1;
                        def.TData2 = parent.TData2;
                        def.TData3 = parent.TData3;
                        def.TData4 = parent.TData4;
                        changed = true;
                    }
                }

                // ID=<defname>: inherit only the base TYPE (the graphic is already
                // resolved by ResolveItemDefReferences). TYPE-only keeps this narrow
                // so a graphic-only ID= reference can't drag in unwanted LAYER/TDATA.
                if (def.Type == ItemType.Normal && !string.IsNullOrEmpty(def.DisplayIdRef))
                {
                    var rid = _resources.ResolveDefName(def.DisplayIdRef!.Trim());
                    if (rid.IsValid && rid.Type == ResType.ItemDef &&
                        _itemDefs.TryGetValue(rid.Index, out var idBase) && idBase != def &&
                        idBase.Type != ItemType.Normal)
                    {
                        def.Type = idBase.Type;
                        changed = true;
                    }
                }
            }
            if (!changed) break;
        }
    }

    private void ResolveItemDefReferences()
    {
        // Resolve after every ITEMDEF has loaded so forward aliases such as
        // ID=i_base_item and crop TDATAx=i_next_stage use the referenced
        // definition's wire graphic instead of a truncated string hash.
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (var def in _itemDefs.Values)
            {
                ushort disp = def.DispIndex;
                uint td1 = def.TData1, td2 = def.TData2, td3 = def.TData3, td4 = def.TData4;
                bool defChanged = ResolveItemReference(def.DisplayIdRef, ref disp);
                defChanged |= ResolveItemReference(def.TData1Name, ref td1);
                defChanged |= ResolveItemReference(def.TData2Name, ref td2);
                defChanged |= ResolveItemReference(def.TData3Name, ref td3);
                defChanged |= ResolveItemReference(def.TData4Name, ref td4);
                if (defChanged)
                {
                    def.DispIndex = disp;
                    def.TData1 = td1; def.TData2 = td2; def.TData3 = td3; def.TData4 = td4;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private bool ResolveItemReference(string? name, ref ushort destination)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        uint value = ResolveItemReferenceValue(name);
        if (value == 0 || value > ushort.MaxValue || destination == value) return false;
        destination = (ushort)value;
        return true;
    }

    private bool ResolveItemReference(string? name, ref uint destination)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        uint value = ResolveItemReferenceValue(name);
        if (value == 0 || destination == value) return false;
        destination = value;
        return true;
    }

    private uint ResolveItemReferenceValue(string name)
        => ResolveItemReferenceValue(name, []);

    private uint ResolveItemReferenceValue(string name, HashSet<int> visited)
    {
        var rid = _resources.ResolveDefName(name.Trim());
        if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
        if (!visited.Add(rid.Index)) return 0;
        if (_itemDefs.TryGetValue(rid.Index, out var referenced))
        {
            if (!string.IsNullOrWhiteSpace(referenced.DisplayIdRef))
            {
                uint nested = ResolveItemReferenceValue(referenced.DisplayIdRef, visited);
                if (nested != 0) return nested;
            }
            if (referenced.DispIndex != 0)
                return referenced.DispIndex;
        }
        return rid.Index is > 0 and <= ushort.MaxValue ? (uint)rid.Index : 0;
    }
}
