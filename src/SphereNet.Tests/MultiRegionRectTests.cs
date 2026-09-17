using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A multi's region is the rectangle its definition declares, not its footprint.
///
/// [MULTIDEF] MULTIREGION names an inclusive rectangle and upstream stores it as the
/// multi's region rect (CItemBaseMulti::SetMultiRegion, CItemBase.cpp:1936), which
/// MultiRealizeRegion then lays on the world. It deliberately reaches past the walls:
/// the small stone-and-plaster house declares -3,-3,3,4 over a 7x7 footprint, one row
/// further south, which is exactly where its front step is.
///
/// This engine built the region from the component bounding box and read MULTIREGION
/// nowhere, so every house region was the shape of its walls - the step outside them,
/// and with it the Safe and NoBuild flags and the @Enter and @Step scripts that the
/// region carries.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MultiRegionRectTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_mr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";
    private const ushort MultiId = 0x4064;

    /// <summary>A registry holding one multi whose FOOTPRINT is a 3x3 block, so a
    /// declared region that differs is unmistakable.</summary>
    private static MultiRegistry RegistryWithSquareFootprint()
    {
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = MultiId };
        for (short dx = -1; dx <= 1; dx++)
            for (short dy = -1; dy <= 1; dy++)
                def.Components.Add(new MultiComponent
                {
                    TileId = 0x0006, DeltaX = dx, DeltaY = dy, DeltaZ = 0, Visible = true,
                });
        def.RecalcBounds();
        registry.Register(def);
        return registry;
    }

    private static MultiDef LoadWith(MultiRegistry registry, string multiRegionLine)
    {
        string dir = Path.Combine(Path.GetTempPath(), "spn_mr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "m.scp");
        File.WriteAllText(file,
            "[MULTIDEF 04064]" + Nl +
            "DEFNAME=m_probe_house" + Nl +
            "NAME=Probe House" + Nl +
            "TYPE=t_multi" + Nl +
            multiRegionLine +
            "BASESTORAGE=489" + Nl);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = dir };
        resources.LoadResourceFile(file);

        registry.MergeScriptMetadata(resources);

        try { Directory.Delete(dir, true); } catch (IOException) { }
        return registry.Get(MultiId)!;
    }

    /// <summary>The footprint is 3x3; the declared region is not, and the declared one
    /// wins.</summary>
    [Fact]
    public void ADeclaredRegionOverridesTheFootprint()
    {
        var registry = RegistryWithSquareFootprint();
        var def = LoadWith(registry, "MULTIREGION=-3,-3,3,4" + Nl);

        Assert.Equal((short)-1, def.MinX);      // the footprint is untouched
        Assert.Equal((short)1, def.MaxY);

        Assert.NotNull(def.ScriptRegion);
        Assert.Equal(((short)-3, (short)-3, (short)3, (short)4), def.RegionBounds);
    }

    /// <summary>With no MULTIREGION the footprint still stands in, which is what
    /// every multi whose definition declares none relies on.</summary>
    [Fact]
    public void WithoutADeclaredRegionTheFootprintStandsIn()
    {
        var registry = RegistryWithSquareFootprint();
        var def = LoadWith(registry, "");

        Assert.Null(def.ScriptRegion);
        Assert.Equal(((short)-1, (short)-1, (short)1, (short)1), def.RegionBounds);
    }

    /// <summary>Written the other way round it is the same rectangle - the corners
    /// are a rectangle, not an order.</summary>
    [Fact]
    public void TheCornersAreNormalised()
    {
        var registry = RegistryWithSquareFootprint();
        var def = LoadWith(registry, "MULTIREGION=3,4,-3,-3" + Nl);

        Assert.Equal(((short)-3, (short)-3, (short)3, (short)4), def.RegionBounds);
    }

    /// <summary>Too few numbers is not a rectangle, and upstream ignores the line
    /// rather than building a broken one.</summary>
    [Fact]
    public void AMalformedDeclarationIsIgnored()
    {
        var registry = RegistryWithSquareFootprint();
        var def = LoadWith(registry, "MULTIREGION=-3,-3" + Nl);

        Assert.Null(def.ScriptRegion);
        Assert.Equal(((short)-1, (short)-1, (short)1, (short)1), def.RegionBounds);
    }

    /// <summary>End to end: the region a placed house gets covers the declared
    /// rectangle, so the tile one row beyond the walls is inside it.</summary>
    [Fact]
    public void ThePlacedHousesRegionCoversTheDeclaredRectangle()
    {
        var registry = RegistryWithSquareFootprint();
        using var lf = LoggerFactory.Create(_ => { });
        string dir = Path.Combine(Path.GetTempPath(), "spn_mr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "m.scp");
        File.WriteAllText(file,
            "[MULTIDEF 04064]" + Nl + "DEFNAME=m_probe_house" + Nl +
            "TYPE=t_multi" + Nl + "MULTIREGION=-3,-3,3,4" + Nl);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = dir };
        resources.LoadResourceFile(file);

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        registry.MergeScriptMetadata(resources);
        var housing = new HousingEngine(world, registry);

        var multi = world.CreateItem();
        multi.BaseId = MultiId;
        multi.ItemType = SphereNet.Core.Enums.ItemType.Multi;
        // RegisterExistingMulti builds the house from the multi's own tags, so it
        // needs an owner on it the way a loaded save carries one.
        multi.SetTag("HOUSE.OWNER", "040000001");
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        var house = housing.RegisterExistingMulti(multi);
        Assert.NotNull(house);

        var region = world.FindRegionByUid(house!.RegionUid);
        Assert.NotNull(region);

        // One row south of the 3x3 footprint - outside the walls, inside the
        // declared region. This is the tile the front step sits on.
        Assert.True(region!.Contains(new Point3D(100, 104, 0, 0)),
            "the declared region has to reach past the walls");
        // And well outside it is still outside.
        Assert.False(region.Contains(new Point3D(100, 110, 0, 0)));

        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
