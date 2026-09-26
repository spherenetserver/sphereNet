using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SphereNet.Tests;

// Source-X CChar::InitPlayer (CChar.cpp:1818-2156) validates the appearance a
// creation packet asks for: the skin hue is forced into the race's range or table
// and carries HUE_UNDERWEAR, a hair/beard graphic the race or sex may not wear is
// dropped, hair/beard hues are forced into the race's range/table, and the starter
// shirt/pants hues are clamped to HUE_BLUE_LOW..HUE_DYE_HIGH.
[Collection("DefinitionLoaderSerial")]
public sealed class CharCreateAppearanceTests
{
    private static Character Create(CharCreateInfo info)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var state = TestHarness.CreateActiveNetState(lf, 19950);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.SetPrivateField(client, "_account", new Account { Name = "appearance" });
        client.PendingCharCreate = info;
        client.HandleCharSelect(-1, info.Name);
        return client.Character!;
    }

    private static CharCreateInfo Info(byte race, bool female = false, ushort skin = 0,
        ushort hair = 0, ushort hairHue = 0, ushort beard = 0, ushort beardHue = 0) => new()
    {
        Name = "Looks", Race = race, Female = female, Str = 30, Dex = 30, Int = 20,
        Skills = [(0, 30), (1, 30), (2, 30)],
        SkinHue = skin, HairStyle = hair, HairHue = hairHue, BeardStyle = beard, BeardHue = beardHue,
    };

    [Theory]
    [InlineData(0x0100, 0x03EA)]  // below HUE_SKIN_LOW
    [InlineData(0x0500, 0x0422)]  // above HUE_SKIN_HIGH
    [InlineData(0x0400, 0x0400)]  // in range
    public void HumanSkin_ClampedToSkinRange_WithUnderwearBit(ushort sent, ushort expected)
    {
        var info = Info(1, skin: sent);
        var ch = Create(info);
        Assert.Equal((ushort)(expected | 0x8000), ch.Hue.Value);
    }

    [Fact]
    public void ElfSkin_NotInTable_FallsBackToFirstEntry()
    {
        var bad = Info(2, skin: 0x0400);
        Assert.Equal(0x80BF, Create(bad).Hue.Value);
        var good = Info(2, skin: 0x0579);
        Assert.Equal(0x8579, Create(good).Hue.Value);
    }

    [Fact]
    public void GargoyleSkin_ClampedToGargSkinRange()
    {
        var info = Info(3, skin: 0x03EA);
        Assert.Equal(0x86DB, Create(info).Hue.Value);
    }

    [Fact]
    public void HumanHair_OutOfRangeGraphicDropped_HueClamped()
    {
        var elfHairOnHuman = Info(1, hair: 0x2FC0, hairHue: 0x0450);
        Assert.Null(Create(elfHairOnHuman).GetEquippedItem(Layer.Hair));

        var info = Info(1, hair: 0x203B, hairHue: 0x0021);
        var hair = Create(info).GetEquippedItem(Layer.Hair);
        Assert.NotNull(hair);
        Assert.Equal(0x044E, hair!.Hue.Value); // HUE_HAIR_LOW
        Assert.True(hair.IsAttr(ObjAttributes.Newbie));
        Assert.True(hair.IsAttr(ObjAttributes.Move_Never));
    }

    [Fact]
    public void HumanHair_SexRestrictedStylesDropped()
    {
        var receding = Info(1, female: true, hair: 0x2048);
        Assert.Null(Create(receding).GetEquippedItem(Layer.Hair));
        var buns = Info(1, female: false, hair: 0x2046);
        Assert.Null(Create(buns).GetEquippedItem(Layer.Hair));
    }

    [Fact]
    public void ElfHair_HueMustBeInElfTable()
    {
        var info = Info(2, hair: 0x2FC0, hairHue: 0x0450);
        var hair = Create(info).GetEquippedItem(Layer.Hair);
        Assert.NotNull(hair);
        Assert.Equal(0x0034, hair!.Hue.Value);
    }

    [Fact]
    public void Beard_FemaleAndElfGetNone_GargoyleUsesHornTable()
    {
        var female = Info(1, female: true, beard: 0x203E);
        Assert.Null(Create(female).GetEquippedItem(Layer.FacialHair));
        var elf = Info(2, beard: 0x203E);
        Assert.Null(Create(elf).GetEquippedItem(Layer.FacialHair));

        var garg = Info(3, beard: 0x42AD, beardHue: 0x0100);
        var horns = Create(garg).GetEquippedItem(Layer.FacialHair);
        Assert.NotNull(horns);
        Assert.Equal(0x0709, horns!.Hue.Value);

        var human = Info(1, beard: 0x2040, beardHue: 0x0600);
        Assert.Equal(0x04AD, Create(human).GetEquippedItem(Layer.FacialHair)!.Hue.Value); // HUE_HAIR_HIGH
    }

    [Fact]
    public void Gargoyle_FemaleHornsOnlyFromFemaleSet()
    {
        var maleHornOnFemale = Info(3, female: true, hair: 0x4258);
        Assert.Null(Create(maleHornOnFemale).GetEquippedItem(Layer.Hair));
        var femaleHorn = Info(3, female: true, hair: 0x42AA, hairHue: 0x076B);
        var hair = Create(femaleHorn).GetEquippedItem(Layer.Hair);
        Assert.NotNull(hair);
        Assert.Equal(0x076B, hair!.Hue.Value);
    }

    [Theory]
    [InlineData(0x0000, 0x0002)]  // HUE_BLUE_LOW
    [InlineData(0x0500, 0x03E9)]  // HUE_DYE_HIGH
    [InlineData(0x0123, 0x0123)]
    public void ClothHue_ClampedToDyeRange(ushort sent, ushort expected)
    {
        Assert.Equal(expected, GameClient.ClampCreateClothHue(sent));
    }
}
