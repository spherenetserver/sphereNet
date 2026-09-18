using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// ID= turns an item into another item; DISPID= only changes how it looks.
///
/// Upstream's IC_ID calls SetID, which re-points the base definition and takes the new
/// type with it (SetBaseID, CItem.cpp:2128-2129) before changing the graphic. It does
/// not run a creation script over an object already in the world.
///
/// The write was refused here, so the pack's decorations and levers - which write ID=
/// in their TIMER, DCLICK, STEP and EQUIP bodies to become something else - stayed as
/// they were.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemReIdTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_reid_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Item.CreateTriggerHook = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private int _createFirings;

    private GameWorld Load()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "r.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0e75]", "DEFNAME=i_reid_pack", "NAME=pack", "TYPE=t_container",
            "[ITEMDEF 01509]", "DEFNAME=i_reid_lever", "NAME=lever", "TYPE=t_switch",
            "ON=@Create", "MOREX=7",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        _createFirings = 0;
        Item.CreateTriggerHook = it =>
        {
            _createFirings++;
            dispatcher.FireItemTrigger(it, ItemTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        };
        return world;
    }

    /// <summary>A named definition re-bases the item: graphic and type both follow.</summary>
    [Fact]
    public void ANamedDefinitionRebasesTheItem()
    {
        var world = Load();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x0E75);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        Assert.Equal(ItemType.Container, it.ItemType);

        Assert.True(it.TrySetProperty("ID", "i_reid_lever"));

        Assert.Equal((ushort)0x1509, it.BaseId);
        Assert.Equal(ItemType.Switch, it.ItemType);
    }

    /// <summary>DISPID stops at the graphic - the item is still what it was.</summary>
    [Fact]
    public void DispIdOnlyChangesTheLook()
    {
        var world = Load();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x0E75);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));

        Assert.True(it.TrySetProperty("DISPID", "01509"));

        // DISPID reads back as the defname of whatever it now looks like.
        Assert.True(it.TryGetProperty("DISPID", out string look));
        Assert.Equal("i_reid_lever", look);
        Assert.Equal(ItemType.Container, it.ItemType);   // but it is still a container
    }

    /// <summary>Re-basing does NOT run the new definition's @Create over an object
    /// already in the world, which is what upstream's SetID leaves alone.</summary>
    [Fact]
    public void RebasingDoesNotRunACreationScript()
    {
        var world = Load();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x0E75);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        int before = _createFirings;

        Assert.True(it.TrySetProperty("ID", "i_reid_lever"));

        Assert.Equal(before, _createFirings);
        Assert.True(it.TryGetProperty("MOREX", out string morex));
        Assert.Equal("0", morex);        // the lever's @Create would have set 7
    }

    /// <summary>A name of its own survives the change - upstream re-points the base
    /// without touching the object's name.</summary>
    [Fact]
    public void ANameOfItsOwnSurvives()
    {
        var world = Load();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x0E75);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        it.TrySetProperty("NAME", "Bob's box");

        it.TrySetProperty("ID", "i_reid_lever");

        Assert.True(it.TryGetProperty("NAME", out string name));
        Assert.Equal("Bob's box", name);
    }

    /// <summary>A bare graphic with no definition behind it changes the look, because
    /// that is all there is to change.</summary>
    [Fact]
    public void ABareGraphicJustChangesTheLook()
    {
        var world = Load();
        var it = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(it, 0x0E75);
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));

        Assert.True(it.TrySetProperty("ID", "04321"));

        // Nothing is defined at that graphic, so it reads back as the bare number.
        Assert.True(it.TryGetProperty("DISPID", out string look));
        Assert.Equal("04321", look);
    }
}
