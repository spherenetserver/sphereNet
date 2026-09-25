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
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The creation fixes, against the shard's own scripts rather than a probe pack.
///
/// A creature's rolled stats and the spellbook filled by the line after an ITEM= were
/// each written against a two-line script that showed the shape. This runs them against
/// the real thing, where those shapes sit among five hundred other lines in whatever
/// order the pack author used - the difference between "works on my example" and "works
/// on their data".
///
/// c_icer is the shard's own definition: STR={96 125} and the rest as ranges, then
/// ITEMNEWBIE=i_spellbook followed by ADDSPELL=s_paralyze - a spell named, not numbered,
/// on the line after the item that receives it.
///
/// Skips cleanly when the shard's scripts are not on this machine.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LivePackCreationTests(ITestOutputHelper outp) : IDisposable
{
    private const string PackRoot = @"C:\sphereNetServer";

    public void Dispose() => SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;

    // Read once for the whole class: it is nineteen hundred files.
    private static (GameWorld World, TriggerDispatcher Dispatcher, ResourceHolder Resources)? s_shard;

    /// <summary>Every expression the interpreter could not resolve while this class
    /// ran, in the order they were reported.</summary>
    private static readonly List<string> s_unresolved = [];

    private static (GameWorld World, TriggerDispatcher Dispatcher, ResourceHolder Resources) Shard()
    {
        s_shard ??= LoadShard();
        var shard = s_shard.Value;

        // The engine statics are cleared between tests - the definition tables among
        // them - so the definitions are re-published from the resources that are
        // already parsed, and the hooks re-pointed. Without this the second test finds
        // no ITEMDEF behind i_spellbook and the creature is given nothing.
        new DefinitionLoader(shard.Resources, new SpellRegistry()).LoadAll();

        ObjBase.ResolveWorld = () => shard.World;
        Item.ResolveWorld = () => shard.World;
        Item.ResolveDefName = defname =>
        {
            var rid = shard.Resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
            var d = DefinitionLoader.GetItemDef(rid.Index);
            return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)rid.Index;
        };
        Item.CreateTriggerHook = it => shard.Dispatcher.FireItemTrigger(it, ItemTrigger.Create,
            new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            shard.Dispatcher.FireCharTrigger(npc, CharTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
        return shard;
    }

    private static (GameWorld World, TriggerDispatcher Dispatcher, ResourceHolder Resources) LoadShard()
    {
        var lf = LoggerFactory.Create(_ => { });
        string scripts = Path.Combine(PackRoot, "scripts");
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (string file in Directory.EnumerateFiles(scripts, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var expr = new ExpressionParser
        {
            DebugUnresolved = true,
            DiagnosticLogger = m => s_unresolved.Add(m)
        };
        var interpreter = new ScriptInterpreter(expr, lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        return (world, dispatcher, resources);
    }

    private static SphereNet.Game.Objects.Characters.Character SpawnIcer(GameWorld world, ResourceHolder res)
    {
        var rid = res.ResolveDefName("c_icer");
        Assert.True(rid.IsValid, "c_icer is not in this shard's scripts");

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var icer = spawn.SpawnSpecific(rid.Index);
        Assert.NotNull(icer);
        return icer!;
    }

    /// <summary>Its stats are rolled inside the ranges the shard declared, not the
    /// zeros an unparsed "{96 125}" would leave.</summary>
    [Fact]
    public void AShardMonstersRolledStatsLandInTheirRanges()
    {
        if (Gate.Missing(outp, "live shard (scripts + mul + save)", !Directory.Exists(PackRoot))) return;
        var (world, _, resources) = Shard();
        var icer = SpawnIcer(world, resources);

        outp.WriteLine($"c_icer str={icer.Str} dex={icer.Dex} int={icer.Int} " +
                       $"tactics={icer.GetSkill(SkillType.Tactics)}");
        Assert.InRange(icer.Str, 96, 125);
        Assert.InRange(icer.Dex, 105, 130);
        Assert.InRange(icer.Int, 140, 165);
        Assert.InRange(icer.GetSkill(SkillType.Tactics), 500, 700);   // TACTICS={50.0 70.0}
    }

    /// <summary>
    /// Every definition the shard declares materialises, and the interpreter
    /// understands every line it runs on the way.
    ///
    /// This is the creation surface as a whole rather than one creature: four hundred
    /// CHARDEFs spawned and fifteen hundred ITEMDEFs made, with the interpreter set to
    /// report anything it cannot resolve. A line the engine does not understand is
    /// silent in production - it stores nothing and says nothing - so the report is the
    /// only place it shows.
    ///
    /// Names the PACK defines are allowed through: the live server resolves those with
    /// its own constant resolver, which is not wired into a test harness, so
    /// &lt;statf_invul&gt; arrives here unresolved while the real server answers it from
    /// the defname table. Anything the pack does NOT define is a gap in the engine.
    /// </summary>
    [Fact]
    public void TheShardsOwnDefinitionsAllMaterialise()
    {
        if (Gate.Missing(outp, "live shard (scripts + mul + save)", !Directory.Exists(PackRoot))) return;
        var (world, _, resources) = Shard();
        s_unresolved.Clear();

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(120, 120, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };

        int creatures = 0, items = 0, restocked = 0, lootItems = 0, lootGold = 0;
        var threw = new List<string>();
        // Snapshot first: a definition can be resolved lazily while a trigger runs,
        // which adds to the table being walked.
        foreach (var kv in DefinitionLoader.AllCharDefs.Take(400).ToList())
        {
            try
            {
                var npc = spawn.SpawnSpecific(kv.Key);
                if (npc == null) continue;
                creatures++;

                // The loot pass is the second biggest body the packs write - four
                // hundred and fifty blocks - and it runs on the creature that was just
                // made, so it belongs in the same sweep.
                var (_, dispatcher, _) = Shard();
                dispatcher.FireCharTrigger(npc, CharTrigger.NPCRestock,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                restocked++;

                foreach (var loot in npc.Backpack?.Contents ?? Enumerable.Empty<Item>())
                {
                    lootItems++;
                    if (loot.ItemType == ItemType.Gold) lootGold += loot.Amount;
                }
            }
            catch (Exception ex) { threw.Add($"CHARDEF 0x{kv.Key:X}: {ex.GetType().Name} {ex.Message}"); }
        }
        foreach (var kv in DefinitionLoader.AllItemDefs.Take(1500).ToList())
        {
            try
            {
                var made = world.CreateItem();
                if (ItemDefHelper.ApplyInstanceMetadata(made, kv.Key)) items++;
            }
            catch (Exception ex) { threw.Add($"ITEMDEF 0x{kv.Key:X}: {ex.GetType().Name} {ex.Message}"); }
        }

        outp.WriteLine($"{creatures} creatures spawned, {restocked} restocked, " +
                       $"{items} items made, {s_unresolved.Count} unresolved");
        outp.WriteLine($"loot: {lootItems} items, {lootGold} gold");

        // The shard's loot lines name amounts as ranges - "ITEM=i_gold,{750 900}" and
        // a thousand more like it. When the range does not roll, each line yields one
        // of the thing instead, and this same walk produces a few hundred coins rather
        // than tens of thousands. The bound is far below what the pack actually pays
        // out and far above what a collapsed amount could reach.
        Assert.True(lootGold > 10_000,
            $"four hundred creatures dropped {lootGold} gold between them, which is the " +
            "shape of amounts that stopped rolling");
        Assert.True(creatures > 100, $"only {creatures} creatures spawned - the sweep measured almost nothing");
        Assert.True(items > 500, $"only {items} items made - the sweep measured almost nothing");
        Assert.True(threw.Count == 0, "creation threw: " + string.Join(" | ", threw.Take(5)));

        // Keep only what the pack does not define for itself.
        var gaps = new List<string>();
        foreach (string line in s_unresolved.Distinct())
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"unresolved <([^>]+)>");
            string token = m.Success ? m.Groups[1].Value : "";
            // A [DEFNAME] constant arrives as a DefName resource whose index IS its
            // value, which is what the live server's constant resolver reads; the
            // text-valued table is a different store and does not carry these.
            if (token.Length > 0 &&
                (resources.ResolveDefName(token).IsValid || resources.TryGetDefValue(token, out _)))
                continue;
            gaps.Add(line);
        }
        Assert.True(gaps.Count == 0,
            "the engine did not understand these, and the pack does not define them: " +
            string.Join(" | ", gaps.Take(10)));
    }

    /// <summary>And it carries the spellbook its definition fills: the ADDSPELL on the
    /// line after ITEMNEWBIE=i_spellbook, naming the spell rather than numbering it.</summary>
    [Fact]
    public void AShardMonsterCarriesTheSpellbookItsDefinitionFills()
    {
        if (Gate.Missing(outp, "live shard (scripts + mul + save)", !Directory.Exists(PackRoot))) return;
        var (world, _, resources) = Shard();
        var icer = SpawnIcer(world, resources);

        var book = icer.Backpack?.Contents.FirstOrDefault(i => i.ItemType == ItemType.Spellbook);
        for (int layer = 0; layer < 32 && book == null; layer++)
            if (icer.GetEquippedItem((Layer)layer) is { ItemType: ItemType.Spellbook } worn)
                book = worn;
        Assert.NotNull(book);

        // Paralyze is spell 38: bit 37 (spell n at bit n-1, CItem.cpp:4485), so it lives
        // in the second word.
        Assert.True(book!.TryGetProperty("MORE2", out string more2));
        uint high = Convert.ToUInt32(more2, 16);
        int paralyze = (int)SpellType.Paralyze;
        outp.WriteLine($"c_icer spellbook MORE2=0x{high:X}, paralyze is bit {paralyze - 33}");
        Assert.NotEqual(0u, high & (1u << (paralyze - 33)));
        Assert.True(book.ContainsSpell(paralyze));
    }
}
