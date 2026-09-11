using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a failed craft still costs, and the ACTIONEFFECT a script sets to say so
/// (port plan İŞ-25 / PLAN-404).
///
/// The reference works the percent out from THREE sources in order
/// (Skill_MakeItem SKTRIG_FAIL, CCharSkill.cpp:928-943): ACTIONEFFECT when a script
/// set one on this attempt, otherwise the crafting skill's own EFFECT curve, and only
/// with neither a flat 0-49% roll. This engine had the flat roll and nothing else, so
/// a pack that tunes the loss - the live one gives Inscription EFFECT=50 - was ignored.
///
/// ACTIONEFFECT itself did not exist here at all, although the live pack prints it in
/// its player-info dialog and writes it back through an INPDLG
/// (dialogs/sphere_dialogs_prop.scp:603/1036).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftFailureCostParityTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_craft_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private const ushort OreId = 0x19B9;
    private const ushort IngotId = 0x1BF2;

    private (GameWorld World, CraftingEngine Engine, Character Crafter, Item Ore, CraftRecipe Recipe)
        Setup(string? skillEffect = null)
    {
        var resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
        File.WriteAllText(_defFile, $"""
            [SKILL 7]
            DEFNAME=Skill_Blacksmithing
            KEY=Blacksmithing
            {(skillEffect != null ? "EFFECT=" + skillEffect : "")}
            """);
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var crafter = world.CreateCharacter();
        crafter.IsPlayer = true;
        crafter.PrivLevel = PrivLevel.Player;
        crafter.MaxHits = 100; crafter.Hits = 100;
        crafter.SetSkill(SkillType.Blacksmithing, 0);   // always fails
        world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        crafter.Backpack = pack;
        crafter.Equip(pack, Layer.Pack);

        var ore = world.CreateItem();
        ore.BaseId = OreId;
        ore.Amount = 1000;
        Assert.True(pack.TryAddItem(ore));

        // Blacksmithing needs a forge within two tiles or CanCraft refuses before
        // anything is spent - which would make every assertion below pass vacuously.
        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        forge.BaseId = 0x0FB1;
        Assert.True(world.PlaceItem(forge, new Point3D(101, 100, 0, 0)));

        var recipe = new CraftRecipe
        {
            ResultDefId = IngotId,
            ResultItemId = IngotId,
            ResultName = "ingot",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 1000,                          // never succeeds
        };
        recipe.Resources.Add(new CraftResource { ItemId = OreId, Amount = 100 });

        return (world, new CraftingEngine(world), crafter, ore, recipe);
    }

    // ---- ACTIONEFFECT itself ------------------------------------------------

    [Fact]
    public void ActionEffectReadsAndWrites()
    {
        var (_, _, crafter, _, _) = Setup();

        Assert.True(crafter.TryGetProperty("ACTIONEFFECT", out string? initial));
        Assert.Equal("-1", initial);                    // "no override" is the default

        Assert.True(crafter.TrySetProperty("ACTIONEFFECT", "42"));
        Assert.True(crafter.TryGetProperty("ACTIONEFFECT", out string? set));
        Assert.Equal("42", set);

        // Any negative write normalises to -1 (CChar.cpp:3730).
        Assert.True(crafter.TrySetProperty("ACTIONEFFECT", "-7"));
        Assert.True(crafter.TryGetProperty("ACTIONEFFECT", out string? cleared));
        Assert.Equal("-1", cleared);
    }

    [Fact]
    public void StartingASkillClearsTheOverride()
    {
        // One attempt must not steer the next: the reference resets it at every
        // skill start and cleanup (CCharSkill.cpp:602/4456).
        var (_, _, crafter, _, _) = Setup();
        crafter.ActionEffect = 90;

        crafter.BeginSkillPending((int)SkillType.Blacksmithing, 0, 0, Serial.Invalid, null);
        Assert.Equal(-1, crafter.ActionEffect);

        crafter.ActionEffect = 90;
        crafter.ClearActiveSkillPending();
        Assert.Equal(-1, crafter.ActionEffect);
    }

    // ---- the failed-craft bill ----------------------------------------------

    [Fact]
    public void ActionEffectDecidesHowMuchAFailedCraftKeeps()
    {
        var (_, engine, crafter, ore, recipe) = Setup();
        crafter.ActionEffect = 25;                      // exactly a quarter

        Assert.Null(engine.TryCraft(crafter, recipe));  // difficulty 1000: always fails

        Assert.Equal(1000 - 25, ore.Amount);            // 25% of the 100-ore bill
    }

    [Fact]
    public void AnActionEffectOfZeroCostsNothing()
    {
        // Zero is a real answer, not "unset": the reference only falls through on -1.
        var (_, engine, crafter, ore, recipe) = Setup();
        crafter.ActionEffect = 0;

        Assert.Null(engine.TryCraft(crafter, recipe));

        Assert.Equal(1000, ore.Amount);
    }

    [Fact]
    public void TheSkillsEffectCurveDecidesWhenNoScriptDid()
    {
        // The live pack tunes Inscription with EFFECT=50; a flat curve makes the
        // loss exact, so the assertion does not have to chase a distribution.
        var (_, engine, crafter, ore, recipe) = Setup(skillEffect: "50");

        Assert.Null(engine.TryCraft(crafter, recipe));

        Assert.Equal(1000 - 50, ore.Amount);            // 50% of 100
    }

    [Fact]
    public void WithoutEitherTheFlatRollStillApplies()
    {
        var (_, engine, crafter, ore, recipe) = Setup();

        Assert.Null(engine.TryCraft(crafter, recipe));

        // 0-49% of a 100-ore bill: something between nothing and half.
        int spent = 1000 - ore.Amount;
        Assert.InRange(spent, 0, 49);
    }
}
