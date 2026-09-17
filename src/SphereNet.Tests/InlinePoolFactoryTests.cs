using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A pool written where a defname goes is picked from, not taken as a name.
///
/// Upstream resolves it in the shared resource lookup, which runs the name through the
/// expression engine before looking anything up - the comment there says as much, "May
/// be some complex expression {}" (CResourceHolder.cpp:129). Because that lookup is
/// shared, the form works for every resource type, which is why the packs write
/// SERV.NEWNPC { c_dolphin 1 c_sea_serpent 1 } as readily as the item version.
///
/// Here the braces reached the defname resolver verbatim, failed to resolve, and the
/// line produced nothing: the nine lines in the live pack that use it - fish, fireworks
/// effects, dolphins, harbour vendors - created no object at all, and said nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class InlinePoolFactoryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_pool_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly BindingFlags Priv = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>Load the probe pack and point the server's factories at it.</summary>
    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "p.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0eed]", "DEFNAME=i_pool_a", "NAME=pool a", "TYPE=t_normal",
            "[ITEMDEF 0eee]", "DEFNAME=i_pool_b", "NAME=pool b", "TYPE=t_normal",
            "[CHARDEF 0013]", "DEFNAME=c_pool_a", "NAME=pool char a",
            "[CHARDEF 0014]", "DEFNAME=c_pool_b", "NAME=pool char b",
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

    private static string NewItem(string arg) =>
        (string?)typeof(SphereNet.Server.Program)
            .GetMethod("HandleServNewItem", Priv)!.Invoke(null, [arg]) ?? "";

    private static string NewNpc(string arg) =>
        (string?)typeof(SphereNet.Server.Program)
            .GetMethod("HandleServNewNpc", Priv)!.Invoke(null, [arg]) ?? "";

    /// <summary>NEWITEM picks one of the pool, every time, and it is one of the two
    /// that were named.</summary>
    [Fact]
    public void NewItemPicksFromThePool()
    {
        var world = Load();
        var made = new HashSet<ushort>();
        for (int i = 0; i < 30; i++)
        {
            string uid = NewItem("{ i_pool_a 1 i_pool_b 1 }");
            Assert.NotEqual("0", uid);
            var item = world.FindItem(new SphereNet.Core.Types.Serial(
                uint.Parse(uid, System.Globalization.NumberStyles.HexNumber)));
            Assert.NotNull(item);
            made.Add(item!.BaseId);
        }
        Assert.Equal([(ushort)0x0EED, (ushort)0x0EEE], made.OrderBy(x => x));
    }

    /// <summary>And the weights are read, not just the names. A pool weighted
    /// 999 to 1 that came out even would mean they were being ignored; 180 of 200 is
    /// far below what the weighted pick produces and far above what an even one
    /// could.</summary>
    [Fact]
    public void TheWeightsAreRead()
    {
        var world = Load();
        int heavy = 0;
        for (int i = 0; i < 200; i++)
        {
            string uid = NewItem("{ i_pool_a 999 i_pool_b 1 }");
            var item = world.FindItem(new SphereNet.Core.Types.Serial(
                uint.Parse(uid, System.Globalization.NumberStyles.HexNumber)));
            if (item!.BaseId == 0x0EED) heavy++;
        }
        Assert.True(heavy >= 180, $"the heavy member came up {heavy} times in 200");
    }

    /// <summary>NEWNPC reads a CHARDEF pool the same way. The two are told apart by
    /// the definition each NPC ended up on, not by name: a fresh NEWNPC carries the
    /// definition and reads its name through it.</summary>
    [Fact]
    public void NewNpcPicksFromThePool()
    {
        var world = Load();
        var names = new HashSet<string>();
        for (int i = 0; i < 30; i++)
        {
            string uid = NewNpc("{ c_pool_a 1 c_pool_b 1 }");
            Assert.NotEqual("0", uid);
            var ch = world.FindChar(new SphereNet.Core.Types.Serial(
                uint.Parse(uid, System.Globalization.NumberStyles.HexNumber)));
            Assert.NotNull(ch);
            names.Add($"{ch!.Name}/{ch.BodyId:X}/{ch.CharDefIndex:X}");
        }
        Assert.Equal(2, names.Count);
    }

    /// <summary>A plain defname still goes straight through - the pool branch is only
    /// for a value that opens with a brace.</summary>
    [Fact]
    public void APlainDefNameIsUntouched()
    {
        var world = Load();
        string uid = NewItem("i_pool_b");
        var item = world.FindItem(new SphereNet.Core.Types.Serial(
            uint.Parse(uid, System.Globalization.NumberStyles.HexNumber)));
        Assert.Equal((ushort)0x0EEE, item!.BaseId);
    }
}
