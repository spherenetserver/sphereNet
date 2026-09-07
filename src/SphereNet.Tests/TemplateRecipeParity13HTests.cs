using System;
using System.Collections.Generic;
using System.IO;
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
/// The boundaries of a [TEMPLATE] recipe walk (review 13H) - what happens when a row
/// FAILS, what a row that names another recipe carries, which items count as a
/// destination, and which lines are commands rather than properties.
///
/// Source-X ReadTemplate (CItem.cpp:600) keeps two references: the container rows are
/// currently filling, and the item the last create row produced. ITC_CONTAINER
/// assigns that second reference BEFORE testing it (:626), so a row that produced
/// nothing leaves no target for the property lines after it. The destination itself
/// is decided by a cast to CItemContainer (:631) - the class every container-ish TYPE
/// maps to in CreateBase (:314), corpses included (CItemCorpse.h:14). FUNC is a CALL,
/// not an assignment (:649). CreateHeader applies the row's amount to whatever it
/// made, a sub-recipe's result as well (:526), and refuses to leave an immovable
/// object inside another item (:519).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TemplateRecipeParity13HTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Defs = """
        [ITEMDEF 01000]
        DEFNAME=i_token
        NAME=Token
        TYPE=t_normal
        VALUE=1

        [ITEMDEF 0e75]
        DEFNAME=i_box
        NAME=Box
        TYPE=t_container

        [ITEMDEF 0e76]
        DEFNAME=i_locked
        NAME=Locked box
        TYPE=t_container_locked

        [ITEMDEF 02006]
        DEFNAME=i_corpse
        NAME=Corpse
        TYPE=t_corpse

        [ITEMDEF 0e7c]
        DEFNAME=i_bank
        NAME=Bank box
        TYPE=t_eq_bank_box

        [TEMPLATE 010001]
        DEFNAME=t_failed_then_prop
        CONTAINER=i_box
        ITEM=i_token
        ITEM=i_token,0
        NAME=Wrong target

        [TEMPLATE 010002]
        DEFNAME=t_missing_then_prop
        CONTAINER=i_box
        ITEM=i_token
        CONTAINER=i_not_there
        NAME=Wrong target

        [TEMPLATE 010003]
        DEFNAME=t_good_then_prop
        CONTAINER=i_box
        ITEM=i_token
        ITEM=i_token,7
        NAME=Gift

        [TEMPLATE 010004]
        DEFNAME=t_inner
        ITEM=i_token,2

        [TEMPLATE 010005]
        DEFNAME=t_outer_amount
        CONTAINER=i_box
        ITEM=t_inner,7

        [TEMPLATE 010006]
        DEFNAME=t_outer_default
        CONTAINER=i_box
        ITEM=t_inner

        [TEMPLATE 010007]
        DEFNAME=t_corpse_cont
        CONTAINER=i_corpse
        ITEM=i_token

        [TEMPLATE 010008]
        DEFNAME=t_bank_cont
        CONTAINER=i_bank
        ITEM=i_token

        [TEMPLATE 010009]
        DEFNAME=t_locked_cont
        CONTAINER=i_locked
        ITEM=i_token

        [TEMPLATE 01000A]
        DEFNAME=t_func
        CONTAINER=i_box
        ITEM=i_token
        FUNC=f_mark 37

        [TEMPLATE 01000B]
        DEFNAME=t_fixed_child
        CONTAINER=i_box
        ITEM=i_token
        """;

    public TemplateRecipeParity13HTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_13h_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose()
    {
        TemplateEngine.FunctionRowHook = null;
        Item.CreateTriggerHook = null;
        File.Delete(_scriptPath);
    }

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

    // ============================================================ 13H-1

    [Fact]
    public void APropertyAfterASwitchedOffRowDoesNotLandOnThePreviousItem()
    {
        var world = NewWorld();
        var box = Build(world, "t_failed_then_prop");

        var token = Assert.Single(box!.Contents);
        // The ",0" row produced nothing, so the NAME line after it has no target -
        // it used to rename the token created by the row BEFORE it.
        Assert.Equal("Token", token.Name);
    }

    [Fact]
    public void APropertyAfterAContainerRowThatFailedDoesNotLandOnThePreviousItem()
    {
        var world = NewWorld();
        var box = Build(world, "t_missing_then_prop");

        var token = Assert.Single(box!.Contents);
        Assert.Equal("Token", token.Name);
    }

    [Fact]
    public void APropertyAfterASuccessfulRowStillReachesIt()
    {
        var world = NewWorld();
        var box = Build(world, "t_good_then_prop");

        Assert.Equal(2, box!.Contents.Count);
        var gift = box.Contents[1];
        Assert.Equal(7, gift.Amount);
        Assert.Equal("Gift", gift.Name);
        Assert.Equal("Token", box.Contents[0].Name);
    }

    // ============================================================ 13H-2

    [Fact]
    public void TheAmountOnARowThatNamesARecipeAppliesToWhatTheRecipeMade()
    {
        var world = NewWorld();
        var box = Build(world, "t_outer_amount");

        var child = Assert.Single(box!.Contents);
        Assert.Equal(7, child.Amount);   // the outer 7, not the inner recipe's 2
    }

    [Fact]
    public void ARowThatNamesARecipeWithNoAmountKeepsTheRecipesOwn()
    {
        var world = NewWorld();
        var box = Build(world, "t_outer_default");

        var child = Assert.Single(box!.Contents);
        Assert.Equal(2, child.Amount);   // only "!= 1" overwrites (CItem.cpp:526)
    }

    // ============================================================ 13H-3

    [Theory]
    [InlineData("t_corpse_cont", ItemType.Corpse)]
    [InlineData("t_bank_cont", ItemType.EqBankBox)]
    [InlineData("t_locked_cont", ItemType.ContainerLocked)]
    public void EveryContainerTypeIsADestinationForTheRowsAfterIt(string recipe, ItemType expected)
    {
        var world = NewWorld();
        var cont = Build(world, recipe);

        Assert.NotNull(cont);
        Assert.Equal(expected, cont!.ItemType);
        Assert.Equal("Token", Assert.Single(cont.Contents).Name);
    }

    [Fact]
    public void TheContainerTestFollowsTheClassTheTypeMapsTo()
    {
        Assert.True(Item.IsContainerItemType(ItemType.Container));
        Assert.True(Item.IsContainerItemType(ItemType.ContainerLocked));
        Assert.True(Item.IsContainerItemType(ItemType.Corpse));
        Assert.True(Item.IsContainerItemType(ItemType.EqBankBox));
        Assert.True(Item.IsContainerItemType(ItemType.EqVendorBox));
        Assert.True(Item.IsContainerItemType(ItemType.Keyring));

        Assert.False(Item.IsContainerItemType(ItemType.Normal));
        Assert.False(Item.IsContainerItemType(ItemType.WeaponSword));
    }

    // ============================================================ 13H-4

    [Fact]
    public void AFuncRowCallsItsFunctionOnTheItemTheRecipeMadeLast()
    {
        var calls = new List<(string Name, string Args, string ItemName)>();
        TemplateEngine.FunctionRowHook = (item, name, args, _) =>
            calls.Add((name, args, item.Name));

        var world = NewWorld();
        var box = Build(world, "t_func");

        var call = Assert.Single(calls);
        Assert.Equal("f_mark", call.Name);
        Assert.Equal("37", call.Args);
        Assert.Equal("Token", call.ItemName);   // the token, not the box

        // ...and the row is NOT also written as a property.
        var token = Assert.Single(box!.Contents);
        Assert.False(token.TryGetProperty("FUNC", out _) &&
                     token.TryGetTag("FUNC", out _));
    }

    // ============================================================ 13H-5

    [Fact]
    public void AnObjectMadeImmovableWhileBeingCreatedNeverEntersTheContainer()
    {
        // The @Create hook stands in for a script that sets ATTR_MOVE_NEVER on the
        // object it is creating - the case CreateHeader catches at CItem.cpp:519.
        Item.CreateTriggerHook = item => item.SetAttr(ObjAttributes.Move_Never);

        var world = NewWorld();
        var box = Build(world, "t_fixed_child");

        Assert.NotNull(box);
        Assert.Empty(box!.Contents);
    }

    [Fact]
    public void AnImmovableObjectIsStillFineOnItsOwn()
    {
        var world = NewWorld();
        var loose = world.CreateItem();
        loose.BaseId = 0x1000;
        loose.SetAttr(ObjAttributes.Move_Never);

        Assert.False(loose.IsMovableType);

        var normal = world.CreateItem();
        normal.BaseId = 0x1000;
        Assert.True(normal.IsMovableType);
    }
}
