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
    // Original (full) pool size and the timestamp of the last pool change —
    // together these drive partial regen (Source-X: a vein slowly refills over
    // time instead of only resetting once fully depleted).
    private const string TagPoolMax = "RES_MAX";
    private const string TagLast = "RES_LAST";

    internal static int GetPool(Item marker) =>
        marker.TryGetTag(TagPool, out string? p) && int.TryParse(p, out int v) ? v : 0;

    private static void SetPool(Item marker, int value) =>
        marker.SetTag(TagPool, Math.Max(0, value).ToString());

    private static int GetPoolMax(Item marker) =>
        marker.TryGetTag(TagPoolMax, out string? p) && int.TryParse(p, out int v) ? v : GetPool(marker);

    private static long GetLast(Item marker) =>
        marker.TryGetTag(TagLast, out string? p) && long.TryParse(p, out long v) ? v : 0;

    private static void SetLast(Item marker, long ms) =>
        marker.SetTag(TagLast, ms.ToString());

    /// <summary>Partially regenerate a vein's pool by the time elapsed since its last
    /// change (Source-X gradual regen): one resource per (Regen / max) seconds, capped
    /// at the original pool. A recovering node clears its decay timer so it isn't
    /// deleted mid-regrow.</summary>
    internal static void RegenMarker(Item marker, RegionResourceDef resDef, long now)
    {
        int pool = GetPool(marker);
        int max = GetPoolMax(marker);
        if (pool >= max) return;

        long last = GetLast(marker);
        if (last <= 0) { SetLast(marker, now); return; }

        long fullRegenMs = resDef.Regen > 0 ? resDef.Regen * 1000L : 36_000_000L;
        long perUnitMs = Math.Max(1, fullRegenMs / Math.Max(1, max));
        long ticks = (now - last) / perUnitMs;
        if (ticks <= 0) return;

        int newPool = (int)Math.Min(max, pool + ticks);
        SetPool(marker, newPool);
        SetLast(marker, last + ticks * perUnitMs);
        if (newPool > 0)
            marker.DecayTime = 0; // recovering — don't decay-delete the vein
    }

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

    private GatherResult TryGatherForSinkCore(Character ch, SkillType skill, Point3D target)
    {
        if (!_skillTypeFilters.TryGetValue(skill, out var typeFilter))
            return new GatherResult { Handled = false };

        RegionTypeDef? matchedType = null;

        var region = _world.FindRegion(target);
        if (region != null && region.RegionTypes.Count > 0)
        {
            foreach (var rtRid in region.RegionTypes)
            {
                var rtDef = DefinitionLoader.GetRegionTypeDef(rtRid.Index);
                if (rtDef == null) continue;

                if (rtDef.ItemTypeFilter != null &&
                    rtDef.ItemTypeFilter.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    matchedType = rtDef;
                    break;
                }

                if (rtDef.ItemTypeFilter == null && matchedType == null)
                    matchedType = rtDef;
            }
        }

        matchedType ??= DefinitionLoader.FindRegionTypeByFilter(typeFilter);

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

        // mr_nothing: weighted "found nothing" result (never persisted on a node)
        if (resDef.Reap == 0)
            return new GatherResult { Handled = true, Success = false };

        // Bind the resource on the first strike, not the first success. A low
        // skill character cannot reroll a difficult vein until an easy resource
        // is selected simply by retrying the same tile.
        if (marker == null)
        {
            int amtMin = Math.Clamp(resDef.AmountMin, 1, ushort.MaxValue);
            int amtMaxInclusive = Math.Clamp(resDef.AmountMax, amtMin, ushort.MaxValue);
            int amtMax = Math.Min(ushort.MaxValue + 1, amtMaxInclusive + 1);
            marker = CreateMarker(target, skillTag, Rng.Next(amtMin, amtMax), resDef);
        }

        // Top the vein up by the time elapsed since the last gather (partial regen)
        // before deciding whether it is depleted.
        if (marker != null)
            RegenMarker(marker, resDef, Environment.TickCount64);

        if (marker != null && GetPool(marker) <= 0)
            return new GatherResult { Handled = true, Depleted = true };

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
            int reapMin = Math.Clamp(resDef.ReapAmountMin, 1, ushort.MaxValue);
            int reapMax = Math.Clamp(resDef.ReapAmountMax, reapMin, ushort.MaxValue);
            int reapAmount = Rng.Next(reapMin, reapMax + 1);
            ushort reapItemId = resDef.Reap;

            // @ResourceGather — RETURN 1 cancels the reap; the script may also
            // override the reaped item (ARGN1) and amount (ARGN2). Source-X CCharSkill.
            if (_triggerDispatcher != null)
            {
                var args = new TriggerArgs
                {
                    CharSrc = ch,
                    N1 = resDef.Reap,
                    N2 = reapAmount,
                };
                if (_triggerDispatcher.FireResourceTrigger(resDef, "ResourceGather", ch, args) == TriggerResult.True)
                    return new GatherResult { Handled = true, Success = false };
                if (args.N1 > 0 && args.N1 <= ushort.MaxValue) reapItemId = (ushort)args.N1;
                if (args.N2 > 0) reapAmount = Math.Clamp(args.N2, 1, ushort.MaxValue);
            }

            // Never hand out more than the pool actually holds — otherwise a
            // near-empty node still yields a full reap.
            Item activeMarker = marker!;
            int pool = GetPool(activeMarker);
            if (reapAmount > pool)
                reapAmount = pool;
            if (reapAmount <= 0)
                return new GatherResult { Handled = true, Success = false };

            int remaining = pool - reapAmount;
            SetPool(activeMarker, remaining);
            SetLast(activeMarker, Environment.TickCount64); // reset the regen clock on each gather
            if (remaining <= 0)
            {
                long regenMs = resDef.Regen > 0 ? resDef.Regen * 1000L : 36_000_000L;
                activeMarker.DecayTime = Environment.TickCount64 + regenMs;
            }

            var item = _world.CreateItem();
            item.BaseId = reapItemId;
            // Carry the itemdef display name so single-click labels and the
            // vendor/sell lists name the resource ("iron ore") instead of an
            // empty string. GetName() pluralizes per Amount on read.
            var reapDef = DefinitionLoader.GetItemDef(reapItemId);
            if (reapDef != null && !string.IsNullOrWhiteSpace(reapDef.Name))
                item.Name = reapDef.Name;
            item.Amount = (ushort)reapAmount;
            return new GatherResult { Handled = true, Success = true, Item = item };
        }

        return new GatherResult { Handled = true, Success = false };
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
        marker.SetTag(TagPoolMax, amount.ToString()); // full size, for partial regen
        SetLast(marker, Environment.TickCount64);
        marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
        marker.SetTag(TagResourceMarker, "1");
        marker.SetTag(TagSkillType, skillTag);
        marker.SetTag(TagResourceId, resDef.Id.Index.ToString());

        // No decay timer at creation: the node persists until it is fully
        // depleted, and only THEN starts its regen timer (set where the pool hits
        // zero). Starting decay at creation reset a partly-mined vein early.
        marker.DecayTime = 0;

        _world.PlaceItem(marker, tile);
        return marker;
    }
}
