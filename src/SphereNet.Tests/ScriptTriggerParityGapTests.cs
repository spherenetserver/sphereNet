using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World.Regions;
using Xunit;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// Script-trigger contracts a live pack depends on, each checked against the Source-X
/// behaviour it follows:
///   @DropOn_Self fires on the RECEIVER with the dragged item as ARGO (CClientEvent.cpp:435);
///   the @item*/@char* mirrors point the source's ACT at the item/char (CItem.cpp:3767);
///   TYPEDEF resolves from the instance/named def type and runs before the ITEMDEF (CItem.cpp:3844);
///   region @Exit/@Enter/@Step and @RegionEnter can refuse a move, ARGO = the area (CCharAct.cpp:5102);
///   @RegenStat (CCharStat.cpp:538) and @ArrowQuest_Add/_Close (CClientMsg.cpp:573) fire.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptTriggerParityGapTests
{
    private static ScriptRuntimeStack Stack(string script)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"trig-gap-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        try { stack.Resources.LoadResourceFile(path); }
        finally { File.Delete(path); }
        return stack;
    }

    private static long TagNumber(IScriptObj obj, string tag)
    {
        Assert.True(obj.TryGetProperty("TAG." + tag, out string raw), $"TAG.{tag} missing");
        Assert.True(ScriptNumber.TryParseToken(raw, out long n), $"TAG.{tag}='{raw}'");
        return n;
    }

    // ---------------------------------------------------------------- @DropOn_Self

    [Fact]
    public void DropOnSelf_RunsOnTheReceivingContainer_WithTheDraggedItemAsArgo_AndReturn1Bounces()
    {
        var stack = Stack("""
            [EVENTS e_test_mailbox]
            ON=@DropOn_Self
            TAG.GOT=<ARGO.UID>
            RETURN 1
            """);
        var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19701);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var mailbox = world.CreateItem();
        mailbox.BaseId = 0x0E7C;
        mailbox.ItemType = ItemType.Container;
        world.PlaceItem(mailbox, new Point3D(101, 100, 0, 0));
        mailbox.Events.Add(stack.Resources.ResolveDefName("e_test_mailbox"));

        var letter = world.CreateItem();
        letter.BaseId = 0x0E34;
        world.PlaceItem(letter, new Point3D(100, 101, 0, 0));

        client.HandleItemPickup(letter.Uid.Value, 1);
        client.HandleItemDrop(letter.Uid.Value, -1, -1, 0, mailbox.Uid.Value);

        // The receiver ran it, and ARGO was the dragged letter.
        Assert.Equal(letter.Uid.Value, (uint)TagNumber(mailbox, "GOT"));
        // RETURN 1 bounced the letter back to where it was lifted from.
        Assert.False(letter.ContainedIn.IsValid);
        Assert.Equal(new Point3D(100, 101, 0, 0), letter.Position);
        Assert.DoesNotContain(letter, world.GetContainerContents(mailbox.Uid));
    }

    [Fact]
    public void DropOnSelf_OnMyOwnCharacter_RunsOnMyPack()
    {
        var stack = Stack("""
            [EVENTS e_test_pack]
            ON=@DropOn_Self
            TAG.GOT=<ARGO.UID>
            """);
        var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19702);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        pack.Events.Add(stack.Resources.ResolveDefName("e_test_pack"));

        var coin = world.CreateItem();
        coin.BaseId = 0x0E34;
        world.PlaceItem(coin, new Point3D(100, 101, 0, 0));

        client.HandleItemPickup(coin.Uid.Value, 1);
        client.HandleItemDrop(coin.Uid.Value, 0, 0, 0, me.Uid.Value);

        Assert.Equal(coin.Uid.Value, (uint)TagNumber(pack, "GOT"));
        Assert.Equal(pack.Uid, coin.ContainedIn);
    }

    // ---------------------------------------------------------------- ACT in mirrors

    [Fact]
    public void ItemAndCharMirrors_PointTheSourcesActAtTheObject_AndRestoreIt()
    {
        var stack = Stack("""
            [EVENTS e_test_player]
            ON=@itemDClick
            TAG.ITEMACT=<ACT.UID>
            ON=@charDClick
            TAG.CHARACT=<ACT.UID>
            """);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        player.Events.Add(stack.Resources.ResolveDefName("e_test_player"));
        var other = world.CreateCharacter();
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        var sword = world.CreateItem();
        world.PlaceItem(sword, new Point3D(100, 101, 0, 0));
        var previous = world.CreateItem();
        player.Act = previous.Uid;

        stack.Dispatcher.FireItemTrigger(sword, ItemTrigger.DClick, new TriggerArgs { CharSrc = player });
        stack.Dispatcher.FireCharTrigger(other, CharTrigger.DClick, new TriggerArgs { CharSrc = player });

        Assert.Equal(sword.Uid.Value, (uint)TagNumber(player, "ITEMACT"));
        Assert.Equal(other.Uid.Value, (uint)TagNumber(player, "CHARACT"));
        Assert.Equal(previous.Uid, player.Act);
    }

    // ---------------------------------------------------------------- TYPEDEF

    private const string TypeDefScript = """
        [TYPEDEF t_test_custom]
        ON=@DClick
        SRC.TAG.ORDER=<SRC.TAG.ORDER>T

        [ITEMDEF i_test_custom_potion]
        ID=0f0e
        TYPE=t_test_custom
        ON=@DClick
        SRC.TAG.ORDER=<SRC.TAG.ORDER>I
        RETURN 1
        """;

    [Fact]
    public void NamedItemdefWithACustomType_ReachesItsTypedef_BeforeTheItemdef()
    {
        var stack = Stack(TypeDefScript);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetTag("ORDER", "S");
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var potion = world.CreateItem();
        var rid = stack.Resources.ResolveDefName("i_test_custom_potion");
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(potion, rid.Index, fireCreate: false));
        world.PlaceItem(potion, new Point3D(100, 101, 0, 0));

        var result = stack.Dispatcher.FireItemTrigger(potion, ItemTrigger.DClick,
            new TriggerArgs { CharSrc = player });

        // TYPEDEF first, then the ITEMDEF - whose RETURN 1 used to hide the TYPEDEF.
        Assert.Equal(TriggerResult.True, result);
        Assert.True(player.TryGetTag("ORDER", out string? order));
        Assert.Equal("STI", order);
        Assert.True(potion.TryGetProperty("TYPE", out string type));
        Assert.Equal("t_test_custom", type, ignoreCase: true);
    }

    [Fact]
    public void CustomTypeSetOnTheInstance_ReachesItsTypedef_AndReadsBackByName()
    {
        var stack = Stack(TypeDefScript);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetTag("ORDER", "S");
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var rock = world.CreateItem();
        rock.BaseId = 0x1363;
        world.PlaceItem(rock, new Point3D(100, 101, 0, 0));

        Assert.True(rock.TrySetProperty("TYPE", "t_test_custom"));
        stack.Dispatcher.FireItemTrigger(rock, ItemTrigger.DClick, new TriggerArgs { CharSrc = player });

        Assert.True(player.TryGetTag("ORDER", out string? order));
        Assert.Equal("ST", order);
        Assert.Equal("t_test_custom", rock.CustomTypeName);
        Assert.True(rock.TryGetProperty("TYPE", out string type));
        Assert.Equal("t_test_custom", type);
    }

    // ---------------------------------------------------------------- regions

    private static (MovementEngine Engine, Character Walker, Region From, Region To,
        SphereNet.Game.World.GameWorld World) RegionBench(ScriptRuntimeStack stack)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var from = new Region { Name = "Road" };
        from.AddRect(90, 90, 100, 110);
        var to = new Region { Name = "Vault", DefName = "a_test_vault" };
        to.AddRect(101, 90, 110, 110);
        world.AddRegion(from);
        world.AddRegion(to);
        var walker = world.CreateCharacter();
        walker.IsPlayer = true;
        world.PlaceCharacter(walker, new Point3D(100, 100, 0, 0));
        return (new MovementEngine(world, stack.Dispatcher), walker, from, to, world);
    }

    [Fact]
    public void RegionEnterReturn1_RefusesTheStep()
    {
        var stack = Stack("""
            [EVENTS r_test_locked]
            ON=@Enter
            RETURN 1
            """);
        var (engine, walker, _, to, _) = RegionBench(stack);
        to.AddEventsFromTag("r_test_locked");

        Assert.False(engine.TryMove(walker, Direction.East, false, 1));
        Assert.Equal(new Point3D(100, 100, 0, 0), walker.Position);

        // A GM is never refused (CCharAct.cpp:5109).
        walker.PrivLevel = PrivLevel.GM;
        Assert.True(engine.TryMove(walker, Direction.East, false, 2));
        Assert.Equal(101, walker.X);
    }

    [Fact]
    public void RegionEnterCharTrigger_SeesTheAreaAsArgo_AndReturn1Refuses_AndTheFunctionRunsOnce()
    {
        var stack = Stack("""
            [EVENTS e_test_walker]
            ON=@RegionEnter
            TAG.AREA=<ARGO.DEFNAME>
            TAG.KEYREQ=<ARGO.TAG0.AreaKeyReq>
            RETURN 1

            [FUNCTION f_onchar_regionenter]
            TAG.FCOUNT=<EVAL <TAG0.FCOUNT>+1>
            """);
        var (engine, walker, _, to, _) = RegionBench(stack);
        to.SetTag("AreaKeyReq", "1");
        walker.Events.Add(stack.Resources.ResolveDefName("e_test_walker"));

        Assert.False(engine.TryMove(walker, Direction.East, false, 1));

        Assert.Equal(100, walker.X);
        Assert.True(walker.TryGetTag("AREA", out string? area));
        Assert.Equal("a_test_vault", area);
        Assert.Equal(1, TagNumber(walker, "KEYREQ"));
        // The events block refused before the f_onchar_ fallback; walk out of it and
        // count the function on an accepted crossing instead.
        walker.Events.Clear();
        Assert.True(engine.TryMove(walker, Direction.East, false, 2));
        Assert.Equal(1, TagNumber(walker, "FCOUNT"));
    }

    [Fact]
    public void RegionStepReturn1_PutsTheWalkerBack_EvenOnAStepThatStaysInside()
    {
        var stack = Stack("""
            [EVENTS r_test_mire]
            ON=@Step
            RETURN 1
            """);
        var (engine, walker, from, _, _) = RegionBench(stack);
        from.AddEventsFromTag("r_test_mire");

        Assert.False(engine.TryMove(walker, Direction.West, false, 1));
        Assert.Equal(new Point3D(100, 100, 0, 0), walker.Position);
    }

    // ---------------------------------------------------------------- @RegenStat

    [Fact]
    public void RegenStat_FiresPerStat_AndItsValueAndReturnAreHonoured()
    {
        var stack = Stack("""
            [EVENTS e_test_regen]
            ON=@RegenStat
            TAG.SEEN<LOCAL.StatID>=1
            IF (<LOCAL.StatID> == 0)
                LOCAL.Value=5
            ENDIF
            IF (<LOCAL.StatID> == 1)
                RETURN 1
            ENDIF
            """);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.BodyId = 0x029A; // no racial hit bonus
        ch.IsPlayer = true;
        ch.MaxHits = 100; ch.Hits = 10;
        ch.MaxMana = 100; ch.Mana = 10;
        ch.Food = 10;
        ch.Events.Add(stack.Resources.ResolveDefName("e_test_regen"));
        Character.OnRegenStat = stack.Dispatcher.FireRegenStat;
        try
        {
            ch.OnTick();
        }
        finally { Character.OnRegenStat = null; }

        Assert.Equal(15, ch.Hits);   // the script's LOCAL.Value, not the base 1
        Assert.Equal(10, ch.Mana);   // RETURN 1 skipped mana
        Assert.True(ch.TryGetTag("SEEN0", out _));
        Assert.True(ch.TryGetTag("SEEN1", out _));
        Assert.True(ch.TryGetTag("SEEN2", out _));   // full stamina still comes due
    }

    // ---------------------------------------------------------------- @ArrowQuest_*

    [Fact]
    public void ArrowQuestVerb_FiresAddThenClose_WithTheCoordinates()
    {
        var stack = Stack("""
            [EVENTS e_test_arrow]
            ON=@ArrowQuest_Add
            TAG.ARROW=<ARGN1>,<ARGN2>
            ON=@Arrowquest_Close
            TAG.CLOSED=1

            [FUNCTION f_test_arrow_on]
            ARROWQUEST 1200,1300

            [FUNCTION f_test_arrow_off]
            ARROWQUEST 0,0
            """);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.Events.Add(stack.Resources.ResolveDefName("e_test_arrow"));
        Character.OnArrowQuest = stack.Dispatcher.FireArrowQuest;
        try
        {
            var args = new SphereNet.Scripting.Execution.TriggerArgs(ch);
            Assert.True(stack.Runner.TryRunFunction("f_test_arrow_on", ch, null, args, out _));
            Assert.True(ch.TryGetTag("ARROW", out string? arrow));
            Assert.Equal("1200,1300", arrow);
            Assert.False(ch.TryGetTag("CLOSED", out _));

            Assert.True(stack.Runner.TryRunFunction("f_test_arrow_off", ch, null, args, out _));
            Assert.True(ch.TryGetTag("CLOSED", out _));
        }
        finally { Character.OnArrowQuest = null; }
    }
}
