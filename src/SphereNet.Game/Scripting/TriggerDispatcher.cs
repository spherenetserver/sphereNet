using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Script trigger arguments passed to trigger handlers.
/// Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public sealed class TriggerArgs
{
    public int N1 { get; set; }
    public int N2 { get; set; }
    public int N3 { get; set; }
    public string S1 { get; set; } = "";
    public IScriptObj? O1 { get; set; }
    public ITextConsole? ScriptConsole { get; set; }

    /// <summary>Shared LOCAL.* pool for the trigger chain (Source-X
    /// CScriptTriggerArgs.m_VarsLocal). The engine seeds values before the
    /// fire and reads script writes back from the SAME map afterwards —
    /// the @SpellEffectTick LOCAL.EFFECT/DELAY/CHARGES contract.</summary>
    public SphereNet.Scripting.Variables.VarMap? Locals { get; set; }

    // Convenience typed references
    public Character? CharSrc { get; set; }
    public Item? ItemSrc { get; set; }
}

/// <summary>
/// Trigger dispatcher. Routes CTRIG/ITRIG events through the Source-X trigger chain:
/// 
/// Character trigger order:
///   1. @Char* on source char (cross-target)
///   2. EVENTS (dynamic event scripts on this char)
///   3. TEVENTS (from CHARDEF)
///   4. CHARDEF (body definition)
///   5. EVENTSPET / EVENTSPLAYER (global config)
///
/// Item trigger order:
///   1. @Item* on source char
///   2. EVENTS (dynamic event scripts on this item)
///   3. TEVENTS (from ITEMDEF)
///   4. EVENTSITEM (global config)
///   5. TYPEDEF (item type behavior)
///   6. ITEMDEF (base definition)
/// </summary>
public sealed class TriggerDispatcher
{
    /// <summary>
    /// Delegate for trigger handlers registered by scripts.
    /// </summary>
    public delegate TriggerResult TriggerHandler(IScriptObj obj, TriggerArgs args);

    private readonly Dictionary<string, List<TriggerHandler>> _globalCharHandlers = [];
    private readonly Dictionary<string, List<TriggerHandler>> _globalItemHandlers = [];
    private readonly Dictionary<string, List<TriggerHandler>> _typeDefHandlers = [];

    // Source-X IsTrigUsed parity: the set of char-trigger names hooked anywhere —
    // by a registered C# handler, an [ON=@X] block in any loaded resource, or an
    // f_onchar_<x> function. Lets a hot path (e.g. ComputeNotoriety → @NotoSend)
    // skip firing entirely when nothing hooks the trigger. Registrations update it
    // live; BuildUsedTriggerCache scans the loaded scripts once after load.
    private readonly HashSet<string> _usedCharTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedItemTriggers = new(StringComparer.OrdinalIgnoreCase);

    // Trigger names that have an f_onchar_<x>/f_onitem_<x> [FUNCTION] fallback.
    // Gates FireCharTriggerByName step 6 / FireItemTriggerByName step 7 so the
    // per-fire name concat + ToLowerInvariant + args wrapping only happens when
    // such a function actually exists. Until BuildUsedTriggerCache runs the gate
    // stays open (always try), preserving behavior when no cache is built.
    private bool _funcTriggerGateBuilt;
    private readonly HashSet<string> _funcCharTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _funcItemTriggers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional resource holder for resolving CHARDEF/ITEMDEF scripts.</summary>
    public ResourceHolder? Resources { get; set; }

    /// <summary>Optional trigger runner for executing script blocks.</summary>
    public TriggerRunner? Runner { get; set; }

    public List<ResourceId> GlobalPlayerEvents { get; } = [];
    public List<ResourceId> GlobalPetEvents { get; } = [];
    public List<ResourceId> GlobalItemEvents { get; } = [];
    public List<ResourceId> GlobalRegionEvents { get; } = [];
    public List<ResourceId> SpeechSelfResources { get; } = [];
    public List<ResourceId> SpeechPetResources { get; } = [];

    public bool ScriptDebug { get; set; }
    public Action<string>? DebugLog { get; set; }

    /// <summary>
    /// Fire a character trigger. Maps to CChar::OnTrigger.
    /// </summary>
    public TriggerResult FireCharTrigger(Character ch, CharTrigger trigger, TriggerArgs args)
        => FireCharTriggerByName(ch, GetCharTriggerName(trigger), args);

    /// <summary>Name-based variant — used by the enum path above and by the
    /// script TRIGGER verb (Source-X CV_TRIGGER), which may fire arbitrary
    /// custom trigger names defined only in TEVENTS/EVENTS sections.</summary>
    public TriggerResult FireCharTriggerByName(Character ch, string trigName, TriggerArgs args)
    {
        if (ScriptDebug)
            DebugLog?.Invoke($"[script_debug] CTRIG @{trigName} on char 0x{ch.Uid.Value:X8} '{ch.Name}' src={args.CharSrc?.Name ?? "-"}");

        // 1. @Char* on source character (cross-target triggers)
        if (args.CharSrc != null && args.CharSrc != ch)
        {
            string crossTrigName = "char" + trigName;
            var result = RunObjectHandlers(args.CharSrc, crossTrigName, args);
            if (result == TriggerResult.True)
            {
                if (ScriptDebug)
                    DebugLog?.Invoke($"[script_debug]   @{crossTrigName} returned TRUE (blocked)");
                return TriggerResult.True;
            }
        }

        // 2. Dynamic EVENTS on this character
        var evResult = RunObjectHandlers(ch, trigName, args);
        if (evResult == TriggerResult.True)
        {
            if (ScriptDebug)
                DebugLog?.Invoke($"[script_debug]   EVENTS @{trigName} returned TRUE (blocked)");
            return TriggerResult.True;
        }

        // 3. TEVENTS from CHARDEF definition (type-level event scripts)
        if (Resources != null && Runner != null)
        {
            var charDef = Definitions.DefinitionLoader.GetCharDef(ch.CharDefIndex);
            if (charDef != null)
            {
                foreach (var tevRid in charDef.Events)
                {
                    var tevLink = Resources.GetResource(tevRid);
                    if (tevLink == null) continue;
                    var result = RunWrapped(tevLink, trigName, ch, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // 4. CHARDEF own triggers (ON=@Trigger in the CHARDEF body)
            var charDefLink = Resources.GetResource(ResType.CharDef, ch.CharDefIndex);
            if (charDefLink != null)
            {
                // CHARDEF links may not have trigger bitmasks precomputed, so resolve by name.
                var result = RunWrapped(charDefLink, trigName, ch, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        // 5. Global EVENTSPET / EVENTSPLAYER
        string globalKey = ch.IsPlayer ? "EVENTSPLAYER" : "EVENTSPET";
        if (_globalCharHandlers.TryGetValue(globalKey + "." + trigName, out var globalHandlers))
        {
            foreach (var handler in globalHandlers)
            {
                var result = handler(ch, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }
        var globalEventResult = RunResourceEventHandlers(
            ch.IsPlayer ? GlobalPlayerEvents : GlobalPetEvents, trigName, ch, args);
        if (globalEventResult == TriggerResult.True)
            return TriggerResult.True;

        if (Runner != null && (!_funcTriggerGateBuilt || _funcCharTriggers.Contains(trigName)))
        {
            string funcName = "f_onchar_" + trigName.ToLowerInvariant();
            if (Runner.TryRunFunction(funcName, ch, args.ScriptConsole, WrapArgs(args), out var result) &&
                result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire an item trigger. Maps to CItem::OnTrigger.
    /// </summary>
    public TriggerResult FireItemTrigger(Item item, ItemTrigger trigger, TriggerArgs args)
        => FireItemTriggerByName(item, GetItemTriggerName(trigger), args);

    /// <summary>Name-based variant — used by the enum path above and by the
    /// script TRIGGER verb, which may fire custom trigger names.</summary>
    public TriggerResult FireItemTriggerByName(Item item, string trigName, TriggerArgs args)
    {
        if (ScriptDebug)
            DebugLog?.Invoke($"[script_debug] ITRIG @{trigName} on item 0x{item.Uid.Value:X8} id=0x{item.BaseId:X4} src={args.CharSrc?.Name ?? "-"}");

        // 1. @Item* on source character
        if (args.CharSrc != null)
        {
            string charTrigName = "item" + trigName;
            var result = RunObjectHandlers(args.CharSrc, charTrigName, args);
            if (result == TriggerResult.True)
            {
                if (ScriptDebug)
                    DebugLog?.Invoke($"[script_debug]   @{charTrigName} returned TRUE (blocked)");
                return TriggerResult.True;
            }
        }

        // 2. Dynamic EVENTS on this item
        var evResult = RunObjectHandlers(item, trigName, args);
        if (evResult == TriggerResult.True)
        {
            if (ScriptDebug)
                DebugLog?.Invoke($"[script_debug]   EVENTS @{trigName} returned TRUE (blocked)");
            return TriggerResult.True;
        }

        // 3. TEVENTS from ITEMDEF definition
        if (Resources != null && Runner != null)
        {
            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (itemDef != null)
            {
                foreach (var tevRid in itemDef.Events)
                {
                    var tevLink = Resources.GetResource(tevRid);
                    if (tevLink == null) continue;
                    var result = RunWrapped(tevLink, trigName, item, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // ITEMDEF own triggers (resolved graphic's def).
            var itemDefLink = Resources.GetResource(ResType.ItemDef, item.BaseId);
            if (itemDefLink != null)
            {
                var result = RunWrapped(itemDefLink, trigName, item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }

            // Named (scripted) ITEMDEF triggers — when [itemdef i_moongate]
            // sets id=i_moongate_blue, the item's BaseId becomes 0x0F6C
            // (the graphic) but the @DClick / @Step triggers live on the
            // i_moongate section itself, keyed by its string-hash index.
            // TryAddAtTarget stashes that index in TAG.SCRIPTDEF so the
            // dispatcher can route triggers back to the scripted def.
            if (item.TryGetTag("SCRIPTDEF", out string? scriptDefStr) &&
                int.TryParse(scriptDefStr, out int scriptDefIdx) &&
                scriptDefIdx != item.BaseId)
            {
                var scriptLink = Resources.GetResource(ResType.ItemDef, scriptDefIdx);
                if (scriptLink != null)
                {
                    var result = RunWrapped(scriptLink, trigName, item, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        // 4. Global EVENTSITEM
        if (_globalItemHandlers.TryGetValue("EVENTSITEM." + trigName, out var globalHandlers))
        {
            foreach (var handler in globalHandlers)
            {
                var result = handler(item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }
        var globalEventResult = RunResourceEventHandlers(GlobalItemEvents, trigName, item, args);
        if (globalEventResult == TriggerResult.True)
            return TriggerResult.True;

        // 5. TYPEDEF (item type behavior)
        string typeKey = item.ItemType.ToString();
        if (_typeDefHandlers.TryGetValue(typeKey + "." + trigName, out var typeHandlers))
        {
            foreach (var handler in typeHandlers)
            {
                var result = handler(item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }
        if (Resources != null && Runner != null)
        {
            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (!string.IsNullOrWhiteSpace(itemDef?.TypeRaw))
            {
                var typeRid = Resources.ResolveDefName(itemDef.TypeRaw.Trim());
                var typeLink = typeRid.IsValid && typeRid.Type == ResType.TypeDef
                    ? Resources.GetResource(typeRid)
                    : null;
                if (typeLink != null)
                {
                    var result = RunWrapped(typeLink, trigName, item, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        // 6. ITEMDEF (base definition script) — already covered by step 3

        // 7. Global f_onitem_* function (parity with f_onchar_*)
        if (Runner != null && (!_funcTriggerGateBuilt || _funcItemTriggers.Contains(trigName)))
        {
            string funcName = "f_onitem_" + trigName.ToLowerInvariant();
            if (Runner.TryRunFunction(funcName, item, args.ScriptConsole, WrapArgs(args), out var result) &&
                result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire SPEECH triggers for an NPC hearing speech.
    /// Iterates the CharDef's SpeechResources list and runs the first matching pattern.
    /// </summary>
    public TriggerResult FireSpeechTrigger(Character npc, Character speaker, string text, int mode = 0)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var charDef = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        if (charDef != null && charDef.SpeechResources.Count > 0)
        {
            var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, mode, 0, text)
            {
                Object1 = npc,
                Object2 = speaker
            };

            foreach (var speechRid in charDef.SpeechResources)
            {
                var link = Resources.GetResource(speechRid);
                if (link == null) continue;

                var result = Runner.RunSpeechTrigger(link, text, npc, null, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        return RunSpeechResourceHandlers(SpeechPetResources, npc, speaker, text, mode);
    }

    public TriggerResult FireSpeechSelfTrigger(Character speaker, string text, int mode)
    {
        return RunSpeechResourceHandlers(SpeechSelfResources, speaker, speaker, text, mode);
    }

    private TriggerResult RunSpeechResourceHandlers(
        IReadOnlyList<ResourceId> speechResources,
        Character target,
        Character speaker,
        string text,
        int mode)
    {
        if (Resources == null || Runner == null || speechResources.Count == 0)
            return TriggerResult.Default;

        var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, mode, 0, text)
        {
            Object1 = target,
            Object2 = speaker
        };

        foreach (var speechRid in speechResources)
        {
            var link = Resources.GetResource(speechRid);
            if (link == null) continue;

            var result = Runner.RunSpeechTrigger(link, text, target, null, args);
            if (result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire region event scripts for a given trigger name.
    /// Iterates the region's EVENTS list and runs matching triggers.
    /// </summary>
    public void FireRegionEvents(World.Regions.Region region, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null) return;

        foreach (var eventRid in region.Events)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;
            RunWrapped(link, trigName, ch, args);
        }

        RunResourceEventHandlers(GlobalRegionEvents, trigName, ch, args);
    }

    /// <summary>
    /// Fire room event scripts for a given trigger name.
    /// Iterates the room's EVENTS list and runs matching triggers.
    /// </summary>
    public void FireRoomEvents(World.Regions.Room room, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null) return;

        foreach (var eventRid in room.Events)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;
            RunWrapped(link, trigName, ch, args);
        }

        RunResourceEventHandlers(GlobalRegionEvents, trigName, ch, args);
    }

    /// <summary>
    /// Fire a trigger on a REGIONRESOURCE script (e.g. @ResourceGather, @ResourceTest).
    /// Runs ON=@TrigName blocks defined in the REGIONRESOURCE section.
    /// </summary>
    public TriggerResult FireResourceTrigger(RegionResourceDef resDef, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var link = Resources.GetResource(resDef.Id);
        if (link == null)
            return TriggerResult.Default;

        // Copy the script's ARGN1/2/3 mutations back (e.g. @ResourceGather changing
        // the reaped item id / amount), matching the char-trigger RunWrapped path.
        var wrapped = WrapArgs(args);
        var result = Runner.RunTriggerByName(link, trigName, ch, args.ScriptConsole, wrapped);
        args.N1 = wrapped.Number1;
        args.N2 = wrapped.Number2;
        args.N3 = wrapped.Number3;
        return result;
    }

    /// <summary>
    /// Fire a trigger on a [SPELL] def's own ON=@TrigName block (Source-X
    /// Spell_OnTrigger). The [SPELL] ResourceLink already retains its trigger
    /// bodies (SpellDef is a definition type), so this just resolves the link by
    /// spell index and runs the body — mirroring the ITEMDEF/REGIONRESOURCE path.
    /// ARGN1/2/3 mutations are copied back so a script can override id/amount.
    /// </summary>
    public TriggerResult FireSpellTrigger(SphereNet.Core.Enums.SpellType spell, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var link = Resources.GetResource(ResType.SpellDef, (int)spell);
        if (link == null)
            return TriggerResult.Default;

        var wrapped = WrapArgs(args);
        var result = Runner.RunTriggerByName(link, trigName, ch, args.ScriptConsole, wrapped);
        args.N1 = wrapped.Number1;
        args.N2 = wrapped.Number2;
        args.N3 = wrapped.Number3;
        return result;
    }

    /// <summary>Register a global character event handler.</summary>
    public void RegisterCharEvent(string eventKey, string trigName, TriggerHandler handler)
    {
        string key = eventKey + "." + trigName;
        if (!_globalCharHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _globalCharHandlers[key] = list;
        }
        list.Add(handler);
        _usedCharTriggers.Add(trigName);
    }

    private TriggerResult RunResourceEventHandlers(
        IReadOnlyList<ResourceId> eventResources,
        string trigName,
        IScriptObj target,
        TriggerArgs args)
    {
        if (Resources == null || Runner == null || eventResources.Count == 0)
            return TriggerResult.Default;

        foreach (var eventRid in eventResources)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;

            var result = RunWrapped(link, trigName, target, args);
            if (result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Scan all loaded script resources for the char-trigger names they hook
    /// (<c>[ON=@X]</c> blocks and <c>f_onchar_x</c> functions) and fold them into
    /// the used-trigger set. Registered C# handlers are already tracked live by
    /// <see cref="RegisterCharEvent"/>. Call once after scripts are loaded.
    /// </summary>
    public void BuildUsedTriggerCache()
    {
        if (Resources == null)
            return;

        foreach (var link in Resources.GetAllResources())
        {
            var keys = link.StoredKeys;
            if (keys == null) continue;
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                    key.Arg.StartsWith('@'))
                {
                    // A scanned [ON=@X] name could hook either a char or an item
                    // trigger; record it for both (a safe over-approximation — the
                    // gate must never report a hooked trigger as unused).
                    string trigName = key.Arg[1..].Trim();
                    _usedCharTriggers.Add(trigName);
                    _usedItemTriggers.Add(trigName);
                }
            }
        }

        // f_onchar_<name>/f_onitem_<name> function fallbacks (FireCharTrigger
        // step 6 / FireItemTrigger step 7). Scanning loaded function defnames
        // instead of the trigger enums also covers custom trigger names fired
        // via the TRIGGER verb. The f_f_* variants account for the automatic
        // f_-prefix retry in function resolution.
        _funcCharTriggers.Clear();
        Resources.CollectFunctionDefNameSuffixes("f_onchar_", _funcCharTriggers);
        Resources.CollectFunctionDefNameSuffixes("f_f_onchar_", _funcCharTriggers);
        _usedCharTriggers.UnionWith(_funcCharTriggers);

        _funcItemTriggers.Clear();
        Resources.CollectFunctionDefNameSuffixes("f_onitem_", _funcItemTriggers);
        Resources.CollectFunctionDefNameSuffixes("f_f_onitem_", _funcItemTriggers);
        _usedItemTriggers.UnionWith(_funcItemTriggers);

        _funcTriggerGateBuilt = true;
    }

    /// <summary>
    /// O(1) check: is <paramref name="trigger"/> hooked by any script block,
    /// registered handler, or function? Also covers the cross-fired
    /// <c>@char&lt;Name&gt;</c> mirror. Returns false only when nothing can fire it,
    /// letting hot-path callers skip the dispatch entirely.
    /// </summary>
    public bool IsCharTriggerUsed(CharTrigger trigger)
    {
        string name = GetCharTriggerName(trigger);
        return _usedCharTriggers.Contains(name) || _usedCharTriggers.Contains("char" + name);
    }

    /// <summary>
    /// O(1) item-trigger counterpart of <see cref="IsCharTriggerUsed"/>. Also
    /// covers the cross-fired <c>@item&lt;Name&gt;</c> mirror. Lets a hot item path
    /// (e.g. memory creation during combat) skip the dispatch when nothing hooks it.
    /// </summary>
    public bool IsItemTriggerUsed(ItemTrigger trigger)
    {
        string name = GetItemTriggerName(trigger);
        return _usedItemTriggers.Contains(name) || _usedItemTriggers.Contains("item" + name);
    }

    /// <summary>Register a global item event handler.</summary>
    public void RegisterItemEvent(string eventKey, string trigName, TriggerHandler handler)
    {
        string key = eventKey + "." + trigName;
        if (!_globalItemHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _globalItemHandlers[key] = list;
        }
        list.Add(handler);
        _usedItemTriggers.Add(trigName);
    }

    /// <summary>Register a TYPEDEF handler.</summary>
    public void RegisterTypeDef(string itemType, string trigName, TriggerHandler handler)
    {
        string key = itemType + "." + trigName;
        if (!_typeDefHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _typeDefHandlers[key] = list;
        }
        list.Add(handler);
    }

    /// <summary>
    /// Run handlers attached directly to an object (EVENTS list).
    /// Iterates the object's runtime Events list and runs matching triggers from EVENTS scripts.
    /// Also checks TAG.EVENT_* overrides for backward compat.
    /// </summary>
    private TriggerResult RunObjectHandlers(IScriptObj obj, string trigName, TriggerArgs args)
    {
        // Check TAG.EVENT_<trigName> override first
        if (obj.TryGetProperty("TAG.EVENT_" + trigName.ToUpperInvariant(), out string value))
        {
            if (value == "1")
                return TriggerResult.True;
        }

        // Run through the object's EVENTS list
        if (Resources != null && Runner != null)
        {
            IReadOnlyList<ResourceId>? events = obj switch
            {
                Character ch => ch.Events,
                Item item => item.Events,
                _ => null
            };

            if (events != null)
            {
                var snapshot = events.ToArray();
                foreach (var eventRid in snapshot)
                {
                    var eventLink = Resources.GetResource(eventRid);
                    if (eventLink == null) continue;

                    var result = RunWrapped(eventLink, trigName, obj, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        return TriggerResult.Default;
    }

    /// <summary>Run one trigger block with wrapped args, then copy the script's
    /// ARGN1/2/3 mutations back into the caller's TriggerArgs. Source-X reads
    /// pScriptArgs->m_iN1/2 back after OnTrigger (e.g. @NPCActFight dist/motivation,
    /// @HitTry swing delay); the wrapped instance is per-block, so without this
    /// copy-back those mutations would be lost. LOCAL.* readback already works
    /// via the shared Locals pool. Copying after each block in a chain also
    /// forward-propagates the values to the next block, matching Source-X.</summary>
    private TriggerResult RunWrapped(SphereNet.Scripting.Resources.ResourceLink link,
        string trigName, IScriptObj obj, TriggerArgs args)
    {
        var wrapped = WrapArgs(args);
        var result = Runner!.RunTriggerByName(link, trigName, obj, args.ScriptConsole, wrapped);
        args.N1 = wrapped.Number1;
        args.N2 = wrapped.Number2;
        args.N3 = wrapped.Number3;
        return result;
    }

    /// <summary>Convert TriggerArgs to ITriggerArgs for the script engine.</summary>
    private static SphereNet.Scripting.Execution.TriggerArgs WrapArgs(TriggerArgs args)
    {
        var wrapped = new SphereNet.Scripting.Execution.TriggerArgs(
            args.CharSrc ?? (IScriptObj?)args.ItemSrc,
            args.N1, args.N2, args.S1);
        // Seed ARGN3 into the script args. The constructor only takes N1/N2, so
        // without this <ARGN3> read as 0 inside every trigger even though the caller
        // set it (e.g. @DropOn_* drop-Z, @SkillUseQuick result) — and the N3 write-
        // back below then read the un-seeded 0 too.
        wrapped.Number3 = args.N3;
        wrapped.Object1 = args.O1;
        wrapped.Object2 = args.CharSrc ?? (IScriptObj?)args.ItemSrc;
        // Reference, not copy: every chain step's wrapped args share the one
        // LOCAL pool, so cross-step and engine readback semantics hold.
        wrapped.SharedLocals = args.Locals;
        return wrapped;
    }


    // --- Trigger name mapping ---

    private static string GetCharTriggerName(CharTrigger trigger) => trigger switch
    {
        CharTrigger.AfterClick => "AfterClick",
        CharTrigger.Attack => "Attack",
        CharTrigger.CallGuards => "CallGuards",
        CharTrigger.CharAttack => "CharAttack",
        CharTrigger.CharClick => "CharClick",
        CharTrigger.CharClientTooltip => "CharClientTooltip",
        CharTrigger.CharContextMenuRequest => "CharContextMenuRequest",
        CharTrigger.CharContextMenuSelect => "CharContextMenuSelect",
        CharTrigger.CharDClick => "CharDClick",
        CharTrigger.CharTradeAccepted => "CharTradeAccepted",
        CharTrigger.Click => "Click",
        CharTrigger.ClientTooltip => "ClientTooltip",
        CharTrigger.ContextMenuRequest => "ContextMenuRequest",
        CharTrigger.ContextMenuSelect => "ContextMenuSelect",
        CharTrigger.DClick => "DClick",
        CharTrigger.Create => "Create",
        CharTrigger.CreateLoot => "CreateLoot",
        CharTrigger.Death => "Death",
        CharTrigger.Destroy => "Destroy",
        CharTrigger.Kill => "Kill",
        CharTrigger.Hit => "Hit",
        CharTrigger.GetHit => "GetHit",
        CharTrigger.HitMiss => "HitMiss",
        CharTrigger.HitParry => "HitParry",
        CharTrigger.HitCheck => "HitCheck",
        CharTrigger.HitIgnore => "HitIgnore",
        CharTrigger.HitTry => "HitTry",
        CharTrigger.SpellCast => "SpellCast",
        CharTrigger.SpellEffect => "SpellEffect",
        CharTrigger.SpellSuccess => "SpellSuccess",
        CharTrigger.SpellFail => "SpellFail",
        CharTrigger.SpellInterrupt => "SpellInterrupt",
        CharTrigger.SpellSelect => "SpellSelect",
        CharTrigger.SpellBook => "SpellBook",
        CharTrigger.SpellTargetCancel => "SpellTargetCancel",
        CharTrigger.SkillPreStart => "SkillPreStart",
        CharTrigger.SkillStart => "SkillStart",
        CharTrigger.SkillStroke => "SkillStroke",
        CharTrigger.SkillSuccess => "SkillSuccess",
        CharTrigger.SkillFail => "SkillFail",
        CharTrigger.SkillGain => "SkillGain",
        CharTrigger.SkillAbort => "SkillAbort",
        CharTrigger.SkillChange => "SkillChange",
        CharTrigger.SkillSelect => "SkillSelect",
        CharTrigger.SkillMakeItem => "SkillMakeItem",
        CharTrigger.SkillMenu => "SkillMenu",
        CharTrigger.SkillTargetCancel => "SkillTargetCancel",
        CharTrigger.SkillUseQuick => "SkillUseQuick",
        CharTrigger.SkillWait => "SkillWait",
        CharTrigger.LogIn => "LogIn",
        CharTrigger.LogOut => "LogOut",
        CharTrigger.Mount => "Mount",
        CharTrigger.Dismount => "Dismount",
        CharTrigger.RegionEnter => "RegionEnter",
        CharTrigger.RegionLeave => "RegionLeave",
        CharTrigger.RegionStep => "RegionStep",
        CharTrigger.RoomEnter => "RoomEnter",
        CharTrigger.RoomLeave => "RoomLeave",
        CharTrigger.RoomStep => "RoomStep",
        CharTrigger.CombatStart => "CombatStart",
        CharTrigger.CombatEnd => "CombatEnd",
        CharTrigger.CombatAdd => "CombatAdd",
        CharTrigger.CombatDelete => "CombatDelete",
        CharTrigger.Criminal => "Criminal",
        CharTrigger.EffectAdd => "EffectAdd",
        CharTrigger.EnvironChange => "EnvironChange",
        CharTrigger.ExpChange => "ExpChange",
        CharTrigger.ExpLevelChange => "ExpLevelChange",
        CharTrigger.FameChange => "FameChange",
        CharTrigger.KarmaChange => "KarmaChange",
        CharTrigger.Hunger => "Hunger",
        CharTrigger.Eat => "Eat",
        CharTrigger.Follow => "Follow",
        CharTrigger.Jail => "Jail",
        CharTrigger.HouseDesignCommit => "HouseDesignCommit",
        CharTrigger.HouseDesignExit => "HouseDesignExit",
        CharTrigger.MurderDecay => "MurderDecay",
        CharTrigger.MurderMark => "MurderMark",
        CharTrigger.NotoSend => "NotoSend",
        CharTrigger.NPCAction => "NPCAction",
        CharTrigger.NPCActFight => "NPCActFight",
        CharTrigger.NPCActFollow => "NPCActFollow",
        CharTrigger.NPCActWander => "NPCActWander",
        CharTrigger.NPCActCast => "NPCActCast",
        CharTrigger.NPCSeeNewPlayer => "NPCSeeNewPlayer",
        CharTrigger.NPCHearGreeting => "NPCHearGreeting",
        CharTrigger.NPCHearUnknown => "NPCHearUnknown",
        CharTrigger.NPCLookAtChar => "NPCLookAtChar",
        CharTrigger.NPCLookAtItem => "NPCLookAtItem",
        CharTrigger.NPCSpecialAction => "NPCSpecialAction",
        CharTrigger.NPCAcceptItem => "NPCAcceptItem",
        CharTrigger.NPCRefuseItem => "NPCRefuseItem",
        CharTrigger.NPCRestock => "NPCRestock",
        CharTrigger.NPCSeeWantItem => "NPCSeeWantItem",
        CharTrigger.NPCLostTeleport => "NPCLostTeleport",
        CharTrigger.PartyDisband => "PartyDisband",
        CharTrigger.PartyInvite => "PartyInvite",
        CharTrigger.PartyLeave => "PartyLeave",
        CharTrigger.PartyRemove => "PartyRemove",
        CharTrigger.PersonalSpace => "PersonalSpace",
        CharTrigger.PetDesert => "PetDesert",
        CharTrigger.Resurrect => "Resurrect",
        CharTrigger.ReceiveItem => "ReceiveItem",
        CharTrigger.Rename => "Rename",
        CharTrigger.SeeCrime => "SeeCrime",
        CharTrigger.SeeSnoop => "SeeSnoop",
        CharTrigger.StatChange => "StatChange",
        CharTrigger.StepStealth => "StepStealth",
        CharTrigger.TradeAccepted => "TradeAccepted",
        CharTrigger.TradeClose => "TradeClose",
        CharTrigger.TradeCreate => "TradeCreate",
        CharTrigger.Targon_Cancel => "Targon_Cancel",
        CharTrigger.UserBugReport => "UserBugReport",
        CharTrigger.UserWarmode => "UserWarmode",
        CharTrigger.UserChatButton => "UserChatButton",
        CharTrigger.UserExtCmd => "UserExtCmd",
        CharTrigger.UserExWalkLimit => "UserExWalkLimit",
        CharTrigger.UserGlobalChatButton => "UserGlobalChatButton",
        CharTrigger.UserGuildButton => "UserGuildButton",
        CharTrigger.UserKRToolbar => "UserKRToolbar",
        CharTrigger.UserMailBag => "UserMailBag",
        CharTrigger.UserQuestArrowClick => "UserQuestArrowClick",
        CharTrigger.UserQuestButton => "UserQuestButton",
        CharTrigger.UserSkills => "UserSkills",
        CharTrigger.UserSpecialMove => "UserSpecialMove",
        CharTrigger.UserStats => "UserStats",
        CharTrigger.UserUltimaStoreButton => "UserUltimaStoreButton",
        CharTrigger.UserVirtue => "UserVirtue",
        CharTrigger.UserVirtueInvoke => "UserVirtueInvoke",
        CharTrigger.ToolTip => "ToolTip",
        CharTrigger.Profile => "Profile",
        CharTrigger.itemAfterClick => "itemAfterClick",
        CharTrigger.itemBuy => "itemBuy",
        CharTrigger.itemClick => "itemClick",
        CharTrigger.itemClientTooltip => "itemClientTooltip",
        CharTrigger.itemContextMenuRequest => "itemContextMenuRequest",
        CharTrigger.itemContextMenuSelect => "itemContextMenuSelect",
        CharTrigger.itemCreate => "itemCreate",
        CharTrigger.itemDamage => "itemDamage",
        CharTrigger.itemDClick => "itemDClick",
        CharTrigger.itemDestroy => "itemDestroy",
        CharTrigger.itemDropOnChar => "itemDropOn_Char",
        CharTrigger.itemDropOnGround => "itemDropOn_Ground",
        CharTrigger.itemDropOnItem => "itemDropOn_Item",
        CharTrigger.itemDropOnSelf => "itemDropOn_Self",
        CharTrigger.itemDropOnTrade => "itemDropOn_Trade",
        CharTrigger.itemEquip => "itemEquip",
        CharTrigger.itemEquipTest => "itemEquipTest",
        CharTrigger.itemMemoryEquip => "itemMemoryEquip",
        CharTrigger.itemPickupGround => "itemPickup_Ground",
        CharTrigger.itemPickupPack => "itemPickup_Pack",
        CharTrigger.itemPickupSelf => "itemPickup_Self",
        CharTrigger.itemPickupStack => "itemPickup_Stack",
        CharTrigger.itemSell => "itemSell",
        CharTrigger.itemSpellEffect => "itemSpellEffect",
        CharTrigger.itemStep => "itemStep",
        CharTrigger.itemTargOnCancel => "itemTargOn_Cancel",
        CharTrigger.itemTargOnChar => "itemTargOn_Char",
        CharTrigger.itemTargOnGround => "itemTargOn_Ground",
        CharTrigger.itemTargOnItem => "itemTargOn_Item",
        CharTrigger.itemTimer => "itemTimer",
        CharTrigger.itemToolTip => "itemToolTip",
        CharTrigger.itemUnequip => "itemUnequip",
        _ => trigger.ToString(),
    };

    private static string GetItemTriggerName(ItemTrigger trigger) => trigger switch
    {
        ItemTrigger.Click => "Click",
        ItemTrigger.DClick => "DClick",
        ItemTrigger.Create => "Create",
        ItemTrigger.Damage => "Damage",
        ItemTrigger.Destroy => "Destroy",
        ItemTrigger.Equip => "Equip",
        ItemTrigger.EquipTest => "EquipTest",
        ItemTrigger.Unequip => "Unequip",
        ItemTrigger.DropOnChar => "DropOn_Char",
        ItemTrigger.DropOnGround => "DropOn_Ground",
        ItemTrigger.DropOnItem => "DropOn_Item",
        ItemTrigger.DropOnSelf => "DropOn_Self",
        ItemTrigger.DropOnTrade => "DropOn_Trade",
        ItemTrigger.PickupGround => "Pickup_Ground",
        ItemTrigger.PickupPack => "Pickup_Pack",
        ItemTrigger.PickupSelf => "Pickup_Self",
        ItemTrigger.PickupStack => "Pickup_Stack",
        ItemTrigger.Step => "Step",
        ItemTrigger.Timer => "Timer",
        ItemTrigger.Tooltip => "ToolTip",
        ItemTrigger.AfterClick => "AfterClick",
        ItemTrigger.ClientTooltip => "ClientTooltip",
        ItemTrigger.ClientTooltipAfterDefault => "ClientTooltipAfterDefault",
        ItemTrigger.ContextMenuRequest => "ContextMenuRequest",
        ItemTrigger.ContextMenuSelect => "ContextMenuSelect",
        ItemTrigger.TargOnCancel => "TargOn_Cancel",
        ItemTrigger.TargOnChar => "TargOn_Char",
        ItemTrigger.TargOnGround => "TargOn_Ground",
        ItemTrigger.TargOnItem => "TargOn_Item",
        ItemTrigger.Sell => "Sell",
        ItemTrigger.Buy => "Buy",
        ItemTrigger.Spawn => "Spawn",
        ItemTrigger.PreSpawn => "PreSpawn",
        ItemTrigger.AddObj => "AddObj",
        ItemTrigger.DelObj => "DelObj",
        ItemTrigger.AddRedCandle => "AddRedCandle",
        ItemTrigger.AddWhiteCandle => "AddWhiteCandle",
        ItemTrigger.DelRedCandle => "DelRedCandle",
        ItemTrigger.DelWhiteCandle => "DelWhiteCandle",
        ItemTrigger.SpellEffect => "SpellEffect",
        ItemTrigger.Hit => "Hit",
        ItemTrigger.GetHit => "GetHit",
        ItemTrigger.ResourceGather => "ResourceGather",
        ItemTrigger.ResourceTest => "ResourceTest",
        ItemTrigger.CarveCorpse => "CarveCorpse",
        ItemTrigger.Complete => "Complete",
        ItemTrigger.Dye => "Dye",
        ItemTrigger.Level => "Level",
        ItemTrigger.MemoryEquip => "MemoryEquip",
        ItemTrigger.Redeed => "Redeed",
        ItemTrigger.RegionEnter => "RegionEnter",
        ItemTrigger.RegionLeave => "RegionLeave",
        ItemTrigger.ShipMove => "ShipMove",
        ItemTrigger.ShipStop => "ShipStop",
        ItemTrigger.ShipTurn => "ShipTurn",
        ItemTrigger.Smelt => "Smelt",
        ItemTrigger.Start => "Start",
        ItemTrigger.Stop => "Stop",
        ItemTrigger.Hear => "Hear",
        _ => trigger.ToString(),
    };
}
