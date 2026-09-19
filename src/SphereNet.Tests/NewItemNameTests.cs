using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// SERV.NEWITEM does not name the item after its defname.
///
/// A per-instance name wins over everything, so stamping the DEFNAME as a fallback cut
/// off the chain that resolves a nameless definition properly: the itemdef NAME, and then
/// the tiledata name. The shipped pack's [ITEMDEF 0eed] carries no NAME= line at all and
/// tiledata calls 0x0EED "gold coin" - so a pile created this way read "65000 i_gold"
/// while the same pile created any other way read "65000 gold coins". Reported from a
/// shard exactly that way.
///
/// Upstream stamps no instance name here either: a name comes from the base definition
/// and, failing that, from the tiledata.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NewItemNameTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_nin_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private SphereNet.Game.World.GameWorld Load(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 256, 256);
        md.SetSyntheticItemTile(0x0EED, new ItemTileData
        { Flags = TileFlag.Generic, Name = "gold coin%s%", Weight = 0 });

        var world = TestHarness.CreateWorld();
        world.MapData = md;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var t = typeof(SphereNet.Server.Program);
        t.GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, res);
        t.GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, world);
        return world;
    }

    private static Item? NewItem(SphereNet.Game.World.GameWorld world, string arg)
    {
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        resolve.Invoke(null, ["_NEWITEM=" + arg]);
        return world.FindItem(world.LastNewItem);
    }

    /// <summary>The reported case: a definition with no NAME= at all.</summary>
    [Fact]
    public void ANamelessDefinitionFallsThroughToTheTiledataName()
    {
        var world = Load("[ITEMDEF 0eed]" + Nl + "DEFNAME=i_gold" + Nl + "TYPE=t_gold" + Nl);

        var gold = NewItem(world, "i_gold");
        Assert.NotNull(gold);
        gold!.Amount = 1;

        Assert.Equal("", gold.Name);                  // nothing stamped on the instance
        Assert.Equal("gold coin", gold.GetName());
    }

    /// <summary>And the plural still works off the tiledata template, which a stamped
    /// defname also broke.</summary>
    [Fact]
    public void ThePluralComesThroughToo()
    {
        var world = Load("[ITEMDEF 0eed]" + Nl + "DEFNAME=i_gold" + Nl + "TYPE=t_gold" + Nl);

        var gold = NewItem(world, "i_gold");
        Assert.NotNull(gold);
        gold!.Amount = 65000;

        Assert.Equal("gold coins", gold.GetName());
    }

    /// <summary>A definition that DOES name itself still wins - that half was never
    /// wrong and must not change.</summary>
    [Fact]
    public void ADefinitionWithANameStillUsesIt()
    {
        var world = Load("[ITEMDEF 0eed]" + Nl + "DEFNAME=i_gold" + Nl +
                         "NAME=shiny coin" + Nl + "TYPE=t_gold" + Nl);

        var gold = NewItem(world, "i_gold");
        Assert.NotNull(gold);

        Assert.Equal("shiny coin", gold!.GetName());
    }
}
