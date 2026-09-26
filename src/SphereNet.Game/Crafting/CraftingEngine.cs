using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Crafting;

/// <summary>
/// A resource requirement for crafting (e.g., 10 ingots, 5 cloth).
/// </summary>
public readonly struct CraftResource
{
    public ushort ItemId { get; init; }
    public int Amount { get; init; }
    /// <summary>When set, the resource matches by item TYPE (Source-X RES_TYPEDEF —
    /// e.g. "any t_ingot") rather than a specific item id. Lets a recipe consume any
    /// item of a category instead of a single hardcoded BaseId.</summary>
    public ItemType? Type { get; init; }
}

/// <summary>A primary-resource variant available to the crafting UI.</summary>
public readonly record struct CraftMaterialOption(
    ushort Hue, int Available, ushort DisplayId, string Name);

/// <summary>
/// A craftable item recipe. Loaded from [ITEMDEF] RESOURCES/SKILLMAKE sections.
/// </summary>
public sealed class CraftRecipe
{
    /// <summary>Resource index of the source ITEMDEF. This can differ from the
    /// display id for named/aliased definitions.</summary>
    public int ResultDefId { get; init; }
    public ushort ResultItemId { get; init; }
    public string ResultName { get; set; } = "";
    public SkillType PrimarySkill { get; init; } = SkillType.Blacksmithing;
    public int Difficulty { get; init; }
    public List<CraftResource> Resources { get; } = [];
    public List<(SkillType Skill, int MinValue)> SkillRequirements { get; } = [];
    /// <summary>Tool types from SKILLMAKE t_* entries — the crafter must
    /// carry (or wield) an item of this type; it is not consumed.</summary>
    public List<ItemType> RequiredToolTypes { get; } = [];
    /// <summary>Specific items from SKILLMAKE i_* entries — must be present,
    /// not consumed. The count is the entry's own quantity: upstream matches with
    /// IsResourceMatch(rid, qty), which is a ContentConsumeTest for that many
    /// (CCharStatus.cpp:74).</summary>
    public List<(ushort ItemId, int Amount)> RequiredItemIds { get; } = [];
}

/// <summary>
/// Crafting engine. Maps to CChar::Skill_MakeItem in Source-X CCharSkill.cpp.
/// Handles resource checking, consumption, success/fail, and item creation.
/// </summary>
public sealed class CraftingEngine
{
    private readonly GameWorld _world;
    /// <summary>Keyed by the ITEMDEF RESOURCE id, not the display graphic. Source-X
    /// carries that id through Skill_MakeItem and looks the definition up with it
    /// directly (CCharSkill.cpp:870/679); keying on the graphic made two definitions
    /// that merely share an art id the same recipe, and the second silently replaced
    /// the first - it vanished from its skill's list and its defname built the other
    /// item.</summary>
    private readonly Dictionary<int, CraftRecipe> _recipes = [];

    public CraftingEngine(GameWorld world)
    {
        _world = world;
    }

    public void RegisterRecipe(CraftRecipe recipe) =>
        _recipes[RecipeKey(recipe)] = recipe;

    /// <summary>A named ITEMDEF gets a synthetic resource index, a numeric one is its
    /// own id; either way that is the recipe's identity. ResultItemId is the fallback
    /// only for a recipe built without a definition behind it.</summary>
    private static int RecipeKey(CraftRecipe recipe) =>
        recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId;

    public CraftRecipe? GetRecipe(int defId) =>
        _recipes.GetValueOrDefault(defId);

    public IReadOnlyDictionary<int, CraftRecipe> AllRecipes => _recipes;

    /// <summary>Get all recipes for a given primary skill.</summary>
    public List<CraftRecipe> GetRecipesBySkill(SkillType skill) =>
        _recipes.Values.Where(r => r.PrimarySkill == skill).ToList();

    /// <summary>
    /// Check if a character has the resources and skills to craft an item.
    /// Maps to SkillResourceTest in Source-X.
    /// </summary>
    /// <summary>Recipe lookup by result display id (SKILLMENU MAKEITEM).</summary>
    /// <summary>Find the recipe a request names. The id is the ITEMDEF resource id
    /// first; a bare display graphic is still accepted for callers that only have
    /// one, but ONLY while it is unambiguous. When several definitions share a
    /// graphic there is no answer to give, and silently handing back whichever
    /// registered last is what this replaces.</summary>
    public CraftRecipe? TryGetRecipe(int resultId)
    {
        if (_recipes.TryGetValue(resultId, out var byDef))
            return byDef;

        CraftRecipe? single = null;
        foreach (var recipe in _recipes.Values)
        {
            if (recipe.ResultItemId != resultId)
                continue;
            if (single != null)
                return null;        // ambiguous graphic - refuse rather than guess
            single = recipe;
        }
        return single;
    }

    /// <param name="skillOnly">Answer the SKILLMAKE half only - the skills and the
    /// tools or items the recipe requires present - and ignore whether the materials
    /// are in the pack. This is upstream's fSkillOnly (CCharSkill.cpp:913), the
    /// difference between CANMAKESKILL and CANMAKE.</param>
    /// <param name="checkWorkSite">Whether standing at a forge (or a fire, for cooking)
    /// is part of the answer. Crafting requires it; the CANMAKE query does not ask -
    /// upstream checks the work site in the skill's own stage, not in Skill_MakeItem's
    /// SKTRIG_SELECT.</param>
    public bool CanCraft(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue = null,
        bool skillOnly = false, bool checkWorkSite = true)
    {
        // Check skill requirements
        foreach (var (skill, minVal) in recipe.SkillRequirements)
        {
            if (crafter.GetSkill(skill) < minVal)
                return false;
        }

        // SKILLMAKE tool/required-item presence (not consumed).
        foreach (var toolType in recipe.RequiredToolTypes)
        {
            if (!HasItemOfType(crafter, toolType))
                return false;
        }
        foreach (var (reqId, reqAmount) in recipe.RequiredItemIds)
        {
            if (CountResource(crafter, reqId) < reqAmount)
                return false;
        }

        // Everything above is the SKILLMAKE side of the recipe, which is all
        // CANMAKESKILL asks about.
        if (skillOnly)
            return true;

        // Work-site proximity (reference Skill_Blacksmith / Skill_Cooking):
        // smithing needs a forge within 2 tiles, cooking a heat source
        // within 3 (fire, forge or campfire).
        if (checkWorkSite && !HasRequiredWorkSite(crafter, recipe.PrimarySkill))
            return false;

        // Check resource availability
        for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
        {
            var res = recipe.Resources[resourceIndex];
            if (CountResource(crafter, res) < res.Amount)
                return false;
            if (resourceIndex == 0 &&
                !TrySelectResourceHue(crafter, res, res.Amount, primaryResourceHue, out _))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Attempt to craft an item. Returns the crafted item on success, null on failure.
    /// Maps to Skill_MakeItem / Skill_MakeItem_Success flow in Source-X.
    /// </summary>
    public Item? TryCraft(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue = null)
    {
        lock (crafter)
            return TryCraftCore(crafter, recipe, primaryResourceHue);
    }

    private Item? TryCraftCore(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue)
    {
        if (crafter.IsDead) return null;
        if (!CanCraft(crafter, recipe, primaryResourceHue))
            return null;

        // Skill check
        bool success = SkillEngine.UseQuick(crafter, recipe.PrimarySkill, recipe.Difficulty);

        // No tool wear: Skill_MakeItem / Skill_MakeItem_Success never damage the
        // crafting tool (CCharSkill.cpp:674-975); upstream's only tool wear is the
        // EF_DamageTools gathering path (:3947-3961). The per-attempt damage here
        // was invented.

        if (success)
        {
            // Re-verify resources before consuming (gump callback delay may have changed state)
            foreach (var res in recipe.Resources)
            {
                if (CountResource(crafter, res) < res.Amount)
                    return null;
            }

            // Capture the primary resource's hue BEFORE consuming so the crafted
            // item can inherit the material colour (e.g. coloured ingots produce
            // a coloured weapon/armour, matching UO material behaviour).
            ushort resourceHue = 0;
            if (recipe.Resources.Count > 0 &&
                !TrySelectResourceHue(crafter, recipe.Resources[0], recipe.Resources[0].Amount,
                    primaryResourceHue, out resourceHue))
                return null;

            // Consume resources
            for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
            {
                var res = recipe.Resources[resourceIndex];
                if (!ConsumeResource(crafter, res, res.Amount,
                        resourceIndex == 0 ? resourceHue : null))
                    return null;
            }

            // Create the item
            var item = _world.CreateItem();
            item.BaseId = recipe.ResultItemId;
            var resultDef = DefinitionLoader.GetItemDef(
                recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId);
            item.Name = !string.IsNullOrWhiteSpace(recipe.ResultName)
                ? recipe.ResultName
                : DefinitionLoader.ResolveNames(resultDef?.Name ?? "");
            if (resultDef != null)
            {
                ItemDefHelper.ApplyInstanceMetadata(item,
                    recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId,
                    setDisplayId: false, setName: false);
                item.ItemType = resultDef.Type;
                item.TData1 = resultDef.TData1;
                item.TData2 = resultDef.TData2;
                item.TData3 = resultDef.TData3;
                item.TData4 = resultDef.TData4;
                foreach (var (key, value) in resultDef.TagDefs.GetAll())
                    item.SetTag(key, value);
                if (resultDef.HitsMax > 0 || resultDef.HitsMin > 0)
                {
                    int minHits = Math.Max(1, resultDef.HitsMin > 0 ? resultDef.HitsMin : resultDef.HitsMax);
                    int maxHits = Math.Max(minHits, resultDef.HitsMax > 0 ? resultDef.HitsMax : minHits);
                    int hits = minHits == maxHits ? minHits : Random.Shared.Next(minHits, maxHits + 1);
                    item.HitsMax = hits;
                    item.HitsCur = hits;
                }
            }
            item.Crafter = crafter.Uid;
            if (resourceHue != 0)
                item.Hue = new Core.Types.Color(resourceHue);

            // Quality roll based on skill (Source-X Skill_MakeItem band table).
            int skillVal = crafter.GetSkill(recipe.PrimarySkill);
            int quality = CalcQuality(skillVal);
            item.Quality = (ushort)quality;

            // Source-X CCharSkill.cpp:799: only a grandmaster (skill > 99.9)
            // producing quality > 175 gets the maker's mark on the name, and only
            // while OF_NOITEMNAMING is off — a shard can switch the marks away, and
            // that bit was in the enum with nothing reading it. The old invented
            // "exceptional" rename + 20% durability boost had no reference basis
            // (durability comes solely from the def).
            if (EarnsMakersMark(skillVal, quality))
                item.Name = $"{item.Name} crafted by {crafter.Name}";

            // Caller (GameClient.OpenCraftingGump) handles placement + notification
            return item;
        }
        else
        {
            // Partial resource loss on failure (reference Skill_MakeItem
            // SKTRIG_FAIL → ResourceConsumePart, CCharSkill.cpp:925-945). The
            // percent has THREE sources in order, and only the last was implemented:
            //   1. ACTIONEFFECT, when a script set one on this attempt;
            //   2. the crafting skill's own EFFECT curve, rolled at random
            //      (the live pack gives Inscription EFFECT=50);
            //   3. a flat 0-49%% roll.
            int lossPercent = ResolveFailureLossPercent(crafter, recipe);
            for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
            {
                var res = recipe.Resources[resourceIndex];
                int lostAmount = res.Amount * lossPercent / 100;
                if (lostAmount > 0)
                    ConsumeResource(crafter, res, lostAmount,
                        resourceIndex == 0 ? primaryResourceHue : null);
            }

            return null;
        }
    }

    /// <summary>How much of a failed craft's bill is still paid, as a percent
    /// (Source-X Skill_MakeItem SKTRIG_FAIL, CCharSkill.cpp:928-943). ACTIONEFFECT
    /// wins when a script set one; otherwise the crafting skill's EFFECT curve
    /// decides, and only with neither does the flat roll apply.</summary>
    private static int ResolveFailureLossPercent(Character crafter, CraftRecipe recipe)
    {
        if (crafter.ActionEffect >= 0)
            return Math.Clamp(crafter.ActionEffect, 0, 100);

        var skillDef = Definitions.DefinitionLoader.GetSkillDef((int)recipe.PrimarySkill);
        if (skillDef is { Effect.IsEmpty: false })
            return Math.Clamp(skillDef.Effect.GetLinear(Random.Shared.Next(1001)), 0, 100);

        return Random.Shared.Next(50);
    }

    /// <summary>Crafted item quality on the 1-200 scale (100 = average) —
    /// Source-X Skill_MakeItem (CCharSkill.cpp:724-794): the skill picks a
    /// quality band (skill*2/10), a logarithmic ±0..2 band variance shifts it,
    /// then the final value rolls inside the band.</summary>
    private int CalcQuality(int skillLevel)
    {
        int variance = 2 - (int)Math.Log10(1.0 + Random.Shared.Next(250));
        if (Random.Shared.Next(2) == 0)
            variance = -variance;

        int bandSelector = skillLevel * 2 / 10;
        int band =
            bandSelector < 25 ? 0 :   // shoddy
            bandSelector < 50 ? 1 :   // poor
            bandSelector < 75 ? 2 :   // below average
            bandSelector < 125 ? 3 :  // average
            bandSelector < 150 ? 4 :  // above average
            bandSelector < 175 ? 5 :  // excellent
            6;                        // superior
        band = Math.Clamp(band + variance, 0, 6);

        return band switch
        {
            0 => Random.Shared.Next(25) + 1,
            1 => Random.Shared.Next(25) + 26,
            2 => Random.Shared.Next(25) + 51,
            3 => Random.Shared.Next(50) + 76,
            4 => Random.Shared.Next(25) + 126,
            5 => Random.Shared.Next(25) + 151,
            _ => Random.Shared.Next(25) + 176,
        };
    }

    /// <summary>Whether a finished piece carries its maker's name. The reference
    /// asks for a grandmaster (skill above 99.9), a quality above 175 AND
    /// OF_NOITEMNAMING to be off (CCharSkill.cpp:799) - the last of the three was
    /// declared here and never read, so a shard could switch the marks away and keep
    /// getting them.</summary>
    public static bool EarnsMakersMark(int skillValue, int quality) =>
        skillValue > 999 && quality > 175 &&
        (Clients.GameClient.ServerOptionFlags & OptionFlags.NoItemNaming) == 0;

    /// <summary>The message a finished piece's QUALITY earns, or null for an
    /// ordinary one.
    ///
    /// The reference names the band as it rolls it and says so to the crafter
    /// (DEFMSG_MAKESUCCESS_1..6, CCharSkill.cpp:758-788; the average band alone
    /// stays quiet, :771). Those six messages were in the table here and nothing
    /// ever sent one, so every piece came out sounding the same. Derived from the
    /// final value rather than carried out of the roll — it is the same band
    /// boundary either way.</summary>
    public static string? QualityMessageKey(int quality) => quality switch
    {
        <= 25 => Messages.Msg.Makesuccess1,       // shoddy
        <= 50 => Messages.Msg.Makesuccess2,       // poor
        <= 75 => Messages.Msg.Makesuccess3,       // below average
        <= 125 => null,                           // average: the reference is silent
        <= 150 => Messages.Msg.Makesuccess4,      // above average
        <= 175 => Messages.Msg.Makesuccess5,      // excellent
        _ => Messages.Msg.Makesuccess6,           // superior
    };

    /// <summary>Take (or just test for) a PART of what a recipe needs, the way
    /// Source-X ResourceConsumePart does (CContainer.cpp:534): each entry is scaled
    /// by a percentage, and a test reports the first entry that falls short instead
    /// of spending anything. Repair uses it for half the damage percentage.</summary>
    public static bool TryConsumeResourcePart(Character ch, CraftRecipe recipe,
        int percent, bool test)
    {
        if (percent <= 0)
            return true;

        foreach (var res in recipe.Resources)
        {
            int need = res.Amount * percent / 100;
            if (need <= 0)
                continue;
            if (test)
            {
                if (CountResource(ch, res) < need)
                    return false;
            }
            else
            {
                ConsumeResource(ch, res, need);
            }
        }
        return true;
    }

    /// <summary>
    /// How much of a resource the character's reachable stock holds — the same search
    /// a craft runs, so a SKILLTEST that says yes is followed by a craft that agrees.
    /// A resource named by TYPE counts every item of that type; one named by ITEMDEF
    /// counts that graphic.
    /// </summary>
    public static int CountStock(Character ch, SphereNet.Core.Types.ResourceId rid)
    {
        var pack = ch.Backpack;
        if (pack == null || !rid.IsValid) return 0;

        if (rid.Type == Core.Enums.ResType.TypeDef)
            return CountInContainerByType(pack, (ItemType)rid.Index);

        var def = DefinitionLoader.GetItemDef(rid.Index);
        ushort itemId = def is { DispIndex: > 0 }
            ? def.DispIndex
            : rid.Index <= ushort.MaxValue ? (ushort)rid.Index : (ushort)0;
        return itemId == 0 ? 0 : CountInContainer(pack, itemId);
    }

    /// <summary>Count a recipe resource in the character's backpack — by item TYPE
    /// (RES_TYPEDEF) or by specific item id.</summary>
    private static int CountResource(Character ch, CraftResource res)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return res.Type.HasValue
            ? CountInContainerByType(pack, res.Type.Value)
            : CountInContainer(pack, res.ItemId);
    }

    /// <summary>Count how many of a specific item ID the character has in their backpack.</summary>
    private static int CountResource(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return CountInContainer(pack, itemId);
    }

    private static int CountInContainerByType(Item container, ItemType type, int depth = 0)
    {
        if (depth > 10) return 0;
        int count = 0;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type)
                count += item.Amount;
            // Source-X gates a recursive resource search on IsSearchable
            // (CContainer::ContentFind, CContainer.cpp:236): a locked chest in
            // the pack is not part of the reachable stock a craft may consume.
            if (item.IsSearchableContainer)
                count += CountInContainerByType(item, type, depth + 1);
        }
        return count;
    }

    /// <summary>Find the first backpack item matching an item ID (used to read
    /// the resource hue before it is consumed).</summary>
    /// <summary>The crafter carries (or wields) an item of the given type.</summary>
    private static bool HasItemOfType(Character ch, ItemType type)
    {
        var oneHand = ch.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == type) return true;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == type) return true;
        return ch.Backpack != null && HasItemOfTypeIn(ch.Backpack, type, depth: 3);
    }

    /// <summary>Answered by the same search that picks the tool, so the tool that
    /// PERMITS the craft and the tool that WEARS from it can never be different
    /// items. They were: this check refused to descend into an unsearchable
    /// container while the wear lookup below happily did, so a spare tool locked
    /// away took the damage owed by the one in the crafter's hand.</summary>
    private static bool HasItemOfTypeIn(Item container, ItemType type, int depth) =>
        FindItemOfTypeIn(container, type, depth) != null;

    private static Item? FindItemOfType(Character ch, ItemType type)
    {
        var oneHand = ch.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == type) return oneHand;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == type) return twoHand;
        return ch.Backpack != null ? FindItemOfTypeIn(ch.Backpack, type, 3) : null;
    }

    private static Item? FindItemOfTypeIn(Item container, ItemType type, int depth)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return item;
            // Source-X ContentFind skips a container it may not search
            // (CContainer.cpp:236) - a locked chest in the pack is not stock the
            // crafter can draw a tool from.
            if (depth > 0 && item.ContentCount > 0 && item.IsSearchableContainer)
            {
                var found = FindItemOfTypeIn(item, type, depth - 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>Work-site proximity (reference Skill_Blacksmith /
    /// Skill_Cooking): smithing needs a forge within 2 tiles, cooking a
    /// heat source within 3. Other craft skills have no site requirement.</summary>
    private bool HasRequiredWorkSite(Character crafter, SkillType skill)
    {
        switch (skill)
        {
            case SkillType.Blacksmithing:
                return HasNearbyType(crafter, 2, ItemType.Forge);
            case SkillType.Cooking:
                return HasNearbyType(crafter, 3, ItemType.Fire, ItemType.Forge, ItemType.Campfire);
            default:
                return true;
        }
    }

    /// <summary>Where the work site is, when there is one. Upstream turns the
    /// crafter toward it on every stroke - UpdateDir(m_Act_p) "toward the forge"
    /// (Skill_Blacksmith, CCharSkill.cpp:3155) and "toward the fire source"
    /// (Skill_Cooking, :2252) - so the hammer swings at the anvil rather than at
    /// whatever the crafter happened to be looking at.</summary>
    public bool TryFindWorkSite(Character crafter, SkillType skill, out SphereNet.Core.Types.Point3D at)
    {
        at = crafter.Position;
        return skill switch
        {
            SkillType.Blacksmithing => FindNearbyType(crafter, 2, ref at, ItemType.Forge),
            SkillType.Cooking => FindNearbyType(crafter, 3, ref at,
                ItemType.Fire, ItemType.Forge, ItemType.Campfire),
            _ => false,
        };
    }

    private bool HasNearbyType(Character crafter, int range, params ItemType[] types)
    {
        SphereNet.Core.Types.Point3D ignored = crafter.Position;
        return FindNearbyType(crafter, range, ref ignored, types);
    }

    private bool FindNearbyType(Character crafter, int range, ref SphereNet.Core.Types.Point3D at, params ItemType[] types)
    {
        foreach (var item in _world.GetItemsInRange(crafter.Position, range))
        {
            if (item.IsDeleted) continue;
            foreach (var t in types)
            {
                if (item.ItemType != t) continue;
                at = item.Position;
                return true;
            }
        }

        // Map statics count as work sites too: a static graphic's type comes
        // from its itemdef (the way the reference resolves static tiles).
        var mapData = _world.MapData;
        if (mapData == null)
            return false;
        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                short x = (short)(crafter.X + dx);
                short y = (short)(crafter.Y + dy);
                foreach (var s in mapData.GetStatics(crafter.MapIndex, x, y))
                {
                    var sdef = DefinitionLoader.GetItemDef(s.TileId);
                    if (sdef == null) continue;
                    foreach (var t in types)
                    {
                        if (sdef.Type != t) continue;
                        at = new SphereNet.Core.Types.Point3D(x, y, (sbyte)s.Z, crafter.MapIndex);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Find the first matching item for a recipe resource (by TYPE or id),
    /// used to read the material hue before consumption.</summary>
    private static Item? FindResourceItem(Character ch, CraftResource res)
    {
        var pack = ch.Backpack;
        if (pack == null) return null;
        return res.Type.HasValue
            ? FindInContainerByType(pack, res.Type.Value, 0)
            : FindInContainer(pack, res.ItemId, 0);
    }

    private static Item? FindResourceItem(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        return pack == null ? null : FindInContainer(pack, itemId, 0);
    }

    private static Item? FindInContainerByType(Item container, ItemType type, int depth)
    {
        if (depth > 10) return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return item;
            if (!item.IsSearchableContainer) continue;
            var found = FindInContainerByType(item, type, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static Item? FindInContainer(Item container, ushort itemId, int depth)
    {
        if (depth > 10) return null;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId) return item;
            if (!item.IsSearchableContainer) continue;
            var found = FindInContainer(item, itemId, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static int CountInContainer(Item container, ushort itemId, int depth = 0)
    {
        if (depth > 10) return 0;
        int count = 0;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId)
                count += item.Amount;
            if (item.IsSearchableContainer)
                count += CountInContainer(item, itemId, depth + 1);
        }
        return count;
    }

    public IReadOnlyList<CraftMaterialOption> GetPrimaryResourceOptions(
        Character crafter, CraftRecipe recipe)
    {
        if (crafter.Backpack == null || recipe.Resources.Count == 0)
            return [];

        var resource = recipe.Resources[0];
        var totals = new SortedDictionary<ushort, long>();
        CollectResourceHues(crafter.Backpack, resource, totals, 0, []);
        var options = new List<CraftMaterialOption>();
        foreach (var pair in totals)
        {
            if (pair.Value < resource.Amount) continue;
            var sample = FindResourceItemByHue(crafter.Backpack, resource, pair.Key, 0, []);
            string name = sample?.GetName() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = pair.Key == 0 ? "Default material" : $"Material 0x{pair.Key:X4}";
            options.Add(new CraftMaterialOption(
                pair.Key, (int)Math.Min(int.MaxValue, pair.Value),
                sample?.BaseId ?? resource.ItemId, name));
        }
        return options;
    }

    private static bool TrySelectResourceHue(Character ch, CraftResource res,
        int requiredAmount, ushort? requestedHue, out ushort hue)
    {
        hue = 0;
        if (ch.Backpack == null) return false;
        var totals = new SortedDictionary<ushort, long>();
        CollectResourceHues(ch.Backpack, res, totals, 0, []);
        if (requestedHue.HasValue)
        {
            if (!totals.TryGetValue(requestedHue.Value, out long requestedTotal) ||
                requestedTotal < requiredAmount)
                return false;
            hue = requestedHue.Value;
            return true;
        }
        foreach (var pair in totals)
        {
            if (pair.Value < requiredAmount) continue;
            hue = pair.Key;
            return true;
        }
        return false;
    }

    private static Item? FindResourceItemByHue(Item container, CraftResource res,
        ushort hue, int depth, HashSet<uint> seen)
    {
        if (depth > 16 || !seen.Add(container.Uid.Value)) return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = res.Type.HasValue
                ? item.ItemType == res.Type.Value
                : item.BaseId == res.ItemId;
            if (matches && item.Hue.Value == hue)
                return item;
            if (item.ContentCount > 0 && item.IsSearchableContainer)
            {
                var found = FindResourceItemByHue(item, res, hue, depth + 1, seen);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static void CollectResourceHues(Item container, CraftResource res,
        SortedDictionary<ushort, long> totals, int depth, HashSet<uint> seen)
    {
        if (depth > 16 || !seen.Add(container.Uid.Value)) return;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = res.Type.HasValue
                ? item.ItemType == res.Type.Value
                : item.BaseId == res.ItemId;
            if (matches)
            {
                totals.TryGetValue(item.Hue.Value, out long amount);
                totals[item.Hue.Value] = Math.Min(int.MaxValue, amount + item.Amount);
            }
            if (item.ContentCount > 0 && item.IsSearchableContainer)
                CollectResourceHues(item, res, totals, depth + 1, seen);
        }
    }

    /// <summary>Consume a recipe resource (by TYPE or id). Returns true if fully consumed.</summary>
    private static bool ConsumeResource(Character ch, CraftResource res, int amount,
        ushort? hue = null)
    {
        var pack = ch.Backpack;
        if (pack == null) return false;
        if (res.Type.HasValue)
            ConsumeFromContainerByType(pack, res.Type.Value, ref amount, hue: hue);
        else
            ConsumeFromContainer(pack, res.ItemId, ref amount, hue: hue);
        return amount == 0;
    }

    /// <summary>Consume a specific amount of items from the backpack. Returns true if fully consumed.</summary>
    private static bool ConsumeResource(Character ch, ushort itemId, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null) return false;
        ConsumeFromContainer(pack, itemId, ref amount);
        return amount == 0;
    }

    private static void ConsumeFromContainerByType(Item container, ItemType type,
        ref int remaining, int depth = 0, ushort? hue = null)
    {
        if (depth > 10) return;
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];
            if (item.IsDeleted) continue;
            if (item.ItemType == type && (!hue.HasValue || item.Hue.Value == hue.Value))
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    item.RemoveFromWorld();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainerByType(item, type, ref remaining, depth + 1, hue);
            }
        }
    }

    private static void ConsumeFromContainer(Item container, ushort itemId,
        ref int remaining, int depth = 0, ushort? hue = null)
    {
        if (depth > 10) return;
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];

            if (item.BaseId == itemId && (!hue.HasValue || item.Hue.Value == hue.Value))
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    item.RemoveFromWorld();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainer(item, itemId, ref remaining, depth + 1, hue);
            }
        }
    }

    /// <summary>
    /// Scan all loaded ItemDefs for SKILLMAKE entries and register them as
    /// CraftRecipes. Called once after definitions are loaded.
    /// </summary>
    public int LoadRecipesFromDefs(ResourceHolder resources)
    {
        _recipes.Clear();
        int count = 0;
        foreach (var (baseId, def) in DefinitionLoader.AllItemDefs)
        {
            if (string.IsNullOrWhiteSpace(def.SkillMakeRaw))
                continue;

            var recipe = ParseRecipe(def, baseId, resources);
            if (recipe != null)
            {
                RegisterRecipe(recipe);
                count++;
            }
        }
        return count;
    }

    private static CraftRecipe? ParseRecipe(ItemDef def, int resultDefId, ResourceHolder resources)
    {
        // SKILLMAKE is a resource list, so it is read with the resource-list grammar:
        // either order, a bare name meaning one (CResourceQty.cpp:55). Reading it as
        // "name then value" only made "1 i_pen_and_ink" look like skill number 1 and
        // dropped the pen the recipe actually required.
        var skillParts = SphereNet.Scripting.Resources.ResourceQtyList.Parse(def.SkillMakeRaw);
        if (skillParts.Count == 0) return null;

        SkillType primarySkill = SkillType.None;
        int difficulty = 0;
        var skillReqs = new List<(SkillType Skill, int MinValue)>();
        var pendingToolTypes = new List<ItemType>();
        var pendingItemIds = new List<(ushort ItemId, int Amount)>();

        foreach (var part in skillParts)
        {
            // t_* = a tool TYPE that must be carried; i_* = a specific item
            // that must be present. Neither is consumed (reference
            // SkillResourceTest semantics).
            if (part.Name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            {
                var trid = resources.ResolveDefName(part.Name);
                if (trid.IsValid && trid.Type == Core.Enums.ResType.TypeDef)
                    pendingToolTypes.Add((ItemType)trid.Index);
                continue;
            }
            if (part.Name.StartsWith("i_", StringComparison.OrdinalIgnoreCase))
            {
                var irid = resources.ResolveDefName(part.Name);
                if (irid.IsValid)
                {
                    var reqDef = DefinitionLoader.GetItemDef(irid.Index);
                    ushort requiredId = reqDef is { DispIndex: > 0 }
                        ? reqDef.DispIndex
                        : irid.Index <= ushort.MaxValue ? (ushort)irid.Index : (ushort)0;
                    if (requiredId != 0)
                        pendingItemIds.Add((requiredId, (int)Math.Max(1, part.Quantity)));
                }
                continue;
            }

            // The shard's own name for the slot, not just the engine's spelling -
            // upstream resolves these with FindSkillKey (CServerConfig.cpp:2347).
            if (!SphereNet.Game.Definitions.SkillNames.TryResolve(part.Name, out var skill))
                continue;

            // A skill requirement's quantity is already in tenths: the pack writes
            // 50.0 and the reference's decimal path ignores the dot, giving 500.
            int val = SphereNet.Scripting.Resources.ResourceQtyList.SkillValue(part);

            if (primarySkill == SkillType.None)
            {
                primarySkill = skill;
                difficulty = val;
            }
            skillReqs.Add((skill, val));
        }

        if (primarySkill == SkillType.None) return null;

        ushort dispId = def.DispIndex != 0
            ? def.DispIndex
            : resultDefId <= ushort.MaxValue ? (ushort)resultDefId : (ushort)0;
        if (dispId == 0) return null;

        var recipe = new CraftRecipe
        {
            ResultDefId = resultDefId,
            ResultItemId = dispId,
            ResultName = def.Name ?? "",
            PrimarySkill = primarySkill,
            Difficulty = difficulty / 10
        };

        foreach (var sr in skillReqs)
            recipe.SkillRequirements.Add(sr);
        recipe.RequiredToolTypes.AddRange(pendingToolTypes);
        recipe.RequiredItemIds.AddRange(pendingItemIds);

        if (!string.IsNullOrWhiteSpace(def.ResourcesRaw))
        {
            // The same grammar again, and here the missing half was the bare name:
            // RESOURCES=i_spellbook means one spellbook, and 257 entries in the
            // shipped pack are written that way. Demanding a written quantity made
            // every one of them free.
            foreach (var rp in SphereNet.Scripting.Resources.ResourceQtyList.Parse(def.ResourcesRaw))
            {
                int amount = (int)Math.Clamp(rp.Quantity, 0, int.MaxValue);
                if (amount <= 0) continue;

                var rid = resources.ResolveDefName(rp.Name);
                if (!rid.IsValid) continue;

                // A RESOURCES entry can name an item TYPE (t_ingot) or a specific
                // item (i_ingot_iron). A type entry resolves to a TypeDef and must
                // match by ItemType — storing the type index as a BaseId (the old
                // behaviour) made the recipe uncraftable.
                if (rid.Type == Core.Enums.ResType.TypeDef)
                {
                    recipe.Resources.Add(new CraftResource { Type = (ItemType)rid.Index, Amount = amount });
                }
                else
                {
                    var resDef = DefinitionLoader.GetItemDef(rid.Index);
                    ushort resItemId = resDef is { DispIndex: > 0 }
                        ? resDef.DispIndex
                        : rid.Index <= ushort.MaxValue ? (ushort)rid.Index : (ushort)0;
                    if (resItemId != 0)
                        recipe.Resources.Add(new CraftResource { ItemId = resItemId, Amount = amount });
                }
            }
        }

        return recipe;
    }
}
