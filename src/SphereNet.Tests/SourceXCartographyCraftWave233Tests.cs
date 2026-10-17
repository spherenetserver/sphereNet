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

    /// <summary>A crafted item's @Create runs with its maker as SRC
    /// (CreateTemplate(id, nullptr, this), CCharSkill.cpp:679): a drawn map reads
    /// &lt;SRC.P&gt; for its area, and without a SRC every map came out blank.</summary>
    [Fact]
    public void CraftedItem_CreateRunsWithTheMakerAsSrc()
    {
        var (_, engine, crafter, _, _) = CreateCrafter();
        var recipe = Assert.Single(engine.GetRecipesBySkill(SkillType.Cartography));
        Character? seen = null;
        int plain = 0;
        Item.CreateTriggerSourceHook = (_, src) => seen = src;
        Item.CreateTriggerHook = _ => plain++;

        Assert.NotNull(engine.TryCraft(crafter, recipe));

        Assert.Same(crafter, seen);
        Assert.Equal(0, plain);
    }

    /// <summary>A blank map has no use of its own (it is not in the double-click
    /// list, CClientUse.cpp:461), and a map with no area says DEFMSG_MAP_IS_BLANK.</summary>
    [Fact]
    public void BlankMap_DoubleClick_CannotThinkOfAWay()
    {
        var (world, _, crafter, blankMap, _) = CreateCrafter();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(loggerFactory, 234);
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, crafter);
        TestHarness.ClearQueuedPackets(state);

        client.HandleDoubleClick(blankMap.Uid.Value);

        string sent = string.Concat(TestHarness.GetQueuedPackets(state).Select(p =>
            System.Text.Encoding.BigEndianUnicode.GetString(p.Span.ToArray()) +
            System.Text.Encoding.ASCII.GetString(p.Span.ToArray())));
        Assert.Contains(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.ItemuseCantthink), sent);
        Assert.DoesNotContain(TestHarness.GetQueuedPackets(state), p => p.Span.Length > 0 && p.Span[0] is 0x90 or 0xF5);
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
            blankMap.SetTag("ITEMDEF", "i_map_blank");   // made from the named definition
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
