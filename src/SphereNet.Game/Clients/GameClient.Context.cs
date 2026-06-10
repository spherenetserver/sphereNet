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
/// Explicit <see cref="IClientContext"/> implementation (decomposition
/// phase 4). Every member forwards to the existing GameClient surface —
/// no behaviour lives here. Explicit implementation keeps the internal
/// members internal (implicit interface implementation would force them
/// public).
/// </summary>
public sealed partial class GameClient : IClientContext
{
    Account? IClientContext.Account => Account;
    Character? IClientContext.Character => Character;
    GameWorld IClientContext.World => World;
    NetState IClientContext.NetState => NetState;
    bool IClientContext.IsPlaying => IsPlaying;
    ILogger IClientContext.Log => Log;

    TriggerDispatcher? IClientContext.Triggers => Triggers;
    HousingEngine? IClientContext.Housing => Housing;
    TradeManager? IClientContext.TradeM => TradeM;
    SpellEngine? IClientContext.Spells => Spells;
    SkillHandlers? IClientContext.SkillH => SkillH;
    Mounts.MountEngine? IClientContext.MountE => MountE;
    CraftingEngine? IClientContext.CraftE => CraftE;
    GuildManager? IClientContext.GuildM => GuildM;
    PartyManager? IClientContext.PartyM => PartyM;
    CommandHandler? IClientContext.Cmds => Cmds;
    MovementEngine? IClientContext.MoveEng => MoveEng;
    SpeechEngine? IClientContext.SpeechEng => SpeechEng;
    DeathEngine? IClientContext.DeathEng => DeathEng;

    ClientViewCache IClientContext.View => View;
    ClientGumpRegistry IClientContext.Gumps => Gumps;
    ClientTargetState IClientContext.Targets => Targets;
    ClientDialogHandler IClientContext.Dialogs => Dialogs;

    ScriptFileHandle? IClientContext.ScriptFile => ScriptFile;
    ScriptDbAdapter? IClientContext.ScriptDb => ScriptDb;
    ScriptDbAdapter? IClientContext.ScriptLdb => ScriptLdb;
    string IClientContext.ScriptDatabaseRoot => ScriptDatabaseRoot;

    string? IClientContext.PendingDialogCloseFunction
    {
        get => PendingDialogCloseFunction;
        set => PendingDialogCloseFunction = value;
    }
    string IClientContext.PendingDialogArgs
    {
        get => PendingDialogArgs;
        set => PendingDialogArgs = value;
    }
    ushort IClientContext.PendingMenuId
    {
        get => _pendingMenuId;
        set => _pendingMenuId = value;
    }
    string IClientContext.PendingMenuDefname
    {
        get => _pendingMenuDefname;
        set => _pendingMenuDefname = value;
    }
    List<MenuOptionEntry>? IClientContext.PendingMenuOptions
    {
        get => _pendingMenuOptions;
        set => _pendingMenuOptions = value;
    }
    short IClientContext.LastHits
    {
        get => _lastHits;
        set => _lastHits = value;
    }
    short IClientContext.LastMana
    {
        get => _lastMana;
        set => _lastMana = value;
    }
    short IClientContext.LastStam
    {
        get => _lastStam;
        set => _lastStam = value;
    }
    long IClientContext.LastVitalsPacketTick
    {
        get => _lastVitalsPacketTick;
        set => _lastVitalsPacketTick = value;
    }

    Action<Point3D, int, PacketWriter, uint>? IClientContext.BroadcastNearby => BroadcastNearby;
    Action<Point3D, int, PacketWriter, uint, Character>? IClientContext.BroadcastMoveNearby => BroadcastMoveNearby;
    Action<Character>? IClientContext.BroadcastCharacterAppear => BroadcastCharacterAppear;
    Action<Point3D, int, uint, Action<Character, GameClient>>? IClientContext.ForEachClientInRange => ForEachClientInRange;
    Action<Serial, PacketWriter>? IClientContext.SendToChar => SendToChar;
    Action<Character>? IClientContext.OnCharacterDeathOfOther => OnCharacterDeathOfOther;
    Action<Character>? IClientContext.OnResurrectOther => OnResurrectOther;
    Action<Character, Character>? IClientContext.OnKillTarget => OnKillTarget;
    Action<Character, Character, Item, Item>? IClientContext.SendTradeToPartner => SendTradeToPartner;
    Action<Character, Item, Item>? IClientContext.SendTradeItemToPartner => SendTradeItemToPartner;
    Action<Character, uint>? IClientContext.SendTradeCloseToPartner => SendTradeCloseToPartner;
    Action<Character, SecureTrade>? IClientContext.SendTradeUpdateToPartner => SendTradeUpdateToPartner;
    Action<Character, string>? IClientContext.SendTradeMessageToPartner => SendTradeMessageToPartner;
    Action<Character>? IClientContext.RefreshBackpackForPartner => RefreshBackpackForPartner;

    void IClientContext.Send(PacketWriter packet) => Send(packet);
    void IClientContext.SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback) => SendGump(gump, callback);
    void IClientContext.NpcSpeech(Character npc, string text) => NpcSpeech(npc, text);
    void IClientContext.ObjectMessage(ObjBase target, string text) => ObjectMessage(target, text);
    void IClientContext.SendCharacterStatus(Character ch, bool includeExtendedStats) => SendCharacterStatus(ch, includeExtendedStats);
    byte IClientContext.GetNotoriety(Character ch) => GetNotoriety(ch);
    void IClientContext.Resync() => Resync();
    void IClientContext.BroadcastDrawObject(Character ch) => BroadcastDrawObject(ch);
    void IClientContext.BroadcastDeleteObject(uint uid) => BroadcastDeleteObject(uid);
    void IClientContext.BroadcastAnimation(Character actor, ushort legacyAction, NewAnimationGesture gesture, byte mode) => BroadcastAnimation(actor, legacyAction, gesture, mode);
    void IClientContext.SendDrawObject(Character ch) => SendDrawObject(ch);
    void IClientContext.SendDrawObjectWithHue(Character ch, ushort hue) => SendDrawObjectWithHue(ch, hue);
    void IClientContext.SendDrawObjectHidden(Character ch) => SendDrawObjectHidden(ch);
    void IClientContext.SendUpdateMobile(Character ch) => SendUpdateMobile(ch);
    void IClientContext.SendUpdateMobileWithHue(Character ch, ushort hue) => SendUpdateMobileWithHue(ch, hue);
    void IClientContext.SendUpdateMobileHidden(Character ch) => SendUpdateMobileHidden(ch);
    void IClientContext.SendWorldItem(Item item) => SendWorldItem(item);
    void IClientContext.SendWorldItemAllShow(Item item) => SendWorldItemAllShow(item);
    void IClientContext.SendSelfRedraw() => SendSelfRedraw();
    void IClientContext.SendPaperdoll(Character ch) => SendPaperdoll(ch);
    void IClientContext.SendOpenContainer(Item container) => SendOpenContainer(container);
    void IClientContext.SendSkillList() => SendSkillList();
    void IClientContext.SendPickupFailed(byte reason) => SendPickupFailed(reason);
    bool IClientContext.CanSendStatusFor(Character ch) => CanSendStatusFor(ch);
    void IClientContext.PlaceItemInPack(Character target, Item item) => PlaceItemInPack(target, item);
    Item? IClientContext.GetTopContainer(Item item) => GetTopContainer(item);
    void IClientContext.RefreshBackpackContents() => RefreshBackpackContents();
    PacketWriter IClientContext.BuildWorldItemPacket(uint serial, ushort itemId, ushort amount,
        short x, short y, sbyte z, ushort hue, byte direction) => BuildWorldItemPacket(serial, itemId, amount, x, y, z, hue, direction);

    void IClientContext.OpenCraftingGump(SkillType craftSkill) => OpenCraftingGump(craftSkill);
    void IClientContext.OpenGuildStoneGump(Item stone) => OpenGuildStoneGump(stone);
    void IClientContext.OpenHouseSignGump(Item signOrMulti) => OpenHouseSignGump(signOrMulti);
    void IClientContext.OpenBook(Item book, bool writable) => OpenBook(book, writable);
    void IClientContext.OpenBankBox() => OpenBankBox();
    void IClientContext.OpenForeignBank(Character victim) => OpenForeignBank(victim);
    void IClientContext.OpenInspectPropDialog(ObjBase obj, int requestedPage) => OpenInspectPropDialog(obj, requestedPage);
    bool IClientContext.OpenNamedDialog(string dialogId, int requestedPage, ObjBase? subject) => OpenNamedDialog(dialogId, requestedPage, subject);
    bool IClientContext.IsScriptDialogOpen(string dialogId) => IsScriptDialogOpen(dialogId);
    bool IClientContext.CloseScriptDialog(string dialogId) => CloseScriptDialog(dialogId);
    bool IClientContext.TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection) => TryFindMenuSection(menuDefname, out menuSection);
    void IClientContext.SendInputPromptGump(IScriptObj target, string propName, int maxLength) => SendInputPromptGump(target, propName, maxLength);

    void IClientContext.OpenVendorBuy(Character vendor) => OpenVendorBuy(vendor);
    void IClientContext.HandleVendorInteraction(Character vendor) => HandleVendorInteraction(vendor);
    void IClientContext.HandleDoubleClick(uint uid) => HandleDoubleClick(uid);
    void IClientContext.HandleCastSpell(SpellType spell, uint targetUid) => HandleCastSpell(spell, targetUid);
    void IClientContext.HandleChatOpen() => HandleChatOpen();
    void IClientContext.HandleQueryDesignDetails(byte[] data) => HandleQueryDesignDetails(data);
    void IClientContext.BeginHouseCustomization(Item multi) => BeginHouseCustomization(multi);
    void IClientContext.BeginInfoSkill(SkillType skill, int skillId) => BeginInfoSkill(skill, skillId);
    void IClientContext.BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind) => BeginActiveSkill(skill, skillId, kind);
    void IClientContext.BeginXVerbTarget(string verb, string args) => BeginXVerbTarget(verb, args);
    void IClientContext.OnResurrect() => OnResurrect();
    Character? IClientContext.DismountCharacter() => DismountCharacter();
    bool IClientContext.TryMountCharacter(Character mount) => TryMountCharacter(mount);
    void IClientContext.ResetWalkValidator() => ResetWalkValidator();
    void IClientContext.ToggleDoor(Item door) => ToggleDoor(door);
    bool IClientContext.TryToggleNearestMapStaticDoor(uint clientSerial) => TryToggleNearestMapStaticDoor(clientSerial);
    void IClientContext.UsePotion(Item potion) => UsePotion(potion);
    bool IClientContext.HasAmmoInBackpack(ItemType ammo) => HasAmmoInBackpack(ammo);
    void IClientContext.ConsumeAmmoFromBackpack(ItemType ammo) => ConsumeAmmoFromBackpack(ammo);
    bool IClientContext.TryHandlePetCommand(string text) => TryHandlePetCommand(text);
    bool IClientContext.TryHandleCommandSpeech(string text) => TryHandleCommandSpeech(text);
    void IClientContext.SetWarMode(bool warMode, bool syncClients, bool preserveTarget) => SetWarMode(warMode, syncClients, preserveTarget);
    void IClientContext.FaceTarget(Character target) => FaceTarget(target);
    void IClientContext.InitiateTrade(Character partner, Item? firstItem) => InitiateTrade(partner, firstItem);
    void IClientContext.SendTradeUpdateToBoth(SecureTrade trade) => SendTradeUpdateToBoth(trade);
    void IClientContext.TickPendingSkill() => TickPendingSkill();
    void IClientContext.TickPendingCraft() => TickPendingCraft();
    bool IClientContext.BeginPendingCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump) =>
        BeginPendingCraft(recipe, craftSkill, reopenGump);

    void IClientContext.SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType) => SetPendingTarget(callback, cursorType);
    void IClientContext.ClearPendingTargetState() => ClearPendingTargetState();
    bool IClientContext.TryAddAtTarget(string token, Point3D targetPos, uint targetSerial) => TryAddAtTarget(token, targetPos, targetSerial);
    bool IClientContext.RemoveTargetedObject(uint uid) => RemoveTargetedObject(uid);
    Item? IClientContext.DuplicateItem(Item src) => DuplicateItem(src);
    void IClientContext.SpawnCageAround(Point3D centre) => SpawnCageAround(centre);
    int IClientContext.ExecuteAreaVerb(string verb, Point3D centre, int range) => ExecuteAreaVerb(verb, centre, range);
    Character? IClientContext.ResolvePickedChar(uint uid) => ResolvePickedChar(uid);
}
