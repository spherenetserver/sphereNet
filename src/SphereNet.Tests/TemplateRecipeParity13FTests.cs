using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Running a [TEMPLATE] recipe (review 13F).
///
/// Source-X reads a recipe line by line in CItem::ReadTemplate (CItem.cpp:600). A
/// CONTAINER row becomes the container the following rows are created in (:631); an
/// ITEM row is created in whichever container is current (:642); anything else is
/// applied with r_LoadVal to the item the recipe most recently created (:686). Each
/// create row goes through CreateHeader (:461), which rolls its R# chance, evaluates
/// its amount, refuses the row when that amount is zero, and takes a nested TEMPLATE
/// as readily as an ITEMDEF. What the caller gets back is the first container the
/// recipe opened, or its last item when it opened none (:691). Numbers in a resource
/// position follow the Sphere rule - only a leading zero is hexadecimal
/// (ResourceGetID_EatStr, CResourceHolder.cpp:102).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TemplateRecipeParity13FTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Defs = """
        [ITEMDEF 01000]
        DEFNAME=i_base_token
        NAME=Base token
        TYPE=t_normal
        VALUE=1

        [ITEMDEF 04096]
        DEFNAME=i_wrong_token
        NAME=Wrong token
        TYPE=t_normal
        VALUE=1

        [ITEMDEF 0e75]
        DEFNAME=i_recipe_box
        NAME=Base box
        TYPE=t_container

        [TEMPLATE 010001]
        DEFNAME=t_plain
        CONTAINER=i_recipe_box
        ITEM=i_base_token

        [TEMPLATE 010002]
        DEFNAME=t_amount
        CONTAINER=i_recipe_box
        ITEM=i_base_token,7

        [TEMPLATE 010003]
        DEFNAME=t_zero
        CONTAINER=i_recipe_box
        ITEM=i_base_token,0

        [TEMPLATE 010004]
        DEFNAME=t_props
        CONTAINER=i_recipe_box
        NAME=Outer recipe
        ITEM=i_base_token
        NAME=Gift recipe
        COLOR=0456
        TAG.RECIPE=37

        [TEMPLATE 010005]
        DEFNAME=t_nested_box
        CONTAINER=i_recipe_box
        CONTAINER=i_recipe_box
        ITEM=i_base_token

        [TEMPLATE 010006]
        DEFNAME=t_inner
        CONTAINER=i_recipe_box
        ITEM=i_base_token

        [TEMPLATE 010007]
        DEFNAME=t_outer_ref
        CONTAINER=i_recipe_box
        ITEM=t_inner

        [TEMPLATE 010008]
        DEFNAME=t_decimal
        CONTAINER=i_recipe_box
        ITEM=4096

        [TEMPLATE 010009]
        DEFNAME=t_spherehex
        CONTAINER=i_recipe_box
        ITEM=01000

        [TEMPLATE 01000A]
        DEFNAME=t_rootamount
        ITEM=i_base_token,7
        """;

    public TemplateRecipeParity13FTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_13f_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose() => File.Delete(_scriptPath);

    /// <summary>Load the fixture and make a world. This has to run INSIDE the test
    /// body, not in the constructor: ResetEngineStatics clears the definition tables
    /// in its Before hook, which xUnit runs after the class is constructed.</summary>
    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return TestHarness.CreateWorld();
    }

    private Item? Build(GameWorld world, string defname)
    {
        var rid = _resources.ResolveDefName(defname);
        Assert.True(rid.IsValid, $"{defname} did not resolve");
        Assert.Equal(ResType.Template, rid.Type);
        return TemplateEngine.BuildTemplate(world, rid.Index);
    }

    // ============================================================ control

    [Fact]
    public void APlainRecipeMakesItsBoxAndItsContents()
    {
        var world = NewWorld();
        var box = Build(world, "t_plain");

        Assert.NotNull(box);
        var child = Assert.Single(box!.Contents);
        Assert.Equal("Base token", child.Name);
    }

    // ============================================================ 13F-1

    [Fact]
    public void ARecipeAmountReachesTheItemItWasWrittenFor()
    {
        var world = NewWorld();
        var box = Build(world, "t_amount");

        var child = Assert.Single(box!.Contents);
        Assert.Equal(7, child.Amount);
    }

    [Fact]
    public void AZeroAmountSwitchesTheRowOff()
    {
        var world = NewWorld();
        var box = Build(world, "t_zero");

        Assert.NotNull(box);
        Assert.Empty(box!.Contents);   // the row is disabled, not defaulted to one
    }

    [Fact]
    public void ARootItemRowKeepsItsAmountToo()
    {
        var world = NewWorld();
        var item = Build(world, "t_rootamount");

        Assert.NotNull(item);
        Assert.Equal(7, item!.Amount);
    }

    // ============================================================ 13F-2

    [Fact]
    public void PropertyLinesApplyToWhateverTheRecipeCreatedLast()
    {
        var world = NewWorld();
        var box = Build(world, "t_props");

        Assert.NotNull(box);
        // The NAME before any ITEM belongs to the container.
        Assert.Equal("Outer recipe", box!.Name);

        var child = Assert.Single(box.Contents);
        Assert.Equal("Gift recipe", child.Name);
        Assert.Equal(0x456, child.Hue.Value);
        Assert.True(child.TryGetProperty("TAG.RECIPE", out string tag));
        Assert.Equal("37", tag);

        // ...and the child's settings did not leak back onto the box.
        Assert.NotEqual(0x456, box.Hue.Value);
    }

    // ============================================================ 13F-3

    [Fact]
    public void ASecondContainerRowBecomesTheDestinationForWhatFollows()
    {
        var world = NewWorld();
        var outer = Build(world, "t_nested_box");

        Assert.NotNull(outer);
        // The outer box holds ONLY the inner box...
        var inner = Assert.Single(outer!.Contents);
        Assert.Equal(ItemType.Container, inner.ItemType);
        // ...and the token is inside THAT, not beside it.
        var token = Assert.Single(inner.Contents);
        Assert.Equal("Base token", token.Name);
    }

    // ============================================================ 13F-4

    [Fact]
    public void AnItemRowMayNameAnotherRecipe()
    {
        var world = NewWorld();
        var outer = Build(world, "t_outer_ref");

        Assert.NotNull(outer);
        var innerBox = Assert.Single(outer!.Contents);
        Assert.Equal(ItemType.Container, innerBox.ItemType);
        Assert.Equal("Base token", Assert.Single(innerBox.Contents).Name);
    }

    [Fact]
    public void ARecipeReferencedDirectlyStillBuildsOnItsOwn()
    {
        var world = NewWorld();
        var box = Build(world, "t_inner");

        Assert.NotNull(box);
        Assert.Equal("Base token", Assert.Single(box!.Contents).Name);
    }

    // ============================================================ 13F-5

    [Fact]
    public void ADecimalResourceNumberIsReadAsDecimal()
    {
        var world = NewWorld();
        var box = Build(world, "t_decimal");

        // 4096 decimal is 0x1000 - the BASE token, not the 0x4096 one.
        var child = Assert.Single(box!.Contents);
        Assert.Equal("Base token", child.Name);
    }

    [Fact]
    public void ALeadingZeroKeepsTheSphereHexMeaning()
    {
        var world = NewWorld();
        var box = Build(world, "t_spherehex");

        var child = Assert.Single(box!.Contents);
        Assert.Equal("Base token", child.Name);
    }

    [Theory]
    [InlineData("4096", 4096)]     // decimal
    [InlineData("01000", 0x1000)]  // Sphere hex
    [InlineData("0x1000", 0x1000)]
    public void ANumericResourceTokenResolvesInTheRightBase(string token, int expected)
    {
        NewWorld();
        Assert.True(TemplateEngine.TryResolveTemplateResource(token, out ResourceId rid));
        Assert.Equal(expected, rid.Index);
    }
}
