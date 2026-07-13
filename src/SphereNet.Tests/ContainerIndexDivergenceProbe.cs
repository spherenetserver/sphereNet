using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ContainerIndexDivergenceProbe
{
    private readonly ITestOutputHelper _out;
    public ContainerIndexDivergenceProbe(ITestOutputHelper o) => _out = o;

    // Reproduces the "item disappeared from bag but .edit still shows it" report:
    // .edit reads Item._contents; the client render reads GameWorld._containerIndex.
    // Any item present in a parent's _contents but absent from GetContainerContents
    // is invisible to the client yet visible to .edit — exactly the symptom.
    [Fact]
    public void LoadRealServer_ReportsContentsVsIndexDivergence()
    {
        const string rootDir = @"C:\sphereNetServer";
        string scripts = Path.Combine(rootDir, "scripts");
        string mul = Path.Combine(rootDir, "mul");
        string save = Path.Combine(rootDir, "save");
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

        int containers = 0, divergentContainers = 0, missingFromIndex = 0, extraInIndex = 0, dupInIndex = 0;
        var examples = new List<string>();

        foreach (var container in world.GetAllObjects().OfType<Item>())
        {
            var contents = container.Contents.Where(c => !c.IsDeleted).ToList();
            if (contents.Count == 0) continue;
            containers++;

            var indexed = world.GetContainerContents(container.Uid).ToList();
            var indexedSet = new HashSet<Item>(indexed, ReferenceEqualityComparer.Instance);
            var contentsSet = new HashSet<Item>(contents, ReferenceEqualityComparer.Instance);

            var missing = contents.Where(c => !indexedSet.Contains(c)).ToList();     // in .edit, NOT on client
            var extra = indexed.Where(c => !contentsSet.Contains(c)).ToList();        // on client, NOT in .edit
            int dups = indexed.Count - indexedSet.Count;                             // double-listed on client

            if (missing.Count > 0 || extra.Count > 0 || dups > 0)
            {
                divergentContainers++;
                missingFromIndex += missing.Count;
                extraInIndex += extra.Count;
                dupInIndex += dups;
                if (examples.Count < 15 && missing.Count > 0)
                    examples.Add($"container 0x{container.Uid.Value:X8} base=0x{container.BaseId:X} " +
                        $"contents={contents.Count} indexed={indexed.Count} missing={missing.Count} " +
                        $"(first missing 0x{missing[0].Uid.Value:X8} base=0x{missing[0].BaseId:X})");
            }
        }

        _out.WriteLine($"containers with contents = {containers}");
        _out.WriteLine($"divergent containers    = {divergentContainers}");
        _out.WriteLine($"items in .edit but NOT on client (missing) = {missingFromIndex}");
        _out.WriteLine($"items on client but NOT in .edit (extra)    = {extraInIndex}");
        _out.WriteLine($"double-listed on client (dup)               = {dupInIndex}");
        foreach (var e in examples) _out.WriteLine("  " + e);

        // Classic-mode client renders each contained item at its (X,Y) pixel slot.
        // Two siblings sharing the same (X,Y) overlap: only the top one is visible,
        // the rest look "disappeared" while .edit still lists every one of them.
        int contWithOverlap = 0, overlappedItems = 0, zeroPos = 0;
        var overlapExamples = new List<string>();
        foreach (var container in world.GetAllObjects().OfType<Item>())
        {
            var contents = container.Contents.Where(c => !c.IsDeleted).ToList();
            if (contents.Count < 2) continue;
            var byPos = new Dictionary<(int, int), int>();
            foreach (var c in contents)
            {
                if (c.X == 0 && c.Y == 0) zeroPos++;
                var key = ((int)c.X, (int)c.Y);
                byPos.TryGetValue(key, out int n);
                byPos[key] = n + 1;
            }
            int overlapHere = byPos.Values.Where(n => n > 1).Sum(n => n - 1);
            if (overlapHere > 0)
            {
                contWithOverlap++;
                overlappedItems += overlapHere;
                if (overlapExamples.Count < 15)
                {
                    var worst = byPos.OrderByDescending(kv => kv.Value).First();
                    overlapExamples.Add($"container 0x{container.Uid.Value:X8} base=0x{container.BaseId:X} " +
                        $"items={contents.Count} worstPos=({worst.Key.Item1},{worst.Key.Item2})x{worst.Value}");
                }
            }
        }
        _out.WriteLine($"--- POSITION OVERLAP (classic-mode hidden items) ---");
        _out.WriteLine($"containers with overlapping sibling positions = {contWithOverlap}");
        _out.WriteLine($"items hidden behind a sibling (overlap)       = {overlappedItems}");
        _out.WriteLine($"contained items at (0,0)                      = {zeroPos}");
        foreach (var e in overlapExamples) _out.WriteLine("  " + e);

        // The load path must keep _contents (what .edit reads) and the container
        // reverse index (what the client's open-container send reads) in lockstep;
        // any missing entry is an item the player can't see but the server still
        // holds — the reported "disappeared from bag" symptom.
        Assert.Equal(0, missingFromIndex);
        Assert.Equal(0, extraInIndex);
        Assert.Equal(0, dupInIndex);
    }
}
