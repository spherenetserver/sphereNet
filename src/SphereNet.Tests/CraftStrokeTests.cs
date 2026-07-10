using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Multi-stroke crafting (reference Skill_MakeItem → Skill_Stroke): the
/// craft resolves after two timed work strokes, a second craft cannot start
/// while one is pending, and walking away from the forge mid-craft fails
/// the completion re-check.
/// </summary>
public class CraftStrokeTests
{
    private static (GameWorld world, GameClient client, Character ch, Item pack) CreateCrafter()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.SetSkill(SkillType.Blacksmithing, 2000);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        client.SetEngines(craftingEngine: new CraftingEngine(world));
        TestHarness.AttachCharacter(client, ch);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = 20;
        pack.AddItem(ingots);
        return (world, client, ch, pack);
    }

    private static CraftRecipe SwordRecipe()
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

    private static bool PackHasSword(Item pack)
    {
        foreach (var item in pack.Contents)
            if (item.BaseId == 0x13B9 && !item.IsDeleted) return true;
        return false;
    }

    private static void TickUntil(GameClient client, Func<bool> done, int maxMs = 4000)
    {
        long start = Environment.TickCount64;
        while (!done() && Environment.TickCount64 - start < maxMs)
        {
            client.TickPendingCraft();
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void Craft_ResolvesAfterStrokes_NotImmediately()
    {
        var (_, client, _, pack) = CreateCrafter();
        var recipe = SwordRecipe();

        Assert.True(client.BeginPendingCraft(recipe, SkillType.Blacksmithing, reopenGump: false));
        Assert.False(PackHasSword(pack)); // strokes pending — no instant result

        // A second craft cannot start while one is pending.
        Assert.False(client.BeginPendingCraft(recipe, SkillType.Blacksmithing, reopenGump: false));

        TickUntil(client, () => PackHasSword(pack));
        Assert.True(PackHasSword(pack));
    }

    [Fact]
    public void Craft_WalkingAwayFromForge_FailsAtCompletion()
    {
        var (world, client, ch, pack) = CreateCrafter();
        var recipe = SwordRecipe();

        Assert.True(client.BeginPendingCraft(recipe, SkillType.Blacksmithing, reopenGump: false));

        // Leave the work site before the strokes finish.
        world.MoveCharacter(ch, new Point3D(120, 100, 0, 0));

        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 3500)
        {
            client.TickPendingCraft();
            Thread.Sleep(50);
        }

        Assert.False(PackHasSword(pack));
        // Completion re-check failed before the roll: no resources were consumed.
        int ingots = 0;
        foreach (var item in pack.Contents)
            if (item.BaseId == 0x1BF2) ingots += item.Amount;
        Assert.Equal(20, ingots);
    }

    [Fact]
    public void Craft_TakingDamage_CancelsPendingStrokeLoop()
    {
        var (_, client, ch, _) = CreateCrafter();
        var recipe = SwordRecipe();
        ch.MaxHits = 100;
        ch.Hits = 100;
        Character.OnDamageActionInterrupt = _ => client.CancelPendingCraftOnInterrupt();

        Assert.True(client.BeginPendingCraft(recipe, SkillType.Blacksmithing, reopenGump: false));
        ch.Hits = 90;

        // The original action was cancelled, so another recipe can start now.
        Assert.True(client.BeginPendingCraft(recipe, SkillType.Blacksmithing, reopenGump: false));
    }
}
