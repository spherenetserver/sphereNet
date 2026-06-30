using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X RES_TYPEDEF parity (wiki/11.txt audit #1): a recipe resource can match
// by item TYPE (any ingot), not just a specific BaseId. Previously a t_* resource
// resolved to a TypeDef and was stored as a bogus BaseId, so the recipe could
// never be crafted.
public class CraftResourceTypeTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeCrafter(GameWorld world, out Item pack)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.SetSkill(SkillType.Tailoring, 1000); // Tailoring has no work-site requirement
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    [Fact]
    public void CanCraft_TypeResource_MatchesAnyItemOfThatType()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var crafter = MakeCrafter(world, out var pack);

        var recipe = new CraftRecipe { ResultItemId = 0x13B0, PrimarySkill = SkillType.Tailoring };
        recipe.Resources.Add(new CraftResource { Type = ItemType.Ingot, Amount = 5 });

        // No ingots yet → cannot craft.
        Assert.False(engine.CanCraft(crafter, recipe));

        // A pile of ANY ingot (BaseId irrelevant, only the TYPE matters) → can craft.
        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.ItemType = ItemType.Ingot;
        ingots.Amount = 10;
        ingots.Hue = (Color)0x0021; // valorite-ish
        pack.AddItem(ingots);

        Assert.True(engine.CanCraft(crafter, recipe));
    }

    [Fact]
    public void CanCraft_TypeResource_CountsAcrossDifferentBaseIds()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var crafter = MakeCrafter(world, out var pack);

        var recipe = new CraftRecipe { ResultItemId = 0x13B0, PrimarySkill = SkillType.Tailoring };
        recipe.Resources.Add(new CraftResource { Type = ItemType.Ingot, Amount = 8 });

        // Two ingot piles with DIFFERENT base ids but the same type — a BaseId-only
        // match would miss the second pile; the type match sums both (5 + 4 = 9 ≥ 8).
        var a = world.CreateItem();
        a.BaseId = 0x1BF2; a.ItemType = ItemType.Ingot; a.Amount = 5;
        pack.AddItem(a);
        var b = world.CreateItem();
        b.BaseId = 0x1BE9; b.ItemType = ItemType.Ingot; b.Amount = 4;
        pack.AddItem(b);

        Assert.True(engine.CanCraft(crafter, recipe));
    }
}
