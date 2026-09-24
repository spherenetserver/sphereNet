using System.Reflection;
using Xunit.Sdk;

[assembly: SphereNet.Tests.ResetEngineStatics]

namespace SphereNet.Tests;

// The engine exposes process-wide mutable static hooks that tests wire in their
// setup (CreateWorld / AttachCharacter):
//   ObjBase.ResolveWorld, Item.ResolveWorld   — resolve the ambient world
//   Character.ResolveAccountForChar           — drives Character.PrivLevel
//   VendorEngine.World                         — vendor trade world
// If one test leaves a hook set, a later test (whose object UIDs can collide
// across its own fresh world) resolves against the stale hook — e.g. a player
// reading another test's GM account and gaining GM privileges, or a move
// resolving against the wrong world. xUnit does not guarantee test order, so
// this surfaced as intermittent, order-dependent CI failures.
//
// Reset every hook to null before AND after each test so each test starts from
// a clean slate and re-establishes only what its own setup wires. Combined with
// assembly-wide serialization (see TestParallelization.cs) this makes the suite
// deterministic regardless of execution order.
public sealed class ResetEngineStaticsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset();
    public override void After(MethodInfo methodUnderTest) => Reset();

    private static void Reset()
    {
        SphereNet.Game.Objects.ObjBase.ResolveWorld = null;
        SphereNet.Game.Objects.ObjBase.ResolveClientConsole = null;
        SphereNet.Game.Objects.Characters.Character.OnTeleportEffect = null;
        SphereNet.Game.Objects.Characters.Character.WakeNpc = null;
        SphereNet.Game.Scripting.ScriptTouchAccess.Configuration = new();
        SphereNet.Game.Objects.ObjBase.OnObjectMessage = null;
        SphereNet.Game.Definitions.DefinitionLoader.ResetForTests();
        SphereNet.Game.Objects.ObjBase.BroadcastNearby = null;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = null;
        SphereNet.Game.Objects.Items.Item.CreateTriggerHook = null;
        SphereNet.Game.Objects.Items.Item.OnVisualUpdate = null;
        // The defname -> graphic resolver. Several tests wire it and one of them
        // cleared it afterwards, which left whichever test ran next resolving nothing;
        // it belongs here for the same reason every other hook does.
        SphereNet.Game.Objects.Items.Item.ResolveDefName = null;
        // The decay-registration report and its once-per-process latch: a test that
        // deliberately loses a registration must not silence the next one.
        SphereNet.Game.Objects.Items.Item.OnDecayRegistrationLost = null;
        SphereNet.Game.Objects.Items.Item.ResetDecayRegistrationWarning();
        // Config-fed engine switches must not leak from one test into the next.
        SphereNet.Game.Magic.SpellEngine.NpcCanFizzleOnHit = false;
        SphereNet.Game.Objects.Items.Item.FlipDroppedItems = true;
        SphereNet.Game.Objects.Items.Item.BackpackOverload = 40;
        SphereNet.Scripting.Execution.ScriptScope.DefaultMaxLoopIterations = 100000;
        SphereNet.Game.Diagnostics.TickFaults.Reset();
        SphereNet.Game.Objects.Characters.Character.OnPetRelease = null;
        SphereNet.Game.Trade.VirtualGold.Enabled = false;
        SphereNet.Game.Clients.GameClient.ConfigureLoginTries(0, TimeSpan.Zero);
        SphereNet.Game.Objects.Items.Item.DragWeightMax = 300;
        SphereNet.Game.Movement.MovementEngine.StaminaLossAtWeight = 150;
        SphereNet.Game.Movement.MovementEngine.StaminaLossOverweight = 5;
        SphereNet.Game.Movement.MovementEngine.RunningPenalty = 50;
        SphereNet.Game.Movement.MovementEngine.MaxShipPlankTeleport = 18;
        SphereNet.Game.Movement.MovementEngine.RunningPenaltyOverweight = 100;
        SphereNet.Game.Movement.MovementEngine.ResetWeightLossRoll();
        SphereNet.Scripting.Definitions.CharDef.DefaultMoveRate = 100;
        SphereNet.Game.Clients.ClientTargetingHandler.SpellTimeoutSeconds = 0;
        SphereNet.Game.Objects.Items.Item.MagicUnlockDoor = 900;
        SphereNet.Game.Movement.MovementEngine.MeditationMovementAbort = false;
        SphereNet.Game.Death.DeathEngine.NoResRobe = false;
        SphereNet.Game.Objects.Characters.Character.CanUndressPets = true;
        SphereNet.Game.Objects.Characters.Character.CanPetsDrinkPotion = true;
        SphereNet.Game.Trade.VendorEngine.DefaultVendorMarkup = 15;
        SphereNet.Game.Trade.VendorEngine.VendorMaxSell = 255;
        SphereNet.Game.AI.NpcAI.LostNpcTeleport = 50;
        SphereNet.Game.Movement.MovementEngine.NpcShoveNpc = false;
        SphereNet.Game.Objects.Characters.Character.ActiveRevealFlags =
            SphereNet.Core.Enums.RevealFlags.DetectingHidden | SphereNet.Core.Enums.RevealFlags.LootingSelf |
            SphereNet.Core.Enums.RevealFlags.LootingOthers | SphereNet.Core.Enums.RevealFlags.Speak |
            SphereNet.Core.Enums.RevealFlags.SpellCast | SphereNet.Core.Enums.RevealFlags.Snooping |
            SphereNet.Core.Enums.RevealFlags.Stealing | SphereNet.Core.Enums.RevealFlags.StealingFail;
        SphereNet.Game.Objects.Characters.Character.HitsHungerLoss = 0;
        SphereNet.Game.Objects.Characters.Character.OverSkillMultiply = 2;
        SphereNet.Game.Clients.ClientItemUseHandler.SkillPracticeMax = 300;
        SphereNet.Game.Objects.Items.Item.WoolGrowthMs = 30 * 60 * 1000;
        SphereNet.Game.Objects.Items.Item.MaxItemComplexity = 25;
        // The reference's own default: a bare server has no weather.
        SphereNet.Game.World.WeatherEngine.NoWeather = true;
        SphereNet.Persistence.Load.WorldLoader.NpcSkillSave = 10;
        SphereNet.Game.Magic.SpellEngine.MaxPolyStats = 150;
        // A recipe's FUNC= row reaches the script layer through this hook; a test
        // that installs one must not leave it standing for the next.
        SphereNet.Game.Definitions.TemplateEngine.FunctionRowHook = null;
        SphereNet.Game.Definitions.TemplateEngine.CreateHeaderRollForTests = null;
        SphereNet.Game.Components.ChampionComponent.ResolveGameClockMs = null;
        SphereNet.Game.Components.SpawnComponent.ReleaseFromPreviousSpawner = null;
        SphereNet.Game.Objects.Characters.Character.ReleaseFromSpawner = null;
        SphereNet.Game.Objects.Items.Item.OnItemUnequipped = null;
        SphereNet.Game.Objects.Items.Item.ItemsMaxAmount = 60000;
        SphereNet.Game.Objects.Characters.Character.BroadcastNearby = null;
        SphereNet.Game.Objects.Characters.Character.ResolveHouseDesignMulti = null;
        SphereNet.Game.Objects.Characters.Character.NpcHireQuote = null;
        SphereNet.Game.Objects.Characters.Character.NpcTrainOffer = null;
        SphereNet.Game.Objects.Characters.Character.NpcShrink = null;
        SphereNet.Game.Objects.Characters.Character.NpcStablePetSelect = null;
        SphereNet.Game.Objects.Characters.Character.NpcStablePetRetrieve = null;
        // The spell-memory bridges: a test that wires one leaves every later test
        // reading its TIMER through that engine instance.
        SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRemover = null;
        SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRemaining = null;
        SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRetimer = null;
        SphereNet.Game.Objects.Characters.Character.FieldTouchHook = null;
        SphereNet.Game.Objects.Characters.Character.MagicFlags = 0;
        SphereNet.Game.Objects.Characters.Character.EmoteFlags = 0;
        SphereNet.Game.Objects.Characters.Character.ResolveSpellDef = null;
        SphereNet.Game.Objects.Characters.Character.BreakParalyzeHook = null;
        SphereNet.Game.Clients.GameClient.ColorInvisHue = 0;
        SphereNet.Game.Clients.GameClient.ColorHiddenHue = 0;
        SphereNet.Game.Clients.GameClient.ColorInvisSpellHue = 0;
        SphereNet.Game.Objects.Characters.Character.CombatSpeedEra = 0;
        SphereNet.Game.Objects.Characters.Character.CombatSpeedScaleFactor = 15_000;
        SphereNet.Game.Objects.Characters.Character.CombatParryingEra =
            (int)(SphereNet.Core.Enums.ParryEraFlags.PreSeFormula |
                  SphereNet.Core.Enums.ParryEraFlags.ShieldBlock);
        SphereNet.Game.Objects.Characters.Character.FeatureSE = 0;
        // The rest of the combat/magic knobs a test may set. They were missing,
        // so a value left behind could steer a later test's swing or cast and
        // surface as a failure once every dozen runs.
        SphereNet.Game.Objects.Characters.Character.CombatFlags = 0;
        SphereNet.Game.Combat.CharacterSounds.GenericSoundsEnabled = true;
        SphereNet.Game.Objects.Characters.Character.CombatDamageEra = 0;
        SphereNet.Game.Objects.Characters.Character.CombatHitChanceEra = 0;
        SphereNet.Game.Objects.Characters.Character.EquippedCastEnabled = false;
        SphereNet.Game.Objects.Characters.Character.ReagentsRequiredEnabled = true;
        SphereNet.Game.Combat.CombatEngine.WeaponDefLookup = null;
        SphereNet.Game.Combat.CombatEngine.DurabilityEnabled = false;
        SphereNet.Game.Combat.CombatEngine.DurabilityLossChance = 25;
        SphereNet.Game.Combat.CombatEngine.DurabilityLossMin = 1;
        SphereNet.Game.Combat.CombatEngine.DurabilityLossMax = 1;
        SphereNet.Game.Objects.Characters.Character.FeatureAOS = 0;
        SphereNet.Game.Objects.Characters.Character.RacialFlags = 0;
        SphereNet.Game.Objects.Characters.Character.OnFacingChanged = null;
        SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = null;
        SphereNet.Game.Objects.Characters.Character.ResolveClientVersionText = null;
        SphereNet.Game.Definitions.CharDefHelper.AfterApplyDefName = null;
        SphereNet.Game.Objects.Characters.Character.ResolvePartyManager = null;
        SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRemover = null;
        SphereNet.Game.Objects.Characters.Character.NpcWantThisItem = null;
        SphereNet.Game.Objects.Characters.Character.NpcCanEatFood = null;
        SphereNet.Game.Trade.VendorEngine.World = null;
        SphereNet.Game.Combat.CombatEngine.OnHitDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnReactiveArmorTrigger = null;
        SphereNet.Game.Combat.CombatEngine.OnReactiveArmorFeedback = null;
        SphereNet.Game.Combat.CombatEngine.OnDirectDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnDirectCharacterDamageApplied = null;
        SphereNet.Game.Combat.CombatEngine.OnItemDamaged = null;
        SphereNet.Game.Combat.CombatEngine.OnItemBroken = null;
        SphereNet.Game.Combat.CombatEngine.BreakOnZeroHits = true;
        SphereNet.Game.Combat.CombatEngine.DefaultHits = 50;
        SphereNet.Game.Combat.CombatEngine.OnHitParry = null;
        SphereNet.Game.Combat.CombatEngine.OnParrySucceeded = null;
        SphereNet.Game.Objects.Items.Item.RedeedHouse = null;
        SphereNet.Game.Objects.Items.Item.RedeedShip = null;
        SphereNet.Game.Speech.CommandHandler.ServerCommandBridge = null;
        SphereNet.Game.Objects.Characters.Character.SpawnNpcFromScript = null;
        SphereNet.Game.Combat.CombatEngine.OnLeechEffect = null;
        SphereNet.Game.Combat.CombatEngine.OnHitAreaDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnHitSpell = null;
        SphereNet.Scripting.Definitions.CharDef.DefNameResolver = null;
        SphereNet.Game.Objects.Characters.Character.OnFameChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnKarmaChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnExpChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnExpLevelChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderMark = null;
        SphereNet.Game.Objects.Characters.Character.OnCriminalCheck = null;
        SphereNet.Game.Objects.Characters.CrimeWitnessService.OnCrimeNoticed = null;
        SphereNet.Game.Objects.Characters.CrimeWitnessService.SnoopCriminalChance = 100;
        SphereNet.Game.Objects.Characters.Character.OnCombatAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnAttackTrigger = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptAttackerAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatDelete = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatEnd = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderDecay = null;
        SphereNet.Game.Objects.Characters.Character.OnNotoSend = null;
        SphereNet.Game.Objects.Characters.Character.ResolveNotoFlag = null;
        SphereNet.Game.Objects.Characters.Character.OnHealthBarStatusChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptSpellEffect = null;
        SphereNet.Game.Objects.Characters.Character.OnPersonalSpace = null;
        SphereNet.Game.Objects.Characters.Character.OnCharShove = null;
        SphereNet.Game.Objects.Characters.Character.OnAfkMode = null;
        SphereNet.Game.Objects.Characters.Character.OnSeeHidden = null;
        SphereNet.Game.Objects.Characters.Character.OnFollowersUpdate = null;
        SphereNet.Game.Trade.VendorEngine.OnPayGold = null;
        SphereNet.Game.Housing.HousingEngine.OnDelMulti = null;
        SphereNet.Game.Housing.CustomHousingEngine.KeepCommitItem = null;
        SphereNet.Game.Housing.CustomHousingEngine.BroadcastRemove = null;
        SphereNet.Game.Housing.HouseDesignValidItems.ClearValidItems();
        SphereNet.Game.Objects.Characters.Character.OnEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnRevealing = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectRemove = null;
        SphereNet.Game.Objects.Characters.Character.OnClientBuffChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnOwnViewRefreshNeeded = null;
        SphereNet.Game.Objects.Characters.Character.DeadCannotSeeLiving = 0;
        SphereNet.Game.Objects.Characters.Character.OnHiddenStateCleared = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectTick = null;
        SphereNet.Game.Objects.Characters.CharacterPoisonState.OnSpellEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnPetDesert = null;
        SphereNet.Game.Objects.Characters.Character.OnJailed = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptDismount = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptMount = null;
        SphereNet.Game.Objects.Characters.Character.ScriptEquipItem = null;
        SphereNet.Game.Objects.Characters.Character.OnDragRelease = null;
        SphereNet.Game.Objects.Characters.Character.OnDragCancel = null;
        SphereNet.Game.Objects.Items.Item.OnTradeWindowChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnHitIgnored = null;
        SphereNet.Game.Objects.Characters.Character.OnNpcLostTeleport = null;
        SphereNet.Game.Objects.Items.Item.OnTimerExpired = null;
        SphereNet.Game.Objects.Items.Item.ResolveHouse = null;
        SphereNet.Game.Objects.Items.Item.ResolveShipEngine = null;
        SphereNet.Game.Objects.Items.Item.ResolveMultiDefId = null;
        SphereNet.Game.Objects.Items.Item.ResolveGuild = null;
        SphereNet.Game.Objects.Items.Item.ResolveGuildManager = null;
        SphereNet.Game.Objects.Characters.Character.ResolveGuildManager = null;
        SphereNet.Game.Clients.PaperdollText.NpcNoFameTitle = false;
        SphereNet.Game.Objects.Items.Item.ResolveGuildCharacter = null;
        SphereNet.Game.Objects.Characters.Character.OnCanCastCheck = null;
        SphereNet.Game.Objects.Characters.Character.OnCanMakeCheck = null;
        SphereNet.Game.Objects.Characters.Character.OnResourcePossessionCheck = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuickProperty = null;
        SphereNet.Game.Objects.Items.Item.OnSpawnStartStop = null;
        SphereNet.Game.Objects.Items.Item.OnScriptOpen = null;
        SphereNet.Game.Objects.Items.Item.OnScriptDClick = null;
        SphereNet.Game.Scripting.ScriptFileHandle.Diagnostic = null;
        SphereNet.Game.Objects.ObjBase.OnScriptTrigger = null;
        SphereNet.Game.Objects.ObjBase.OnScriptSingleClick = null;
        SphereNet.Game.World.Regions.Region.OnAllClients = null;
        SphereNet.Game.World.Regions.Room.OnAllClients = null;
        SphereNet.Game.Movement.WalkCheck.ResolveCustomDesign = null;
        // Load-profile counters are process-wide and are bumped by any test that
        // moves a character or fires a char trigger; zero them per test so a
        // profile assertion measures its own test and not the ones before it.
        SphereNet.Game.Diagnostics.LoadProfile.Reset();
        // Per-talkmode default hue/font: a pack sets these by DEFNAME at load, so a
        // test that loads one must not tint the next test's messages.
        SphereNet.Game.Messages.ServerMessages.ResetTalkDefaults();
        SphereNet.Game.Objects.Characters.Character.OnMemoryEquip = null;
        SphereNet.Game.Objects.Characters.Character.OnEnvironChange = null;
        SphereNet.Game.Objects.Characters.Character.OnRegenStat = null;
        SphereNet.Game.Objects.Characters.Character.OnArrowQuest = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuick = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuickDetailed = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptSkillUse = null;
        SphereNet.Game.Objects.Characters.Character.OnDamageActionInterrupt = null;
        SphereNet.Game.Objects.Characters.Character.ActiveSkillAborted = null;
        SphereNet.Game.Objects.Characters.Character.OnNpcSeeNewPlayer = null;
        SphereNet.Game.Objects.Characters.Character.MountedNpcDeletedHook = null;
        SphereNet.Game.Objects.Items.Item.FigurineDeletedHook = null;
        SphereNet.Game.Housing.House.OnRedeed = null;
        SphereNet.Game.Housing.HousingEngine.OnHouseCheck = null;
        SphereNet.Game.AI.NpcAI.PetFollowMaxDistance = 36;
        SphereNet.Game.Objects.Characters.Character.SpellbookRequiredEnabled = true;
        SphereNet.Game.Objects.Characters.Character.PacketDeathAnimationEnabled = false;
        SphereNet.Game.Clients.GameClient.ClientLingerSeconds = 60;
        SphereNet.Game.Clients.GameClient.ServerFeatureT2A = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureLBR = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureAOS = 0x0F;
        SphereNet.Game.Clients.GameClient.ServerFeatureSE = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureML = 0x01;
        SphereNet.Game.Clients.GameClient.ServerFeatureKR = 0;
        SphereNet.Game.Clients.GameClient.ServerFeatureSA = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureTOL = 0x01;
        SphereNet.Game.Clients.GameClient.ServerFeatureExtra = 0;
        SphereNet.Game.Clients.GameClient.ServerMaxCharsPerAccount = 7;
        SphereNet.Game.Clients.GameClient.ServerAutoResDisp = true;
        SphereNet.Game.Clients.GameClient.ServerToolTipMode = 1;
        SphereNet.Game.Clients.GameClient.ServerChatFlags = 0;
        SphereNet.Game.Clients.GameClient.ServerOptionFlags =
            SphereNet.Core.Enums.OptionFlags.FileCommands | SphereNet.Core.Enums.OptionFlags.Buffs;
        SphereNet.Network.Packets.Outgoing.PacketCharList.AosTooltipsEnabled = true;
        SphereNet.Game.Components.SpawnComponent.OnSpawnTrigger = null;
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
        SphereNet.Game.Death.DeathEngine.EnableDeathShroud = true;
        SphereNet.Game.Skills.SkillEngine.OnSkillGainCheck = null;
        SphereNet.Game.Skills.SkillEngine.OnSkillDecrease = null;
        SphereNet.Game.Skills.SkillEngine.OnStatDecrease = null;
        SphereNet.Game.Skills.SkillHandlers.OnCraftSkillUsed = null;
        SphereNet.Game.Skills.SkillHandlers.OnScriptedSkillUse = null;
        SphereNet.Game.Skills.SkillEngine.SkillMaxOverrides.Clear();
        SphereNet.Game.Skills.SkillEngine.SkillSumMaxOverride = 7000;
        SphereNet.Game.Objects.Characters.Character.OnSkillChange = null;
        SphereNet.Game.Skills.SkillEngine.StatAdvCurves =
            [SphereNet.Scripting.Definitions.ValueCurve.Empty,
             SphereNet.Scripting.Definitions.ValueCurve.Empty,
             SphereNet.Scripting.Definitions.ValueCurve.Empty];
        SphereNet.Scripting.Expressions.ExpressionParser.ObsceneChecker = null;
        SphereNet.Core.Configuration.AccountNameValidator.ObsceneChecker = null;
        SphereNet.Game.Magic.SpellDef.RuneWordResolver = null;
        SphereNet.Game.Objects.ObjBase.DiagnosticLog = null;
        SphereNet.Game.Objects.Characters.Character.OnHearRouted = null;
        SphereNet.Game.Objects.Characters.Character.FindCharByClientIndex = null;
        SphereNet.Game.Objects.Characters.Character.FindCharBySocketId = null;
        SphereNet.Game.Housing.HouseDesignValidItems.ClearValidItems();
        SphereNet.Game.World.Sectors.Sector.SleepDelayMs = 10L * 60 * 1000;
        SphereNet.Game.Objects.Characters.Character.RegenHitsSeconds = 40;
        SphereNet.Game.Objects.Characters.Character.RegenManaSeconds = 20;
        SphereNet.Game.Objects.Characters.Character.RegenStamSeconds = 10;
        SphereNet.Game.Objects.Characters.Character.RegenFoodSeconds = 3600;
    }
}
