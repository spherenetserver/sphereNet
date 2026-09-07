using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// One selection entry inside a TEMPLATE block. Source-X packs use either
/// ID=itemdef (single pick) or ITEMID=itemdef,weight (weighted list).
/// Amount is optional and defaults to 1.
/// </summary>
public sealed class TemplateEntry
{
    /// <summary>Defname of the item or nested template to spawn. May be an
    /// inline weighted pool <c>{ a w b w }</c> — Source-X resolves the braces
    /// in defname position (CItem::CreateHeader → ResourceGetID).</summary>
    public string DefName { get; init; } = "";
    /// <summary>Selection weight when picking randomly (default 1).</summary>
    public int Weight { get; init; } = 1;
    /// <summary>Stack amount for stackable items (e.g. gold). 0 = let the
    /// ItemDef default decide.</summary>
    public int Amount { get; init; }
    /// <summary>Raw args after the defname, verbatim (Source-X
    /// CItem::CreateHeader "ITEM=#id,#amount,R#chance"): each token is either
    /// an amount expression (<c>3</c>, <c>{600 750}</c>) or a 1-in-X chance
    /// (<c>R5</c>). Empty when the row had no args.</summary>
    public string[] RawArgs { get; init; } = [];
    /// <summary>True for a CONTAINER= row — following ITEM rows spawn inside
    /// this container (Source-X ReadTemplate ITC_CONTAINER).</summary>
    public bool IsContainer { get; init; }
}

/// <summary>What one line of a template recipe is.</summary>
public enum TemplateRowKind
{
    /// <summary>An <c>ITEM=</c> / <c>ITEMNEWBIE=</c> line.</summary>
    Item,
    /// <summary>A <c>CONTAINER=</c> line. It becomes the container the rows after
    /// it are created in (Source-X ITC_CONTAINER, CItem.cpp:631).</summary>
    Container,
    /// <summary>Any other line. Source-X applies it with r_LoadVal to the item the
    /// recipe most recently created (ReadTemplate, CItem.cpp:686), which is how a
    /// recipe gives its reward a NAME, a COLOR or a TAG.</summary>
    Property,
}

/// <summary>One line of a template recipe, IN THE ORDER IT WAS WRITTEN. Order is
/// the whole grammar here: a CONTAINER line changes where the following items go,
/// and a property line belongs to whatever was created immediately before it.</summary>
public sealed class TemplateRow
{
    public TemplateRowKind Kind { get; init; }
    /// <summary>The create arguments, for an Item or Container row.</summary>
    public TemplateEntry? Entry { get; init; }
    /// <summary>Property key, for a Property row.</summary>
    public string Key { get; init; } = "";
    /// <summary>Property value, for a Property row.</summary>
    public string Value { get; init; } = "";
}

/// <summary>
/// Script <c>[TEMPLATE name]</c> resource. Can behave as:
///   - a <b>list</b> template — every ITEM= line gets spawned (used by NPC
///     equip blocks: ITEM=random_shirts adds one shirt);
///   - a <b>random pick</b> template — ID= / ITEMID= lines build a
///     weighted pool and one entry is picked per resolve.
/// Source-X has both modes in CItemBase / CCharBase template handling.
/// </summary>
public sealed class TemplateDef : ResourceLink
{
    /// <summary>Weighted entries from ID=/ITEMID= lines — random pick.</summary>
    public List<TemplateEntry> RandomEntries { get; } = [];

    /// <summary>Sequential entries from ITEM= lines — each spawned in turn.</summary>
    public List<TemplateEntry> ItemEntries { get; } = [];

    /// <summary>Every recipe line in source order, create commands and property
    /// lines alike. <see cref="ItemEntries"/> is the flat create-only view the older
    /// callers use; this is what a faithful ReadTemplate walk needs, because the
    /// meaning of a line depends on what came before it.</summary>
    public List<TemplateRow> Rows { get; } = [];

    /// <summary>True when this template only has random-pick entries
    /// (i.e. random_hats, random_shirts). False when it enumerates items
    /// that must all be spawned (e.g. a VENDOR_S_* restock list).</summary>
    public bool IsRandomPick => RandomEntries.Count > 0 && ItemEntries.Count == 0;

    public TemplateDef(ResourceId id) : base(id) { }
}
