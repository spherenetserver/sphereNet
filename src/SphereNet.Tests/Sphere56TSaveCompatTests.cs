using Microsoft.Extensions.Logging;
using SphereNet.Persistence.Load;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Real-data compatibility check for the 0.56T-Release save family
/// (C:\56T\save). Skips cleanly when the directory is absent (CI); on the
/// dev/VDS box it asserts the loader ingests the world and reports which
/// keys fell through to SAVE.* tags so format gaps are visible, not silent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class Sphere56TSaveCompatTests
{
    private const string SaveDir = @"C:\56T\save";
    private const string ScriptsDir = @"C:\56T\scripts";
    private readonly ITestOutputHelper _out;

    public Sphere56TSaveCompatTests(ITestOutputHelper output) => _out = output;

    /// <summary>Field report: imported 56T spawner worldgems never spawned
    /// (MORE1 is a raw chardef defname the ItemDef-gated resolver dropped)
    /// and NPCs showed as "creature". Full-stack check against the REAL 56T
    /// script pack + save, mirroring the Program loader wiring.</summary>
    [Fact]
    public void SpawnersAndNpcNames_SurviveImport_WithThe56TScriptPack()
    {
        if (!Directory.Exists(ScriptsDir) || !File.Exists(Path.Combine(SaveDir, "sphereworld.scp")))
        {
            _out.WriteLine("SKIP: 56T scripts/save not found");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            lf.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        foreach (var file in Directory.EnumerateFiles(ScriptsDir, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        new SphereNet.Game.Definitions.DefinitionLoader(resources,
            new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

        var world = new SphereNet.Game.World.GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        // Mirror Program.cs loader wiring (incl. the ItemDef-gated
        // Item.ResolveDefName that pushes chardef MORE1 into the tag).
        var loader = new WorldLoader(lf);
        loader.ApplyCharDefFromName = (ch, defname) =>
            SphereNet.Game.Definitions.CharDefHelper.TryApplyDefName(ch, defname, resources);
        loader.ResolveBodyFromCharDefIndex = idx =>
            SphereNet.Game.Definitions.CharDefHelper.ResolveBodyId(idx, resources);
        loader.ResolveCharDef = defname =>
        {
            int idx = SphereNet.Game.Definitions.CharDefHelper.ResolveDefIndex(defname, resources);
            return idx != 0 ? SphereNet.Game.Definitions.CharDefHelper.ResolveBodyId(idx, resources) : (ushort)0;
        };
        loader.ResolveItemDef = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == SphereNet.Core.Enums.ResType.ItemDef)
            {
                var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        SphereNet.Game.Objects.Items.Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == SphereNet.Core.Enums.ResType.ItemDef)
                return (ushort)rid.Index;
            return 0;
        };
        try
        {
            loader.Load(world, SaveDir);

            // --- NPC names: an imported NPC without NAME= must render its
            // chardef name, not the "creature" fallback.
            var llama = world.FindChar(new SphereNet.Core.Types.Serial(0x6FD6));
            Assert.NotNull(llama);
            _out.WriteLine($"llama: Name='{llama!.Name}' display='{llama.GetDisplayName()}' defIdx=0x{llama.CharDefIndex:X}");
            Assert.NotEqual("creature", llama.GetDisplayName());

            // Diagnostic: what did the NPC-spawner worldgems load as?
            int dumped = 0;
            foreach (var obj in world.GetAllObjects())
            {
                if (dumped >= 5) break;
                if (obj is SphereNet.Game.Objects.Items.Item it &&
                    it.TryGetTag("MORE1_DEFNAME", out string? md))
                {
                    _out.WriteLine($"worldgem? base=0x{it.BaseId:X} type={it.ItemType} more1=0x{it.More1:X} tag={md}");
                    dumped++;
                }
            }
            if (dumped == 0)
                _out.WriteLine("no items carry MORE1_DEFNAME — the defname reached More1 or was dropped");

            // --- Spawners: replicate the (fixed) bootstrap pass.
            int npcSpawners = 0, itemSpawners = 0, deadSpawners = 0;
            foreach (var obj in world.GetAllObjects())
            {
                if (obj is not SphereNet.Game.Objects.Items.Item item) continue;
                if (item.BaseId != 0 && item.ItemType == SphereNet.Core.Enums.ItemType.Normal)
                {
                    var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(item.BaseId);
                    if (idef != null && idef.Type != SphereNet.Core.Enums.ItemType.Normal)
                        item.ItemType = idef.Type;
                }
                if (item.ItemType is SphereNet.Core.Enums.ItemType.SpawnChar
                    or SphereNet.Core.Enums.ItemType.SpawnItem)
                {
                    item.InitializeSpawnComponent(world, resources);
                    if (item.ItemType == SphereNet.Core.Enums.ItemType.SpawnChar)
                    {
                        npcSpawners++;
                        if (string.IsNullOrEmpty(item.SpawnChar?.GetSpawnDefName()))
                        {
                            deadSpawners++;
                            item.TryGetTag("MORE1_DEFNAME", out string? deadDef);
                            _out.WriteLine($"DEAD npc spawner uid=0x{item.Uid.Value:X} more1=0x{item.More1:X} tag='{deadDef}' at {item.X},{item.Y}");
                        }
                    }
                    else
                    {
                        itemSpawners++;
                        if ((item.SpawnItem?.ItemDefId ?? 0) == 0)
                            deadSpawners++;
                    }
                }
            }
            var ridLlama = resources.ResolveDefName("c_llama");
            _out.WriteLine($"rid c_llama: valid={ridLlama.IsValid} type={ridLlama.Type} idx=0x{ridLlama.Index:X}");
            var sample = world.GetAllObjects().OfType<SphereNet.Game.Objects.Items.Item>()
                .FirstOrDefault(i => i.ItemType == SphereNet.Core.Enums.ItemType.SpawnChar &&
                                     i.TryGetTag("MORE1_DEFNAME", out _));
            if (sample != null)
                _out.WriteLine($"sample post-init: more1=0x{sample.More1:X} spawnDef='{sample.SpawnChar?.GetSpawnDefName()}' comp={(sample.SpawnChar != null)}");
            _out.WriteLine($"spawners: npc={npcSpawners} item={itemSpawners} dead={deadSpawners}");
            Assert.True(npcSpawners > 100, $"expected the 56T NPC spawner fleet, got {npcSpawners}");
            Assert.True(itemSpawners >= 50, $"expected the mining-vein item spawners, got {itemSpawners}");
            // The defname fallback must leave no dead spawners — except the
            // shard's own data quirk: exactly one worldgem was saved with
            // MORE1=00 (a GM never configured it; uid 0x40001EF5).
            Assert.True(deadSpawners <= 1, $"dead spawners: {deadSpawners}");
        }
        finally
        {
            SphereNet.Game.Objects.Items.Item.ResolveDefName = null;
            SphereNet.Game.Objects.ObjBase.ResolveWorld = null;
        }
    }

    [Fact]
    public void Loads56TSaves_AndReportsUnhandledKeys()
    {
        if (!File.Exists(Path.Combine(SaveDir, "sphereworld.scp")))
        {
            _out.WriteLine($"SKIP: 56T save data not found at {SaveDir}");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var world = new SphereNet.Game.World.GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        var loader = new WorldLoader(lf);

        var (items, chars) = loader.Load(world, SaveDir);
        _out.WriteLine($"56T load: {items} items, {chars} chars");

        // The shard dump carries ~76k items / ~4.2k chars — a large shortfall
        // means whole sections stopped parsing.
        Assert.True(items > 70_000, $"expected >70k items, got {items}");
        Assert.True(chars > 4_000, $"expected >4k chars, got {chars}");

        // Spot-check a known player from spherechars.scp.
        var stigma = world.FindChar(new SphereNet.Core.Types.Serial(0x0F9E6));
        Assert.NotNull(stigma);
        Assert.Equal("Stigma", stigma!.Name);

        // Inventory of keys the loader could not map (parked as SAVE.* tags).
        var unhandled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Tally(SphereNet.Game.Objects.ObjBase obj)
        {
            foreach (var tag in obj.Tags.GetAll())
                if (tag.Key.StartsWith("SAVE.", StringComparison.OrdinalIgnoreCase))
                {
                    string key = tag.Key[5..];
                    unhandled[key] = unhandled.GetValueOrDefault(key) + 1;
                }
        }
        foreach (var obj in world.GetAllObjects()) Tally(obj);

        foreach (var kv in unhandled.OrderByDescending(k => k.Value).Take(25))
            _out.WriteLine($"  unhandled {kv.Key} x{kv.Value}");
        _out.WriteLine($"total unhandled key kinds: {unhandled.Count}");
    }
}
