using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
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
/// Sphere 56T custom-version compatibility: a monster's loot is made by a global NPC
/// event's @DeathCorpse - SERV.NEWITEM of the TEMPLATE named in the chardef's
/// TAG.LOOT, the bag moved into the corpse (ARGO), its contents FORCONT-moved out and
/// the bag removed. Locked end to end for a creature read back from a 56T world record
/// (no NAME, no EVENTS line, the TAG only on the CHARDEF) and killed the way CHV_KILL
/// does it (a 10000-point blow on the attacker list, then the death).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DeathCorpseScriptLootTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_corpseloot_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void GmKilledMonsterFromA56TRecordGetsItsTemplateLoot()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0e75]", "DEFNAME=i_backpack", "TYPE=t_container",
            "[ITEMDEF 0eed]", "DEFNAME=i_gold", "TYPE=t_gold",
            "[ITEMDEF 02006]", "DEFNAME=i_corpse", "TYPE=t_corpse",
            "[ITEMDEF 01078]", "DEFNAME=i_hides_test", "TYPE=t_normal",
            "[TEMPLATE loot_test_dragon]", "CONTAINER=i_backpack",
            "ITEM=i_gold,{2500 3500}", "ITEM=i_hides_test,{1 2}",
            "[CHARDEF 069]", "DEFNAME=c_test_dragon", "NAME=Greater Dragon",
            "TEVENTS=e_test_carnivores", "tag.loot loot_test_dragon",
            "ON=@Create", "NPC=brain_monster",
            "[EVENTS e_test_carnivores]",
            "[EVENTS e_test_npc_monster]",
            "on=@deathcorpse",
            "if !<isempty <tag.loot>>",
            "\tserv.newitem <tag0.loot>",
            "\tnew.cont=<argo>",
            "\tlocal.container=<argo>",
            "\tif (<new>) && (<new.type> == t_container)",
            "\t\tforcont <new> 0",
            "\t\t\tcont=<local.container>",
            "\t\tendfor",
            "\t\tnew.remove",
            "\tendif",
            "endif",
        });

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.GlobalPetEvents.Add(DefinitionLoader.ResolveEventName("e_test_npc_monster", resources));
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        // SERV.NEWITEM and the NEW reference go through the server, as in production.
        var program = typeof(SphereNet.Server.Program);
        var fResources = program.GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fWorld = program.GetField("_world", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? oldResources = fResources.GetValue(null), oldWorld = fWorld.GetValue(null);
        fResources.SetValue(null, resources);
        fWorld.SetValue(null, world);
        try
        {
            var resolve = program.GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
            interpreter.ServerPropertyResolver = s => (string?)resolve.Invoke(null, [s]);
            interpreter.ResolveObjectRef = (obj, head) =>
                head.Equals("NEW", StringComparison.OrdinalIgnoreCase)
                    ? world.FindObject(world.LastNewObject)
                    : obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;

            string saveDir = Path.Combine(_dir, "save");
            Directory.CreateDirectory(saveDir);
            File.WriteAllLines(Path.Combine(saveDir, "sphereworld.scp"), new[]
            {
                "[SPHERE]", "TITLE=Sphere World Script", "VERSION=0.56T-Release", "",
                "[WORLDCHAR c_test_dragon]", "SERIAL=02ef95", "NPC=10", "P=100,100",
                "FLAGS=031000000", "OSTR=795", "HITS=795", "",
                "[WORLDITEM i_backpack]", "SERIAL=04002ef86", "CONT=02ef95", "",
                "[EOF]",
            });
            var loader = new SphereNet.Persistence.Load.WorldLoader(lf)
            {
                ApplyCharDefFromName = (ch, defname) => CharDefHelper.TryApplyDefName(ch, defname, resources),
                ResolveBodyFromCharDefIndex = idx => CharDefHelper.ResolveBodyId(idx, resources),
                ResolveItemDef = defname =>
                {
                    var rid = resources.ResolveDefName(defname);
                    return rid.IsValid && rid.Type == ResType.ItemDef ? (ushort)rid.Index : (ushort)0;
                },
                ResolveItemDefFullIndex = defname =>
                {
                    var rid = resources.ResolveDefName(defname);
                    return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
                },
            };
            loader.Load(world, saveDir);

            var dragon = world.FindChar(new Serial(0x2EF95));
            Assert.NotNull(dragon);
            Assert.Equal("", dragon!.Name);                       // read back nameless
            Assert.True(dragon.TryGetProperty("TAG.LOOT", out string loot));
            Assert.Equal("loot_test_dragon", loot);                // from the CHARDEF

            var gm = world.CreateCharacter();
            gm.IsPlayer = true;
            gm.PrivLevel = PrivLevel.GM;
            world.PlaceCharacter(gm, new Point3D(101, 100, 0, 0));

            var death = new DeathEngine(world) { TriggerDispatcher = dispatcher };
            dragon.RecordAttack(gm.Uid, 10000);
            var corpse = death.ProcessDeath(dragon, gm);

            Assert.NotNull(corpse);
            var gold = corpse!.Contents.SingleOrDefault(i => i.BaseId == 0x0EED);
            Assert.NotNull(gold);
            Assert.InRange(gold!.Amount, 2500, 3500);
            Assert.Contains(corpse.Contents, i => i.BaseId == 0x1078);
            var bag = world.FindItem(world.LastNewItem);            // the template's bag
            Assert.True(bag == null || bag.IsDeleted);
        }
        finally
        {
            fResources.SetValue(null, oldResources);
            fWorld.SetValue(null, oldWorld);
        }
    }
}
