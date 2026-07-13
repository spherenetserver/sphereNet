using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// An item spawner that targets an ITEMDEF with a non-numeric header ([ITEMDEF
/// i_xxx], keyed by a synthetic index above 0xFFFF) must keep the FULL index.
/// ItemSpawnComponent.ItemDefId was a ushort, so the parked MORE1_DEFNAME resolved
/// to (ushort)rid.Index — a truncated, wrong def — and the spawner produced the
/// wrong item or nothing (the item-side twin of the Spawn_FFFF char bug).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemSpawnerSyntheticIndexTests
{
    private readonly ITestOutputHelper _out;
    public ItemSpawnerSyntheticIndexTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void ItemSpawner_TargetingNonNumericItemDef_KeepsFullIndex()
    {
        const string scripts = @"C:\sphereNetServer\scripts";
        if (!Directory.Exists(scripts)) { _out.WriteLine("no scripts"); return; }

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts)) res.LoadResourceFile(f);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        const string defName = "i_pouch_trapped"; // synthetic index > 0xFFFF, Type=Script
        var rid = res.ResolveDefName(defName);
        Assert.True(rid.IsValid && rid.Type == ResType.ItemDef && rid.Index > 0xFFFF,
            "test relies on a synthetic-index itemdef");
        _out.WriteLine($"{defName}: index=0x{rid.Index:X} (truncated would be 0x{(ushort)rid.Index:X})");

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnItem;
        // Legacy item spawners park a named target in MORE1_DEFNAME (MORE1 can't
        // hold a 24-bit synthetic index as a 16-bit graphic).
        spawner.SetTag("MORE1_DEFNAME", defName);
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));

        spawner.InitializeSpawnComponent(world, res);

        Assert.NotNull(spawner.SpawnItem);
        _out.WriteLine($"resolved ItemDefId = 0x{spawner.SpawnItem!.ItemDefId:X}");
        // The fix: the full 24-bit index survives instead of being masked to ushort.
        Assert.Equal(rid.Index, spawner.SpawnItem.ItemDefId);
        Assert.True(spawner.SpawnItem.ItemDefId > 0xFFFF);
    }
}
