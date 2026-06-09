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
/// Inventory/interaction handler extracted from the GameClient.Inventory
/// partial (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Single click, item pickup/drop/equip, drag bookkeeping, profile and
/// status requests. The method bodies moved verbatim; the private context
/// shims below enumerate exactly what this handler needs from GameClient.
/// </summary>
public sealed class ClientInventoryHandler
{
    private readonly GameClient _client;

    public ClientInventoryHandler(GameClient client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private HousingEngine? _housingEngine => _client.Housing;
    private const int UpdateRange = GameClient.UpdateRange;
    private static NotorietyHueSettings NotorietyHues => GameClient.NotorietyHues;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, uint, Action<Character, GameClient>>? ForEachClientInRange => _client.ForEachClientInRange;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private void SendPickupFailed(byte reason) => _client.SendPickupFailed(reason);
    private TradeManager? _tradeManager => _client.TradeM;
    private void PlaceItemInPack(Character target, Item item) => _client.PlaceItemInPack(target, item);
    private void SendTradeUpdateToBoth(SecureTrade trade) => _client.SendTradeUpdateToBoth(trade);
    private Action<Character, Item, Item>? SendTradeItemToPartner => _client.SendTradeItemToPartner;
    private SpellEngine? _spellEngine => _client.Spells;
    private bool CanSendStatusFor(Character ch) => _client.CanSendStatusFor(ch);
    private void SendSkillList() => _client.SendSkillList();
    private void InitiateTrade(Character partner, Item? firstItem = null) => _client.InitiateTrade(partner, firstItem);
    private void SendCharacterStatus(Character ch, bool includeExtendedStats = true) => _client.SendCharacterStatus(ch, includeExtendedStats);

    public void HandleSingleClick(uint uid)
    {
        if (_character == null) return;

        var obj = _world.FindObject(new Serial(uid));
        if (obj == null) return;

        if (obj is Character clickTarget &&
            clickTarget.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible) &&
            _character.PrivLevel < PrivLevel.Counsel)
            return;

        // Fire @Click trigger (and the legacy @ToolTip — old-style single-click
        // name/tooltip request. IsTrigUsed-gated: single click is a hot path).
        if (_triggerDispatcher != null)
        {
            if (obj is Character clickCh)
            {
                if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.ToolTip) &&
                    _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.ToolTip,
                        new TriggerArgs { CharSrc = _character, ScriptConsole = _client }) == TriggerResult.True)
                    return;
                var result = _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = _client });
                if (result == TriggerResult.True)
                    return;
            }
            else if (obj is Item clickItem)
            {
                if (_triggerDispatcher.IsItemTriggerUsed(ItemTrigger.Tooltip) &&
                    _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Tooltip,
                        new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = _client }) == TriggerResult.True)
                    return;
                var result = _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = _client });
                if (result == TriggerResult.True)
                    return;
            }
        }

        // Overhead name: for characters, the hue follows notoriety so the
        // label reads blue/green/grey/orange/red/yellow. Items stay grey.
        ushort nameHue = 0x03B2;
        if (obj is Character labelCh)
            nameHue = NotoToHue(GetNotoriety(labelCh), labelCh);
        _netState.Send(new PacketSpeechUnicodeOut(
            uid, (ushort)(obj is Character c ? c.BodyId : 0),
            6, nameHue, 3, "TRK", "", obj.GetName()));

        if (_triggerDispatcher != null)
        {
            if (obj is Character afterClickCh)
            {
                _triggerDispatcher.FireCharTrigger(afterClickCh, CharTrigger.AfterClick,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = _client });
            }
            else if (obj is Item afterClickItem)
            {
                _triggerDispatcher.FireItemTrigger(afterClickItem, ItemTrigger.AfterClick,
                    new TriggerArgs { CharSrc = _character, ItemSrc = afterClickItem, ScriptConsole = _client });
            }
        }
    }

    private bool IsInsideContainer(Item container, Serial parentUid, int maxDepth = 16)
    {
        var current = container;
        for (int i = 0; i < maxDepth && current != null; i++)
        {
            if (current.Uid == parentUid) return true;
            if (!current.ContainedIn.IsValid) break;
            current = _world.FindItem(current.ContainedIn);
        }
        return false;
    }

    internal Item? GetTopContainer(Item item)
    {
        var current = item;
        for (int i = 0; i < 16 && current.ContainedIn.IsValid; i++)
        {
            var parent = _world.FindItem(current.ContainedIn);
            if (parent == null) break;
            current = parent;
        }
        return current;
    }

    /// <summary>Convert a notoriety byte (1-7) to the hue used for
    /// overhead labels and system speech. Values mirror Source-X
    /// CServerConfig::m_iColorNoto* defaults:
    /// good/innocent=0x59 blue, guild-same=0x3f green, neutral=0x3b2 grey,
    /// criminal=0x3b2 grey, guild-war=0x90 orange, evil/murderer=0x22 red,
    /// invul=0x35 yellow. Values are configurable through sphere.ini ColorNoto* keys.</summary>
    private static ushort NotoToHue(byte noto, Character subject)
    {
        var hues = NotorietyHues;
        return noto switch
        {
            1 => !subject.IsPlayer && subject.NpcBrain != NpcBrainType.None ? hues.GoodNpc : hues.Good,
            2 => hues.GuildSame,
            4 => hues.Criminal,
            5 => hues.GuildWar,
            6 => hues.Evil,
            7 => subject.PrivLevel >= PrivLevel.GM ? hues.InvulGameMaster : hues.Invul,
            3 => hues.Neutral,
            _ => hues.Default,
        };
    }

    // ==================== Item Pick Up ====================

    // Picks the pickup-source trigger matching where the item is being taken from.
    // Equipment and stack-splits are distinguished from the plain pack/ground cases
    // so scripts can gate each independently (Source-X PICKUP_SELF / PICKUP_STACK).
    // Equipped items report ContainedIn = the wearer, so the equip check must come
    // before the container check to avoid misclassifying worn items as pack pickups.
    private static ItemTrigger SelectPickupTrigger(Item item, ushort amount)
    {
        if (item.IsEquipped) return ItemTrigger.PickupSelf;
        if (amount > 0 && amount < item.Amount && item.Amount > 1) return ItemTrigger.PickupStack;
        if (item.ContainedIn.IsValid) return ItemTrigger.PickupPack;
        return ItemTrigger.PickupGround;
    }

    public void HandleItemPickup(uint serial, ushort amount)
    {
        if (_character == null) return;
        if (_character.IsDead)
        {
            SendPickupFailed(1);
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item == null)
        {
            SendPickupFailed(5); // doesn't exist
            return;
        }

        if (_housingEngine != null && !_housingEngine.CanPickupHouseItem(_character, item))
        {
            SendPickupFailed(1);
            return;
        }

        var (dragSourceSerial, dragSourcePos) = GetDragSource(item);

        // Fire the pickup trigger, choosing the most specific source variant:
        // Self  = dragged off the character's own equipment layers,
        // Stack = a partial amount split out of a larger stack,
        // Pack  = taken from inside a container, Ground = loose on the ground.
        if (_triggerDispatcher != null)
        {
            var trigger = SelectPickupTrigger(item, amount);
            var result = _triggerDispatcher.FireItemTrigger(item, trigger,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
            {
                SendPickupFailed(1);
                return;
            }
        }

        if (_character.PrivLevel < PrivLevel.GM)
        {
            if (!item.ContainedIn.IsValid)
            {
                int dist = _character.Position.GetDistanceTo(item.Position);
                if (dist > 3) { SendPickupFailed(4); return; }
            }
            else
            {
                var topCont = GetTopContainer(item);
                if (topCont != null)
                {
                    if (!topCont.ContainedIn.IsValid)
                    {
                        int cDist = _character.Position.GetDistanceTo(topCont.Position);
                        if (cDist > 3) { SendPickupFailed(4); return; }
                    }
                    else
                    {
                        var wearer = _world.FindChar(topCont.ContainedIn);
                        if (wearer != null && wearer != _character && wearer.IsPlayer)
                        {
                            SendPickupFailed(1); return;
                        }
                        if (wearer != null)
                        {
                            int wDist = _character.Position.GetDistanceTo(wearer.Position);
                            if (wDist > 3) { SendPickupFailed(4); return; }
                        }
                    }
                }
            }
        }

        // Stack splitting: if picking up less than the full stack, create a split item
        if (amount > 0 && amount < item.Amount && item.Amount > 1)
        {
            var splitItem = _world.CreateItem();
            splitItem.BaseId = item.BaseId;
            splitItem.Hue = item.Hue;
            splitItem.Amount = amount;
            splitItem.More1 = item.More1;
            splitItem.More2 = item.More2;
            item.Amount -= amount;
            splitItem.ContainedIn = _character.Uid;
            _character.SetTag("DRAGGING", splitItem.Uid.Value.ToString());
            BroadcastDragAnimation(splitItem, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);
            return;
        }

        if (item.IsEquipped)
        {
            var owner = _world.FindChar(item.ContainedIn);
            if (owner != null && owner != _character && _character.PrivLevel < PrivLevel.GM)
            {
                SendPickupFailed(1); // cannot pick up
                return;
            }
            // Fire @Unequip trigger on the item being removed
            if (_triggerDispatcher != null && owner != null)
            {
                var unequipResult = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Unequip,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (unequipResult == TriggerResult.True)
                {
                    SendPickupFailed(1);
                    return;
                }
            }
            var unequipOwner = owner;
            owner?.Unequip(item.EquipLayer);
            if (unequipOwner != null)
            {
                var removePkt = new PacketDeleteObject(item.Uid.Value);
                BroadcastNearby?.Invoke(unequipOwner.Position, UpdateRange, removePkt, _character.Uid.Value);
            }
        }
        else if (item.ContainedIn.IsValid)
        {
            var container = _world.FindItem(item.ContainedIn);
            container?.RemoveItem(item);
        }
        else
        {
            var sector = _world.GetSector(item.Position);
            sector?.RemoveItem(item);
        }

        item.ContainedIn = _character.Uid;
        _character.SetTag("DRAGGING", serial.ToString());
        BroadcastDragAnimation(item, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);

        if (item.BaseId == 0x0EED)
            SendCharacterStatus(_character);
    }

    private (uint Serial, Point3D Position) GetDragSource(Item item)
    {
        if (!item.ContainedIn.IsValid)
            return (0, item.Position);

        var container = _world.FindItem(item.ContainedIn);
        if (container != null)
        {
            var top = GetTopContainer(container) ?? container;
            if (top.ContainedIn.IsValid)
            {
                var holder = _world.FindChar(top.ContainedIn);
                if (holder != null)
                    return (holder.Uid.Value, holder.Position);
            }
            return (top.Uid.Value, top.Position);
        }

        var character = _world.FindChar(item.ContainedIn);
        if (character != null)
            return (character.Uid.Value, character.Position);

        return (0, item.Position);
    }

    private void BroadcastDragAnimation(Item item, uint sourceSerial, Point3D sourcePos,
        uint targetSerial, Point3D targetPos, Point3D origin)
    {
        var packet = new PacketDragAnimation(
            item.DispIdFull,
            item.Hue,
            item.Amount == 0 ? (ushort)1 : item.Amount,
            sourceSerial,
            sourcePos.X,
            sourcePos.Y,
            sourcePos.Z,
            targetSerial,
            targetPos.X,
            targetPos.Y,
            targetPos.Z);

        if (ForEachClientInRange != null)
        {
            ForEachClientInRange(origin, UpdateRange, 0, (_, observer) =>
            {
                if (observer.NetState.IsKingdomRebornClient
                    || observer.NetState.IsEnhancedClient
                    || observer.NetState.SupportsStygianAbyss)
                    return;

                observer.Send(packet);
            });
            return;
        }

        BroadcastNearby?.Invoke(origin, UpdateRange, packet, 0);
    }

    // ==================== Item Drop ====================

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        if (!_character.TryGetTag("DRAGGING", out var dragTag) || dragTag != serial.ToString())
        {
            _netState.Send(new PacketDropReject());
            return;
        }

        _character.RemoveTag("DRAGGING");

        if (containerUid != 0 && containerUid != 0xFFFFFFFF)
        {
            var container = _world.FindItem(new Serial(containerUid));
            if (container != null && _tradeManager?.FindByContainer(containerUid) is { } dropTrade)
            {
                if (!dropTrade.IsParticipant(_character))
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                var dropOnTradeResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnTrade,
                    new TriggerArgs
                    {
                        CharSrc = _character,
                        ItemSrc = item,
                        O1 = dropTrade.GetPartner(_character),
                        N1 = (int)dropTrade.SessionId.Value
                    });
                if (dropOnTradeResult == TriggerResult.True)
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                var myCont = dropTrade.GetOwnContainer(_character);
                myCont.AddItem(item);
                item.Position = new Point3D(30, 30, 0, _character.MapIndex);
                dropTrade.ResetAcceptance();
                SendTradeUpdateToBoth(dropTrade);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, 30, 30,
                    myCont.Uid.Value, item.Hue, _netState.IsClientPost6017));
                SendTradeItemToPartner?.Invoke(dropTrade.GetPartner(_character), item, myCont);
                _netState.Send(new PacketDropAck());
                return;
            }
            if (container != null)
            {
                // Distance check: player must be near the container (or its parent on world).
                // Bank boxes (equipped on the player) are always reachable; other containers
                // must be within 3 tiles. Without this a crafted packet can move items into
                // distant containers the client happened to open earlier.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    if (container.EquipLayer == Layer.BankBox || container.EquipLayer == Layer.Pack)
                    {
                        var owner = _world.FindChar(container.ContainedIn);
                        if (owner != null && owner != _character)
                        {
                            PlaceItemInPack(_character, item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                    else
                    {
                        var topContainer = GetTopContainer(container);
                        if (topContainer != null && !topContainer.ContainedIn.IsValid)
                        {
                            int cDist = _character.Position.GetDistanceTo(topContainer.Position);
                            if (cDist > 3)
                            {
                                PlaceItemInPack(_character, item);
                                _netState.Send(new PacketDropReject());
                                return;
                            }
                        }
                    }
                }

                // Nesting depth limit — prevent container-in-container bypass of slot limits.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    int depth = 0;
                    var parent = container;
                    while (parent != null && parent.ContainedIn.IsValid && depth < 8)
                    {
                        parent = _world.FindItem(parent.ContainedIn);
                        depth++;
                    }
                    if (depth >= 8)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }

                if (_character.PrivLevel < PrivLevel.GM)
                {
                    bool isBank = container.EquipLayer == Layer.BankBox;
                    int currentCount = _world.GetContainerContents(container.Uid).Count();
                    int maxItems = isBank ? _world.MaxBankItems : _world.MaxContainerItems;
                    if (currentCount >= maxItems)
                    {
                        SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullItems : Msg.ContFullItems));
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    int weightLimit = isBank ? _world.MaxBankWeight : _world.MaxContainerWeight;
                    if (weightLimit > 0)
                    {
                        int totalWeight = 0;
                        foreach (var b in _world.GetContainerContents(container.Uid))
                            totalWeight += b.Weight * Math.Max(1, (int)b.Amount);
                        if (totalWeight + item.Weight * Math.Max(1, (int)item.Amount) > weightLimit)
                        {
                            SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullWeight : Msg.ContFullWeight));
                            PlaceItemInPack(_character, item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                }

                if (item.Uid == container.Uid || IsInsideContainer(container, item.Uid))
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                // Fire @DropOn_Item
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = container });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }
                container.AddItem(item);
                item.Position = new Point3D(x, y, 0, _character.MapIndex);
                // Critical: tell the client the item actually landed in the
                // container. Without 0x25 the client only remembers the
                // earlier pickup → the item silently vanishes from its view.
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    container.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                _netState.Send(new PacketDropAck());
                if (item.BaseId == 0x0EED)
                    SendCharacterStatus(_character);
                return;
            }

            var charTarget = _world.FindChar(new Serial(containerUid));
            if (charTarget != null && charTarget == _character)
            {
                // Fire @DropOn_Self
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnSelf,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
            else if (charTarget != null)
            {
                if (_character.PrivLevel < PrivLevel.GM &&
                    (_character.MapIndex != charTarget.MapIndex ||
                     _character.Position.GetDistanceTo(charTarget.Position) > 3))
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                // Fire @DropOn_Char
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnChar,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = charTarget });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }

                if (charTarget.IsPlayer && _tradeManager != null)
                {
                    InitiateTrade(charTarget, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Giving an item to an NPC — fire @ReceiveItem so quest/reward/
                // "bring me X" scripts can handle it (<src> = giver, <argo> = item).
                // RETURN 1 means the script fully consumed/handled the item.
                if (!charTarget.IsPlayer && _triggerDispatcher != null)
                {
                    var rcv = _triggerDispatcher.FireCharTrigger(charTarget, CharTrigger.ReceiveItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item });
                    if (rcv == TriggerResult.True)
                    {
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                    // @NPCRefuseItem — the engine's accept gate (Source-X). RETURN 1
                    // refuses the gift: the item bounces back to the giver instead of
                    // entering the NPC's pack. Default (no handler) accepts, so prior
                    // behaviour is preserved.
                    var refuse = _triggerDispatcher.FireCharTrigger(charTarget, CharTrigger.NPCRefuseItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item });
                    if (refuse == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                    // Default: NPC accepts the item into its pack and fires @NPCAcceptItem.
                    PlaceItemInPack(charTarget, item);
                    _triggerDispatcher.FireCharTrigger(charTarget, CharTrigger.NPCAcceptItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item });
                    _netState.Send(new PacketDropAck());
                    return;
                }

                PlaceItemInPack(charTarget, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // Distance check + map bounds check for ground drops
        if (_character.PrivLevel < PrivLevel.GM)
        {
            var md = _world.MapData;
            if (md != null)
            {
                var (mapW, mapH) = md.GetMapSize(_character.MapIndex);
                if (x < 0 || y < 0 || x >= mapW || y >= mapH)
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
            }
            int dropDist = Math.Max(Math.Abs(_character.X - x), Math.Abs(_character.Y - y));
            if (dropDist > 3)
            {
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        if (_character.PrivLevel < PrivLevel.GM && _housingEngine != null)
        {
            var dropPos = new Point3D(x, y, z, _character.MapIndex);
            var house = _housingEngine.FindHouseAt(dropPos);
            if (house != null && !house.CanAccess(_character.Uid))
            {
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        var dropResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnGround,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
        if (dropResult == TriggerResult.True)
        {
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropReject());
            return;
        }

        var groundPos = new Point3D(x, y, z, _character.MapIndex);
        var sector = _world.GetSector(groundPos);

        // Stack onto a matching pile if possible; otherwise count the non-stacking
        // items already on this exact tile so the new drop gets a distinct facing.
        // A pile of separate items should scatter (Source-X behaviour) rather than
        // all share one orientation — basing direction on the item's own previous
        // value made every fresh drop land as Direction=1.
        int tileItemCount = 0;
        if (sector != null)
        {
            foreach (var existing in sector.Items)
            {
                if (existing.X != x || existing.Y != y || existing.MapIndex != _character.MapIndex)
                    continue;

                if (existing.CanStackWith(item) && (existing.Amount + item.Amount) <= ushort.MaxValue)
                {
                    existing.Amount += item.Amount;
                    BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, existing.Position, existing.Position);
                    _world.RemoveItem(item);
                    item.Delete();
                    _netState.Send(new PacketDropAck());
                    BroadcastNearby?.Invoke(existing.Position, UpdateRange,
                        new PacketSound(GetDropSound(existing), existing.X, existing.Y, existing.Z), 0);
                    BroadcastNearby?.Invoke(existing.Position, UpdateRange,
                        new PacketWorldItem(existing.Uid.Value, existing.DispIdFull, existing.Amount,
                            existing.X, existing.Y, existing.Z, existing.Hue, existing.Direction), 0);
                    return;
                }

                tileItemCount++;
            }
        }

        item.Direction = (byte)((tileItemCount % 7) + 1);
        item.TryFlipDisplay();

        _world.PlaceItemWithDecay(item, groundPos);
        _netState.Send(new PacketDropAck());
        BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, groundPos, groundPos);
        BroadcastNearby?.Invoke(groundPos, UpdateRange,
            new PacketSound(GetDropSound(item), groundPos.X, groundPos.Y, groundPos.Z), 0);
        BroadcastNearby?.Invoke(groundPos, UpdateRange,
            new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                item.X, item.Y, item.Z, item.Hue, item.Direction), 0);
    }

    /// <summary>
    /// Script BOUNCE/DROP verb bridge (Character.OnDragRelease): release the
    /// item the character is dragging (DRAGGING tag) either to the ground at
    /// their feet or back into the backpack, and cancel the client-side drag
    /// cursor with 0x27. Returns false when nothing is being dragged.
    /// </summary>
    public bool ReleaseDraggedItem(bool toGround)
    {
        if (_character == null)
            return false;
        if (!_character.TryGetTag("DRAGGING", out string? dragSer) ||
            !uint.TryParse(dragSer, out uint dragUid))
            return false;

        _character.RemoveTag("DRAGGING");
        _netState.Send(new PacketPickupFailed(0)); // cancel the drag cursor

        var item = _world.FindItem(new Serial(dragUid));
        if (item == null || item.IsDeleted)
            return true;

        var pack = _character.Backpack;
        if (!toGround && pack != null)
        {
            pack.AddItem(item);
            item.Position = new Point3D(50, 50, 0, _character.MapIndex);
            _netState.Send(new PacketContainerItem(
                item.Uid.Value, item.DispIdFull, 0, item.Amount,
                item.X, item.Y, pack.Uid.Value, item.Hue,
                _netState.IsClientPost6017));
            return true;
        }

        _world.PlaceItemWithDecay(item, _character.Position);
        BroadcastNearby?.Invoke(item.Position, UpdateRange,
            new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                item.X, item.Y, item.Z, item.Hue, item.Direction), 0);
        return true;
    }

    /// <summary>Item-aware drop sound: gold coins get the amount-scaled coin
    /// sounds; everything else keeps the generic 0x42 item drop.</summary>
    private static ushort GetDropSound(Item item)
    {
        if (item.BaseId == 0x0EED)
            return item.Amount switch { 1 => (ushort)0x02E4, < 6 => (ushort)0x02E5, _ => (ushort)0x02E6 };
        return 0x0042;
    }

    // ==================== Item Equip ====================

    public void HandleItemEquip(uint serial, byte layer, uint charSerial)
    {
        if (_character == null) return;
        if (_character.IsDead) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        if (layer == 0 || layer >= (byte)Layer.Qty) return;

        if (_character.PrivLevel < PrivLevel.GM &&
            item.ContainedIn != _character.Uid)
            return;

        var target = _world.FindChar(new Serial(charSerial));
        if (target == null) target = _character;

        if (target != _character && _character.PrivLevel < PrivLevel.GM) return;

        // Fire @EquipTest — if script blocks, deny equip
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.EquipTest,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
                return;
        }

        // Spell interruption on equip change
        _spellEngine?.TryInterruptFromEquip(target);

        // Two-handed weapon ↔ shield mutual exclusion
        if ((Layer)layer == Layer.TwoHanded && item.IsTwoHanded)
        {
            var oldShield = target.GetEquippedItem(Layer.OneHanded);
            if (oldShield != null && oldShield.ItemType == ItemType.Shield)
            {
                target.Unequip(Layer.OneHanded);
                PlaceItemInPack(target, oldShield);
            }
        }
        else if ((Layer)layer == Layer.OneHanded && item.ItemType == ItemType.Shield)
        {
            var oldWeapon = target.GetEquippedItem(Layer.TwoHanded);
            if (oldWeapon != null && oldWeapon.IsTwoHanded)
            {
                target.Unequip(Layer.TwoHanded);
                PlaceItemInPack(target, oldWeapon);
            }
        }

        target.Equip(item, (Layer)layer);
        if ((Layer)layer is Layer.OneHanded or Layer.TwoHanded && IsCombatEquipItem(item))
        {
            bool noWait = (Character.CombatFlags & (int)CombatFlags.FirstHitInstant) != 0;
            int delayMs = CombatEngine.GetSwingDelayMs(target, item);
            target.BeginEquipSwingWait(Environment.TickCount64, delayMs, noWait);
        }

        var wornPkt = new PacketWornItem(
            item.Uid.Value, item.DispIdFull, layer,
            target.Uid.Value, item.Hue);
        _netState.Send(wornPkt);
        BroadcastNearby?.Invoke(target.Position, UpdateRange, wornPkt, _character.Uid.Value);

        _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
    }

    private static bool IsCombatEquipItem(Item item) => item.ItemType is
        ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or ItemType.WeaponSword or
        ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponMaceStaff or
        ItemType.WeaponMaceCrook or ItemType.WeaponMacePick or ItemType.WeaponAxe or
        ItemType.WeaponXBow or ItemType.WeaponThrowing or ItemType.WeaponWhip or
        ItemType.Shield;

    // ==================== Status Request ====================

    public void HandleProfileRequest(byte mode, uint serial, string bioText = "")
    {
        if (_character == null) return;

        Character? ch = _world.FindChar(new Serial(serial));
        ch ??= _character;

        if (mode == 1)
        {
            if (ch == _character || _character.PrivLevel >= PrivLevel.GM)
            {
                var result = _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Profile, new TriggerArgs
                {
                    CharSrc = _character,
                    S1 = bioText,
                    N1 = mode
                });
                if (result == TriggerResult.True)
                    return;

                ch.SetTag("PROFILE_BIO", bioText);
            }
            return;
        }

        if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Profile, new TriggerArgs
        {
            CharSrc = _character,
            N1 = mode
        }) == TriggerResult.True)
            return;

        string title = string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";

        string profile = ch.TryGetTag("PROFILE_BIO", out string? bio) && bio != null ? bio : "";
        _netState.Send(new PacketProfileResponse(ch.Uid.Value, title, profile));
    }

    public void HandleStatusRequest(byte type, uint serial)
    {
        if (_character == null) return;

        if (type == 4 || type == 0) // status
        {
            Character? ch = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                ch = _world.FindChar(new Serial(serial));

            // Some clients may request status with invalid/empty serial after resync.
            // Fallback to self so status bars are never blank.
            ch ??= _character;

            // Self status is always allowed; other mobiles require visibility/range.
            if (ch != _character && !CanSendStatusFor(ch))
                return;

            // @UserStats fires when the client opens the status window
            // on *its own* character. Matches Source-X CClient::Event_StatusRequest.
            if (ch == _character)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserStats,
                    new TriggerArgs { CharSrc = _character });
            }

            SendCharacterStatus(ch, includeExtendedStats: ch == _character);
        }
        else if (type == 5) // skill list
        {
            SendSkillList();
        }
    }

    // ==================== Target Response ====================
}
