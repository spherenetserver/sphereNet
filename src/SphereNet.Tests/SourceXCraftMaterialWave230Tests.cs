using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCraftMaterialWave230Tests
{
    [Fact]
    public void PrimaryResourceOptions_ListOnlySufficientHuesInStableOrder()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        AddMaterial(world, pack, "valorite ingots", 0x08AB, 8);
        AddMaterial(world, pack, "iron ingots", 0x0000, 10);
        AddMaterial(world, pack, "copper ingots", 0x06D6, 4);

        var options = engine.GetPrimaryResourceOptions(crafter, recipe);

        Assert.Collection(options,
            iron =>
            {
                Assert.Equal((ushort)0x0000, iron.Hue);
                Assert.Equal(10, iron.Available);
                Assert.Equal("iron ingots", iron.Name);
            },
            valorite =>
            {
                Assert.Equal((ushort)0x08AB, valorite.Hue);
                Assert.Equal(8, valorite.Available);
                Assert.Equal("valorite ingots", valorite.Name);
            });
    }

    [Fact]
    public void SelectedMaterialHue_IsConsumedAndInheritedByCraftedItem()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        var iron = AddMaterial(world, pack, "iron ingots", 0x0000, 10);
        var valorite = AddMaterial(world, pack, "valorite ingots", 0x08AB, 10);

        var result = engine.TryCraft(crafter, recipe, 0x08AB);

        Assert.NotNull(result);
        Assert.Equal((ushort)0x08AB, result!.Hue.Value);
        Assert.Equal(10, iron.Amount);
        Assert.Equal(5, valorite.Amount);
    }

    [Fact]
    public void RequestedHue_MustContainTheWholePrimaryRequirement()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        var iron = AddMaterial(world, pack, "iron ingots", 0x0000, 10);
        var valorite = AddMaterial(world, pack, "valorite ingots", 0x08AB, 4);

        Assert.False(engine.CanCraft(crafter, recipe, 0x08AB));
        Assert.Null(engine.TryCraft(crafter, recipe, 0x08AB));
        Assert.Equal(10, iron.Amount);
        Assert.Equal(4, valorite.Amount);
    }

    [Fact]
    public void FailedCraft_LosesOnlyTheSelectedPrimaryMaterial()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        crafter.SetSkill(SkillType.Tailoring, 0);
        var recipe = CreateRecipe(difficulty: 300);
        var iron = AddMaterial(world, pack, "iron ingots", 0x0000, 10);
        var valorite = AddMaterial(world, pack, "valorite ingots", 0x08AB, 10);

        Assert.Null(engine.TryCraft(crafter, recipe, 0x08AB));
        Assert.Equal(10, iron.Amount);
        Assert.InRange((int)valorite.Amount, 6, 10);
    }

    [Fact]
    public void CraftRecipeButton_WithSeveralHues_OpensMaterialGumpAndAcceptsChoice()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var engine = new CraftingEngine(world);
        var crafter = world.CreateCharacter();
        crafter.IsPlayer = true;
        crafter.SetSkill(SkillType.Tailoring, 2000);
        world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        crafter.Equip(pack, Layer.Pack);
        AddMaterial(world, pack, "iron ingots", 0x0000, 10);
        AddMaterial(world, pack, "valorite ingots", 0x08AB, 10);
        var recipe = CreateRecipe();
        engine.RegisterRecipe(recipe);

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        client.SetEngines(craftingEngine: engine);
        TestHarness.AttachCharacter(client, crafter);

        client.OpenCraftingGump(SkillType.Tailoring);
        uint recipeGump = Assert.Single(client.Gumps.ActiveGumps);
        client.HandleGumpResponse(crafter.Uid.Value, recipeGump, 100, [], []);

        uint materialGump = Assert.Single(client.Gumps.ActiveGumps);
        Assert.NotEqual(recipeGump, materialGump);
        Assert.True(client.Gumps.Callbacks.ContainsKey(materialGump));

        client.HandleGumpResponse(crafter.Uid.Value, materialGump, 101, [], []);
        Assert.Empty(client.Gumps.ActiveGumps);
        Assert.False(client.BeginPendingCraft(recipe, SkillType.Tailoring, reopenGump: false));
        client.CancelPendingCraftOnInterrupt();
    }

    private static (GameWorld World, CraftingEngine Engine, Character Crafter, Item Pack)
        CreateCrafter()
    {
        var world = TestHarness.CreateWorld();
        var engine = new CraftingEngine(world);
        var crafter = world.CreateCharacter();
        crafter.IsPlayer = true;
        crafter.SetSkill(SkillType.Tailoring, 2000);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        crafter.Equip(pack, Layer.Pack);
        return (world, engine, crafter, pack);
    }

    private static CraftRecipe CreateRecipe(int difficulty = 0)
    {
        var recipe = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            ResultName = "colored sword",
            PrimarySkill = SkillType.Tailoring,
            Difficulty = difficulty,
        };
        recipe.Resources.Add(new CraftResource { Type = ItemType.Ingot, Amount = 5 });
        return recipe;
    }

    private static Item AddMaterial(GameWorld world, Item pack, string name, ushort hue, int amount)
    {
        var material = world.CreateItem();
        material.BaseId = 0x1BF2;
        material.ItemType = ItemType.Ingot;
        material.Name = name;
        material.Hue = new Color(hue);
        material.Amount = (ushort)amount;
        pack.AddItem(material);
        return material;
    }
}
