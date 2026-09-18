using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// What &lt;WEIGHT&gt; and &lt;MAXWEIGHT&gt; answer, and in what unit.
///
/// Upstream answers WEIGHT with GetWeight() - the unit weight times the STACK
/// (CItem.cpp:2025), plus the contents for a container (CItemContainer.cpp:341), and
/// the whole carried tree for a character - and MAXWEIGHT with Calc_MaxCarryWeight,
/// which ends on "return iQty * WEIGHT_UNITS" (CResourceCalc.cpp:30). Both are
/// therefore in TENTHS of a stone.
///
/// The packs say the same from the other side: they print <FVAL &lt;WEIGHT&gt;>, which
/// renders tenths as "21.5", and test (&lt;WEIGHT&gt; == 10) to choose between the
/// "1 stone" and "N stones" clilocs.
///
/// These reads answered whole stones instead, and the item one skipped the stack and
/// the contents entirely: a status line read 2.1 where it meant 21.5, and a stack of
/// 100 ingots weighed what one ingot does.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WeightReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_wt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "w.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 01bef]", "DEFNAME=i_wt_ingot", "NAME=ingot", "TYPE=t_ingot",
            "WEIGHT=1",
            "[ITEMDEF 0e75]", "DEFNAME=i_wt_pack", "NAME=pack", "TYPE=t_container",
            "WEIGHT=3",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static string Read(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    private static Item Make(GameWorld world, int id, Point3D at)
    {
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, id);
        world.PlaceItem(it, at);
        return it;
    }

    /// <summary>A one-stone item reads 10, which is the singular case the packs test
    /// for by name.</summary>
    [Fact]
    public void AOneStoneItemReadsTen()
    {
        var world = Load();
        var ingot = Make(world, 0x1BEF, new Point3D(100, 100, 0, 0));
        Assert.Equal("10", Read(ingot, "WEIGHT"));
    }

    /// <summary>The stack counts. This is the one that showed: a hundred ingots used
    /// to weigh what one does.</summary>
    [Fact]
    public void AStackWeighsWhatTheStackWeighs()
    {
        var world = Load();
        var ingots = Make(world, 0x1BEF, new Point3D(100, 100, 0, 0));
        ingots.Amount = 100;
        Assert.Equal("1000", Read(ingots, "WEIGHT"));
    }

    /// <summary>A container carries its contents' weight, as upstream's does.</summary>
    [Fact]
    public void AContainerCarriesWhatIsInside()
    {
        var world = Load();
        var pack = Make(world, 0x0E75, new Point3D(100, 100, 0, 0));
        pack.ItemType = ItemType.Container;
        Assert.Equal("30", Read(pack, "WEIGHT"));       // the empty pack: 3 stones

        var ingots = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(ingots, 0x1BEF);
        ingots.Amount = 5;
        Assert.True(pack.TryAddItem(ingots));

        Assert.Equal("80", Read(pack, "WEIGHT"));       // 3 stones + 5 ingots
    }

    /// <summary>A character reads the whole tree it carries, in the same unit, so the
    /// pack line "IF (&lt;SRC.WEIGHT&gt; >= &lt;SRC.MAXWEIGHT&gt;)" compares like with
    /// like.</summary>
    [Fact]
    public void ACharacterReadsWhatItCarriesAgainstWhatItCanCarry()
    {
        var world = Load();
        var ch = world.CreateCharacter();
        ch.Str = 50;
        world.PlaceCharacter(ch, new Point3D(101, 100, 0, 0));

        var pack = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(pack, 0x0E75);
        pack.ItemType = ItemType.Container;
        Assert.True(ch.Equip(pack, Layer.Pack));

        var ingots = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(ingots, 0x1BEF);
        ingots.Amount = 20;
        Assert.True(pack.TryAddItem(ingots));

        // 3 stones of pack + 20 ingots = 23 stones = 230 tenths.
        Assert.Equal("230", Read(ch, "WEIGHT"));

        // 40 + 50 * 3.5 = 215 stones = 2150 tenths.
        Assert.Equal("2150", Read(ch, "MAXWEIGHT"));
        Assert.True(long.Parse(Read(ch, "WEIGHT")) < long.Parse(Read(ch, "MAXWEIGHT")));
    }

    /// <summary>MAXWEIGHT follows the ADJUSTED strength, which is what the character
    /// actually lifts with: the read used to work the formula out again from the raw
    /// stat, so a strength bonus never reached it.</summary>
    [Fact]
    public void MaxWeightFollowsTheAdjustedStrength()
    {
        var world = Load();
        var ch = world.CreateCharacter();
        ch.Str = 50;
        world.PlaceCharacter(ch, new Point3D(102, 100, 0, 0));
        Assert.Equal("2150", Read(ch, "MAXWEIGHT"));

        ch.ModStr = 20;                       // 40 + 70 * 3.5 = 285 stones
        Assert.Equal("2850", Read(ch, "MAXWEIGHT"));
        Assert.Equal((long)ch.MaxWeight * Item.WeightUnits, long.Parse(Read(ch, "MAXWEIGHT")));
    }
}
