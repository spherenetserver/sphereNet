using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// TYPEDEF is a reference head on every object: its own base definition.
///
/// Upstream lists it beside ROOM, SECTOR, TOPOBJ and SPAWNITEM (sm_szRefKeys,
/// CObjBase.cpp:899) and resolves it to Base_GetDef (CObjBase.cpp:941), so
/// &lt;TYPEDEF.TDATA1&gt; asks the ITEMDEF or CHARDEF the thing was made from rather
/// than the thing itself. Nothing answered it here, so every such read collapsed to
/// the unresolved "0" - and, being a read, it did so silently.
///
/// It goes through the host the way ROOM does: the definition tables and the reader
/// for their fields already live there. A second reader would only be something for
/// the first to disagree with.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TypeDefRefHeadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_td_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    /// <summary>Publish the definitions and point the host's resolver at them.</summary>
    private void Load(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        typeof(SphereNet.Server.Program)
            .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, resources);
    }

    /// <summary>Run one read on <paramref name="target"/> with the host wired in, the
    /// way the server wires it at startup.</summary>
    private static string Read(ObjBase target, string expr)
    {
        var resolve = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Interpreter.ServerPropertyResolver = p => (string?)resolve.Invoke(null, [p]);
        stack.Interpreter.Execute([new ScriptKey("TAG.OUT", expr)], target, null,
            new TriggerArgs(), new ScriptScope());
        target.TryGetProperty("TAG.OUT", out string v);
        return v;
    }

    /// <summary>Point the host at a world too - HandleTypeDefGet finds the object that
    /// asked by its uid.</summary>
    private static SphereNet.Game.World.GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        typeof(SphereNet.Server.Program)
            .GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, world);
        return world;
    }

    /// <summary>An item asks its ITEMDEF.</summary>
    [Fact]
    public void AnItemReadsItsItemDef()
    {
        Load("[ITEMDEF 04321]" + Nl + "DEFNAME=i_probe_thing" + Nl +
             "NAME=Probe Thing" + Nl + "TYPE=t_gem" + Nl + "TDATA1=077" + Nl + "WEIGHT=3" + Nl);
        var world = World();
        var it = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(it, 0x4321));
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));

        Assert.Equal("Probe Thing", Read(it, "<TYPEDEF.NAME>"));
        Assert.Equal("i_probe_thing", Read(it, "<TYPEDEF.DEFNAME>"));
        // Written 077, so hex - the definition keeps it and hands it back the same way.
        Assert.Equal("077", Read(it, "<TYPEDEF.TDATA1>"));
    }

    /// <summary>The definition answers even when the instance has been changed away
    /// from it - which is the whole point of asking the definition.</summary>
    [Fact]
    public void TheDefinitionAnswersNotTheInstance()
    {
        Load("[ITEMDEF 04321]" + Nl + "DEFNAME=i_probe_thing" + Nl +
             "NAME=Probe Thing" + Nl + "TYPE=t_gem" + Nl + "TDATA1=077" + Nl);
        var world = World();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x4321);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        it.TrySetProperty("NAME", "renamed on the instance");

        Assert.Equal("renamed on the instance", Read(it, "<NAME>"));
        Assert.Equal("Probe Thing", Read(it, "<TYPEDEF.NAME>"));
    }

    /// <summary>And a character asks its CHARDEF.</summary>
    [Fact]
    public void ACharacterReadsItsCharDef()
    {
        Load("[CHARDEF 0211]" + Nl + "DEFNAME=c_probe_guy" + Nl +
             "NAME=Probe Guy" + Nl + "ID=0190" + Nl);
        var world = World();
        var ch = world.CreateCharacter();
        ch.CharDefIndex = 0x211;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.Equal("Probe Guy", Read(ch, "<TYPEDEF.NAME>"));
    }

    /// <summary>A field the definition does not name reads as nothing, not as a
    /// stray value from somewhere else.</summary>
    [Fact]
    public void AnUnknownFieldReadsZero()
    {
        Load("[ITEMDEF 04321]" + Nl + "DEFNAME=i_probe_thing" + Nl + "NAME=Probe Thing" + Nl);
        var world = World();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x4321);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));

        Assert.Equal("0", Read(it, "<TYPEDEF.NOSUCHFIELD>"));
    }
}
