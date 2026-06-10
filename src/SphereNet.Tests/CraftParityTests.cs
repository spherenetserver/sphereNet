using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Crafting parity (reference Skill_Blacksmith / Skill_Cooking /
/// Skill_MakeItem): work-site proximity, SKILLMAKE tool presence and the
/// uniform 0-50% fail loss.
/// </summary>
public class CraftParityTests
{
    private static (GameWorld world, CraftingEngine engine, Character ch, Item pack) CreateCrafter()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var engine = new CraftingEngine(world);
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Blacksmithing, 2000);
        ch.SetSkill(SkillType.Cooking, 2000);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        return (world, engine, ch, pack);
    }

    private static CraftRecipe SmithRecipe()
    {
        var recipe = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            ResultName = "viking sword",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 0,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });
        return recipe;
    }

    private static void AddIngots(GameWorld world, Item pack, int amount)
    {
        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = (ushort)amount;
        pack.AddItem(ingots);
    }

    [Fact]
    public void Blacksmithing_RequiresForgeWithinTwoTiles()
    {
        var (world, engine, ch, pack) = CreateCrafter();
        AddIngots(world, pack, 10);
        var recipe = SmithRecipe();

        Assert.False(engine.CanCraft(ch, recipe)); // no forge anywhere

        var farForge = world.CreateItem();
        farForge.ItemType = ItemType.Forge;
        world.PlaceItem(farForge, new Point3D(104, 100, 0, 0)); // 4 tiles away
        Assert.False(engine.CanCraft(ch, recipe));

        var nearForge = world.CreateItem();
        nearForge.ItemType = ItemType.Forge;
        world.PlaceItem(nearForge, new Point3D(102, 100, 0, 0)); // 2 tiles
        Assert.True(engine.CanCraft(ch, recipe));
    }

    [Fact]
    public void Cooking_RequiresHeatSourceWithinThreeTiles()
    {
        var (world, engine, ch, pack) = CreateCrafter();
        var recipe = new CraftRecipe
        {
            ResultItemId = 0x103B,
            ResultName = "bread",
            PrimarySkill = SkillType.Cooking,
            Difficulty = 0,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1039, Amount = 1 });
        var flour = world.CreateItem();
        flour.BaseId = 0x1039;
        flour.Amount = 5;
        pack.AddItem(flour);

        Assert.False(engine.CanCraft(ch, recipe));

        var campfire = world.CreateItem();
        campfire.ItemType = ItemType.Campfire;
        world.PlaceItem(campfire, new Point3D(103, 100, 0, 0)); // 3 tiles
        Assert.True(engine.CanCraft(ch, recipe));
    }

    [Fact]
    public void RequiredToolType_MustBeCarried_NotConsumed()
    {
        var (world, engine, ch, pack) = CreateCrafter();
        AddIngots(world, pack, 10);
        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var recipe = SmithRecipe();
        recipe.RequiredToolTypes.Add(ItemType.WeaponMaceSmith); // smith hammer type

        Assert.False(engine.CanCraft(ch, recipe)); // no hammer carried

        var hammer = world.CreateItem();
        hammer.ItemType = ItemType.WeaponMaceSmith;
        pack.AddItem(hammer);
        Assert.True(engine.CanCraft(ch, recipe));

        var crafted = engine.TryCraft(ch, recipe);
        Assert.NotNull(crafted);
        Assert.False(hammer.IsDeleted); // tool is not consumed
    }

    [Fact]
    public void FailLoss_NeverExceedsHalfOfRequirements()
    {
        var (world, engine, ch, pack) = CreateCrafter();
        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        // Skill 0 vs an impossible difficulty: the roll always fails, so
        // only the fail-loss branch runs.
        ch.SetSkill(SkillType.Blacksmithing, 0);

        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = 10;
        pack.AddItem(ingots);

        var recipeHard = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 300, // far past the zero-chance tail at skill 0
        };
        recipeHard.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });
        engine.RegisterRecipe(recipeHard);

        for (int i = 0; i < 20; i++)
        {
            ingots.Amount = 10; // top the stack back up before each attempt

            Assert.Null(engine.TryCraft(ch, recipeHard));

            // Loss is amount * (0..49) / 100 → at most 4 of 10, never more than half.
            Assert.InRange((int)ingots.Amount, 6, 10);
        }
    }
}
