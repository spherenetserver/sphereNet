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
/// What a plant yields is the plant's own, and FRUIT= is how a script says so.
///
/// Upstream keeps it in the crop's fruit override, which shares storage with MORE2
/// (IC_FRUIT, CItem.cpp:3404), and reads the argument through the expression engine -
/// so a defname resolves, and the inline pool the crop rows are written as is picked
/// from. The harvest here already reads MORE2 as that override; only the key to write
/// it was missing, so the 28 lines in the live pack that give a palm its coconuts or
/// pick a field's crop from a list left the plant on its definition's fruit.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlantFruitOverrideTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_fr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Item.ResolveDefName is cleared by ResetEngineStatics between tests.
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Load the probe pack and wire the defname resolver the harvest path
    /// uses, the way the server wires it.</summary>
    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0f84]", "DEFNAME=i_fruit_coconut", "NAME=coconut", "TYPE=t_fruit",
            "[ITEMDEF 0c64]", "DEFNAME=i_fruit_pumpkin", "NAME=pumpkin", "TYPE=t_fruit",
            "[ITEMDEF 0c95]", "DEFNAME=i_prb_palm", "NAME=palm", "TYPE=t_foliage",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
            var d = DefinitionLoader.GetItemDef(rid.Index);
            return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)rid.Index;
        };
        return world;
    }

    private Item Palm(GameWorld world)
    {
        var palm = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(palm, 0x0C95);
        world.PlaceItem(palm, new Point3D(100, 100, 0, 0));
        return palm;
    }

    /// <summary>The 22-line case: a named fruit lands in the override the harvest
    /// reads.</summary>
    [Fact]
    public void ANamedFruitBecomesThePlantsOverride()
    {
        var world = Load();
        var palm = Palm(world);

        Assert.Equal(0u, palm.More2);
        Assert.True(palm.TrySetProperty("FRUIT", "i_fruit_coconut"));
        Assert.Equal(0x0F84u, palm.More2);
    }

    /// <summary>The 6-line case: a crop row names a list and one of them is picked.</summary>
    [Fact]
    public void AListOfFruitIsPickedFrom()
    {
        var world = Load();
        var seen = new HashSet<uint>();
        for (int i = 0; i < 40; i++)
        {
            var palm = Palm(world);
            Assert.True(palm.TrySetProperty("FRUIT", "{ i_fruit_coconut, i_fruit_pumpkin }"));
            Assert.True(palm.More2 is 0x0F84u or 0x0C64u, $"picked {palm.More2:X}");
            seen.Add(palm.More2);
        }
        Assert.Equal(2, seen.Count);
    }

    /// <summary>A plain id works too - it is the same number the override holds.</summary>
    [Fact]
    public void ANumberIsTakenAsTheIdItIs()
    {
        var world = Load();
        var palm = Palm(world);

        Assert.True(palm.TrySetProperty("FRUIT", "0f84"));
        Assert.Equal(0x0F84u, palm.More2);
    }

    /// <summary>A name that resolves to nothing leaves the plant as it was, rather
    /// than blanking the fruit it already had.</summary>
    [Fact]
    public void AnUnknownNameChangesNothing()
    {
        var world = Load();
        var palm = Palm(world);
        palm.TrySetProperty("FRUIT", "i_fruit_coconut");

        Assert.True(palm.TrySetProperty("FRUIT", "i_not_a_thing"));
        Assert.Equal(0x0F84u, palm.More2);
    }
}
