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

/// <summary>
/// The colour of a craft's material plays no part in Source-X. A resource matches by
/// its definition alone (CItem::IsResourceMatch, CItem.cpp:6027), ContentConsume
/// takes whatever matching piles it meets (CContainer.cpp:418), and
/// Skill_MakeItem_Success never colours what it makes (CCharSkill.cpp:674-865) - a
/// pack colours a result from @SkillMakeItem through ACT. The old material picker,
/// the per-colour bill and the inherited hue were SphereNet's own.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCraftMaterialWave230Tests
{
    [Fact]
    public void MixedColours_PayTheBillTogether()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        var iron = AddMaterial(world, pack, "iron ingots", 0x0000, 3);
        var valorite = AddMaterial(world, pack, "valorite ingots", 0x08AB, 3);

        Assert.True(engine.CanCraft(crafter, recipe));
        var result = engine.TryCraft(crafter, recipe);

        Assert.NotNull(result);
        Assert.Equal(1, Remaining(iron) + Remaining(valorite));
    }

    [Fact]
    public void TheResult_TakesNoColourFromItsMaterial()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        AddMaterial(world, pack, "valorite ingots", 0x08AB, 10);

        var result = engine.TryCraft(crafter, recipe);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Hue.Value);
    }

    [Fact]
    public void TooLittleOfEveryColourTogether_CannotBeCrafted()
    {
        var (world, engine, crafter, pack) = CreateCrafter();
        var recipe = CreateRecipe();
        var iron = AddMaterial(world, pack, "iron ingots", 0x0000, 2);
        var valorite = AddMaterial(world, pack, "valorite ingots", 0x08AB, 2);

        Assert.False(engine.CanCraft(crafter, recipe));
        Assert.Null(engine.TryCraft(crafter, recipe));
        Assert.Equal(2, iron.Amount);
        Assert.Equal(2, valorite.Amount);
    }

    [Fact]
    public void CraftRecipeButton_StartsTheCraftWithoutAMaterialPicker()
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

        // The craft is running: no second gump, and a second start is refused.
        Assert.Empty(client.Gumps.ActiveGumps);
        Assert.False(client.BeginPendingCraft(recipe, SkillType.Tailoring, reopenGump: false));
        client.CancelPendingCraftOnInterrupt();
    }

    private static int Remaining(Item item) => item.IsDeleted ? 0 : item.Amount;

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
            ResultName = "sword",
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
