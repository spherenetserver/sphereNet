using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Network.State;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;
using GameTriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// The stage order of a finished craft, through the real script interpreter and
/// trigger dispatcher (Source-X Skill_Done / Skill_MakeItem / Skill_MakeItem_Success,
/// CCharSkill.cpp:674-975 and :3899-3974):
///   - @SkillSuccess runs BEFORE anything is spent and its RETURN 1 aborts the stage;
///   - the SUCCESS stage pays for the replications and makes the item, whose @Create
///     runs exactly once and keeps what it set;
///   - @SkillMakeItem runs on the finished item, held in ACT, with ARGN1 = the base
///     skill, ARGN2 = the quality, ARGO = the previous ACT and LOCAL.Notify = 1; its
///     RETURN 1 deletes the result; ACT is restored;
///   - a scroll takes the scribe's skill as MOREY and no quality;
///   - the result bounces to the feet when it cannot be carried;
///   - MAKEITEM item,amount makes that many replications from what the stock covers.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftPipelineStageParityTests : IDisposable
{
    private const string Script = """
        [TYPEDEFS]
        t_normal=0
        t_weapon_sword=13
        t_scroll=32

        [ITEMDEF i_audit_ingot]
        ID=01bf2
        NAME=audit ingot
        CAN=0100

        [ITEMDEF i_audit_blank]
        ID=0e34
        NAME=audit blank
        CAN=0100

        [ITEMDEF i_audit_blade]
        ID=0f51
        NAME=audit blade
        TYPE=t_normal
        SKILLMAKE=Tinkering 0.0
        RESOURCES=5 i_audit_ingot

        ON=@Create
        TAG.CREATE_COUNT=<eval <TAG0.CREATE_COUNT> + 1>
        TYPE=t_weapon_sword
        COLOR=0481
        MORE1=7

        [ITEMDEF i_audit_pile]
        ID=0f52
        NAME=audit pile
        CAN=0100
        SKILLMAKE=Tinkering 0.0
        RESOURCES=2 i_audit_ingot

        [ITEMDEF i_audit_anvil]
        ID=0f53
        NAME=audit anvil
        WEIGHT=10000
        SKILLMAKE=Tinkering 0.0
        RESOURCES=1 i_audit_ingot

        ON=@DropOn_Ground
        TAG.DG_N1=<ARGN1>
        TAG.DG_N2=<ARGN2>
        TAG.DG_S1=<ARGS>
        ARGN1=600

        ON=@DropOn_Item
        TAG.DOI_ARGO=<ARGO.UID>

        [ITEMDEF i_audit_scroll]
        ID=01f2d
        NAME=audit scroll
        TYPE=t_scroll
        SKILLMAKE=Inscription 0.0
        RESOURCES=1 i_audit_blank

        [EVENTS e_craft_probe]
        ON=@SkillSuccess
        TAG.SS_COUNT=<eval <TAG0.SS_COUNT> + 1>
        IF (<TAG0.VETO_SUCCESS>)
            RETURN 1
        ENDIF

        ON=@SkillMakeItem
        TAG.MI_COUNT=<eval <TAG0.MI_COUNT> + 1>
        TAG.MI_N1=<ARGN1>
        TAG.MI_N2=<ARGN2>
        TAG.MI_ACT=<ACT.UID>
        TAG.MI_ACT_CREATES=<ACT.TAG0.CREATE_COUNT>
        TAG.MI_ARGO=<ARGO.UID>
        TAG.MI_NOTIFY=<LOCAL.NOTIFY>
        IF (<TAG0.SILENT_MAKE>)
            LOCAL.NOTIFY=0
        ENDIF
        IF (<TAG0.VETO_MAKE>)
            RETURN 1
        ENDIF

        ON=@SkillAbort
        TAG.AB_COUNT=<eval <TAG0.AB_COUNT> + 1>

        ON=@SkillFail
        TAG.SF_COUNT=<eval <TAG0.SF_COUNT> + 1>

        ON=@SkillUseQuick
        TAG.UQ_COUNT=<eval <TAG0.UQ_COUNT> + 1>

        ON=@SkillStart
        IF (<TAG0.FORCE_DIFF>)
            ACTDIFF=<TAG0.FORCE_DIFF>
        ENDIF
        IF (<TAG0.FAIL_EFFECT>)
            // Skill_Start reads m_Act_Effect back from LOCAL.Effect (CCharSkill.cpp:4526),
            // so an ACTIONEFFECT write here would be overwritten.
            LOCAL.Effect=<TAG0.FAIL_EFFECT>
        ENDIF

        [EVENTS e_pack_probe]
        ON=@DropOn_Self
        TAG.DOS_ARGO=<ARGO.UID>
        IF (<TAG0.REFUSE>)
            RETURN 1
        ENDIF
        """;

    private readonly string _scriptPath;

    public CraftPipelineStageParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"spherenet_craftstage_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Script);
    }

    public void Dispose()
    {
        try { File.Delete(_scriptPath); } catch { }
    }

    private sealed record Bench(GameWorld World, GameClient Client, NetState State, Character Crafter,
        Item Pack, CraftingEngine Engine, ResourceHolder Resources);

    private Bench Build(int str = 100)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_scriptPath) ?? ""
        };
        resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var world = TestHarness.CreateWorld();
        // The host wiring of Program.EngineWiring: every item made from a definition
        // runs its @Create through this hook.
        Item.CreateTriggerHook = item =>
            dispatcher.FireItemTrigger(item, ItemTrigger.Create, new GameTriggerArgs { ItemSrc = item });

        // The recipes ask for 0.0, so the start never rolls (Skill_Start rolls only
        // a positive difficulty, CCharSkill.cpp:4566); a test that wants a failure
        // raises ACTDIFF from @SkillStart.

        var engine = new CraftingEngine(world);
        Assert.True(engine.LoadRecipesFromDefs(resources) >= 4);

        var state = TestHarness.CreateActiveNetState(lf, 7701);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        client.SetEngines(craftingEngine: engine);
        typeof(GameClient).GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, dispatcher);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = (short)str;
        ch.MaxHits = ch.Hits = 100;
        ch.SetSkill(SkillType.Tinkering, 1000);
        ch.SetSkill(SkillType.Inscription, 873);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.Events.Add(ResourceId.FromEventName("e_craft_probe"));
        TestHarness.AttachCharacter(client, ch);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Equip(pack, Layer.Pack);
        return new Bench(world, client, state, ch, pack, engine, resources);
    }

    private static int DefId(Bench b, string defname)
    {
        var rid = b.Resources.ResolveDefName(defname);
        Assert.True(rid.IsValid, defname);
        return rid.Index;
    }

    private static void GiveStock(Bench b, string defname, int amount)
    {
        var stock = b.World.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(stock, DefId(b, defname), fireCreate: false);
        stock.Amount = (ushort)amount;
        Assert.True(b.Pack.TryAddItem(stock));
    }

    private static int CountInPack(Bench b, ushort baseId)
    {
        int n = 0;
        foreach (var item in b.Pack.Contents)
            if (!item.IsDeleted && item.BaseId == baseId)
                n += item.Amount;
        return n;
    }

    private static List<Item> ItemsInPack(Bench b, ushort baseId) =>
        b.Pack.Contents.Where(i => !i.IsDeleted && i.BaseId == baseId).ToList();

    private static CraftRecipe Recipe(Bench b, string defname)
    {
        var recipe = b.Engine.TryGetRecipe(DefId(b, defname));
        Assert.NotNull(recipe);
        return recipe!;
    }

    /// <summary>Finish the pending craft now: the stroke timer is expired and the
    /// client tick runs the completion exactly as the pump does.</summary>
    private static void FinishCraft(Bench b)
    {
        var handler = b.Client.WorldFeatures;
        typeof(ClientWorldFeaturesHandler)
            .GetField("_pendingCraftNextStroke", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(handler, 0L);
        b.Client.TickPendingCraft();
    }

    private static long Tag(Character ch, string key)
    {
        Assert.True(ch.TryGetTag(key, out string? raw), key);
        Assert.True(ScriptNumber.TryParseToken(raw, out long value), $"{key}={raw}");
        return value;
    }

    private static bool HasTag(Character ch, string key) =>
        ch.TryGetTag(key, out string? raw) && !string.IsNullOrEmpty(raw);

    private static string SentText(NetState state)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var packet in TestHarness.GetQueuedPackets(state))
        {
            var bytes = packet.Span.ToArray();
            sb.Append(System.Text.Encoding.ASCII.GetString(bytes));
            sb.Append(System.Text.Encoding.BigEndianUnicode.GetString(bytes));
        }
        return sb.ToString();
    }

    // --- E01: @SkillMakeItem ---------------------------------------------------

    [Fact]
    public void SkillMakeItem_DoesNotFireAtTheStart()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));

        Assert.False(HasTag(b.Crafter, "MI_COUNT"));
    }

    [Fact]
    public void SkillMakeItem_SeesTheNewItemInAct_WithSourceXArguments()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);
        var previousAct = b.World.CreateItem();
        previousAct.BaseId = 0x1EB8;
        Assert.True(b.Pack.TryAddItem(previousAct));
        b.Crafter.Act = previousAct.Uid;

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        var blade = Assert.Single(ItemsInPack(b, 0x0F51));
        Assert.Equal(1, Tag(b.Crafter, "MI_COUNT"));
        Assert.Equal(1000, Tag(b.Crafter, "MI_N1"));                  // base skill, not the skill number
        Assert.Equal(blade.Quality, Tag(b.Crafter, "MI_N2"));           // the rolled quality
        Assert.InRange(Tag(b.Crafter, "MI_N2"), 1, 200);
        Assert.Equal(blade.Uid.Value, (uint)Tag(b.Crafter, "MI_ACT"));  // the finished item
        Assert.Equal(1, Tag(b.Crafter, "MI_ACT_CREATES"));             // already created
        Assert.Equal(previousAct.Uid.Value, (uint)Tag(b.Crafter, "MI_ARGO"));
        Assert.Equal(1, Tag(b.Crafter, "MI_NOTIFY"));
        Assert.Equal(previousAct.Uid, b.Crafter.Act);                  // ACT restored
    }

    [Fact]
    public void SkillMakeItem_Return1_DeletesTheResult_ResourcesStaySpent_AndAborts()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);
        b.Crafter.SetTag("VETO_MAKE", "1");

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        Assert.Empty(ItemsInPack(b, 0x0F51));
        Assert.Equal(15, CountInPack(b, 0x1BF2));          // paid before the trigger (CCharSkill.cpp:920)
        Assert.Equal(1, Tag(b.Crafter, "AB_COUNT"));       // Skill_Fail(true)
        long actUid = Tag(b.Crafter, "MI_ACT");
        Assert.True(b.World.FindItem(new Serial((uint)actUid)) is null or { IsDeleted: true });
        Assert.False(b.Crafter.Act.IsValid);
    }

    [Fact]
    public void SkillMakeItem_NotifyZero_KeepsThePackLineQuiet()
    {
        var loud = Build();
        GiveStock(loud, "i_audit_ingot", 20);
        TestHarness.ClearQueuedPackets(loud.State);
        Assert.True(loud.Client.BeginPendingCraft(Recipe(loud, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(loud);
        Assert.Contains("in your pack", SentText(loud.State));

        var quiet = Build();
        GiveStock(quiet, "i_audit_ingot", 20);
        quiet.Crafter.SetTag("SILENT_MAKE", "1");
        TestHarness.ClearQueuedPackets(quiet.State);
        Assert.True(quiet.Client.BeginPendingCraft(Recipe(quiet, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(quiet);
        Assert.Single(ItemsInPack(quiet, 0x0F51));
        Assert.DoesNotContain("in your pack", SentText(quiet.State));
    }

    // --- E02: @SkillSuccess before the stage --------------------------------------

    [Fact]
    public void SkillSuccess_RunsBeforeTheStage_AndItsReturn1SpendsNothing()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);
        b.Crafter.SetTag("VETO_SUCCESS", "1");

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        Assert.Equal(1, Tag(b.Crafter, "SS_COUNT"));
        Assert.Equal(20, CountInPack(b, 0x1BF2));
        Assert.Empty(ItemsInPack(b, 0x0F51));
        Assert.False(HasTag(b.Crafter, "MI_COUNT"));
        Assert.Equal(1, Tag(b.Crafter, "AB_COUNT"));
    }

    [Fact]
    public void SkillSuccess_WithoutVeto_ThenTheItemIsMade()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        Assert.Equal(1, Tag(b.Crafter, "SS_COUNT"));
        Assert.Equal(15, CountInPack(b, 0x1BF2));
        Assert.Single(ItemsInPack(b, 0x0F51));
        Assert.False(HasTag(b.Crafter, "AB_COUNT"));
    }

    // --- E03: @Create once, and what it set is kept -----------------------------------

    [Fact]
    public void CraftedItem_CreateRunsOnce_AndKeepsTypeColourAndMore()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        var blade = Assert.Single(ItemsInPack(b, 0x0F51));
        Assert.True(blade.TryGetTag("CREATE_COUNT", out string? creates));
        Assert.Equal("1", creates);
        Assert.Equal(ItemType.WeaponSword, blade.ItemType);   // not re-stamped with the def's TYPE
        Assert.Equal(0x0481, blade.Hue.Value);
        Assert.Equal(7u, blade.More1);
    }

    // --- E07: inscription ---------------------------------------------------------------

    [Fact]
    public void InscribedScroll_TakesTheScribesSkillAsMoreY_AndNoQuality()
    {
        var b = Build();
        GiveStock(b, "i_audit_blank", 5);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_scroll"), SkillType.Inscription, reopenGump: false));
        FinishCraft(b);

        var scroll = Assert.Single(ItemsInPack(b, 0x1F2D));
        Assert.Equal(ItemType.Scroll, scroll.ItemType);
        Assert.Equal(873, scroll.MoreP.Y);
        Assert.Equal(0, scroll.Quality);
        Assert.Equal(873, Tag(b.Crafter, "MI_N1"));
        Assert.Equal(0, Tag(b.Crafter, "MI_N2"));
    }

    [Fact]
    public void ReplicatedScrolls_StillCarryTheSpellLevel()
    {
        var b = Build();
        GiveStock(b, "i_audit_blank", 5);
        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_scroll 3", null));
        FinishCraft(b);

        // The scroll definition here is not a pile, so the copies are separate scrolls;
        // only the one the trigger saw is given the level (CCharSkill.cpp:690-707).
        Assert.Equal(2, CountInPack(b, 0x0E34));
        Assert.Equal(3, CountInPack(b, 0x1F2D));
        var scrolls = ItemsInPack(b, 0x1F2D);
        Assert.Contains(scrolls, s => s.MoreP.Y == 873);
        Assert.All(scrolls, s => Assert.Equal(0, s.Quality));
    }

    // --- E19: carry capacity ---------------------------------------------------------

    [Fact]
    public void TooHeavyAResult_BouncesToTheFeet()
    {
        var b = Build(str: 10);
        GiveStock(b, "i_audit_ingot", 5);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        Assert.Empty(ItemsInPack(b, 0x0F53));
        var anvil = b.World.GetItemsInRange(b.Crafter.Position, 0).SingleOrDefault(i => i.BaseId == 0x0F53);
        Assert.NotNull(anvil);
        Assert.False(anvil!.IsDeleted);
        Assert.Equal(b.Crafter.X, anvil.X);
        Assert.Equal(b.Crafter.Y, anvil.Y);
    }

    [Fact]
    public void AStackableResult_MergesIntoThePilesInThePack()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        // Two replicated piles by the same maker are the same kind of pile.
        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_pile 2", null));
        FinishCraft(b);
        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_pile 3", null));
        FinishCraft(b);

        var pile = Assert.Single(ItemsInPack(b, 0x0F52));
        Assert.Equal(5, pile.Amount);
        Assert.Equal(10, CountInPack(b, 0x1BF2));
    }

    // --- E20: MAKEITEM item,amount -----------------------------------------------------

    [Theory]
    [InlineData("i_audit_blade 3")]
    [InlineData("i_audit_blade,3")]
    [InlineData("i_audit_blade, 3")]
    public void MakeItem_SecondArgument_IsTheReplicationQuantity(string line)
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", line, null));
        FinishCraft(b);

        // Not stackable: one item per replication.
        Assert.Equal(3, ItemsInPack(b, 0x0F51).Count);
        Assert.Equal(5, CountInPack(b, 0x1BF2));
        // The trigger ran for the item it was handed, once (CCharSkill.cpp:811).
        Assert.Equal(1, Tag(b.Crafter, "MI_COUNT"));
        Assert.All(ItemsInPack(b, 0x0F51), i => Assert.True(i.TryGetTag("CREATE_COUNT", out string? c) && c == "1"));
    }

    [Fact]
    public void MakeItem_AStackableResult_IsOnePileOfTheAmount()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_pile 4", null));
        FinishCraft(b);

        var pile = Assert.Single(ItemsInPack(b, 0x0F52));
        Assert.Equal(4, pile.Amount);
        Assert.Equal(0, pile.Quality);                      // no quality on a replication
        Assert.Equal(12, CountInPack(b, 0x1BF2));
    }

    [Fact]
    public void MakeItem_ShortStock_MakesWhatTheStockCovers()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 12);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_blade 3", null));
        FinishCraft(b);

        Assert.Equal(2, ItemsInPack(b, 0x0F51).Count);
        Assert.Equal(2, CountInPack(b, 0x1BF2));
    }

    [Fact]
    public void MakeItem_ANonNumericSecondArgument_MakesOne()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_blade abc", null));
        FinishCraft(b);

        Assert.Single(ItemsInPack(b, 0x0F51));
        Assert.Equal(15, CountInPack(b, 0x1BF2));
    }

    [Fact]
    public void MakeItem_AFailedReplication_PaysPartOfOneReplicationOnly()
    {
        var b = Build();
        b.Crafter.SetSkill(SkillType.Tinkering, 0);
        GiveStock(b, "i_audit_ingot", 20);
        b.Crafter.SetTag("FORCE_DIFF", "1000");   // 100.0 against a skill of 0
        b.Crafter.SetTag("FAIL_EFFECT", "100");   // the whole bill of the failed attempt

        Assert.True(b.Client.TryExecuteScriptCommand(b.Crafter, "MAKEITEM", "i_audit_blade 3", null));
        FinishCraft(b);

        Assert.Empty(ItemsInPack(b, 0x0F51));
        Assert.Equal(1, Tag(b.Crafter, "SF_COUNT"));
        // Skill_MakeItem(SKTRIG_FAIL) runs with its default quantity of one
        // (CCharSkill.cpp:3104): 100% of ONE replication is 5, not 15.
        Assert.Equal(15, CountInPack(b, 0x1BF2));
    }

    // --- the roll belongs to the start ----------------------------------------------

    [Fact]
    public void TheRoll_IsMadeAtTheStart_WithoutSkillUseQuick()
    {
        var b = Build();
        b.Crafter.SetSkill(SkillType.Tinkering, 0);
        GiveStock(b, "i_audit_ingot", 20);
        b.Crafter.SetTag("FORCE_DIFF", "1000");

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));

        // Skill_Start negated the difficulty already (CCharSkill.cpp:4566-4570).
        Assert.Equal(-100, b.Crafter.ActDiff);
        Assert.False(HasTag(b.Crafter, "UQ_COUNT"));

        FinishCraft(b);
        Assert.Empty(ItemsInPack(b, 0x0F51));
        Assert.Equal(1, Tag(b.Crafter, "SF_COUNT"));
        Assert.False(HasTag(b.Crafter, "SS_COUNT"));
        Assert.False(HasTag(b.Crafter, "UQ_COUNT"));
        Assert.Equal(0, b.Crafter.ActDiff);          // Skill_Cleanup
    }

    [Fact]
    public void ADifficultyOfZero_IsNeverRolled()
    {
        var b = Build();
        b.Crafter.SetSkill(SkillType.Tinkering, 0);
        GiveStock(b, "i_audit_ingot", 20);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        Assert.Equal(0, b.Crafter.ActDiff);
        FinishCraft(b);

        Assert.Single(ItemsInPack(b, 0x0F51));
    }

    [Fact]
    public void ANegativeActDiffFromSkillStart_CancelsTheStart()
    {
        var b = Build();
        GiveStock(b, "i_audit_ingot", 20);
        b.Crafter.SetTag("FORCE_DIFF", "-1");

        Assert.False(b.Client.BeginPendingCraft(Recipe(b, "i_audit_blade"), SkillType.Tinkering, reopenGump: false));
        Assert.Equal(20, CountInPack(b, 0x1BF2));
    }

    // --- carry check in GM mode, drop triggers and sound ------------------------------

    [Fact]
    public void GmMode_CarriesAnything_ButAGmWithGmModeOffDoesNot()
    {
        var on = Build(str: 10);
        on.Crafter.PrivLevel = PrivLevel.GM;
        GiveStock(on, "i_audit_ingot", 5);
        Assert.True(on.Client.BeginPendingCraft(Recipe(on, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(on);
        Assert.Single(ItemsInPack(on, 0x0F53));

        var off = Build(str: 10);
        off.Crafter.PrivLevel = PrivLevel.GM;
        Assert.True(off.Crafter.TrySetProperty("GM", "0"));
        Assert.False(off.Crafter.IsGmMode);
        GiveStock(off, "i_audit_ingot", 5);
        Assert.True(off.Client.BeginPendingCraft(Recipe(off, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(off);
        Assert.Empty(ItemsInPack(off, 0x0F53));
    }

    [Fact]
    public void ABounceToTheGround_RunsDropOnGround_AndPlaysTheDropSound()
    {
        var b = Build(str: 10);
        GiveStock(b, "i_audit_ingot", 5);
        var sounds = new List<ushort>();
        b.Client.BroadcastNearby = (_, _, packet, _) =>
        {
            if (packet is SphereNet.Network.Packets.Outgoing.PacketSound)
            {
                var bytes = packet.Build().Span;
                sounds.Add((ushort)((bytes[2] << 8) | bytes[3]));
            }
        };

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);

        var anvil = b.World.GetItemsInRange(b.Crafter.Position, 0).Single(i => i.BaseId == 0x0F53);
        Assert.True(anvil.TryGetTag("DG_N2", out string? n2));
        Assert.Equal("1", n2);
        Assert.True(anvil.TryGetTag("DG_N1", out string? n1));
        Assert.True(long.Parse(n1!) > 0);
        Assert.True(anvil.TryGetTag("DG_S1", out string? s1));
        Assert.StartsWith("100,100", s1);
        Assert.False(anvil.TryGetTag("DOI_ARGO", out _));      // never offered to the pack
        // CItem::GetDropSound(pPack): the pack exists, so "onto something" (0x057).
        Assert.Contains((ushort)0x057, sounds);
        Assert.Contains("at your feet", SentText(b.State));
    }

    [Fact]
    public void ThePack_SeesDropOnSelf_AndItsReturn1SendsTheItemToTheFeet()
    {
        var b = Build();
        b.Crafter.PrivLevel = PrivLevel.GM;     // GM mode: the heavy anvil may go in the pack
        b.Pack.Events.Add(ResourceId.FromEventName("e_pack_probe"));
        GiveStock(b, "i_audit_ingot", 5);

        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);
        var inPack = Assert.Single(ItemsInPack(b, 0x0F53));
        Assert.True(inPack.TryGetTag("DOI_ARGO", out string? doi));
        Assert.Equal(b.Pack.Uid.Value, (uint)long.Parse(doi!.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber));
        Assert.True(b.Pack.TryGetTag("DOS_ARGO", out _));

        b.Pack.SetTag("REFUSE", "1");
        GiveStock(b, "i_audit_ingot", 5);
        Assert.True(b.Client.BeginPendingCraft(Recipe(b, "i_audit_anvil"), SkillType.Tinkering, reopenGump: false));
        FinishCraft(b);
        Assert.Single(ItemsInPack(b, 0x0F53));
        Assert.Single(b.World.GetItemsInRange(b.Crafter.Position, 0), i => i.BaseId == 0x0F53);
    }
}
