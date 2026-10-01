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
    /// <summary>The graphic of an ITEMDEF resource - what the crafting UI shows. It is
    /// not the resource's identity: <see cref="Resource"/> is.</summary>
    public ushort ItemId { get; init; }
    public int Amount { get; init; }
    /// <summary>When set, the resource matches by item TYPE (Source-X RES_TYPEDEF —
    /// e.g. "any t_ingot") rather than a specific item id. Lets a recipe consume any
    /// item of a category instead of a single hardcoded BaseId.</summary>
    public ItemType? Type { get; init; }
    /// <summary>The full resource id the recipe named (RES_ITEMDEF with the definition's
    /// own index, or RES_TYPEDEF). Two definitions sharing a graphic are two
    /// resources (CItem::IsResourceMatch, CItem.cpp:6034).</summary>
    public SphereNet.Core.Types.ResourceId Resource { get; init; }

    /// <summary>The id the resource walk matches: <see cref="Resource"/> when the
    /// recipe carried one, else the TYPE, else the graphic as a numbered ITEMDEF.</summary>
    public SphereNet.Core.Types.ResourceId Rid =>
        Resource.IsValid ? Resource
        : Type.HasValue ? ResourceMatch.ForType(Type.Value)
        : ResourceMatch.ForItemDef(ItemId);
}

/// <summary>A SKILLMAKE i_* entry: present, not consumed. <see cref="ItemId"/> is the
/// graphic for display; <see cref="Resource"/> is what is matched.</summary>
public readonly record struct CraftRequiredItem(ushort ItemId, int Amount,
    SphereNet.Core.Types.ResourceId Resource)
{
    public SphereNet.Core.Types.ResourceId Rid =>
        Resource.IsValid ? Resource : ResourceMatch.ForItemDef(ItemId);
}

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
    public List<CraftRequiredItem> RequiredItemIds { get; } = [];
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
    public bool CanCraft(Character crafter, CraftRecipe recipe,
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
        foreach (var required in recipe.RequiredItemIds)
        {
            if (ResourceMatch.ConsumeTest(crafter, required.Rid, required.Amount) > 0)
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
        }

        return true;
    }

    /// <summary>
    /// Attempt to craft an item in one go: the roll, then the success or failure stage.
    /// Returns the crafted item on success, null on failure. The client's timed craft
    /// runs the same stages one by one (<see cref="RollCraft"/>,
    /// <see cref="CraftSuccess"/>, <see cref="CraftFail"/>) so that its triggers can
    /// sit between them the way Source-X Skill_Start and Skill_Done order them.
    /// </summary>
    public Item? TryCraft(Character crafter, CraftRecipe recipe)
    {
        lock (crafter)
        {
            if (crafter.IsDead) return null;
            if (!CanCraft(crafter, recipe))
                return null;

            if (RollCraft(crafter, recipe.PrimarySkill, recipe.Difficulty))
            {
                var outcome = CraftSuccessCore(crafter, recipe, 1);
                if (outcome != null)
                    SkillEngine.GainExperience(crafter, recipe.PrimarySkill, recipe.Difficulty);
                return outcome?.Item;
            }

            CraftFailCore(crafter, recipe);
            SkillEngine.GainExperience(crafter, recipe.PrimarySkill, -recipe.Difficulty);
            return null;
        }
    }

    /// <summary>A craft's success roll as Skill_Start makes it (CCharSkill.cpp:4566):
    /// only a positive difficulty is rolled - Skill_CheckSuccess on the bell curve,
    /// with no experience and no @SkillUseQuick - and a difficulty of zero never
    /// fails. The difficulty is the primary SKILLMAKE value in whole points
    /// (Skill_MakeItem SKTRIG_START, :964).</summary>
    public static bool RollCraft(Character crafter, SkillType skill, int difficulty) =>
        !crafter.IsDead &&
        (difficulty <= 0 || SkillEngine.CheckSuccess(crafter, skill, difficulty, useBellCurve: true));

    /// <summary>How far the crafter may stand from the work site the start found
    /// (Skill_Blacksmith 2, Skill_Cooking 3; CCharSkill.cpp:3146/2229); 0 for a skill
    /// with no work site.</summary>
    public static int WorkSiteRange(SkillType skill) => skill switch
    {
        SkillType.Blacksmithing => 2,
        SkillType.Cooking => 3,
        _ => 0,
    };

    /// <summary>What one craft success produced: the item the trigger and the bounce
    /// see, every further copy a non-stackable replication made, the quality the
    /// single-item branch rolled (0 when that branch did not run) and how many
    /// replications the resources really paid for.</summary>
    public sealed record CraftOutcome(Item Item, IReadOnlyList<Item> Extras, int Quality,
        bool QualityRolled, int Amount);

    /// <summary>How many whole replications of <paramref name="recipe"/> the crafter's
    /// stock pays for, capped at <paramref name="requested"/> - Source-X
    /// ResourceConsume in test mode (CContainer.cpp:568): every resource is counted in
    /// units of one replication and the smallest count wins; a recipe with no
    /// resources makes as many as were asked for. Colour plays no part
    /// (CItem::IsResourceMatch, CItem.cpp:6027).</summary>
    public int TestReplication(Character crafter, CraftRecipe recipe, int requested)
    {
        if (requested <= 0)
            requested = 1;
        int possible = requested;
        for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
        {
            var res = recipe.Resources[resourceIndex];
            if (res.Amount <= 0)
                continue;
            long available = CountResource(crafter, res);
            possible = (int)Math.Min(possible, available / res.Amount);
            if (possible <= 0)
                return 0;
        }
        return possible;
    }

    /// <summary>The SUCCESS stage of a craft (Source-X Skill_MakeItem SKTRIG_SUCCESS,
    /// CCharSkill.cpp:920/968, then Skill_MakeItem_Success, :674): pay for as many of
    /// the <paramref name="replicationQty"/> replications as the stock still covers,
    /// then make the result. Null when nothing could be paid for - the stage aborts.
    /// No trigger runs here; @SkillMakeItem and the bounce belong to the caller.</summary>
    public CraftOutcome? CraftSuccess(Character crafter, CraftRecipe recipe, int replicationQty)
    {
        lock (crafter)
            return CraftSuccessCore(crafter, recipe, replicationQty);
    }

    private CraftOutcome? CraftSuccessCore(Character crafter, CraftRecipe recipe, int replicationQty)
    {
        if (crafter.IsDead)
            return null;

        // ResourceConsume (CContainer.cpp:568): test what can really be made first,
        // then take that many replications of every resource.
        int amount = TestReplication(crafter, recipe, Math.Clamp(replicationQty, 1, ushort.MaxValue));
        if (amount <= 0)
            return null;

        foreach (var res in recipe.Resources)
        {
            if (!ConsumeResource(crafter, res, res.Amount * amount))
                return null;
        }

        var item = CreateCraftedItem(crafter, recipe);
        var extras = new List<Item>();
        int skillVal = crafter.GetSkill(recipe.PrimarySkill);   // Skill_GetBase of the active skill
        int quality = 0;
        bool qualityRolled = false;

        // Skill_MakeItem_Success (CCharSkill.cpp:688-801) has three exclusive branches.
        if (amount != 1)
        {
            // A replication: a scroll still takes the scribe's skill as its spell
            // level, a pile takes the amount, anything else is made again - one
            // plain copy per replication, bounced on its own (:690-707). No quality.
            if (item.ItemType == ItemType.Scroll)
                SetSpellLevel(item, skillVal);
            if (item.IsStackable)
            {
                item.Amount = (ushort)Math.Min(amount, ushort.MaxValue);
            }
            else
            {
                for (int n = 1; n < amount; n++)
                    extras.Add(CreateCraftedItem(crafter, recipe));
            }
        }
        else if (item.ItemType == ItemType.Scroll)
        {
            // "scrolls have the skill level of the inscriber" (:709-713): MOREY is the
            // spell level, and a scroll gets no quality.
            SetSpellLevel(item, skillVal);
        }
        else if (item.ItemType == ItemType.Potion)
        {
            // Potions are left as made (:714-717).
        }
        else
        {
            // Quality roll based on skill (Source-X Skill_MakeItem band table), only
            // on a single item.
            quality = CalcQuality(skillVal);
            qualityRolled = true;
            item.Quality = (ushort)quality;

            // Source-X CCharSkill.cpp:799: only a grandmaster (skill > 99.9)
            // producing quality > 175 gets the maker's mark on the name, and only
            // while OF_NOITEMNAMING is off — a shard can switch the marks away, and
            // that bit was in the enum with nothing reading it. The old invented
            // "exceptional" rename + 20% durability boost had no reference basis
            // (durability comes solely from the def).
            if (EarnsMakersMark(skillVal, quality))
                item.Name = $"{item.Name} crafted by {crafter.Name}";
        }

        return new CraftOutcome(item, extras, quality, qualityRolled, amount);
    }

    /// <summary>MOREY of a scroll is m_itSpell.m_spelllevel (CItem.h:257).</summary>
    private static void SetSpellLevel(Item item, int level)
    {
        var p = item.MoreP;
        item.MoreP = new SphereNet.Core.Types.Point3D(p.X,
            (short)Math.Clamp(level, 0, ushort.MaxValue), p.Z, p.Map);
    }

    /// <summary>One crafted piece from its definition (CItem::CreateTemplate): the
    /// definition's instance data, its hit points and the maker, and THEN its @Create - exactly once, through the instance's own
    /// guard - so whatever the creation script sets (TYPE, COLOR, a bonus) is what the
    /// item keeps. The definition's TYPE/TDATA/TAGs used to be written a second time
    /// after @Create had run, and the client then dispatched @Create again.</summary>
    private Item CreateCraftedItem(Character crafter, CraftRecipe recipe)
    {
        var item = _world.CreateItem();
        item.BaseId = recipe.ResultItemId;
        int defIndex = recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId;
        var resultDef = DefinitionLoader.GetItemDef(defIndex);
        item.Name = !string.IsNullOrWhiteSpace(recipe.ResultName)
            ? recipe.ResultName
            : DefinitionLoader.ResolveNames(resultDef?.Name ?? "");
        if (resultDef != null)
        {
            ItemDefHelper.ApplyInstanceMetadata(item, defIndex,
                setDisplayId: false, setName: false, fireCreate: false);
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
        // No material colour: Skill_MakeItem_Success never colours the result
        // (CCharSkill.cpp:674-865); a pack does that from @SkillMakeItem through ACT.
        if (resultDef != null)
            item.FireCreateTrigger();
        return item;
    }

    /// <summary>The FAIL stage of a craft (Source-X Skill_MakeItem SKTRIG_FAIL,
    /// CCharSkill.cpp:920-946): when the stock still covers one replication, part of
    /// that ONE replication's bill is paid - whatever replication count the craft was
    /// started with (Skill_MakeItem(SKTRIG_FAIL) is called with its default quantity
    /// of 1, :3104).</summary>
    public void CraftFail(Character crafter, CraftRecipe recipe)
    {
        lock (crafter)
            CraftFailCore(crafter, recipe);
    }

    private void CraftFailCore(Character crafter, CraftRecipe recipe)
    {
        // Skill_MakeItem runs SkillResourceTest first at every stage (:913), and
        // nothing is spent unless one whole replication is still there (:920).
        if (!CanCraft(crafter, recipe, skillOnly: true) || TestReplication(crafter, recipe, 1) <= 0)
            return;

        // Partial resource loss on failure (reference Skill_MakeItem
        // SKTRIG_FAIL → ResourceConsumePart, CCharSkill.cpp:925-945). The
        // percent has THREE sources in order, and only the last was implemented:
        //   1. ACTIONEFFECT, when a script set one on this attempt;
        //   2. the crafting skill's own EFFECT curve, rolled at random
        //      (the live pack gives Inscription EFFECT=50);
        //   3. a flat 0-49%% roll.
        // ResourceConsumePart (:945): no MATOVERRIDE, IMulDiv-scaled entries.
        int lossPercent = ResolveFailureLossPercent(crafter, recipe);
        TryConsumeResourcePart(crafter, recipe, lossPercent, test: false);
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
        // ResourceConsumePart has neither MATOVERRIDE nor the skill-entry check that
        // ResourceConsume has: each entry is matched as written, scaled with IMulDiv,
        // and a real spend takes what is there of every entry.
        var list = recipe.Resources
            .Select(r => new ResourceMatch.ResourceQty(r.Rid, r.Amount))
            .ToList();
        return ResourceMatch.ResourceConsumePart(ch, list, 1, percent, test) < 0;
    }

    /// <summary>
    /// How much of a resource the character's reachable stock holds — the same walk
    /// a craft tests and spends with (<see cref="ResourceMatch"/>), so a SKILLTEST that
    /// says yes is followed by a craft that agrees. A TYPE resource counts every item
    /// of that type; an ITEMDEF resource counts that definition (plus the reference's
    /// boards-for-logs / leather-for-hides alternatives).
    /// </summary>
    public static int CountStock(Character ch, SphereNet.Core.Types.ResourceId rid) =>
        rid.IsValid ? (int)Math.Min(int.MaxValue, ResourceMatch.Count(ch, rid)) : 0;

    /// <summary>Count a recipe resource the way Skill_MakeItem's ResourceConsume test
    /// does: over the crafter's whole content (CCharSkill.cpp:920 runs it on the
    /// CHARACTER), descending only into searchable containers.
    /// A skill entry in RESOURCES is a level, not a stock (ResourceConsume returns 0
    /// when the crafter's base skill is short of it), and an ITEMDEF entry is first
    /// replaced by the crafter's TAG.MATOVERRIDE_&lt;defname&gt; (CContainer.cpp:600-617).</summary>
    private static int CountResource(Character ch, CraftResource res)
    {
        var rid = res.Rid;
        if (rid.Type == Core.Enums.ResType.SkillDef)
            return ch.GetSkill((SkillType)rid.Index) >= res.Amount ? int.MaxValue : 0;
        return (int)Math.Min(int.MaxValue, ResourceMatch.Count(ch, ResourceMatch.MaterialOverride(ch, rid)));
    }

    /// <summary>The crafter holds an item of the given type - SKILLMAKE's t_* entry,
    /// a ContentConsumeTest for one on the character (CCharStatus.cpp:72): in hand,
    /// worn, or anywhere in a container the walk may search.</summary>
    private static bool HasItemOfType(Character ch, ItemType type) =>
        ResourceMatch.ConsumeTest(ch, ResourceMatch.ForType(type), 1) == 0;

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

    /// <summary>Consume a recipe resource - ContentConsume on the crafter
    /// (CCharSkill.cpp:920), the walk <see cref="CountResource"/> tested with, so a
    /// locked chest that was not counted is never spent from. A chosen material hue
    /// narrows the match. A skill entry spends nothing; an ITEMDEF entry honours
    /// MATOVERRIDE as ResourceConsume does. Returns true if fully consumed.
    /// <paramref name="materialOverride"/> false is the ResourceConsumePart flavour -
    /// the failed-craft loss (CCharSkill.cpp:945) - which applies no override.</summary>
    private static bool ConsumeResource(Character ch, CraftResource res, int amount,
        ushort? hue = null, bool materialOverride = true)
    {
        var rid = res.Rid;
        if (rid.Type == Core.Enums.ResType.SkillDef)
            return materialOverride || ResourceMatch.ConsumeTest(ch, rid, amount) == 0;
        if (materialOverride)
            rid = ResourceMatch.MaterialOverride(ch, rid);
        Func<Item, bool>? filter = hue.HasValue
            ? item => item.Hue.Value == hue.Value
            : null;
        return ResourceMatch.Consume(ch, rid, amount, filter: filter) == 0;
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
        var pendingItemIds = new List<CraftRequiredItem>();

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
                // Matched by the definition it names, not by its picture: upstream's
                // SkillResourceTest is a ContentConsumeTest on that resource id
                // (CCharStatus.cpp:76).
                var irid = resources.ResolveDefName(part.Name);
                if (irid.IsValid && irid.Type == Core.Enums.ResType.ItemDef)
                {
                    var reqDef = DefinitionLoader.GetItemDef(irid.Index);
                    ushort requiredId = ItemDefHelper.CreateGraphic(reqDef, irid.Index);
                    pendingItemIds.Add(new CraftRequiredItem(requiredId,
                        (int)Math.Max(1, part.Quantity), irid));
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
            //
            // The list is loaded the way CResourceQtyArray::Load loads it (a bare
            // number is not a resource and ends the list; a repeat replaces the
            // earlier entry), and every entry keeps its full resource id: an ITEMDEF
            // is the definition it named, not its graphic (CItem::IsResourceMatch,
            // :6034), a TYPEDEF matches by type, a skill is a level the crafter must
            // have (CContainer.cpp:600).
            foreach (var rp in ResourceMatch.LoadList(def.ResourcesRaw, resources))
            {
                int amount = (int)Math.Clamp(rp.Qty, 0, int.MaxValue);
                if (amount <= 0) continue;
                var rid = rp.Rid;

                if (rid.Type == Core.Enums.ResType.TypeDef)
                {
                    recipe.Resources.Add(new CraftResource
                    {
                        Type = (ItemType)rid.Index, Amount = amount, Resource = rid,
                    });
                }
                else if (rid.Type == Core.Enums.ResType.ItemDef)
                {
                    // The graphic stays for the crafting UI only.
                    var resDef = DefinitionLoader.GetItemDef(rid.Index);
                    recipe.Resources.Add(new CraftResource
                    {
                        ItemId = ItemDefHelper.CreateGraphic(resDef, rid.Index),
                        Amount = amount,
                        Resource = rid,
                    });
                }
                else
                {
                    recipe.Resources.Add(new CraftResource { Amount = amount, Resource = rid });
                }
            }
        }

        return recipe;
    }
}
