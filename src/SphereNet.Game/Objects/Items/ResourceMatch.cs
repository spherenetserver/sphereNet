using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Objects.Items;

/// <summary>
/// The one place a resource is matched against an item and a stock is counted or
/// spent. It is Source-X's CItem::IsResourceMatch (CItem.cpp:6034) and
/// CContainer::ContentConsumeTest / ContentConsume / ContentCount
/// (CContainer.cpp:374-473), and every caller that asks "does this character or
/// container hold N of X" goes through it: crafting's test and consumption, repair,
/// SKILLTEST, the CONTCONSUME / RESCOUNT / RESTEST script surface, and gold.
///
/// Two rules carry the parity:
///   * Identity. An ITEMDEF resource is the item's DEFINITION, not its graphic: two
///     definitions sharing an art id are different resources. A DUPEITEM graphic is
///     its master's definition (CItemBase::FindItemBase). With strict comparison off,
///     boards stand in for logs and leather for hides - in that direction only.
///     A TYPEDEF resource matches the item's own TYPE.
///   * Reach. The walk descends into a sub-container only when it may be searched
///     (CItemContainer::IsSearchable: not a bank box, vendor box, trade window or
///     locked container); gold alone also reaches the bank and skips only a locked
///     container. The test and the consumption walk the same tree, so what is counted
///     is what is spent.
/// </summary>
public static class ResourceMatch
{
    /// <summary>EF_Item_Strict_Comparison (CServerConfig.h:49).</summary>
    public const int EfItemStrictComparison = 0x0000040;

    /// <summary>EF_Item_Strict_Comparison: when set, logs/boards and hides/leather are
    /// never the same resource.</summary>
    public static bool StrictComparison { get; set; }

    // The two hardcoded alternatives (CItem.cpp:6050-6063). They are item ids in
    // the reference, compared against the definition's own id.
    private const int ItemIdLog1 = 0x1BDD;
    private const int ItemIdBoard1 = 0x1BD7;
    private const int ItemIdHides = 0x1078;
    private const int ItemIdLeather1 = 0x1067;

    /// <summary>Deep enough for any real container nesting; a guard against a
    /// corrupt containment loop, not a gameplay limit.</summary>
    private const int MaxDepth = 64;

    /// <summary>The t_gold resource (RES_TYPEDEF, IT_GOLD) - the one resource whose
    /// walk reaches into the bank box.</summary>
    public static readonly ResourceId Gold = new(ResType.TypeDef, (int)ItemType.Gold);

    public static ResourceId ForItemDef(int defIndex) => new(ResType.ItemDef, defIndex);
    public static ResourceId ForType(ItemType type) => new(ResType.TypeDef, (int)type);

    /// <summary>A script's resource token as a resource id: a defname (i_*, t_*, or a
    /// [DEFNAME] constant), else a Sphere number taken as an ITEMDEF index - the way
    /// ResourceGetID(RES_ITEMDEF, name) reads it. Invalid when nothing resolves.</summary>
    public static ResourceId Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ResourceId.Invalid;
        string name = token.Trim();

        var resources = DefinitionLoader.StaticResources;
        if (resources != null)
        {
            var rid = resources.ResolveDefName(name);
            if (rid.IsValid)
            {
                if (rid.Type == ResType.DefName)
                    return resources.TryResolveDefNameValue(name, out long value) && value > 0
                        ? ForItemDef((int)value)
                        : ResourceId.Invalid;
                return rid;
            }
        }

        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
        {
            var type = Item.ParseItemType(name);
            return type == ItemType.Invalid ? ResourceId.Invalid : ForType(type);
        }

        if (ScriptNumber.TryParseToken(name, out long number) && number is > 0 and <= 0x00FFFFFF)
            return ForItemDef((int)number);

        // Last resort for a host with no script resources wired (a defname the item
        // layer can still turn into a graphic).
        ushort graphic = Item.ResolveDefName?.Invoke(name) ?? 0;
        return graphic != 0 ? ForItemDef(graphic) : ResourceId.Invalid;
    }

    /// <summary>A name in a resource LIST, read as CResourceQty::Load reads it
    /// (ResourceGetID_EatStr with RES_UNKNOWN, CResourceHolder.cpp:99): the name must
    /// carry its own resource type - an item/type definition, a skill. A bare number
    /// carries none and is a bad entry, exactly as upstream ("Bad resource list id").</summary>
    public static ResourceId ResolveListName(string? token,
        SphereNet.Scripting.Resources.ResourceHolder? resources = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ResourceId.Invalid;
        string name = token.Trim();

        resources ??= DefinitionLoader.StaticResources;
        if (resources != null)
        {
            var rid = resources.ResolveDefName(name);
            if (rid.IsValid)
                return rid.Type == ResType.DefName ? ResourceId.Invalid : rid;
        }

        if (SkillNames.TryResolve(name, out var skill) && skill != SkillType.None)
            return new ResourceId(ResType.SkillDef, (int)skill);

        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
        {
            var type = Item.ParseItemType(name);
            return type == ItemType.Invalid ? ResourceId.Invalid : ForType(type);
        }
        return ResourceId.Invalid;
    }

    /// <summary>One loaded entry of a resource list.</summary>
    public readonly record struct ResourceQty(ResourceId Rid, long Qty);

    /// <summary>CResourceQtyArray::Load (CResourceQty.cpp:181): comma-separated entries,
    /// a lone "0" clears what came before, a repeated resource replaces its earlier
    /// entry, and the first entry that cannot be read ends the list.</summary>
    public static List<ResourceQty> LoadList(string? raw,
        SphereNet.Scripting.Resources.ResourceHolder? resources = null)
    {
        var list = new List<ResourceQty>();
        if (string.IsNullOrEmpty(raw))
            return list;
        foreach (string part in raw.Split(','))
        {
            if (part == "0")
            {
                list.Clear();
                continue;
            }
            var entry = SphereNet.Scripting.Resources.ResourceQtyList.ParseEntry(part);
            if (entry.Name.Length == 0)
                break;
            var rid = ResolveListName(entry.Name, resources);
            if (!rid.IsValid)
                break;
            int existing = list.FindIndex(r => r.Rid == rid);
            if (existing >= 0)
                list[existing] = new ResourceQty(rid, entry.Quantity);
            else
                list.Add(new ResourceQty(rid, entry.Quantity));
        }
        return list;
    }

    /// <summary>TAG.MATOVERRIDE_&lt;defname&gt; (CContainer::ResourceConsume,
    /// CContainer.cpp:608-617): a character may have an ITEMDEF resource replaced by
    /// another one. The tag is named after the resource's defname (or its hex index when
    /// it has none) and its value is evaluated to a definition.</summary>
    public static ResourceId MaterialOverride(Character ch, ResourceId rid)
    {
        if (rid.Type != ResType.ItemDef)
            return rid;
        var def = DefinitionLoader.GetItemDef(rid.Index);
        string name = !string.IsNullOrWhiteSpace(def?.DefName)
            ? def!.DefName!
            : $"0{rid.Index:x}";
        if (!ch.TryGetTag("matoverride_" + name, out string? value) || string.IsNullOrWhiteSpace(value))
            return rid;
        var over = Resolve(value);
        return over.IsValid && over.Index > 0 ? ForItemDef(over.Index) : rid;
    }

    /// <summary>CContainer::ResourceConsume (CContainer.cpp:572) on a character: how
    /// many whole replications the list allows (test) or was paid for. A skill entry
    /// is a level the character must have, an ITEMDEF entry honours MATOVERRIDE, and
    /// a real spend of several replications tests the count first.</summary>
    public static int ResourceConsume(Character ch, IReadOnlyList<ResourceQty> list,
        int replication, bool test) => ResourceConsumeCore(ch, null, list, replication, test);

    /// <summary>CContainer::ResourceConsume on a container item: skill entries are
    /// skipped (there is no character to ask) and no override applies.</summary>
    public static int ResourceConsume(Item container, IReadOnlyList<ResourceQty> list,
        int replication, bool test) => ResourceConsumeCore(null, container, list, replication, test);

    private static int ResourceConsumeCore(Character? ch, Item? container,
        IReadOnlyList<ResourceQty> list, int replication, bool test)
    {
        if (replication <= 0)
            replication = 1;
        if (!test && replication > 1)
            replication = ResourceConsumeCore(ch, container, list, replication, true);

        long qtyMin = int.MaxValue;
        foreach (var entry in list)
        {
            long resQty = entry.Qty;
            if (resQty <= 0)
                continue;
            long total = resQty * replication;
            var rid = entry.Rid;
            if (rid.Type == ResType.SkillDef)
            {
                if (ch == null)
                    continue;
                if (ch.GetSkill((SkillType)rid.Index) < resQty)
                    return 0;
                continue;
            }
            if (ch != null)
                rid = MaterialOverride(ch, rid);

            long left = ch != null
                ? (test ? ConsumeTest(ch, rid, total) : Consume(ch, rid, total))
                : (test ? ConsumeTest(container!, rid, total) : Consume(container!, rid, total));
            long cur = (total - left) / resQty;
            if (cur < qtyMin)
                qtyMin = cur;
        }
        return qtyMin == int.MaxValue ? replication : (int)qtyMin;
    }

    /// <summary>CContainer::ResourceConsumePart (CContainer.cpp:534) on a character:
    /// each entry scaled by <paramref name="percent"/> (IMulDiv rounding), tested or
    /// spent on its own - a real spend takes what is there even when an entry falls
    /// short. Returns the index of the LAST entry that was short, or -1. No
    /// MATOVERRIDE and no skill handling here: a skill entry simply is not found.</summary>
    public static int ResourceConsumePart(Character ch, IReadOnlyList<ResourceQty> list,
        int replication, int percent, bool test, uint arg = 0)
    {
        if (percent <= 0)
            return -1;
        int missing = -1;
        for (int i = 0; i < list.Count; i++)
        {
            long resQty = list[i].Qty;
            if (resQty <= 0)
                continue;
            long total = resQty * replication;
            if (total <= 0)
                continue;
            total = IMulDiv(total, percent, 100);
            if (total <= 0)
                continue;
            long left = test
                ? ConsumeTest(ch, list[i].Rid, total, arg)
                : Consume(ch, list[i].Rid, total, arg);
            if (left != 0)
                missing = i;
        }
        return missing;
    }

    /// <summary>Source-X IMulDiv (common.h:192): a*b/c rounded half up, one less for
    /// a negative product.</summary>
    public static long IMulDiv(long a, long b, long c)
    {
        long ab = a * b;
        return (ab + c / 2) / c - (ab < 0 ? 1 : 0);
    }

    /// <summary>The ITEMDEF resource index an item IS - Item_GetDef()->GetResourceID().
    /// A named definition is its own index (SCRIPTDEF / ITEMDEF routing tags), a
    /// numbered one its id; a DUPEITEM graphic resolves to its master, as
    /// CItemBase::FindItemBase hands the master back for a dupe stub. The dupe link is
    /// one hop and only to a master that is itself a base (MakeDupeReplacement refuses
    /// a chain as a "circle").</summary>
    public static int ItemDefIndexOf(Item item)
    {
        int index = ItemDefHelper.ResolveInstanceDefIndex(item);
        var def = DefinitionLoader.GetItemDef(index);
        if (def == null || def.DupItemId == 0 || def.DupItemId == index)
            return index;
        int masterIndex = def.DupItemId;
        var master = DefinitionLoader.GetItemDef(masterIndex);
        if (master == null)
            return index;                       // DUPEITEM not exist: stays itself
        if (master.DupItemId != 0 && master.DupItemId != masterIndex &&
            DefinitionLoader.GetItemDef(master.DupItemId) != null)
            return index;                       // master is a dupe itself: "circle"
        return masterIndex;
    }

    /// <summary>CItem::IsResourceMatch (CItem.cpp:6034). <paramref name="arg"/> is the
    /// reference's dwArg: for t_map it is the map's top/left pair and for t_key the
    /// lock it opens (both MORE1) - zero means "any".</summary>
    public static bool IsMatch(Item item, ResourceId rid, uint arg = 0)
    {
        if (item.IsDeleted || !rid.IsValid)
            return false;

        if (rid.Type == ResType.ItemDef)
        {
            int own = ItemDefIndexOf(item);
            if (own == rid.Index)
                return true;
            if (StrictComparison)
                return false;
            return rid.Index switch
            {
                // boards can be used as logs (but logs can't be used as boards)
                ItemIdLog1 => own == ItemIdBoard1,
                // leather can be used as hide (but hide can't be used as leather)
                ItemIdHides => own == ItemIdLeather1,
                _ => false,
            };
        }

        if (rid.Type == ResType.TypeDef)
        {
            var type = (ItemType)rid.Index;
            // IsType: the instance's TYPE alone - gold included, whatever its graphic.
            if (item.ItemType != type)
                return false;
            if (arg != 0 && type is ItemType.Map or ItemType.Key && item.More1 != arg)
                return false;           // a different map / a key to another lock
            return true;
        }

        return false;
    }

    /// <summary>May the walk for <paramref name="rid"/> descend into
    /// <paramref name="container"/>? Gold skips only a locked container; everything
    /// else needs a searchable one (CContainer.cpp:400-410 / 442-458).</summary>
    public static bool CanSearchInto(Item container, ResourceId rid) =>
        rid == Gold
            ? container.ItemType != ItemType.ContainerLocked
            : container.IsSearchableContainer;

    // --- container-rooted walk (CContainer of an item) ----------------------

    /// <summary>ContentConsumeTest over a container's contents: how much of
    /// <paramref name="amount"/> is still missing (0 = all present).
    /// <paramref name="filter"/> narrows the match further (a chosen material hue);
    /// it never widens it.</summary>
    public static long ConsumeTest(Item container, ResourceId rid, long amount, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        TestIn(container.Contents, rid, amount, arg, filter, 0);

    /// <summary>ContentCount: everything the walk reaches.</summary>
    public static long Count(Item container, ResourceId rid, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        long.MaxValue - ConsumeTest(container, rid, long.MaxValue, arg, filter);

    /// <summary>ContentConsume: spend up to <paramref name="amount"/>; returns what
    /// could not be found. Like the reference it takes what is there even when the
    /// total falls short - callers that must not spend a partial bill test first.</summary>
    public static long Consume(Item container, ResourceId rid, long amount, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        ConsumeIn(container.Contents.ToArray(), rid, amount, arg, filter, 0);

    // --- character-rooted walk (CChar as a CContainer: what it wears) -------

    public static long ConsumeTest(Character ch, ResourceId rid, long amount, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        TestIn(Worn(ch), rid, amount, arg, filter, 0);

    public static long Count(Character ch, ResourceId rid, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        long.MaxValue - ConsumeTest(ch, rid, long.MaxValue, arg, filter);

    public static long Consume(Character ch, ResourceId rid, long amount, uint arg = 0,
        Func<Item, bool>? filter = null) =>
        ConsumeIn(Worn(ch), rid, amount, arg, filter, 0);

    /// <summary>Every item the walk for <paramref name="rid"/> would count, in walk
    /// order - for a caller that must look at the matching stock (its hues) rather
    /// than just total it.</summary>
    public static IEnumerable<Item> EnumerateMatches(Character ch, ResourceId rid, uint arg = 0)
    {
        var found = new List<Item>();
        CollectIn(Worn(ch), rid, arg, found, 0);
        return found;
    }

    public static IEnumerable<Item> EnumerateMatches(Item container, ResourceId rid, uint arg = 0)
    {
        var found = new List<Item>();
        CollectIn(container.Contents, rid, arg, found, 0);
        return found;
    }

    // --- CContainer FINDID / FINDTYPE references (r_GetRefContainer) -------

    /// <summary>The token of a FINDID / FINDTYPE reference as
    /// ResourceGetID_EatStr(<paramref name="defaultType"/>) reads it
    /// (CContainer.cpp:652-685): a loaded definition keeps its own type (an ITEMDEF, a
    /// TYPEDEF, ...); a [DEFNAME] constant or a bare number is an index of the default
    /// type; a t_ name the engine knows natively is that TYPEDEF.</summary>
    public static ResourceId ResolveRefToken(string? token, ResType defaultType)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ResourceId.Invalid;
        string name = token.Trim();

        var resources = DefinitionLoader.StaticResources;
        if (resources != null)
        {
            var rid = resources.ResolveDefName(name);
            if (rid.IsValid)
            {
                if (rid.Type != ResType.DefName)
                    return rid;
                return resources.TryResolveDefNameValue(name, out long constant) &&
                       constant is > 0 and <= 0x00FFFFFF
                    ? new ResourceId(defaultType, (int)constant)
                    : ResourceId.Invalid;
            }
        }

        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
        {
            var type = Item.ParseItemType(name);
            return type == ItemType.Invalid ? ResourceId.Invalid : ForType(type);
        }

        if (ScriptNumber.TryParseToken(name, out long number) && number is > 0 and <= 0x00FFFFFF)
            return new ResourceId(defaultType, (int)number);

        if (defaultType == ResType.ItemDef)
        {
            ushort graphic = Item.ResolveDefName?.Invoke(name) ?? 0;
            if (graphic != 0)
                return ForItemDef(graphic);
        }
        return ResourceId.Invalid;
    }

    /// <summary>CContainer::ContentFind (CContainer.cpp:216) on a container item: the
    /// first item that matches <paramref name="rid"/>, looking inside each searchable
    /// sub-container right after the container itself.</summary>
    public static Item? ContentFind(Item container, ResourceId rid) =>
        FindIn(container.Contents, rid, 0);

    /// <summary>ContentFind on a character: what it wears, in layer order (the pack
    /// searched where it stands), then its memories - which Source-X also wears, on
    /// the memory layer after the pack.</summary>
    public static Item? ContentFind(Character ch, ResourceId rid) =>
        FindIn(Worn(ch), rid, 0) ?? FindIn(ch.Memories, rid, 0);

    private static Item? FindIn(IReadOnlyList<Item> contents, ResourceId rid, int depth)
    {
        if (!rid.IsValid || rid.Index == 0 || depth > MaxDepth)
            return null;
        foreach (var item in contents)
        {
            if (item.IsDeleted) continue;
            if (IsMatch(item, rid))
                return item;
            if (item.ContentCount > 0 && item.IsSearchableContainer)
            {
                var inner = FindIn(item.Contents, rid, depth + 1);
                if (inner != null)
                    return inner;
            }
        }
        return null;
    }

    /// <summary>Split "&lt;token&gt;[.&lt;rest&gt;]" the way EatStr + SKIP_SEPARATORS
    /// do: the token ends at the first dot.</summary>
    public static (string Token, string Tail) SplitRef(string chain)
    {
        chain = chain.Trim();
        int dot = chain.IndexOf('.');
        return dot < 0 ? (chain, "") : (chain[..dot].Trim(), chain[(dot + 1)..].Trim());
    }

    /// <summary>CScriptObj::r_WriteVal on a resolved reference (CScriptObj.cpp:497-528):
    /// nothing after it reads the UID (0 for no object), ISVALID reads 1/0, anything
    /// else is read off the object found - and reads 0 when there is none.</summary>
    public static bool ReadRef(Item? found, string rest, out string value)
    {
        if (found != null && found.IsDeleted)
            found = null;
        if (rest.Length == 0)
        {
            value = found == null ? "0" : $"0{found.Uid.Value:X}";
            return true;
        }
        if (rest.Equals("ISVALID", StringComparison.OrdinalIgnoreCase))
        {
            value = found == null ? "0" : "1";
            return true;
        }
        if (found == null)
        {
            value = "0";
            return true;
        }
        return found.TryGetProperty(rest, out value);
    }

    /// <summary>A verb addressed through a resolved reference
    /// ("FINDTYPE.t_x.REMOVE", "FINDID.i_x.COLOR 021"): the rest of the line is a
    /// whole verb line on the object found - verb, script function, then property, as
    /// the target's own r_Verb takes it. A missing object leaves the line handled and
    /// does nothing.</summary>
    public static bool ExecRef(Item? found, string rest, string args,
        SphereNet.Core.Interfaces.ITextConsole source)
    {
        if (found == null || found.IsDeleted || rest.Length == 0)
            return true;
        found.ExecuteVerbLine(rest, args, source);
        return true;
    }

    /// <summary>A character's own content list - its worn items, in layer order (the
    /// pack before the bank box). The pack is the character's Backpack even when it
    /// is held as a cached reference rather than in the layer slot.</summary>
    private static Item[] Worn(Character ch)
    {
        var list = new List<Item>();
        for (int layer = 0; layer < (int)Layer.Qty; layer++)
        {
            var worn = ch.GetEquippedItem((Layer)layer);
            if (layer == (int)Layer.Pack)
            {
                var pack = ch.Backpack;
                if (pack != null && !ReferenceEquals(pack, worn))
                    list.Add(pack);
            }
            if (worn != null && !worn.IsDeleted)
                list.Add(worn);
        }
        return list.ToArray();
    }

    private static bool Matches(Item item, ResourceId rid, uint arg, Func<Item, bool>? filter) =>
        IsMatch(item, rid, arg) && (filter == null || filter(item));

    private static long TestIn(IReadOnlyList<Item> contents, ResourceId rid, long amount,
        uint arg, Func<Item, bool>? filter, int depth)
    {
        if (!rid.IsValid || rid.Index == 0 || depth > MaxDepth)
            return amount;      // "from skills menus": nothing to look for
        foreach (var item in contents)
        {
            if (item.IsDeleted) continue;
            if (Matches(item, rid, arg, filter))
            {
                amount -= Math.Min(amount, item.Amount);
                if (amount <= 0)
                    break;
            }
            if (item.ContentCount > 0 && CanSearchInto(item, rid))
            {
                amount = TestIn(item.Contents, rid, amount, arg, filter, depth + 1);
                if (amount <= 0)
                    break;
            }
        }
        return amount;
    }

    private static long ConsumeIn(IReadOnlyList<Item> contents, ResourceId rid, long amount,
        uint arg, Func<Item, bool>? filter, int depth)
    {
        if (!rid.IsValid || rid.Index == 0 || depth > MaxDepth)
            return amount;
        foreach (var item in contents)
        {
            if (amount <= 0) break;
            if (item.IsDeleted) continue;
            if (Matches(item, rid, arg, filter))
            {
                long take = Math.Min(amount, item.Amount);
                amount -= take;
                if (take >= item.Amount)
                    item.RemoveFromWorld();
                else
                    item.Amount = (ushort)(item.Amount - take);
                if (amount <= 0)
                    break;
                if (item.IsDeleted)
                    continue;
            }
            if (item.ContentCount > 0 && CanSearchInto(item, rid))
            {
                amount = ConsumeIn(item.Contents.ToArray(), rid, amount, arg, filter, depth + 1);
                if (amount <= 0)
                    break;
            }
        }
        return amount;
    }

    private static void CollectIn(IReadOnlyList<Item> contents, ResourceId rid, uint arg,
        List<Item> found, int depth)
    {
        if (!rid.IsValid || rid.Index == 0 || depth > MaxDepth)
            return;
        foreach (var item in contents)
        {
            if (item.IsDeleted) continue;
            if (IsMatch(item, rid, arg))
                found.Add(item);
            if (item.ContentCount > 0 && CanSearchInto(item, rid))
                CollectIn(item.Contents, rid, arg, found, depth + 1);
        }
    }
}
