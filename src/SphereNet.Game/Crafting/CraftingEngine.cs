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
}

/// <summary>
/// A craftable item recipe. Loaded from [ITEMDEF] RESOURCES/SKILLMAKE sections.
/// </summary>
public sealed class CraftRecipe
{
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
    /// not consumed.</summary>
    public List<ushort> RequiredItemIds { get; } = [];
}

/// <summary>
/// Crafting engine. Maps to CChar::Skill_MakeItem in Source-X CCharSkill.cpp.
/// Handles resource checking, consumption, success/fail, and item creation.
/// </summary>
public sealed class CraftingEngine
{
    private readonly GameWorld _world;
    private readonly Dictionary<ushort, CraftRecipe> _recipes = [];

    public CraftingEngine(GameWorld world)
    {
        _world = world;
    }

    public void RegisterRecipe(CraftRecipe recipe) =>
        _recipes[recipe.ResultItemId] = recipe;

    public CraftRecipe? GetRecipe(ushort itemId) =>
        _recipes.GetValueOrDefault(itemId);

    public IReadOnlyDictionary<ushort, CraftRecipe> AllRecipes => _recipes;

    /// <summary>Get all recipes for a given primary skill.</summary>
    public List<CraftRecipe> GetRecipesBySkill(SkillType skill) =>
        _recipes.Values.Where(r => r.PrimarySkill == skill).ToList();

    /// <summary>
    /// Check if a character has the resources and skills to craft an item.
    /// Maps to SkillResourceTest in Source-X.
    /// </summary>
    /// <summary>Recipe lookup by result display id (SKILLMENU MAKEITEM).</summary>
    public CraftRecipe? TryGetRecipe(ushort resultDispId) =>
        _recipes.GetValueOrDefault(resultDispId);

    public bool CanCraft(Character crafter, CraftRecipe recipe)
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
        foreach (var reqId in recipe.RequiredItemIds)
        {
            if (CountResource(crafter, reqId) < 1)
                return false;
        }

        // Work-site proximity (reference Skill_Blacksmith / Skill_Cooking):
        // smithing needs a forge within 2 tiles, cooking a heat source
        // within 3 (fire, forge or campfire).
        if (!HasRequiredWorkSite(crafter, recipe.PrimarySkill))
            return false;

        // Check resource availability
        foreach (var res in recipe.Resources)
        {
            if (CountResource(crafter, res.ItemId) < res.Amount)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Attempt to craft an item. Returns the crafted item on success, null on failure.
    /// Maps to Skill_MakeItem / Skill_MakeItem_Success flow in Source-X.
    /// </summary>
    public Item? TryCraft(Character crafter, CraftRecipe recipe)
    {
        if (crafter.IsDead) return null;
        if (!CanCraft(crafter, recipe))
            return null;

        // Skill check
        bool success = SkillEngine.UseQuick(crafter, recipe.PrimarySkill, recipe.Difficulty);

        if (success)
        {
            // Re-verify resources before consuming (gump callback delay may have changed state)
            foreach (var res in recipe.Resources)
            {
                if (CountResource(crafter, res.ItemId) < res.Amount)
                    return null;
            }

            // Capture the primary resource's hue BEFORE consuming so the crafted
            // item can inherit the material colour (e.g. coloured ingots produce
            // a coloured weapon/armour, matching UO material behaviour).
            ushort resourceHue = 0;
            if (recipe.Resources.Count > 0)
            {
                var primaryRes = FindResourceItem(crafter, recipe.Resources[0].ItemId);
                if (primaryRes != null) resourceHue = primaryRes.Hue.Value;
            }

            // Consume resources
            foreach (var res in recipe.Resources)
            {
                ConsumeResource(crafter, res.ItemId, res.Amount);
            }

            // Create the item
            var item = _world.CreateItem();
            item.BaseId = recipe.ResultItemId;
            item.Name = recipe.ResultName;
            if (resourceHue != 0)
                item.Hue = new Core.Types.Color(resourceHue);

            // Quality roll based on skill
            int skillVal = crafter.GetSkill(recipe.PrimarySkill);
            int quality = CalcQuality(skillVal, recipe.Difficulty);
            if (quality > 100)
                item.SetTag("QUALITY", quality.ToString());

            // Exceptional check — also apply a concrete durability bonus and an
            // EXCEPTIONAL tag, so the result is mechanically better, not just a
            // renamed normal item.
            if (quality >= 150)
            {
                item.Name = "exceptional " + item.Name;
                item.SetTag("EXCEPTIONAL", "1");
                int baseMax = item.HitsMax;
                if (baseMax > 0)
                {
                    int boosted = baseMax * 120 / 100;
                    item.HitsMax = boosted;
                    item.HitsCur = boosted;
                }
            }

            // Caller (GameClient.OpenCraftingGump) handles placement + notification
            return item;
        }
        else
        {
            // Partial resource loss on failure (reference Skill_MakeItem
            // SKTRIG_FAIL → ResourceConsumePart): one 0-50%% roll applied
            // uniformly to every required resource.
            int lossPercent = Random.Shared.Next(50);
            foreach (var res in recipe.Resources)
            {
                int lostAmount = res.Amount * lossPercent / 100;
                if (lostAmount > 0)
                    ConsumeResource(crafter, res.ItemId, lostAmount);
            }

            return null;
        }
    }

    /// <summary>
    /// Calculate item quality (100 = normal, 150+ = exceptional).
    /// </summary>
    private int CalcQuality(int skillLevel, int difficulty)
    {
        int excess = skillLevel - difficulty;
        int quality = 100 + excess / 10;
        quality += Random.Shared.Next(-10, 11);
        return Math.Max(10, Math.Min(200, quality));
    }

    /// <summary>Count how many of a specific item ID the character has in their backpack.</summary>
    private static int CountResource(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return CountInContainer(pack, itemId);
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

    private static bool HasItemOfTypeIn(Item container, ItemType type, int depth)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return true;
            if (depth > 0 && item.ContentCount > 0 && HasItemOfTypeIn(item, type, depth - 1))
                return true;
        }
        return false;
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

    private bool HasNearbyType(Character crafter, int range, params ItemType[] types)
    {
        foreach (var item in _world.GetItemsInRange(crafter.Position, range))
        {
            if (item.IsDeleted) continue;
            foreach (var t in types)
            {
                if (item.ItemType == t)
                    return true;
            }
        }
        return false;
    }

    private static Item? FindResourceItem(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        return pack == null ? null : FindInContainer(pack, itemId, 0);
    }

    private static Item? FindInContainer(Item container, ushort itemId, int depth)
    {
        if (depth > 10) return null;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId) return item;
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
            count += CountInContainer(item, itemId, depth + 1);
        }
        return count;
    }

    /// <summary>Consume a specific amount of items from the backpack. Returns true if fully consumed.</summary>
    private static bool ConsumeResource(Character ch, ushort itemId, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null) return false;
        ConsumeFromContainer(pack, itemId, ref amount);
        return amount == 0;
    }

    private static void ConsumeFromContainer(Item container, ushort itemId, ref int remaining, int depth = 0)
    {
        if (depth > 10) return;
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];

            if (item.BaseId == itemId)
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    container.RemoveItem(item);
                    item.Delete();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainer(item, itemId, ref remaining, depth + 1);
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

            var recipe = ParseRecipe(def, (ushort)baseId, resources);
            if (recipe != null)
            {
                RegisterRecipe(recipe);
                count++;
            }
        }
        return count;
    }

    private static CraftRecipe? ParseRecipe(ItemDef def, ushort resultId, ResourceHolder resources)
    {
        var skillParts = def.SkillMakeRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (skillParts.Length == 0) return null;

        SkillType primarySkill = SkillType.None;
        int difficulty = 0;
        var skillReqs = new List<(SkillType Skill, int MinValue)>();
        var pendingToolTypes = new List<ItemType>();
        var pendingItemIds = new List<ushort>();

        foreach (var part in skillParts)
        {
            // t_* = a tool TYPE that must be carried; i_* = a specific item
            // that must be present. Neither is consumed (reference
            // SkillResourceTest semantics).
            if (part.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            {
                string toolName = part.Split(' ', 2)[0].Trim();
                var trid = resources.ResolveDefName(toolName);
                if (trid.IsValid && trid.Type == Core.Enums.ResType.TypeDef)
                    pendingToolTypes.Add((ItemType)trid.Index);
                continue;
            }
            if (part.StartsWith("i_", StringComparison.OrdinalIgnoreCase))
            {
                string itemName = part.Split(' ', 2)[0].Trim();
                var irid = resources.ResolveDefName(itemName);
                if (irid.IsValid)
                {
                    var reqDef = DefinitionLoader.GetItemDef(irid.Index);
                    pendingItemIds.Add(reqDef?.DispIndex ?? (ushort)irid.Index);
                }
                continue;
            }

            int spaceIdx = part.LastIndexOf(' ');
            if (spaceIdx < 0) continue;

            string skillName = part[..spaceIdx].Trim();
            string valStr = part[(spaceIdx + 1)..].Trim();

            if (!Enum.TryParse<SkillType>(skillName, true, out var skill))
                continue;

            int val = 0;
            if (valStr.Contains('.'))
            {
                if (double.TryParse(valStr, System.Globalization.CultureInfo.InvariantCulture, out double dv))
                    val = (int)(dv * 10);
            }
            else if (int.TryParse(valStr, out int iv))
            {
                val = iv * 10;
            }

            if (primarySkill == SkillType.None)
            {
                primarySkill = skill;
                difficulty = val;
            }
            skillReqs.Add((skill, val));
        }

        if (primarySkill == SkillType.None) return null;

        ushort dispId = def.DispIndex != 0 ? def.DispIndex : resultId;

        var recipe = new CraftRecipe
        {
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
            var resParts = def.ResourcesRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var rp in resParts)
            {
                int spIdx = rp.IndexOf(' ');
                if (spIdx < 0) continue;

                string amtStr = rp[..spIdx].Trim();
                string resName = rp[(spIdx + 1)..].Trim();
                if (!int.TryParse(amtStr, out int amount) || amount <= 0) continue;

                var rid = resources.ResolveDefName(resName);
                ushort resItemId;
                if (rid.IsValid)
                {
                    var resDef = DefinitionLoader.GetItemDef(rid.Index);
                    resItemId = resDef?.DispIndex ?? (ushort)rid.Index;
                }
                else
                {
                    continue;
                }

                recipe.Resources.Add(new CraftResource { ItemId = resItemId, Amount = amount });
            }
        }

        return recipe;
    }
}
