using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class ScriptPackCorpusRunnerTests
{
    [Fact]
    public void GoldenScriptFixtures_RunTriggerCorpus()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string fixtureDir = Path.Combine(root, "tests", "fixtures", "scripts");
        var files = Directory.GetFiles(fixtureDir, "*.scp", SearchOption.TopDirectoryOnly);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Name = "FixtureProbe";
        ch.IsPlayer = true;
        ch.Events.Add(stack.Resources.ResolveDefName("e_combat_hittry_probe"));
        ch.Events.Add(stack.Resources.ResolveDefName("e_combat_attack_probe"));
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var dispatchArgs = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch };
        stack.Dispatcher.FireCharTrigger(ch, CharTrigger.LogIn, dispatchArgs);
        stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Attack, dispatchArgs);
        stack.Dispatcher.FireCharTrigger(ch, CharTrigger.HitTry, dispatchArgs);

        Assert.True(ch.TryGetTag("GLOBAL_LOGIN", out var globalLogin) && globalLogin == "1");
        Assert.True(ch.TryGetTag("GLOBAL_ATTACK", out var globalAttack) && globalAttack == "1");
        Assert.True(ch.TryGetTag("ATTACK_SEEN", out var attackSeen) && attackSeen == "1");
        Assert.True(ch.TryGetTag("HITTRY_SEEN", out var hitTrySeen) && hitTrySeen == "1");

        var npc = world.CreateCharacter();
        int npcDefIndex = CharDefHelper.ResolveDefIndex("c_npc_parity_probe", stack.Resources);
        Assert.NotEqual(0, npcDefIndex);
        npc.CharDefIndex = npcDefIndex;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        var npcArgs = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch };
        stack.Dispatcher.FireCharTrigger(npc, CharTrigger.NPCActFight, npcArgs);
        stack.Dispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtChar, npcArgs);

        var npcLink = stack.Resources.GetResource(ResType.CharDef, npcDefIndex);
        Assert.NotNull(npcLink);
        stack.Runner.RunTriggerByName(npcLink!, "NPCActFight", npc, null, new TriggerArgs { ArgString = "" });
        stack.Runner.RunTriggerByName(npcLink!, "NPCLookAtChar", npc, null, new TriggerArgs { ArgString = "" });

        Assert.True(npc.TryGetTag("NPC_ACT_FIGHT", out var fightSeen) && fightSeen == "1");
        Assert.True(npc.TryGetTag("NPC_LOOK_CHAR", out var lookSeen) && lookSeen == "1");

        var item = world.CreateItem();
        int itemDefIndex = TemplateEngine.ResolveItemDefIndex(stack.Resources, "i_parity_container");
        Assert.NotEqual(0, itemDefIndex);
        item.BaseId = 0x0E7D;
        item.SetTag("SCRIPTDEF", itemDefIndex.ToString());
        item.ItemType = ItemType.Container;
        world.PlaceItem(item, ch.Position);

        var itemArgs = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, ItemSrc = item };
        stack.Dispatcher.FireItemTrigger(item, ItemTrigger.DClick, itemArgs);

        var typeLink = stack.Resources.GetResource(stack.Resources.ResolveDefName("t_parity_container"));
        Assert.NotNull(typeLink);
        stack.Runner.RunTriggerByName(typeLink!, "DClick", item, null, new TriggerArgs { ArgString = "" });

        Assert.True(item.TryGetTag("TYPEDEF_DCLICK", out var dclickSeen) && dclickSeen == "1");
    }

    [Fact]
    public void GoldenScriptFixtures_GlobalFunctionSweepReportsHandledFunctions()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string fixtureDir = Path.Combine(root, "tests", "fixtures", "scripts");
        var files = Directory.GetFiles(fixtureDir, "*.scp", SearchOption.TopDirectoryOnly);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var args = new TriggerArgs { ArgString = "" };
        int handled = 0;
        foreach (string function in new[] { "f_onchar_login", "f_onchar_attack", "f_onitem_dclick" })
        {
            if (stack.Runner.TryRunFunction(function, ch, null, args, out _))
                handled++;
        }

        Assert.Equal(2, handled);
    }
}
