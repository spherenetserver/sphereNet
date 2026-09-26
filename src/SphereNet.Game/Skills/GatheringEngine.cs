using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Skills;

public readonly struct GatherResult
{
    public bool Handled { get; init; }
    public bool Success { get; init; }
    public bool Depleted { get; init; }
    public Item? Item { get; init; }
}

/// <summary>
/// Region-based resource gathering engine.
/// Routes Mining/Fishing/Lumberjacking through REGIONTYPE → REGIONRESOURCE definitions.
/// Per-tile invisible marker items track depletion (Source-X parity).
/// </summary>
public sealed class GatheringEngine
{
    private readonly GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private static Random Rng => Random.Shared;

    private const string TagResourceMarker = "RESOURCE_MARKER";
    private const string TagSkillType = "RES_SKILL";
    // Remaining pool count. Stored in a TAG, NOT in Item.Amount: the Amount
    // setter floors at 1 (Math.Max(1, value)), so a depletion counter kept in
    // Amount could never reach 0 — the node stayed at 1 forever and mining was
    // effectively infinite. Tags have no such clamp.
    private const string TagPool = "RES_POOL";
    // Resource the node fixed on first strike — keeps a vein yielding the same
    // thing on every swing instead of re-rolling iron→gold each time.
    private const string TagResourceId = "RES_ID";

    internal static int GetPool(Item marker) =>
        marker.TryGetTag(TagPool, out string? p) && int.TryParse(p, out int v) ? v : 0;

    private static void SetPool(Item marker, int value) =>
        marker.SetTag(TagPool, Math.Max(0, value).ToString());

    /// <summary>Marker lifetime for a freshly found node, in milliseconds:
    /// Source-X samples the REGEN curve and converts tenths to milliseconds, once,
    /// at creation (CWorldMap.cpp:148). A resource with no REGEN samples zero and
    /// its node decays almost at once, so each search rolls a fresh one.</summary>
    private long RollNodeLifetimeMs(RegionResourceDef resDef) =>
        Math.Max(0, resDef.GetRandomRegen(Rng)) * 100L;

    /// <summary>Sphere worldgem-bit graphic. Resource markers use it so staff
    /// can see and inspect veins with AllShow (the old 0x1 "nodraw" graphic
    /// rendered nothing even for GMs); ATTR_INVIS keeps it hidden from
    /// players.</summary>
    internal const ushort MarkerGraphic = 0x1EA7;
    private const ushort MarkerBaseId = MarkerGraphic;

    /// <summary>Skill → ItemTypeFilter mapping for REGIONTYPE filtering.</summary>
    private static readonly Dictionary<SkillType, string> _skillTypeFilters = new()
    {
        [SkillType.Mining] = "t_rock",
        [SkillType.Lumberjacking] = "t_tree",
        [SkillType.Fishing] = "t_water",
    };

    public GatheringEngine(GameWorld world, TriggerDispatcher? triggerDispatcher = null)
    {
        _world = world;
        _triggerDispatcher = triggerDispatcher;
    }

    /// <summary>
    /// Sink-aware gather: creates the item but does NOT add to backpack.
    /// Caller uses sink.DeliverItem for stacking + client notification.
    /// Per-tile invisible marker items track resource depletion.
    /// </summary>
    public GatherResult TryGatherForSink(Character ch, SkillType skill, Point3D target)
    {
        lock (_world)
            return TryGatherForSinkCore(ch, skill, target);
    }

    /// <summary>What the node at this tile has to say BEFORE the swing starts.
    ///
    /// Upstream asks twice. Skill_Mining runs the resource check at every stage and
    /// refuses outright at SKTRIG_START when the tile holds no node or an empty one
    /// (CCharSkill.cpp:1448-1459) - so a spent vein is answered before a single
    /// stroke is scheduled, and nothing is animated. Here the resource was consulted
    /// only when the swing FINISHED, so working an exhausted vein played the whole
    /// two-to-six stroke animation and then said there was nothing there.
    ///
    /// This is the START half: it finds or binds the node exactly as the real gather
    /// does - upstream creates the bit at START too - but rolls no skill, fires no
    /// @ResourceGather and takes nothing out of the pool.</summary>
    public GatherResult ProbeResource(Character ch, SkillType skill, Point3D target)
    {
        lock (_world)
            return TryGatherForSinkCore(ch, skill, target, probeOnly: true);
    }

    private GatherResult TryGatherForSinkCore(Character ch, SkillType skill, Point3D target,
        bool probeOnly = false)
    {
        if (!_skillTypeFilters.TryGetValue(skill, out var typeFilter))
            return new GatherResult { Handled = false };

        // Source-X CWorldMap::CheckNaturalResource (CWorldMap.cpp:83-111): the AREA
        // at the tile answers, and only a region with no EVENTS/RESOURCES at all
        // hands over to the map's background region (the one at 0,0). The region's
        // links are then searched for the REGIONTYPE whose page is exactly this
        // resource type (CRegionWorld::FindNaturalResource, CRegion.cpp:1077-1091) -
        // no untyped REGIONTYPE stands in, and no table is borrowed from some other
        // region of the world.
        RegionTypeDef? matchedType = FindNaturalResourceType(target, typeFilter);

        if (matchedType == null || matchedType.Resources.Count == 0)
            return new GatherResult { Handled = false };

        string skillTag = skill.ToString();
        var marker = FindMarker(target, skillTag);

        // An established vein keeps its resource: reuse the marker's stored id so
        // the node yields the same thing every swing instead of re-rolling.
        RegionResourceDef? resDef = null;
        if (marker != null && marker.TryGetTag(TagResourceId, out string? ridStr)
            && int.TryParse(ridStr, out int ridIdx))
            resDef = DefinitionLoader.GetRegionResourceDef(ridIdx);
        if (resDef == null)
        {
            var resRid = matchedType.SelectRandomResource(Rng);
            resDef = DefinitionLoader.GetRegionResourceDef(resRid.Index);
        }
        if (resDef == null)
            return new GatherResult { Handled = false };

        // Bind the resource on the first strike, not the first success. A low
        // skill character cannot reroll a difficult vein until an easy resource
        // is selected simply by retrying the same tile.
        //
        // This binds a BARREN draw too. Source-X creates the resource bit even
        // when the group answered mr_nothing and arms its decay from THAT
        // definition's REGEN (CWorldMap.cpp:119/131/148); only afterwards does
        // Skill_NaturalResource_Create refuse, because the def reaps nothing
        // (CCharSkill.cpp:1012). The live pack counts on it: its mr_nothing says
        // REGEN=60*60*10 with the comment "Nothing can be found at this location
        // for this many seconds", and weights the draw at 60% for water. Throwing
        // the barren draw away instead — as this did — let a fisherman stand on
        // one tile and re-roll that 60% on every cast until it paid out, which is
        // the opposite of what the pack asked for.
        if (marker == null)
        {
            int poolAmount = Math.Clamp(resDef.GetRandomAmount(Rng), 1, ushort.MaxValue);
            poolAmount = ApplyWorkhorsePoolBonus(ch, skill, target.Map, poolAmount);
            marker = CreateMarker(target, skillTag, poolAmount, resDef);
            FireResourceFound(ch, resDef, marker);
        }

        // ...and now the barren spot answers, for as long as that node lives.
        if (resDef.Reap == 0)
            return new GatherResult { Handled = true, Success = false };

        // A node that already exists is handed back exactly as it stands: Source-X
        // returns the resource bit it found without topping it up or re-arming its
        // timer (CWorldMap.cpp:71), and a node whose amount has reached zero counts
        // as spent (CCharSkill.cpp:1456). It is the decay timeout set at creation
        // that ends the node's life, after which the next search rolls a new one.
        if (marker != null && GetPool(marker) <= 0)
            return new GatherResult { Handled = true, Depleted = true };

        // The probe stops here: the node exists and has something in it, which is all
        // the caller needed to know before committing to a swing.
        if (probeOnly)
            return new GatherResult { Handled = true, Success = true };

        // Source-X Skill_NaturalResource_Setup uses m_vcSkill.GetRandom()/10:
        // each attempt samples this resource's full SKILL curve. There is no
        // separate hard SkillMin gate; the regular S-curve decides success.
        int difficulty = resDef.GetRandomSkillDifficulty(Rng);

        // @ResourceTest — Source-X lets the script block gathering (RETURN 1).
        if (_triggerDispatcher != null)
        {
            var args = new TriggerArgs
            {
                CharSrc = ch,
                N1 = resDef.SkillMin,
                N2 = resDef.SkillMax,
            };
            if (_triggerDispatcher.FireResourceTrigger(resDef, "ResourceTest", ch, args) == TriggerResult.True)
                return new GatherResult { Handled = true, Success = false };
        }

        bool success = SkillEngine.UseQuick(ch, skill, difficulty);

        if (success)
        {
            int reapAmount = Math.Clamp(
                resDef.GetRandomReapAmount(ch.GetSkill(skill), Rng), 1, ushort.MaxValue);
            ushort reapItemId = resDef.Reap;
            bool scriptChoseReapId = false;

            // Never hand out more than the pool actually holds — otherwise a
            // near-empty node still yields a full reap. The reference clamps here
            // too, BEFORE the trigger runs (CCharSkill.cpp:1025), so a script reads
            // the amount really on offer rather than the unclamped roll.
            Item activeMarker = marker!;
            int pool = GetPool(activeMarker);
            if (reapAmount > pool)
                reapAmount = pool;

            // @ResourceGather — RETURN 1 cancels the reap.
            //
            // The argument contract is Source-X's: Init(wAmount, 0, 0, pResBit) plus
            // LOCAL.ResourceID = the reap item (CCharSkill.cpp:1029). ARGN1 is the
            // AMOUNT, the object argument is the resource marker, and the item id
            // travels in the local — the id is read back from there afterwards
            // (:1044). SphereNet passed the ITEM ID as ARGN1 and the amount as ARGN2,
            // so a script halving a yield with ARGN1=2 produced four copies of item
            // id 2, and ARGN1=0 — which the reference reads as "take nothing" — was
            // discarded as a zero and the full reap handed over anyway.
            if (_triggerDispatcher != null)
            {
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("ResourceID", reapItemId);
                var args = new TriggerArgs
                {
                    CharSrc = ch,
                    N1 = reapAmount,
                    O1 = activeMarker,
                    Locals = locals,
                };
                // Both triggers see the SAME arguments and share ONE return value,
                // the later one overwriting the earlier - but only when a script
                // actually hooks it, because the reference guards each assignment with
                // IsTrigUsed (CCharSkill.cpp:1034-1038). A char-level @RegionResource-
                // Gather therefore cancels the reap unless a @ResourceGather block
                // exists to have the last word.
                var gatherRet = TriggerResult.Default;
                if (_triggerDispatcher.IsTriggerNameUsed("RegionResourceGather"))
                    gatherRet = _triggerDispatcher.FireCharTrigger(
                        ch, CharTrigger.RegionResourceGather, args);
                if (_triggerDispatcher.IsTriggerNameUsed("ResourceGather"))
                    gatherRet = _triggerDispatcher.FireResourceTrigger(resDef, "ResourceGather", ch, args);
                if (gatherRet == TriggerResult.True)
                    return new GatherResult { Handled = true, Success = false };

                reapAmount = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N1);
                // A local left at zero or holding something that is not an item id
                // keeps the definition's own reap; the reference would build id 0.
                long scriptItemId = locals.GetInt("ResourceID");
                if (scriptItemId > 0 && scriptItemId <= ushort.MaxValue &&
                    (ushort)scriptItemId != reapItemId)
                {
                    // A script naming its own id names a GRAPHIC, so it replaces the
                    // definition the resource would otherwise have built from.
                    reapItemId = (ushort)scriptItemId;
                    scriptChoseReapId = true;
                }
            }

            // ConsumeAmount takes what the pool can give and answers with what was
            // really taken; zero or less yields no item at all (CCharSkill.cpp:1046).
            if (reapAmount > pool)
                reapAmount = pool;
            if (reapAmount <= 0)
                return new GatherResult { Handled = true, Success = false };

            // Consuming from the pool does not touch the timer: the reference
            // decrements the amount at CCharSkill.cpp:1046 and leaves the decay set
            // at creation alone, so working a vein cannot keep it alive.
            int remaining = pool - reapAmount;
            SetPool(activeMarker, remaining);

            var item = _world.CreateItem();

            // Source-X builds the reaped item through CItem::CreateScript
            // (CCharSkill.cpp:1050) from the REAP resource id - a DEFINITION, not a
            // graphic - so GenerateScript runs that definition's own @Create
            // (CItem.cpp:404/415) and the item comes out with its name, its TDATA and
            // its scripted colour.
            //
            // Resolving the reap to a graphic instead collapsed a pack's ore table
            // into one item: every colour but iron is written as a named def sharing
            // iron's art (`[ITEMDEF i_ore_copper] ID=i_ore_iron` + an @Create that
            // colours it), so the graphic is iron's for all fifteen of them. Mining
            // produced iron ore whatever the vein was.
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
                return new GatherResult { Handled = true, Success = false };

            item.Amount = (ushort)reapAmount;
            return new GatherResult { Handled = true, Success = true, Item = item };
        }

        return new GatherResult { Handled = true, Success = false };
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

        if (ret == TriggerResult.True)
            SetPool(marker, 0);
    }

    /// <summary>Source-X RACIALF_HUMAN_WORKHORSE node-size bonus: +1 ore in
    /// Felucca and +2 logs in Trammel.</summary>
    internal static int ApplyWorkhorsePoolBonus(Character character, SkillType skill,
        byte map, int amount)
    {
        if ((((RacialFlags)Character.RacialFlags) & RacialFlags.HumanWorkhorse) == 0 ||
            !character.IsHuman)
            return amount;
        int bonus = skill == SkillType.Mining && map == 0 ? 1
            : skill == SkillType.Lumberjacking && map == 1 ? 2
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
    /// pCharSrc) (CWorldMap.cpp:26-172) for a resource no gathering skill owns -
    /// the grass a grazing creature eats (IT_GRASS). The resource bit already at
    /// the tile is handed back as it stands; otherwise one is made from the area's
    /// REGIONTYPE for <paramref name="typeFilter"/> (r_default_grass t_grass ->
    /// mr_grass in the packs): a hidden world-gem bit holding AMOUNT, decaying
    /// after REGEN, announced through @RegionResourceFound / @ResourceFound. Null
    /// when the area has no such resource, or when some other kind of item lies on
    /// the tile (:88-97). The caller has already made sure the tile is of that
    /// type (the fTest half).</summary>
    public Item? CheckNaturalResource(Character? src, Point3D tile, string typeFilter, ItemType itemType)
    {
        lock (_world)
        {
            string tag = typeFilter.ToUpperInvariant();
            var bit = FindMarker(tile, tag);
            if (bit != null)
                return bit;

            var regionType = FindNaturalResourceType(tile, typeFilter);
            if (regionType == null || regionType.Resources.Count == 0)
                return null;
            foreach (var item in _world.GetItemsInRange(tile, 0))
            {
                if (item.X == tile.X && item.Y == tile.Y && !item.IsDeleted && item.ItemType != itemType)
                    return null;
            }

            var resDef = DefinitionLoader.GetRegionResourceDef(regionType.SelectRandomResource(Rng).Index);
            if (resDef == null)
                return null;
            int amount = Math.Clamp(resDef.GetRandomAmount(Rng), 1, ushort.MaxValue);
            bit = CreateMarker(tile, tag, amount, resDef);
            bit.ItemType = itemType;
            if (src != null)
                FireResourceFound(src, resDef, bit);
            return bit;
        }
    }

    /// <summary>What is left in a natural-resource bit.</summary>
    public static int NaturalResourceAmount(Item bit) => GetPool(bit);

    /// <summary>CItem::ConsumeAmount on a resource bit: take up to
    /// <paramref name="qty"/> and say how much was taken.</summary>
    public static int ConsumeNaturalResource(Item bit, int qty)
    {
        int pool = GetPool(bit);
        int taken = Math.Clamp(qty, 0, pool);
        SetPool(bit, pool - taken);
        return taken;
    }

    private Item? FindMarker(Point3D tile, string skillTag)
    {
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.BaseId != MarkerBaseId) continue;
            if (!item.TryGetTag(TagResourceMarker, out string? mk) || mk != "1") continue;
            if (!item.TryGetTag(TagSkillType, out string? st) || st != skillTag) continue;
            if (item.X == tile.X && item.Y == tile.Y) return item;
        }
        return null;
    }

    private Item CreateMarker(Point3D tile, string skillTag, int amount, RegionResourceDef resDef)
    {
        var marker = _world.CreateItem();
        marker.BaseId = MarkerBaseId;
        marker.Name = "worldgem bit";
        SetPool(marker, amount); // remaining pool — tag, not Amount (see TagPool)
        marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
        marker.SetTag(TagResourceMarker, "1");
        marker.SetTag(TagSkillType, skillTag);
        marker.SetTag(TagResourceId, resDef.Id.Index.ToString());

        // Source-X MoveToDecay()s the bit ONCE here, for a sampled regen period
        // (CWorldMap.cpp:148). Nothing re-arms it afterwards: the node lives out
        // that one window and is then deleted, and the next search rolls a fresh
        // node with a fresh pool. No marker is ever immortal (TIMER=-1), which used
        // to leave one invisible worldgem per fished tile in the world forever.
        marker.SetDecayAt(Environment.TickCount64 + RollNodeLifetimeMs(resDef));

        _world.PlaceItem(marker, tile);
        return marker;
    }
}
