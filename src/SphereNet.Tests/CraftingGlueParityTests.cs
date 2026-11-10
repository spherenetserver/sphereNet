using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Network.State;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The glue around the crafting engine (Source-X CCharUse.cpp Use_Repair, CClientUse.cpp
/// Skill_Menu / Cmd_Skill_Menu_Build, CClientTarg.cpp OnTarg_Use_Item, CCharSkill.cpp
/// Skill_MakeItem):
///   - repair is gated by Armor_IsRepairable, speaks through REPAIR_MSG as an emote,
///     names a missing component, destroys a piece worn down to nothing and ends
///     silently without a SKILLMAKE skill;
///   - the item REPAIR verb and the REPAIR cursor both reach Use_Repair;
///   - a skill menu lists only what SKTRIG_SELECT lets through and says
///     DEFMSG_CANT_MAKE_RES when nothing is left;
///   - MAKEITEM keeps the client's TARG as ACT, which @SkillMakeItem reads as ARGO;
///   - crafting tools hand their id and themselves to the menu; a chop tool or a dagger
///     aimed at a log opens one;
///   - scissors cut silently.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftingGlueParityTests : IDisposable
{
    private const ushort ArmorId = 0x13BB;
    private const ushort IronIngotId = 0x1BF2;
    private const string Nl = "\r\n";

    private readonly string _scriptPath;

    public CraftingGlueParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"spherenet_craftglue_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, ScriptText);
    }

    public void Dispose()
    {
        try { File.Delete(_scriptPath); } catch { }
    }

    // ---------------------------------------------------------------------------
    //  Repair, on the engine alone
    // ---------------------------------------------------------------------------

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => Math.Min(value, Math.Max(0, maxValue - 1));
    }

    private sealed class RepairSink(Character self, GameWorld world, CraftingEngine? crafting, Random random)
        : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = random;
        public GameWorld World { get; } = world;
        public CraftingEngine? Crafting { get; } = crafting;
        public List<string> Messages { get; } = [];
        public List<string> Emotes { get; } = [];
        public List<ushort> Animations { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) => Emotes.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) => Animations.Add(animId);
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private sealed record Forge(GameWorld World, Character Smith, Item Piece, RepairSink Sink);

    private static void DefineArmor(string? skillMake)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, ArmorId))
        {
            Type = ItemType.Armor,
        };
        if (skillMake != null)
            def.SkillMakeRaw = skillMake;
        DefinitionLoader.SetItemDef(ArmorId, def);
    }

    private static void SkillRolls(bool armsLore, bool tinkering)
    {
        Character.OnSkillUseQuick = (_, skillId, _, result) =>
            skillId == (int)SkillType.ArmsLore ? (armsLore ? 1 : 0) :
            skillId == (int)SkillType.Tinkering ? (tinkering ? 1 : 0) : result;
    }

    private static Forge RepairBench(int hitsCur = 20, int hitsMax = 100, bool anvil = true,
        string? skillMake = "Tinkering 50.0", ItemType pieceType = ItemType.Armor,
        CraftingEngine? crafting = null, Random? random = null, bool ground = false)
    {
        DefineArmor(skillMake);
        var world = TestHarness.CreateWorld();
        ObjBaseWorld(world);
        var smith = world.CreateCharacter();
        smith.IsPlayer = true;
        smith.SetSkill(SkillType.Tinkering, 2000);
        smith.SetSkill(SkillType.ArmsLore, 1000);
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        smith.Backpack = pack;
        smith.Equip(pack, Layer.Pack);

        if (anvil)
        {
            var block = world.CreateItem();
            block.ItemType = ItemType.Anvil;
            world.PlaceItem(block, new Point3D(101, 100, 0, 0));
        }

        var piece = world.CreateItem();
        piece.BaseId = ArmorId;
        piece.ItemType = pieceType;
        piece.Name = "damaged armor";
        piece.HitsMax = hitsMax;
        piece.HitsCur = hitsCur;
        if (ground)
            world.PlaceItem(piece, new Point3D(150, 100, 0, 0));
        else
            Assert.True(pack.TryAddItem(piece));

        return new Forge(world, smith, piece,
            new RepairSink(smith, world, crafting, random ?? new FixedRandom(7)));
    }

    private static void ObjBaseWorld(GameWorld world)
    {
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
    }

    [Fact]
    public void PlateTypeArmorWithoutTheRepairFlagIsRepaired()
    {
        // Armor_IsRepairable (CItem.cpp:4846): the type switch decides, not a REPAIR=1
        // on the definition. The old gate refused every piece that lacked the flag.
        SkillRolls(armsLore: true, tinkering: true);
        var forge = RepairBench();

        Assert.True(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.Equal(100, forge.Piece.HitsCur);
        Assert.Empty(forge.Sink.Messages);       // nothing whispered: the result is an emote
        var emote = Assert.Single(forge.Sink.Emotes);
        Assert.Equal(ServerMessages.GetFormatted(Msg.RepairMsg,
            ServerMessages.Get(Msg.Repair1), "damaged armor"), emote);
        Assert.Contains((ushort)AnimationType.AttackWeapon, forge.Sink.Animations);   // ANIM_ATTACK_1H_SLASH
    }

    [Fact]
    public void ClothingIsNotRepairableAndNoItemIsTheSame()
    {
        SkillRolls(armsLore: true, tinkering: true);
        var forge = RepairBench(pieceType: ItemType.Clothing);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));
        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, null));

        Assert.Equal(2, forge.Sink.Messages.Count);
        Assert.All(forge.Sink.Messages, m => Assert.Equal(ServerMessages.Get(Msg.RepairNot), m));
        Assert.Equal(20, forge.Piece.HitsCur);
    }

    [Fact]
    public void APieceOutOfReachIsRefusedWithTheReachLine()
    {
        // CanUse(pItemArmor, true) failing is DEFMSG_REPAIR_REACH (CCharUse.cpp:760).
        SkillRolls(armsLore: true, tinkering: true);
        var forge = RepairBench(ground: true);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.Equal(ServerMessages.Get(Msg.RepairReach), Assert.Single(forge.Sink.Messages));
    }

    [Fact]
    public void WithoutASkillMakeSkillTheRepairEndsInSilence()
    {
        // No RES_SKILL entry in SKILLMAKE: Use_Repair just returns false (:812). The
        // old code invented "Tinkering at 50".
        SkillRolls(armsLore: true, tinkering: true);
        var forge = RepairBench(skillMake: null);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.Empty(forge.Sink.Messages);
        Assert.Empty(forge.Sink.Emotes);
        Assert.Equal(20, forge.Piece.HitsCur);
    }

    [Fact]
    public void AFailedRepairIsAnEmoteAndTheOrdinaryBranchChangesNothing()
    {
        SkillRolls(armsLore: true, tinkering: false);
        var forge = RepairBench(random: new FixedRandom(1));   // neither 1-in-6 nor 1-in-3 fires

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.Equal(20, forge.Piece.HitsCur);
        Assert.Equal(ServerMessages.GetFormatted(Msg.RepairMsg,
            ServerMessages.Get(Msg.Repair4), "damaged armor"), Assert.Single(forge.Sink.Emotes));
    }

    [Fact]
    public void APieceWornDownToNothingIsDestroyed()
    {
        // m_wHitsCur <= 0 turns the text into REPAIR_5 and deletes the item (:873-876).
        SkillRolls(armsLore: true, tinkering: false);
        var forge = RepairBench(hitsCur: 1, hitsMax: 100, random: new FixedRandom(0));   // the 1-in-6 branch

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.True(forge.Piece.IsDeleted);
        Assert.Equal(ServerMessages.GetFormatted(Msg.RepairMsg,
            ServerMessages.Get(Msg.Repair5), "damaged armor"), Assert.Single(forge.Sink.Emotes));
    }

    [Fact]
    public void AMissingComponentIsNamedToTheSmith()
    {
        // DEFMSG_REPAIR_LACK_1 with the component's name (CCharUse.cpp:817-820).
        SkillRolls(armsLore: true, tinkering: true);
        var world0 = TestHarness.CreateWorld();
        DefinitionLoader.SetItemDef(IronIngotId,
            new ItemDef(new ResourceId(ResType.ItemDef, IronIngotId)) { Type = ItemType.Ingot, Name = "iron ingot" });
        var crafting = new CraftingEngine(world0);
        var recipe = new CraftRecipe { ResultDefId = ArmorId, ResultItemId = ArmorId };
        recipe.Resources.Add(new CraftResource { ItemId = IronIngotId, Amount = 100 });
        crafting.RegisterRecipe(recipe);

        var forge = RepairBench(crafting: crafting);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Piece));

        Assert.Equal(ServerMessages.GetFormatted(Msg.RepairLack1, "iron ingot"),
            Assert.Single(forge.Sink.Messages));
        Assert.Equal(20, forge.Piece.HitsCur);
    }

    // ---------------------------------------------------------------------------
    //  Scripted bench: client, menus, tools
    // ---------------------------------------------------------------------------

    private const string ScriptText = """
        [ITEMDEF i_glue_ingot]
        ID=01bf2
        NAME=glue ingot
        CAN=0100

        [ITEMDEF i_glue_blade]
        ID=0f51
        NAME=glue blade
        SKILLMAKE=Tinkering 0.0
        RESOURCES=5 i_glue_ingot

        [ITEMDEF i_glue_adept]
        ID=0f5c
        NAME=glue adept
        SKILLMAKE=Tinkering 90.0
        RESOURCES=1 i_glue_ingot

        [SKILLMENU sm_glue_mixed]
        Pick
        ON=0 easy
        MAKEITEM=i_glue_blade
        ON=0 hard
        MAKEITEM=i_glue_adept
        ON=0 nested
        SKILLMENU=sm_glue_empty

        [SKILLMENU sm_glue_empty]
        Nothing
        ON=0 hard
        MAKEITEM=i_glue_adept

        [SKILLMENU sm_glue_single]
        Only nested
        ON=0 deeper
        SKILLMENU=sm_glue_leaf

        [SKILLMENU sm_glue_leaf]
        Leaf
        ON=0 first
        MAKEITEM=i_glue_blade
        ON=0 second
        MAKEITEM=i_glue_blade

        [SKILLMENU sm_glue_gated]
        Gated
        ON=0 gated
        TEST=TINKERING 50.0
        MAKEITEM=i_glue_blade
        ON=0 plain
        MAKEITEM=i_glue_blade

        [SKILLMENU sm_tinker]
        Tinker
        ON=0 first
        MAKEITEM=i_glue_blade
        ON=0 second
        MAKEITEM=i_glue_blade

        [SKILLMENU sm_carpentry]
        Carpentry
        ON=0 first
        MAKEITEM=i_glue_blade
        ON=0 second
        MAKEITEM=i_glue_blade

        [SKILLMENU sm_bowcraft]
        Bowcraft
        ON=0 first
        MAKEITEM=i_glue_blade
        ON=0 second
        MAKEITEM=i_glue_blade

        [SKILLMENU sm_inscription]
        Inscription
        ON=0 first
        MAKEITEM=i_glue_blade
        ON=0 second
        MAKEITEM=i_glue_blade

        [EVENTS e_glue_probe]
        ON=@SkillMakeItem
        TAG.MI_ARGO=<ARGO.UID>

        ON=@SkillMenu
        TAG.SM_USED=<LOCAL.ITEMUSED>
        """;

    private sealed record Bench(GameWorld World, GameClient Client, NetState State, Character Me,
        Item Pack, CraftingEngine Engine);

    private Bench Build(int tinkering = 500)
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
        ObjBaseWorld(world);

        var engine = new CraftingEngine(world);
        Assert.True(engine.LoadRecipesFromDefs(resources) >= 2);

        var state = TestHarness.CreateActiveNetState(lf, 7702);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        client.SetEngines(craftingEngine: engine, skillHandlers: new SkillHandlers(world));
        typeof(GameClient).GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, dispatcher);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = PrivLevel.Player;
        me.Str = 100;
        me.MaxHits = me.Hits = 100;
        me.SetSkill(SkillType.Tinkering, (ushort)tinkering);
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        me.Events.Add(ResourceId.FromEventName("e_glue_probe"));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        me.Equip(pack, Layer.Pack);

        var stock = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(stock, resources.ResolveDefName("i_glue_ingot").Index, fireCreate: false);
        stock.Amount = 50;
        Assert.True(pack.TryAddItem(stock));
        return new Bench(world, client, state, me, pack, engine);
    }

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

    private static int Offered(Bench b) =>
        ((IClientContext)b.Client).PendingMenuOptions?.Count ?? 0;

    private static string OpenMenu(Bench b) => ((IClientContext)b.Client).PendingMenuDefname;

    private static void FinishCraft(Bench b)
    {
        typeof(ClientWorldFeaturesHandler)
            .GetField("_pendingCraftNextStroke", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(b.Client.WorldFeatures, 0L);
        b.Client.TickPendingCraft();
    }

    private static void SetTarg(Character ch, Item obj) =>
        ch.SetTag("TARG.UID", $"0{obj.Uid.Value:x}");

    private Item Put(Bench b, ItemType type, ushort baseId, string name = "", bool inPack = true)
    {
        var item = b.World.CreateItem();
        item.BaseId = baseId;
        item.ItemType = type;
        if (name.Length > 0)
            item.Name = name;
        if (inPack)
            Assert.True(b.Pack.TryAddItem(item));
        else
            b.World.PlaceItem(item, new Point3D(101, 100, 0, 0));
        return item;
    }

    // --- SKILLMENU build: SKTRIG_SELECT ------------------------------------------

    [Fact]
    public void AMenuListsOnlyWhatTheCharacterCanMakeAndHidesAnEmptyNestedMenu()
    {
        // Tinkering 50.0: the plain recipe passes, the one needing 90.0 is hidden, and
        // the entry that only leads to a menu with nothing in it goes with it
        // (Cmd_Skill_Menu_Build, CClientUse.cpp:925-966).
        var b = Build(tinkering: 500);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "SKILLMENU", "sm_glue_mixed", null));

        Assert.Equal(1, Offered(b));
        Assert.Equal("sm_glue_mixed", OpenMenu(b));
    }

    [Fact]
    public void AMenuWithNothingMakableSaysSo()
    {
        var b = Build(tinkering: 500);
        TestHarness.ClearQueuedPackets(b.State);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "SKILLMENU", "sm_glue_empty", null));

        Assert.Equal(0, Offered(b));
        Assert.Contains(ServerMessages.Get(Msg.CantMakeRes), SentText(b.State));
    }

    [Fact]
    public void ARecipeWhoseMaterialsAreGoneIsNotListed()
    {
        // SELECT tests the stock too (ResourceConsume in test mode, :920).
        var b = Build(tinkering: 500);
        foreach (var item in b.Pack.Contents.ToList())
            b.World.RemoveItem(item);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "SKILLMENU", "sm_glue_leaf", null));

        Assert.Equal(0, Offered(b));
    }

    [Fact]
    public void ASingleNestedMenuIsOpenedWithoutAsking()
    {
        // iShowCount == 1 and a nested menu behind it: Cmd_Skill_Menu selects it
        // (CClientUse.cpp:729-745).
        var b = Build(tinkering: 500);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "SKILLMENU", "sm_glue_single", null));

        Assert.Equal("sm_glue_leaf", OpenMenu(b));
        Assert.Equal(2, Offered(b));
    }

    [Fact]
    public void AnEntryWhoseTestNoLongerHoldsWhenPickedIsCantMake()
    {
        // The pick re-reads the entry's TEST lines; failing them is DEFMSG_CANT_MAKE
        // (Cmd_Skill_Menu, CClientUse.cpp:707-712).
        var b = Build(tinkering: 500);
        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "SKILLMENU", "sm_glue_gated", null));
        Assert.Equal(2, Offered(b));
        ushort menuId = ((IClientContext)b.Client).PendingMenuId;

        b.Me.SetSkill(SkillType.Tinkering, 100);
        TestHarness.ClearQueuedPackets(b.State);
        b.Client.HandleMenuChoice(b.Me.Uid.Value, menuId, (ushort)1, (ushort)0);

        Assert.Contains(ServerMessages.Get(Msg.CantMake), SentText(b.State));
        Assert.False(b.Client.WorldFeatures.GetType()
            .GetField("_pendingCraftRecipe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(b.Client.WorldFeatures) != null);
    }

    [Fact]
    public void TheUsedObjectMustMatchATypeTheRecipeNames()
    {
        // The SELECT filter (CCharSkill.cpp:900-909): a recipe that names a TYPE in its
        // SKILLMAKE is offered only for an object of that type - or for none at all.
        var b = Build();
        var recipe = new CraftRecipe { ResultDefId = 1, ResultItemId = 0x0F51 };
        recipe.RequiredToolTypes.Add(ItemType.CarpentryChop);
        var chop = Put(b, ItemType.CarpentryChop, 0x0F43);
        var other = Put(b, ItemType.Cloth, 0x1766);

        Assert.True(CraftingEngine.MatchesUsedItem(recipe, chop));
        Assert.False(CraftingEngine.MatchesUsedItem(recipe, other));
        Assert.True(CraftingEngine.MatchesUsedItem(recipe, null));
        Assert.True(CraftingEngine.MatchesUsedItem(new CraftRecipe(), other));   // names no type: any object
    }

    // --- MAKEITEM: ACT -------------------------------------------------------------

    [Fact]
    public void MakeItemKeepsTheClientsTargetAsActAndHandsItToTheTriggerAsArgo()
    {
        // CHV_MAKEITEM: ACT = the client's Targ_UID (CChar.cpp:4720); START keeps it
        // (CCharSkill.cpp:960-964) and @SkillMakeItem reads it as ARGO (:811-827).
        var b = Build();
        var cloth = Put(b, ItemType.Cloth, 0x1766, "cloth");
        SetTarg(b.Me, cloth);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "MAKEITEM", "i_glue_blade", null));
        FinishCraft(b);

        Assert.True(b.Me.TryGetTag("MI_ARGO", out string? raw));
        Assert.True(ScriptNumber.TryParseToken(raw, out long argo));
        Assert.Equal(cloth.Uid.Value, (uint)argo);
    }

    [Fact]
    public void ATargetForgottenAtTheSkillButtonIsNotCarriedIntoTheNextCraft()
    {
        var b = Build();
        var cloth = Put(b, ItemType.Cloth, 0x1766, "cloth");
        SetTarg(b.Me, cloth);

        b.Client.OpenCraftingGump(SkillType.Tinkering);   // the skill button: m_Targ_UID.InitUID

        Assert.False(b.Me.TryGetTag("TARG.UID", out string? left) && !string.IsNullOrEmpty(left));
    }

    // --- tools hand themselves to the menu ----------------------------------------

    [Fact]
    public void ACraftingToolPassesItsIdToTheMenuAndBecomesTheTarget()
    {
        // Skill_Menu(skill, menu, pItem->GetID()) with m_Targ_UID = pItem
        // (CClientUse.cpp:142, :540-549).
        var b = Build();
        var tools = Put(b, ItemType.TinkerTools, 0x1EB8, "tinker's tools");

        b.Client.HandleDoubleClick(tools.Uid.Value);

        Assert.True(b.Me.TryGetTag("SM_USED", out string? used));
        Assert.True(ScriptNumber.TryParseToken(used, out long usedId));
        Assert.Equal(0x1EB8, usedId);
        Assert.Equal(tools.Uid, WorldFeaturesTarg(b));
        Assert.Equal("sm_tinker", OpenMenu(b));
    }

    private static Serial WorldFeaturesTarg(Bench b) => ClientWorldFeaturesHandler.TargUid(b.Me);

    [Fact]
    public void ACarpentryChopToolAsksForATargetInsteadOfOpeningTheMenu()
    {
        // CClientUse.cpp:420-424.
        var b = Build();
        var chop = Put(b, ItemType.CarpentryChop, 0x0F43, "hatchet");

        b.Client.HandleDoubleClick(chop.Uid.Value);

        Assert.True(b.Client.HasPendingTarget);
        Assert.Equal(0, Offered(b));
    }

    [Fact]
    public void AChopToolAimedAtALogOpensTheCarpentryMenu()
    {
        // OnTarg_Use_Item IT_LOG with an IT_CARPENTRY_CHOP tool (CClientTarg.cpp:1900).
        var b = Build();
        var chop = Put(b, ItemType.CarpentryChop, 0x0F43, "hatchet");
        var log = Put(b, ItemType.Log, 0x1BDD, "logs", inPack: false);

        b.Client.HandleDoubleClick(chop.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, log.Uid.Value, 0, 0, 0, 0);

        Assert.Equal("sm_carpentry", OpenMenu(b));
        Assert.Equal(log.Uid, WorldFeaturesTarg(b));
    }

    [Fact]
    public void ADaggerAimedAtALogOpensTheBowcraftMenuAndAnotherBladeIsTold()
    {
        var b = Build();
        var dagger = Put(b, ItemType.WeaponFence, 0x0F51, "dagger");
        var sword = Put(b, ItemType.WeaponSword, 0x13B9, "sword");
        var log = Put(b, ItemType.Log, 0x1BDD, "logs", inPack: false);

        b.Client.HandleDoubleClick(dagger.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, log.Uid.Value, 0, 0, 0, 0);
        Assert.Equal("sm_bowcraft", OpenMenu(b));

        TestHarness.ClearQueuedPackets(b.State);
        b.Client.HandleDoubleClick(sword.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, log.Uid.Value, 0, 0, 0, 0);
        Assert.Contains(ServerMessages.Get(Msg.ItemuseLogUse), SentText(b.State));
    }

    [Fact]
    public void ALogThatCannotBeMovedIsUnableNotSmashed()
    {
        var b = Build();
        var dagger = Put(b, ItemType.WeaponFence, 0x0F51, "dagger");
        var log = Put(b, ItemType.Log, 0x1BDD, "logs", inPack: false);
        log.SetAttr(ObjAttributes.Move_Never);
        TestHarness.ClearQueuedPackets(b.State);

        b.Client.HandleDoubleClick(dagger.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, log.Uid.Value, 0, 0, 0, 0);

        Assert.Contains(ServerMessages.Get(Msg.ItemuseLogUnable), SentText(b.State));
        Assert.Equal(0, Offered(b));
    }

    // --- scissors --------------------------------------------------------------------

    [Fact]
    public void ScissorsCutClothIntoBandagesWithoutASuccessLine()
    {
        // CClientTarg.cpp:2175-2178: ItemBounce and a snip, no text.
        var b = Build();
        var scissors = Put(b, ItemType.Scissors, 0x0F9E, "scissors");
        var cloth = Put(b, ItemType.Cloth, 0x1766, "cloth");
        cloth.Amount = 3;
        TestHarness.ClearQueuedPackets(b.State);

        b.Client.HandleDoubleClick(scissors.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, cloth.Uid.Value, 0, 0, 0, 0);

        var bandages = Assert.Single(b.Pack.Contents, i => !i.IsDeleted && i.BaseId == 0x0E21);
        Assert.Equal(3, bandages.Amount);
        Assert.True(cloth.IsDeleted);
        string sent = SentText(b.State);
        Assert.DoesNotContain("You cut", sent);
        Assert.DoesNotContain(ServerMessages.Get(Msg.ItemuseScissorsUse), sent);
    }

    [Fact]
    public void ScissorsOnSomethingUncuttableTellWhatTheyAreFor()
    {
        var b = Build();
        var scissors = Put(b, ItemType.Scissors, 0x0F9E, "scissors");
        var gem = Put(b, ItemType.Gem, 0x0F10, "gem");
        TestHarness.ClearQueuedPackets(b.State);

        b.Client.HandleDoubleClick(scissors.Uid.Value);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, gem.Uid.Value, 0, 0, 0, 0);

        Assert.Contains(ServerMessages.Get(Msg.ItemuseScissorsUse), SentText(b.State));
        Assert.False(gem.IsDeleted);
    }

    // --- inscription ------------------------------------------------------------------

    [Fact]
    public void ABlankScrollAnywhereOnTheCharacterOpensTheInscriptionMenu()
    {
        // ContentFind(RES_TYPEDEF IT_SCROLL_BLANK) (CClientUse.cpp:1301) searches
        // everything carried - here the scroll is held in hand, not in the pack.
        var b = Build();
        var scroll = b.World.CreateItem();
        scroll.ItemType = ItemType.ScrollBlank;
        scroll.BaseId = 0x0E34;
        b.Me.Equip(scroll, Layer.OneHanded);
        TestHarness.ClearQueuedPackets(b.State);

        b.Client.InscriptionMenu();

        Assert.Equal("sm_inscription", OpenMenu(b));
        Assert.DoesNotContain(ServerMessages.Get(Msg.InscriptionFail), SentText(b.State));
    }

    // --- the item REPAIR verb ---------------------------------------------------------

    [Fact]
    public void TheItemRepairVerbRepairsThroughTheSourceCharacter()
    {
        // CIV_REPAIR (CItem.cpp:3676) -> pCharSrc->Use_Repair(this).
        SkillRolls(armsLore: true, tinkering: true);
        var b = Build(tinkering: 2000);
        DefineArmor("Tinkering 50.0");   // after Build: LoadAll starts the tables over
        b.Me.SetSkill(SkillType.ArmsLore, 1000);
        var anvil = b.World.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        b.World.PlaceItem(anvil, new Point3D(101, 100, 0, 0));
        var armor = Put(b, ItemType.Armor, ArmorId, "damaged armor");
        armor.HitsMax = 100;
        armor.HitsCur = 20;

        Assert.True(armor.TryExecuteCommand("REPAIR", "", b.Client));

        Assert.Equal(100, armor.HitsCur);
    }

    [Fact]
    public void TheRepairCursorRunsTheSameRepairOnThePickedItem()
    {
        // CV_REPAIR (CClient.cpp:1506) -> CLIMODE_TARG_REPAIR -> Use_Repair
        // (CClientEvent.cpp:2556).
        SkillRolls(armsLore: true, tinkering: true);
        var b = Build(tinkering: 2000);
        DefineArmor("Tinkering 50.0");   // after Build: LoadAll starts the tables over
        b.Me.SetSkill(SkillType.ArmsLore, 1000);
        var anvil = b.World.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        b.World.PlaceItem(anvil, new Point3D(101, 100, 0, 0));
        var armor = Put(b, ItemType.Armor, ArmorId, "damaged armor");
        armor.HitsMax = 100;
        armor.HitsCur = 20;
        TestHarness.ClearQueuedPackets(b.State);

        Assert.True(b.Client.TryExecuteScriptCommand(b.Me, "REPAIR", "", null));
        Assert.Contains(ServerMessages.Get(Msg.SelectItemRepair), SentText(b.State));
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, armor.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(100, armor.HitsCur);
    }

    // --- maker's mark --------------------------------------------------------------------

    [Fact]
    public void ThePiecesMakersMarkIsTheDefmessageLine()
    {
        // CCharSkill.cpp:799: DEFMSG_GRANDMASTER_MARK(item name, maker name).
        var b = Build();
        b.Me.Name = "Smith";
        b.Me.SetSkill(SkillType.Tinkering, 1000);
        var recipe = b.Engine.AllRecipes.Values.First(r => r.ResultName == "glue blade" || r.ResultItemId == 0x0F51);

        Item? marked = null;
        for (int i = 0; i < 400 && marked == null; i++)
        {
            var outcome = b.Engine.CraftSuccess(b.Me, recipe, 1);
            Assert.NotNull(outcome);
            if (outcome!.Quality > 175)
                marked = outcome.Item;
            foreach (var spare in b.Pack.Contents.ToList())
                if (spare.BaseId != IronIngotId)
                    b.World.RemoveItem(spare);
            // keep the stock topped up
            var stock = b.Pack.Contents.FirstOrDefault(c => c.BaseId == IronIngotId);
            if (stock != null)
                stock.Amount = 50;
        }

        Assert.NotNull(marked);
        Assert.Equal(ServerMessages.GetFormatted(Msg.GrandmasterMark, "glue blade", "Smith"), marked!.Name);
    }
}
