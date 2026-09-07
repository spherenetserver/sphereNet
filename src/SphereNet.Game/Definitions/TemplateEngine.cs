using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>
/// Runtime template resolver. Picks a single itemdef from a random-pool
/// template (random_hats, random_shirts_human, colors_red …) or
/// enumerates the full item list from a sequential template
/// (VENDOR_S_ALCHEMIST …). Nested templates are followed once before
/// giving up — prevents infinite loops if a shard scripts a cycle.
/// </summary>
public static class TemplateEngine
{
    private static readonly Random _rand = Random.Shared;
    private const int MaxNestedResolves = 8;

    /// <summary>Pick one concrete ItemDef defname from a template's
    /// random pool. Follows nested template references and Source-X
    /// <c>[DEFNAME ...]</c> text entries that use the <c>{ a w b w }</c>
    /// weighted form — random_shirts_human / random_hats / random_facial_hair
    /// etc. ship in those defname blocks, not in [TEMPLATE] sections.
    /// Returns the empty string when the pool is empty.</summary>
    public static string PickRandomItemDefName(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname))
            return "";
        string current = defname.Trim();
        for (int hops = 0; hops < MaxNestedResolves; hops++)
        {
            // 0) Inline weighted pool in defname position — Source-X resolves
            //    "{ a w b w }" through the expression engine before the
            //    resource lookup (loot rows like
            //    ITEM={ random_weapon_sword_normal 99 i_sword_demon 1 }).
            //    A "0" member is a deliberate empty slot ("nothing" chance).
            if (current.StartsWith('{'))
            {
                current = PickFromDefValue(current);
                if (string.IsNullOrEmpty(current) || current == "0")
                    return "";
                continue;
            }

            // 1) Explicit [TEMPLATE] block wins.
            var tpl = DefinitionLoader.GetTemplateDef(current);
            if (tpl != null)
            {
                if (tpl.RandomEntries.Count == 0)
                    return current; // sequential template — caller enumerates
                current = PickByWeight(tpl.RandomEntries);
                if (string.IsNullOrEmpty(current)) return "";
                continue;
            }

            // 2) [DEFNAME items_*] text entry — could be a weighted
            //    list "{ a w b w ... }" or a simple "i_foo" alias.
            var resources = DefinitionLoader.StaticResources;
            if (resources != null && resources.TryGetDefValue(current, out string? val) &&
                !string.IsNullOrWhiteSpace(val))
            {
                string picked = PickFromDefValue(val);
                if (!string.IsNullOrEmpty(picked) && !picked.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    current = picked;
                    continue;
                }
            }

            return current; // terminal — resolve as ItemDef or leave alone
        }
        return current;
    }

    /// <summary>Parse the RHS of a <c>[DEFNAME ...]</c> entry. Handles
    /// <c>{ a w b w }</c> weighted lists, single <c>i_foo</c> aliases,
    /// and unweighted space-separated lists. Returns empty when the
    /// value doesn't look like an item selector.</summary>
    private static string PickFromDefValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return "";

        if (trimmed[0] == '{')
        {
            int close = trimmed.LastIndexOf('}');
            if (close < 0) close = trimmed.Length;
            string inner = trimmed.Substring(1, close - 1).Trim();
            // Tokens: defname weight defname weight … (or defname defname …)
            var tokens = inner.Split(new[] { ' ', '\t', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return "";

            // Detect weighted form: (name,number) pairs.
            var names = new List<string>();
            var weights = new List<int>();
            int i = 0;
            while (i < tokens.Length)
            {
                string name = tokens[i];
                int weight = 1;
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out int w))
                {
                    weight = Math.Max(1, w);
                    i += 2;
                }
                else
                {
                    i += 1;
                }
                names.Add(name);
                weights.Add(weight);
            }

            int total = 0;
            for (int k = 0; k < weights.Count; k++) total += weights[k];
            int roll = _rand.Next(total);
            for (int k = 0; k < names.Count; k++)
            {
                roll -= weights[k];
                if (roll < 0) return names[k];
            }
            return names[^1];
        }

        // Single alias: "random_pants_elven  i_elven_pants" form.
        int firstSpace = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return firstSpace < 0 ? trimmed : trimmed[..firstSpace];
    }

    /// <summary>Roll one template row's chance and amount (Source-X
    /// CItem::CreateHeader, CItem.cpp:461-488): every arg after the defname is
    /// either <c>R#</c> — a 1-in-# chance to create the row at all — or an
    /// amount expression (<c>3</c> / <c>{600 750}</c> dice). Returns false when
    /// the chance roll fails or the amount resolves to 0; otherwise the rolled
    /// amount (minimum 1).</summary>
    public static bool TryRollTemplateRow(TemplateEntry entry, out int amount)
    {
        amount = 1;
        foreach (string rawArg in entry.RawArgs)
        {
            string arg = rawArg.Trim();
            if (arg.Length == 0) continue;
            if ((arg[0] == 'R' || arg[0] == 'r') && arg.Length > 1 &&
                int.TryParse(arg.AsSpan(1), out int chance))
            {
                if (chance > 1 && _rand.Next(chance) != 0)
                    return false; // g_Rand.GetVal(x) != 0 → don't create
                continue;
            }
            amount = RollAmountExpr(arg);
            if (amount <= 0)
                return false; // Source-X: amount == 0 → no item
        }
        return true;
    }

    /// <summary>Evaluate a template amount expression: plain integer or a
    /// <c>{lo hi}</c> dice range (inclusive, Source-X Exp_GetWVal on a braced
    /// range). Unparseable input yields 0.</summary>
    public static int RollAmountExpr(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        string trimmed = expr.Trim();
        if (trimmed[0] == '{')
        {
            int close = trimmed.LastIndexOf('}');
            string inner = trimmed.Substring(1, (close < 0 ? trimmed.Length : close) - 1).Trim();
            var parts = inner.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int lo) && int.TryParse(parts[^1], out int hi))
            {
                if (hi < lo) (lo, hi) = (hi, lo);
                return _rand.Next(lo, hi + 1);
            }
            return parts.Length == 1 && int.TryParse(parts[0], out int single) ? single : 0;
        }
        return int.TryParse(trimmed, out int plain) ? plain : 0;
    }

    /// <summary>Enumerate every item the sequential-spawn template
    /// should produce. For VENDOR_S_* / VENDOR_B_* restock lists this
    /// returns (defname, amount) pairs. Random-pool templates fall
    /// back to a single random pick exposed as one entry.</summary>
    public static IEnumerable<(string DefName, int Amount)> EnumerateSequential(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname))
            yield break;
        var key = defname.Trim();
        var tpl = DefinitionLoader.GetTemplateDef(key);
        if (tpl == null)
        {
            // Unknown template name — Source-X behaviour treats this as a
            // single direct itemdef pick. Yield as-is and let the caller
            // try to resolve it through the regular ITEMDEF lookup.
            yield return (key, 0);
            yield break;
        }
        if (tpl.ItemEntries.Count > 0)
        {
            foreach (var entry in tpl.ItemEntries)
            {
                if (entry.IsContainer) continue; // loot-template CONTAINER rows — not vendor stock
                yield return (entry.DefName, entry.Amount);
            }
            yield break;
        }
        // Random pool only → emit one pick.
        string picked = PickByWeight(tpl.RandomEntries);
        if (!string.IsNullOrEmpty(picked))
            yield return (picked, 0);
    }

    /// <summary>
    /// Optional diagnostic sink — wired by Program.cs to bridge static
    /// helpers into the central logger without forcing every static
    /// method to plumb an ILogger argument.
    /// </summary>
    public static Action<string>? Diagnostic { get; set; }

    /// <summary>Resolve a single-item defname to its full ItemDef
    /// resource index (used as the key into DefinitionLoader._itemDefs).
    /// For numeric ITEMDEFs (<c>[ITEMDEF 0xF0E]</c>) this equals the
    /// graphic ID; for string-named ITEMDEFs (<c>[ITEMDEF i_potion_refresh]</c>)
    /// it's a 32-bit string hash and DOES NOT equal the wire graphic —
    /// callers must do <c>DefinitionLoader.GetItemDef(rid).DispIndex</c>
    /// to get the actual UO graphic. Returns 0 when the name is not
    /// an ItemDef (e.g. still a Template, or unknown).</summary>
    public static int ResolveItemDefIndex(ResourceHolder resources, string defname)
    {
        if (string.IsNullOrWhiteSpace(defname) || resources == null)
            return 0;
        var rid = resources.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
    }

    /// <summary>Resolve a single-item defname to its wire-side UO
    /// graphic (DispIdFull). Honors Source-X <c>ID=</c> / <c>DISPID=</c>
    /// (becomes <c>ItemDef.DispIndex</c>) and <c>DUPEITEM=</c> alias
    /// chains; falls back to the numeric resource index for plain
    /// <c>[ITEMDEF 0xNNNN]</c> blocks. Returns 0 when the defname is
    /// unknown / not an ItemDef.</summary>
    public static ushort ResolveDispId(ResourceHolder resources, string defname)
    {
        int idx = ResolveItemDefIndex(resources, defname);
        if (idx == 0) return 0;
        var idef = DefinitionLoader.GetItemDef(idx);
        if (idef != null)
        {
            if (idef.DispIndex != 0) return idef.DispIndex;
            if (idef.DupItemId != 0) return idef.DupItemId;
        }
        // Plain numeric [ITEMDEF 0xNNNN] — index IS the graphic, but
        // only when it fits in 16 bits. String-hash indexes (>0xFFFF)
        // would otherwise truncate to garbage graphics (e.g. lava /
        // window-shutter / elven-plate were classic symptoms of this
        // exact bug — i_potion_refresh hashed to 0x40B2C7E1, the
        // (ushort) cast yielded 0xC7E1 which happens to be a random
        // valid UO tile). Refuse to truncate.
        return idx <= 0xFFFF ? (ushort)idx : (ushort)0;
    }

    /// <summary>Backwards-compat alias. Prefer <see cref="ResolveDispId"/>
    /// (returns the wire graphic) or <see cref="ResolveItemDefIndex"/>
    /// (returns the def storage key) at new call-sites.</summary>
    public static ushort ResolveItemId(ResourceHolder resources, string defname)
        => ResolveDispId(resources, defname);


    // ---- Building real items out of a [TEMPLATE] recipe -------------------
    //
    // A TEMPLATE is a recipe, not an itemdef. Source-X reads it line by line in
    // CItem::ReadTemplate (CItem.cpp:600): a CONTAINER row becomes the container the
    // rows after it are created in, an ITEM row is created in whatever container is
    // current, and ANY OTHER line is applied with r_LoadVal to the item the recipe
    // most recently created (:686). Each create row goes through CreateHeader
    // (:461), which rolls the row's R# chance, evaluates its amount, refuses the row
    // outright when that amount is zero, and accepts a nested TEMPLATE as readily as
    // an ITEMDEF. The object handed back is the first container the recipe opened,
    // or the last item when it opened none (:691).

    /// <summary>How deep a recipe may reference other recipes before we stop. A
    /// cycle in the data must end in a bounded walk, not a stack overflow.</summary>
    private const int MaxTemplateDepth = 8;

    /// <summary>Run a whole recipe and return the object its caller receives.
    /// <paramref name="into"/> is the container the recipe starts in (null for a
    /// free-standing build); rows may replace it as they open containers of their
    /// own.</summary>
    public static SphereNet.Game.Objects.Items.Item? BuildTemplate(
        SphereNet.Game.World.GameWorld world, int templateIndex,
        SphereNet.Game.Objects.Items.Item? into = null, int depth = 0)
    {
        var tdef = DefinitionLoader.GetTemplateDef(templateIndex);
        if (tdef == null || depth >= MaxTemplateDepth)
            return null;
        return WalkRows(world, tdef, into, depth, skipLeadingContainer: false, topCont: null);
    }

    /// <summary>Fill an ALREADY-CREATED container from a recipe whose leading
    /// CONTAINER row the caller has built itself (the spawner does this: it needs to
    /// own the object so it can apply its own spawn semantics to it). The rest of
    /// the recipe runs normally against that container, property rows included.</summary>
    public static void FillTemplateContents(
        SphereNet.Game.World.GameWorld world,
        SphereNet.Game.Objects.Items.Item container, int templateIndex)
    {
        var tdef = DefinitionLoader.GetTemplateDef(templateIndex);
        if (tdef == null || tdef.Rows.Count == 0) return;
        if (tdef.Rows[0].Kind != TemplateRowKind.Container) return;   // no box, nothing to fill
        WalkRows(world, tdef, container, depth: 0, skipLeadingContainer: true, topCont: container);
    }

    private static SphereNet.Game.Objects.Items.Item? WalkRows(
        SphereNet.Game.World.GameWorld world, TemplateDef tdef,
        SphereNet.Game.Objects.Items.Item? cont, int depth,
        bool skipLeadingContainer, SphereNet.Game.Objects.Items.Item? topCont)
    {
        SphereNet.Game.Objects.Items.Item? item = topCont;
        bool skipped = !skipLeadingContainer;

        foreach (var row in tdef.Rows)
        {
            switch (row.Kind)
            {
                case TemplateRowKind.Container:
                {
                    if (!skipped)
                    {
                        // The caller already built this one and is holding it.
                        skipped = true;
                        continue;
                    }
                    var made = CreateHeaderRow(world, row.Entry!, cont, depth);
                    if (made == null) continue;
                    item = made;
                    // The new container becomes the destination for what follows
                    // (ITC_CONTAINER, CItem.cpp:631). Reusing the outer container
                    // left a second box empty and its contents beside it. When the
                    // row does not actually name a container the destination becomes
                    // NOTHING, exactly as the reference's failed dynamic_cast leaves
                    // pCont null, and the following rows stop rather than spilling.
                    cont = made.ItemType is ItemType.Container or ItemType.ContainerLocked
                        ? made : null;
                    if (cont != null)
                        topCont ??= made;
                    continue;
                }

                case TemplateRowKind.Item:
                {
                    // Nowhere to put it yet and something has already been made:
                    // upstream stops rather than dropping items loose (:643).
                    if (cont == null && item != null) continue;
                    var made = CreateHeaderRow(world, row.Entry!, cont, depth);
                    if (made != null) item = made;
                    continue;
                }

                default:
                    // Applies to the item the recipe last created - which for the
                    // rows before any ITEM is the container itself.
                    item?.TrySetProperty(row.Key, row.Value);
                    continue;
            }
        }

        return topCont ?? item;
    }

    /// <summary>Create one recipe row: roll its chance and amount, resolve what it
    /// names, and put the result in <paramref name="cont"/>. The port of
    /// CItem::CreateHeader (CItem.cpp:461). Returns null when the row declines to
    /// produce anything - a failed R# roll, an amount of zero, or a name that
    /// resolves to nothing.</summary>
    private static SphereNet.Game.Objects.Items.Item? CreateHeaderRow(
        SphereNet.Game.World.GameWorld world, TemplateEntry entry,
        SphereNet.Game.Objects.Items.Item? cont, int depth)
    {
        // "R5" is a 1-in-5 chance to create the row at all; an amount of 0 means the
        // row is switched off. Both used to be ignored, so a disabled reward was
        // still produced and a seven-piece one arrived as a single item.
        if (!TryRollTemplateRow(entry, out int amount))
            return null;

        string picked = PickRandomItemDefName(entry.DefName);
        if (string.IsNullOrWhiteSpace(picked))
            return null;

        if (!TryResolveTemplateResource(picked, out ResourceId rid))
            return null;

        // A row may name another RECIPE. Upstream's CreateHeader accepts
        // RES_TEMPLATE and reads it in the current container (:515 -> :555);
        // collapsing the resource to an itemdef index lost that and produced
        // nothing at all.
        if (rid.Type == ResType.Template)
            return BuildTemplate(world, rid.Index, cont, depth + 1);

        var made = world.CreateItem();
        if (!ItemDefHelper.ApplyInstanceMetadata(made, rid.Index))
        {
            if (rid.Index is <= 0 or > ushort.MaxValue)
            {
                world.RemoveItem(made);
                return null;
            }
            made.BaseId = (ushort)rid.Index;
        }
        made.FireCreateTrigger();

        // Amount before the container, so stacking sees the final quantity
        // (CItem.cpp:530 makes the same ordering point).
        if (amount != 1)
            made.Amount = (ushort)Math.Clamp(amount, 1, ushort.MaxValue);

        cont?.AddItem(made);
        return made;
    }

    /// <summary>Resolve what a recipe row names, KEEPING the resource type: a name
    /// first, then a Sphere number. Upstream reads the number with Exp_GetDWVal
    /// (ResourceGetID_EatStr, CResourceHolder.cpp:102), so only a leading zero means
    /// hexadecimal - reading every number as hex turned a decimal id into a
    /// different, perfectly valid item.</summary>
    public static bool TryResolveTemplateResource(string token, out ResourceId rid)
    {
        rid = ResourceId.Invalid;
        string name = (token ?? "").Trim();
        if (name.Length == 0) return false;

        var resolved = DefinitionLoader.StaticResources?.ResolveDefName(name);
        if (resolved is { IsValid: true } r && r.Type is ResType.ItemDef or ResType.Template)
        {
            rid = r;
            return true;
        }

        if (!SphereNet.Core.Types.ScriptNumber.TryParseToken(name, out long numeric) ||
            numeric is <= 0 or > int.MaxValue)
            return false;
        rid = new ResourceId(ResType.ItemDef, (int)numeric);
        return true;
    }

    /// <summary>The itemdef index a recipe's first resolvable create row names - what
    /// a caller that wants to build the object itself needs to know. 0 when the
    /// recipe resolves to nothing.</summary>
    public static int ResolveTemplatePrimary(int templateIndex)
    {
        var tdef = DefinitionLoader.GetTemplateDef(templateIndex);
        if (tdef == null) return 0;
        foreach (var entry in tdef.ItemEntries)
        {
            int idx = ResolveTemplateEntryIndex(entry.DefName);
            if (idx > 0) return idx;
        }
        return 0;
    }

    /// <summary>Resolve one recipe row to an itemdef storage index, discarding the
    /// resource type. Prefer <see cref="TryResolveTemplateResource"/>; this remains
    /// for the callers that build the object themselves.</summary>
    public static int ResolveTemplateEntryIndex(string defName) =>
        TryResolveTemplateResource(defName, out ResourceId rid) && rid.Type == ResType.ItemDef
            ? rid.Index
            : 0;


    private static string PickByWeight(List<TemplateEntry> pool)
    {
        if (pool.Count == 0) return "";
        int total = 0;
        foreach (var e in pool) total += Math.Max(1, e.Weight);
        int roll = _rand.Next(total);
        foreach (var e in pool)
        {
            roll -= Math.Max(1, e.Weight);
            if (roll < 0) return e.DefName;
        }
        return pool[^1].DefName;
    }
}
