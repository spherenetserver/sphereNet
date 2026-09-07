using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Where a script factory puts what it made, and what it reports when it cannot
/// (review 13G).
///
/// NEWITEM takes four fields - id, amount, parent, equip flag (CScriptObj.cpp:1340).
/// The amount is an EXPRESSION handed straight to SetAmount, a zero included
/// (CItem.cpp:2207). The parent goes through LoadSetContainer (CItem.cpp:2516): an
/// item takes the object as content, a character wears it at the layer its definition
/// declares, and CChar::LayerAdd bounces into the pack whatever the slot refuses
/// (CCharAct.cpp:251). The fourth field switches to CChar::ItemEquip instead
/// (:3278), which works the layer out for itself and therefore applies the strength
/// requirement (CanEquipLayer -> CanEquipStr, CCharStatus.cpp:326) and lets
/// @EquipTest refuse the item.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptFactoryParity13GTests
{
    private static Item MakeItem(GameWorld world, ushort id = 0x1F03)
    {
        var item = world.CreateItem();
        item.BaseId = id;
        return item;
    }

    private static Item MakeContainer(GameWorld world)
    {
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        return box;
    }

    private static Character MakePlayer(GameWorld world, int str = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = (short)str;
        world.PlaceCharacter(ch, new Point3D(120, 130, 0, 0));
        return ch;
    }

    // ============================================================ 13G-1

    [Theory]
    [InlineData("2", 2)]
    [InlineData("1+1", 2)]        // an expression, not a parse that stops at the '+'
    [InlineData("010", 16)]       // Sphere hex
    [InlineData("0", 0)]          // zero SURVIVES - it is not raised to one
    [InlineData("", 1)]           // unreadable -> the default
    [InlineData("junk", 1)]
    public void AFactoryAmountFollowsTheSphereExpressionContract(string field, int expected)
    {
        Assert.Equal(expected, ScriptNumber.ToScriptAmount(field));
    }

    // ============================================================ 13G-2

    [Fact]
    public void AContainerParentThatCannotTakeTheObjectRefusesRatherThanPretending()
    {
        var world = TestHarness.CreateWorld();
        var box = MakeContainer(world);
        for (int i = 0; i < Item.MaxContainerItems; i++)
            Assert.True(box.TryAddItem(MakeItem(world)));

        var extra = MakeItem(world);
        var outcome = ScriptItemPlacement.Place(world, extra, box.Uid, def: null, triggerEquip: false);

        Assert.Equal(ScriptItemPlacement.Outcome.Refused, outcome);
        Assert.Equal(Item.MaxContainerItems, box.ContentCount);
    }

    [Fact]
    public void AContainerParentWithRoomTakesIt()
    {
        var world = TestHarness.CreateWorld();
        var box = MakeContainer(world);
        var item = MakeItem(world);

        Assert.Equal(ScriptItemPlacement.Outcome.InContainer,
            ScriptItemPlacement.Place(world, item, box.Uid, def: null, triggerEquip: false));
        Assert.Equal(box.Uid, item.ContainedIn);
    }

    // ============================================================ 13G-3

    [Fact]
    public void APackLayerObjectBecomesThePackOfSomeoneWhoHasNone()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world);
        Assert.Null(owner.Backpack);

        var pack = MakeContainer(world);
        var token = MakeItem(world);
        Assert.True(pack.TryAddItem(token));

        var outcome = ScriptItemPlacement.Place(world, pack, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Pack },
            triggerEquip: false);

        Assert.Equal(ScriptItemPlacement.Outcome.Equipped, outcome);
        // The scripted pack IS the backpack - no second, empty bag was invented to
        // hold it, and what it carried is still inside it.
        Assert.Same(pack, owner.Backpack);
        Assert.Same(token, Assert.Single(pack.Contents));
    }

    [Fact]
    public void APackLayerObjectGoesInsideTheExistingPackRatherThanReplacingIt()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world);
        var worn = MakeContainer(world);
        Assert.True(owner.Equip(worn, Layer.Pack));

        var second = MakeContainer(world);
        var outcome = ScriptItemPlacement.Place(world, second, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Pack },
            triggerEquip: false);

        // CanEquipLayer refuses an occupied pack layer, so LayerAdd bounces it
        // (CCharStatus.cpp:455) - the wearer keeps the pack they had.
        Assert.Equal(ScriptItemPlacement.Outcome.InPack, outcome);
        Assert.Same(worn, owner.Backpack);
        Assert.Same(second, Assert.Single(worn.Contents));
    }

    [Fact]
    public void AnObjectWhoseDefinitionNamesNoLayerGoesInThePack()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world);
        var item = MakeItem(world);

        var outcome = ScriptItemPlacement.Place(world, item, owner.Uid, def: null, triggerEquip: false);

        Assert.Equal(ScriptItemPlacement.Outcome.InPack, outcome);
        Assert.NotNull(owner.Backpack);
        Assert.Same(item, Assert.Single(owner.Backpack!.Contents));
    }

    // ============================================================ 13G-4

    [Fact]
    public void TheEquipFlagAppliesTheStrengthRequirementAndBouncesWhatFailsIt()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world, str: 10);
        var shirt = MakeItem(world);
        shirt.SetTag("OVERRIDE.REQSTR", "100");

        var outcome = ScriptItemPlacement.Place(world, shirt, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Shirt },
            triggerEquip: true);

        Assert.Equal(ScriptItemPlacement.Outcome.InPack, outcome);
        Assert.Null(owner.GetEquippedItem(Layer.Shirt));
    }

    [Fact]
    public void TheEquipFlagWearsWhatTheWearerIsStrongEnoughFor()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world, str: 100);
        var shirt = MakeItem(world);
        shirt.SetTag("OVERRIDE.REQSTR", "40");

        bool equipFired = false;
        var outcome = ScriptItemPlacement.Place(world, shirt, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Shirt },
            triggerEquip: true,
            equipTestVeto: null,
            onEquipped: (_, _) => equipFired = true);

        Assert.Equal(ScriptItemPlacement.Outcome.Equipped, outcome);
        Assert.Same(shirt, owner.GetEquippedItem(Layer.Shirt));
        Assert.True(equipFired);
    }

    [Fact]
    public void TheEquipFlagLetsTheScriptRefuseTheItem()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world);
        var shirt = MakeItem(world);

        var outcome = ScriptItemPlacement.Place(world, shirt, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Shirt },
            triggerEquip: true,
            equipTestVeto: (_, _) => true);

        // @EquipTest RETURN 1: not worn, bounced into the pack (CCharAct.cpp:3307).
        Assert.Equal(ScriptItemPlacement.Outcome.InPack, outcome);
        Assert.Null(owner.GetEquippedItem(Layer.Shirt));
    }

    [Fact]
    public void WithoutTheFlagTheLoadStylePathMakesNoStrengthTest()
    {
        var world = TestHarness.CreateWorld();
        var owner = MakePlayer(world, str: 10);
        var shirt = MakeItem(world);
        shirt.SetTag("OVERRIDE.REQSTR", "100");

        // LoadSetContainer passes the definition's layer to LayerAdd, and
        // CanEquipLayer only reaches CanEquipStr when it has to derive the layer
        // itself (CCharStatus.cpp:326) - so this path dresses them regardless.
        var outcome = ScriptItemPlacement.Place(world, shirt, owner.Uid,
            new ItemDef(new ResourceId(ResType.ItemDef, 1)) { Layer = Layer.Shirt },
            triggerEquip: false);

        Assert.Equal(ScriptItemPlacement.Outcome.Equipped, outcome);
        Assert.Same(shirt, owner.GetEquippedItem(Layer.Shirt));
    }

    // ============================================================ 13G-5

    [Fact]
    public void AParentThatIsNotThereRefusesTheObject()
    {
        var world = TestHarness.CreateWorld();
        var item = MakeItem(world);

        Assert.Equal(ScriptItemPlacement.Outcome.Refused,
            ScriptItemPlacement.Place(world, item, new Serial(0x0BADF00D), def: null, triggerEquip: false));
    }
}
