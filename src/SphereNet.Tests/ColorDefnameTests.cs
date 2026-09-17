using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// COLOR=&lt;defname&gt;, which is how every colour constant in a pack is written.
///
///     [DEFNAME colors_ore]
///     color_o_valorite   0515
///
///     [ITEMDEF 019b7]
///     ON=@Create
///        COLOR=color_o_valorite
///
/// Two things have to be right and neither is obvious. The name has to resolve at
/// all - a colour constant is filed as a NUMERIC defname, not a text one, so the
/// range lookup that handles colors_skin -&gt; {1002 1058} never saw it and the item
/// silently kept hue 0. And the number has to be read the way Sphere writes it:
/// a leading zero means HEX (CObjBase / ScriptKey.TryParseNumber), so 0515 is 1301
/// and not five hundred and fifteen - a plausible-looking wrong colour is worse
/// than no colour, because nobody reports it as a bug.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ColorDefnameTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_colordef_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void LoadDefs()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "colors.scp");
        File.WriteAllText(file,
            "[DEFNAME colors_ore]\r\n" +
            "color_o_valorite   0515\r\n" +
            "color_o_copper     0641\r\n" +
            "color_plain_ten    10\r\n" +
            "\r\n" +
            "[DEFNAME colors_range]\r\n" +
            "colors_skin        {1002 1058}\r\n");
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static Item NewItem()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));
        return item;
    }

    /// <summary>The name resolves, and the leading zero means hex.</summary>
    [Fact]
    public void AColourDefnameResolvesAndItsLeadingZeroMeansHex()
    {
        LoadDefs();
        var item = NewItem();

        Assert.True(item.TrySetProperty("COLOR", "color_o_valorite"));
        Assert.Equal(0x515, item.Hue.Value);      // 1301, not 515

        Assert.True(item.TrySetProperty("COLOR", "color_o_copper"));
        Assert.Equal(0x641, item.Hue.Value);
    }

    /// <summary>A defname written WITHOUT a leading zero is decimal, as Sphere reads
    /// it - the hex rule is the leading zero, not "always hex".</summary>
    [Fact]
    public void ADefnameWithoutALeadingZeroStaysDecimal()
    {
        LoadDefs();
        var item = NewItem();

        Assert.True(item.TrySetProperty("COLOR", "color_plain_ten"));
        Assert.Equal(10, item.Hue.Value);
    }

    /// <summary>A RANGE defname still picks from the range - the numeric branch was
    /// added beside that one, not in place of it.</summary>
    [Fact]
    public void ARangeDefnameStillPicksFromItsRange()
    {
        LoadDefs();
        var item = NewItem();

        Assert.True(item.TrySetProperty("COLOR", "colors_skin"));
        Assert.InRange(item.Hue.Value, 1002, 1058);
    }

    /// <summary>A literal number is unaffected, and a name nothing defines leaves the
    /// hue alone rather than zeroing it.</summary>
    [Fact]
    public void ALiteralStillWinsAndAnUnknownNameChangesNothing()
    {
        LoadDefs();
        var item = NewItem();

        Assert.True(item.TrySetProperty("COLOR", "0515"));
        Assert.Equal(0x515, item.Hue.Value);

        item.TrySetProperty("COLOR", "no_such_colour_defname");
        Assert.Equal(0x515, item.Hue.Value);
    }

    /// <summary>The same on a character: an NPC's @Create tints its body this way
    /// just as an item's does.</summary>
    [Fact]
    public void ACharacterTakesAColourDefnameToo()
    {
        LoadDefs();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        Assert.True(ch.TrySetProperty("COLOR", "color_o_copper"));
        Assert.Equal(0x641, ch.Hue.Value);
    }
}
