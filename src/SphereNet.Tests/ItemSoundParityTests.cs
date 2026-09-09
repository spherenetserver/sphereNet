using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What an item sounds like when it lands and when it is worn (port plan İŞ-12 /
/// PLAN-305).
///
/// Upstream picks the drop sound from the item's TYPE - coins and gold by how many,
/// gems by how big, an ingot only when it lands on the ground - then lets the item's
/// own DROPSOUND key override it, and falls back to 0x057 for landing ON something and
/// 0x042 for the ground (CItem.cpp:1490-1552). Wearing something plays EQUIPSOUND,
/// 0x057 by default, on any layer that shows (CCharAct.cpp:3355).
///
/// SphereNet had a flat 0x042 for every drop with an invented gold banding beside it,
/// no notion of landing on something, and no equip sound whatsoever.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemSoundParityTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Defs = """
        [ITEMDEF 0eed]
        DEFNAME=i_gold_snd
        NAME=gold coin
        TYPE=t_gold

        [ITEMDEF 01bf2]
        DEFNAME=i_ingot_snd
        NAME=iron ingot
        TYPE=t_ingot

        [ITEMDEF 0f10]
        DEFNAME=i_gem_small_snd
        NAME=small gem
        TYPE=t_gem

        [ITEMDEF 0f26]
        DEFNAME=i_gem_big_snd
        NAME=big gem
        TYPE=t_gem

        [ITEMDEF 01517]
        DEFNAME=i_shirt_snd
        NAME=shirt
        TYPE=t_clothing
        LAYER=5

        [ITEMDEF 01518]
        DEFNAME=i_loud_shirt_snd
        NAME=loud shirt
        TYPE=t_clothing
        LAYER=5
        DROPSOUND=0x123
        EQUIPSOUND=0x456
        """;

    public ItemSoundParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_snd_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose() => File.Delete(_scriptPath);

    /// <summary>Definitions are built inside the test, not in the constructor: the
    /// shared-statics reset runs between the two and would wipe them.</summary>
    private Item Make(ushort baseId, ushort amount = 1)
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = baseId;
        item.MaterializeDefinitionType();
        item.Amount = amount;
        return item;
    }

    [Theory]
    [InlineData(1, 0x035)]
    [InlineData(2, 0x032)]
    [InlineData(3, 0x036)]
    [InlineData(4, 0x036)]
    [InlineData(5, 0x037)]
    [InlineData(500, 0x037)]
    public void GoldSoundsLikeHowMuchOfItThereIs(ushort amount, int expected)
    {
        Assert.Equal((ushort)expected, Make(0x0EED, amount).GetDropSound(ontoSomething: false));
    }

    [Fact]
    public void AGemSoundsLikeItsSize()
    {
        Assert.Equal((ushort)0x032, Make(0x0F10).GetDropSound(ontoSomething: false));  // <= 0x0F20
        Assert.Equal((ushort)0x034, Make(0x0F26).GetDropSound(ontoSomething: false));  // >  0x0F20
    }

    [Fact]
    public void AnIngotOnlyRingsWhenItHitsTheGround()
    {
        Assert.Equal((ushort)0x033, Make(0x1BF2).GetDropSound(ontoSomething: false));
        // Landed on something: the type table stays quiet and the general sound answers.
        Assert.Equal((ushort)0x057, Make(0x1BF2).GetDropSound(ontoSomething: true));
    }

    [Fact]
    public void AnOrdinaryItemDependsOnWhatItLandedOn()
    {
        Assert.Equal((ushort)0x042, Make(0x1517).GetDropSound(ontoSomething: false));
        Assert.Equal((ushort)0x057, Make(0x1517).GetDropSound(ontoSomething: true));
    }

    [Fact]
    public void TheDefinitionCanSayWhatItSoundsLike()
    {
        var loud = Make(0x1518);
        Assert.Equal((ushort)0x123, loud.GetDropSound(ontoSomething: false));
        Assert.Equal((ushort)0x456, loud.GetEquipSound());
    }

    [Fact]
    public void AnInstanceOverridesItsDefinition()
    {
        var loud = Make(0x1518);
        loud.SetTag("DROPSOUND", "0x789");
        Assert.Equal((ushort)0x789, loud.GetDropSound(ontoSomething: false));
    }

    [Fact]
    public void WithoutAKeyWearingSomethingStillMakesASound()
    {
        Assert.Equal((ushort)0x057, Make(0x1517).GetEquipSound());
    }

    [Fact]
    public void OnlyALayerThatShowsIsHeard()
    {
        Assert.True(Item.IsVisibleLayer(Layer.Shirt));
        Assert.True(Item.IsVisibleLayer(Layer.Horse));      // the mount is the last one
        Assert.False(Item.IsVisibleLayer(Layer.None));
        Assert.False(Item.IsVisibleLayer(Layer.BankBox));   // bookkeeping layers are silent
        Assert.False(Item.IsVisibleLayer(Layer.Dragging));
    }
}
