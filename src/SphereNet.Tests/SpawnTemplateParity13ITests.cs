using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A spawner and SERV.NEWITEM build a recipe the same way (review 13I).
///
/// Source-X CCSpawn::GenerateItem (CCSpawn.cpp:323) calls CItem::CreateTemplate on the
/// resolved id and works with what comes back: the recipe is RUN - amounts, switched-off
/// rows, property lines and nested recipes included - and only then does PILE apply, the
/// spawn attributes get stripped and @Spawn run (:329/:340/:346). @PreSpawn may change
/// the target first (:311), and everything after it is built from THAT id.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnTemplateParity13ITests
{
    private const string Script = """
        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_13i
        TYPE=t_spawn_item

        [ITEMDEF 01000]
        DEFNAME=i_a_13i
        NAME=A

        [ITEMDEF 01001]
        DEFNAME=i_b_13i
        NAME=B

        [ITEMDEF 0e75]
        DEFNAME=i_box_13i
        NAME=Base box
        TYPE=t_container

        [ITEMDEF 0e76]
        DEFNAME=i_otherbox_13i
        NAME=Other box
        TYPE=t_container

        [TEMPLATE 010001]
        DEFNAME=t_amount_13i
        ITEM=i_a_13i,7

        [TEMPLATE 010002]
        DEFNAME=t_named_13i
        ITEM=i_a_13i
        NAME=Recipe name

        [TEMPLATE 010003]
        DEFNAME=t_zero_13i
        CONTAINER=i_box_13i,0
        ITEM=i_a_13i,0

        [TEMPLATE 010004]
        DEFNAME=t_inner_13i
        CONTAINER=i_box_13i
        ITEM=i_a_13i

        [TEMPLATE 010005]
        DEFNAME=t_outer_ref_13i
        ITEM=t_inner_13i

        [TEMPLATE 010006]
        DEFNAME=t_box_named_13i
        CONTAINER=i_box_13i
        NAME=Recipe box
        ITEM=i_a_13i

        [TEMPLATE 010007]
        DEFNAME=t_other_13i
        CONTAINER=i_otherbox_13i
        ITEM=i_b_13i
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_13i_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ResourceHolder res, string target)
    {
        var stone = world.CreateItem();
        stone.BaseId = 0x1F14;
        stone.ItemType = ItemType.SpawnItem;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", target);
        stone.Amount = 1;
        stone.InitializeSpawnComponent(world, res);
        stone.SpawnItem!.SetFromDefName(target, res);
        return stone;
    }

    private static Item? SpawnOnce(GameWorld world, Item stone)
    {
        stone.SpawnItem!.RespawnNow();
        return stone.SpawnItem.CurrentCount == 0
            ? null
            : world.FindItem(stone.SpawnItem.SpawnedUids[0]);
    }

    // ================================================================ 13I-1

    [Fact]
    public void TheSpawnerRunsTheRootRowsAmount()
    {
        var res = LoadResources();
        var world = NewWorld();
        var made = SpawnOnce(world, Spawner(world, res, "t_amount_13i"));

        Assert.NotNull(made);
        Assert.Equal(7, made!.Amount);
    }

    [Fact]
    public void TheSpawnerAppliesTheRecipesPropertyLines()
    {
        var res = LoadResources();
        var world = NewWorld();
        var made = SpawnOnce(world, Spawner(world, res, "t_named_13i"));

        Assert.NotNull(made);
        Assert.Equal("Recipe name", made!.Name);
    }

    [Fact]
    public void ARecipeWhoseRowsAreAllSwitchedOffSpawnsNothing()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, "t_zero_13i");

        Assert.Null(SpawnOnce(world, stone));
        Assert.Equal(0, stone.SpawnItem!.CurrentCount);
    }

    // ================================================================ 13I-2

    [Fact]
    public void ARecipeWhoseFirstRowNamesAnotherRecipeStillSpawns()
    {
        var res = LoadResources();
        var world = NewWorld();
        var made = SpawnOnce(world, Spawner(world, res, "t_outer_ref_13i"));

        Assert.NotNull(made);
        Assert.Equal(ItemType.Container, made!.ItemType);
        Assert.Equal("A", Assert.Single(made.Contents).Name);
    }

    // ================================================================ 13I-3

    [Fact]
    public void TheRecipeIsFinishedBeforeTheSpawnTriggerRuns()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, "t_box_named_13i");

        int seenContents = -1;
        string seenName = "";
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.Spawn && args.SpawnedItem is { } spawned)
            {
                seenContents = spawned.ContentCount;
                seenName = spawned.Name;
                spawned.Name = "Hook name";   // the trigger has the last word
            }
            return TriggerResult.Default;
        };
        try
        {
            var made = SpawnOnce(world, stone);

            Assert.NotNull(made);
            // @Spawn saw the finished object, not an empty box with its base name.
            Assert.Equal(1, seenContents);
            Assert.Equal("Recipe box", seenName);
            // ...and what the trigger wrote afterwards was not overwritten.
            Assert.Equal("Hook name", made!.Name);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }
    }

    // ================================================================ 13I-4

    [Fact]
    public void RetargetingInPreSpawnBuildsTheWholeObjectFromTheNewRecipe()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, "t_inner_13i");
        int otherRecipe = res.ResolveDefName("t_other_13i").Index;

        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.PreSpawn)
                args.SpawnDefIndex = otherRecipe;
            return TriggerResult.Default;
        };
        try
        {
            var made = SpawnOnce(world, stone);

            Assert.NotNull(made);
            // Root AND contents come from the recipe the trigger chose - the container
            // used to be the new one while its contents stayed the old recipe's.
            Assert.Equal(res.ResolveDefName("i_otherbox_13i").Index, made!.BaseId);
            Assert.Equal("B", Assert.Single(made.Contents).Name);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }
    }

    // ================================================================ control

    [Fact]
    public void APlainItemTargetIsUnaffected()
    {
        var res = LoadResources();
        var world = NewWorld();
        var made = SpawnOnce(world, Spawner(world, res, "i_a_13i"));

        Assert.NotNull(made);
        Assert.Equal("A", made!.Name);
        Assert.Equal(1, made.Amount);
    }

    [Fact]
    public void ATemplateContainerStillCarriesItsContents()
    {
        var res = LoadResources();
        var world = NewWorld();
        var made = SpawnOnce(world, Spawner(world, res, "t_inner_13i"));

        Assert.NotNull(made);
        Assert.Equal(ItemType.Container, made!.ItemType);
        Assert.Equal("A", Assert.Single(made.Contents).Name);
    }
}
