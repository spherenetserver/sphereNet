using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
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
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// The narrow GameClient surface the extracted handler classes depend on
/// (decomposition phase 4 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Derived from the union of the handlers' context-shim blocks; GameClient
/// is the only implementation. Handlers hold this interface instead of the
/// concrete GameClient, so the compiler enforces that new handler code can
/// only reach the surface listed here. Extends <see cref="ITextConsole"/>
/// because handlers pass their context onward as the script console
/// identity (TriggerArgs.ScriptConsole, TryRunFunction, TryExecuteCommand).
/// </summary>
internal interface IClientContext : ITextConsole
{
    // --- core state ---
    Account? Account { get; }
    Character? Character { get; }
    GameWorld World { get; }
    NetState NetState { get; }
    bool IsPlaying { get; }
    ILogger Log { get; }

    // --- engines ---
    TriggerDispatcher? Triggers { get; }
    HousingEngine? Housing { get; }
    TradeManager? TradeM { get; }
    SpellEngine? Spells { get; }
    SkillHandlers? SkillH { get; }
    Mounts.MountEngine? MountE { get; }
    CraftingEngine? CraftE { get; }
    GuildManager? GuildM { get; }
    PartyManager? PartyM { get; }
    CommandHandler? Cmds { get; }
    MovementEngine? MoveEng { get; }
    SpeechEngine? SpeechEng { get; }
    DeathEngine? DeathEng { get; }

    // --- decomposition components / sibling handlers ---
    ClientViewCache View { get; }

    /// <summary>Build an NPC from a CHARDEF index, the same way ".add" does. Handlers
    /// that create a creature (a figurine turning into the thing it names) need it and
    /// have no other route to the definition-to-character mapping.</summary>
    SphereNet.Game.Objects.Characters.Character CreateNpcFromDefinition(int defIndex, string fallbackName);
    ClientGumpRegistry Gumps { get; }
    ClientTargetState Targets { get; }
    ClientTargetingHandler Targeting { get; }
    ClientDialogHandler Dialogs { get; }
    ClientItemUseHandler ItemUse { get; }

    // --- script services ---
    ScriptFileHandle? ScriptFile { get; }
    ScriptDbAdapter? ScriptDb { get; }
    ScriptDbAdapter? ScriptLdb { get; }
    ScriptDbAdapter? ScriptMdb { get; }
    string ScriptDatabaseRoot { get; }

    // --- shared mutable state bridges ---
    string? PendingDialogCloseFunction { get; set; }
    string PendingDialogArgs { get; set; }
    ushort PendingMenuId { get; set; }
    string PendingMenuDefname { get; set; }
    List<MenuOptionEntry>? PendingMenuOptions { get; set; }
    List<(uint ClilocId, string Args)>? ScriptTooltipProperties { get; set; }
    List<(ushort EntryTag, uint ClilocId, ushort Flags)>? ScriptContextEntries { get; set; }
    short LastHits { get; set; }
    short LastMana { get; set; }
    short LastStam { get; set; }
    long LastVitalsPacketTick { get; set; }

    // --- server-wired callbacks ---
    Action<Point3D, int, PacketWriter, uint>? BroadcastNearby { get; }
    Action<Point3D, int, PacketWriter, uint, Character>? BroadcastMoveNearby { get; }
    Action<Character>? BroadcastCharacterAppear { get; }
    Action<Point3D, int, uint, Action<Character, GameClient>>? ForEachClientInRange { get; }
    Action<Action<Character, GameClient>>? ForEachPlayingClient { get; }
    Action<Serial, PacketWriter>? SendToChar { get; }
    Action<Character>? OnCharacterDeathOfOther { get; }
    Action<Character>? OnResurrectOther { get; }
    Action<Character, Character>? OnKillTarget { get; }
    Action<Character, Character, Item, Item>? SendTradeToPartner { get; }
    Action<Character, Item, Item>? SendTradeItemToPartner { get; }
    Action<Character, uint>? SendTradeCloseToPartner { get; }
    Action<Character, SecureTrade>? SendTradeUpdateToPartner { get; }
    Action<Character, string>? SendTradeMessageToPartner { get; }
    Action<Character>? RefreshBackpackForPartner { get; }

    // --- send / packet helpers ---
    void Send(PacketWriter packet);
    void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null);
    void NpcSpeech(Character npc, string text);
    void ObjectMessage(ObjBase target, string text);
    void SendCharacterStatus(Character ch, bool includeExtendedStats = true);
    byte GetNotoriety(Character ch);
    void Resync();
    void BroadcastDrawObject(Character ch);
    void BroadcastDeleteObject(uint uid);
    void BroadcastAnimation(Character actor, ushort legacyAction, NewAnimationGesture gesture, byte mode = 0, byte animDelay = 0);
    void SendDrawObject(Character ch);
    void SendDrawObjectWithHue(Character ch, ushort hue);
    void SendDrawObjectHidden(Character ch);
    void SendUpdateMobile(Character ch);
    void SendUpdateMobileWithHue(Character ch, ushort hue);
    void SendUpdateMobileHidden(Character ch);
    /// <summary>Send a container-add and hand the uid over from the ground view to
    /// the container view. Never send a 0x25 any other way: the ground view would
    /// keep the uid and delete it on the next tick.</summary>
    void SendContainerItem(SphereNet.Network.Packets.Outgoing.PacketContainerItem packet);
    void SendWorldItem(Item item);
    void SendItemVisualUpdate(Item item);
    void OpenDyeWindow(ObjBase target);
    void SendWorldItemAllShow(Item item);
    void SendSelfRedraw();
    void SendPaperdoll(Character ch);
    void SendOpenContainer(Item container);

    /// <summary>Containers this client has actually been shown (Source-X
    /// CClient::m_openedContainers). The pickup path consults it before letting an
    /// item leave a container.</summary>
    OpenedContainerRegistry OpenedContainers { get; }
    void SendAosTooltip(ObjBase obj, bool requested, bool invalidate = false);

    /// <summary>0xBF 0x19 type 0 - a creature is bonded (Source-X addBondedStatus).</summary>
    void SendBondedStatus(Objects.Characters.Character ch, bool isGhost);
    void SendSkillList();
    void SendPickupFailed(byte reason);
    bool CanSendStatusFor(Character ch);

    /// <summary>The mobile flags byte as THIS client must read it - bit 0x04 means
    /// poisoned or flying depending on the viewer's client (CChar::GetModeFlag,
    /// CCharStatus.cpp:659).</summary>
    byte BuildMobileFlags(Character ch);

    /// <summary>Play an action on a character for everyone who can see them, with the
    /// body/mount translation and the per-viewer packet choice applied once, here.</summary>
    void PlayAnimation(Character actor, ushort action,
        SphereNet.Core.Enums.NewAnimationGesture gesture);

    /// <summary>Change the client's season, skipping a season it is already in
    /// (upstream CClient::addSeason, CClientMsg.cpp:509).</summary>
    void SendSeason(byte season, bool playSound, bool force = false);
    void PlaceItemInPack(Character target, Item item);
    bool TryDClickEquip(Item item, Layer layer);
    Item? GetTopContainer(Item item);
    void RefreshBackpackContents();
    PacketWriter BuildWorldItemPacket(uint serial, ushort itemId, ushort amount,
        short x, short y, sbyte z, ushort hue, byte direction = 0);

    // --- gump / dialog entry points ---
    void OpenCraftingGump(SkillType craftSkill);
    void OpenGuildStoneGump(Item stone);
    void OpenHouseSignGump(Item signOrMulti);
    void OpenBook(Item book, bool writable);

    /// <summary>Whether this reader may write that book - the same answer the open
    /// packet announces and the page handler enforces.</summary>
    bool IsBookWritableFor(Item book);
    void OpenBankBox();
    void OpenForeignBank(Character victim);
    void OpenInspectPropDialog(ObjBase obj, int requestedPage);
    bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null, string? arguments = null);
    bool IsScriptDialogOpen(string dialogId);
    bool CloseScriptDialog(string dialogId, int buttonId = 0);

    /// <summary>Feed a gump response through the normal receive path. Used by
    /// DIALOGCLOSE, which upstream answers on the client's behalf.</summary>
    void HandleGumpResponse(uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries);
    bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection);
    void SetPendingMenuContext(IScriptObj subject, IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> keys);
    void SendInputPromptGump(IScriptObj target, string propName, int maxLength);
    void SendScriptPrompt(IScriptObj target, string functionName, string message, bool unicode = false);

    // --- gameplay bridges ---
    void OpenVendorBuy(Character vendor);
    void OpenVendorSell(Character vendor);
    void HandleVendorInteraction(Character vendor);
    void HandleDoubleClick(uint uid);
    ResDisplayVersion HandleResolvedClientVersion();
    void StoreReportedClientVersion();
    void HandleCastSpell(SpellType spell, uint targetUid);
    void HandleChatOpen();
    void HandleQueryDesignDetails(byte[] data);
    void BeginHouseCustomization(Item multi);
    void BeginInfoSkill(SkillType skill, int skillId);
    void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind);
    void BeginTargetedSkill(SkillType skill, int skillId, Core.Types.Serial targetUid);
    void BeginXVerbTarget(string verb, string args);
    void BeginAreaTarget(string verb, int range, string verbArgs = "");
    void ResendCharacterList();
    void ApplyNewbieSection(Objects.Characters.Character ch, string sectionName);
    void SendPrompt(uint promptId, string message, Action<uint, uint, uint, string>? callback = null, bool unicode = false);
    void OnResurrect();
    Character? DismountCharacter();
    bool TryMountCharacter(Character mount);
    void ResetWalkValidator();
    void ToggleDoor(Item door);

    /// <summary>Move a vertical gate between its two heights (Source-X
    /// Use_Portculis).</summary>
    bool UsePortcullis(Item gate);

    /// <summary>Signal whatever this item is LINKed to, the way Source-X Use_Item
    /// follows m_uidLink after the item's own use.</summary>
    void FollowItemLinks(Item start);
    bool TryToggleNearestMapStaticDoor(uint clientSerial);
    void UsePotion(Item potion);

    /// <summary>Convey a potion's stored effect to a character other than the client's
    /// own — the pet that was just handed a bottle (CANPETSDRINKPOTION). False when the
    /// bottle names no resolvable effect.</summary>
    bool ApplyPotionEffectTo(Objects.Characters.Character target, Item potion);
    bool HasAmmoInBackpack(ItemType ammo);
    void ConsumeAmmoFromBackpack(ItemType ammo);
    bool TryHandlePetCommand(string text);
    bool TryHandleCommandSpeech(string text);
    void SetWarMode(bool warMode, bool syncClients, bool preserveTarget);
    void FaceTarget(Character target);
    bool InitiateTrade(Character partner, Item? firstItem = null);
    void SendTradeUpdateToBoth(SecureTrade trade);
    void TickPendingSkill();
    /// <summary>Source-X Skill_Start for a skill a tool puts to work at a target.</summary>
    void StartSkillFromTool(SkillType skill, Serial targetUid, Objects.ObjBase? target, Point3D? point, Objects.Items.Item? tool);
    void TickPendingCraft();
    bool BeginPendingCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump);

    // --- targeting ---
    void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1);
    void SetPendingMultiTarget(Action<uint, short, short, sbyte, ushort> callback,
        ushort multiId, short xOff, short yOff, short zOff, ushort hue);
    /// <summary>Raise a cursor through the shared arming path (replaced-cursor
    /// cancel, fresh session id, 0x6C or the 0x99 multi preview).</summary>
    void ArmTargetCursor(Action<uint, short, short, sbyte, ushort>? callback, byte cursorType,
        byte flags = 0, ushort? multiId = null, short yOff = 0, ushort hue = 0);
    void ClearPendingTargetState();
    bool TryAddAtTarget(string token, Point3D targetPos, uint targetSerial = 0, ushort amount = 1);
    bool RemoveTargetedObject(uint uid);
    bool TryDeleteItemFromClient(Item item) => World.TryDeleteObject(item, notify: Triggers == null ? null : target =>
        Triggers.FireItemTrigger(target, ItemTrigger.Destroy, new TriggerArgs()) != TriggerResult.True);
    Item? DuplicateItem(Item src);
    void SpawnCageAround(Point3D centre);
    int ExecuteAreaVerb(string verb, Point3D centre, int range, string verbArgs = "");
    Character? ResolvePickedChar(uint uid);
}
