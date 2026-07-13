using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpawnIdxProbe
{
    private readonly ITestOutputHelper _o;
    public SpawnIdxProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    public void SpawnOfNonNumericChardef_ResolvesRealBody_NotFFFF()
    {
        const string scripts = @"C:\sphereNetServer\scripts";
        if (!Directory.Exists(scripts)) { _o.WriteLine("no scripts"); return; }
        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts)) res.LoadResourceFile(f);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        // c_alchemist has a synthetic chardef index above 0xFFFF — the case that
        // previously clamped to 0xFFFF and spawned a nameless body-0xFFFF NPC.
        var rid = res.ResolveDefName("c_alchemist");
        _o.WriteLine($"c_alchemist index=0x{rid.Index:X} (>0xFFFF={rid.Index > 0xFFFF})");
        Assert.True(rid.Index > 0xFFFF, "expected a synthetic index for the regression to be meaningful");

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var spawnItem = world.CreateItem();
        spawnItem.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawnItem, new Point3D(1000, 1000, 0, 0));
        var comp = new SpawnComponent(spawnItem, world);
        comp.SetFromDefName("c_alchemist", res);

        var spawned = comp.SpawnSpecific(rid.Index);
        _o.WriteLine($"spawned body=0x{spawned?.BodyId:X} name='{spawned?.Name}'");

        Assert.NotNull(spawned);
        Assert.NotEqual(0xFFFF, spawned!.BodyId);
        Assert.DoesNotContain("Spawn_", spawned.Name ?? "");
    }
}
