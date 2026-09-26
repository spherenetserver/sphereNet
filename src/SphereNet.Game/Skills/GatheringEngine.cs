using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Skills;

public readonly struct GatherResult
{
    /// <summary>A resource bit answered at the tile. False is Source-X's
    /// CheckNaturalResource returning nullptr: the tile is not of the type, the area
    /// has no resource of it, or something else lies there (Skill_Mining/_Fishing/
    /// _Lumberjack answer DEFMSG_*_1).</summary>
    public bool Handled { get; init; }
    public bool Success { get; init; }
    /// <summary>The bit is there and holds nothing (GetAmount() == 0, DEFMSG_*_2).</summary>
    public bool Depleted { get; init; }
    /// <summary>The skill check passed but Skill_NaturalResource_Create made nothing -
    /// a resource that reaps nothing, a script that cancelled @ResourceGather or took
    /// the amount to zero (the SUCCESS stage's own failure message).</summary>
    public bool CreateFailed { get; init; }
    public Item? Item { get; init; }
}

/// <summary>
/// Region-based resource gathering engine.
/// Routes Mining/Fishing/Lumberjacking through REGIONTYPE → REGIONRESOURCE definitions.
/// Per-tile invisible resource bits track depletion (Source-X
/// CWorldMap::CheckNaturalResource, CWorldMap.cpp:26-172).
/// </summary>
public sealed class GatheringEngine
{
    private readonly GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private static Random Rng => Random.Shared;

    private const string TagResourceMarker = "RESOURCE_MARKER";
    private const string TagSkillType = "RES_SKILL";
    // Remaining pool count. Kept in a TAG, not in the item's own amount field: that
    // setter floors at 1, and a resource bit must be able to stand at 0 until it
    // decays (CItem::ConsumeAmount "let there be 0 amount here til decay",
    // CItem.cpp:4357). Scripts still see it as AMOUNT - Item routes the AMOUNT
    // property of a resource bit here (see IsResourceBit) - and saves keep the tag.
    private const string TagPool = "RES_POOL";
    // The REGIONRESOURCE the bit was made from. Source-X keeps it in MORE1
    // (m_itResource.m_ridRes); it is written there too, and the tag stays for the
    // saves that were written before MORE1 carried it.
    private const string TagResourceId = "RES_ID";

    internal static int GetPool(Item marker) =>
        marker.TryGetTag(TagPool, out string? p) && int.TryParse(p, out int v) ? v : 0;

    private static void SetPool(Item marker, int value) =>
        marker.SetTag(TagPool, Math.Clamp(value, 0, ushort.MaxValue).ToString());

    /// <summary>Whether this item is a natural-resource bit, whose AMOUNT is its
    /// remaining pool.</summary>
    public static bool IsResourceBit(Item item) =>
        item.BaseId == MarkerGraphic &&
        item.TryGetTag(TagResourceMarker, out string? mk) && mk == "1";

    /// <summary>AMOUNT of a resource bit as scripts read it.</summary>
    public static int GetResourceBitAmount(Item bit) => GetPool(bit);

    /// <summary>AMOUNT of a resource bit as scripts write it - ARGO.AMOUNT in
    /// @ResourceFound / @ResourceGather sets the pool, zero included.</summary>
    public static void SetResourceBitAmount(Item bit, int amount) => SetPool(bit, amount);

    /// <summary>Source-X ITEMID_WorldGem. Resource bits use it so staff can see and
    /// inspect veins with AllShow; ATTR_INVIS keeps it hidden from players.</summary>
    internal const ushort MarkerGraphic = 0x1EA7;
    private const ushort MarkerBaseId = MarkerGraphic;

    /// <summary>Source-X ITEMID_KINDLING1 (uofiles_enums_itemid.h:273).</summary>
    internal const ushort KindlingGraphic = 0x0DE1;

    /// <summary>The resource TYPE each gathering skill works: the target handlers set
    /// m_atResource.m_ridType to IT_ROCK / IT_WATER / IT_TREE (CClientTarg.cpp:1810,
    /// :2265, :1893).</summary>
    public static ItemType ResourceTypeFor(SkillType skill) => skill switch
    {
        SkillType.Mining => ItemType.Rock,
        SkillType.Fishing => ItemType.Water,
        SkillType.Lumberjacking => ItemType.Tree,
        _ => ItemType.Invalid,
    };

    /// <summary>REGIONTYPE page for a resource type.</summary>
    private static string? TypeFilterFor(ItemType type) => type switch
    {
        ItemType.Rock => "t_rock",
        ItemType.Water => "t_water",
        ItemType.Tree => "t_tree",
        ItemType.Grass => "t_grass",
        _ => null,
    };

    /// <summary>The RES_SKILL tag bits of older builds carried instead of a TYPE.</summary>
    private static string LegacyTagFor(ItemType type) => type switch
    {
        ItemType.Rock => nameof(SkillType.Mining),
        ItemType.Water => nameof(SkillType.Fishing),
        ItemType.Tree => nameof(SkillType.Lumberjacking),
        _ => (TypeFilterFor(type) ?? type.ToString()).ToUpperInvariant(),
    };

    public GatheringEngine(GameWorld world, TriggerDispatcher? triggerDispatcher = null)
    {
        _world = world;
        _triggerDispatcher = triggerDispatcher;
    }

    /// <summary>
    /// The SUCCESS stage of a gathering skill: find the tile's resource bit, run the
    /// skill check against its SKILL curve, and build the reaped item. The item is
    /// NOT added to the backpack; the caller bounces it.
    /// </summary>
    /// <param name="kindling">A fencing weapon hacking at a tree: the success makes
    /// one kindling and takes one from the bit instead of reaping
    /// (Skill_Lumberjack, CCharSkill.cpp:1672-1678).</param>
    public GatherResult TryGatherForSink(Character ch, SkillType skill, Point3D target, bool kindling = false)
    {
        lock (_world)
            return TryGatherForSinkCore(ch, skill, target, probeOnly: false, kindling);
    }

    /// <summary>The START stage: what the tile has to say before the swing starts.
    ///
    /// Skill_Mining/_Fishing/_Lumberjack run CheckNaturalResource at SKTRIG_START
    /// with fTest set (CCharSkill.cpp:1449/1548/1647): the tile has to BE of the
    /// resource type, and a bit is found or created there. No bit answers
    /// DEFMSG_*_1, an empty one DEFMSG_*_2, both before a stroke is scheduled.
    /// Nothing is rolled or reaped here.</summary>
    public GatherResult ProbeResource(Character ch, SkillType skill, Point3D target)
    {
        lock (_world)
            return TryGatherForSinkCore(ch, skill, target, probeOnly: true, kindling: false);
    }

    private GatherResult TryGatherForSinkCore(Character ch, SkillType skill, Point3D target,
        bool probeOnly, bool kindling)
    {
        var type = ResourceTypeFor(skill);
        if (type == ItemType.Invalid)
            return new GatherResult { Handled = false };

        var bit = CheckNaturalResourceCore(ch, target, type, fTest: probeOnly);
        if (bit == null)
            return new GatherResult { Handled = false };
        if (GetPool(bit) <= 0)
            return new GatherResult { Handled = true, Depleted = true };
        if (probeOnly)
            return new GatherResult { Handled = true, Success = true };

        // Skill_NaturalResource_Setup: the difficulty is a sample of the bit's
        // resource SKILL curve /10, or -1 (bound to fail) when the bit names no
        // resource (CCharSkill.cpp:977-990).
        var resDef = ResolveBitResource(bit);
        if (resDef == null)
            return new GatherResult { Handled = true, Success = false };
        int difficulty = resDef.GetRandomSkillDifficulty(Rng);

        // A failed check is Skill_Fail: the stage answers 0 and says nothing - the
        // skill's own @Fail speaks, if the pack has one (CCharSkill.cpp:1403/1500).
        if (!SkillEngine.UseQuick(ch, skill, difficulty))
            return new GatherResult { Handled = true, Success = false };

        if (kindling)
        {
            // CCharSkill.cpp:1672-1678: one kindling bounced, one taken off the bit.
            ConsumeNaturalResource(bit, 1);
            var kindle = _world.CreateItem();
            if (!ItemDefHelper.ApplyInstanceMetadata(kindle, KindlingGraphic))
            {
                kindle.BaseId = KindlingGraphic;
                kindle.FireCreateTrigger();
            }
            return new GatherResult { Handled = true, Success = true, Item = kindle };
        }

        var item = NaturalResourceCreate(ch, skill, bit, resDef);
        if (item == null)
            return new GatherResult { Handled = true, Success = false, CreateFailed = true };
        return new GatherResult { Handled = true, Success = true, Item = item };
    }

    /// <summary>Source-X CChar::Skill_NaturalResource_Create (CCharSkill.cpp:992-1054).</summary>
    private Item? NaturalResourceCreate(Character ch, SkillType skill, Item bit, RegionResourceDef resDef)
    {
        // "I intended for there to be nothing here" (:1011).
        if (resDef.Reap == 0)
            return null;

        int reapAmount = Math.Clamp(
            resDef.GetRandomReapAmount(ch.GetSkill(skill), Rng), 1, ushort.MaxValue);
        ushort reapItemId = resDef.Reap;
        bool scriptChoseReapId = false;

        // Never more than the bit holds - clamped BEFORE the trigger runs (:1025), so a
        // script reads the amount really on offer.
        int pool = GetPool(bit);
        if (reapAmount > pool)
            reapAmount = pool;

        // @RegionResourceGather / @ResourceGather: Init(wAmount, 0, 0, pResBit) plus
        // LOCAL.ResourceID = the reap item (:1028-1044). Both see the same arguments
        // and share one return value, each assignment guarded by IsTrigUsed.
        if (_triggerDispatcher != null)
        {
            var locals = new SphereNet.Scripting.Variables.VarMap();
            locals.SetInt("ResourceID", reapItemId);
            var args = new TriggerArgs
            {
                CharSrc = ch,
                N1 = reapAmount,
                O1 = bit,
                Locals = locals,
            };
            var gatherRet = TriggerResult.Default;
            if (_triggerDispatcher.IsTriggerNameUsed("RegionResourceGather"))
                gatherRet = _triggerDispatcher.FireCharTrigger(
                    ch, CharTrigger.RegionResourceGather, args);
            if (_triggerDispatcher.IsTriggerNameUsed("ResourceGather"))
                gatherRet = _triggerDispatcher.FireResourceTrigger(resDef, "ResourceGather", ch, args);
            if (gatherRet == TriggerResult.True)
                return null;

            reapAmount = (int)Math.Clamp(
                SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N1), 0, ushort.MaxValue);
            // A local left at zero or holding something that is not an item id keeps
            // the definition's own reap; the reference would build id 0.
            long scriptItemId = locals.GetInt("ResourceID");
            if (scriptItemId > 0 && scriptItemId <= ushort.MaxValue &&
                (ushort)scriptItemId != reapItemId)
            {
                reapItemId = (ushort)scriptItemId;
                scriptChoseReapId = true;
            }
        }

        // ConsumeAmount takes what the bit can give (the script may have emptied it
        // through ARGO.AMOUNT) and answers with what was really taken; zero yields no
        // item (:1046-1048). The bit keeps its decay: consuming never re-arms it.
        reapAmount = ConsumeNaturalResource(bit, reapAmount);
        if (reapAmount <= 0)
            return null;

        var item = _world.CreateItem();

        // CItem::CreateScript from the REAP resource id - a DEFINITION, so its own
        // @Create runs and a coloured ore def sharing iron's art stays itself (:1050).
        int reapDefIndex = resDef.ReapDefIndex;
        if (scriptChoseReapId || reapDefIndex == 0)
            reapDefIndex = reapItemId;

        if (!ItemDefHelper.ApplyInstanceMetadata(item, reapDefIndex))
        {
            // No definition behind the id: a bare graphic still becomes an item.
            item.BaseId = reapItemId;
            item.FireCreateTrigger();
        }
        if (item.IsDeleted)
            return null;

        item.Amount = (ushort)reapAmount;
        return item;
    }

    /// <summary>The REGIONRESOURCE a bit was made from: MORE1 (m_itResource.m_ridRes),
    /// else the tag older builds wrote.</summary>
    private static RegionResourceDef? ResolveBitResource(Item bit)
    {
        uint more1 = bit.More1;
        if ((more1 >> 24) == (uint)ResType.RegionResource)
        {
            var byMore = DefinitionLoader.GetRegionResourceDef((int)(more1 & 0x00FFFFFF));
            if (byMore != null)
                return byMore;
        }
        if (bit.TryGetTag(TagResourceId, out string? ridStr) && int.TryParse(ridStr, out int ridIdx))
            return DefinitionLoader.GetRegionResourceDef(ridIdx);
        return null;
    }

    /// <summary>Announce a vein the moment it is found under a tile.
    ///
    /// The reference creates the resource bit, gives it its amount, and only then tells
    /// the scripts: @RegionResourceFound on the CHARACTER and @ResourceFound on the
    /// REGIONRESOURCE definition, both with the bit as the object argument
    /// (CWorldMap.cpp:151-166). They share one return value, the later overwriting the
    /// earlier and each assignment guarded by IsTrigUsed, and RETURN 1 empties the vein
    /// rather than removing it - the spot is found and holds nothing, which is how a
    /// script says "not here" without disturbing the node's own lifetime.</summary>
    private void FireResourceFound(Character ch, RegionResourceDef resDef, Item marker)
    {
        if (_triggerDispatcher == null)
            return;

        bool charUsed = _triggerDispatcher.IsTriggerNameUsed("RegionResourceFound");
        bool defUsed = _triggerDispatcher.IsTriggerNameUsed("ResourceFound");
        if (!charUsed && !defUsed)
            return;

        var args = new TriggerArgs { CharSrc = ch, O1 = marker };
        var ret = TriggerResult.Default;
        if (charUsed)
            ret = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RegionResourceFound, args);
        if (defUsed)
            ret = _triggerDispatcher.FireResourceTrigger(resDef, "ResourceFound", ch, args);

        if (ret == TriggerResult.True && !marker.IsDeleted)
            SetPool(marker, 0);
    }

    /// <summary>Source-X RACIALF_HUMAN_WORKHORSE bit-size bonus (CWorldMap.cpp:
    /// 138-144): +1 ore in Felucca and +2 logs in Trammel.</summary>
    internal static int ApplyWorkhorsePoolBonus(Character character, SkillType skill,
        byte map, int amount) =>
        ApplyWorkhorsePoolBonus(character, ResourceTypeFor(skill), map, amount);

    private static int ApplyWorkhorsePoolBonus(Character character, ItemType type,
        byte map, int amount)
    {
        if ((((RacialFlags)Character.RacialFlags) & RacialFlags.HumanWorkhorse) == 0 ||
            !character.IsHuman)
            return amount;
        int bonus = type == ItemType.Rock && map == 0 ? 1
            : type == ItemType.Tree && map == 1 ? 2
            : 0;
        return (int)Math.Min(ushort.MaxValue, (long)amount + bonus);
    }

    /// <summary>
    /// Legacy gather path. Returns true if a region resource was found and processed.
    /// Kept for backward compatibility with non-sink callers.
    /// </summary>
    public bool TryGather(Character ch, SkillType skill, Point3D target, out bool success, out ushort itemId, out int amount)
    {
        var result = TryGatherForSink(ch, skill, target);
        success = result.Success;
        itemId = 0;
        amount = 0;

        if (!result.Handled)
            return false;

        if (result.Item != null)
        {
            itemId = result.Item.BaseId;
            amount = result.Item.Amount;

            var actual = ch.Backpack?.TryAddItemWithStack(result.Item);
            if (actual == null)
                _world.PlaceItemWithDecay(result.Item, ch.Position);
            else if (actual != result.Item)
                _world.RemoveItem(result.Item);
        }

        return true;
    }

    /// <summary>The REGIONTYPE of the area at <paramref name="tile"/> whose page is
    /// <paramref name="typeFilter"/> (CWorldMap::CheckNaturalResource ->
    /// CRegionWorld::FindNaturalResource, CWorldMap.cpp:83-111, CRegion.cpp:
    /// 1077-1091); an area with no EVENTS/RESOURCES hands over to the map's
    /// background region at 0,0.</summary>
    private RegionTypeDef? FindNaturalResourceType(Point3D tile, string typeFilter)
    {
        var region = _world.FindRegion(tile);
        if (region != null && region.Events.Count == 0 && region.RegionTypes.Count == 0)
            region = _world.FindRegion(new Point3D(0, 0, 0, tile.Map));
        if (region == null)
            return null;
        foreach (var rtRid in region.RegionTypes)
        {
            var rtDef = DefinitionLoader.GetRegionTypeDef(rtRid.Index);
            if (rtDef?.ItemTypeFilter != null &&
                rtDef.ItemTypeFilter.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                return rtDef;
        }
        return null;
    }

    /// <summary>Source-X CWorldMap::CheckNaturalResource(pt, iType, fTest=false,
    /// pCharSrc) for a resource no gathering skill owns - the grass a grazing
    /// creature eats (IT_GRASS). The caller has already made sure the tile is of
    /// that type (the fTest half).</summary>
    public Item? CheckNaturalResource(Character? src, Point3D tile, string typeFilter, ItemType itemType)
    {
        lock (_world)
            return CheckNaturalResourceCore(src, tile, itemType, fTest: false, typeFilter);
    }

    /// <summary>Source-X CWorldMap::CheckNaturalResource (CWorldMap.cpp:26-172).
    ///   1. fTest: the tile has to be of the type - water on top of the tile
    ///      (IsTypeNear_Top), rock and tree anywhere on it (IsItemTypeNear).
    ///   2. A world-gem bit of the type already on the tile is handed back as it
    ///      stands - no top-up, no new timer.
    ///   3. Otherwise any other kind of item on the tile refuses (:78-85), the area's
    ///      REGIONTYPE for the type is found (background region when the area has no
    ///      EVENTS), a member is drawn for this character - mr_nothing when none can
    ///      be - and an invisible, immovable bit of the type is made holding AMOUNT,
    ///      decaying after REGEN tenths, then @RegionResourceFound/@ResourceFound.</summary>
    private Item? CheckNaturalResourceCore(Character? src, Point3D pt, ItemType type, bool fTest,
        string? typeFilter = null)
    {
        typeFilter ??= TypeFilterFor(type);
        if (typeFilter == null || pt.X < 0 || pt.Y < 0)
            return null;

        if (fTest && !TileIsType(pt, type))
            return null;

        var bit = FindMarker(pt, type);
        if (bit != null)
            return bit;

        var regionType = FindNaturalResourceType(pt, typeFilter);
        if (regionType == null)
            return null;

        foreach (var item in _world.GetItemsInRange(pt, 0))
        {
            if (item.X == pt.X && item.Y == pt.Y && !item.IsDeleted && item.IsOnGround &&
                item.ItemType != type)
                return null;
        }

        var resDef = SelectResource(src, regionType);
        if (resDef == null)
            return null;

        // Total amount here: a (word) of the AMOUNT curve - zero when the resource
        // defines none, as mr_nothing does, which leaves an empty bit (:132-146).
        int amount = Math.Clamp(resDef.GetRandomAmount(Rng), 0, ushort.MaxValue);
        if (src != null)
            amount = ApplyWorkhorsePoolBonus(src, type, pt.Map, amount);
        bit = CreateMarker(pt, type, amount, resDef);
        if (src != null)
            FireResourceFound(src, resDef, bit);
        return bit.IsDeleted ? null : bit;
    }

    /// <summary>The fTest half of CheckNaturalResource (CWorldMap.cpp:40-53). With no
    /// map loaded (bare test worlds) only the region's resources can answer.</summary>
    private bool TileIsType(Point3D pt, ItemType type)
    {
        if (_world.MapData == null)
            return true;
        return type is ItemType.Rock or ItemType.Tree
            ? NaturalResourceTiles.IsItemTypeAt(_world, pt, type)
            : NaturalResourceTiles.IsTypeNearTop(_world, pt, type);
    }

    /// <summary>Source-X CRandGroupDef::GetRandMemberIndex(pCharSrc)
    /// (CRandGroupDef.cpp:218-289): with a character, only the members whose reap
    /// item that character could make (Skill_MakeItem at SKTRIG_SELECT - the reap
    /// ITEMDEF's SKILLMAKE, :255) and whose @ResourceTest does not RETURN 1 (:258)
    /// take part in the weighted draw; with no one left, or a draw that falls off
    /// the end, the group answers nothing and mr_nothing stands in
    /// (CWorldMap.cpp:115-120).</summary>
    private RegionResourceDef? SelectResource(Character? src, RegionTypeDef regionType)
    {
        var members = new List<(RegionResourceDef? Def, int Weight)>(regionType.Resources.Count);
        int totalWeight = 0;
        foreach (var (rid, weight) in regionType.Resources)
        {
            var def = DefinitionLoader.GetRegionResourceDef(rid.Index);
            if (src != null && def != null && def.Reap != 0)
            {
                if (!CanMakeReap(src, def))
                    continue;
                if (_triggerDispatcher != null && _triggerDispatcher.IsTriggerNameUsed("ResourceTest") &&
                    _triggerDispatcher.FireResourceTrigger(def, "ResourceTest", src,
                        new TriggerArgs { CharSrc = src }) == TriggerResult.True)
                    continue;
            }
            members.Add((def, weight));
            totalWeight += weight;
        }

        RegionResourceDef? chosen = null;
        bool found = false;
        if (members.Count > 0)
        {
            int roll = (totalWeight > 0 ? Rng.Next(totalWeight) : 0) + 1;
            foreach (var (def, weight) in members)
            {
                roll -= weight;
                if (roll <= 0)
                {
                    chosen = def;
                    found = true;
                    break;
                }
            }
        }
        if (found)
            return chosen;

        var nothing = DefinitionLoader.StaticResources?.ResolveDefName("mr_nothing") ?? ResourceId.Invalid;
        return nothing.Type == ResType.RegionResource
            ? DefinitionLoader.GetRegionResourceDef(nothing.Index)
            : null;
    }

    /// <summary>Skill_MakeItem(reap, SKTRIG_SELECT)'s requirement half: the reap
    /// ITEMDEF's SKILLMAKE all met (SkillResourceTest, CCharSkill.cpp:911).</summary>
    private static bool CanMakeReap(Character ch, RegionResourceDef def)
    {
        var idef = DefinitionLoader.GetItemDef(def.ReapDefIndex != 0 ? def.ReapDefIndex : def.Reap);
        if (idef == null || string.IsNullOrWhiteSpace(idef.SkillMakeRaw))
            return true;
        foreach (var entry in ResourceQtyList.Parse(idef.SkillMakeRaw))
        {
            if (!ch.MatchesResourceEntry(entry.Name, entry.Quantity))
                return false;
        }
        return true;
    }

    /// <summary>What is left in a natural-resource bit.</summary>
    public static int NaturalResourceAmount(Item bit) => GetPool(bit);

    /// <summary>CItem::ConsumeAmount on a resource bit: take up to
    /// <paramref name="qty"/> and say how much was taken. An invisible top-level bit
    /// stays at zero until it decays (CItem.cpp:4357-4359).</summary>
    public static int ConsumeNaturalResource(Item bit, int qty)
    {
        int pool = GetPool(bit);
        int taken = Math.Clamp(qty, 0, pool);
        SetPool(bit, pool - taken);
        return taken;
    }

    /// <summary>The bit of this type on the tile: a world gem whose TYPE is the
    /// resource type (CWorldMap.cpp:60-67). Bits saved by older builds carry no type,
    /// only the RES_SKILL tag; they are recognised by it and given their type.</summary>
    private Item? FindMarker(Point3D tile, ItemType type)
    {
        string legacyTag = LegacyTagFor(type);
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.BaseId != MarkerBaseId || item.IsDeleted) continue;
            if (item.X != tile.X || item.Y != tile.Y) continue;
            if (item.ItemType == type)
                return item;
            if (IsResourceBit(item) &&
                item.TryGetTag(TagSkillType, out string? st) &&
                string.Equals(st, legacyTag, StringComparison.OrdinalIgnoreCase))
            {
                item.ItemType = type;
                return item;
            }
        }
        return null;
    }

    private Item CreateMarker(Point3D tile, ItemType type, int amount, RegionResourceDef resDef)
    {
        // CItem::CreateScript(ITEMID_WorldGem, pCharSrc, iType) - the type overridden
        // so the gem is not taken for a spawn - then ATTR_INVIS|ATTR_MOVE_NEVER and
        // m_ridRes = the REGIONRESOURCE (CWorldMap.cpp:124-130).
        var marker = _world.CreateItem();
        marker.BaseId = MarkerBaseId;
        marker.ItemType = type;
        marker.Name = "worldgem bit";
        SetPool(marker, amount);
        marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
        marker.More1 = ((uint)ResType.RegionResource << 24) | ((uint)resDef.Id.Index & 0x00FFFFFF);
        marker.SetTag(TagResourceMarker, "1");
        marker.SetTag(TagSkillType, LegacyTagFor(type));
        marker.SetTag(TagResourceId, resDef.Id.Index.ToString());

        _world.PlaceItem(marker, tile);

        // MoveToDecay(pt, REGEN.GetRandom() * MSECS_PER_TENTH), once (:148): the bit
        // lives out that one window and is deleted, and the next search draws a fresh
        // one. SetDecayTime(0) is "the default decay", which for a MOVE_NEVER item is
        // none at all (CItem::GetDecayTime, CItem.cpp:1435) - a resource with no REGEN
        // leaves a bit that never goes away, as upstream's does.
        long lifetimeMs = Math.Max(0, resDef.GetRandomRegen(Rng)) * 100L;
        if (lifetimeMs > 0)
            marker.SetDecayTime(lifetimeMs, overrideAlways: true);
        return marker;
    }
}
