using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class RealServerLoadProbe
{
    private readonly ITestOutputHelper _out;
    public RealServerLoadProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void LoadRealServer_CountsNakedNpcsAndGroundItems()
    {
        const string root = @"C:\sphereNetServer";
        string scripts = Path.Combine(root, "scripts");
        string mul = Path.Combine(root, "mul");
        string save = Path.Combine(root, "save");
        if (!Directory.Exists(scripts) || !File.Exists(Path.Combine(mul, "tiledata.mul")) ||
            !File.Exists(Path.Combine(save, "sphereworld.scp")))
        {
            _out.WriteLine("real server dir not available");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string f in ScriptResourceManifest.Resolve(scripts))
            resources.LoadResourceFile(f);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = ResolveGraphic;

        ushort ResolveGraphic(string defname)
        {
            var rid = resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        }

        var loader = new WorldLoader(lf);
        loader.ResolveItemDef = ResolveGraphic;
        loader.ResolveCharDef = defname =>
        {
            int idx = CharDefHelper.ResolveDefIndex(defname, resources);
            return idx != 0 ? CharDefHelper.ResolveBodyId(idx, resources) : (ushort)0;
        };
        loader.ApplyCharDefFromName = (ch, defname) => CharDefHelper.TryApplyDefName(ch, defname, resources);
        loader.ResolveEquipLayerFromTile = baseId => map.GetItemTileData(baseId).Quality;

        var (items, chars) = loader.Load(world, save);
        _out.WriteLine($"loaded items={items} chars={chars}");

        // Layers that count as "worn clothing/armor/weapon" for a dressed NPC.
        var wornLayers = new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 13, 17, 20, 22, 23, 24 };
        int npcs = 0, dressed = 0, humanoid = 0;
        foreach (var ch in world.GetAllObjects().OfType<Character>())
        {
            npcs++;
            bool isHuman = ch.BodyId is 0x0190 or 0x0191 or 0x025D or 0x025E;
            if (!isHuman) continue;
            humanoid++;
            if (wornLayers.Any(L => ch.GetEquippedItem((Layer)L) != null))
                dressed++;
        }

        // Items sitting on the ground (no container) — the "dropped under the vendor" symptom.
        int groundItems = world.GetAllObjects().OfType<Item>().Count(i => !i.ContainedIn.IsValid);

        _out.WriteLine($"NPCs total={npcs} humanoid={humanoid} dressed={dressed} nakedHumanoid={humanoid - dressed}");
        _out.WriteLine($"ground items (no container)={groundItems}");

        // With correct tiledata layer derivation every stock humanoid NPC keeps its
        // worn gear; a regression that drops it would show up as naked humanoids.
        Assert.True(humanoid > 0);
        Assert.Equal(humanoid, dressed);
    }
}
