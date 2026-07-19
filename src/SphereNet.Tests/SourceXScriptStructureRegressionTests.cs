using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Gumps;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Speech;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SourceXScriptStructureRegressionTests
{
    [Theory]
    [InlineData("skara*brae", "skara brae")]
    [InlineData("*thiev*guild*", "where is the thieves guild today")]
    [InlineData("*how*quit*?*", "how can I quit?")]
    [InlineData("WHERE<ANY>MONKS", "where are the monks")]
    public void SpeechGlob_MatchesSourceXAndLegacyPackPatterns(string pattern, string spoken)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript($$"""
            [SPEECH spk_glob_probe]
            ON={{pattern}}
            TAG.MATCHED=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        var link = stack.Resources.GetResource(stack.Resources.ResolveDefName("spk_glob_probe"));

        Assert.NotNull(link);
        Assert.Equal(TriggerResult.True,
            stack.Runner.RunSpeechTrigger(link!, spoken, npc, null,
                new SphereNet.Scripting.Execution.TriggerArgs(npc, argStr: spoken)));
        Assert.True(npc.TryGetTag("MATCHED", out string? matched));
        Assert.Equal("1", matched);
    }

    [Fact]
    public void MultiDefTSpeech_RunsOnHouseOrShipItem()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [MULTIDEF 064]
            TSPEECH=spk_multi_probe

            [SPEECH spk_multi_probe]
            ON=lock*this*
            SRC.TAG.MULTI_SPEECH=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        Character speaker = world.CreateCharacter();
        speaker.IsPlayer = true;
        Item multi = world.CreateItem();
        multi.BaseId = 0x64;
        multi.ItemType = ItemType.Multi;

        var result = stack.Dispatcher.FireMultiSpeechTrigger(multi, speaker, "lock this down");

        Assert.Equal(TriggerResult.True, result);
        Assert.True(speaker.TryGetTag("MULTI_SPEECH", out string? value));
        Assert.Equal("1", value);
    }

    /// <summary>Field bug (2026-07-20): ship speech commands did nothing. The
    /// pack stacks alias patterns over one shared body (on=forward /
    /// on=foreward / on=unfurl sail → SHIPFORE); matching any alias but the
    /// LAST collected an empty body, so "forward"/"back" never moved the ship.</summary>
    [Theory]
    [InlineData("forward")]
    [InlineData("foreward")]
    [InlineData("unfurl sail")]
    public void SpeechAliasGroup_SharesTheBodyAfterTheLastAlias(string spoken)
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [SPEECH spk_alias_probe]
            ON=forward
            ON=foreward
            ON=unfurl sail
            TAG.ALIAS_BODY=1
            RETURN 1

            ON=stop
            TAG.STOPPED=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        var link = stack.Resources.GetResource(stack.Resources.ResolveDefName("spk_alias_probe"));
        Assert.NotNull(link);

        Assert.Equal(TriggerResult.True,
            stack.Runner.RunSpeechTrigger(link!, spoken, npc, null,
                new SphereNet.Scripting.Execution.TriggerArgs(npc, argStr: spoken)));
        Assert.True(npc.TryGetTag("ALIAS_BODY", out string? v) && v == "1",
            $"alias '{spoken}' did not run the shared body");
        Assert.False(npc.TryGetTag("STOPPED", out _),
            "alias match leaked into the NEXT alias group's body");
    }

    [Fact]
    public void NamedItemDef_MetadataPreservesTEventsAndOwnTriggers()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_item_meta_probe]
            ON=@DClick
            TAG.EVENT_SEEN=1
            RETURN 0

            [ITEMDEF i_item_meta_probe]
            ID=0eed
            TEVENTS=e_item_meta_probe
            TAG.DEF_META=present
            ON=@DClick
            TAG.DEF_SEEN=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        Item item = world.CreateItem();
        int defIndex = stack.Resources.ResolveDefName("i_item_meta_probe").Index;

        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, defIndex));
        Assert.Equal((ushort)0x0EED, item.BaseId);
        Assert.True(item.TryGetTag("SCRIPTDEF", out _));
        Assert.True(item.TryGetTag("DEF_META", out string? meta));
        Assert.Equal("present", meta);

        Assert.Equal(TriggerResult.True,
            stack.Dispatcher.FireItemTrigger(item, ItemTrigger.DClick,
                new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item }));
        Assert.True(item.TryGetTag("EVENT_SEEN", out _));
        Assert.True(item.TryGetTag("DEF_SEEN", out _));
    }

    [Fact]
    public void LegacyItemUnequipTest_CanVetoCanonicalUnequip()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_legacy_unequip_probe]
            ON=@ItemUnEquipTest
            TAG.LEGACY_UNEQUIP=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        Character wearer = world.CreateCharacter();
        wearer.Events.Add(stack.Resources.ResolveDefName("e_legacy_unequip_probe"));
        Item item = world.CreateItem();

        var result = stack.Dispatcher.FireItemTrigger(item, ItemTrigger.Unequip,
            new SphereNet.Game.Scripting.TriggerArgs { CharSrc = wearer, ItemSrc = item });

        Assert.Equal(TriggerResult.True, result);
        Assert.True(wearer.TryGetTag("LEGACY_UNEQUIP", out _));
    }

    [Fact]
    public void HouseDesignBegin_FiresBeforeSessionAndCanCancel()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_house_begin_probe]
            ON=@HouseDesignBegin
            TAG.HOUSE_BEGIN=1
            RETURN 1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        registry.Register(new MultiDef { Id = 0x64, Name = "custom foundation" });
        var housing = new HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = -1,
            MaxHousesPerAccount = -1
        };
        Character owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Events.Add(stack.Resources.ResolveDefName("e_house_begin_probe"));
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        House house = Assert.IsType<House>(housing.PlaceHouse(owner, 0x64,
            new Point3D(100, 100, 0, 0), customFoundation: true, magic: true));
        var custom = new CustomHousingEngine(world, housing);

        var accounts = new AccountManager(stack.LoggerFactory);
        var client = TestHarness.CreateClient(stack.LoggerFactory, world, accounts, 9912);
        TestHarness.AttachCharacter(client, owner);
        client.SetEngines(housingEngine: housing, customHousing: custom,
            triggerDispatcher: stack.Dispatcher);

        client.BeginHouseCustomization(house.MultiItem);

        Assert.True(owner.TryGetTag("HOUSE_BEGIN", out _));
        Assert.Null(custom.GetSession(owner.Uid));
        Assert.Empty(TestHarness.GetQueuedPackets(client.NetState));
    }

    [Fact]
    public void AddMulti_FiresFromNativeHouseRegistration()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [EVENTS e_add_multi_probe]
            ON=@AddMulti
            IF (<ARGN2>==1)
                TAG.ADD_MULTI=1
            ENDIF
            RETURN 0
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        registry.Register(new MultiDef { Id = 0x64, Name = "house" });
        var housing = new HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = -1,
            MaxHousesPerAccount = -1,
            OnAddMulti = (owner, multi, privilege) =>
                stack.Dispatcher.FireCharTrigger(owner, CharTrigger.AddMulti,
                    new SphereNet.Game.Scripting.TriggerArgs
                    {
                        CharSrc = owner, O1 = multi, N1 = 1,
                        N2 = (int)privilege, N3 = 1
                    })
        };
        Character owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Events.Add(stack.Resources.ResolveDefName("e_add_multi_probe"));

        Assert.NotNull(housing.PlaceHouse(owner, 0x64,
            new Point3D(100, 100, 0, 0), magic: true));
        Assert.True(owner.TryGetTag("ADD_MULTI", out _));
    }

    [Fact]
    public void NpcSeeWantItem_FiresBeforeDesiredGroundItemPickup()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [ITEMDEF i_gold]
            ID=0eed

            [CHARDEF c_scavenge_probe]
            ID=0190
            DESIRES=i_gold
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        Character npc = world.CreateCharacter();
        npc.CharDefIndex = stack.Resources.ResolveDefName("c_scavenge_probe").Index;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        Item gold = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(gold,
            stack.Resources.ResolveDefName("i_gold").Index);
        world.PlaceItem(gold, new Point3D(101, 100, 0, 0));

        var ai = new NpcAI(world, new SphereConfig());
        bool fired = false;
        ai.OnNpcSeeWantItem = (seenNpc, seenItem) =>
        {
            fired = seenNpc == npc && seenItem == gold;
            return true; // veto pickup so the assertion remains stable
        };
        MethodInfo look = typeof(NpcAI).GetMethod("LookAtNearbyItems",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 5000 && !fired; i++)
            look.Invoke(ai, [npc]);

        Assert.True(fired);
        Assert.True(gold.IsOnGround);
    }

    [Fact]
    public void DialogLayout_ExecutesFunctionsAndHonorsReturn()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [FUNCTION f_dialog_layout_probe]
            SRC.TAG.LAYOUT_FUNCTION=1
            BUTTONTILEART 10 10 4005 4006 1 0 1 0eed 0 0 0
            XMFHTMLTOK 10 40 200 20 0 0 0 1042971 @probe@

            [FUNCTION f_dialog_call_probe]
            SRC.TAG.LAYOUT_CALL_ARGS=<ARGS>

            [DIALOG d_layout_runtime_probe]
            0,0
            CALL f_dialog_call_probe source-x-args
            f_dialog_layout_probe
            RETURN 1
            SRC.TAG.AFTER_LAYOUT_RETURN=1
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(stack.LoggerFactory);
        var client = TestHarness.CreateClient(stack.LoggerFactory, world, accounts, 9911);
        Character player = world.CreateCharacter();
        player.IsPlayer = true;
        TestHarness.AttachCharacter(client, player);
        client.SetEngines(
            commands: new CommandHandler { Resources = stack.Resources },
            triggerDispatcher: stack.Dispatcher);

        Assert.True(client.TryShowScriptDialog("d_layout_runtime_probe", 0));
        Assert.True(player.TryGetTag("LAYOUT_FUNCTION", out string? called));
        Assert.Equal("1", called);
        Assert.True(player.TryGetTag("LAYOUT_CALL_ARGS", out string? callArgs));
        Assert.Equal("source-x-args", callArgs);
        Assert.False(player.TryGetTag("AFTER_LAYOUT_RETURN", out _));
        Assert.Single(TestHarness.GetQueuedPackets(client.NetState));
    }

    [Fact]
    public void GumpBuilder_EmitsExtendedSourceXControls()
    {
        var gump = new GumpBuilder(1, 2)
            .AddButtonTileArt(1, 2, 3, 4, 7, 1, 0, 0xEED, 9, -10, -5)
            .AddXmfHtmlTok(10, 20, 100, 30, false, true, 55, 1042971, "@arg@")
            .AddPicInPic(5, 6, 7, 8, 9, 10, 11);

        string layout = gump.BuildLayoutString();
        Assert.Contains("{ buttontileart 1 2 3 4 1 0 7 3821 9 -10 -5 }", layout);
        Assert.Contains("{ xmfhtmltok 10 20 100 30 0 1 55 1042971 @arg@ }", layout);
        Assert.Contains("{ picinpic 5 6 7 8 9 10 11 }", layout);
    }

    private static string WriteScript(string contents)
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-sourcex-structure-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".scp");
        File.WriteAllText(path, contents);
        return path;
    }
}
