using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// World-features handler extracted from the GameClient.WorldFeatures partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Crafting gump, vendor buy/sell, secure trade, rename, guild/house gumps,
/// doors, potions, skill use, 0xBF extended-command dispatch, party commands,
/// context menus. Method bodies moved verbatim; the private context shims
/// below enumerate exactly what this handler needs from GameClient.
/// </summary>
public sealed class ClientWorldFeaturesHandler
{
    private readonly IClientContext _client;

    internal ClientWorldFeaturesHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    /// <summary>Container-add for this client: see IClientContext.SendContainerItem.</summary>
    private void SendContainerItemPacket(SphereNet.Network.Packets.Outgoing.PacketContainerItem packet)
        => _client.SendContainerItem(packet);

    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private HousingEngine? _housingEngine => _client.Housing;
    private TradeManager? _tradeManager => _client.TradeM;
    private CraftingEngine? _craftingEngine => _client.CraftE;
    private GuildManager? _guildManager => _client.GuildM;
    private PartyManager? _partyManager => _client.PartyM;
    private SkillHandlers? _skillHandlers => _client.SkillH;
    private ClientTargetState Targets => _client.Targets;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private const int UpdateRange = GameClient.UpdateRange;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint, Character>? BroadcastMoveNearby => _client.BroadcastMoveNearby;
    private Action<Character>? BroadcastCharacterAppear => _client.BroadcastCharacterAppear;
    private Action<Serial, SphereNet.Network.Packets.PacketWriter>? SendToChar => _client.SendToChar;
    private Action<Character, Character, Item, Item>? SendTradeToPartner => _client.SendTradeToPartner;
    private Action<Character, Item, Item>? SendTradeItemToPartner => _client.SendTradeItemToPartner;
    private Action<Character, uint>? SendTradeCloseToPartner => _client.SendTradeCloseToPartner;
    private Action<Character, SecureTrade>? SendTradeUpdateToPartner => _client.SendTradeUpdateToPartner;
    private Action<Character, string>? SendTradeMessageToPartner => _client.SendTradeMessageToPartner;
    private Action<Character>? RefreshBackpackForPartner => _client.RefreshBackpackForPartner;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) => _client.SetPendingTarget(callback, cursorType);
    private void NpcSpeech(Character npc, string text) => _client.NpcSpeech(npc, text);
    private void RefreshBackpackContents() => _client.RefreshBackpackContents();
    private void SendCharacterStatus(Character ch) => _client.SendCharacterStatus(ch);
    private SphereNet.Network.Packets.PacketWriter BuildWorldItemPacket(uint serial, ushort itemId, ushort amount, short x, short y, sbyte z, ushort hue, byte direction = 0) => _client.BuildWorldItemPacket(serial, itemId, amount, x, y, z, hue, direction);
    private void SendPaperdoll(Character ch) => _client.SendPaperdoll(ch);
    private void SendOpenContainer(Item container) => _client.SendOpenContainer(container);
    private void HandleVendorInteraction(Character vendor) => _client.HandleVendorInteraction(vendor);
    private void OpenBankBox() => _client.OpenBankBox();
    private void HandleDoubleClick(uint uid) => _client.HandleDoubleClick(uid);
    private Character? DismountCharacter() => _client.DismountCharacter();
    private void BroadcastDeleteObject(uint uid) => _client.BroadcastDeleteObject(uid);
    private void ResetWalkValidator() => _client.ResetWalkValidator();
    private byte BuildMobileFlags(Character ch) => _client.BuildMobileFlags(ch);
    private void PlayAnimation(Character actor, ushort action) =>
        _client.PlayAnimation(actor, action);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private void BeginInfoSkill(SkillType skill, int skillId) => _client.BeginInfoSkill(skill, skillId);
    private void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind) => _client.BeginActiveSkill(skill, skillId, kind);
    private void BeginTargetedSkill(SkillType skill, int skillId, Serial targetUid) => _client.BeginTargetedSkill(skill, skillId, targetUid);
    private void BeginHouseCustomization(Item multi) => _client.BeginHouseCustomization(multi);
    private void HandleQueryDesignDetails(byte[] data) => _client.HandleQueryDesignDetails(data);
    private void HandleChatOpen() => _client.HandleChatOpen();

    private static readonly IReadOnlyDictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>> s_extendedCommandHandlers =
        BuildExtendedCommandHandlers();


    /// <summary>
    /// Open a crafting gump for the given skill.
    /// Lists available recipes and lets the player select one to craft.
    /// </summary>
    /// <summary>Recipes shown per craft-menu page (rows that fit between y=50..390).</summary>
    private const int CraftRecipesPerPage = 15;
    private const int CraftButtonNextPage = 1;
    private const int CraftButtonPrevPage = 2;
    private const int CraftButtonRecipeBase = 100;

    public void OpenCraftingGump(SkillType craftSkill) => OpenCraftingGump(craftSkill, 0, fireMenuTrigger: true);

    private void OpenCraftingGump(SkillType craftSkill, int page, bool fireMenuTrigger = false)
    {
        if (_character == null || _craftingEngine == null) return;
        if (!SkillHandlers.CanUse(_character, craftSkill)) return;

        if (fireMenuTrigger &&
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMenu,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True)
            return;

        var recipes = _craftingEngine.GetRecipesBySkill(craftSkill);
        if (recipes.Count == 0)
        {
            SysMessage(ServerMessages.Get("craft_no_recipes"));
            return;
        }

        // Clamp the page so a stale Next/Prev (or a recipe-count change) can't land
        // on an empty page — long recipe lists are paged instead of truncated, so
        // every recipe is reachable (the old single page cut off at y>390).
        int pageCount = (recipes.Count + CraftRecipesPerPage - 1) / CraftRecipesPerPage;
        page = Math.Clamp(page, 0, Math.Max(0, pageCount - 1));
        int skip = page * CraftRecipesPerPage;

        var gump = new GumpBuilder(_character.Uid.Value, 0, 530, 437);
        gump.AddResizePic(0, 0, 5054, 530, 437);
        gump.AddText(15, 15, 0, $"{craftSkill} Menu (page {page + 1}/{pageCount})");

        int y = 50;
        for (int i = skip; i < recipes.Count && i < skip + CraftRecipesPerPage; i++)
        {
            var recipe = recipes[i];
            string name = string.IsNullOrEmpty(recipe.ResultName)
                ? $"Item 0x{recipe.ResultItemId:X4}"
                : recipe.ResultName;
            bool canMake = _craftingEngine.CanCraft(_character, recipe);
            int hue = canMake ? 0x0044 : 0x0020; // green vs red

            gump.AddButton(15, y, 4005, 4007, CraftButtonRecipeBase + (i - skip));
            gump.AddText(55, y, hue, name);

            if (recipe.Resources.Count > 0)
            {
                var resText = string.Join(", ", recipe.Resources.Select(r =>
                    r.Type.HasValue ? $"{r.Amount}x {r.Type.Value}" : $"{r.Amount}x 0x{r.ItemId:X4}"));
                gump.AddText(280, y, 0, resText);
            }

            y += 22;
        }

        // Prev / Next paging buttons
        if (page > 0)
        {
            gump.AddButton(200, 400, 4014, 4016, CraftButtonPrevPage);
            gump.AddText(240, 400, 0, "Prev");
        }
        if (page < pageCount - 1)
        {
            gump.AddButton(320, 400, 4005, 4007, CraftButtonNextPage);
            gump.AddText(360, 400, 0, "Next");
        }

        gump.AddButton(15, 400, 4017, 4019, 0);
        gump.AddText(55, 400, 0, "Close");

        int capturedSkip = skip;
        int capturedPage = page;
        SendGump(gump, (pressedButton, switches, textEntries) =>
        {
            if (pressedButton == CraftButtonNextPage)
                OpenCraftingGump(craftSkill, capturedPage + 1, fireMenuTrigger: false);
            else if (pressedButton == CraftButtonPrevPage)
                OpenCraftingGump(craftSkill, capturedPage - 1, fireMenuTrigger: false);
            else if (pressedButton >= CraftButtonRecipeBase)
            {
                int index = capturedSkip + ((int)pressedButton - CraftButtonRecipeBase);
                if (index < recipes.Count)
                {
                    // No material picker: Source-X spends any colour of a resource
                    // and never colours the result (CItem::IsResourceMatch,
                    // CItem.cpp:6027; Skill_MakeItem_Success, CCharSkill.cpp:674).
                    BeginPendingCraft(recipes[index], craftSkill, reopenGump: true);
                }
            }
        });
    }

    // --- multi-stroke crafting (reference Skill_Stroke) ----------------------
    private CraftRecipe? _pendingCraftRecipe;
    private SkillType _pendingCraftSkill;
    private int _pendingCraftAmount = 1;
    private int _pendingCraftStrokes;
    private long _pendingCraftNextStroke;
    private bool _pendingCraftReopenGump;
    private Point3D _pendingCraftStartPosition;
    /// <summary>m_Act_p of a smith or a cook: the work site the start found, which
    /// the SUCCESS stage measures the crafter against again.</summary>
    private Point3D? _pendingCraftSite;

    /// <summary>Start a craft as a stroke loop (reference Skill_MakeItem SKTRIG_START →
    /// Skill_Start → Skill_Stroke). ONE work stroke by default - Skill_Start sets
    /// m_atCreate.m_dwStrokeCount = 1 (CCharSkill.cpp:4481) and @SkillStart may change
    /// it through LOCAL.CraftStrokeCnt (:4538). The start makes the skill's sound and
    /// plays its animation (:4543-4555); each stroke, one DELAY later, does it again,
    /// and the count reaching zero is the end of the skill.
    ///
    /// The start is also where the outcome is decided: ACTDIFF is the primary
    /// SKILLMAKE value in whole points (:964), @SkillStart may change it (a negative
    /// value cancels, :4527), and a positive one is rolled right there - a failed roll
    /// negates it, which Skill_Done turns into the FAIL stage (:4566-4570, :3920). A
    /// difficulty of zero is never rolled. ACTIONEFFECT starts at -1 (:4446).</summary>
    /// <param name="replicationQty">MAKEITEM's second argument (CChar.cpp:4696): how
    /// many replications to make. The start keeps only as many as the stock pays for
    /// (Skill_MakeItem SKTRIG_START, CCharSkill.cpp:920/957).</param>
    internal bool BeginPendingCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump,
        int replicationQty = 1)
    {
        if (_character == null || _craftingEngine == null)
            return false;
        if (!SkillHandlers.CanUse(_character, craftSkill))
            return false;
        if (_character.IsCasting || _character.HasActiveSkillPending())
        {
            SysMessage("You must wait to perform another action.");
            return false;
        }
        if (_pendingCraftRecipe != null)
        {
            SysMessage(ServerMessages.Get("craft_busy"));
            return false;
        }
        if (!_craftingEngine.CanCraft(_character, recipe))
        {
            SysMessage(ServerMessages.Get("craft_fail"));
            return false;
        }
        // ResourceConsume in test mode: the replications the stock really covers.
        int craftAmount = _craftingEngine.TestReplication(_character, recipe,
            Math.Clamp(replicationQty, 1, ushort.MaxValue));
        if (craftAmount <= 0)
        {
            SysMessage(ServerMessages.Get("craft_fail"));
            return false;
        }

        // No @SkillMakeItem here: it belongs to the finished item, and fires from
        // CompleteCraft once that item exists.
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillSelect,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True ||
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True)
            return false;

        // Skill_Stage(SKTRIG_START) keeps the difficulty Skill_Start was handed
        // (CCharSkill.cpp:3094, :4425-4429), and m_Act_Effect starts over.
        _character.ActDiff = Math.Max(0, recipe.Difficulty);
        _character.ActionEffect = -1;

        if (FireCraftStart(craftSkill, ref craftAmount, out int craftStrokes, out long craftWaitTenths) ||
            _character.ActDiff < 0)
        {
            _character.ActDiff = 0;      // Skill_Cleanup
            return false;
        }

        // The roll belongs to the start (:4566-4570).
        if (_character.ActDiff > 0 &&
            !CraftingEngine.RollCraft(_character, craftSkill, _character.ActDiff))
            _character.ActDiff = -_character.ActDiff;

        _pendingCraftSite = null;
        if (CraftingEngine.WorkSiteRange(craftSkill) > 0 &&
            _craftingEngine.TryFindWorkSite(_character, craftSkill, out var site))
            _pendingCraftSite = site;

        _pendingCraftRecipe = recipe;
        _pendingCraftSkill = craftSkill;
        _pendingCraftAmount = craftAmount;
        _pendingCraftStrokes = Math.Max(1, craftStrokes);
        _pendingCraftReopenGump = reopenGump;
        _pendingCraftStartPosition = _character.Position;
        FaceCraftWorkSite(craftSkill);
        PlayCraftEffects(craftSkill);
        _pendingCraftNextStroke = Environment.TickCount64 +
            (craftWaitTenths > 0 ? craftWaitTenths * 100L : GetCraftStrokeIntervalMs(craftSkill));
        return true;
    }

    /// <summary>@SkillStart for a craft (Skill_Start, CCharSkill.cpp:4466-4541):
    /// ARGN1 = the skill, ARGN2 = the wait in tenths, LOCAL.CraftStrokeCnt = 1. True
    /// when a script cancelled; otherwise the stroke count and the wait are what the
    /// script left behind. LOCAL.CraftAmount carries the replication count in and
    /// out the same way (:4486/:4541).</summary>
    private bool FireCraftStart(SkillType craftSkill, ref int amount, out int strokes, out long waitTenths)
    {
        strokes = 1;
        int delayMs = Skills.SkillEngine.GetSkillDelayMs(craftSkill, _character?.GetSkill(craftSkill) ?? 0);
        waitTenths = delayMs / 100;
        if (_triggerDispatcher == null || _character == null)
            return false;
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt("CraftStrokeCnt", strokes);
        locals.SetInt("CraftAmount", amount);
        var args = new TriggerArgs
        {
            CharSrc = _character, N1 = (int)craftSkill, N2 = waitTenths, Locals = locals,
        };
        if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart, args) == TriggerResult.True)
            return true;
        strokes = (int)Math.Clamp(locals.GetInt("CraftStrokeCnt", 1), 1, 100);
        // m_dwAmount is a word; Skill_MakeItem(SKTRIG_SUCCESS) reads a zero as one
        // (CCharSkill.cpp:3100).
        amount = (int)(locals.GetInt("CraftAmount", amount) & 0xFFFF);
        waitTenths = args.N2;
        return false;
    }

    /// <summary>Advance the pending craft stroke loop. Called from the
    /// per-client tick pump.</summary>
    internal void TickPendingCraft()
    {
        if (_pendingCraftRecipe == null || _character == null || _craftingEngine == null)
            return;
        if (_character.IsDead || _character.IsDeleted ||
            (SkillEngine.HasFlag(_pendingCraftSkill, SkillFlag.Immobile) &&
             _character.Position != _pendingCraftStartPosition))
        {
            CancelPendingCraft();
            return;
        }
        if (Environment.TickCount64 < _pendingCraftNextStroke)
            return;

        // Source-X Skill_Stroke: @SkillStroke fires per stroke with
        // LOCAL.Strokes seeded to the remaining count and WRITABLE — a
        // script can lengthen or shorten the craft per recipe (the default
        // is Skill_Start's one stroke); RETURN 1 aborts the craft.
        if (_triggerDispatcher != null)
        {
            var strokeLocals = new SphereNet.Scripting.Variables.VarMap();
            strokeLocals.SetInt("Strokes", _pendingCraftStrokes);
            var strokeArgs = new TriggerArgs
            {
                CharSrc = _character,
                N1 = (int)_pendingCraftSkill,
                Locals = strokeLocals,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStroke,
                    strokeArgs) == TriggerResult.True)
            {
                CancelPendingCraft();
                return;
            }
            long rewrittenStrokes = strokeLocals.GetInt("Strokes", _pendingCraftStrokes);
            if (rewrittenStrokes != _pendingCraftStrokes && rewrittenStrokes >= 0)
                _pendingCraftStrokes = (int)Math.Min(rewrittenStrokes, 100);
        }

        // Skill_Stroke plays with a count of one or more, THEN counts down, and the
        // count reaching zero is the success (CCharSkill.cpp:3578-3643) - so the last
        // stroke is heard and seen too, in the same tick as the result.
        if (_pendingCraftStrokes >= 1)
        {
            FaceCraftWorkSite(_pendingCraftSkill);
            PlayCraftEffects(_pendingCraftSkill);
        }
        _pendingCraftStrokes--;
        if (_pendingCraftStrokes > 0)
        {
            _pendingCraftNextStroke = Environment.TickCount64 + GetCraftStrokeIntervalMs(_pendingCraftSkill);
            return;
        }

        var recipe = _pendingCraftRecipe;
        var craftSkill = _pendingCraftSkill;
        bool reopen = _pendingCraftReopenGump;
        int craftAmount = _pendingCraftAmount;
        var site = _pendingCraftSite;
        _pendingCraftRecipe = null;
        _pendingCraftAmount = 1;
        _pendingCraftSite = null;

        CompleteCraft(recipe, craftSkill, reopen, craftAmount, site);
    }

    private void CancelPendingCraft(bool notify = true)
    {
        if (_pendingCraftRecipe == null)
            return;
        int skillId = (int)_pendingCraftSkill;
        _pendingCraftRecipe = null;
        _pendingCraftStrokes = 0;
        _pendingCraftNextStroke = 0;
        _pendingCraftAmount = 1;
        _pendingCraftSite = null;
        _triggerDispatcher?.FireCharTrigger(_character!, CharTrigger.SkillAbort,
            new TriggerArgs { CharSrc = _character, N1 = skillId });
        if (_character != null)
            _character.ActDiff = 0;      // Skill_Cleanup
        if (notify)
            SysMessage("You stop what you were doing.");
    }

    internal void CancelPendingCraftOnInterrupt() => CancelPendingCraft();
    internal void CancelPendingCraftOnDisconnect() => CancelPendingCraft(notify: false);

    private void FaceCraftWorkSite(SkillType craftSkill)
    {
        if (_character == null)
            return;
        // Face the work site, as upstream does on every stroke: UpdateDir(m_Act_p)
        // "toward the forge" (Skill_Blacksmith, CCharSkill.cpp:3155) and "toward the
        // fire source" (Skill_Cooking, :2252). Without it the hammer swings at
        // whatever the crafter happened to be looking at.
        if (_craftingEngine != null &&
            _craftingEngine.TryFindWorkSite(_character, craftSkill, out var workSite))
        {
            SphereNet.Game.Skills.Information.ActiveSkillEngine.FaceSkillTarget(_character, workSite);
        }
    }

    /// <summary>The craft skill's own sound and animation, unless its FLAGS switch
    /// them off - played by the start and by every stroke.</summary>
    private void PlayCraftEffects(SkillType craftSkill)
    {
        if (_character == null)
            return;
        var (craftAnim, craftSound) = GetCraftAnimAndSound(craftSkill);
        if (craftAnim != 0 && !SkillEngine.HasFlag(craftSkill, SkillFlag.NoAnim))
            PlayAnimation(_character, craftAnim);
        if (craftSound != 0 && !SkillEngine.HasFlag(craftSkill, SkillFlag.NoSfx))
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketSound(craftSound, _character.X, _character.Y, _character.Z), 0);
    }

    /// <summary>The stroke re-arm interval: Skill_GetTimeout, the DELAY curve at the
    /// crafter's base skill with a floor of ONE tenth (CCharSkill.cpp:659-671). The
    /// old floor of a full second read Skill_Stroke's "delay &lt; 10" clamp as tenths;
    /// it is milliseconds (:3645).</summary>
    private int GetCraftStrokeIntervalMs(SkillType craftSkill)
    {
        int delayMs = Skills.SkillEngine.GetSkillDelayMs(craftSkill, _character?.GetSkill(craftSkill) ?? 0);
        return Math.Max(100, delayMs);
    }

    /// <summary>The end of a craft, in Source-X Skill_Done order (CCharSkill.cpp:3899-3974).
    /// A difficulty the start's roll negated is the FAIL stage (:3920). Otherwise
    /// @SkillSuccess runs FIRST and its RETURN 1 aborts before anything is spent
    /// (:3935); then the skill's SUCCESS stage - a smith or a cook who walked out of
    /// range of the work site is a failure there (:3159-3163, :2255-2259); then
    /// Skill_MakeItem pays for the replications and makes the item (:920/968), and
    /// @SkillMakeItem runs on that finished item (:811); the skill gain comes last.
    /// </summary>
    private void CompleteCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump,
        int craftAmount = 1, Point3D? workSite = null)
    {
        if (_character == null || _craftingEngine == null)
            return;

        if (_character.ActDiff < 0)
        {
            FailCraftStage(recipe, craftSkill);
        }
        else
        {
            var successLocals = new SphereNet.Scripting.Variables.VarMap();
            successLocals.SetInt("ITEMDAMAGECHANCE", 25);
            successLocals.SetInt("ITEMDAMAGEAMOUNT", 1);
            int range = CraftingEngine.WorkSiteRange(craftSkill);
            if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillSuccess,
                    new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill, Locals = successLocals })
                == TriggerResult.True)
            {
                AbortCraftStage(craftSkill);
            }
            else if (range > 0 && workSite is { } at &&
                     (_character.MapIndex != at.Map || _character.Position.GetDistanceTo(at) > range))
            {
                FailCraftStage(recipe, craftSkill);
            }
            else if (!DeliverCraftSuccess(recipe, craftSkill, craftAmount > 0 ? craftAmount : 1))
            {
                AbortCraftStage(craftSkill);
            }
            else
            {
                // Skill_Experience after the stage went through.
                SkillEngine.GainExperience(_character, craftSkill, _character.ActDiff);
            }
        }
        _character.ActDiff = 0;          // Skill_Cleanup

        if (reopenGump)
            OpenCraftingGump(craftSkill, 0, fireMenuTrigger: false);
    }

    /// <summary>Skill_Fail(false) (CCharSkill.cpp:3783): the difficulty is made
    /// negative, @SkillFail runs and its RETURN 1 turns the failure into a cancel
    /// (Skill_Stage(SKTRIG_ABORT): no partial cost, no gain); otherwise the FAIL
    /// stage pays part of one replication and the failure earns its experience.</summary>
    private void FailCraftStage(CraftRecipe recipe, SkillType craftSkill)
    {
        if (_character == null || _craftingEngine == null)
            return;
        if (_character.ActDiff > 0)
            _character.ActDiff = -_character.ActDiff;
        bool cancelled = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillFail,
            new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True;
        if (cancelled)
            return;
        _craftingEngine.CraftFail(_character, recipe);
        SysMessage(ServerMessages.Get("craft_fail"));
        SkillEngine.GainExperience(_character, craftSkill, _character.ActDiff);
    }

    /// <summary>Skill_Fail(true), reached when a stage returned -SKTRIG_ABORT
    /// (OnTickSkill, CCharAct.cpp:5800): @SkillAbort, no message, no credit.</summary>
    private void AbortCraftStage(SkillType craftSkill)
    {
        if (_character == null)
            return;
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
            new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });
    }

    /// <summary>Skill_MakeItem's SUCCESS stage and Skill_MakeItem_Success
    /// (CCharSkill.cpp:674-865, 913-971). False when the stage aborts: the SKILLMAKE
    /// test no longer passes, nothing could be paid for, or @SkillMakeItem returned 1 -
    /// which deletes the new item, while what it cost stays spent.</summary>
    private bool DeliverCraftSuccess(CraftRecipe recipe, SkillType craftSkill, int craftAmount)
    {
        if (_character == null || _craftingEngine == null)
            return false;

        // SkillResourceTest runs at every stage (:913): a tool or a skill that is gone
        // ends the craft here.
        if (!_craftingEngine.CanCraft(_character, recipe, skillOnly: true))
            return false;

        var outcome = _craftingEngine.CraftSuccess(_character, recipe, craftAmount);
        if (outcome == null)
            return false;

        // The further copies of a non-stackable replication bounce as they are made,
        // before the trigger, speaking only under VERBOSEITEMBOUNCE (:703-705).
        foreach (var extra in outcome.Extras)
            BounceCraftedItem(extra, GameClient.VerboseItemBounce);

        var item = outcome.Item;

        // "Item goes into ACT of player" for the trigger, then ACT is restored
        // (:811-827). ARGN1 = the base skill, ARGN2 = the quality, ARGO = the
        // previous ACT, LOCAL.Notify = 1 (the bounce message).
        var oldAct = _character.Act;
        var oldActObject = oldAct.IsValid ? _world.FindObject(oldAct) : null;
        var result = TriggerResult.Default;
        bool notify = true;
        _character.Act = item.Uid;
        try
        {
            if (_triggerDispatcher != null)
            {
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("Notify", 1);
                var args = new TriggerArgs
                {
                    CharSrc = _character,
                    N1 = _character.GetSkill(craftSkill),
                    N2 = outcome.Quality,
                    N3 = 0,
                    O1 = oldActObject,
                    Locals = locals,
                };
                result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillMakeItem, args);
                notify = locals.GetInt("Notify", 1) != 0;
            }
        }
        finally
        {
            _character.Act = oldAct;
        }

        if (result == TriggerResult.True)
        {
            if (!item.IsDeleted)
                _world.RemoveItem(item);
            return false;
        }
        if (item.IsDeleted)
            return true;

        if (result == TriggerResult.Default)
        {
            if (!SkillEngine.HasFlag(craftSkill, SkillFlag.NoSfx))
            {
                ushort sound = item.ItemType switch
                {
                    ItemType.Potion => 0x240,
                    ItemType.Map => 0x255,
                    _ => 0,
                };
                if (sound != 0)
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                        new PacketSound(sound, _character.X, _character.Y, _character.Z), 0);
            }
            // The band's message, only from the single-item quality branch (:744-788).
            if (outcome.QualityRolled && CraftingEngine.QualityMessageKey(outcome.Quality) is { } qualityMsg)
                SysMessage(ServerMessages.Get(qualityMsg));
        }

        // EXP_MODE_RAISE_CRAFT (CCharSkill.cpp:849): one point per 100 gold of the
        // piece's vendor value.
        if (Character.ExperienceSystem && (Character.ExperienceMode & Character.ExpModeRaiseCraft) != 0)
        {
            int craftExp = Skills.Information.InfoSkillEngine.EstimateVendorPrice(item) / 100;
            if (craftExp != 0)
                _character.ChangeExperience(craftExp);
        }

        BounceCraftedItem(item, notify);
        return true;
    }

    /// <summary>CChar::ItemBounce (CCharAct.cpp:3076) for a crafted piece.
    ///
    /// The pack takes it when the crafter can carry it - CanCarry, which a character
    /// in GM mode always passes (CCharStatus.cpp:267) - and only after the item's
    /// @DropOn_Item (ARGO = the pack) and the pack's @DropOn_Self (ARGO = the item)
    /// let it: a RETURN 1 from the pack sends it to the ground, unless the script
    /// already moved it somewhere else. The ground gets it otherwise, after its
    /// @DropOn_Ground (ARGN1 = decay in tenths, read back; ARGN2 = 1; ARGS = the point).
    /// The point the script writes is checked and then not used: the item lands at
    /// the crafter's feet (:3176-3202). The drop sound follows, then the line - the
    /// feet line always, the others only when asked for (:3199-3212).</summary>
    private void BounceCraftedItem(Item item, bool displayMessage)
    {
        if (_character == null || item.IsDeleted)
            return;
        string name = item.GetName();
        var pack = _character.Backpack;
        string? where = null;
        bool toPack = false;
        bool toGround = false;

        if (pack != null && (_character.IsGmMode || _character.CanCarry(item)))
        {
            toPack = true;
            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = pack });
                if (item.IsDeleted)
                    return;

                var prevCont = item.ContainedIn;
                var selfResult = _triggerDispatcher.FireItemTrigger(pack, ItemTrigger.DropOnSelf,
                    new TriggerArgs { CharSrc = _character, ItemSrc = pack, O1 = item });
                if (item.IsDeleted)
                    return;
                if (selfResult == TriggerResult.True)
                {
                    toPack = false;
                    if (item.ContainedIn == prevCont)
                        toGround = true;
                    else
                        where = ServerMessages.GetFormatted("msg_bounce_cont",
                            _world.FindObject(item.ContainedIn)?.GetName() ?? "");
                }
            }
        }
        else
        {
            toGround = true;
        }

        if (toPack)
        {
            var actual = pack!.TryAddItemWithStack(item);
            if (actual == null)
            {
                toGround = true;     // a full pack: the ground takes it
            }
            else
            {
                where = ServerMessages.Get("msg_bounce_pack");
                if (actual != item)
                    _world.RemoveItem(item);
                if (actual.ContainedIn == pack.Uid)
                    SendContainerItemPacket(new PacketContainerItem(
                        actual.Uid.Value, actual.DispIdFull, 0,
                        actual.Amount, actual.X, actual.Y,
                        pack.Uid.Value, actual.Hue,
                        _netState.IsClientPost6017));
            }
        }

        if (toGround)
        {
            var prevCont = item.ContainedIn;
            long decayMs = GameWorld.DefaultDecayTimeMs;
            if (_triggerDispatcher != null)
            {
                var pos = _character.Position;
                var groundArgs = new TriggerArgs
                {
                    CharSrc = _character, ItemSrc = item,
                    N1 = decayMs / 100, N2 = 1,
                    S1 = $"{pos.X},{pos.Y},{pos.Z},{pos.Map}",
                };
                _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnGround, groundArgs);
                if (item.IsDeleted)
                    return;
                decayMs = groundArgs.N1 * 100L;
            }
            if (item.ContainedIn == prevCont)
            {
                where = ServerMessages.Get("msg_feet");
                displayMessage = true;
                _world.PlaceItemWithDecay(item, _character.Position, decayMs);
            }
        }

        if (!_character.IsDead)
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketSound(item.GetDropSound(ontoSomething: pack != null),
                    _character.X, _character.Y, _character.Z), 0);
        if (displayMessage && where != null)
            SysMessage(ServerMessages.GetFormatted("msg_itemplace", name, where));
    }

    /// <summary>Handle vendor buy packet (0x3B).</summary>
    public void HandleVendorBuy(uint vendorSerial, byte flag,
        List<SphereNet.Network.Packets.Incoming.VendorBuyEntry> buyItems)
    {
        if (_character == null) return;
        // A vendor window outlives the player: they can die with it open, and the
        // client can still send a queued confirmation afterwards. Source-X CanTouch
        // refuses a dead character every item that is not death-immune, so the
        // transaction must not reach the stock at all.
        if (_character.IsDead) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || !VendorEngine.IsVendorLike(vendor)) return;
        if (_character.MapIndex != vendor.MapIndex ||
            _character.Position.GetDistanceTo(vendor.Position) > 3)
            return;

        if (flag == 0 || buyItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // ALLOWBUYSELLAGENT off: a purchase confirmed sooner than 3 ms per line
        // after the list went out is an agent, and is refused (receive.cpp:763-772).
        if (IsBuySellTooFast(buyItems.Count, 3))
        {
            SysMessage(ServerMessages.Get("npc_vendor_buyfast"));
            return;
        }

        // Fire @Buy trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "BUY" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var bi in buyItems)
        {
            var item = _world.FindItem(new Serial(bi.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = bi.Amount
            });
        }

        // @Buy fires per item BEFORE the purchase; RETURN 1 drops that line.
        entries = FilterVendorEntriesByTrigger(vendor, entries, ItemTrigger.Buy);
        if (entries.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            RefreshBackpackContents();
            return;
        }

        var buyerChar = _character;
        long result = VendorEngine.ProcessBuy(_character, vendor, entries,
            out var refusal, (buyer, figurine) => CreateBoughtFigurinePet(buyer, figurine, vendor),
            onHairCut: () =>
            {
                // pVendor->UpdateAnimate(ANIM_ATTACK_1H_SLASH); m_pChar->Sound(SOUND_SNIP)
                // (CClientEvent.cpp:1319-1320).
                PlayAnimation(vendor, (ushort)AnimationType.AttackWeapon);
                BroadcastNearby?.Invoke(buyerChar.Position, UpdateRange,
                    new PacketSound(0x0248, buyerChar.X, buyerChar.Y, buyerChar.Z), 0);
            });
        if (refusal != VendorEngine.VendorBuyRefusal.None)
        {
            // A refused purchase ends there, the window left open (CClientEvent.cpp:1158-1262).
            switch (refusal)
            {
                case VendorEngine.VendorBuyRefusal.PetSlots:
                    SysMessage(ServerMessages.Get(Msg.PetslotsTryControl));
                    return;
                case VendorEngine.VendorBuyRefusal.CantBuy:
                    NpcSpeech(vendor, ServerMessages.Get("npc_vendor_cantbuy"));
                    return;
                case VendorEngine.VendorBuyRefusal.CostTooHigh:
                case VendorEngine.VendorBuyRefusal.CantFulfill:
                    NpcSpeech(vendor, ServerMessages.Get("npc_vendor_cantfulfill"));
                    SysMessage(ServerMessages.Get("npc_vendor_cantbuy"));
                    return;
                case VendorEngine.VendorBuyRefusal.NoMoney:
                    NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nomoney1"));
                    return;
                default:
                    SysMessage(ServerMessages.Get("npc_vendor_cantbuy"));
                    return;
            }
        }
        // The owner, or a GM in GM mode, takes the goods: "That is N gold coins worth
        // of goods" instead of "That will be N" (DEFMSG_NPC_VENDOR_S1 / _B1, :1386).
        string costKey = VendorEngine.IsVendorBoss(vendor, _character) ? "npc_vendor_s1" : "npc_vendor_b1";
        NpcSpeech(vendor, ServerMessages.GetFormatted(costKey, result, result == 1 ? "" : "s"));

        // The window showed stock that has now been bought. Upstream closes it when
        // a purchase completes (CClientEvent.cpp:1413) - left open, the client offers
        // a list the vendor no longer has.
        CloseVendorWindow(vendor);

        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>The BUYSELLTIME check: with ALLOWBUYSELLAGENT off, a confirmation that
    /// arrives before <paramref name="msPerLine"/> per line has passed since the list
    /// was sent is refused.</summary>
    internal bool IsBuySellTooFast(int lines, int msPerLine)
    {
        if (GameClient.AllowBuySellAgent || _client.VendorListSentMs == 0)
            return false;
        return Environment.TickCount64 < _client.VendorListSentMs + (long)lines * msPerLine;
    }

    /// <summary>Clear the shop display for this vendor (Source-X addVendorClose,
    /// CClientMsg.cpp:2386).</summary>
    private void CloseVendorWindow(Character vendor) =>
        _netState.Send(new SphereNet.Network.Packets.Outgoing.PacketCloseVendor(vendor.Uid.Value).Build());

    /// <summary>Handle vendor sell packet (0x9F).</summary>
    public void HandleVendorSell(uint vendorSerial,
        List<SphereNet.Network.Packets.Incoming.VendorSellEntry> sellItems)
    {
        if (_character == null) return;
        // A vendor window outlives the player: they can die with it open, and the
        // client can still send a queued confirmation afterwards. Source-X CanTouch
        // refuses a dead character every item that is not death-immune, so the
        // transaction must not reach the stock at all.
        if (_character.IsDead) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || !VendorEngine.IsVendorLike(vendor)) return;
        if (_character.MapIndex != vendor.MapIndex ||
            _character.Position.GetDistanceTo(vendor.Position) > 3)
            return;

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // ALLOWBUYSELLAGENT off: 300 ms per line for a sale (receive.cpp:1906-1915).
        if (IsBuySellTooFast(sellItems.Count, 300))
        {
            SysMessage(ServerMessages.Get("npc_vendor_sellfast"));
            return;
        }

        // Fire @Sell trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "SELL" });

        // Build trade entries from packet data. The price is the quote of the BUY
        // sample the item matches - the figure the list showed, @Sell is shown and
        // the vendor pays (CClientEvent.cpp:1488). An item the vendor does not buy
        // is skipped before its @Sell runs, as upstream skips it (:1474), and so is
        // one the seller does not hold (:1468). A line naming nothing, or something
        // that cannot change hands, ends the sale at that line (:1463): it is passed
        // on so the sale stops there, and nothing after it is read.
        var entries = new List<TradeEntry>();
        foreach (var si in sellItems)
        {
            var item = _world.FindItem(new Serial(si.ItemSerial));
            if (item == null || !VendorEngine.IsValidSaleItem(item, buyFromVendor: true))
            {
                entries.Add(new TradeEntry { ItemUid = new Serial(si.ItemSerial), Amount = si.Amount });
                break;
            }
            if (!ReferenceEquals(item.GetTopLevelObj(), _character))
                continue;

            var (quote, sample) = VendorEngine.GetSellQuote(vendor, item);
            if (sample == null) continue;
            int price = (int)Math.Min(quote, int.MaxValue);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = si.Amount
            });
        }

        // @Sell fires per item BEFORE the sale; RETURN 1 drops that line.
        entries = FilterVendorEntriesByTrigger(vendor, entries, ItemTrigger.Sell);
        if (entries.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            RefreshBackpackContents();
            return;
        }

        int result = VendorEngine.ProcessSell(_character, vendor, entries, out bool shortfall);
        // Event_VendorSell's closing lines (CClientEvent.cpp:1547-1572): thanks for
        // what was paid and, when the purse ran dry part way, that it has; nothing
        // paid and a dry purse is "I cannot afford any more".
        if (result > 0)
        {
            NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_sell_ty", result, result == 1 ? "" : "s"));
            if (shortfall)
                NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nomoney"));
        }
        else if (shortfall)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_cantafford"));

        // Same on the way out: upstream closes the window when a sale completes -
        // when gold changed hands (CClientEvent.cpp:1550-1566).
        if (result > 0)
            CloseVendorWindow(vendor);

        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Get the buy price for an item from vendor inventory. Uses TAG.PRICE or defaults.</summary>
    internal static int GetVendorItemPrice(Character vendor, Item item) =>
        (int)Math.Min(SphereNet.Game.Trade.VendorEngine.GetVendorSellToPlayerPrice(vendor, item), int.MaxValue);

    /// <summary>Get the sell price (what vendor pays the player): the quote of the
    /// BUY sample the item matches (<see cref="VendorEngine.GetSellQuote"/>), the
    /// same figure the sell list shows and the sale pays; 0 when the vendor does not
    /// buy it.</summary>
    internal static int GetVendorItemSellPrice(Character vendor, Item item) =>
        (int)Math.Min(VendorEngine.GetSellQuote(vendor, item).UnitPrice, int.MaxValue);

    /// <summary>A figurine bought from an NPC vendor (Use_Figurine, CCharUse.cpp:1115,
    /// called per unit from Event_VendorBuy, CClientEvent.cpp:1306): the creature it
    /// names is made, takes the figurine's name and hue, becomes the buyer's pet -
    /// refused, and unmade, when it would exceed the follower slots - and appears
    /// where the figurine's top-level holder, the vendor, stands. The vendor's stock
    /// figurine itself is not used up by this.</summary>
    private Character? CreateBoughtFigurinePet(Character buyer, Item figurine, Character vendor)
    {
        int creatureId = VendorEngine.ResolveFigurineCreature(figurine);
        if (creatureId == 0)
            return null;
        var pet = _client.CreateNpcFromDefinition(creatureId, $"0{creatureId:X}");
        if (pet == null)
            return null;
        pet.Name = figurine.GetName();
        if (figurine.Hue.Value != 0)
        {
            pet.OSkin = figurine.Hue.Value;
            pet.Hue = figurine.Hue;
        }
        if (!pet.TryAssignOwnership(buyer, buyer, summoned: false, enforceFollowerCap: true))
        {
            _world.DeleteObject(pet);
            if (buyer == _character)
                SysMessage(ServerMessages.Get(Msg.PetslotsTryControl));
            return null;
        }
        if (!_world.PlaceCharacter(pet, vendor.Position))
        {
            _world.DeleteObject(pet);
            return null;
        }
        pet.ClearStatFlag(StatFlag.Ridden);
        return pet;
    }

    /// <summary>Fire the per-item @Buy / @Sell trigger BEFORE the transfer and
    /// return the entries that were NOT cancelled. Source-X runs @Buy/@Sell ahead
    /// of moving the item so RETURN 1 can veto that line (it used to fire after
    /// the trade had already completed, with its return value ignored).
    ///
    /// Argument contract (Source-X CClientEvent.cpp:1288 / :1504):
    /// <c>ARGN1</c> = amount, <c>ARGN2</c> = the LINE TOTAL (amount x price), not
    /// the unit price, and <c>ARGO</c> = the vendor. @Buy additionally carries
    /// <c>LOCAL.TOTALCOST</c>, the running total of the lines still standing, which
    /// a vetoed line is subtracted from.
    ///
    /// Deliberate deviation: Source-X builds ARGN2 from the price in the CLIENT
    /// packet. SphereNet uses the server-resolved price the transaction will
    /// actually charge, so a script cannot be shown a figure the engine disagrees
    /// with. Do not "restore parity" here by reading the client value.</summary>
    private List<TradeEntry> FilterVendorEntriesByTrigger(Character vendor, IReadOnlyList<TradeEntry> entries, ItemTrigger trigger)
    {
        if (_triggerDispatcher == null || _character == null)
            return entries.ToList();

        long runningTotal = 0;
        foreach (var entry in entries)
            runningTotal += (long)entry.Price * entry.Amount;

        var kept = new List<TradeEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var item = _world.FindItem(entry.ItemUid);
            if (item == null) { kept.Add(entry); continue; }

            long lineTotal = (long)entry.Price * entry.Amount;

            var locals = new SphereNet.Scripting.Variables.VarMap();
            if (trigger == ItemTrigger.Buy)
                locals.SetInt("TOTALCOST", (int)Math.Clamp(runningTotal, int.MinValue, int.MaxValue));

            var result = _triggerDispatcher.FireItemTrigger(item, trigger, new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = item,
                O1 = vendor,
                N1 = entry.Amount,
                N2 = (int)Math.Clamp(lineTotal, int.MinValue, int.MaxValue),
                Locals = locals
            });

            if (result == TriggerResult.True)
            {
                // Source-X subtracts a vetoed line from the running total so a later
                // line's LOCAL.TOTALCOST reflects what is still being bought.
                runningTotal -= lineTotal;
                continue;
            }
            kept.Add(entry);
        }
        return kept;
    }

    /// <summary>
    /// Handle secure trade packet (0x6F).
    /// Actions: 0=display, 1=close, 2=update (check/uncheck accept).
    /// </summary>
    public void HandleSecureTrade(byte action, uint containerSerial, uint param)
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindByContainer(containerSerial);
        if (trade == null) return;
        if (!trade.IsParticipant(_character)) return;

        // Source-X receive.cpp:1132 refuses the packet when the named container is
        // not the sender's own (`character != container->GetParent()`). Without it,
        // reading the accept value from the packet would let a client name the
        // PARTNER's container and set the partner's flag - turning a toggle bug into
        // one-sided completion. Now that each window is parented to its owner
        // (CreateTradeContainer) this is a direct comparison.
        if (trade.GetOwnContainer(_character).Uid.Value != containerSerial)
            return;

        switch (action)
        {
            case 1: // Cancel
                CancelTrade(trade);
                break;
            case 2: // Accept / un-accept, per the packet's own flag
            {
                bool bothAccepted = trade.SetAccept(_character, param != 0);
                SendTradeUpdateToBoth(trade);

                if (bothAccepted)
                {
                    if (!TryCompleteTrade(trade))
                    {
                        trade.ResetAcceptance();
                        SendTradeUpdateToBoth(trade);
                    }
                    else
                        return;
                }
                break;
            }
        }
    }

    /// <summary>0x6F action 3 - the gold and platinum this side puts in the window
    /// (Source-X Trade_UpdateGold, CItemContainer.cpp:252). Only with
    /// FEATURE_TOL_VIRTUALGOLD; without it the offer is always nothing, as upstream's
    /// cap to the (then empty) virtual purse makes it. The offer is capped to what the
    /// trader actually holds and shown to the partner. A changed offer also withdraws
    /// both acceptances, so nobody can lower the gold after the other side agreed -
    /// upstream leaves the check marks alone here, which lets exactly that happen.</summary>
    public void HandleSecureTradeGold(uint containerSerial, uint gold, uint platinum)
    {
        if (_character == null || _tradeManager == null || !Trade.VirtualGold.Enabled) return;
        var trade = _tradeManager.FindByContainer(containerSerial);
        if (trade == null || !trade.IsParticipant(_character)) return;
        if (trade.GetOwnContainer(_character).Uid.Value != containerSerial) return;

        long offer = Math.Min(Trade.VirtualGold.Join(gold, platinum), Trade.VirtualGold.Get(_character));
        if (offer == trade.GetGoldOffer(_character)) return;
        trade.SetGoldOffer(_character, offer);

        if (trade.InitiatorAccepted || trade.PartnerAccepted)
        {
            trade.ResetAcceptance();
            SendTradeUpdateToBoth(trade);
        }

        var partner = trade.GetPartner(_character);
        var (g, p) = Trade.VirtualGold.Split(offer);
        Character.SendPacketToOwner?.Invoke(partner, new PacketSecureTradeGold(
            PacketSecureTradeGold.OfferType, trade.GetOwnContainer(partner).Uid.Value, g, p));
    }

    /// <summary>Move the offered virtual gold between the traders on completion
    /// (Trade_Status, CItemContainer.cpp:206-244), each offer re-capped to what its
    /// trader still holds, with upstream's sent/received messages.</summary>
    private void TransferTradeGold(SecureTrade trade)
    {
        var a = trade.Initiator;
        var b = trade.Partner;
        long fromA = Math.Min(trade.GetGoldOffer(a), Trade.VirtualGold.Get(a));
        long fromB = Math.Min(trade.GetGoldOffer(b), Trade.VirtualGold.Get(b));
        if (fromA == 0 && fromB == 0) return;

        AnnounceTradeGold(a, b, fromA);
        AnnounceTradeGold(b, a, fromB);
        Trade.VirtualGold.Set(a, Trade.VirtualGold.Get(a) + fromB - fromA);
        Trade.VirtualGold.Set(b, Trade.VirtualGold.Get(b) + fromA - fromB);
    }

    private void AnnounceTradeGold(Character sender, Character receiver, long amount)
    {
        if (amount <= 0) return;
        var (gold, plat) = Trade.VirtualGold.Split(amount);
        string sent, received;
        if (plat != 0 && gold != 0)
        {
            sent = ServerMessages.GetFormatted(Msg.MsgTradeSentPlatGold, plat, gold, receiver.Name);
            received = ServerMessages.GetFormatted(Msg.MsgTradeReceivedPlatGold, plat, gold, sender.Name);
        }
        else if (plat != 0)
        {
            sent = ServerMessages.GetFormatted(Msg.MsgTradeSentPlat, plat, receiver.Name);
            received = ServerMessages.GetFormatted(Msg.MsgTradeReceivedPlat, plat, sender.Name);
        }
        else
        {
            sent = ServerMessages.GetFormatted(Msg.MsgTradeSentGold, gold, receiver.Name);
            received = ServerMessages.GetFormatted(Msg.MsgTradeReceivedGold, gold, sender.Name);
        }
        TellTrader(sender, sent);
        TellTrader(receiver, received);
    }

    private void TellTrader(Character ch, string text)
    {
        if (ch == _character) SysMessage(text);
        else SendTradeMessageToPartner?.Invoke(ch, text);
    }

    /// <summary>Cancel active trade on disconnect — return items, notify partner.</summary>
    internal void AbortActiveTradeOnDisconnect()
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindTradeFor(_character);
        if (trade == null) return;

        var partner = trade.GetPartner(_character);
        FinalizeTradeCancel(trade, partner, sendSelfClose: false);
    }

    private bool TryCompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;

        if (!TradeManager.CanAcceptTradeItems(partner, _world, trade.InitiatorContainer, out string? reason))
        {
            SysMessage(reason ?? "Trade failed.");
            SendTradeMessageToPartner?.Invoke(partner, reason ?? "Trade failed.");
            SendTradeUpdateToBoth(trade);
            return false;
        }

        if (!TradeManager.CanAcceptTradeItems(initiator, _world, trade.PartnerContainer, out reason))
        {
            SysMessage(reason ?? "Trade failed.");
            SendTradeMessageToPartner?.Invoke(partner, "Your partner cannot carry that much.");
            SendTradeUpdateToBoth(trade);
            return false;
        }

        CompleteTrade(trade);
        return true;
    }

    /// <summary>
    /// Build one side of a secure trade window and attach it to its owner.
    ///
    /// Source-X Cmd_SecureTrade (CClientUse.cpp:1414/1420) does
    /// <c>LayerAdd(pCont, LAYER_SPECIAL)</c> on each character with type
    /// IT_EQ_TRADE_WINDOW, so the window belongs to a person. SphereNet left both
    /// containers as loose world items with no parent and no position, which is why
    /// nothing downstream could tell whose window it was: the pickup reach check
    /// found neither an owner nor an opened-container record, so a player could not
    /// take back what they had just offered.
    ///
    /// Attached by hand rather than through <see cref="Character.Equip"/>: Layer.Special
    /// is shared with memory items, which deliberately stay out of the equipment slot
    /// array (CharacterMemoryState), so writing the slot here would collide with them.
    /// </summary>
    private Item CreateTradeContainer(Character owner)
    {
        var cont = _world.CreateItem();
        cont.BaseId = 0x1E5E;
        cont.ItemType = Core.Enums.ItemType.EqTradeWindow;
        cont.Name = "Trade Container";
        cont.IsEquipped = true;
        cont.EquipLayer = Core.Enums.Layer.Special;
        cont.ContainedIn = owner.Uid;
        return cont;
    }

    /// <summary>
    /// Open a secure trade with <paramref name="partner"/>, optionally seeded with
    /// the item that was dropped on them. Returns false when the trade did not open.
    ///
    /// The result matters: Source-X Event_Item_Drop bounces the dropped item when
    /// Cmd_SecureTrade fails (CClientEvent.cpp:325). This used to return void, so
    /// every early refusal - a dead or distant partner, REFUSETRADES, either side
    /// already trading - left the item parented to the character with no drag and no
    /// container, unreachable through any normal inventory view, while the client was
    /// told the drop had succeeded.
    /// </summary>
    public bool InitiateTrade(Character partner, Item? firstItem = null)
    {
        if (_character == null || _tradeManager == null) return false;
        if (partner == _character || partner.IsDeleted || !partner.IsPlayer)
        { SysMessage("That is not a valid trade partner."); return false; }
        if (_character.IsDead || partner.IsDead) { SysMessage("You cannot trade while dead."); return false; }
        // Source-X Cmd_SecureTrade (CClientUse.cpp:1338) refuses a partner with no
        // active client, "and also offline players" — a character standing in the
        // world is not the same thing as a player who can answer.
        if (!partner.IsOnline)
        { SysMessage($"{partner.Name} is not available for trade."); return false; }
        if (_character.MapIndex != partner.MapIndex ||
            _character.Position.GetDistanceTo(partner.Position) > 3)
        { SysMessage("That person is too far away."); return false; }
        if (partner.TryGetTag("REFUSETRADES", out string? refuse) &&
            (!ScriptNumber.TryParseInt(refuse, out int refuseValue) || refuseValue != 0))
        { SysMessage($"{partner.Name} is refusing trade requests."); return false; }

        var existing = _tradeManager.FindTradeFor(_character);
        if (existing != null) { SysMessage("You are already trading."); return false; }

        var partnerTrade = _tradeManager.FindTradeFor(partner);
        if (partnerTrade != null) { SysMessage("They are already trading."); return false; }

        var cont1 = CreateTradeContainer(_character);
        var cont2 = CreateTradeContainer(partner);

        var trade = _tradeManager.StartTrade(_character, partner, cont1, cont2);
        if (FireTradeTrigger(_character, CharTrigger.TradeCreate, trade, partner, firstItem) == TriggerResult.True ||
            FireTradeTrigger(partner, CharTrigger.TradeCreate, trade, _character, firstItem) == TriggerResult.True)
        {
            if (firstItem != null)
                TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
            trade.Cancel();
            _tradeManager.EndTrade(trade);
            _world.RemoveItem(cont1);
            _world.RemoveItem(cont2);
            return false;
        }

        if (firstItem != null && _triggerDispatcher?.FireItemTrigger(firstItem,
            ItemTrigger.DropOnTrade, new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = firstItem,
                O1 = partner,
                N1 = (int)trade.SessionId.Value
            }) == TriggerResult.True)
        {
            TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
            trade.Cancel();
            _tradeManager.EndTrade(trade);
            _world.RemoveItem(cont1);
            _world.RemoveItem(cont2);
            return false;
        }

        _netState.Send(BuildWorldItemPacket(cont1.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(BuildWorldItemPacket(cont2.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketSecureTradeOpen(
            partner.Uid.Value, cont1.Uid.Value, cont2.Uid.Value, partner.GetName()));
        // The window's ledger: how much this trader can offer (Cmd_SecureTrade,
        // CClientUse.cpp:1428 - TOL virtual gold, new-secure-trade clients).
        if (Trade.VirtualGold.Enabled && _netState.SupportsNewSecureTrading)
        {
            var (lg, lp) = Trade.VirtualGold.Split(Trade.VirtualGold.Get(_character));
            _netState.Send(new PacketSecureTradeGold(PacketSecureTradeGold.LedgerType, cont1.Uid.Value, lg, lp));
        }

        SendTradeToPartner?.Invoke(partner, _character, cont1, cont2);

        if (firstItem != null)
        {
            if (!cont1.TryAddItem(firstItem))
            {
                TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
                CancelTrade(trade);
                return false;
            }
            SendContainerItemPacket(new PacketContainerItem(
                firstItem.Uid.Value, firstItem.DispIdFull, 0,
                firstItem.Amount, 30, 30,
                cont1.Uid.Value, firstItem.Hue, _netState.IsClientPost6017));
            SendTradeItemToPartner?.Invoke(partner, firstItem, cont1);
        }

        return true;
    }

    private void CancelTrade(SecureTrade trade)
    {
        var partner = trade.GetPartner(_character!);
        FinalizeTradeCancel(trade, partner, sendSelfClose: true);
    }

    /// <summary>Server-initiated trade cancel — Source-X CChar::Death deletes
    /// any open trade window when a participant dies. Runs the same finalize
    /// path as a client-side close (items return to packs, both windows close,
    /// @TradeClose fires) so the returned items reach the corpse loot drop.</summary>
    public void CancelActiveTradeOnDeath()
    {
        if (_character == null || _tradeManager == null) return;
        var trade = _tradeManager.FindTradeFor(_character);
        if (trade == null) return;
        FinalizeTradeCancel(trade, trade.GetPartner(_character), sendSelfClose: true);
    }

    private void FinalizeTradeCancel(SecureTrade trade, Character partner, bool sendSelfClose)
    {
        if (_character == null || _tradeManager == null) return;

        TradeManager.ReturnTradeItems(_world, trade);

        if (sendSelfClose)
        {
            var myCont = trade.GetOwnContainer(_character);
            _netState.Send(new PacketSecureTradeClose(myCont.Uid.Value));
        }

        SendTradeCloseToPartner?.Invoke(partner, trade.GetPartnerContainer(_character).Uid.Value);

        FireTradeTrigger(_character, CharTrigger.TradeClose, trade, partner, src: _character);
        FireTradeTrigger(partner, CharTrigger.TradeClose, trade, _character, src: _character);

        trade.Cancel();
        _tradeManager.EndTrade(trade);

        _world.RemoveItem(trade.InitiatorContainer);
        _world.RemoveItem(trade.PartnerContainer);
    }

    private void CompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;
        var cont1 = trade.InitiatorContainer;
        var cont2 = trade.PartnerContainer;

        // @TradeAccepted fires BEFORE any item changes hands. RETURN 1 from either
        // side vetoes THIS EXCHANGE, and nothing more: Source-X
        // CItemContainer.cpp:189 simply returns, without Trade_Delete, so the window
        // stays open with both offers untouched and the script's caller can adjust
        // the offer and accept again. Cancelling the whole trade instead handed the
        // goods back and fired @TradeClose on both sides, which a script written
        // against the reference does not expect.
        //
        // Source-X also leaves both check marks SET on this path (:145 sets them,
        // :188 returns before anything clears them), so a later Trade_Status
        // re-fires the trigger. That is reproduced deliberately rather than
        // clearing the flags, which would be a different contract.
        if (FireTradeTrigger(initiator, CharTrigger.TradeAccepted, trade, partner) == TriggerResult.True ||
            FireTradeTrigger(partner, CharTrigger.TradeAccepted, trade, initiator) == TriggerResult.True)
        {
            return;
        }

        if (Trade.VirtualGold.Enabled)
            TransferTradeGold(trade);

        foreach (var item in cont1.Contents.ToList())
            TradeManager.ReturnItemToCharacter(_world, partner, item);
        foreach (var item in cont2.Contents.ToList())
            TradeManager.ReturnItemToCharacter(_world, initiator, item);

        _netState.Send(new PacketSecureTradeClose(
            trade.GetOwnContainer(_character!).Uid.Value));
        SendTradeCloseToPartner?.Invoke(
            trade.GetPartner(_character!),
            trade.GetPartnerContainer(_character!).Uid.Value);

        FireTradeTrigger(initiator, CharTrigger.TradeClose, trade, partner, src: _character);
        FireTradeTrigger(partner, CharTrigger.TradeClose, trade, initiator, src: _character);

        trade.Complete();
        _tradeManager!.EndTrade(trade);

        _world.RemoveItem(cont1);
        _world.RemoveItem(cont2);

        RefreshBackpackContents();
        RefreshBackpackForPartner?.Invoke(trade.GetPartner(_character!));

        SysMessage("Trade complete.");
        SendTradeMessageToPartner?.Invoke(trade.GetPartner(_character!), "Trade complete.");
    }

    internal void SendTradeUpdateToBoth(SecureTrade trade)
    {
        var myCont = trade.GetOwnContainer(_character!);
        bool myAcc = _character == trade.Initiator ? trade.InitiatorAccepted : trade.PartnerAccepted;
        bool theirAcc = _character == trade.Initiator ? trade.PartnerAccepted : trade.InitiatorAccepted;
        _netState.Send(new PacketSecureTradeUpdate(myCont.Uid.Value, myAcc, theirAcc));

        var partner = trade.GetPartner(_character!);
        SendTradeUpdateToPartner?.Invoke(partner, trade);
    }

    /// <remarks>Source-X argument contract per trigger:
    /// @TradeAccepted - Init(self): ARGO is the character the trigger runs on, SRC the
    /// partner, ARGN1/ARGN2 the received/given counts (CItemContainer.cpp:160-188).
    /// @TradeClose - Init(self) as well, and SRC is the character whose window closed
    /// for BOTH calls (CItemContainer.cpp:321-331); pass it as <paramref name="src"/>.
    /// @TradeCreate - ARGO is the item that opened the trade, or nothing, and no ARGN
    /// (CClientUse.cpp:1380-1383).</remarks>
    private TriggerResult FireTradeTrigger(Character target, CharTrigger trigger, SecureTrade trade,
        Character other, Item? offeredItem = null, Character? src = null)
    {
        bool accepted = trigger == CharTrigger.TradeAccepted;
        // ARGN1 is the count of what THIS side receives and ARGN2 of what it gives;
        // both are the length of the list Source-X builds, so they are counted off
        // the same walk that names the refs.
        var incoming = accepted ? BuildOfferedRefs(trade.GetPartnerContainer(target)) : null;
        int outgoing = accepted ? BuildOfferedRefs(trade.GetOwnContainer(target)).Count : 0;
        return _triggerDispatcher?.FireCharTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = src ?? other,
            O1 = trigger == CharTrigger.TradeCreate ? offeredItem : target,
            N1 = accepted ? incoming!.Count : 0,
            N2 = outgoing,
            Refs = incoming
        }) ?? TriggerResult.Default;
    }

    /// <summary>Name each item this side is about to RECEIVE as REF1..REFn, the way
    /// Source-X fills the trigger args' object list (CItemContainer::Trade_Status,
    /// CItemContainer.cpp:196 - m_VarObjs.Insert(i, pItem)). ARGN1 is only the count;
    /// without the list a script cannot tell WHAT it is being handed, which is how the
    /// reference pack's house transfer reads the deed out of an accepted trade
    /// (`for &lt;ARGN1&gt; ... &lt;REF&lt;dLOCAL._FOR&gt;.TYPE&gt;`).</summary>
    private static Dictionary<int, string> BuildOfferedRefs(Item container)
    {
        var refs = new Dictionary<int, string>();
        int index = 1;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            refs[index++] = $"0{item.Uid.Value:X}";
        }
        return refs;
    }


    /// <summary>Handle rename request (0x75).</summary>
    public void HandleRename(uint serial, string name)
    {
        if (_character == null) return;

        if (_character.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("rename_no_permission"));
            return;
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 30)
        {
            SysMessage("Invalid name length.");
            return;
        }

        foreach (char c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '\'')
            {
                SysMessage("Name contains invalid characters.");
                return;
            }
        }

        var target = _world.FindChar(new Serial(serial));
        if (target != null)
        {
            string oldName = target.Name;
            // @Rename runs on the RENAMER with ARGO = the pet being renamed and ARGS = the
            // new name (m_pChar->OnTrigger(CTRIG_Rename...), CClientEvent.cpp:2221-2227).
            var result = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Rename, new TriggerArgs
            {
                CharSrc = _character,
                O1 = target,
                S1 = trimmed
            });
            if (result == TriggerResult.True)
                return;

            target.Name = trimmed;
            SysMessage(ServerMessages.GetFormatted("msg_rename_success", oldName, target.Name));
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            item.Name = trimmed;
            SysMessage(ServerMessages.GetFormatted("rename_item_ok", item.Name));
        }
    }

    /// <summary>Handle client view range change (0xC8).</summary>
    public void HandleViewRange(byte range)
    {
        // Clamp to valid range (4-24)
        if (range < 4) range = 4;
        if (range > 24) range = 24;
        _netState.ViewRange = range;
    }

    /// <summary>Open guild stone gump with member list, options.</summary>
    internal void OpenGuildStoneGump(Item stone)
    {
        if (_character == null || _guildManager == null) return;

        // Source-X: a town stone runs the same stone engine — only the
        // wording, the memory type and who may FOUND it differ. Towns are
        // shard infrastructure, so establishing one takes Counsel+.
        bool isTown = stone.ItemType == ItemType.StoneTown;
        string stoneWord = isTown ? "Town" : "Guild";

        var guild = _guildManager.GetGuild(stone.Uid);
        if (guild == null)
        {
            if (isTown && _character.PrivLevel < PrivLevel.Counsel)
            {
                SysMessage("No town is established at this stone.");
                return;
            }

            // No record on this stone yet — offer to create one
            var createGump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 400, 300);
            createGump.AddResizePic(0, 0, 5054, 400, 300);
            createGump.AddText(30, 20, 0, $"{stoneWord} Stone");
            createGump.AddText(30, 50, 0, $"No {stoneWord.ToLowerInvariant()} is registered to this stone.");
            createGump.AddText(30, 80, 0, isTown ? "Establish a town here?" : "Create a new guild?");
            createGump.AddButton(30, 130, 4005, 4007, 1); // Create
            createGump.AddText(70, 130, 0, isTown ? "Establish Town" : "Create Guild");
            createGump.AddButton(150, 250, 4017, 4019, 0); // Cancel

            SendGump(createGump, (buttonId, switches, textEntries) =>
            {
                if (buttonId == 1)
                {
                    if (_character == null || _character.IsDeleted || stone.IsDeleted ||
                        _world.FindItem(stone.Uid) != stone || _guildManager.GetGuild(stone.Uid) != null ||
                        _guildManager.FindGuildRecordFor(_character.Uid, isTown) != null ||
                        (isTown && _character.PrivLevel < PrivLevel.Counsel) ||
                        _character.MapIndex != stone.MapIndex ||
                        _character.Position.GetDistanceTo(stone.Position) > 3)
                    {
                        SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                        return;
                    }
                    // A town takes its name from the GM-named stone item.
                    string newName = isTown && !string.IsNullOrWhiteSpace(stone.Name)
                        ? stone.Name!
                        : $"{_character.Name}'s {stoneWord}";
                    var newGuild = _guildManager.CreateGuild(stone.Uid, newName, _character.Uid, isTown);
                    StampStoneMemory(_character, stone, add: true);
                    SysMessage(ServerMessages.GetFormatted("guild_created", newGuild.Name));
                }
            });
            return;
        }

        // Show stone info gump
        var gump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 500, 520);
        gump.AddResizePic(0, 0, 5054, 500, 520);
        gump.AddText(30, 10, 0, $"{stoneWord}: {guild.Name}");
        gump.AddText(30, 30, 0, $"Abbreviation: [{guild.Abbreviation}]");
        if (!string.IsNullOrEmpty(guild.Charter))
            gump.AddText(30, 50, 0, $"Charter: {guild.Charter}");
        gump.AddText(30, 70, 0,
            $"{(isTown ? "Citizens" : "Members")}: {guild.MemberCount} | Wars: {guild.Wars.Count()} | Allies: {guild.Allies.Count()}");

        // Member list with titles and candidate status
        int y = 100;
        int memberIdx = 0;
        foreach (var member in guild.Members)
        {
            var ch = _world.FindChar(member.CharUid);
            string memberName = ch?.Name ?? $"UID 0x{member.CharUid.Value:X}";
            string privText = member.Priv switch
            {
                GuildPriv.Master => isTown ? " [Mayor]" : " [Master]",
                GuildPriv.Candidate => " [Candidate]",
                _ => ""
            };
            string titleText = !string.IsNullOrEmpty(member.Title) ? $" ({member.Title})" : "";
            int hue = member.Priv == GuildPriv.Candidate ? 33 : 0; // yellow for candidates
            gump.AddText(50, y, hue, $"{memberName}{privText}{titleText}");
            y += 20;
            memberIdx++;
            if (y > 350) break;
        }

        var myMember = guild.FindMember(_character.Uid);
        int btnY = 370;

        if (myMember == null)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1); // Join
            gump.AddText(70, btnY, 0, isTown ? "Request Citizenship" : "Request to Join");
            btnY += 25;
        }
        else if (myMember.Priv == GuildPriv.Master)
        {
            // Two columns — the master verb surface mirrors the Source-X
            // guild-stone MASTERMENU (CItemStone_functions.tbl).
            gump.AddButton(30, btnY, 4005, 4007, 2); // Disband
            gump.AddText(70, btnY, 0, isTown ? "Dissolve Town" : "Disband Guild");
            gump.AddButton(260, btnY, 4005, 4007, 15); // Dismiss member
            gump.AddText(300, btnY, 0, "Dismiss Member");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10); // Accept candidates
            gump.AddText(70, btnY, 0, "Accept Candidate");
            gump.AddButton(260, btnY, 4005, 4007, 16); // Declare alliance
            gump.AddText(300, btnY, 0, "Declare Alliance");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11); // Set title
            gump.AddText(70, btnY, 0, "Set Member Title");
            gump.AddButton(260, btnY, 4005, 4007, 17); // Break alliance
            gump.AddText(300, btnY, 0, "Break Alliance");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12); // Declare war
            gump.AddText(70, btnY, 0, "Declare War");
            gump.AddButton(260, btnY, 4005, 4007, 13); // Declare peace
            gump.AddText(300, btnY, 0, "Declare Peace");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14); // Set charter
            gump.AddText(70, btnY, 0, "Set Charter");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 1, guild.Charter);
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 18); // Rename guild
            gump.AddText(70, btnY, 0, "Set Name");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 2, guild.Name);
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 19); // Set abbreviation
            gump.AddText(70, btnY, 0, "Set Abbreviation");
            gump.AddTextEntry(170, btnY, 100, 20, 0, 3, guild.Abbreviation);
            btnY += 25;
        }
        else
        {
            gump.AddButton(30, btnY, 4005, 4007, 3); // Leave
            gump.AddText(70, btnY, 0, isTown ? "Renounce Citizenship" : "Leave Guild");
            btnY += 25;
        }
        if (myMember != null && guild.IsMember(_character.Uid))
        {
            // Member-level verbs: abbreviation display toggle + fealty vote
            // (Source-X TOGGLEABBREVIATION / DECLAREFEALTY).
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, myMember.ShowAbbrev ? "Hide Abbreviation" : "Show Abbreviation");
            gump.AddButton(260, btnY, 4005, 4007, 21);
            gump.AddText(300, btnY, 0, "Declare Fealty");
            btnY += 25;
        }
        if (!string.IsNullOrEmpty(guild.WebUrl))
        {
            gump.AddButton(30, btnY, 4005, 4007, 4); // Visit web page (0xA5)
            gump.AddText(70, btnY, 0, "Visit Web Page");
        }
        gump.AddButton(350, 480, 4017, 4019, 0); // Close

        var capturedGuild = guild;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleGuildGumpResponse(stone, capturedGuild, buttonId, textEntries);
        });
    }

    /// <summary>Source-X CStoneMember link: membership carries a MEMORY_GUILD /
    /// MEMORY_TOWN memory of the stone, readable by scripts. Stamped on
    /// acceptance/founding, cleared on leave/dismiss/disband.</summary>
    private void StampStoneMemory(Character ch, Item stone, bool add)
    {
        var memType = stone.ItemType == ItemType.StoneTown ? MemoryType.Town : MemoryType.Guild;
        if (add)
        {
            ch.Memory_AddObjTypes(stone.Uid, memType);
        }
        else
        {
            var mem = ch.Memory_FindObjTypes(stone.Uid, memType);
            if (mem != null)
                ch.Memory_ClearTypes(mem, memType);
        }
    }

    private void HandleGuildGumpResponse(Item stone, GuildDef guild, uint buttonId, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null || _guildManager == null) return;
        if (stone.IsDeleted || _world.FindItem(stone.Uid) != stone ||
            _guildManager.GetGuild(stone.Uid) != guild)
            return;

        if ((buttonId is 2 or (>= 10 and <= 19)) &&
            guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
        {
            SysMessage(ServerMessages.Get("msg_insufficient_priv"));
            return;
        }

        switch (buttonId)
        {
            case 1: // Join request
                // One guild AND one town per character — the pools are
                // independent (Source-X MEMORY_GUILD vs MEMORY_TOWN), so a
                // guild member may still become a town citizen and vice versa.
                if (_guildManager.FindGuildRecordFor(_character.Uid,
                        stone.ItemType == ItemType.StoneTown) != null)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                guild.AddRecruit(_character.Uid);
                SysMessage(ServerMessages.Get("guild_join_request"));
                break;
            case 2: // Disband — only guild master may disband
            {
                var disbandMember = guild.FindMember(_character.Uid);
                if (disbandMember == null || disbandMember.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                foreach (var m in guild.Members.ToArray())
                {
                    var mch = _world.FindChar(m.CharUid);
                    if (mch != null)
                        StampStoneMemory(mch, stone, add: false);
                }
                _guildManager.RemoveGuild(stone.Uid, _world);
                SysMessage(ServerMessages.Get("guild_disbanded"));
                break;
            }
            case 4: // Visit web page — 0xA5 opens the client's browser
                if (!string.IsNullOrEmpty(guild.WebUrl))
                    Send(new PacketWebLink(guild.WebUrl));
                break;
            case 3: // Leave — must actually belong to this guild (button can be
                    // spoofed by a crafted client packet, so don't trust the gump).
            {
                var leaveMember = guild.FindMember(_character.Uid);
                if (leaveMember == null)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                // The shared departure (election, forced peace, stone memory) - the
                // script RESIGN verb takes the same path.
                _guildManager.MemberLeft(guild, _character.Uid, _world);
                SysMessage(ServerMessages.Get("guild_left"));
                break;
            }
            case 10: // Accept candidate
                SysMessage(ServerMessages.Get("guild_target_candidate"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (guild.AcceptMember(target.Uid))
                    {
                        StampStoneMemory(target, stone, add: true);
                        SysMessage(ServerMessages.GetFormatted("guild_member_added", target.Name));
                    }
                    else
                        SysMessage(ServerMessages.GetFormatted("guild_not_candidate", target.Name));
                });
                break;
            case 11: // Set member title
                SysMessage(ServerMessages.Get("guild_target_title"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var member = guild.FindMember(target.Uid);
                    if (member == null) { SysMessage(ServerMessages.Get("guild_not_member")); return; }
                    // Use text entry if provided
                    var titleEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                    if (!string.IsNullOrWhiteSpace(titleEntry.Text))
                    {
                        member.Title = titleEntry.Text.Trim()[..Math.Min(40, titleEntry.Text.Trim().Length)];
                        SysMessage(ServerMessages.GetFormatted("guild_title_set", target.Name, member.Title));
                    }
                    else
                        SysMessage(ServerMessages.Get("guild_no_title"));
                });
                break;
            case 12: // Declare war
                SysMessage(ServerMessages.Get("guild_target_enemy"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var enemyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (enemyGuild == null) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    _guildManager.DeclareWar(stone.Uid, targetItem.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_war_declared", enemyGuild.Name));
                });
                break;
            case 13: // Declare peace
                SysMessage(ServerMessages.Get("guild_target_peace"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    _guildManager.DeclarePeace(stone.Uid, targetItem.Uid);
                    SysMessage(ServerMessages.Get("guild_peace_declared"));
                });
                break;
            case 14: // Set charter
            {
                var charterEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                if (!string.IsNullOrWhiteSpace(charterEntry.Text))
                {
                    var charter = charterEntry.Text.Trim();
                    guild.Charter = charter[..Math.Min(200, charter.Length)];
                    SysMessage(ServerMessages.Get("guild_charter_updated"));
                }
                break;
            }
            case 15: // Dismiss member (Source-X DISMISSMEMBER) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the member to dismiss.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (target.Uid == _character.Uid) { SysMessage("Use Disband or Leave instead."); return; }
                    if (guild.FindMember(target.Uid) == null)
                    {
                        SysMessage(ServerMessages.Get("guild_not_member"));
                        return;
                    }
                    // Dismissal deletes the record like any departure (election,
                    // forced peace, stone memory) - the shared MemberLeft path.
                    _guildManager?.MemberLeft(guild, target.Uid, _world);
                    SysMessage($"{target.Name} has been dismissed from the {(stone.ItemType == ItemType.StoneTown ? "town" : "guild")}.");
                });
                break;
            }
            case 16: // Declare alliance (Source-X DECLAREPEACE/ally path) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the guild stone to ally with.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var allyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (allyGuild == null || allyGuild == guild) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    _guildManager.DeclareAlliance(stone.Uid, targetItem.Uid);
                    _guildManager.DeclareAlliance(targetItem.Uid, stone.Uid);
                    SysMessage($"Alliance declared with {allyGuild.Name}.");
                });
                break;
            }
            case 17: // Break alliance — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the allied guild stone.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    _guildManager.WithdrawAlliance(stone.Uid, targetItem.Uid);
                    _guildManager.WithdrawAlliance(targetItem.Uid, stone.Uid);
                    SysMessage("The alliance has been dissolved.");
                });
                break;
            }
            case 18: // Rename guild (Source-X SETNAME) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                var nameEntry = textEntries.FirstOrDefault(e => e.Id == 2);
                if (!string.IsNullOrWhiteSpace(nameEntry.Text))
                {
                    var guildName = nameEntry.Text.Trim();
                    guild.Name = guildName[..Math.Min(40, guildName.Length)];
                    SysMessage($"The guild is now known as {guild.Name}.");
                }
                break;
            }
            case 19: // Set abbreviation (Source-X SETABBREVIATION) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                var abbrevEntry = textEntries.FirstOrDefault(e => e.Id == 3);
                if (!string.IsNullOrWhiteSpace(abbrevEntry.Text))
                {
                    var abbreviation = abbrevEntry.Text.Trim();
                    guild.Abbreviation = abbreviation[..Math.Min(4, abbreviation.Length)];
                    SysMessage($"Guild abbreviation set to [{guild.Abbreviation}].");
                }
                break;
            }
            case 20: // Toggle abbreviation display (Source-X TOGGLEABBREVIATION)
            {
                var toggleMember = guild.FindMember(_character.Uid);
                if (toggleMember == null || !guild.IsMember(_character.Uid))
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                toggleMember.ShowAbbrev = !toggleMember.ShowAbbrev;
                SysMessage(toggleMember.ShowAbbrev
                    ? "Your guild abbreviation is now shown."
                    : "Your guild abbreviation is now hidden.");
                break;
            }
            case 21: // Declare fealty (Source-X DECLAREFEALTY) — vote, then recount
            {
                var voter = guild.FindMember(_character.Uid);
                if (voter == null || !guild.IsMember(_character.Uid))
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the member you pledge your loyalty to.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var currentVoter = guild.FindMember(_character.Uid);
                    if (currentVoter == null || !guild.IsMember(_character.Uid)) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (!guild.IsMember(target.Uid))
                    {
                        SysMessage(ServerMessages.Get("guild_not_member"));
                        return;
                    }
                    currentVoter.LoyalTo = target.Uid;
                    guild.ElectMaster(); // recount — mastership follows the votes
                    SysMessage($"You are now loyal to {target.Name}.");
                });
                break;
            }
        }
    }

    /// <summary>Open house management gump from house sign or multi item.</summary>
    internal void OpenHouseSignGump(Item signOrMulti)
    {
        if (_character == null || _housingEngine == null) return;

        // Find the house — could be the multi item itself or linked via tag
        var house = _housingEngine.GetHouse(signOrMulti.Uid);
        if (house == null && signOrMulti.Link.IsValid)
            house = _housingEngine.GetHouse(signOrMulti.Link);
        if (house == null && signOrMulti.TryGetTag("HOUSE_UID", out string? houseUidStr) &&
            ScriptNumber.TryParseUInt(houseUidStr, out uint houseUid))
        {
            house = _housingEngine.GetHouse(new Serial(houseUid));
        }

        if (house == null)
        {
            SysMessage(ServerMessages.Get("house_not_house"));
            return;
        }

        // Auto-refresh on owner visit
        _housingEngine.OnCharacterEnterHouse(_character, house);

        var priv = house.GetPriv(_character.Uid);
        var ownerCh = _world.FindChar(house.Owner);
        string ownerName = ownerCh?.Name ?? "Unknown";

        bool isStaff = _character.PrivLevel >= PrivLevel.GM;
        bool isOwner = priv == HousePriv.Owner || isStaff;
        bool canManageStorage = priv is HousePriv.Owner or HousePriv.CoOwner || isStaff;

        var gump = new GumpBuilder(_character.Uid.Value, signOrMulti.Uid.Value, 420, 540);
        gump.AddResizePic(0, 0, 5054, 420, 540);
        gump.AddText(30, 10, 0, "House Management");
        gump.AddText(30, 35, 0, $"Owner: {ownerName}");
        gump.AddText(30, 55, 0, $"Type: {house.Type}");
        gump.AddText(30, 75, 0, $"Storage: {house.Lockdowns.Count}/{house.MaxLockdowns} lockdowns, {house.SecureContainers.Count}/{house.MaxSecure} secure");
        gump.AddText(30, 95, 0, $"Condition: {house.DecayStage}");
        gump.AddText(30, 115, 0, $"Co-Owners: {house.CoOwners.Count}  Friends: {house.Friends.Count}");

        int btnY = 145;
        if (isOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1);
            gump.AddText(70, btnY, 0, "Transfer House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 2);
            gump.AddText(70, btnY, 0, "Demolish House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10);
            gump.AddText(70, btnY, 0, "Add Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12);
            gump.AddText(70, btnY, 0, "Remove Co-Owner");
            btnY += 25;
        }
        if (canManageStorage)
        {
            gump.AddButton(30, btnY, 4005, 4007, 11);
            gump.AddText(70, btnY, 0, "Add Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13);
            gump.AddText(70, btnY, 0, "Remove Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14);
            gump.AddText(70, btnY, 0, "Lock Down Item");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 15);
            gump.AddText(70, btnY, 0, "Release Lockdown");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 16);
            gump.AddText(70, btnY, 0, "Secure Container");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 17);
            gump.AddText(70, btnY, 0, "Release Secure");
            btnY += 25;
        }
        if (isOwner && house.MultiItem.ItemType == ItemType.MultiCustom)
        {
            gump.AddButton(30, btnY, 4005, 4007, 4);
            gump.AddText(70, btnY, 0, "Customize House");
            btnY += 25;
        }
        if (isOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, "Ban Player");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 21);
            gump.AddText(70, btnY, 0, "Unban Player");
            btnY += 25;
        }
        if (isStaff || house.CanAccess(_character.Uid))
        {
            gump.AddButton(30, btnY, 4005, 4007, 3);
            gump.AddText(70, btnY, 0, "Open Door");
            btnY += 25;
        }
        gump.AddButton(280, 505, 4017, 4019, 0); // Close

        var capturedHouse = house;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleHouseGumpResponse(signOrMulti, capturedHouse, buttonId);
        });
    }

    private void HandleHouseGumpResponse(Item signOrMulti, House house, uint buttonId)
    {
        if (_character == null || _housingEngine == null) return;

        bool IsRegistered() => _housingEngine.GetHouse(house.MultiItem.Uid) == house;
        bool HasOwnerAuthority() => IsRegistered() &&
            (_character.PrivLevel >= PrivLevel.GM || house.Owner == _character.Uid);
        bool HasStorageAuthority() => IsRegistered() &&
            (_character.PrivLevel >= PrivLevel.GM || house.CanLockdown(_character.Uid));

        bool authorized = buttonId switch
        {
            1 or 2 or 4 or 10 or 12 or 20 or 21 => HasOwnerAuthority(),
            11 or 13 or 14 or 15 or 16 or 17 => HasStorageAuthority(),
            3 => IsRegistered() && (_character.PrivLevel >= PrivLevel.GM || house.CanAccess(_character.Uid)),
            _ => true,
        };
        if (!authorized)
        {
            SysMessage("You do not have permission to manage this house.");
            return;
        }

        switch (buttonId)
        {
            case 1: // Transfer — target the new owner
                SysMessage(ServerMessages.Get("house_select_owner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer)
                    {
                        SysMessage(ServerMessages.Get("msg_invalid_target"));
                        return;
                    }
                    // Transfer must respect the recipient's house cap, same as
                    // PlaceHouse — otherwise it's an easy way to exceed the limit.
                    if (!_housingEngine.TransferHouse(house, _character, target))
                    {
                        SysMessage(ServerMessages.Get("house_add_limit"));
                        return;
                    }
                    SysMessage(ServerMessages.GetFormatted("house_transferred", target.Name));
                });
                break;
            case 2: // Demolish
                var deed = _housingEngine.RemoveHouse(house.MultiItem.Uid, _character);
                if (deed != null)
                    SysMessage(ServerMessages.Get("house_demolished"));
                else
                    SysMessage(ServerMessages.Get("house_cant_demolish"));
                break;
            case 3: // Open door
                OpenDoor();
                break;
            case 4: // Customize House — enter the client design editor
                BeginHouseCustomization(house.MultiItem);
                break;
            case 10: // Add Co-Owner
                SysMessage(ServerMessages.Get("house_add_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_coowner", target.Name));
                });
                break;
            case 11: // Add Friend
                SysMessage(ServerMessages.Get("house_add_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_friend", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_friend", target.Name));
                });
                break;
            case 12: // Remove Co-Owner
                SysMessage(ServerMessages.Get("house_remove_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_coowner"));
                });
                break;
            case 13: // Remove Friend
                SysMessage(ServerMessages.Get("house_remove_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_friend", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_friend"));
                });
                break;
            case 14: // Lock Down Item
                SysMessage(ServerMessages.Get("house_lockdown"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    var lockItem = _world.FindItem(targetUid);
                    if (lockItem == null || _housingEngine?.FindHouseAt(lockItem.Position) != house)
                    {
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                        return;
                    }
                    if (house.Lockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                });
                break;
            case 15: // Release Lockdown
                SysMessage(ServerMessages.Get("house_lockdown_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.ReleaseLockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_released"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_not"));
                });
                break;
            case 16: // Secure Container
                SysMessage(ServerMessages.Get("house_secure"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.SecureContainer(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_fail"));
                });
                break;
            case 17: // Release Secure
                SysMessage(ServerMessages.Get("house_secure_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.ReleaseSecure(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_released"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_not"));
                });
                break;
            case 20: // Ban
                SysMessage(ServerMessages.Get("house_ban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (_housingEngine.BanFromHouse(house, target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_banned", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_banned", target.Name));
                });
                break;
            case 21: // Unban
                SysMessage(ServerMessages.Get("house_unban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_unbanned", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_banned"));
                });
                break;
        }
    }


    public void OpenDoor(int distance = 1)
    {
        if (_character == null) return;
        if (_character.IsDead) return;
        distance = Math.Clamp(distance, 0, 14);
        var (dx, dy) = ((byte)_character.Direction & 7) switch
        {
            0 => (0, -1), 1 => (1, -1), 2 => (1, 0), 3 => (1, 1),
            4 => (0, 1), 5 => (-1, 1), 6 => (-1, 0), _ => (-1, -1)
        };
        var center = new Point3D((short)(_character.X + dx), (short)(_character.Y + dy),
            _character.Z, _character.MapIndex);
        foreach (var item in _world.GetItemsInRange(center, distance))
        {
            if (!DoorHelper.IsDoorItem(item, _world.MapData) || Math.Abs(item.Z - _character.Z) >= 20)
                continue;
            SysMessage(ServerMessages.Get(Msg.MacroOpendoor));
            _client.HandleDoubleClick(item.Uid.Value);
            return;
        }

        TryToggleNearestMapStaticDoor(0, center, distance);
    }

    internal bool TryToggleNearestMapStaticDoor(uint clientSerial, Point3D? searchCenter = null, int radius = 2)
    {
        if (_character == null) return false;
        if (_character.IsDead) return false;
        var center = searchCenter ?? _character.Position;
        if (!DoorHelper.FindNearestStaticDoor(
                _world.MapData, _character.MapIndex, center.X, center.Y, radius,
                out short x, out short y, out sbyte z, out ushort tileId, out ushort hue))
            return false;

        if (searchCenter.HasValue && Math.Abs(z - _character.Z) >= 20) return false;
        if (searchCenter.HasValue && (Math.Abs(x - _character.X) > 2 || Math.Abs(y - _character.Y) > 2))
            return false;

        bool open = _world.IsMapStaticDoorOpen(_character.MapIndex, x, y, z);
        // Classic-set doors: derive the pair arts from the doordir slot so
        // closing restores the STATIC's own art (the old tileId−1 walked
        // below the closed art when the map static was the closed leaf), and
        // shift the open visual by the Source-X hinge offset.
        int doorDir = DoorHelper.GetDoorDir(tileId);
        ushort closedArt = doorDir >= 0 ? (ushort)(tileId - (doorDir & 1)) : (ushort)(tileId - 1);
        ushort openArt = doorDir >= 0 ? (ushort)(closedArt + 1) : (ushort)(tileId + 1);
        bool opening = !open;
        ushort newTile = opening ? openArt : closedArt;
        short vx = x, vy = y;
        if (opening && doorDir >= 0)
        {
            var (sx, sy) = DoorHelper.GetDoorShift(doorDir & ~1);
            vx += sx;
            vy += sy;
        }
        _world.SetMapStaticDoorOpen(_character.MapIndex, x, y, z, opening);

        // Reuse the client's serial only when it is one of OUR synthetic static
        // serials (item-flagged coordinate encoding). Broadcasting the door art
        // under any other serial re-types that object client-side — with a
        // character uid it made the mobile vanish into a door graphic.
        uint serial = clientSerial != 0 && (clientSerial & Serial.ItemFlag) != 0
            ? clientSerial
            : (uint)(Serial.ItemFlag | (uint)((x & 0x7FFF) << 16) | (uint)((y & 0x3FFF) << 3) | (uint)(z & 0x07));
        BroadcastMapStaticDoorUpdate(serial, newTile, vx, vy, z, hue, opening);
        return true;
    }

    private void BroadcastMapStaticDoorUpdate(
        uint serial, ushort tileId, short x, short y, sbyte z, ushort hue, bool opening)
    {
        if (_character == null) return;
        var pos = new Point3D(x, y, z, _character.MapIndex);
        ushort soundId = (ushort)(opening ? 0x00EA : 0x00F1);
        var soundPacket = new PacketSound(soundId, x, y, z);
        BroadcastNearby?.Invoke(pos, UpdateRange, soundPacket, 0);

        _netState.Send(BuildWorldItemPacket(serial, tileId, 1, x, y, z, hue));
        var broadcastPacket = new PacketWorldItem(serial, tileId, 1, x, y, z, hue);
        BroadcastNearby?.Invoke(pos, UpdateRange, broadcastPacket, _character.Uid.Value);
    }

    /// <summary>Raise or lower a portcullis. Source-X gives the vertical gate its own
    /// function (Use_Portculis, CItem.cpp:4583): it moves the gate between the two
    /// heights in MORE1 and MORE2 and does NOT touch the graphic. SphereNet sent it
    /// down the hinged-door path instead, which swapped the art by an offset of two
    /// and left the gate at its old Z, so the designed heights never took effect and
    /// the classic door's 20-second timer was inherited along with them.</summary>
    internal bool UsePortcullis(Item gate)
    {
        // Not while it is inside something (:4587).
        if (gate.ContainedIn.IsValid)
            return false;

        sbyte lowered = unchecked((sbyte)gate.More1);
        sbyte raised = unchecked((sbyte)gate.More2);
        sbyte target = gate.Z == lowered ? raised : lowered;
        if (target == gate.Z)
            return false;       // both heights the same: nothing to do (:4596)

        var world = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke();
        var pos = new Point3D(gate.X, gate.Y, target, gate.MapIndex);
        if (world != null)
            world.PlaceItem(gate, pos);
        else
            gate.Position = pos;

        // PORTCULISSOUND overrides the default (:4602).
        ushort sound = gate.TryGetTag("PORTCULISSOUND", out string? raw) &&
            ScriptNumber.TryParseUShort(raw, out ushort custom) && custom != 0 ? custom : (ushort)0x021D;
        BroadcastNearby?.Invoke(gate.Position, UpdateRange,
            new PacketSound(sound, gate.X, gate.Y, gate.Z), 0);
        BroadcastNearby?.Invoke(gate.Position, UpdateRange,
            new PacketWorldItem(gate.Uid.Value, gate.DispIdFull, gate.Amount,
                gate.X, gate.Y, gate.Z, gate.Hue), 0);
        return true;
    }

    /// <summary>Follow an item's LINK chain after it has been used. Source-X returns
    /// MASK_RETURN_FOLLOW_LINKS from Do_Use_Item and Use_Item then walks m_uidLink,
    /// calling Do_Use_Item(target, fLink: true) for up to 64 hops, stopping on a
    /// missing target or a return to the item it started from (CCharUse.cpp:1962).
    /// A lever's graphic flipped but the door it was linked to never moved.
    ///
    /// The chain applies the LINKED use, not a fresh double-click: a link only ever
    /// opens a door (:4641), and it carries the authority that lets it work a locked
    /// portcullis (:1771).</summary>
    internal void FollowItemLinks(Item start)
    {
        var current = start;
        for (int hop = 0; hop < 64; hop++)
        {
            if (!current.Link.IsValid)
                return;

            var next = _world.FindItem(current.Link);
            if (next == null || next.IsDeleted || next == start)
                return;

            switch (next.ItemType)
            {
                case ItemType.Door:
                case ItemType.DoorLocked:
                    ToggleDoor(next, justOpen: true, viaLink: true);
                    break;
                case ItemType.Portculis:
                case ItemType.PortLocked:
                    UsePortcullis(next);
                    break;
                case ItemType.Switch:
                    // A linked switch flips too (Do_Use_Item IT_SWITCH with fLink,
                    // CCharUse.cpp:1763).
                    next.SetSwitchState();
                    break;
            }

            current = next;
        }
    }

    /// <param name="justOpen">A link signal only ever opens: Source-X returns early
    /// when a just-open use finds the door already open (Use_DoorNew, CItem.cpp:4641),
    /// so a second pull of the lever does not close it.</param>
    /// <param name="viaLink">The use arrived through a LINK rather than from the
    /// player's own hand, so the door may be out of reach - the lever was what they
    /// touched.</param>
    internal void ToggleDoor(Item door, bool justOpen = false, bool viaLink = false)
    {
        if (_character == null) return;
        if (_character.IsDead) return;

        // Source-X refuses a door that is not top-level before touching anything
        // (Use_DoorNew :4637, Use_Door :4695). Without it, a door carried in a pack
        // had its container slot read as world coordinates: the hinge shift was added
        // to them and PlaceItem then dropped the door out of the pack onto the map.
        // The timer close path reached the same code, so a door picked up while open
        // left the backpack on its own.
        if (door.ContainedIn.IsValid)
            return;

        if (!viaLink)
        {
            int dx = Math.Abs(_character.X - door.X);
            int dy = Math.Abs(_character.Y - door.Y);
            if (_character.MapIndex != door.MapIndex || dx > 2 || dy > 2)
            {
                SysMessage("That is too far away.");
                return;
            }
        }

        bool isOpen = door.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1";
        int doorDir = DoorHelper.GetDoorDir(door.DispIdFull);
        // The graphic IS the state for a classic set, whose ids come in closed/open
        // pairs. A custom door's alternate art is not part of that table - it may even
        // land on another set's closed slot - so its state is the flag alone, which is
        // what the reference reads too (ATTR_OPENED).
        if (doorDir >= 0 && door.DoorOpenId == 0)
            isOpen = (doorDir & 1) != 0;

        if (justOpen && isOpen)
            return;

        // Source-X Use_DoorNew resolves an alternate open graphic first - the
        // DOOROPENID override, or the itemdef's door switch id - and only falls back
        // to the classic hinge table when there is none (CItem.cpp:4649). SphereNet
        // had no DOOROPENID at all, so a door scripted with a custom open art and
        // offset behaved like an ordinary one.
        if (door.DoorOpenId != 0)
        {
            UseCustomDoor(door, isOpen);
            return;
        }

        ushort displayId = door.DispIdFull;
        ushort newDisplayId = (ushort)(displayId + (isOpen ? -1 : 1));
        if (door.DispIdOverride != 0)
            door.TrySetProperty("DISPID", $"0{newDisplayId:X}");
        else
            door.BaseId = newDisplayId;

        if (isOpen)
            door.RemoveTag("DOOR_OPEN");
        else
            door.SetTag("DOOR_OPEN", "1");

        // Source-X Use_Door: the leaf swings around its hinge, so the item
        // MOVES as it opens/closes. Without the shift the open art renders
        // anchored at the closed tile — the door looks like it opens
        // backwards / into the wall.
        DoorHelper.MoveDoorLeaf(door, doorDir);

        // Source-X _SetTimeoutS(20): an opened door swings shut on its own.
        door.SetTimeout(isOpen ? 0 : Environment.TickCount64 + 20_000);
        Character.Diagnostic?.Invoke(
            $"[door] 0x{door.Uid.Value:X} {(isOpen ? "closed" : "opened")} by 0x{_character.Uid.Value:X} " +
            $"'{_character.Name}' -> {door.Position} art 0x{door.DispIdFull:X}");

        // Play door sound and broadcast updated item to nearby clients
        // The pair was hard-coded here, so DOOROPENSOUND/DOORCLOSESOUND were read by
        // nobody: the door sounded right and the keys were silently inert.
        ushort soundId = door.GetDoorSound(opening: !isOpen);
        var soundPacket = new PacketSound(soundId, door.X, door.Y, door.Z);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, soundPacket, 0);

        SendDoorUpdate(door);
    }

    /// <summary>Redraw a door that just swung for everyone who can see it, each
    /// with their own packet (0xF3 or 0x1A, the viewer's MOVABLE flag and the
    /// tile's light). A bare 0x1A broadcast carried none of that, so the opener's
    /// copy disagreed with the view refresh that followed a moment later.</summary>
    private void SendDoorUpdate(Item door)
    {
        if (Item.OnVisualUpdate is { } redraw)
        {
            redraw(door);
            return;
        }
        _client.SendWorldItem(door);
    }

    /// <summary>The custom half of Source-X Use_DoorNew (CItem.cpp:4633): swap to the
    /// alternate graphic, shift by MOREP rather than the hinge table, and remember the
    /// graphic just replaced in DOOROPENID so the next use swaps straight back.</summary>
    private void UseCustomDoor(Item door, bool isOpen)
    {
        short dx = door.MoreP.X;
        short dy = door.MoreP.Y;

        var pos = new Point3D(
            (short)(isOpen ? door.X - dx : door.X + dx),
            (short)(isOpen ? door.Y - dy : door.Y + dy),
            door.Z, door.MapIndex);

        // The graphic being replaced becomes the alternate for the way back (:4681).
        ushort previous = door.DispIdFull;
        ushort alternate = door.DoorOpenId;
        door.DoorOpenId = previous;
        if (door.DispIdOverride != 0)
            door.TrySetProperty("DISPID", $"0{alternate:X}");
        else
            door.BaseId = alternate;

        if (isOpen)
            door.RemoveTag("DOOR_OPEN");
        else
            door.SetTag("DOOR_OPEN", "1");

        var world = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke();
        if (world != null)
            world.PlaceItem(door, pos);
        else
            door.Position = pos;

        door.SetTimeout(isOpen ? 0 : Environment.TickCount64 + 20_000);

        ushort soundId = (ushort)(isOpen ? 0x00F1 : 0x00EA);
        BroadcastNearby?.Invoke(door.Position, UpdateRange,
            new PacketSound(soundId, door.X, door.Y, door.Z), 0);
        SendDoorUpdate(door);
    }

    /// <summary>A craft stroke's animation and sound: Source-X Skill_GetAnim and
    /// Skill_GetSound (CCharSkill.cpp:3526-3569). Smithing is the only craft that
    /// animates - its one-handed swing, which UpdateAnimate turns into the hammer's
    /// own swing (GenerateAnimate, CCharAct.cpp:811-850) - and tinkering and cooking
    /// make no stroke sound. The bows, slashes and bashes this table used to give
    /// every other craft, and the tinkering and cooking sounds, are not upstream's.
    /// Zero means none.</summary>
    internal static (ushort Anim, ushort Sound) GetCraftAnimAndSound(SkillType skill) =>
        (SkillEngine.GetSkillAnim(skill) ?? 0, SkillEngine.GetSkillSound(skill));

    /// <summary>Source-X CChar::Use_Drink (CCharUse.cpp:983-1112), the one path for
    /// IT_POTION, IT_DRINK, IT_PITCHER, IT_WATER_WASH and IT_BOOZE
    /// (Do_Use_Item, :1860-1868).
    ///
    /// The drink is refused when it cannot be moved; @Drink sees the effect delay
    /// (ARGN1: (TDATA2 ?: 1500 booze / 15 other) * 10 tenths), the amount to consume
    /// (ARGN2) and LOCAL.BottleId (TDATA1), and RETURN 1 stops everything. Then booze
    /// makes the drinker drunk, a potion conveys its MORE1 spell at MORE2 strength
    /// behind the LAYER_FLAG_PotionUsed cooldown, and a plain drink feeds only with
    /// OF_DrinkIsFood. There is no stamina gain and no @Eat. The consumed units go and
    /// the TDATA1 empty container is bounced into the pack.</summary>
    internal void UsePotion(Item potion)
    {
        if (_character == null) return;

        if (!ItemMoveRules.CanMove(_character, potion, out _))
        {
            SysMessage(ServerMessages.Get(Msg.DrinkCantmove));
            return;
        }

        var drinkDef = DefinitionLoader.GetItemDef(potion.BaseId);
        bool isBooze = potion.ItemType == ItemType.Booze;
        ushort bottleId = Item.ResolveTDataId(drinkDef?.TData1 ?? 0, drinkDef?.TData1Name);
        uint delaySeconds = drinkDef?.TData2 ?? 0;
        long delayTenths = (delaySeconds != 0 ? delaySeconds : (isBooze ? 1500u : 15u)) * 10L;
        int consume = 1;
        int bottleAmount = consume;

        if (_triggerDispatcher != null)
        {
            var locals = new SphereNet.Scripting.Variables.VarMap();
            locals.SetInt("BottleId", bottleId);
            var args = new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = potion,
                O1 = potion,
                N1 = delayTenths,
                N2 = consume,
                Locals = locals,
            };
            var ret = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Drink, args);
            bottleId = (ushort)locals.GetInt("BottleId");
            delayTenths = args.N1 > 0 ? SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N1) : 1;
            consume = SphereNet.Core.Types.ScriptNumber.ToEngineInt(args.N2);
            bottleAmount = consume;
            // Source-X reads the raw TRIGRET number (CCharUse.cpp:1017-1022): RETURN 1
            // stops, RETURN 5 (TRIGRET_ELSEIF) still consumes ARGN2 but hands back ONE
            // empty bottle, RETURN 6 (TRIGRET_RET_HALFBAKED) hands back none.
            long drinkReturn = args.ReturnNumber ?? (ret == TriggerResult.True ? 1L : 0L);
            if (drinkReturn == DrinkReturnElseIf)
                bottleAmount = 1;
            else if (drinkReturn == DrinkReturnHalfBaked)
                bottleAmount = 0;
            else if (ret == TriggerResult.True)
                return;
        }

        if (consume > 0 && potion.Amount < consume)
        {
            SysMessage(ServerMessages.GetFormatted(Msg.DrinkNotEnough, potion.GetName()));
            return;
        }

        if (isBooze)
        {
            // Liquor at rand(300)+10 strength; a running one is strengthened (:1034-1053).
            _client.Spells?.ApplyDirectEffect(_character, _character,
                SphereNet.Core.Enums.SpellType.Liquor, Random.Shared.Next(300) + 10);
        }
        else if (potion.ItemType == ItemType.Potion)
        {
            // Time limit on using potions: the LAYER_FLAG_PotionUsed marker (:1057-1066).
            if (HasPotionUsedMarker(_character))
            {
                SysMessage(ServerMessages.Get(Msg.DrinkPotionDelay));
                return;
            }
            if (!ApplyPotionEffect(_character, potion))
                SysMessage(ServerMessages.Get("potion_drink"));
            AddPotionUsedMarker(_character, delayTenths);
        }
        else if (potion.ItemType == ItemType.Drink &&
                 (GameClient.ServerOptionFlags & OptionFlags.DrinkIsFood) != 0)
        {
            // OF_DrinkIsFood (:1068-1087): MOREM, else the itemdef volume, at least 1.
            if (_character.Food >= _character.MaxFood)
            {
                SysMessage(ServerMessages.Get(Msg.DrinkFull));
                return;
            }
            int restore = SphereNet.Game.NPCs.EatEngine.RestorePerUnit(potion);
            _character.Food = (ushort)Math.Min(_character.MaxFood, _character.Food + restore);
            int coat = CombatEngine.GetWeaponPoisonSkill(potion);
            if (coat > 0)
                _character.SetPoison(coat * 10, 1 + coat / 50, _character);
        }

        PlayAnimation(_character, (ushort)AnimationType.Eat);
        SendCharacterStatus(_character);

        if (consume > 0)
        {
            if (potion.Amount > consume)
            {
                potion.Amount -= (ushort)consume;
                if (potion.ContainedIn.IsValid)
                    SendContainerItemPacket(new PacketContainerItem(
                        potion.Uid.Value, potion.DispIdFull, 0, potion.Amount, potion.X, potion.Y,
                        potion.ContainedIn.Value, potion.Hue, _netState.IsClientPost6017));
            }
            else
            {
                _client.TryDeleteItemFromClient(potion);
            }
        }

        // Create the empty bottle (:1104-1111), bounced into the drinker's pack.
        if (bottleId != 0 && bottleAmount > 0)
        {
            var empty = _world.CreateItem();
            empty.BaseId = bottleId;
            if (DefinitionLoader.GetItemDef(bottleId) is { } emptyDef)
                empty.ItemType = emptyDef.Type;
            empty.Amount = (ushort)Math.Clamp(bottleAmount, 1, ushort.MaxValue);
            _client.PlaceItemInPack(_character, empty);
        }
    }

    /// <summary>TRIGRET_ELSEIF / TRIGRET_RET_HALFBAKED (CScriptObj.h:34-47): the enum
    /// positions a script's RETURN names, which Use_Drink reads as bottle counts.</summary>
    internal const long DrinkReturnElseIf = 5;
    internal const long DrinkReturnHalfBaked = 6;

    /// <summary>LAYER_FLAG_PotionUsed (uofiles_enums.h:613): the potion cooldown
    /// marker a drinker wears. An expired one that its timer has not yet removed
    /// counts as gone.</summary>
    internal static bool HasPotionUsedMarker(Character ch)
    {
        var mem = ch.GetEquippedItem(Layer.FlagPotionUsed);
        if (mem == null || mem.IsDeleted)
            return false;
        if (mem.Timeout > 0 && Environment.TickCount64 >= mem.Timeout)
        {
            mem.Delete();
            return false;
        }
        return true;
    }

    /// <summary>Spell_Effect_Create(SPELL_NONE, LAYER_FLAG_PotionUsed, .., dwDelay)
    /// (CCharUse.cpp:1066): a timed marker, duration in tenths of a second.</summary>
    internal static void AddPotionUsedMarker(Character ch, long delayTenths)
    {
        var world = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke();
        if (world == null) return;
        var mem = world.CreateItem();
        mem.BaseId = 0x1F14; // the generic memory/rune graphic
        mem.Name = "Potion Used";
        mem.ItemType = ItemType.EqMemoryObj;
        mem.SetAttr(ObjAttributes.Newbie | ObjAttributes.Move_Never);
        if (!ch.Equip(mem, Layer.FlagPotionUsed))
        {
            world.DeleteObject(mem);
            return;
        }
        mem.SetTimeout(Environment.TickCount64 + Math.Max(1, delayTenths) * 100);
    }

    /// <summary>Resolve the spell a drink conveys (Source-X m_itPotion.m_Type):
    /// numeric MORE1, the MORE1_DEFNAME routing tag (s_heal, ...), or — for
    /// old-system bottles — the legacy POTION_TYPE tag mapped to its spell.</summary>
    /// <summary>
    /// Convey a potion's stored effect to somebody — the part of Use_Drink that is not
    /// about the drinker being the client's own character.
    ///
    /// A potion carries a SPELL in MORE1 at the strength in MORE2; drinking it is
    /// delivering that spell. Pulling the resolution out here keeps CANPETSDRINKPOTION
    /// from needing a second potion reader, which is how the two would drift into
    /// disagreeing about what a bottle contains.
    /// </summary>
    /// <returns>False when the bottle holds no resolvable effect.</returns>
    internal bool ApplyPotionEffect(Character target, Item potion)
    {
        var spell = ResolveDrinkSpell(potion);
        if (spell == 0 || _client.Spells == null)
            return false;

        // The bottle is OnSpellEffect's pSourceItem: ARGO of the drinker's @SpellEffect
        // (Use_Drink, CCharUse.cpp:1071).
        _client.Spells.ApplyDirectEffect(target, target, spell, PotionStrength(target, potion), potion);
        return true;
    }

    /// <summary>m_itPotion.m_dwSkillQuality - MORE2 exactly as stored - raised by the
    /// drinker's ENHANCEPOTIONS percent (CCharUse.cpp:1060-1063). Upstream keeps the
    /// equipment share folded into the char prop; here it is summed on read.</summary>
    internal static int PotionStrength(Character drinker, Item potion)
    {
        int strength = (int)Math.Clamp(potion.More2, 0, int.MaxValue);
        int enhance = CombatEngine.GetEquipmentPropertyValue(drinker, "ENHANCEPOTIONS");
        if (enhance != 0)
            strength += SphereNet.Game.Skills.Information.InfoSkillEngine.IMulDiv(strength, enhance, 100);
        return strength;
    }

    private SpellType ResolveDrinkSpell(Item potion)
    {
        if (potion.More1 is > 0 and < 1000)
            return (SpellType)potion.More1;

        string? name = null;
        if (potion.TryGetTag("MORE1_DEFNAME", out string? m1d) && !string.IsNullOrWhiteSpace(m1d))
            name = m1d;
        else if (potion.TryGetTag("POTION_TYPE", out string? pt))
            name = pt?.ToLowerInvariant() switch
            {
                "heal" => "s_heal",
                "greatheal" => "s_greater_heal",
                "cure" => "s_cure",
                "strength" => "s_strength",
                "agility" => "s_agility",
                // Legacy refresh bottles are the s_Refresh spell (SPELL_Refresh,
                // uofiles_enums.h:890), total refresh at its higher MORE2.
                "refresh" or "totalrefresh" => "s_refresh",
                _ => null,
            };
        if (string.IsNullOrEmpty(name))
            return 0;

        var rid = _triggerDispatcher?.Resources?.ResolveDefName(name);
        if (rid is { IsValid: true, Type: ResType.SpellDef })
            return (SpellType)rid.Value.Index;
        return 0;
    }

    /// <summary>Handle UseSkill request (from packet 0x12 or extended command).</summary>
    public void HandleUseSkill(int skillId)
    {
        if (_character == null || _character.IsDead) return;
        if (skillId < 0 || skillId >= SkillEngine.BaseSkillCount) return;

        var skill = (SkillType)skillId;
        if (!SkillHandlers.IsClientUsable(skill))
        {
            SysMessage("You cannot use that skill directly.");
            return;
        }

        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) ||
            _character.IsCasting)
        {
            SysMessage("You must wait to perform another action.");
            return;
        }
        if (!SkillHandlers.CanUse(_character, skill))
            return;

        int menuSkill = _character.TryGetTag("SKILL_MENU_PENDING", out string? menuSkillText) &&
            ScriptNumber.TryParseInt(menuSkillText, out int parsedMenuSkill) ? parsedMenuSkill : -1;
        int currentSkill = _character.HasActiveSkillPending()
            ? _character.SkillPendingId
            : _pendingCraftRecipe != null ? (int)_pendingCraftSkill
            : Targets.SkillCancelId >= 0 ? Targets.SkillCancelId
            : menuSkill;
        // Skill_Wait runs on every skill request, idle or not: ARGN2 is the active skill
        // or SKILL_NONE (-1) (CClientEvent.cpp:612, CCharSkill.cpp:3984-3986).
        TriggerResult waitResult = TriggerResult.Default;
        {
            var waitArgs = new TriggerArgs { CharSrc = _character, N1 = skillId, N2 = currentSkill };
            waitResult = _triggerDispatcher?.FireCharTrigger(
                _character, CharTrigger.SkillWait, waitArgs) ?? TriggerResult.Default;
            if (waitResult == TriggerResult.True)
                return;
        }

        if (currentSkill >= 0)
        {
            bool cancelCurrent = waitResult == TriggerResult.False ||
                (currentSkill != skillId && (SkillType)currentSkill is
                    SkillType.Meditation or SkillType.Hiding or SkillType.Stealth);
            if (!cancelCurrent)
            {
                SysMessage("You must wait to perform another action.");
                return;
            }

            if (_character.HasActiveSkillPending())
            {
                int aborted = _character.ClearActiveSkillPending();
                if (aborted >= 0)
                    Character.ActiveSkillAborted?.Invoke(_character, aborted);
            }
            CancelPendingCraft(notify: false);
            if (Targets.SkillCancelId >= 0)
            {
                int cancelledTargetSkill = Targets.SkillCancelId;
                _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
                Targets.Clear();
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = cancelledTargetSkill });
            }
            if (menuSkill >= 0)
            {
                _character.RemoveTag("SKILL_MENU_PENDING");
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = _character, N1 = menuSkill });
            }
        }

        if (_character.IsStatFlag(StatFlag.Meditation) && skill != SkillType.Meditation)
            _character.InterruptMeditation();

        if (skill is not (SkillType.Stealth or SkillType.Snooping or SkillType.Stealing))
            _character.ClearHiddenState();

        // Opening a craft menu is selection/UI. The chosen recipe owns the
        // single SkillSelect/PreStart/Start/Stroke/Success chain.
        if (!SkillEngine.HasFlag(skill, SkillFlag.Scripted) && SkillHandlers.IsCraftSkill(skill))
        {
            _character.Act = Serial.Invalid;
            _character.ActPrv = Serial.Invalid;
            _character.ActP = _character.Position;
            _skillHandlers?.UseSkill(_character, skill);
            return;
        }

        var selectResult = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillSelect,
            new TriggerArgs { CharSrc = _character, N1 = skillId }) ?? TriggerResult.Default;
        if (selectResult == TriggerResult.True)
            return;

        // A fresh client action must not inherit object/point state from the
        // previous skill (notably Camping, Poisoning and bard multi-targets).
        _character.Act = Serial.Invalid;
        _character.ActPrv = Serial.Invalid;
        _character.ActP = _character.Position;

        // SKF_SCRIPTED overrides the native implementation even for a built-in
        // skill id. A prompt makes it targeted; otherwise it resolves at once.
        if (SkillEngine.HasFlag(skill, SkillFlag.Scripted))
        {
            BeginActiveSkill(skill, skillId, SkillHandlers.GetActiveSkillTarget(skill));
            return;
        }

        // Source-X parity: information skills prompt for a target before emitting
        // any message. Route through the info-skill pipeline in BeginInfoSkill so
        // the player sees the exact CClientTarg.cpp text sequence.
        if (SkillHandlers.IsInfoSkill(skill))
        {
            BeginInfoSkill(skill, skillId);
            return;
        }

        // Active skills with parity coverage in ActiveSkillEngine.
        var activeKind = SkillHandlers.GetActiveSkillTarget(skill);
        if (activeKind != SkillHandlers.ActiveSkillTargetKind.Unsupported)
        {
            BeginActiveSkill(skill, skillId, activeKind);
            return;
        }

        // Fire @SkillPreStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var preResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (preResult == TriggerResult.True)
                return;
        }

        // Fire @SkillStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (result == TriggerResult.True)
                return;
        }

        // Fire @SkillStroke — the main action moment
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = skillId });

        bool success = _skillHandlers?.UseSkill(_character, skill) ?? false;

        // Fire @SkillSuccess or @SkillFail
        if (_triggerDispatcher != null)
        {
            var trigger = success ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
            _triggerDispatcher.FireCharTrigger(_character, trigger,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
        }

        if (success)
            SysMessage(ServerMessages.GetFormatted("skill_use_ok", skill));
        else
            SysMessage(ServerMessages.GetFormatted("skill_use_fail", skill));
    }

    /// <summary>Handle extended command (0xBF sub-commands).</summary>
    public void HandleExtendedCommand(ushort subCmd, byte[] data)
    {
        if (s_extendedCommandHandlers.TryGetValue(subCmd, out var handler))
            handler(this, data);
    }

    /// <summary>0xBF 0x10 — tooltip request from clients older than 5.0.9 that show
    /// tooltips. Source-X PacketAosTooltipInfo::onReceive (receive.cpp:2949) reads one
    /// serial and answers with addAOSTooltip(object, requested) when the character can
    /// see it - the non-shop branch of the 0xD6 request. SendAosTooltip carries the
    /// same client-version / AOS-feature / can-see gates.</summary>
    private void HandleExtendedOldTooltipRequest(byte[] data)
    {
        if (_character == null || data.Length < 4) return;
        uint serial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        var obj = _world.FindObject(new Serial(serial));
        if (obj != null)
            _client.SendAosTooltip(obj, requested: true);
    }

    /// <summary>0xBF 0x33 — wheel-boat steering (Source-X PacketWheelBoatMove /
    /// SetPilot). Payload: serial(4), moving dir, facing dir, speed
    /// (0 = stop, 1 = one tile, 2+ = continuous). The steering char must be
    /// aboard; an assigned pilot excludes everyone else at the wheel.</summary>
    private void HandleExtendedWheelBoatMove(byte[] data)
    {
        if (_character == null || data.Length < 7) return;
        var engine = Item.ResolveShipEngine?.Invoke();
        if (engine == null) return;

        var ship = engine.FindShipCarrying(_character);
        if (ship == null) return;
        if (ship.Pilot != _character.Uid) return;

        byte dir = (byte)(data[4] & 0x07);
        byte speed = data[6];
        if (speed == 0)
            engine.Stop(ship);
        else
            engine.SetMoveDir(ship, (Direction)dir,
                speed >= 2 ? ShipMovementType.Normal : ShipMovementType.OneTile,
                wheelMove: true);
    }

    private static IReadOnlyDictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>> BuildExtendedCommandHandlers()
    {
        var handlers = new Dictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>>
        {
            [0x0005] = static (client, data) => client.HandleExtendedScreenSize(data),
            [0x0006] = static (client, data) => client.HandleExtendedParty(data),
            [0x0007] = static (client, data) => client.HandleQuestArrowClick(data),
            [0x0009] = static (client, _) => client.HandleWrestleSpecialMove(0x05), // Wrestle Disarm
            [0x000A] = static (client, _) => client.HandleWrestleSpecialMove(0x0B), // Wrestle Stun (Paralyzing Blow)
            [0x000B] = static (client, data) =>
            {
                if (data.Length >= 3)
                    client._netState.ClientLanguage = System.Text.Encoding.ASCII.GetString(data, 0, 3);
                client.FireExtendedButtonTrigger(CharTrigger.UserChatButton, 0x000B);
            },
            [0x0010] = static (client, data) => client.HandleExtendedOldTooltipRequest(data),
            [0x0013] = static (client, data) => client.HandleExtendedContextMenuRequest(data),
            [0x0015] = static (client, data) => client.HandleExtendedContextMenuResponse(data),
            [0x001A] = static (client, data) => client.HandleExtendedStatLock(data),
            [0x001C] = static (client, data) => client.HandleSpellSelect(data),
            [0x001E] = static (client, data) => client.HandleQueryDesignDetails(data),
            [0x0024] = static (client, _) => client.FireExtendedButtonTrigger(CharTrigger.UserKRToolbar, 0x0024),
            [0x002C] = static (client, data) => client.HandleBandageMacro(data),
            [0x002E] = static (client, data) => client.HandleTargetedSkill(data),
            [0x0032] = static (client, data) => client.HandleGargoyleFly(data),
            [0x0033] = static (client, data) => client.HandleExtendedWheelBoatMove(data),
        };

        if (handlers.Keys.Any(subCmd => !ExtendedCommandRegistry.IsKnown(subCmd)) ||
            ExtendedCommandRegistry.KnownSubCommands.Any(subCmd => !handlers.ContainsKey(subCmd)))
            throw new InvalidOperationException("0xBF extended command handlers drifted from ExtendedCommandRegistry.");

        return handlers;
    }

    private void HandleExtendedStatLock(byte[] data)
    {
        if (data.Length < 2 || _character == null)
            return;

        if (data[0] > 2 || data[1] > 2)
            return;

        _character.SetStatLock(data[0], data[1]);
    }

    private long _lastContextMenuRequestMs;

    private void HandleExtendedContextMenuRequest(byte[] data)
    {
        if (data.Length < 4)
            return;

        long now = Environment.TickCount64;
        if (now - _lastContextMenuRequestMs < 500)
            return;
        _lastContextMenuRequestMs = now;

        uint targetSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        SendContextMenu(targetSerial);
    }

    private void HandleExtendedContextMenuResponse(byte[] data)
    {
        if (data.Length < 6)
            return;

        uint respSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        ushort entryTag = (ushort)((data[4] << 8) | data[5]);
        HandleContextMenuResponse(respSerial, entryTag);
    }

    private void HandleExtendedParty(byte[] data)
    {
        if (data.Length >= 1)
            HandlePartyCommand(data);
    }

    private void HandleExtendedScreenSize(byte[] data)
    {
        if (data.Length < 8 || _character == null)
            return;

        ushort width = (ushort)((data[4] << 8) | data[5]);
        ushort height = (ushort)((data[6] << 8) | data[7]);
        _netState.ScreenWidth = width;
        _netState.ScreenHeight = height;
        _character.SetScreenSize(width, height);
    }

    // 0xBF 0x1C — cast a spell selected from the client UI/macro
    // (Source-X PacketSpellSelect, receive.cpp:3087). Payload is a 2-byte skip
    // then the 1-based spell id. The modern client (>= 6.0.1.42) uses this;
    // older clients cast via the 0x12/0x56 text command, handled elsewhere.
    // (This slot previously mis-handled viewport size, which the client actually
    // reports via 0xBF 0x05 / 0xC8 — the duplicate handler was dead.)
    private void HandleSpellSelect(byte[] data)
    {
        if (data.Length < 4 || _character == null)
            return;

        int spellId = (data[2] << 8) | data[3];
        if (spellId > 0)
            _client.HandleCastSpell((SpellType)spellId, 0);
    }

    /// <summary>0xEB KR/EC toolbar use - Source-X CClient::Event_UseToolbar
    /// (CClientEvent.cpp:2937). @UserKRToolbar runs first with ARGN1 = the slot type and
    /// ARGN2 = its argument; RETURN 1 stops the action. Then: 1 casts the spell (KR
    /// clients only reach Spellweaving, so higher ids are ignored), 2 (combat ability)
    /// does nothing, 3 uses the skill, 4 double-clicks the object as a macro would, 5
    /// selects the virtue (@UserVirtue on the player, as Event_VirtueSelect).</summary>
    public void HandleUseToolbar(byte type, uint argument)
    {
        if (_character == null)
            return;

        if (_triggerDispatcher != null &&
            _triggerDispatcher.FireCharTrigger(_character, CharTrigger.UserKRToolbar,
                new TriggerArgs { CharSrc = _character, N1 = type, N2 = argument, N3 = 0 }) == TriggerResult.True)
            return;

        switch (type)
        {
            case 0x01: // spell
                if (argument > 0 && argument <= (uint)SpellType.ArcaneEmpowerment)
                    _client.HandleCastSpell((SpellType)argument, 0);
                break;
            case 0x02: // combat ability: nothing upstream either
                break;
            case 0x03: // skill
                if (argument <= int.MaxValue)
                    HandleUseSkill((int)argument);
                break;
            case 0x04: // item
                UseToolbarObject(argument);
                break;
            case 0x05: // virtue
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserVirtue,
                    new TriggerArgs { CharSrc = _character, O1 = _character, N1 = argument });
                break;
        }
    }

    /// <summary>Event_DoubleClick(uid, fMacro: true, fTestTouch: true): the macro form
    /// never mounts or dismounts, it opens the paperdoll for a character - the same
    /// thing the client's own macro double-click (high bit set) asks for. A uid that
    /// already carries the high bit is a resource uid upstream and finds nothing.</summary>
    private void UseToolbarObject(uint uid)
    {
        if ((uid & 0x80000000) != 0)
            return;
        if (_world.FindChar(new Serial(uid)) != null)
            _client.HandleDoubleClick(uid | 0x80000000);
        else
            _client.HandleDoubleClick(uid);
    }

    /// <summary>0xF4 crash report → @UserBugReport.</summary>
    public void HandleCrashReport() =>
        FireExtendedButtonTrigger(CharTrigger.UserBugReport, 0x00F4);

    /// <summary>Stateless client UI button packets: 0xFA Ultima Store,
    /// 0xB5 chat window open.</summary>
    public void HandleClientUiButton(byte opcode)
    {
        switch (opcode)
        {
            case 0xFA:
                FireExtendedButtonTrigger(CharTrigger.UserUltimaStoreButton, opcode);
                break;
            case 0xB5:
                FireExtendedButtonTrigger(CharTrigger.UserGlobalChatButton, opcode);
                HandleChatOpen();
                break;
        }
    }

    /// <summary>0xBF 0x07 quest-arrow click (receive.cpp:2746-2770): @UserQuestArrowClick
    /// with ARGN1 = 1 for a right click, 0 for a left one; RETURN 1 keeps the "Follow the
    /// Arrow" line off the screen.</summary>
    private void HandleQuestArrowClick(byte[] data)
    {
        if (_character == null)
            return;
        bool rightClick = data.Length > 0 && data[0] != 0;
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserQuestArrowClick,
                new TriggerArgs { CharSrc = _character, N1 = rightClick ? 1 : 0 }) == TriggerResult.True)
            return;
        SysMessage(ServerMessages.Get(Msg.MsgFollowArrow));
    }

    private void FireExtendedButtonTrigger(CharTrigger trigger, ushort subCmd)
    {
        if (_character == null)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, trigger,
            new TriggerArgs { CharSrc = _character, N1 = subCmd });
    }

    /// <summary>0xBF 0x32 — Source-X GargoyleFly (receive.cpp:3329): a living
    /// gargoyle carrying the RACIALF_GARG_FLY racial toggles hovering flight.
    /// Flip STATF_HOVERING and the BI_GARGOYLEFLY buff, then resend the mobile so
    /// the flight state rides the MobileFlags 0x04 bit to the player and every
    /// nearby observer.</summary>
    private void HandleGargoyleFly(byte[] data)
    {
        if (_character == null || _character.IsDead || !_character.IsGargoyle)
            return;
        if ((Character.RacialFlags & (int)RacialFlags.GargoyleFly) == 0)
            return;
        // @ToggleFlying (receive.cpp:3352): RETURN 1 keeps the current state.
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.ToggleFlying,
                new TriggerArgs { CharSrc = _character }) == TriggerResult.True)
            return;

        bool flying = !_character.IsStatFlag(StatFlag.Hovering);
        if (flying)
            _character.SetStatFlag(StatFlag.Hovering);
        else
            _character.ClearStatFlag(StatFlag.Hovering);

        Character.OnClientBuffChanged?.Invoke(_character, BuffIcon.GargoyleFly, flying, 0, null);

        _client.SendSelfRedraw();
        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        var moving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z,
            (byte)((byte)_character.Direction & 0x07), _character.Hue, flags, noto);
        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby(_character.Position, UpdateRange, moving, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, moving, _character.Uid.Value);
    }

    /// <summary>0xBF 0x09/0x0A — pre-AOS wrestling special moves (disarm/stun).
    /// Source-X routes both through Event_CombatAbilitySelect, which fires
    /// @UserSpecialMove with the ability id (0x5 disarm / 0xB paralyzing blow),
    /// the same sink as the AOS special-move packet (0xD7 0x19).</summary>
    private void HandleWrestleSpecialMove(int ability)
    {
        if (_character == null)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserSpecialMove,
            new TriggerArgs { CharSrc = _character, N1 = ability });
    }

    /// <summary>Virtue invocation — Source-X EXTCMD_INVOKE_VIRTUE, which rides the
    /// 0x12 text-command packet as ext-type 0xF4 (CClientEvent.cpp:3127), NOT 0xBF
    /// 0x2C (that is the bandage macro). Fires @UserVirtueInvoke with the virtue id
    /// (1=Honor, 2=Sacrifice, 3=Valor) in N1, matching the reference m_iN1 =
    /// iVirtueID. Distinct from the virtue-gump select (@UserVirtue /
    /// Event_VirtueSelect), which is the 0xB1 dialog path.</summary>
    internal void HandleVirtueInvoke(int virtueId)
    {
        if (_character == null || virtueId <= 0)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserVirtueInvoke,
            new TriggerArgs { CharSrc = _character, N1 = virtueId });
    }

    // 0xBF 0x2E — TargetedSkill (Source-X PacketTargetedSkill, receive.cpp:3280):
    // [word skillId][dword targetUID]. Use the skill against the pre-selected
    // target with no cursor round-trip. skillId 0 ("last skill") is not tracked
    // and is ignored. Runs the same busy/can-use gates as HandleUseSkill.
    private void HandleTargetedSkill(byte[] data)
    {
        if (_character == null || _character.IsDead || data.Length < 6)
            return;

        int skillId = (data[0] << 8) | data[1];
        uint targetSerial = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        if (skillId <= 0 || skillId >= SkillEngine.BaseSkillCount)
            return;

        var skill = (SkillType)skillId;
        if (!SkillHandlers.IsClientUsable(skill))
            return;
        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) || _character.IsCasting)
            return;
        if (!SkillHandlers.CanUse(_character, skill))
            return;

        BeginTargetedSkill(skill, skillId, new Serial(targetSerial));
    }

    // 0xBF 0x2C — BandageMacro (Source-X PacketBandageMacro, receive.cpp:3196):
    // [dword bandageUID][dword targetUID]. The client's bandage hotkey double-
    // clicks the bandage and applies it to the pre-selected target with no cursor
    // round-trip. Mirror the reference gates: the item must be a bandage and the
    // caster must be able to use Healing; the Healing pipeline then validates the
    // target and consumes a bandage from the pack.
    private void HandleBandageMacro(byte[] data)
    {
        if (_character == null || _character.IsDead || data.Length < 8)
            return;

        uint bandageUid = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        uint targetUid = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

        var bandage = _world.FindItem(new Serial(bandageUid));
        if (bandage == null || bandage.ItemType != ItemType.Bandage)
            return;
        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) || _character.IsCasting)
            return;
        if (!SkillHandlers.CanUse(_character, SkillType.Healing))
            return;

        BeginTargetedSkill(SkillType.Healing, (int)SkillType.Healing, new Serial(targetUid));
    }

    /// <summary>
    /// Handle party sub-commands (0xBF sub 0x0006).
    /// Sub-types: 1=Add, 2=Remove, 3=PrivateMsg, 4=PublicMsg, 6=SetLoot, 8=Accept, 9=Decline.
    /// </summary>
    /// <summary>CPartyDef::MessageEvent's SPEECHFILTER step (CParty.cpp:252): the
    /// party's filter function runs with ARGN1 = sender uid, ARGN2 = recipient uid
    /// (0 for the whole party) and ARGS = the text, under the server as SRC; RETURN 1
    /// drops the message. True = dropped.</summary>
    internal bool PartySpeechFiltered(PartyDef party, uint dstUid, string text)
    {
        var runner = _triggerDispatcher?.Runner;
        if (_character == null || string.IsNullOrEmpty(party.SpeechFilter) || runner == null)
            return false;
        var args = new ExecTriggerArgs
        {
            Number1 = _character.Uid.Value,
            Number2 = dstUid,
            ArgString = text,
        };
        return runner.TryRunFunction(party.SpeechFilter, _character, null, args, out var result) &&
               result == TriggerResult.True;
    }

    private void HandlePartyCommand(byte[] data)
    {
        if (_character == null || _partyManager == null) return;
        byte partyCmd = data[0];

        switch (partyCmd)
        {
            case 1: // Add member (invite)
                if (data.Length >= 5)
                {
                    // CClient::OnTarg_Party_Add (CClientTarg.cpp:2398): the record of the
                    // invitation is kept on the inviter (PARTY_LASTINVITE), so a second
                    // invitation replaces the first and the answer is checked against it.
                    uint targetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    _partyManager.Invite(_character, _world.FindChar(new Serial(targetUid)), PartyIoForClient());
                }
                break;

            case 2: // Remove member
                if (data.Length >= 5)
                {
                    // receive.cpp:2673: RemoveMember(serial, me) with fDisband left at its
                    // default, so the leader removing themselves disbands the party.
                    uint removeUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party == null) break;
                    _partyManager.RemoveMember(party, new Serial(removeUid), _character.Uid, PartyIoForClient());
                }
                break;

            case 3: // Private party message
                if (data.Length >= 5)
                {
                    uint pmTargetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    string pmMsg = data.Length > 5
                        ? System.Text.Encoding.BigEndianUnicode.GetString(data, 5, data.Length - 5).TrimEnd('\0')
                        : "";
                    if (!string.IsNullOrEmpty(pmMsg))
                    {
                        var party = _partyManager.FindParty(_character.Uid);
                        var targetUid = new Serial(pmTargetUid);
                        if (party == null || !party.IsMember(targetUid))
                        {
                            SysMessage(ServerMessages.Get("party_join_failed"));
                            break;
                        }
                        if (PartySpeechFiltered(party, pmTargetUid, pmMsg))
                            break;
                        SendToChar?.Invoke(targetUid,
                            new PacketPartyMessage(_character.Uid.Value, pmMsg, isPrivate: true));
                        SysMessage(ServerMessages.GetFormatted("party_msg", $"{pmTargetUid:X}", pmMsg));
                    }
                }
                break;

            case 4: // Public party message
                if (data.Length >= 2)
                {
                    string msg = System.Text.Encoding.BigEndianUnicode.GetString(data, 1, data.Length - 1).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(msg)) break;
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null && !PartySpeechFiltered(party, 0, msg))
                    {
                        var chatPacket = new PacketPartyMessage(_character.Uid.Value, msg);
                        foreach (var memberUid in party.Members)
                            SendToChar?.Invoke(memberUid, chatPacket);
                    }
                }
                break;

            case 6: // Set loot flag
                if (data.Length >= 2)
                {
                    bool canLoot = data[1] != 0;
                    var party = _partyManager.FindParty(_character.Uid);
                    party?.SetLootFlag(_character.Uid, canLoot);
                    SysMessage(canLoot ? "Party loot sharing enabled." : "Party loot sharing disabled.");
                }
                break;

            case 8: // Accept invite
            {
                // The client names the inviter whose invitation it answers
                // (receive.cpp:2708 -> AcceptEvent, CParty.cpp:443).
                uint answeredUid = data.Length >= 5
                    ? (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4])
                    : 0;
                _partyManager.AcceptEvent(_character, new Serial(answeredUid), forced: false, PartyIoForClient());
                break;
            }

            case 9: // Decline invite
            {
                uint declinedUid = data.Length >= 5
                    ? (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4])
                    : 0;
                _partyManager.DeclineEvent(_character, new Serial(declinedUid), PartyIoForClient());
                break;
            }
        }
    }

    /// <summary>The party operations' view of this client: its triggers, its packet
    /// delivery and its own system messages.</summary>
    private PartyIo PartyIoForClient() =>
        PartyIo.ForClient(_world, _triggerDispatcher, SendToChar, _character, SysMessage);

    internal void SendContextMenu(uint targetSerial)
    {
        if (_character == null) return;

        var ch = _world.FindChar(new Serial(targetSerial));
        var item = ch == null ? _world.FindItem(new Serial(targetSerial)) : null;
        if (ch != null && (ch.MapIndex != _character.MapIndex ||
            _character.Position.GetDistanceTo(ch.Position) > 12))
            return;
        if (item != null && !item.ContainedIn.IsValid && (item.Position.Map != _character.MapIndex ||
            _character.Position.GetDistanceTo(item.Position) > 3))
            return;

        var entries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();
        var scriptEntries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();
        _client.ScriptContextEntries = scriptEntries;

        if (ch != null)
        {
            // RETURN 1 means the script built the menu itself: upstream adds the
            // hardcoded character entries only when the trigger did NOT return TRUE
            // (CClientEvent.cpp:2596-2608). Adding them regardless gave a script that
            // claimed the menu the engine's entries on top of its own.
            //
            // Nothing in the shipped packs returns TRUE here today - all 26 blocks
            // hang off items - so this changes no pack behaviour; it makes the return
            // value mean what a script writing one would expect.
            //
            // ARGN1 is 1 on this first call. A script that changes it asks for a second
            // call, with ARGN1 = 2 and the same args, once the hardcoded entries are in
            // (CClientEvent.cpp:2593-2595 and :2724-2728).
            var requestArgs = new TriggerArgs { CharSrc = _character, N1 = 1 };
            bool scriptOwnsMenu = _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.ContextMenuRequest,
                requestArgs) == TriggerResult.True;
            if (!scriptOwnsMenu)
            {
                entries.Add((1, 3006123, 0)); // Open Paperdoll
                if (ch == _character)
                {
                    entries.Add((2, 3006145, 0)); // Open Backpack
                }
                if (VendorEngine.IsVendorLike(ch))
                {
                    entries.Add((3, 3006103, 0)); // Buy
                    entries.Add((4, 3006106, 0)); // Sell
                }
                if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Banker)
                {
                    entries.Add((5, 3006105, 0)); // Open Bankbox
                }
                // Mount / Dismount: exposed as a context-menu action so the client
                // does not require a DoubleClick to saddle. Double-click remains
                // equivalent. Entry is filtered by IsMountable so non-ridable
                // mobs (monsters, humans) don't get a useless "Mount Me" line.
                if (!ch.IsPlayer && ch != _character &&
                    Mounts.MountEngine.IsMountable(ch.BodyId))
                {
                    entries.Add((6, 3006155, 0)); // Mount Me
                }
                if (ch == _character && _character.IsMounted)
                {
                    entries.Add((7, 3006112, 0)); // Dismount
                }

                if (requestArgs.N1 != 1 && _triggerDispatcher != null)
                {
                    requestArgs.N1 = 2;
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.ContextMenuRequest, requestArgs);
                }
            }
        }
        else if (item != null)
        {
            // ARGN1 = 1 for an item's request as well (CClientEvent.cpp:2578).
            FireContextMenuTrigger(item, ItemTrigger.ContextMenuRequest, 1);
        }

        entries.AddRange(scriptEntries);
        _client.ScriptContextEntries = null;

        if (entries.Count > 0)
            _netState.Send(new PacketContextMenu(targetSerial, entries.ToArray(),
                _netState.SupportsNewContextMenu));
    }

    internal void HandleContextMenuResponse(uint targetSerial, ushort entryTag)
    {
        if (_character == null) return;

        var charTarget = _world.FindChar(new Serial(targetSerial));
        if (charTarget != null && (charTarget.MapIndex != _character.MapIndex ||
            _character.Position.GetDistanceTo(charTarget.Position) > 12))
            return;
        var itemObj = charTarget == null ? _world.FindItem(new Serial(targetSerial)) : null;
        if (itemObj != null && !itemObj.ContainedIn.IsValid && (itemObj.Position.Map != _character.MapIndex ||
            _character.Position.GetDistanceTo(itemObj.Position) > 3))
            return;

        var target = charTarget;
        if (target == null)
        {
            // An item's menu is the script's alone: upstream fires the trigger and
            // returns, "there's no hardcoded stuff for items" (CClientEvent.cpp:2768).
            // Running the character switch afterwards meant an entry tag that happened
            // to match one of the engine's own - 6 is Mount Me, which double-clicks the
            // target - did that to the ITEM as well. Nothing in the packs collides
            // today (their tags all start at 100, which is where upstream reserves
            // them from), but the engine should not be waiting for one that does.
            if (_world.FindItem(new Serial(targetSerial)) is { } itemTarget)
                FireContextMenuTrigger(itemTarget, ItemTrigger.ContextMenuSelect, entryTag);
            return;
        }

        // RETURN 1 means the script handled the entry, so the engine's own action for
        // that tag is skipped (CClientEvent.cpp:2778). The return value used to be
        // discarded and both ran.
        if (FireContextMenuTrigger(target, CharTrigger.ContextMenuSelect, entryTag)
            == TriggerResult.True)
            return;

        switch (entryTag)
        {
            case 1: // Open Paperdoll
                if (target != null) SendPaperdoll(target);
                break;
            case 2: // Open Backpack
                if (_character.Backpack != null)
                    SendOpenContainer(_character.Backpack);
                break;
            case 3: // Buy
                var vendor = _world.FindChar(new Serial(targetSerial));
                if (vendor != null) HandleVendorInteraction(vendor);
                break;
            case 4: // Sell
                if (_world.FindChar(new Serial(targetSerial)) is { } sellVendor &&
                    VendorEngine.IsVendorLike(sellVendor))
                {
                    _client.OpenVendorSell(sellVendor);
                }
                break;
            case 5: // Open Bankbox
            {
                var banker = _world.FindChar(new Serial(targetSerial));
                if (banker != null && banker.NpcBrain == NpcBrainType.Banker &&
                    _character.Position.GetDistanceTo(banker.Position) <= 3 &&
                    _character.MapIndex == banker.MapIndex)
                {
                    OpenBankBox();
                }
                break;
            }
            case 6: // Mount Me
                HandleDoubleClick(targetSerial);
                break;
            case 7: // Dismount
            {
                if (_character.IsMounted && _mountEngine != null)
                {
                    uint oldMountItemUid = _character.GetEquippedItem(Core.Enums.Layer.Horse)?.Uid.Value ?? 0;
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                        new PacketSound(0x0140, _character.X, _character.Y, _character.Z), 0);
                    var npc = DismountCharacter();

                    // Re-seat via the shared standing resolver (audit design;
                    // multi decks / house floors included).
                    var wfStand = _world.Standing.ResolveStandingSurface(_character,
                        _character.MapIndex, _character.X, _character.Y, _character.Z,
                        SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
                    if (wfStand.Found && wfStand.Z != _character.Z)
                        _character.Position = new Point3D(_character.X, _character.Y, wfStand.Z, _character.MapIndex);

                    if (oldMountItemUid != 0)
                        BroadcastDeleteObject(oldMountItemUid);

                    ResetWalkValidator();
                    _netState.WalkSequence = 0;
                    _netState.SendPriority(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    byte flags = BuildMobileFlags(_character);
                    byte dir77 = (byte)((byte)_character.Direction & 0x07);
                    byte noto = GetNotoriety(_character);
                    var movePacket = new PacketMobileMoving(
                        _character.Uid.Value, _character.BodyId,
                        _character.X, _character.Y, _character.Z, dir77,
                        _character.Hue, flags, noto);
                    _netState.Send(movePacket);
                    if (BroadcastMoveNearby != null)
                        BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
                    else
                        BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

                    if (npc != null)
                    {
                        npc.ClearStatFlag(Core.Enums.StatFlag.Ridden);
                        BroadcastCharacterAppear?.Invoke(npc);
                    }
                }
                break;
            }
        }
    }

    private TriggerResult FireContextMenuTrigger(Character target, CharTrigger trigger, ushort entryTag)
    {
        return _triggerDispatcher?.FireCharTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = _character,
            N1 = entryTag
        }) ?? TriggerResult.Default;
    }

    private void FireContextMenuTrigger(Item target, ItemTrigger trigger, ushort entryTag)
    {
        _triggerDispatcher?.FireItemTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = target,
            N1 = entryTag
        });
    }

    // ==================== Single Click ====================
}
