using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// The member names the REFERENCE script distribution calls that this engine did
/// not answer.
///
/// A name nothing answers is a line that does nothing: no error, no log. These were
/// found by sweeping oldSphere/Scripts-X-main - written against the reference engine,
/// so a name it calls and nothing answers is a gap by construction - and every one
/// here is present in the reference's own key tables.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ReferencePackMemberGapTests
{
    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static string Get(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"nothing answered {key}");
        return v;
    }

    /// <summary>MOREM is the MAP component of the item's MOREP point
    /// (IC_MOREM, CItem.cpp:2923/3429). The three axes each had a key; the map did
    /// not, so a marked location could not be read back with its facet.</summary>
    [Fact]
    public void MoremIsTheMapComponentOfMorep()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        item.TrySetProperty("MOREP", "50,60,7,3");
        Assert.Equal("3", Get(item, "MOREM"));

        item.TrySetProperty("MOREM", "1");
        Assert.Equal("1", Get(item, "MOREM"));
        // Setting the map must not disturb the axes.
        Assert.Equal("50", Get(item, "MOREX"));
        Assert.Equal("60", Get(item, "MOREY"));
        Assert.Equal("7", Get(item, "MOREZ"));
    }

    /// <summary>The same clock in three units (CObjBase.cpp:1570-1578). Only the
    /// seconds key could be read, so a script that wrote its timer in tenths - which
    /// sets fine - had no way to read it back.</summary>
    [Fact]
    public void TimerReadsBackInEveryUnitItCanBeWrittenIn()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        item.TrySetProperty("TIMERD", "50");   // five seconds
        int seconds = int.Parse(Get(item, "TIMER"));
        int tenths = int.Parse(Get(item, "TIMERD"));
        int ms = int.Parse(Get(item, "TIMERMS"));

        Assert.InRange(seconds, 4, 5);
        Assert.InRange(tenths, 45, 50);
        Assert.InRange(ms, 4500, 5000);

        // An unset timer answers -1 in every unit, as upstream's own guard does.
        var fresh = world.CreateItem();
        world.PlaceItem(fresh, new Point3D(11, 10, 0, 0));
        Assert.Equal("-1", Get(fresh, "TIMER"));
        Assert.Equal("-1", Get(fresh, "TIMERD"));
        Assert.Equal("-1", Get(fresh, "TIMERMS"));
    }

    /// <summary>The AOS suit family (CCPropsChar / CCPropsItemEquippable /
    /// CCPropsItemWeapon). Four of these the combat engine already read out of these
    /// very tags - the value it acted on was invisible to script and a line setting
    /// it did nothing.</summary>
    [Theory]
    [InlineData("INCREASEHITCHANCE")]
    [InlineData("INCREASEDEFCHANCE")]
    [InlineData("INCREASEDAM")]
    [InlineData("REFLECTPHYSICALDAM")]
    [InlineData("ENHANCEPOTIONS")]
    [InlineData("HITLOWERATK")]
    [InlineData("HITLOWERDEF")]
    [InlineData("INCREASEGOLD")]
    [InlineData("INCREASESPELLDAM")]
    [InlineData("SPELLCHANNELING")]
    [InlineData("MAGEARMOR")]
    [InlineData("BALANCED")]
    [InlineData("MAGEWEAPON")]
    public void AosSuitPropertiesRoundTripOnBothCharAndItem(string name)
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(11, 10, 0, 0));

        // Unset reads 0, which is the default the reference answers.
        Assert.Equal("0", Get(ch, name));
        Assert.Equal("0", Get(item, name));

        Assert.True(ch.TrySetProperty(name, "17"), $"char refused {name}");
        Assert.True(item.TrySetProperty(name, "23"), $"item refused {name}");
        Assert.Equal("17", Get(ch, name));
        Assert.Equal("23", Get(item, name));
    }

    /// <summary>What a script sets is what the combat engine sums. The suit values
    /// live on the same tags CombatEngine.GetEquipmentPropertyValue walks, so a
    /// scripted bonus has to reach the roll it is named for.</summary>
    [Fact]
    public void AScriptedSuitBonusReachesTheCombatAggregate()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        ch.TrySetProperty("INCREASEHITCHANCE", "12");
        Assert.Equal(12, SphereNet.Game.Combat.CombatEngine
            .GetEquipmentPropertyValue(ch, "INCREASEHITCHANCE"));
    }

    /// <summary>SKILLCHECK takes two arguments and the reference distribution writes
    /// them separated by a SPACE: "SKILLCHECK TINKERING 300". Str_Parse's default
    /// separator set is "=, \t" (CExpression.cpp:144) - splitting on the comma alone
    /// refused every space-written call and the whole key went unanswered.</summary>
    [Fact]
    public void SkillCheckAcceptsSpaceSeparatedArgumentsAsTheReferenceWritesThem()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;   // a GM succeeds at everything but Parrying
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        Assert.Equal("1", Get(ch, "SKILLCHECK TINKERING 300"));
        Assert.Equal("1", Get(ch, "SKILLCHECK.TINKERING,300"));   // the comma form still works

        // An unknown skill is not an answer of zero: upstream leaves the key unhandled.
        Assert.False(ch.TryGetProperty("SKILLCHECK NOTASKILL 300", out _));
    }

    /// <summary>HOUSETYPE is the name the reference answers on the multi itself
    /// (SHL_HOUSETYPE, CItemMulti.cpp:2847/3024). Only HOUSE.TYPE could be read and
    /// the write landed in a tag, so the housing packs' own permission test -
    /// if &lt;link.housetype&gt;==&lt;def.house_private&gt; - compared nothing against 0
    /// and every house took the private branch.</summary>
    [Fact]
    public void HouseTypeIsReadableOnTheMultiAndTheWriteReachesTheLiveHouse()
    {
        var world = NewWorld();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(10, 10, 0, 0));
        var house = new House(multi);
        Item.ResolveHouse = u => u == multi.Uid ? house : null;
        try
        {
            Assert.Equal("0", Get(multi, "HOUSETYPE"));   // Private

            Assert.True(multi.TrySetProperty("HOUSETYPE", "2"));
            Assert.Equal(HouseType.Guild, house.Type);
            Assert.Equal("2", Get(multi, "HOUSETYPE"));
            Assert.Equal("2", Get(multi, "HOUSE.TYPE"));  // the prefixed form agrees
        }
        finally { Item.ResolveHouse = null; }
    }

    /// <summary>DEFNAME is a BASE def key asked of the INSTANCE
    /// (CBaseBaseDef_props.tbl) - how a pack finds out what it is holding without
    /// comparing graphics. Only a region answered it.</summary>
    [Fact]
    public void DefnameAnswersOnAnItemAndACharacterInstance()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spn_defname_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "defs.scp");
        File.WriteAllText(file,
            "[ITEMDEF 0eed]\r\nDEFNAME=i_gold_probe\r\nTYPE=t_normal\r\n\r\n" +
            "[CHARDEF 0190]\r\nDEFNAME=c_man_probe\r\nNAME=Probe Man\r\n");
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));
        Assert.Equal("i_gold_probe", Get(item, "DEFNAME"));

        var ch = world.CreateCharacter();
        ch.BaseId = 0x0190;
        world.PlaceCharacter(ch, new Point3D(11, 10, 0, 0));
        Assert.Equal("c_man_probe", Get(ch, "DEFNAME"));

        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>CLIENTISENHANCED is the name the reference answers
    /// (CClient_props.tbl:10), beside CLIENTISKR which was already spelled that
    /// way here.</summary>
    [Fact]
    public void ClientIsEnhancedAnswersUnderTheReferenceName()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        Character.ResolveClientInfo = _ => (0, Character.ClientType.Enhanced);
        try
        {
            Assert.Equal("1", Get(ch, "CLIENTISENHANCED"));
            Assert.Equal("1", Get(ch, "ISENHANCED"));
            Character.ResolveClientInfo = _ => (0, Character.ClientType.ClassicWindows);
            Assert.Equal("0", Get(ch, "CLIENTISENHANCED"));
        }
        finally { Character.ResolveClientInfo = null; }
    }

    /// <summary>HOUSEDESIGN and TARGPRV are client REFERENCE heads
    /// (CClient::sm_szRefKeys, CClient.cpp:538-548): the interpreter resolves the
    /// head to another object and asks THAT one for the rest. Neither head existed,
    /// so the housing pack's "you are already designing another building" branch was
    /// unreachable and a uid written to TARGPRV could never be read back.</summary>
    [Fact]
    public void ClientReferenceHeadsResolveToTheObjectTheyName()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(20, 20, 0, 0));

        // With no design session and no previous target, both heads resolve to
        // nothing - which is what makes the pack's bare truth test false.
        Assert.Null(ch.ResolveRefHead("HOUSEDESIGN"));
        Assert.Null(ch.ResolveRefHead("TARGPRV"));

        Character.ResolveHouseDesignMulti = _ => multi;
        try
        {
            Assert.Same(multi, ch.ResolveRefHead("HOUSEDESIGN"));
        }
        finally { Character.ResolveHouseDesignMulti = null; }

        ch.TrySetProperty("TARGPRV", $"0{multi.Uid.Value:X}");
        Assert.Same(multi, ch.ResolveRefHead("TARGPRV"));
    }
}
