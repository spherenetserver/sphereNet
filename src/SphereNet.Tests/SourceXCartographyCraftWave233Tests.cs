using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCartographyCraftWave233Tests
{
    private const string CartographyScript = """
        [TYPEDEFS]
        t_map=73
        t_map_blank=125
        t_cartography=195

        [ITEMDEF i_map_blank]
        ID=014EB
        NAME=blank map
        TYPE=t_map_blank

        [ITEMDEF i_map_local]
        ID=014EC
        NAME=local map
        TYPE=t_map
        RESOURCES=1 i_map_blank
        SKILLMAKE=Cartography 0.0,t_cartography
        """;

    [Fact]
    public void MapDefinition_LoadsAsCartographyRecipe_WithToolAndBlankMap()
    {
        var (_, engine, _, _, _) = CreateCrafter();

        var recipe = Assert.Single(engine.GetRecipesBySkill(SkillType.Cartography));

        Assert.Equal((ushort)0x14EC, recipe.ResultItemId);
        Assert.Equal(SkillType.Cartography, recipe.PrimarySkill);
        Assert.Contains(ItemType.CartographyTool, recipe.RequiredToolTypes);
        var resource = Assert.Single(recipe.Resources);
        Assert.Equal((ushort)0x14EB, resource.ItemId);
        Assert.Equal(1, resource.Amount);
    }

    [Fact]
    public void CartographyTool_DoubleClick_OpensMapRecipeAndStartsCraft()
    {
        var (world, engine, crafter, _, tool) = CreateCrafter();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(loggerFactory, 233);
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        client.SetEngines(craftingEngine: engine);
        TestHarness.AttachCharacter(client, crafter);

        client.HandleDoubleClick(tool.Uid.Value);

        uint gump = Assert.Single(client.Gumps.ActiveGumps);
        client.HandleGumpResponse(crafter.Uid.Value, gump, 100, [], []);
        var recipe = Assert.Single(engine.GetRecipesBySkill(SkillType.Cartography));
        Assert.False(client.BeginPendingCraft(recipe, SkillType.Cartography, reopenGump: false));
        client.CancelPendingCraftOnInterrupt();
    }

    [Fact]
    public void CartographyRecipe_ConsumesBlankMap_AndCreatesMap()
    {
        var (world, engine, crafter, blankMap, _) = CreateCrafter();
        var recipe = Assert.Single(engine.GetRecipesBySkill(SkillType.Cartography));

        var result = engine.TryCraft(crafter, recipe);

        Assert.NotNull(result);
        Assert.Equal(ItemType.Map, result!.ItemType);
        Assert.Equal((ushort)0x14EC, result.DispIdFull);
        Assert.Equal(crafter.Uid, result.Crafter);
        Assert.True(blankMap.IsDeleted);
        Assert.DoesNotContain(blankMap, crafter.Backpack!.Contents);
    }

    [Fact]
    public void CartographyCraftStroke_UsesSourceXDrawingSound()
    {
        var (_, sound) = ClientWorldFeaturesHandler.GetCraftAnimAndSound(SkillType.Cartography);

        Assert.Equal((ushort)0x0249, sound);
    }

    private static (GameWorld World, CraftingEngine Engine, Character Crafter,
        Item BlankMap, Item Tool) CreateCrafter()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_cartography_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, CartographyScript);
        try
        {
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(path) ?? ""
            };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 6144, 4096);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;
            var engine = new CraftingEngine(world);
            Assert.Equal(1, engine.LoadRecipesFromDefs(resources));

            var crafter = world.CreateCharacter();
            crafter.IsPlayer = true;
            crafter.SetSkill(SkillType.Cartography, 2000);
            world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            crafter.Equip(pack, Layer.Pack);

            var blankMap = world.CreateItem();
            blankMap.BaseId = 0x14EB;
            blankMap.ItemType = ItemType.MapBlank;
            pack.AddItem(blankMap);

            var tool = world.CreateItem();
            tool.BaseId = 0x0FBF;
            tool.ItemType = ItemType.CartographyTool;
            tool.HitsCur = tool.HitsMax = 10;
            pack.AddItem(tool);

            return (world, engine, crafter, blankMap, tool);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
