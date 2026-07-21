using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Engine gaps found auditing the live dialogs/ script pack — all Source-X-
/// present (pack-custom tokens skipped per the maintainer rule): CANSEELOS
/// space-arg form, ISNEARTYPE item-type search, FINDLAYER layer names,
/// PARTY.MEMBER/MASTER ref-chaining, item RESCOUNT/RESOURCES/SKILLMAKE/INSTANCES,
/// SEXTANTP, and the CharDef-derived MAXFOOD.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class DialogPackParityTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanSeeLos_AcceptsSpaceAndDotArgForms()
    {
        var world = World();
        var a = world.CreateCharacter();
        var b = world.CreateCharacter();
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(b, new Point3D(101, 100, 0, 0));

        string uid = $"0{b.Uid.Value:X}";
        // Both the classic dot form and the space form (as produced by
        // <SRC.CANSEELOS <UID>>) must resolve identically.
        Assert.True(a.TryGetProperty($"CANSEELOS.{uid}", out string dotForm));
        Assert.True(a.TryGetProperty($"CANSEELOS {uid}", out string spaceForm));
        Assert.Equal(dotForm, spaceForm);
        Assert.Equal("1", spaceForm);
    }

    [Fact]
    public void IsNearType_FindsDynamicItemByType()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(ch.TryGetProperty("ISNEARTYPE T_MOONGATE 4", out string before));
        Assert.Equal("0", before);

        var gate = world.CreateItem();
        gate.ItemType = ItemType.Moongate;
        world.PlaceItem(gate, new Point3D(102, 100, 0, 0));

        Assert.True(ch.TryGetProperty("ISNEARTYPE T_MOONGATE 4", out string near));
        Assert.Equal("1", near);
        // Out of range → not found.
        Assert.True(ch.TryGetProperty("ISNEARTYPE T_MOONGATE 1", out string far));
        Assert.Equal("0", far);
    }

    [Fact]
    public void FindLayer_ResolvesLayerNameAndChains()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var hair = world.CreateItem();
        hair.BaseId = 0x203B;
        hair.Hue = new Color(0x0044);
        ch.Equip(hair, Layer.Hair); // layer 11

        // Numeric still works; the layer NAME resolves via DEF (layer_hair=11).
        Assert.True(ch.TryGetProperty("FINDLAYER.11", out string byNum));
        Assert.Equal($"0{hair.Uid.Value:X}", byNum);

        // Chained read to the found item's property.
        Assert.True(ch.TryGetProperty("FINDLAYER.11.COLOR", out string color));
        Assert.Equal(hair.Hue.Value.ToString(), color);

        // Chained REMOVE strips it.
        Assert.True(ch.TryExecuteCommand("FINDLAYER.11.REMOVE", "", null!));
        Assert.Null(ch.GetEquippedItem(Layer.Hair));
    }

    [Fact]
    public void ItemRescountAndInstances_Resolve()
    {
        var world = World();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        world.PlaceItem(pack, new Point3D(100, 100, 0, 0));

        Assert.True(pack.TryGetProperty("RESCOUNT", out string empty));
        Assert.Equal("0", empty);

        var a = world.CreateItem(); a.BaseId = 0x1BFB;
        var b = world.CreateItem(); b.BaseId = 0x1BFB;
        pack.AddItem(a);
        pack.AddItem(b);

        Assert.True(pack.TryGetProperty("RESCOUNT", out string count));
        Assert.Equal("2", count);

        // INSTANCES counts world items sharing the display id.
        Assert.True(a.TryGetProperty("INSTANCES", out string inst));
        Assert.Equal("2", inst);
    }

    [Fact]
    public void Sextant_ProducesCoordinateString()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(1323, 1624, 0, 0)); // the zero point

        Assert.True(ch.TryGetProperty("SEXTANTP", out string sextant));
        // At the zero point both axes read 0 degrees.
        Assert.Equal("0o 0'N, 0o 0'E", sextant);
    }

    [Fact]
    public void CharDef_MaxFood_DerivedFromFoodType()
    {
        var def = new CharDef(ResourceId.Invalid);
        def.LoadFromKey("FOODTYPE", "5 t_crops, 5 t_grain, 64 t_grass");
        Assert.Equal(64, def.MaxFood);

        // Bare resource with no quantity counts as 1.
        var def2 = new CharDef(ResourceId.Invalid);
        def2.LoadFromKey("FOODTYPE", "t_arock,t_coin,t_ore");
        Assert.Equal(1, def2.MaxFood);
    }

    [Fact]
    public void VirtualGoldAndMaxFood_CharReadsDefaultZero()
    {
        var world = World();
        var ch = world.CreateCharacter();

        Assert.True(ch.TryGetProperty("VIRTUALGOLD", out string vg));
        Assert.Equal("0", vg);
        Assert.True(ch.TrySetProperty("VIRTUALGOLD", "500"));
        Assert.True(ch.TryGetProperty("VIRTUALGOLD", out string vg2));
        Assert.Equal("500", vg2);

        Assert.True(ch.TryGetProperty("MAXFOOD", out string mf));
        Assert.Equal("0", mf);
    }
}
