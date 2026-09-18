using System.Reflection;
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
/// &lt;NAME&gt; answers what the object is called, whichever door it came through.
///
/// Upstream writes OC_NAME from the virtual GetName() (CObjBase.cpp:1546), which falls
/// back to the definition when the instance carries no name of its own - CChar::GetName
/// to the CHARDEF, CItem::GetName to the ITEMDEF, both pluralising on the way out.
/// Reading the raw field instead made the answer depend on how the object was made: an
/// NPC from a spawner answered, because that path copies the definition's name onto the
/// instance, while the same NPC from SERV.NEWNPC answered an empty string - and a script
/// comparing &lt;NAME&gt; saw nothing to compare.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NameReadFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_nm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly BindingFlags Priv = BindingFlags.Static | BindingFlags.NonPublic;

    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "p.scp");
        File.WriteAllLines(file, new[]
        {
            "[CHARDEF 0013]", "DEFNAME=c_probe_named", "NAME=an orcish brute", "ID=c_orc",
            "[ITEMDEF 0eed]", "DEFNAME=i_probe_coin", "NAME=%gold coins/gold coin%",
            "TYPE=t_gold",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var t = typeof(SphereNet.Server.Program);
        t.GetField("_resources", Priv)!.SetValue(null, resources);
        t.GetField("_world", Priv)!.SetValue(null, world);
        return world;
    }

    private static string Read(ObjBase o)
    {
        Assert.True(o.TryGetProperty("NAME", out string v));
        return v;
    }

    /// <summary>The two doors agree, which is the whole point.</summary>
    [Fact]
    public void BothWaysOfMakingAnNpcAnswerTheSameName()
    {
        var world = Load();

        string uid = (string?)typeof(SphereNet.Server.Program)
            .GetMethod("HandleServNewNpc", Priv)!.Invoke(null, ["c_probe_named"]) ?? "";
        var viaNew = world.FindChar(new Serial(
            uint.Parse(uid, System.Globalization.NumberStyles.HexNumber)));
        Assert.NotNull(viaNew);

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var viaSpawn = spawn.SpawnSpecific(0x13);
        Assert.NotNull(viaSpawn);

        Assert.Equal("an orcish brute", Read(viaNew!));
        Assert.Equal("an orcish brute", Read(viaSpawn!));
    }

    /// <summary>A name of its own still wins over the definition's.</summary>
    [Fact]
    public void AnInstanceNameWinsOverTheDefinition()
    {
        var world = Load();
        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var ch = spawn.SpawnSpecific(0x13)!;

        Assert.True(ch.TrySetProperty("NAME", "Grukk"));
        Assert.Equal("Grukk", Read(ch));
    }

    /// <summary>An item reaches its ITEMDEF the same way, and the plural marker is
    /// resolved against the stack size on the way out - upstream pluralises inside
    /// GetName too (GetNamePluralize, CItem.cpp:1770).
    ///
    /// Inside the markers the PLURAL comes first: "%gold coins/gold coin%". Both
    /// engines read it that way, char for char (CItemBase.cpp:217).</summary>
    [Fact]
    public void AnItemReadsItsDefinitionAndPluralises()
    {
        var world = Load();
        var one = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(one, 0x0EED);
        world.PlaceItem(one, new Point3D(101, 100, 0, 0));
        one.Amount = 1;
        Assert.Equal("gold coin", Read(one));

        var many = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(many, 0x0EED);
        world.PlaceItem(many, new Point3D(102, 100, 0, 0));
        many.Amount = 7;
        Assert.Equal("gold coins", Read(many));
    }
}
