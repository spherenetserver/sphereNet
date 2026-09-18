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

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
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

        // Paralyze is spell 38, so its bit lives in the second word.
        Assert.True(book!.TryGetProperty("MORE2", out string more2));
        uint high = Convert.ToUInt32(more2, 16);
        int paralyze = (int)SpellType.Paralyze;
        outp.WriteLine($"c_icer spellbook MORE2=0x{high:X}, paralyze is bit {paralyze - 32}");
        Assert.NotEqual(0u, high & (1u << (paralyze - 32)));
    }
}
