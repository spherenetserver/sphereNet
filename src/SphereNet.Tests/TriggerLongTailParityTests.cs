using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;
using GameArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// Trigger dispatch contracts that follow the reference's CChar::OnTrigger /
/// CItem::OnTrigger: the TRIGRET a chain hands back (RETURN 0 is TRIGRET_RET_FALSE),
/// the @char*/@item* mirror as a full trigger on the acting character, the @Create and
/// CHARDEF-only orders, and a set of fire sites whose arguments or return handling
/// follow the reference.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TriggerLongTailParityTests
{
    private static ScriptRuntimeStack Load(string script, bool definitions = false)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"trig-longtail-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        if (definitions)
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        return stack;
    }

    // ---------------------------------------------------------------- TRIGRET

    [Fact]
    public void ItemChain_Return0_IsTrigretFalse_Return1_IsTrue_NoReturn_IsDefault()
    {
        var stack = Load("""
            [EVENTS e_ret0]
            ON=@Timer
            RETURN 0
            [EVENTS e_ret1]
            ON=@Timer
            RETURN 1
            [EVENTS e_noret]
            ON=@Timer
            TAG.ran=1
            """);
        var world = TestHarness.CreateWorld();

        var zero = world.CreateItem();
        zero.Events.Add(stack.Resources.ResolveDefName("e_ret0"));
        Assert.Equal(TriggerResult.False,
            stack.Dispatcher.FireItemTrigger(zero, ItemTrigger.Timer, new GameArgs { ItemSrc = zero }));

        var one = world.CreateItem();
        one.Events.Add(stack.Resources.ResolveDefName("e_ret1"));
        Assert.Equal(TriggerResult.True,
            stack.Dispatcher.FireItemTrigger(one, ItemTrigger.Timer, new GameArgs { ItemSrc = one }));

        var none = world.CreateItem();
        none.Events.Add(stack.Resources.ResolveDefName("e_noret"));
        Assert.Equal(TriggerResult.Default,
            stack.Dispatcher.FireItemTrigger(none, ItemTrigger.Timer, new GameArgs { ItemSrc = none }));
    }

    [Fact]
    public void ChainResult_IsTheLastBlockThatRan()
    {
        // CCharAct.cpp:5602 - each stage assigns iRet, so a block that falls off its
        // end after an earlier RETURN 0 leaves the chain at DEFAULT.
        var stack = Load("""
            [EVENTS e_first]
            ON=@Timer
            RETURN 0
            [EVENTS e_second]
            ON=@Timer
            TAG.second=1
            [EVENTS e_unhooked]
            ON=@DClick
            RETURN 1
            """);
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.Events.Add(stack.Resources.ResolveDefName("e_first"));
        item.Events.Add(stack.Resources.ResolveDefName("e_second"));
        Assert.Equal(TriggerResult.Default,
            stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Timer, new GameArgs { ItemSrc = item }));

        // A resource that does not hook the trigger leaves the answer alone.
        var other = world.CreateItem();
        other.Events.Add(stack.Resources.ResolveDefName("e_first"));
        other.Events.Add(stack.Resources.ResolveDefName("e_unhooked"));
        Assert.Equal(TriggerResult.False,
            stack.Dispatcher.FireItemTrigger(other, ItemTrigger.Timer, new GameArgs { ItemSrc = other }));
    }

    [Fact]
    public void ItemTimer_Return0_DeletesTheItemThroughTheTimerPath()
    {
        // CItem.cpp:6416 - "if (iRet == TRIGRET_RET_FALSE) return false" deletes it.
        var stack = Load("""
            [EVENTS e_timer0]
            ON=@Timer
            RETURN 0
            """);
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.Events.Add(stack.Resources.ResolveDefName("e_timer0"));
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Item.OnTimerExpired = it => stack.Dispatcher.FireItemTrigger(it, ItemTrigger.Timer,
            new GameArgs { ItemSrc = it });
        item.SetTimeout(Environment.TickCount64 - 1);

        Assert.False(item.OnTick());
    }

    [Theory]
    [InlineData(ItemType.SpawnChar)]
    [InlineData(ItemType.SpawnItem)]
    public void SpawnerTimer_Return0_KeepsTheSpawner(ItemType type)
    {
        // The spawn component answers the expiry (CCSpawn::OnTickComponent returns
        // CCRET_TRUE, CItem.cpp:6231), so a spawner @Timer ending in RETURN 0 - as the
        // worldgen spawners do - never reaches the default-path deletion.
        var stack = Load("""
            [EVENTS e_spawn_timer0]
            ON=@Timer
            RETURN 0
            """);
        var world = TestHarness.CreateWorld();
        var spawner = world.CreateItem();
        spawner.ItemType = type;
        spawner.InitializeSpawnComponent(world, stack.Resources);
        spawner.Events.Add(stack.Resources.ResolveDefName("e_spawn_timer0"));
        world.PlaceItem(spawner, new Point3D(100, 100, 0, 0));
        Item.OnTimerExpired = it => stack.Dispatcher.FireItemTrigger(it, ItemTrigger.Timer,
            new GameArgs { ItemSrc = it });
        spawner.SetTimeout(Environment.TickCount64 - 1);

        Assert.True(spawner.OnTick());
        Assert.False(spawner.IsDeleted);
    }

    [Fact]
    public void BareReturn_CountsAsReturnZero()
    {
        var stack = Load("""
            [EVENTS e_bare]
            ON=@Timer
            RETURN
            """);
        var item = TestHarness.CreateWorld().CreateItem();
        item.Events.Add(stack.Resources.ResolveDefName("e_bare"));
        Assert.Equal(TriggerResult.False,
            stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Timer, new GameArgs { ItemSrc = item }));
    }

    [Fact]
    public void SkillWait_Return0_StopsBeforeTheSkillSection()
    {
        // Skill_Wait acts on the character trigger's TRIGRET_RET_FALSE at once
        // (CCharSkill.cpp:3990-3996); the [SKILL] @Wait never runs.
        var stack = Load("""
            [EVENTS e_wait]
            ON=@SkillWait
            RETURN 0
            [SKILL 21]
            DEFNAME=skill_snooping
            ON=@Wait
            TAG.section=1
            """);
        var ch = TestHarness.CreateWorld().CreateCharacter();
        ch.IsPlayer = true;
        ch.Events.Add(stack.Resources.ResolveDefName("e_wait"));
        var result = stack.Dispatcher.FireCharTrigger(ch, CharTrigger.SkillWait,
            new GameArgs { CharSrc = ch, N1 = 21, N2 = -1 });
        Assert.Equal(TriggerResult.False, result);
        Assert.False(ch.TryGetTag("section", out _));
    }

    // ------------------------------------------------------------ mirrors

    [Fact]
    public void ItemMirror_RunsTheActingCharactersGlobalPlayerEvents()
    {
        // The @itemDClick mirror is pChar->OnTrigger(...) - the full character chain,
        // EVENTSPLAYER included (CItem.cpp:3771).
        var stack = Load("""
            [EVENTS e_player]
            ON=@itemDClick
            TAG.mirrored=1
            RETURN 1
            """);
        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        var item = world.CreateItem();
        stack.Dispatcher.GlobalPlayerEvents.Add(stack.Resources.ResolveDefName("e_player"));

        var result = stack.Dispatcher.FireItemTrigger(item, ItemTrigger.DClick,
            new GameArgs { CharSrc = player, ItemSrc = item });

        Assert.Equal(TriggerResult.True, result);
        Assert.True(player.TryGetTag("mirrored", out _));
    }

    [Fact]
    public void Mirror_OnlyRunsForNamesInTheCharacterTriggerTable()
    {
        // @charHit / @itemSpellEffect are not in CChar::sm_szTrigName, so upstream never
        // mirrors those (FindTableSorted(...) > XTRIG_UNKNOWN, CCharAct.cpp:5557).
        var stack = Load("""
            [EVENTS e_src]
            ON=@charHit
            TAG.charhit=1
            ON=@itemSpellEffect
            TAG.itemspell=1
            ON=@charDClick
            TAG.chardclick=1
            """);
        var world = TestHarness.CreateWorld();
        var src = world.CreateCharacter();
        src.IsPlayer = true;
        src.Events.Add(stack.Resources.ResolveDefName("e_src"));
        var target = world.CreateCharacter();
        var item = world.CreateItem();

        stack.Dispatcher.FireCharTrigger(target, CharTrigger.Hit, new GameArgs { CharSrc = src });
        stack.Dispatcher.FireItemTrigger(item, ItemTrigger.SpellEffect, new GameArgs { CharSrc = src, ItemSrc = item });
        stack.Dispatcher.FireCharTrigger(target, CharTrigger.DClick, new GameArgs { CharSrc = src });

        Assert.False(src.TryGetTag("charhit", out _));
        Assert.False(src.TryGetTag("itemspell", out _));
        Assert.True(src.TryGetTag("chardclick", out _));
    }

    [Fact]
    public void ClientTooltipAfterDefault_UsesTheUnderscoredTriggerName()
    {
        var stack = Load("""
            [EVENTS e_tt]
            ON=@ClientTooltip_AfterDefault
            TAG.after=1
            """);
        var item = TestHarness.CreateWorld().CreateItem();
        item.Events.Add(stack.Resources.ResolveDefName("e_tt"));
        stack.Dispatcher.FireItemTrigger(item, ItemTrigger.ClientTooltipAfterDefault, new GameArgs { ItemSrc = item });
        Assert.True(item.TryGetTag("after", out _));
    }

    // ------------------------------------------------------ @Create orders

    [Fact]
    public void ItemCreate_RunsTheItemdefBlockFirst_AndATeventsReturn1CannotHideIt()
    {
        // CItem.cpp:3753-3755 / :3883-3885.
        var stack = Load("""
            [EVENTS e_icreate]
            ON=@Create
            TAG.order=<TAG0.order>t
            RETURN 1
            [ITEMDEF 0f0e]
            DEFNAME=i_create_order
            TEVENTS=e_icreate
            ON=@Create
            TAG.order=<TAG0.order>d
            """, definitions: true);
        var item = TestHarness.CreateWorld().CreateItem();
        item.BaseId = 0x0F0E;
        stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Create, new GameArgs { ItemSrc = item });
        Assert.True(item.TryGetTag("order", out var order));
        Assert.Equal("0dt", order);
    }

    [Fact]
    public void NpcCreate_ChardefFirst_ThenTevents_AndNoDynamicEvents()
    {
        // NPC_LoadScript runs the CHARDEF block, NPC_CreateTrigger TEVENTS and
        // EVENTSPET (CCharNPC.cpp:279-343); a dynamic EVENTS never sees @Create.
        var stack = Load("""
            [EVENTS e_tev]
            ON=@Create
            TAG.order=<TAG0.order>t
            [EVENTS e_dyn]
            ON=@Create
            TAG.dynamic=1
            [CHARDEF 0190]
            DEFNAME=c_create_order
            TEVENTS=e_tev
            ON=@Create
            TAG.order=<TAG0.order>c
            """, definitions: true);
        var npc = TestHarness.CreateWorld().CreateCharacter();
        npc.BodyId = 0x190;
        npc.CharDefIndex = 0x190;
        npc.IsPlayer = false;
        npc.Events.Add(stack.Resources.ResolveDefName("e_dyn"));

        stack.Dispatcher.FireCharTrigger(npc, CharTrigger.Create, new GameArgs { CharSrc = npc });

        Assert.True(npc.TryGetTag("order", out var order));
        Assert.Equal("0ct", order);
        Assert.False(npc.TryGetTag("dynamic", out _));
    }

    [Fact]
    public void NpcRestockAndCreateLoot_ReadOnlyTheChardefBlock()
    {
        // ReadScriptReducedTrig (CCharNPCAct_Vendor.cpp:85, CCharAct.cpp:4405).
        var stack = Load("""
            [EVENTS e_restock]
            ON=@NPCRestock
            TAG.events_restock=1
            ON=@CreateLoot
            TAG.events_loot=1
            [CHARDEF 0190]
            DEFNAME=c_restock_only
            TEVENTS=e_restock
            ON=@NPCRestock
            TAG.def_restock=1
            ON=@CreateLoot
            TAG.def_loot=1
            """, definitions: true);
        var npc = TestHarness.CreateWorld().CreateCharacter();
        npc.BodyId = 0x190;
        npc.CharDefIndex = 0x190;
        npc.Events.Add(stack.Resources.ResolveDefName("e_restock"));

        stack.Dispatcher.FireCharTrigger(npc, CharTrigger.NPCRestock, new GameArgs { CharSrc = npc });
        stack.Dispatcher.FireCharTrigger(npc, CharTrigger.CreateLoot, new GameArgs { CharSrc = npc });

        Assert.True(npc.TryGetTag("def_restock", out _));
        Assert.True(npc.TryGetTag("def_loot", out _));
        Assert.False(npc.TryGetTag("events_restock", out _));
        Assert.False(npc.TryGetTag("events_loot", out _));
    }

    // ---------------------------------------------------------- fire sites

    [Fact]
    public void UseQuick_Return1Hook_SucceedsWithoutExperience()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetSkill(SkillType.Hiding, 0);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) =>
            SkillEngine.UseQuickHandledSuccess;
        Assert.True(SkillEngine.UseQuick(player, SkillType.Hiding, 100));
        Assert.Equal(0, player.GetSkill(SkillType.Hiding));

        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => -1;
        Assert.False(SkillEngine.UseQuick(player, SkillType.Hiding, 0));
        Assert.Equal(0, player.GetSkill(SkillType.Hiding));
    }

    [Fact]
    public void ItemStep_Return1_BlocksTheWalk()
    {
        // fStepCancel (CCharAct.cpp:4955-4958, :5057).
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Dex = 50; ch.MaxStam = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var plate = world.CreateItem();
        plate.BaseId = 0x0EED;
        world.PlaceItem(plate, new Point3D(101, 100, 0, 0));

        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "Step", (_, _) => TriggerResult.True);

        bool moved = new MovementEngine(world, dispatcher).TryMove(ch, Direction.East, running: false, sequence: 1);

        Assert.False(moved);
        Assert.Equal(100, ch.X);
    }
}
