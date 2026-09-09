using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// CANMAKE and CANMAKESKILL: can this character make that (port plan İŞ-13 /
/// PLAN-304).
///
/// Upstream answers both from Skill_MakeItem at its SELECT stage (CChar.cpp:2787).
/// CANMAKE asks the whole question - the SKILLMAKE requirements AND whether the
/// materials are to hand - while CANMAKESKILL passes fSkillOnly and asks about the
/// skill side alone (CCharSkill.cpp:913). Neither existed here, and the script packs
/// on hand use them eight times between them.
///
/// The work site is deliberately NOT part of the answer: upstream checks the forge in
/// the smithing stage, not in Skill_MakeItem's SELECT, so a smith away from the forge
/// still answers "yes, I know how and I have the iron".
/// </summary>
public sealed class CanMakeQueryParityTests : IDisposable
{
    private const ushort DaggerId = 0x0F51;
    private const ushort IngotId = 0x1BF2;

    public void Dispose() => Character.OnCanMakeCheck = null;

    private static (GameWorld World, Character Smith, CraftingEngine Engine) Bench()
    {
        var world = TestHarness.CreateWorld();
        var smith = world.CreateCharacter();
        smith.Name = "Smith";
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        smith.Equip(pack, Layer.Pack);
        smith.Backpack = pack;

        var engine = new CraftingEngine(world);
        var recipe = new CraftRecipe
        {
            ResultDefId = DaggerId,
            ResultItemId = DaggerId,
            PrimarySkill = SkillType.Blacksmithing,
        };
        recipe.SkillRequirements.Add((SkillType.Blacksmithing, 300));
        recipe.Resources.Add(new CraftResource { ItemId = IngotId, Amount = 3 });
        engine.RegisterRecipe(recipe);

        // The host's wiring, reproduced: a query never asks about the work site.
        Character.OnCanMakeCheck = (ch, itemId, skillOnly) =>
        {
            var found = engine.TryGetRecipe(itemId);
            return found != null && engine.CanCraft(ch, found, primaryResourceHue: null,
                skillOnly: skillOnly, checkWorkSite: false);
        };
        return (world, smith, engine);
    }

    private static void GiveIngots(GameWorld world, Character smith, ushort amount)
    {
        var ingots = world.CreateItem();
        ingots.BaseId = IngotId;
        ingots.Amount = amount;
        smith.Backpack!.AddItem(ingots);
    }

    private static string Ask(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out string? v));
        return v!;
    }

    [Fact]
    public void WithoutTheSkillTheAnswerIsNo()
    {
        var (world, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 100);
        GiveIngots(world, smith, 10);

        Assert.Equal("0", Ask(smith, "CANMAKE.0F51"));
        Assert.Equal("0", Ask(smith, "CANMAKESKILL.0F51"));
    }

    [Fact]
    public void WithTheSkillButNoMaterialsOnlyTheSkillQuestionSaysYes()
    {
        var (_, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 500);

        // This is the whole point of the two keys: one asks "do I know how", the other
        // "can I do it right now".
        Assert.Equal("1", Ask(smith, "CANMAKESKILL.0F51"));
        Assert.Equal("0", Ask(smith, "CANMAKE.0F51"));
    }

    [Fact]
    public void WithBothTheAnswerIsYes()
    {
        var (world, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 500);
        GiveIngots(world, smith, 3);

        Assert.Equal("1", Ask(smith, "CANMAKE.0F51"));
        Assert.Equal("1", Ask(smith, "CANMAKESKILL.0F51"));
    }

    [Fact]
    public void NotQuiteEnoughMaterialIsStillNo()
    {
        var (world, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 500);
        GiveIngots(world, smith, 2);        // the recipe wants three

        Assert.Equal("0", Ask(smith, "CANMAKE.0F51"));
    }

    [Fact]
    public void AnItemNobodyHasARecipeForAnswersNo()
    {
        var (_, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 1000);

        Assert.Equal("0", Ask(smith, "CANMAKE.0E75"));
    }

    [Fact]
    public void TheKeyIsAcceptedWithASpaceToo()
    {
        // Upstream skips the separator without caring which one it is.
        var (world, smith, _) = Bench();
        smith.SetSkill(SkillType.Blacksmithing, 500);
        GiveIngots(world, smith, 3);

        Assert.Equal("1", Ask(smith, "CANMAKE 0F51"));
        Assert.Equal("1", Ask(smith, "CANMAKESKILL 0F51"));
    }
}
