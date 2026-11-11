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
    private readonly IClientContext _client;

    internal ClientInventoryHandler(IClientContext client)
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

    /// <summary>Source-X NPC_OnTrainPay: consume gold handed to the trainer as
    /// skill points (1 gp = 0.1) up to the offered cap. Returns false when the
    /// pending offer doesn't match this NPC (normal gift handling continues).
    /// Leftover gold above the cap bounces back to the student's pack.</summary>
    /// <summary>Source-X NPC_OnHirePay/NPC_OnHirePayMore: gold given to an NPC
    /// whose chardef sets HIREDAYWAGE (or a legacy HIRE_WAGE tag) funds the
    /// NPC's own prepaid wage balance. First payment hires (ownership +
    /// follow); later payments extend the balance. Refuses another player's
    /// hireling and payments below one day's wage.</summary>
    private bool TryApplyHirePayment(Character npc, Item gold)
    {
        if (_character == null) return false;

        // Gold is hire money only after the hire talk: NPC_OnHireHear leaves an
        // NPC_MEM_ACT_SPEAK_HIRE memory of the payer, and NPC_OnItemGive routes gold
        // to NPC_OnHirePay only through it (CCharNPCAct.cpp:2078-2088).
        var mem = npc.Memory_FindObj(_character.Uid);
        if (mem == null || (mem.More1 & 0xFFFF) != Character.NpcMemActSpeakHire)
            return false;

        uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
        if (dayWage == 0 && npc.TryGetTag("HIRE_WAGE", out string? w) &&
            ScriptNumber.TryParseUInt(w, out uint tagWage))
            dayWage = tagWage;
        if (dayWage == 0)
            return false;

        var owner = npc.ResolveOwnerCharacter();
        if (owner != null && owner != _character)
        {
            _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetEmployed));
            return false;
        }

        if (owner == null && gold.Amount < dayWage)
        {
            _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetNotEnough));
            return false; // gift path may still bounce it back
        }

        // Someone the NPC fought, was harmed or irritated by does not get hired
        // (CCharNPCPet.cpp:750-755).
        if (owner == null &&
            (mem.GetMemoryTypes() & (MemoryType.Fight | MemoryType.HarmedBy | MemoryType.IrritatedBy)) != 0)
        {
            _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetNotWork));
            return false;
        }

        if (owner == null &&
            !npc.TryAssignOwnership(_character, _character, summoned: false, enforceFollowerCap: true))
            return false;

        if (owner == null)
        {
            // Put the loot cash away and mark everything it carries ATTR_OWNED -
            // a hireling does not give its gear away (CCharNPCPet.cpp:757-760).
            MarkHirelingGearOwned(npc);
        }
        // The talk is settled (:765).
        mem.More1 &= 0xFFFF0000;

        // The wage goes into the hireling's bank-box purse - m_Check_Amount, the
        // same number NPC_CheckHirelingStatus draws on (NPC_OnHirePayMore,
        // CCharNPCPet.cpp:705-723). A balance an earlier save kept in
        // TAG.HIRE_BALANCE is folded into the purse the first time it is read.
        // A new hire starts from nothing ("zero any previous balance", :713); the
        // owner paying more adds to what is left.
        long balance = (owner == null ? 0 : VendorEngine.GetVendorGold(npc)) + gold.Amount;
        VendorEngine.SetVendorGold(npc, balance);
        // NPC_PetSetOwner gives no order: the new hireling is idle, and an idle pet
        // that sees its owner guards it (Character.TryAssignOwnership).
        long daysPaid = balance / Math.Max(1, dayWage);
        // The NPC says how long it is paid for (Speak, :722).
        _client.NpcSpeech(npc, ServerMessages.GetFormatted(Msg.NpcPetHireTime, daysPaid.ToString()));
        gold.RemoveFromWorld(); // consumed into the wage balance
        return true;
    }

    /// <summary>ContentConsume(IT_GOLD) + ContentAttrMod(ATTR_OWNED)
    /// (CCharNPCPet.cpp:757-760): the new hireling's own coins go and everything it
    /// wears or carries becomes owned.</summary>
    private static void MarkHirelingGearOwned(Character npc)
    {
        var stack = new Stack<Item>();
        for (int layer = 0; layer < (int)Layer.Qty; layer++)
            if (npc.GetEquippedItem((Layer)layer) is { } worn)
                stack.Push(worn);
        var golds = new List<Item>();
        while (stack.Count > 0)
        {
            var it = stack.Pop();
            if (it.IsDeleted) continue;
            if (VendorEngine.IsGold(it))
            {
                golds.Add(it);
                continue;
            }
            it.SetAttr(ObjAttributes.Owned);
            foreach (var inner in it.Contents)
                stack.Push(inner);
        }
        foreach (var g in golds)
            g.Delete();
    }

    /// <summary>Gold handed to a trainer after its price (NPC_OnTrainPay,
    /// CCharNPCAct_Vendor.cpp:273): the trainer keeps its fee in its pack, any change
    /// is set down beside the student (UnStackSplit + MoveNearObj), and gold it will
    /// not take goes back.</summary>
    private void PayTrainer(Character trainer, Item offer, Item gold)
    {
        if (_character == null) return;
        int keep = Trade.VendorTrainingEngine.OnTrainPay(trainer, _character, offer, gold,
            line => _client.NpcSpeech(trainer, line));
        if (keep <= 0)
        {
            PlaceItemInPack(_character, gold);
            return;
        }
        if (keep < gold.Amount)
        {
            var change = _world.CreateItem();
            change.CopyStackInstanceStateFrom(gold);
            change.Amount = (ushort)(gold.Amount - keep);
            gold.Amount = (ushort)keep;
            _world.PlaceItemWithDecay(change, _character.Position);
        }
        if (trainer.Backpack is { } pack)
            pack.AddItem(gold);
        else
            gold.Delete();
    }

    private void SendTradeUpdateToBoth(SecureTrade trade) => _client.SendTradeUpdateToBoth(trade);

    /// <summary>Something taken out of a trade window clears both check marks, as
    /// Source-X CItemContainer::OnRemoveObj calls Trade_Status(false) - else an offer
    /// could be emptied after the other side agreed to it.</summary>
    private void WithdrawTradeAccept(Item? container)
    {
        if (container?.ItemType != Core.Enums.ItemType.EqTradeWindow ||
            _tradeManager?.FindByContainer(container.Uid.Value) is not { } trade)
            return;
        if (!trade.InitiatorAccepted && !trade.PartnerAccepted) return;
        trade.ResetAcceptance();
        SendTradeUpdateToBoth(trade);
    }
    private Action<Character, Item, Item>? SendTradeItemToPartner => _client.SendTradeItemToPartner;
    private SpellEngine? _spellEngine => _client.Spells;
    private bool CanSendStatusFor(Character ch) => _client.CanSendStatusFor(ch);
    private void SendSkillList() => _client.SendSkillList();
    private bool InitiateTrade(Character partner, Item? firstItem = null) => _client.InitiateTrade(partner, firstItem);
    private void SendCharacterStatus(Character ch, bool includeExtendedStats = true) => _client.SendCharacterStatus(ch, includeExtendedStats);
    private void BroadcastWorldItem(Item item)
    {
        if (ForEachClientInRange != null)
        {
            ForEachClientInRange(item.Position, UpdateRange, 0, (_, observer) =>
            {
                observer.SendWorldItem(item);
                observer.View.KnownItems.Add(item.Uid.Value);
                observer.View.LastKnownItemState[item.Uid.Value] =
                    (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            });
            return;
        }

        BroadcastNearby?.Invoke(item.Position, UpdateRange,
            _client.BuildWorldItemPacket(item.Uid.Value, item.DispIdFull, item.Amount,
                item.X, item.Y, item.Z, item.Hue, item.Direction), 0);
    }

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
                // ARGO is the clicking client (CClientEvent.cpp:2424).
                var result = _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = _client, O1 = _client as Core.Interfaces.IScriptObj });
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
                    new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = _client, O1 = _client as Core.Interfaces.IScriptObj });
                if (result == TriggerResult.True)
                    return;
            }
        }

        // Overhead name: for characters, the hue follows notoriety so the
        // label reads blue/green/grey/orange/red/yellow. Items stay grey.
        ushort nameHue = 0x03B2;
        if (obj is Character labelCh)
            nameHue = NotoToHue(GetNotoriety(labelCh), labelCh);

        // Containers and corpses append their content summary on single
        // click — "corpse of X (3 items, 25 stones)" (CONT_ITEMS defmessage).
        // Pile items prefix the stack amount ("1234 gold coins") via GetDisplayName.
        string label = obj is Item nameItem ? nameItem.GetDisplayName() : obj.GetName();
        if (obj is Item contItem &&
            contItem.ItemType is ItemType.Container or ItemType.Corpse &&
            contItem.Contents.Count > 0)
        {
            int tenths = 0;
            foreach (var inner in contItem.Contents)
                tenths += inner.TotalWeightTenths;
            int stones = tenths / Item.WeightUnits;
            label += ServerMessages.GetFormatted(Msg.ContItems, contItem.Contents.Count, stones);
        }
        else if (obj is Character guildedCh)
        {
            // A guilded player's overhead name carries the guild abbreviation
            // (e.g. "Lord Yunus [ABC]") when the member keeps it visible.
            label += GuildAbbrevSuffix(guildedCh);
            label += CharNameSuffix(guildedCh, _character.AllShow || _character.DebugView);
        }

        // @AfterClick runs BEFORE the name goes out (addItemName / addCharName,
        // CClientMsg.cpp:1316-1333 and :1443-1460): LOCAL.ClickMsgText and
        // LOCAL.ClickMsgHue carry the line and its hue and are read back, and RETURN 1
        // sends no name at all.
        if (_triggerDispatcher != null && obj is Character or Item)
        {
            var clickLocals = new SphereNet.Scripting.Variables.VarMap();
            clickLocals.Set("ClickMsgText", label);
            clickLocals.SetInt("ClickMsgHue", nameHue);
            var afterArgs = new TriggerArgs
            {
                CharSrc = _character, ScriptConsole = _client, O1 = _client as Core.Interfaces.IScriptObj,
                Locals = clickLocals,
            };
            TriggerResult afterResult;
            if (obj is Character afterClickCh)
            {
                afterResult = _triggerDispatcher.IsCharTriggerUsed(CharTrigger.AfterClick)
                    ? _triggerDispatcher.FireCharTrigger(afterClickCh, CharTrigger.AfterClick, afterArgs)
                    : TriggerResult.Default;
            }
            else
            {
                var afterClickItem = (Item)obj;
                afterArgs.ItemSrc = afterClickItem;
                afterResult = _triggerDispatcher.IsItemTriggerUsed(ItemTrigger.AfterClick)
                    ? _triggerDispatcher.FireItemTrigger(afterClickItem, ItemTrigger.AfterClick, afterArgs)
                    : TriggerResult.Default;
            }
            if (afterResult == TriggerResult.True)
                return;
            label = clickLocals.Get("ClickMsgText") ?? label;
            nameHue = (ushort)clickLocals.GetInt("ClickMsgHue", nameHue);
        }

        _netState.Send(new PacketSpeechUnicodeOut(
            uid, (ushort)(obj is Character c ? c.BodyId : 0),
            6, nameHue, 3, PacketSpeechUnicodeOut.SystemLanguage, "", label));
    }

    /// <summary>The " [ABBR]" suffix appended to a guilded player's overhead name,
    /// or empty when they are unguilded or have hidden their abbreviation.</summary>
    private string GuildAbbrevSuffix(Character ch) =>
        _client.GuildM?.GetAbbrevSuffix(ch.Uid) ?? "";

    /// <summary>The rest of CClient::addCharName (CClientMsg.cpp:1373-1413): an NPC of
    /// the human brain group gets its trade title with VENDORTRADETITLE, and with
    /// CHARTAGS (or an AllShow/debug viewer) the state tags follow.</summary>
    internal static string CharNameSuffix(Character ch, bool allShow)
    {
        var sb = new System.Text.StringBuilder();
        if (!ch.IsPlayer && GameClient.VendorTradeTitle &&
            ch.NpcBrain is >= NpcBrainType.Human and <= NpcBrainType.Stable)
        {
            string title = PaperdollText.GetTradeTitle(ch);
            if (title.Length > 0)
                sb.Append(' ').Append(title);
        }
        if (GameClient.CharTags || allShow)
        {
            if (!ch.IsPlayer)
            {
                if (PaperdollText.IsPlayableBody(ch.BodyId))
                    sb.Append(ServerMessages.Get("charinfo_npc"));
                if (ch.IsStatFlag(StatFlag.Conjured))
                    sb.Append(ServerMessages.Get("charinfo_summoned"));
                else if (ch.IsStatFlag(StatFlag.Pet))
                    sb.Append(ServerMessages.Get(ch.IsBonded ? "charinfo_bonded" : "charinfo_tame"));
            }
            if (ch.IsStatFlag(StatFlag.Invul) && !ch.IsStatFlag(StatFlag.Incognito) && ch.PrivShow)
                sb.Append(ServerMessages.Get("charinfo_invul"));
            if (ch.IsStatFlag(StatFlag.Stone))
                sb.Append(ServerMessages.Get("charinfo_stone"));
            if (ch.IsStatFlag(StatFlag.Freeze))
                sb.Append(ServerMessages.Get("charinfo_frozen"));
            if (ch.IsStatFlag(StatFlag.Insubstantial | StatFlag.Invisible | StatFlag.Hidden))
                sb.Append(ServerMessages.Get("charinfo_hidden"));
            if (ch.IsStatFlag(StatFlag.Sleeping))
                sb.Append(ServerMessages.Get("charinfo_sleeping"));
            if (ch.IsStatFlag(StatFlag.Hallucinating))
                sb.Append(ServerMessages.Get("charinfo_hallu"));
            if (allShow && ch.IsStatFlag(StatFlag.Spawned))
                sb.Append(ServerMessages.Get("charinfo_spawn"));
        }
        return sb.ToString();
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

    /// <summary>True when the character may still reach into their own bank box.
    ///
    /// The rule is upstream's, in full: the box records the point its opener was
    /// standing on (m_itEqBankBox.m_pntOpen = pCharOpener-&gt;GetTopPoint(),
    /// CItemContainer.cpp:1119 - "these are special. they can only be opened near the
    /// designated opener"), and CanTouch refuses the box unless the character is still
    /// on that exact point (CCharStatus.cpp:1067). A box that was never opened carries
    /// no point and is not reachable, which is what upstream's comparison against an
    /// unset point says too.
    ///
    /// There is no second test against the BANKER's distance - upstream has none. The
    /// banker opens the box from as far as it can hear, so asking for three tiles
    /// afterwards refused every deposit made from across the room: an open box that
    /// took nothing, which reads from the client like the gold vanishing.</summary>
    private static bool CanReachOwnBank(Character ch)
    {
        var box = ch.GetEquippedItem(Layer.BankBox);
        if (box == null)
            return false;
        var openedAt = box.MoreP;
        return openedAt.X == ch.X && openedAt.Y == ch.Y &&
               openedAt.Z == ch.Z && openedAt.Map == ch.MapIndex;
    }

    /// <summary>True when <paramref name="item"/>'s top-level container is THIS
    /// character's own bank box (directly or via a nested bag).</summary>
    /// <summary>Source-X CChar::NPC_OnItemGive (CCharNPCAct.cpp:2060) — the
    /// native give flow after @ReceiveItem and the train/hire gold pay:
    /// an owned pet eats offered food and refuses what it cannot carry; a
    /// banker deposits gold into the giver's bank box; dropping goods on a
    /// vendor is a quick-sell offer; everyone else runs the
    /// NPC_WantThisItem gate — an UNWANTED item is refused by default,
    /// @NPCRefuseItem RETURN 1 overrides the refusal (opens the accept
    /// path), and @NPCAcceptItem RETURN 1 cancels the native accept.</summary>
    private void HandleGiveItemToNpc(Character npc, Item item)
    {
        bool isGold = VendorEngine.IsGold(item);

        // Own pet / hireling.
        if (npc.HasOwner(_character!.Uid))
        {
            // Owned VENDOR - NPC_IsVendor(), every vendor brain and not the VENDOR
            // one alone (CCharNPCAct.cpp:2099). Gold from the owner is wage money:
            // the vendor says DEFMSG_NPC_PET_MONEY and it goes into the purse
            // (NPC_OnHirePayMore without fHire, :2101-2110, CCharNPCPet.cpp:705-723).
            bool ownedVendor = VendorEngine.IsVendorLike(npc);
            if (ownedVendor && isGold)
            {
                uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
                if (dayWage > 0)
                {
                    _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetMoney));
                    long purse = VendorEngine.GetVendorGold(npc) + item.Amount;
                    VendorEngine.SetVendorGold(npc, purse);
                    _client.NpcSpeech(npc, ServerMessages.GetFormatted(Msg.NpcPetHireTime,
                        (purse / dayWage).ToString()));
                    _world.RemoveItem(item);
                    _netState.Send(new PacketDropAck());
                    return;
                }
            }

            // Goods go into the vendor stock box (GetBank(LAYER_VENDOR_STOCK), :2114).
            if (ownedVendor && !isGold)
            {
                var stock = npc.GetEquippedItem(Layer.VendorStock);
                if (stock == null)
                {
                    stock = _world.CreateItem();
                    stock.BaseId = 0x0E75;
                    stock.ItemType = ItemType.Container;
                    stock.Name = "Vendor Stock";
                    npc.Equip(stock, Layer.VendorStock);
                }
                if (stock.TryAddItem(item))
                {
                    // The vendor says it (DEFMSG_NPC_PET_SELL, CCharNPCAct.cpp:2113).
                    _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetSell));
                    _netState.Send(new PacketDropAck());
                    return;
                }
            }

            // The pet eats an offered meal on the spot — Food_CanEat honors
            // the chardef FOODTYPE diet (a carnivore refuses an apple).
            bool edible = Character.NpcCanEatFood?.Invoke(npc, item)
                ?? item.ItemType is ItemType.Food or ItemType.Fruit
                    or ItemType.Grain or ItemType.FoodRaw;
            if (edible)
            {
                // The pet eats what it has room for and no more. Source-X hands the
                // offer to Use_EatQty, which refuses outright when the creature is
                // full (CCharUse.cpp:891) and otherwise takes only the units the free
                // space calls for (:894), then NPC_OnItemGive returns the rest to the
                // player (CCharNPCAct.cpp:2119). SphereNet added ten per unit and
                // destroyed the whole stack either way, so a hundred rations dropped
                // on a full pet vanished. It also fired no @Eat, so nothing a shard
                // scripted around feeding ever ran.
                int eaten = SphereNet.Game.NPCs.EatEngine.Eat(
                    npc, item, _triggerDispatcher, item.Amount);
                // The pet itself answers, when it can speak (NPC_OnItemGive,
                // CCharNPCAct.cpp:2121-2131: DEFMSG_NPC_PET_FOOD_TY / _FOOD_NO).
                bool petSpeaks = SphereNet.Game.AI.NpcAI.NpcCanSpeak(npc);
                if (eaten <= 0)
                {
                    if (petSpeaks)
                        _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetFoodNo));
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                if (petSpeaks)
                    _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetFoodTy));
                if (eaten >= item.Amount)
                {
                    _world.RemoveItem(item);
                }
                else
                {
                    item.Amount = (ushort)(item.Amount - eaten);
                    PlaceItemInPack(_character, item);
                }
                _netState.Send(new PacketDropAck());
                return;
            }
            if (!npc.CanCarry(item))
            {
                if (SphereNet.Game.AI.NpcAI.NpcCanSpeak(npc))
                    _client.NpcSpeech(npc, ServerMessages.Get(Msg.NpcPetWeak)); // :2136
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }

            // CANPETSDRINKPOTION — a potion dropped on a pet is drunk on the spot
            // rather than pocketed (Source-X CCharNPCAct.cpp:2145), which is how a
            // player heals a wounded animal mid-fight. Off, or a bottle with no
            // resolvable effect, and it falls through to the pack like any other gift.
            if (item.ItemType == ItemType.Potion && Character.CanPetsDrinkPotion &&
                _client.ApplyPotionEffectTo(npc, item))
            {
                SysMessage("Your pet drinks the potion.");
                if (item.Amount > 1)
                {
                    item.Amount--;
                    PlaceItemInPack(_character, item);
                }
                else
                {
                    _world.RemoveItem(item);
                }
                _netState.Send(new PacketDropAck());
                return;
            }

            PlaceItemInPack(npc, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Banker: gold goes into the GIVER's bank box.
        if (isGold && npc.NpcBrain == NpcBrainType.Banker)
        {
            var bank = _character.GetEquippedItem(Layer.BankBox);
            if (bank == null)
            {
                bank = _world.CreateItem();
                bank.BaseId = 0x09AB; // bank box container graphic
                bank.ItemType = ItemType.EqBankBox;
                bank.Name = "Bank Box";
                _character.Equip(bank, Layer.BankBox);
            }
            // The banker says it, when it can speak (DEFMSG_NPC_BANKER_DEPOSIT,
            // CCharNPCAct.cpp:2174-2179).
            if (SphereNet.Game.AI.NpcAI.NpcCanSpeak(npc))
                _client.NpcSpeech(npc, ServerMessages.GetFormatted(Msg.NpcBankerDeposit, item.Amount.ToString()));
            if (!bank.TryAddItem(item))
                PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Source-X: gold handed to any NON-banker NPC (after the train/hire
        // and pet paths above) is refused outright — a vendor never pockets
        // it, even though it "wants" gold for the ground-pickup score.
        if (isGold)
        {
            // Silently: `return false` with no line (CCharNPCAct.cpp:2184).
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        // Non-pet vendor: a dropped item is a quick-sell offer
        // (Source-X routes it into Event_VendorSell for that one item).
        // NPC_IsVendor() && !STATF_PET (CCharNPCAct.cpp:2187): any vendor brain.
        if (!isGold && VendorEngine.IsVendorLike(npc) && !npc.OwnerSerial.IsValid &&
            !npc.IsStatFlag(StatFlag.Pet))
        {
            // Back into the pack first — the sell transaction consumes it
            // from there like a normal 0x9F response.
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            (_client as GameClient)?.HandleVendorSell(npc.Uid.Value,
                [new SphereNet.Network.Packets.Incoming.VendorSellEntry
                    { ItemSerial = item.Uid.Value, Amount = item.Amount }]);
            return;
        }

        // NPC_WantThisItem gate: an unwanted gift is refused by default;
        // @NPCRefuseItem RETURN 1 overrides the refusal.
        int want = Character.NpcWantThisItem?.Invoke(npc, item) ?? 0;
        if (want <= 0)
        {
            // The refusal is silent unless @NPCRefuseItem is scripted: only then,
            // and only when the script did not RETURN 1, is the giver told the NPC
            // does not want it (DEFMSG_NPC_GENERIC_DONTWANT, CCharNPCAct.cpp:2200-2211).
            bool refuseHooked = _triggerDispatcher?.IsCharTriggerUsed(CharTrigger.NPCRefuseItem) == true;
            var refuse = refuseHooked
                ? _triggerDispatcher!.FireCharTrigger(npc, CharTrigger.NPCRefuseItem,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item })
                : TriggerResult.Default;
            if (refuse != TriggerResult.True)
            {
                if (refuseHooked)
                    SysMessage(ServerMessages.Get(Msg.NpcGenericDontwant));
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // @NPCAcceptItem RETURN 1 cancels the native accept.
        if (_triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCAcceptItem,
                new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item })
            == TriggerResult.True)
        {
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropAck());
            return;
        }

        PlaceItemInPack(npc, item);
        _netState.Send(new PacketDropAck());
    }

    private bool IsInSelfBankBox(Character ch, Item item)
    {
        var top = GetTopContainer(item);
        return top != null && top.EquipLayer == Layer.BankBox && top.ContainedIn == ch.Uid;
    }

    /// <summary>Walk up the containment chain to the nearest enclosing corpse, or
    /// null if the item is not (transitively) inside a corpse. Used by the looting-
    /// crime check so taking an item from a sub-pack inside a corpse still counts.</summary>
    private Item? FindEnclosingCorpse(Item item)
    {
        var current = item;
        for (int i = 0; i < 16 && current.ContainedIn.IsValid; i++)
        {
            var parent = _world.FindItem(current.ContainedIn);
            if (parent == null) break;
            if (parent.ItemType == ItemType.Corpse) return parent;
            current = parent;
        }
        return null;
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

    /// <summary>
    /// The lift triggers of Source-X CChar::ItemPickup (CCharAct.cpp:2967-3007).
    /// Returns false when a script refused the lift.
    ///
    /// A worn item hears no pickup trigger at all: taking it off the layer runs its
    /// @Unequip (upstream's "unequip is done later", through OnRemoveObj). Anything
    /// else hears @PickUp_Pack - taken out of a container or a character's pack - or
    /// @PickUp_Ground, with ARGN1 = the amount being lifted; then the CONTAINER it
    /// comes out of hears @PickUp_Self with the item as ARGO. A partial stack also
    /// hears @PickUp_Stack, fired where the split happens. This used to fire exactly
    /// one of the four on the item - @PickUp_Self for worn items, @PickUp_Stack
    /// alone for a split - so a container's own @PickUp_Self never ran.
    /// </summary>
    private bool FirePickupTriggers(Item item, ushort amount)
    {
        if (_triggerDispatcher == null || item.IsEquipped)
            return true;

        ushort liftAmount = amount > 0 && amount < item.Amount ? amount : item.Amount;
        var trigger = item.ContainedIn.IsValid ? ItemTrigger.PickupPack : ItemTrigger.PickupGround;
        if (_triggerDispatcher.FireItemTrigger(item, trigger,
                new TriggerArgs { CharSrc = _character, ItemSrc = item, N1 = liftAmount })
            == TriggerResult.True)
            return false;

        if (trigger == ItemTrigger.PickupPack && !item.IsDeleted &&
            _world.FindItem(item.ContainedIn) is { } container &&
            _triggerDispatcher.FireItemTrigger(container, ItemTrigger.PickupSelf,
                new TriggerArgs { CharSrc = _character, O1 = item })
            == TriggerResult.True)
            return false;

        return !item.IsDeleted;
    }

    /// <summary>
    /// May this client take <paramref name="item"/> off <paramref name="wearer"/>
    /// (Source-X ItemPickup, CCharAct.cpp:2946)?
    ///
    /// The rule is about OWNERSHIP, not about being a player: staff outrank; an owner
    /// may always reach into their pet's PACK; and CANUNDRESSPETS decides whether the
    /// owner may also strip what the pet is WEARING. Nobody else may do either - the
    /// gate used to read "not another PLAYER", which left every NPC in the world open
    /// to anyone who walked past a stranger's tamed creature.
    /// </summary>
    private bool CanTakeFrom(Character wearer, Item item)
    {
        if (_character == null) return false;
        if (wearer == _character) return true;
        if (_character.PrivLevel >= PrivLevel.GM)
            return _character.PrivLevel > wearer.PrivLevel;
        if (!wearer.HasOwner(_character.Uid))
            return false;
        // Newbie / blessed / cursed things on another creature stay there, even on
        // one's own pet - unless ALLOWNEWBTRANSFER lets the owner take the newbie ones
        // back (CCharStatus.cpp:1641-1664).
        if (item.IsAttr(ObjAttributes.Newbie | ObjAttributes.Blessed2 | ObjAttributes.Cursed | ObjAttributes.Cursed2))
            return item.IsAttr(ObjAttributes.Newbie) && Item.AllowNewbTransfer;
        // Equipped directly on the creature is the half the setting governs; anything
        // in its pack is the owner's to take either way.
        return item.ContainedIn != wearer.Uid || Character.CanUndressPets;
    }

    /// <summary>Fastloot protection window between two pickups, in ms
    /// (CClient::Event_Item_Pickup: m_tNextPickup = now + MSECS_PER_SEC/3,
    /// CClientEvent.cpp:209). Zero disables it (tests, scripted bulk moves).</summary>
    public static int PickupMinIntervalMs = 1000 / 3;

    /// <summary>Monotonic ms before which the next pickup is cancelled
    /// (CClient::m_tNextPickup).</summary>
    private long _nextPickupMs;

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

        // Fastloot / empty-container protection: a pickup that arrives inside the
        // window opened by the previous one is cancelled and does not extend it
        // (CClientEvent.cpp:201-209, 236-237).
        long nowMs = Environment.TickCount64;
        if (PickupMinIntervalMs > 0)
        {
            if (_nextPickupMs > nowMs)
            {
                SendPickupFailed(1);
                return;
            }
            _nextPickupMs = nowMs + PickupMinIntervalMs;
            // A stack of more than one slows the client by another 100 ms
            // (CClientEvent.cpp:236-237; the lifted amount is what ItemPickup returns).
            ushort liftedAmount = amount > 0 && amount < item.Amount ? amount : item.Amount;
            if (liftedAmount > 1) _nextPickupMs += 100;
        }

        if (_housingEngine != null && !_housingEngine.CanPickupHouseItem(_character, item))
        {
            SendPickupFailed(1);
            return;
        }

        // Central flag gate (Source-X CChar::CanMoveItem): ATTR_MOVE_NEVER items
        // (corpses, static furniture) never drag, a frozen mover can't lift, and
        // an equipped cursed item refuses to leave its layer.
        // Dead is already rejected above; housing/distance/looting stay inline.
        if (!ItemMoveRules.CanMove(_character, item, out var moveDenial))
        {
            if (moveDenial == ItemMoveRules.MoveDenial.ItemCursed)
                SysMessage(ServerMessages.Get(Msg.CantmoveCursed));
            else if (moveDenial is ItemMoveRules.MoveDenial.MoverFrozen
                     or ItemMoveRules.MoveDenial.MoverIncapacitated)
                SysMessage(ServerMessages.Get(Msg.CantmoveDead)); // CCharStatus.cpp:1604-1606
            else if (moveDenial == ItemMoveRules.MoveDenial.ItemInTrade)
                SysMessage(ServerMessages.Get(Msg.MsgTradeCantmove)); // CCharStatus.cpp:1627
            SendPickupFailed(1);
            return;
        }

        // Snoop guard: an item may only leave a container this client has actually
        // been shown. Source-X ItemPickup rejects a pickup whose container is not in
        // m_openedContainers (CCharAct.cpp:2895). Without it, knowing a child's uid
        // was enough to reach into a locked chest that had never been opened.
        if (!CanReachInsideContainer(item))
        {
            SendPickupFailed(1);
            return;
        }

        // A client may only hold ONE item on the cursor. Source-X resolves the
        // conflict in CanEquipLayer, which bounces whatever is on LAYER_DRAGGING
        // before the new item takes the layer (CCharStatus.cpp:459). Overwriting the
        // DRAGGING tag and the lift origin instead left the previous item parented
        // to the character with no layer and no drag - present in the world but
        // absent from the pack, the equipment and the cursor alike.
        if (!ResolvePreviousDrag(item))
            return;

        // Stamp the lift origin BEFORE any reparent so a failed drop can bounce the
        // item back where it came from (Source-X). Overwritten on every pickup.
        CaptureDragOrigin(item);

        var (dragSourceSerial, dragSourcePos) = GetDragSource(item);

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
                        // Taking something off somebody else (Source-X ItemPickup,
                        // CCharAct.cpp:2946). The gate was written as "not another
                        // PLAYER", which left every NPC in the world open: anyone could
                        // walk up to a stranger's tamed dragon and lift its armour.
                        //
                        // The rule is about ownership, not about being a player. A GM
                        // outranks; the owner may always reach into their pet's PACK;
                        // and CANUNDRESSPETS decides whether the owner may also strip
                        // what the pet is WEARING. Nobody else may do either.
                        if (wearer != null && wearer != _character && !CanTakeFrom(wearer, item))
                        {
                            SysMessage(ServerMessages.Get(Msg.MsgSteal));
                            SendPickupFailed(1);
                            return;
                        }
                        // Self bank box: reachable while still standing where it was
                        // opened (CClientUse.cpp:46).
                        if (wearer == _character && topCont.EquipLayer == Layer.BankBox &&
                            !CanReachOwnBank(_character))
                        {
                            SendPickupFailed(4); return;
                        }
                        if (wearer != null)
                        {
                            int wDist = _character.Position.GetDistanceTo(wearer.Position);
                            if (wDist > 3) { SendPickupFailed(4); return; }
                        }
                    }
                }
            }

            // ItemPickup asks CanTouch (CCharAct.cpp:2828), which wants the item's
            // top-level object on this map and in line of sight (CCharStatus.cpp:
            // 1415-1422), not merely within reach: the tile count alone let items be
            // lifted through walls and floors, or off another facet at the same x,y.
            // Same touch rule the double-click uses, CAN_I/C_DCIGNORELOS honoured.
            if (!_client.ItemUse.CanTouchItem(item)) { SendPickupFailed(4); return; }
        }

        // Looting crime (Source-X CheckCorpseCrime): taking an item out of another
        // player's corpse — when the owner is still present and innocent toward the
        // looter — flags the looter criminal. IsLootingCriminal already exempts the
        // own/party/guild/criminal-owner and deleted-owner (NPC corpse) cases.
        if (_character.PrivLevel < PrivLevel.GM && _client.DeathEng is { } deathEng)
        {
            var lootCorpse = FindEnclosingCorpse(item);
            if (lootCorpse != null && deathEng.IsLootingCriminal(_character, lootCorpse))
                deathEng.ReportCorpseCrime(_character, lootCorpse);
        }

        // Too heavy to even drag (Source-X CCharAct.cpp:2932). DRAGWEIGHTMAX is a
        // percent of carry weight measured on the load the lift WOULD produce, so the
        // item that would cross the line is the one refused rather than the one after
        // it. Below zero means no limit.
        //
        // The escape hatch matters as much as the rule: if the item is in the lifter's
        // OWN pack it falls to their feet instead of being refused, or an overloaded
        // player could never put anything down again.
        if (_character.PrivLevel < PrivLevel.GM && Item.DragWeightMax > 0)
        {
            ushort liftAmount = amount > 0 && amount < item.Amount ? amount : item.Amount;
            long liftTenths = item.Amount > 0
                ? (long)item.TotalWeightTenths * liftAmount / item.Amount
                : item.TotalWeightTenths;

            if (_character.GetWeightLoadPercent(liftTenths) > Item.DragWeightMax)
            {
                var ownPack = _character.Backpack;
                bool fromOwnPack = ownPack != null && item.ContainedIn == ownPack.Uid;
                if (fromOwnPack)
                {
                    SendPickupFailed(0);
                    _world.PlaceItem(item, _character.Position);
                }
                else
                {
                    SendPickupFailed(1);
                }
                SysMessage(ServerMessages.Get(Msg.MsgHeavy));
                return;
            }
        }

        // The pickup triggers, in upstream's order (CChar::ItemPickup,
        // CCharAct.cpp:2967-3020), once every reach/ownership/weight gate has passed.
        if (!FirePickupTriggers(item, amount))
        {
            SendPickupFailed(1);
            return;
        }

        // Stack splitting: the client keeps dragging the serial it clicked.
        // Source-X/ServUO reduce that original item to the lifted amount and
        // create a new leftover stack at the old location/container.
        if (amount > 0 && amount < item.Amount && item.Amount > 1)
        {
            ushort originalAmount = item.Amount;
            ushort remainderAmount = (ushort)(originalAmount - amount);
            var sourceContainer = item.ContainedIn.IsValid ? _world.FindItem(item.ContainedIn) : null;
            var sourcePos = item.Position;

            // The left-behind remainder must be a full clone of the original
            // (tags, attributes, durability, price/link, timers, TDATA), not just
            // id/hue/more — otherwise that state is lost from the leftover stack.
            var remainder = _world.CreateItem();
            remainder.CopyStackInstanceStateFrom(item);
            remainder.Amount = remainderAmount;

            item.Amount = amount;

            if (sourceContainer != null)
            {
                sourceContainer.AddItem(remainder);
                remainder.Position = sourcePos;
                SendContainerItemPacket(new PacketContainerItem(
                    remainder.Uid.Value, remainder.DispIdFull, 0,
                    remainder.Amount, remainder.X, remainder.Y,
                    sourceContainer.Uid.Value, remainder.Hue,
                    _netState.IsClientPost6017));
            }
            else
            {
                _world.PlaceItemWithDecay(remainder, sourcePos);
                BroadcastWorldItem(remainder);
            }

            // @PickUp_Stack fires IN ADDITION to the pack/ground trigger, on the
            // lifted pile, with the left-behind pile as ARGO (CCharAct.cpp:3014-3020).
            // RETURN 1 keeps the pile where it was: the split is undone and the lift
            // refused. (Upstream returns 0 there, which leaves the split half-done
            // with the client dragging a pile that never reached the cursor.)
            if (_triggerDispatcher != null &&
                _triggerDispatcher.FireItemTrigger(item, ItemTrigger.PickupStack,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = remainder })
                == TriggerResult.True)
            {
                if (!remainder.IsDeleted)
                {
                    uint remainderUid = remainder.Uid.Value;
                    _world.RemoveItem(remainder);
                    _netState.Send(new PacketDeleteObject(remainderUid));
                }
                if (!item.IsDeleted)
                    item.Amount = originalAmount;
                SendPickupFailed(1);
                return;
            }

            if (sourceContainer != null)
            {
                sourceContainer.RemoveItem(item);
                WithdrawTradeAccept(sourceContainer);
            }
            else
                _world.GetSector(sourcePos)?.RemoveItem(item);

            item.ContainedIn = _character.Uid;
            // Picking something up ends its rot: upstream calls SetDecayTime(-1) at the
            // end of the pickup (CCharAct.cpp:3064), which clears the timer and the
            // attribute - and leaves a genuine script timer alone. The item used to
            // carry its old ground decay into the backpack.
            item.SetDecayTime(-1);
            _character.SetTag("DRAGGING", item.Uid.Value.ToString());
            BroadcastDragAnimation(item, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);
            return;
        }

        if (item.IsEquipped)
        {
            var owner = _world.FindChar(item.ContainedIn);
            // The same ownership question as the container branch above, asked once.
            // This arm used to refuse every non-self wearer outright, so an owner could
            // not undress their own pet however CANUNDRESSPETS was set - two gates on
            // one rule, disagreeing with each other.
            if (owner != null && owner != _character && !CanTakeFrom(owner, item))
            {
                SysMessage(ServerMessages.Get(Msg.MsgSteal));
                SendPickupFailed(1); // cannot pick up
                return;
            }
            var unequipOwner = owner;
            if (owner != null) UnequipWithNotification(owner, item.EquipLayer);
            if (item.IsDeleted) { SendPickupFailed(1); return; }
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
            WithdrawTradeAccept(container);
        }
        else
        {
            var sector = _world.GetSector(item.Position);
            sector?.RemoveItem(item);
        }

        item.ContainedIn = _character.Uid;
        item.SetDecayTime(-1);
        _character.SetTag("DRAGGING", serial.ToString());
        BroadcastDragAnimation(item, dragSourceSerial, dragSourcePos, 0, _character.Position, dragSourcePos);

        // Lifting something makes a sound (CClientEvent.cpp:239-240). It goes to the
        // lifter alone: upstream sends it with addSound on the client that picked the
        // item up, not to the neighbourhood the way a drop does.
        _netState.Send(new PacketSound(item.GetPickupSound(),
            _character.X, _character.Y, _character.Z));

        if (VendorEngine.IsGold(item))
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

    // === Source-X drag bounce-to-origin (CClientEvent pickup origin) ===

    /// <summary>Lift origin for the item currently being dragged. Held as
    /// transient per-client state (one drag at a time) rather than item tags —
    /// tags would pollute the item's tag set and break stack-merge equality.</summary>
    private enum DragOriginKind : byte { Pack = 0, Container = 1, Ground = 2 }
    private readonly record struct DragOrigin(DragOriginKind Kind, uint Parent, short X, short Y, sbyte Z);
    /// <summary>
    /// Whether a drop target can legitimately hold children. Anything that is not a
    /// container is a plain item, however the client addressed it.
    /// </summary>
    /// <summary>Is this drop landing IN something, rather than on top of it?
    ///
    /// This used to carry its own list of container types, and it was the shorter of the
    /// two: it did not name the ship hold, the trash can, the keyring or the game board,
    /// all of which <see cref="Item.IsContainerItemType"/> does and all of which upstream
    /// builds as a CItemContainer. A drop aimed at one of them was therefore redirected
    /// as if it had been aimed at a plain item - onto the tile the thing stands on. That
    /// is a chest on a ship's deck refusing everything and leaving it lying on the deck,
    /// which is how a shard reported it, and a trash can you cannot throw anything into.
    ///
    /// Spellbooks have a separate scroll-learning path, never physical contents.</summary>
    private static bool IsDropTargetContainer(Item target) =>
        Item.IsContainerItemType(target.ItemType) ||
        target.ItemType == ItemType.EqMemoryObj ||
        target.EquipLayer is Layer.Pack or Layer.BankBox or
                            Layer.VendorStock or Layer.VendorExtra;

    /// <summary>
    /// Resolve a drop aimed at a plain item to the place that item actually lives
    /// (Source-X CClientEvent.cpp:504) - the container holding it, or its ground
    /// tile. Returns null to mean "put it on the ground", with the coordinates
    /// updated to the target's tile.
    /// </summary>
    private Item? RedirectNonContainerTarget(Item target, Item item, ref short x, ref short y)
    {
        var parent = target.ContainedIn.IsValid ? _world.FindItem(target.ContainedIn) : null;
        if (parent != null && !ReferenceEquals(parent, item))
        {
            // Land next to the target inside its own container.
            x = target.X;
            y = target.Y;
            return parent;
        }

        var pos = target.GetTopLevelPosition();
        x = pos.X;
        y = pos.Y;
        return null;   // ground drop at the target's tile
    }

    /// <summary>
    /// Whether this client is allowed to take <paramref name="item"/> out of
    /// whatever holds it. Items on the ground, worn, or held by the character
    /// itself are not container pickups and pass straight through.
    /// </summary>
    private bool CanReachInsideContainer(Item item)
    {
        if (_character == null) return false;
        if (_character.PrivLevel >= PrivLevel.GM) return true;
        if (item.IsEquipped || !item.ContainedIn.IsValid) return true;

        var container = _world.FindItem(item.ContainedIn);
        if (container == null) return true;   // held by a character, not a container

        var topMost = container.ResolveTopObject();
        bool topIsCharacter = topMost is Character;

        // The character's own pack and bank are always reachable: the client is
        // never sent an "open" for its own backpack on every login, and Source-X
        // treats a pickup whose top-level object is a character as legitimate.
        if (topIsCharacter && ReferenceEquals(topMost, _character))
            return true;

        return _client.OpenedContainers.IsOpen(container, topMost, topIsCharacter);
    }

    private DragOrigin? _dragOrigin;

    /// <summary>
    /// Settle whatever is already on the cursor before a new pickup takes it over.
    /// Returns false when the new request should be dropped entirely (it is a repeat
    /// of the item already being dragged, so there is nothing left to do).
    /// </summary>
    private bool ResolvePreviousDrag(Item incoming)
    {
        if (_character == null) return true;
        if (!_character.TryGetTag("DRAGGING", out string? raw) ||
            !ScriptNumber.TryParseUInt(raw, out uint heldUid) || heldUid == 0)
            return true;

        // Same item twice: Source-X ItemPickup returns early rather than restarting
        // the drag, so the origin captured by the first pickup survives.
        if (heldUid == incoming.Uid.Value)
        {
            SendPickupFailed(0);
            return false;
        }

        _character.RemoveTag("DRAGGING");

        var held = _world.FindItem(new Serial(heldUid));
        if (held is { IsDeleted: false })
            RestoreToOrigin(held);   // consumes and clears _dragOrigin

        _dragOrigin = null;
        return true;
    }

    /// <summary>
    /// Snapshot where an item is being lifted FROM so a failed drop can bounce it
    /// back to that container slot / ground tile (Source-X stores the prior parent
    /// + position), not just the backpack. Overwritten on every pickup, so it
    /// can't go stale — a drop only follows the pickup that just set it.
    /// </summary>
    private void CaptureDragOrigin(Item item)
    {
        if (item.IsEquipped)
            // Equip-origin bounces to the pack: re-equipping on the failure path
            // would re-fire @EquipTest / hand-conflict logic.
            _dragOrigin = new DragOrigin(DragOriginKind.Pack, 0, 0, 0, 0);
        else if (item.ContainedIn.IsValid && _world.FindItem(item.ContainedIn) != null)
            _dragOrigin = new DragOrigin(DragOriginKind.Container, item.ContainedIn.Value, item.X, item.Y, 0);
        else
            _dragOrigin = new DragOrigin(DragOriginKind.Ground, 0, item.X, item.Y, item.Z);
    }

    /// <summary>
    /// Bounce a failed drop back to its lift origin — the original container slot
    /// or ground tile — and send the matching client update. Falls back to the
    /// backpack when the origin is gone, full, or was an equip layer. Mirrors
    /// PlaceItemInPack's reparent + 0x25, but targets the origin.
    /// </summary>
    private void RestoreToOrigin(Item item)
    {
        if (_character == null) return;
        var origin = _dragOrigin;
        _dragOrigin = null;

        if (origin is { Kind: DragOriginKind.Container } co)
        {
            var originCont = _world.FindItem(new Serial(co.Parent));
            // The item was a leaf of this container moments ago, so there is room
            // and no cycle; guard only against a deleted/full origin.
            if (originCont != null && !originCont.IsDeleted &&
                _world.GetContainerContents(originCont.Uid).Count() < Item.MaxContainerItems)
            {
                originCont.AddItem(item);
                item.Position = new Point3D(co.X, co.Y, 0, _character.MapIndex);
                SendContainerItemPacket(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0, item.Amount,
                    co.X, co.Y, originCont.Uid.Value, item.Hue, _netState.IsClientPost6017));
                return;
            }
        }
        else if (origin is { Kind: DragOriginKind.Ground } go)
        {
            var pos = new Point3D(go.X, go.Y, go.Z, _character.MapIndex);
            if (_world.GetSector(pos) != null)
            {
                _world.PlaceItemWithDecay(item, pos);
                BroadcastWorldItem(item);
                return;
            }
        }

        PlaceItemInPack(_character, item);
    }

    /// <summary>
    /// Fire @DropOn_Self on <paramref name="receiver"/> - the container, pile or item
    /// something was dropped on - with the dragged <paramref name="item"/> as ARGO
    /// (Source-X Event_Item_Drop, CClientEvent.cpp:435-446). Returns true when the
    /// drop is settled and the caller must stop: RETURN 1 bounces the item back to
    /// where it was lifted from, unless the script already put it somewhere else, in
    /// which case it stays there; a script that deleted the item ends the drop.
    /// </summary>
    private bool FireDropOnSelf(Item receiver, Item item)
    {
        if (_triggerDispatcher == null || _character == null) return false;
        var result = _triggerDispatcher.FireItemTrigger(receiver, ItemTrigger.DropOnSelf,
            new TriggerArgs { CharSrc = _character, O1 = item });
        if (item.IsDeleted)
        {
            _dragOrigin = null;
            _netState.Send(new PacketDropAck());
            return true;
        }
        if (result != TriggerResult.True)
            return false;

        // Still on the cursor (parented to the dragger, on no layer) means the
        // script left it alone: bounce it (Event_Item_Drop_Fail).
        bool stillDragged = item.ContainedIn == _character.Uid && !item.IsEquipped;
        if (stillDragged)
        {
            RestoreToOrigin(item);
            _netState.Send(new PacketDropReject());
        }
        else
        {
            _dragOrigin = null;
            _netState.Send(new PacketDropAck());
        }
        return true;
    }

    /// <summary>
    /// Fire @DropOn_Item on the dragged <paramref name="item"/> with ARGO = what it
    /// was dropped on (Source-X Event_Item_Drop, CClientEvent.cpp:418-431). Returns
    /// true when the drop is settled and the caller must stop: RETURN 1 bounces the
    /// item only while it is still on the cursor (Event_Item_Drop_Fail,
    /// CClientEvent.cpp:253), and a script that moved the item elsewhere ends the drop
    /// there - upstream compares the container before and after and returns (:430).
    /// </summary>
    private bool FireDropOnItem(Item item, Item droppedOn)
    {
        if (_triggerDispatcher == null || _character == null) return false;
        var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
            new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = droppedOn });
        if (item.IsDeleted)
        {
            _dragOrigin = null;
            _netState.Send(new PacketDropAck());
            return true;
        }
        bool stillDragged = item.ContainedIn == _character.Uid && !item.IsEquipped;
        if (result == TriggerResult.True && stillDragged)
        {
            RestoreToOrigin(item);
            _netState.Send(new PacketDropReject());
            return true;
        }
        if (result == TriggerResult.True || !stillDragged)
        {
            _dragOrigin = null;
            _netState.Send(new PacketDropAck());
            return true;
        }
        return false;
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

    /// <summary>
    /// Item-count and weight limits for a drop into <paramref name="container"/>,
    /// asked of the target and then of every container it sits in, up to the worn
    /// pack / bank box (Source-X Event_Item_Drop walks CanContainerHold up the
    /// chain, CClientEvent.cpp:359-405). Returns the refusal message key, or null
    /// when every container on the way up has room.
    /// </summary>
    private string? ContainerChainRefusal(Item container, Item item)
    {
        var topContainer = GetTopContainer(container) ?? container;
        bool targetInBank = topContainer.EquipLayer == Layer.BankBox;

        int guard = 0;
        for (var link = container; link != null && guard < 16;
             link = link.ContainedIn.IsValid ? _world.FindItem(link.ContainedIn) : null, guard++)
        {
            bool isTarget = guard == 0;
            bool linkIsBank = link.IsEquipped && link.EquipLayer == Layer.BankBox;
            bool linkIsPack = link.IsEquipped && link.EquipLayer == Layer.Pack;
            if (link.IsEquipped && !linkIsBank && !linkIsPack) break;   // not a container the chain climbs through

            // Direct contents cap, applied to every container including the bank
            // box (CItemContainer.cpp:897-903). OVERRIDE.MAXITEMS replaces the
            // global default for THIS container (e.g. a small pouch, a vendor crate).
            int directMax = _world.MaxContainerItems;
            if (link.TryGetTag("OVERRIDE.MAXITEMS", out string? maxItemsRaw) &&
                ScriptNumber.TryParseInt(maxItemsRaw, out int overrideMax) && overrideMax >= 0)
                directMax = overrideMax;
            if (link.ContentCount >= directMax)
                return Msg.ContFullItems;

            // A drop anywhere in the bank tree (the box itself or a nested bag)
            // counts against the bank cap, computed over the WHOLE tree from the
            // bank root so nested bags can't bypass the limit. The incoming
            // container's children count too (CItemContainer.cpp:941-955).
            if (isTarget && targetInBank)
            {
                int incomingChildren = item.ContentCount > 0
                    ? _world.GetContainerItemCountDeep(item.Uid) : 0;
                if (_world.GetContainerItemCountDeep(topContainer.Uid) + incomingChildren >= _world.MaxBankItems)
                    return Msg.BvboxFullItems;
            }

            // Weight. A container's own MODMAXWEIGHT is its limit, and the reference
            // has no other for an ordinary chest (CItemContainer.cpp:906); the
            // configured value is a shard's optional global for the target only.
            // A worn pack adds what its owner can carry plus BACKPACKOVERLOAD
            // (:907-914); the bank box adds its MODMAXWEIGHT to the bank cap
            // (:962-963).
            int weightLimit;
            if (linkIsBank)
            {
                weightLimit = _world.MaxBankWeight > 0 ? _world.MaxBankWeight + link.ModMaxWeight : 0;
            }
            else if (linkIsPack)
            {
                weightLimit = link.ModMaxWeight;
                if (Item.BackpackOverload >= 0 && _world.FindChar(link.ContainedIn) is { } packOwner)
                    weightLimit += packOwner.MaxWeight + Item.BackpackOverload;
            }
            else if (isTarget && targetInBank)
            {
                weightLimit = _world.MaxBankWeight;
            }
            else
            {
                weightLimit = link.ModMaxWeight > 0 ? link.ModMaxWeight
                    : isTarget ? _world.MaxContainerWeight : 0;
            }
            if (weightLimit > 0)
            {
                int totalWeightTenths = 0;
                foreach (var b in _world.GetContainerContents(link.Uid))
                    totalWeightTenths += b.TotalWeightTenths;
                if (totalWeightTenths + item.TotalWeightTenths > weightLimit * Item.WeightUnits)
                    return (linkIsBank || (isTarget && targetInBank)) ? Msg.BvboxFullWeight : Msg.ContFullWeight;
            }
        }
        return null;
    }

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        bool isDragTag = _character.TryGetTag("DRAGGING", out var dragTag) && dragTag == serial.ToString();

        // The drop is of the item on LAYER_DRAGGING and of nothing else: upstream
        // looks it up there and cancels the drag when it is not, or when the item is
        // gone (Event_Item_Drop, CClientEvent.cpp:283-291). Two cases went past that.
        // An item that no longer exists - a dragged pile sold whole to a vendor -
        // returned without a word, so the client kept it on the cursor. And an item
        // that had left the cursor while the drag was still recorded - the same sale
        // to a player's vendor, which keeps what it buys - was dropped wherever the
        // packet said: out of the vendor's box and onto the ground at the seller's
        // feet, with the price already paid.
        if (item == null || (isDragTag && (item.ContainedIn != _character.Uid || item.IsEquipped)))
        {
            if (isDragTag)
            {
                _character.RemoveTag("DRAGGING");
                _dragOrigin = null;
            }
            // The cancel makes the client put the item back where it lifted it from;
            // the removal that follows takes that picture away again.
            _netState.Send(new PacketDropReject());
            _netState.Send(new PacketDeleteObject(serial));
            return;
        }

        if (!isDragTag)
        {
            _netState.Send(new PacketDropReject());
            return;
        }

        // Symmetric with HandleItemPickup, and with Source-X CanTouch, which refuses
        // a dead character every item that is not death-immune. Death now settles any
        // held item into the pack before the corpse is filled, so a ghost arriving
        // here is a stale client-side drag rather than a legitimate move.
        if (_character.IsDead)
        {
            _character.RemoveTag("DRAGGING");
            RestoreToOrigin(item);
            _netState.Send(new PacketDropReject());
            return;
        }

        _character.RemoveTag("DRAGGING");

        // A game piece goes nowhere but a game board (Event_Item_Drop,
        // CClientEvent.cpp:556-570: any other drop puts it back on its board). It
        // could be carried off to the ground, a pack or another container.
        if (item.ItemType == ItemType.GamePiece && _character.PrivLevel < PrivLevel.GM)
        {
            var on = containerUid is not 0 and not 0xFFFFFFFF ? _world.FindItem(new Serial(containerUid)) : null;
            bool onBoard = on != null && (on.ItemType == ItemType.GameBoard ||
                (on.ContainedIn.IsValid && _world.FindItem(on.ContainedIn)?.ItemType == ItemType.GameBoard));
            if (!onBoard)
            {
                RestoreToOrigin(item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        // A quest item may only go into its holder's own backpack or onto a trash can,
        // which deletes it; any other drop is refused and the item bounces, unless the
        // dropper is staff (CClientEvent.cpp:295-310).
        if (item.IsAttr(ObjAttributes.QuestItem))
        {
            var questOn = containerUid is not 0 and not 0xFFFFFFFF
                ? _world.FindItem(new Serial(containerUid)) : null;
            if (questOn?.ItemType == ItemType.TrashCan)
            {
                var canPos = questOn.GetTopLevelPosition();
                _netState.Send(new PacketSound(item.GetDropSound(ontoSomething: true),
                    canPos.X, canPos.Y, canPos.Z));
                _netState.Send(new PacketDropAck());
                _client.BroadcastDeleteObject(item.Uid.Value);
                _world.DeleteObject(item);
                return;
            }
            if (_character.PrivLevel < PrivLevel.GM &&
                (questOn == null || questOn != _character.Backpack))
            {
                SysMessage(ServerMessages.Get(Msg.ItemCantdroptrade));
                RestoreToOrigin(item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        // A plain item lying on the ground that the drop was aimed at: the drop lands
        // on its tile, but that item still hears @DropOn_Self first.
        Item? groundDropOnTarget = null;

        if (containerUid != 0 && containerUid != 0xFFFFFFFF)
        {
            var container = _world.FindItem(new Serial(containerUid));
            // The object the client dropped ON (Source-X pObjOn / pItemOn). The
            // redirect below may swap `container` for the place a plain item lives,
            // but @DropOn_Item's ARGO and the @DropOn_Self receiver stay this item
            // (CClientEvent.cpp:421-446 fire both before the redirect at :504).
            var dropOnTarget = container;

            // Source-X Event_Item_Drop (CClientEvent.cpp:489) branches on whether the
            // target really is a container. A plain item is NOT one: the drop is
            // redirected to wherever that item lives, after the stack-merge and
            // special-target cases below have had their chance. Passing it to
            // TryAddItem instead turned a sword into a container, and the gem inside
            // it could not be reached through any container view again.
            if (container != null && !container.IsSpellbook && !IsDropTargetContainer(container) &&
                !container.CanStackWith(item))
            {
                container = RedirectNonContainerTarget(container, item, ref x, ref y);
                if (container == null)
                    groundDropOnTarget = dropOnTarget;
            }

            if (container != null && _tradeManager?.FindByContainer(container.Uid.Value) is { } dropTrade)
            {
                if (!dropTrade.IsParticipant(_character))
                {
                    RestoreToOrigin(item);
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
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                var myCont = dropTrade.GetOwnContainer(_character);
                if (!myCont.TryAddItem(item))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                item.Position = new Point3D(30, 30, 0, _character.MapIndex);
                dropTrade.ResetAcceptance();
                SendTradeUpdateToBoth(dropTrade);
                SendContainerItemPacket(new PacketContainerItem(
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
                // The self pack is always reachable; the self bank box only while near a
                // banker; another player's pack/bank never; a world container within 3 tiles.
                // Without this a crafted packet can move items into distant containers the
                // client happened to open earlier.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    if (IsInSelfBankBox(_character, container))
                    {
                        // Self bank box (direct or via a nested bag): reachable while
                        // the character still stands where the box was opened
                        // (CClientUse.cpp:46).
                        //
                        // The refusal says so rather than being silent, which is the
                        // worst way to lose a deposit: the item came back to the pack
                        // and nothing said why, so it read as the gold vanishing.
                        if (!CanReachOwnBank(_character))
                        {
                            SysMessage(ServerMessages.Get(Msg.ItemuseToofar));
                            RestoreToOrigin(item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                    else
                    {
                        // Resolve the ROOT of the target chain, not just the layer of
                        // the container the client named. Checking only a direct
                        // Pack/BankBox layer meant a bag nested inside another
                        // player's backpack fell through to the distance branch,
                        // which does not apply to a character-held container - so an
                        // item could be pushed straight into someone else's
                        // inventory, bypassing the secure-trade flow Source-X routes
                        // it to (CClientEvent.cpp:338).
                        var topContainer = GetTopContainer(container) ?? container;
                        var rootOwner = topContainer.ContainedIn.IsValid
                            ? _world.FindChar(topContainer.ContainedIn)
                            : null;

                        if (rootOwner != null && rootOwner != _character)
                        {
                            // Someone else's inventory (or their pet's). Handing an
                            // item over needs their consent.
                            if (!rootOwner.HasOwner(_character.Uid))
                            {
                                RestoreToOrigin(item);
                                _netState.Send(new PacketDropReject());
                                return;
                            }
                            // Put into a container carried by an NPC I own, the item
                            // is no longer anyone's: ATTR_OWNED goes, and ATTR_NEWBIE
                            // with it unless ALLOWNEWBTRANSFER (CClientEvent.cpp:351-357).
                            // This branch is the only place upstream does that.
                            if (!rootOwner.IsPlayer)
                            {
                                item.ClearAttr(ObjAttributes.Owned);
                                if (!Item.AllowNewbTransfer)
                                    item.ClearAttr(ObjAttributes.Newbie);
                            }
                        }
                        else if (rootOwner == null && !topContainer.ContainedIn.IsValid)
                        {
                            int cDist = _character.Position.GetDistanceTo(topContainer.Position);
                            if (cDist > 3)
                            {
                                RestoreToOrigin(item);
                                _netState.Send(new PacketDropReject());
                                return;
                            }
                        }

                        // "CanTouch(pObjOn) // Must also be LOS" (CClientEvent.cpp:314):
                        // a world container through a wall, and a pet's pack from
                        // anywhere in the world, were both accepted on reach alone.
                        if (!_client.ItemUse.CanTouchItem(container))
                        {
                            RestoreToOrigin(item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                }

                // FEATURE_TOL_VIRTUALGOLD: gold put in one's own bank becomes virtual
                // gold (Event_Item_Drop, CClientEvent.cpp:362) - credited, announced,
                // and the coins are gone.
                if (Trade.VirtualGold.Enabled && item.ItemType == ItemType.Gold &&
                    IsInSelfBankBox(_character, container))
                {
                    int deposited = item.Amount;
                    Trade.VirtualGold.Add(_character, deposited);
                    SysMessage(ServerMessages.GetFormatted(Msg.BvboxDeposited, deposited));
                    _netState.Send(new PacketSound(0x02E6, _character.X, _character.Y, _character.Z));
                    _netState.Send(new PacketDropAck());
                    _client.BroadcastDeleteObject(item.Uid.Value);
                    _world.DeleteObject(item);
                    return;
                }

                // When the "container" is actually a matching stackable pile, this
                // drop is a MERGE, not an insert into a container. Source-X does not
                // gate a pile merge by the destination's item-count / weight / nesting
                // budget (the pile just grows up to GetMaxAmount) — those container
                // caps only apply when dropping a distinct item into a container slot.
                // Applying MaxContainerWeight to a ground pile wrongly rejected large
                // stacks (e.g. dropping 5000 gold — 500 stones — onto a ground pile
                // when the container weight cap is 400).
                bool isPileMerge = container.CanStackWith(item);
                bool isSpellbook = container.IsSpellbook;

                // What a special container holds (CanContainerHold, CItemContainer.cpp:
                // 977-1001): a game board only game pieces, a bulletin board nothing
                // by drop, a key ring only keys that are cut to a lock.
                if (!isPileMerge && _character.PrivLevel < PrivLevel.GM)
                {
                    string? holdRefusal = container.ItemType switch
                    {
                        ItemType.GameBoard when item.ItemType != ItemType.GamePiece => Msg.MsgErrNotgamepiece,
                        ItemType.BBoard => "",
                        ItemType.Keyring when item.ItemType != ItemType.Key => Msg.MsgErrNotkey,
                        ItemType.Keyring when item.More1 == 0 => Msg.MsgErrNoblankring,
                        _ => null,
                    };
                    // Unless the shard allows containers in containers, a container that
                    // is not worn refuses one whose definition is as big or bigger
                    // ("can't put barrels in barrels"): volume is the definition weight in
                    // stones (CItemContainer.cpp:922-929, CItemBase.h:350). A worn pack is
                    // exempt.
                    if (holdRefusal == null &&
                        (GameClient.ServerOptionFlags & OptionFlags.AllowContainerInsideContainer) == 0 &&
                        !container.IsEquipped && item.IsContainerType &&
                        item.DefinitionWeightRaw / Item.WeightUnits >= container.DefinitionWeightRaw / Item.WeightUnits)
                        holdRefusal = Msg.ContToosmall;
                    if (holdRefusal != null)
                    {
                        if (holdRefusal.Length > 0)
                            SysMessage(ServerMessages.Get(holdRefusal));
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }

                // Nesting depth limit — prevent container-in-container bypass of slot limits.
                if (!isPileMerge && !isSpellbook && _character.PrivLevel < PrivLevel.GM)
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
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }

                if (!isPileMerge && !isSpellbook && _character.PrivLevel < PrivLevel.GM)
                {
                    string? chainRefusal = ContainerChainRefusal(container, item);
                    if (chainRefusal != null)
                    {
                        SysMessage(ServerMessages.Get(chainRefusal));
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }

                if (item.Uid == container.Uid || IsInsideContainer(container, item.Uid))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }

                // Fire @DropOn_Item
                if (FireDropOnItem(item, dropOnTarget ?? container))
                    return;
                // @DropOn_Self on the RECEIVER - the container, pile or item the drop
                // landed on - with the dragged item as ARGO.
                if (FireDropOnSelf(dropOnTarget ?? container, item))
                    return;
                if (isSpellbook)
                {
                    // Source-X AddSpellbookScroll consumes ONE and bounces any
                    // remainder. A book must never become a hidden container.
                    var spell = _spellEngine?.GetScrollSpell(item);
                    if (spell == null || !container.TryLearnSpell((int)spell.Id))
                    {
                        SysMessage(ServerMessages.Get(Msg.CantAddSpellbook));
                        RestoreToOrigin(item);
                    }
                    else
                    {
                        ulong bits = ((ulong)container.More2 << 32) | container.More1;
                        _netState.Send(new PacketSpellbookContent(container.Uid.Value,
                            container.DispIdFull, (ushort)(container.SpellbookOffset + 1), bits));
                        if (item.Amount > 1)
                        {
                            item.Amount--;
                            RestoreToOrigin(item);
                        }
                        else
                        {
                            _world.RemoveItem(item);
                            _dragOrigin = null;
                            var soundPos = container.GetTopLevelPosition();
                            _netState.Send(new PacketSound(0x057, soundPos.X, soundPos.Y, soundPos.Z));
                        }
                    }
                    _netState.Send(new PacketDropReject());
                    return;
                }
                // Dropping one stack onto another: the client sends the TARGET
                // STACK's serial as the "container". If that target is itself a
                // stackable item that matches, MERGE the amounts instead of
                // nesting the dragged item inside the stack — nesting made the
                // dropped pile a hidden child of the target and it silently
                // vanished (the "stack gold onto gold and 10k disappears" bug).
                if (isPileMerge)
                {
                    // A target stack on the ground is a world object, not a
                    // container child: its amount update must go out as a world
                    // item broadcast (0x1A), not a container-content packet
                    // (0x25) addressed to itself. Sending 0x25 with parent ==
                    // the item's own serial desynced the ground pile's label.
                    bool targetOnGround = !container.ContainedIn.IsValid;
                    uint stackParent = container.ContainedIn.IsValid
                        ? container.ContainedIn.Value : container.Uid.Value;
                    // Cap the merge at the target's effective stack limit
                    // (per-item MAXAMOUNT override or ITEMSMAXAMOUNT, Source-X
                    // GetMaxAmount), not the raw ushort ceiling — otherwise a pile
                    // grows past the configured/per-item cap. The other two merge
                    // sites (Item.TryAddItemWithStack, ground-drop merge) already
                    // use MaxAmount.
                    int room = container.MaxAmount - container.Amount;
                    if (room < 0) room = 0;
                    int originalAmount = item.Amount;
                    int moved = Math.Min(room, originalAmount);
                    int remaining = originalAmount - moved;
                    if (moved > 0)
                    {
                        container.Amount = (ushort)(container.Amount + moved);
                        if (targetOnGround)
                            BroadcastWorldItem(container);
                        else
                            SendContainerItemPacket(new PacketContainerItem(
                                container.Uid.Value, container.DispIdFull, 0,
                                container.Amount, container.X, container.Y,
                                stackParent, container.Hue, _netState.IsClientPost6017));
                    }
                    if (remaining <= 0)
                    {
                        _world.RemoveItem(item);
                    }
                    else if (container.ContainedIn.IsValid &&
                             _world.FindItem(container.ContainedIn) is { } realParent)
                    {
                        // Overflow remainder stays beside the target stack.
                        item.Amount = (ushort)remaining;
                        if (!realParent.TryAddItem(item))
                        {
                            _world.PlaceItemWithDecay(item, container.GetTopLevelPosition());
                            BroadcastWorldItem(item);
                            _netState.Send(new PacketDropAck());
                            return;
                        }
                        item.Position = new Point3D(container.X, container.Y, 0, _character.MapIndex);
                        SendContainerItemPacket(new PacketContainerItem(
                            item.Uid.Value, item.DispIdFull, 0,
                            item.Amount, item.X, item.Y,
                            realParent.Uid.Value, item.Hue, _netState.IsClientPost6017));
                    }
                    else if (targetOnGround)
                    {
                        // Overflow remainder drops to the ground beside the
                        // target pile so it is never lost.
                        item.Amount = (ushort)remaining;
                        _world.PlaceItemWithDecay(item, container.Position);
                        BroadcastWorldItem(item);
                    }
                    else
                    {
                        // Contained target whose parent lookup failed (a child that
                        // outlived its parent — torn world state). Without this the
                        // source kept its full Amount while the target already grew
                        // by `moved`, duplicating `moved` units. Reduce the source to
                        // the remainder and bounce it to the target's top-level tile.
                        item.Amount = (ushort)remaining;
                        _world.PlaceItemWithDecay(item, container.GetTopLevelPosition());
                        BroadcastWorldItem(item);
                    }
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // A stackable item dropped ON a container - with no slot named, which
                // is what the client sends as -1,-1 - merges with a matching pile
                // already inside it (CItemContainer::ContentAdd, CItemContainer.cpp:
                // 618-634). Without this, gold looted off a corpse and dropped on the
                // pack sat beside the gold already there as a second pile.
                //
                // A pile that cannot take all of it is topped up and the remainder
                // carries on to the next candidate, exactly as CItem::Stack does
                // (CItem.cpp: tops the target to its max, reduces the source, returns
                // false so the caller keeps looking).
                if (x < 0 && y < 0)
                {
                    // The remainder is tracked here rather than written back each time:
                    // Amount clamps to a minimum of one, so writing a fully consumed
                    // pile back left a stray single coin behind.
                    int left = item.Amount;
                    foreach (var existing in _world.GetContainerContents(container.Uid).ToList())
                    {
                        if (left <= 0 || existing.Uid == item.Uid || !existing.CanStackWith(item))
                            continue;
                        int room = existing.MaxAmount - existing.Amount;
                        if (room <= 0)
                            continue;

                        int moved = Math.Min(room, left);
                        existing.Amount = (ushort)(existing.Amount + moved);
                        left -= moved;
                        SendContainerItemPacket(new PacketContainerItem(
                            existing.Uid.Value, existing.DispIdFull, 0,
                            existing.Amount, existing.X, existing.Y,
                            container.Uid.Value, existing.Hue, _netState.IsClientPost6017));
                    }

                    if (left <= 0)
                    {
                        _world.RemoveItem(item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                    if (left != item.Amount)
                        item.Amount = (ushort)left;
                }

                if (!container.TryAddItem(item))
                {
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                item.Position = new Point3D(x, y, 0, _character.MapIndex);
                // Critical: tell the client the item actually landed in the
                // container. Without 0x25 the client only remembers the
                // earlier pickup → the item silently vanishes from its view.
                SendContainerItemPacket(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    container.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                _netState.Send(new PacketDropAck());
                // A trash can "eats" what goes in: a sound, the message, and the can
                // empties itself 15 seconds later (CanContainerHold IT_TRASH_CAN,
                // CItemContainer.cpp:1025-1029; emptied in _OnTick, :1415-1418).
                if (container.ItemType == ItemType.TrashCan)
                {
                    var canPos = container.GetTopLevelPosition();
                    _netState.Send(new PacketSound(0x235, canPos.X, canPos.Y, canPos.Z));
                    SysMessage(ServerMessages.Get(Msg.ItemuseTrashcan));
                    container.SetTimeout(Environment.TickCount64 + 15_000);
                }
                if (VendorEngine.IsGold(item))
                    SendCharacterStatus(_character);
                return;
            }

            var charTarget = _world.FindChar(new Serial(containerUid));
            if (charTarget != null && charTarget == _character)
            {
                // Dropped on myself: upstream turns the target into the pack
                // (CClientEvent.cpp:331) and carries on as a drop INTO it, so the pack
                // hears @DropOn_Item's ARGO and runs @DropOn_Self with the dragged item
                // as ARGO. This used to fire @DropOn_Self on the dragged item itself.
                var ownPack = _character.Backpack;
                if (ownPack != null && _triggerDispatcher != null)
                {
                    if (FireDropOnItem(item, ownPack))
                        return;
                    if (FireDropOnSelf(ownPack, item))
                        return;
                }
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
            else if (charTarget != null)
            {
                if (_character.PrivLevel < PrivLevel.GM &&
                    (_character.MapIndex != charTarget.MapIndex ||
                     _character.Position.GetDistanceTo(charTarget.Position) > 3 ||
                     !_world.CanSeeLOSFor(_character, charTarget)))   // CanTouch, CClientEvent.cpp:314
                {
                    RestoreToOrigin(item);
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
                    // Source-X Event_Item_Drop bounces the item when Cmd_SecureTrade
                    // refuses (CClientEvent.cpp:325). Acking a drop that never landed
                    // told the client it had succeeded while the item sat parented to
                    // the character, out of reach of every inventory view.
                    if (!InitiateTrade(charTarget, item))
                    {
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // No attribute is touched here. Dropping an item on a character's
                // head is Cmd_SecureTrade -> NPC_OnItemGive (CClientEvent.cpp:320-328),
                // and an NPC that refuses bounces it back unchanged; ATTR_OWNED /
                // ATTR_NEWBIE are cleared only in the other branch, for an item put
                // into a container on an NPC the giver owns (:351-357). Clearing them
                // here cost a blessed item its blessing for being offered to a
                // shopkeeper who did not even take it.

                // Source-X NPC_OnItemGive: @ReceiveItem fires FIRST — a
                // quest/reward script may fully consume the gift before ANY
                // native handling, the train/hire gold pay included. RETURN 1 is
                // "not accepted" (CCharNPCAct.cpp:2073-2077), which makes the drop
                // fail: an item still on the cursor goes back where it came from
                // (Event_Item_Drop_Fail, CClientEvent.cpp:325-326). One the script
                // deleted or put somewhere itself stays as the script left it.
                if (!charTarget.IsPlayer && _triggerDispatcher != null &&
                    _triggerDispatcher.FireCharTrigger(charTarget, CharTrigger.ReceiveItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = item })
                    == TriggerResult.True)
                {
                    if (!item.IsDeleted && item.ContainedIn == _character.Uid && !item.IsEquipped)
                    {
                        RestoreToOrigin(item);
                        _netState.Send(new PacketDropReject());
                    }
                    else
                    {
                        _dragOrigin = null;
                        _netState.Send(new PacketDropAck());
                    }
                    return;
                }

                // NPC_OnItemGive (CCharNPCAct.cpp:2079): gold goes where the NPC's
                // memory of the giver points - a price it named for a lesson makes it
                // the fee (NPC_OnTrainPay).
                if (!charTarget.IsPlayer && VendorEngine.IsGold(item) &&
                    Trade.VendorTrainingEngine.FindOffer(charTarget, _character) is { } offer)
                {
                    PayTrainer(charTarget, offer, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Source-X NPC_OnHirePay: gold handed to a hireable NPC
                // (chardef HIREDAYWAGE) hires it and/or extends its PREPAID
                // wage balance — the hireling later drains this balance per
                // period (NPC_CheckHirelingStatus), never the master's bank.
                if (!charTarget.IsPlayer &&
                    VendorEngine.IsGold(item) &&
                    TryApplyHirePayment(charTarget, item))
                {
                    _netState.Send(new PacketDropAck());
                    return;
                }

                // Native give flow (Source-X NPC_OnItemGive tail).
                if (!charTarget.IsPlayer)
                {
                    HandleGiveItemToNpc(charTarget, item);
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
                    RestoreToOrigin(item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
            }
            int dropDist = Math.Max(Math.Abs(_character.X - x), Math.Abs(_character.Y - y));
            // CChar::CanTouch(pt) is line of sight to the point (CCharStatus.cpp:1282,
            // called at CClientEvent.cpp:549 "Must also be LOS"): no dropping into a
            // sealed room through its wall.
            if (dropDist > 3 ||
                !_world.CanSeeLOSFor(_character, _character.Position,
                    new Point3D(x, y, z, _character.MapIndex), maxDist: 6))
            {
                RestoreToOrigin(item);
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
                RestoreToOrigin(item);
                _netState.Send(new PacketDropReject());
                return;
            }
        }

        // Dropped on top of a plain item on the ground: upstream runs @DropOn_Item
        // (ARGO = that item) and the item's @DropOn_Self before settling the drop on
        // its tile (CClientEvent.cpp:421-446, then :504).
        if (groundDropOnTarget != null && _triggerDispatcher != null)
        {
            if (FireDropOnItem(item, groundDropOnTarget))
                return;
            if (FireDropOnSelf(groundDropOnTarget, item))
                return;
        }

        // @DropOn_Ground — Source-X hands ARGN1 the decay time in TENTHS OF A SECOND
        // and the drop point as the string argument, and reads BOTH back afterwards
        // (MoveToCheck, CItem.cpp:1629). Both halves used to differ: the coordinates
        // went out as ARGN1/2/3 and the interval as a LOCAL, so a Source-X script's
        // choices reached nothing. LOCAL.DECAY is still read, in seconds, so the
        // scripts written against the older reading keep working.
        long defaultDecaySec = GameWorld.DefaultDecayTimeMs / 1000;
        // "iDecayTime = GetDecayTime()" (MoveToCheck, CItem.cpp:1618): -1 for an
        // item that never rots.
        long naturalDecayMs = item.GetDecayTime();
        if (naturalDecayMs < 0)
            naturalDecayMs = -1000;
        // The region's protection is applied to the NATURAL time, before the script
        // speaks (:1620) - not as a veto over whatever it then chooses.
        var preRegion = _world.FindRegion(new Point3D(x, y, z, _character.MapIndex));
        if (naturalDecayMs > 0 && preRegion != null &&
            preRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.NoDecay))
            naturalDecayMs = -1000;

        var dropLocals = new SphereNet.Scripting.Variables.VarMap();
        long seededDecaySec = naturalDecayMs > 0 ? naturalDecayMs / 1000 : defaultDecaySec;
        dropLocals.SetInt("DECAY", seededDecaySec);
        var dropArgs = new TriggerArgs
        {
            CharSrc = _character, ItemSrc = item,
            N1 = (int)(naturalDecayMs / 100),          // tenths of a second
            S1 = $"{x},{y},{z},{_character.MapIndex}", // the drop point
            Locals = dropLocals,
        };
        var priorContainer = item.ContainedIn;
        var dropResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnGround, dropArgs);

        // A script that removed the item ends the drop right there (:1634).
        if (item.IsDeleted)
        {
            _netState.Send(new PacketDropAck());
            return;
        }

        naturalDecayMs = dropArgs.N1 * 100L;

        // Honor a script-relocated drop point, re-validating map bounds + reach so
        // a script can't fling the item out of the world / out of range (GM keeps
        // the same bypass the pre-trigger gate used).
        short finalX = x, finalY = y;
        sbyte finalZ = z;
        if (!string.IsNullOrWhiteSpace(dropArgs.S1) &&
            Point3D.TryParse(dropArgs.S1.Trim(), out var relocated))
        {
            finalX = relocated.X; finalY = relocated.Y; finalZ = relocated.Z;
        }
        if (finalX != x || finalY != y || finalZ != z)
        {
            bool inBounds = true;
            var relocMd = _world.MapData;
            if (relocMd != null)
            {
                var (mw, mh) = relocMd.GetMapSize(_character.MapIndex);
                inBounds = finalX >= 0 && finalY >= 0 && finalX < mw && finalY < mh;
            }
            int relocDist = Math.Max(Math.Abs(_character.X - finalX), Math.Abs(_character.Y - finalY));
            if (inBounds && (_character.PrivLevel >= PrivLevel.GM || relocDist <= 3))
            {
                x = finalX; y = finalY; z = finalZ;
            }
        }

        // ARGN1 is the reference's channel; LOCAL.DECAY still works for the scripts
        // written against the older reading, and wins when the script actually changed
        // it. A negative interval means "do not rot" (the region's own protection, or a
        // script asking for the same).
        // The local only wins when the script actually CHANGED it - it is seeded with
        // the natural value, so its mere presence says nothing.
        long dropDecaySec = dropLocals.GetInt("DECAY", seededDecaySec);
        long dropDecayMs = dropDecaySec != seededDecaySec
            ? (dropDecaySec > 0 ? dropDecaySec * 1000 : -1000)
            : naturalDecayMs;
        if (dropDecayMs == 0)
            dropDecayMs = GameWorld.DefaultDecayTimeMs;

        // A script that moved the item somewhere else keeps it there: upstream only
        // repositions when the container is unchanged (:1654).
        if (item.ContainedIn != priorContainer && item.ContainedIn.IsValid)
        {
            _netState.Send(new PacketDropAck());
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

                if (existing.CanStackWith(item) && (existing.Amount + item.Amount) <= existing.MaxAmount)
                {
                    existing.Amount += item.Amount;
                    if (existing.IsPile)
                        existing.Direction = 0;
                    BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, existing.Position, existing.Position);
                    _world.RemoveItem(item);
                    _netState.Send(new PacketDropAck());
                    BroadcastNearby?.Invoke(existing.Position, UpdateRange,
                        new PacketSound(existing.GetDropSound(ontoSomething: true),
                            existing.X, existing.Y, existing.Z), 0);
                    BroadcastWorldItem(existing);
                    return;
                }

                tileItemCount++;
            }
        }

        if (item.IsPile)
        {
            item.Direction = 0;
        }
        else
        {
            item.Direction = (byte)((tileItemCount % 7) + 1);
            // Whether a dropped item turns to its flipped graphic is a setting, and it
            // never applies to something that cannot be moved (CCharAct.cpp:3266). The
            // flip was unconditional here, so a shard that turned it off still got it.
            if (Item.FlipDroppedItems && item.IsMovableType)
                item.TryFlipDisplay();
        }

        // RETURN 1 on THIS event means the script took over after the item was placed -
        // upstream moves and updates it and then returns success (:1654/:1660). It is
        // not the general veto other events use, so bouncing the item back to the
        // backpack was the opposite of what the script asked for.
        if (dropResult == TriggerResult.True)
        {
            _world.PlaceItem(item, groundPos);
            _netState.Send(new PacketDropAck());
            BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, groundPos, groundPos);
            BroadcastWorldItem(item);
            return;
        }

        // "Too many items here!" (MoveToCheck, CItem.cpp:1664-1684): past
        // MAXITEMCOMPLEXITY items on one spot the drop still lands, but the item
        // says so and is gone in a minute - a NODECAY region included.
        bool tooManyItems = tileItemCount + 1 > Item.MaxItemComplexity;
        if (tooManyItems)
            dropDecayMs = 60_000;

        if (dropDecayMs < 0)
        {
            // No rot at all - place it and clear the decay.
            _world.PlaceItem(item, groundPos);
            item.SetDecayTime(-1);
        }
        else
        {
            _world.PlaceItemWithDecay(item, groundPos, dropDecayMs);
        }
        _netState.Send(new PacketDropAck());
        BroadcastDragAnimation(item, _character.Uid.Value, _character.Position, 0, groundPos, groundPos);
        BroadcastNearby?.Invoke(groundPos, UpdateRange,
            new PacketSound(item.GetDropSound(ontoSomething: false),
                groundPos.X, groundPos.Y, groundPos.Z), 0);
        BroadcastWorldItem(item);
        if (tooManyItems)
            _client.ObjectMessage(item, ServerMessages.Get(Msg.TooManyItems));
    }

    /// <summary>
    /// Script BOUNCE/DROP verb bridge (Character.OnDragRelease): release the
    /// item the character is dragging (DRAGGING tag) either to the ground at
    /// their feet or back into the backpack, and cancel the client-side drag
    /// cursor with 0x27. Returns false when nothing is being dragged.
    /// </summary>
    /// <summary>Tell the client to put the drag cursor down. The item itself is the
    /// caller's business — death takes it and applies the equipment rules.</summary>
    public void CancelDragCursor() => _netState.Send(new PacketPickupFailed(0));

    public bool ReleaseDraggedItem(bool toGround)
    {
        if (_character == null)
            return false;
        if (!_character.TryGetTag("DRAGGING", out string? dragSer) ||
            !ScriptNumber.TryParseUInt(dragSer, out uint dragUid))
            return false;

        _character.RemoveTag("DRAGGING");
        _netState.Send(new PacketPickupFailed(0)); // cancel the drag cursor

        var item = _world.FindItem(new Serial(dragUid));
        if (item == null || item.IsDeleted)
            return true;

        var pack = _character.Backpack;
        if (!toGround && pack != null)
        {
            if (!pack.TryAddItem(item))
            {
                _world.PlaceItemWithDecay(item, _character.Position);
                BroadcastWorldItem(item);
                return true;
            }
            item.Position = new Point3D(50, 50, 0, _character.MapIndex);
            SendContainerItemPacket(new PacketContainerItem(
                item.Uid.Value, item.DispIdFull, 0, item.Amount,
                item.X, item.Y, pack.Uid.Value, item.Hue,
                _netState.IsClientPost6017));
            return true;
        }

        _world.PlaceItemWithDecay(item, _character.Position);
        BroadcastWorldItem(item);
        return true;
    }

    // ==================== Item Equip ====================

    /// <summary>Equip-first double-click step (Source-X CClient::Cmd_Use_Item):
    /// an equippable item that is not currently equipped is armed/worn when
    /// double-clicked — from the ground or a container — before its use-type
    /// behavior runs. Routes through the real pickup + equip paths so reach,
    /// ownership, @PickUp_*/@EquipTest/@Equip triggers and hand-swap rules all
    /// apply; the current occupant of the layer is bounced to the backpack.
    /// Returns true when the item ended up equipped.</summary>
    public bool TryDClickEquip(Item item, Layer layer)
    {
        if (_character == null || item.IsEquipped)
            return false;
        if (layer <= Layer.None || layer >= Layer.Qty)
            return false;

        // Lift through the real pickup path (reach, housing, trigger gates).
        HandleItemPickup(item.Uid.Value, 0);
        if (!_character.TryGetTag("DRAGGING", out string? dragStr) ||
            dragStr != item.Uid.Value.ToString())
            return false;

        // The drag stays open until the equip answers. Clearing it here, and
        // emptying the layer with it, left a refused item with no layer, no pack
        // entry and no drag to bounce it back from - and took the wearer's previous
        // piece off for an equip that never happened. Both are the equip's own
        // business now (SettleEquipDrag, and the occupant bounce behind the gates).
        return HandleItemEquipCore(item.Uid.Value, (byte)layer, _character.Uid.Value, fromDoubleClick: true);
    }

    /// <summary>0xEC equip-item macro (Source-X PacketEquipItemMacro). Each
    /// serial must already be carried by the character and not worn; the item
    /// is lifted and equipped on its own layer, so reach checks and the
    /// @PickUp_*/@EquipTest/@Equip triggers all still run. Serials that fail
    /// any step are skipped rather than aborting the batch, as upstream does.
    /// </summary>
    public void HandleEquipMacro(IReadOnlyList<uint> serials)
    {
        if (_character == null || _character.IsDead) return;

        foreach (uint serial in serials)
        {
            var item = _world.FindItem(new Serial(serial));
            if (item == null || item.IsEquipped)
                continue;
            // Source-X requires the item's top-level owner to be the character:
            // the macro equips from your own pack, never off the ground.
            if (item.ResolveTopObject() != _character)
                continue;

            Layer layer = _client.ItemUse.ResolveWearableLayer(item);
            if (layer is Layer.None or Layer.Pack or Layer.Hair or Layer.FacialHair ||
                (int)layer >= (int)Layer.Horse)
                continue;

            TryDClickEquip(item, layer);
        }
    }

    /// <summary>0xD7 0x1E EquipLastWeapon macro: wield the last weapon held again.
    /// Source-X PacketEquipLastWeapon (receive.cpp:4121-4148) lifts it with ItemPickup -
    /// ending there when the pickup is refused (:4142) - and only then calls ItemEquip.
    ///
    /// The lift used to be done by hand: the weapon was re-parented to the character
    /// out of ANY container the character held directly (the bank box as well as the
    /// pack), with no CanMoveItem, reach or @PickUp_* check, and before the equip was
    /// asked whether it would happen. A ghost's equip is refused, so the weapon stayed
    /// parented to the character with no layer. It now goes through the same pickup
    /// and equip as the double-click and the equip macro do.</summary>
    public void HandleEquipLastWeapon()
    {
        if (_character == null || _character.IsDead) return;

        var lastUid = _character.LastWeaponUid;
        if (!lastUid.IsValid) return;
        if (_world.FindObject(lastUid) is not Item weapon || weapon.IsDeleted) return;
        // Already wielding it: nothing to do (m_uidWeaponLast == m_uidWeapon, :4136).
        if (ReferenceEquals(_character.GetEquippedItem(Layer.OneHanded), weapon) ||
            ReferenceEquals(_character.GetEquippedItem(Layer.TwoHanded), weapon))
            return;

        TryDClickEquip(weapon, weapon.IsTwoHanded ? Layer.TwoHanded : Layer.OneHanded);
    }

    /// <summary>0xED unequip-item macro (Source-X PacketUnEquipItemMacro):
    /// strip the named layers back into the pack.</summary>
    public void HandleUnequipMacro(IReadOnlyList<ushort> layers)
    {
        if (_character == null || _character.IsDead) return;

        foreach (ushort raw in layers)
        {
            var layer = (Layer)raw;
            if (layer is Layer.None or Layer.Pack or Layer.Hair or Layer.FacialHair ||
                (int)layer >= (int)Layer.Horse)
                continue;

            var item = _character.GetEquippedItem(layer);
            if (item == null || !ItemMoveRules.CanMove(_character, item, out _))
                continue;

            UnequipWithNotification(_character, layer);
            if (item.IsDeleted) continue;
            var removePkt = new PacketDeleteObject(item.Uid.Value);
            _netState.Send(removePkt);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, removePkt, _character.Uid.Value);
            PlaceItemInPack(_character, item);
        }
    }

    private void UnequipWithNotification(Character wearer, Layer layer)
    {
        if (_triggerDispatcher == null) wearer.Unequip(layer);
        else wearer.Unequip(layer, (item, source) =>
            _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Unequip,
                new TriggerArgs { CharSrc = source, ItemSrc = item }));
    }

    /// <summary>Settle the drag this equip request belongs to.
    ///
    /// Source-X ends the drag mode as soon as the request is validated, whatever
    /// the outcome (PacketItemEquipReq, receive.cpp:542), and hands a refused item
    /// to Event_Item_Drop_Fail, which puts it back where it was lifted from
    /// (CClientEvent.cpp:248). SphereNet did neither: a successful equip left the
    /// DRAGGING tag and the lift origin standing, so the NEXT pickup treated the
    /// worn item as still on the cursor and restored it out of its layer - and a
    /// refused one was left parented to the character with no layer, no pack entry
    /// and no drag to recover it from.</summary>
    private bool SettleEquipDrag(Item item, bool equipped)
    {
        if (_character == null)
            return equipped;

        bool wasDragged = _character.TryGetTag("DRAGGING", out string? raw) &&
                          ScriptNumber.TryParseUInt(raw, out uint heldUid) && heldUid == item.Uid.Value;
        if (wasDragged)
            _character.RemoveTag("DRAGGING");

        if (equipped)
        {
            // The drag ended in the equipment; there is nothing left to bounce to.
            if (wasDragged)
                _dragOrigin = null;
            return true;
        }

        if (wasDragged)
        {
            RestoreToOrigin(item);      // consumes _dragOrigin
            CancelDragCursor();
        }
        else if (item.ContainedIn == _character.Uid && !item.IsEquipped)
        {
            // Lifted onto the character by a path that never opened a drag (the
            // EquipLastWeapon macro). Source-X ItemBounce sends it to the pack.
            PlaceItemInPack(_character, item);
        }
        return false;
    }

    public bool HandleItemEquip(uint serial, byte layer, uint charSerial)
        => HandleItemEquipCore(serial, layer, charSerial, fromDoubleClick: false);

    private bool HandleItemEquipCore(uint serial, byte layer, uint charSerial, bool fromDoubleClick)
    {
        if (_character == null) return false;

        var item = _world.FindItem(new Serial(serial));
        if (item == null)
        {
            // Nothing to wear, but the client may still be showing a cursor
            // (PacketDragCancel::Other, the silent reason).
            SendPickupFailed(5);
            return false;
        }

        // The request must name the item this client is dragging: Source-X looks the
        // item up on LAYER_DRAGGING, wants the client in drag mode and the serial to
        // match, and answers anything else with PacketDragCancel (PacketItemEquipReq,
        // receive.cpp:541-547). The test here was "parented to the character", which
        // a worn item also passes: re-sending 0x13 for something already on a layer
        // ran @EquipTest/@Equip again with no @Unequip between them, restarted the
        // swing wait and broadcast the equip sound and 0x2E each time. An item on a
        // layer is refused for staff as well - it is not on anybody's cursor.
        bool isDragged = !item.IsEquipped && item.ContainedIn == _character.Uid &&
            _character.TryGetTag("DRAGGING", out string? heldRaw) &&
            ScriptNumber.TryParseUInt(heldRaw, out uint heldSerial) && heldSerial == item.Uid.Value;
        if (item.IsEquipped || (!isDragged && _character.PrivLevel < PrivLevel.GM))
        {
            SendPickupFailed(5);    // PacketDragCancel::Other
            return false;   // not the item on this cursor - nothing of ours to settle
        }

        // A ghost wears nothing (CanTouch refuses a dead character every item that is
        // not death-immune), but the drag is still settled: returning here without it
        // left the item parented to the character with no layer and no drag.
        if (_character.IsDead)
            return SettleEquipDrag(item, false);

        // Reject the internal-only dragging/sentinel layers (31+). Dragging is
        // not a client-equippable slot; this preserves the prior bound now that
        // Layer.Dragging sits between Special and Qty.
        if (layer == 0 || layer >= (byte)Layer.Dragging)
            return SettleEquipDrag(item, false);

        var target = _world.FindChar(new Serial(charSerial));
        if (target == null) target = _character;

        if (target != _character && _character.PrivLevel < PrivLevel.GM)
            return SettleEquipDrag(item, false);

        // The layer byte comes from the client and is not trusted: Source-X replaces
        // it with the item's own layer (CanEquipLayer(item, LAYER_QTY), receive.cpp:
        // 554-562), refuses anything that is not wearable, and equips nothing at
        // LAYER_HORSE or above from this packet (:564). A forged 0x13 used to wear any
        // dragged item on any layer - a sword on the head, a stack of gold over the
        // backpack or the bank box.
        Layer ownLayer = _client.ItemUse.ResolveWearableLayer(item);
        if (ownLayer == Layer.None || ownLayer >= Layer.Horse)
            return SettleEquipDrag(item, false);
        layer = (byte)ownLayer;
        // A pack already worn is never displaced by another one: CanEquipLayer
        // answers LAYER_NONE for an occupied LAYER_PACK (CCharStatus.cpp:454-457).
        if (ownLayer == Layer.Pack &&
            target.GetEquippedItem(ownLayer) is { } wornContainer && !ReferenceEquals(wornContainer, item))
            return SettleEquipDrag(item, false);

        // Resolve the final hand before testing/bouncing occupants. Character.Equip
        // applies the same promotion; doing it only there bypassed client updates
        // for the item displaced from HAND2 (for example a held candle).
        if ((Layer)layer == Layer.OneHanded && item.IsTwoHanded)
            layer = (byte)Layer.TwoHanded;

        // Central equip gate (Source-X CChar::CanEquipLayer): block an
        // underpowered wearer from a high-REQSTR item. GM actor bypasses; the
        // layer-31 / ownership / hand-conflict guards are handled separately.
        if (_character.PrivLevel < PrivLevel.GM &&
            !target.CanEquip(item, (Layer)layer, out var equipDenial))
        {
            if (equipDenial == Character.EquipDenial.TooWeak)
                SysMessage("You are not strong enough to equip that.");
            return SettleEquipDrag(item, false);
        }

        // @EquipTest comes after the layer/strength gate (CanEquipLayer) and runs with
        // the WEARER as SRC; RETURN 1, or a script that deleted the item, refuses the
        // equip (CChar::ItemEquip, CCharAct.cpp:3298-3331).
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.EquipTest,
                new TriggerArgs { CharSrc = target, ItemSrc = item });
            if (item.IsDeleted)
            {
                SettleEquipDrag(item, true); // nothing left to bounce
                return false;
            }
            if (result == TriggerResult.True)
                return SettleEquipDrag(item, false);
        }

        // Changing equipment does not disturb a cast: Source-X's only hand rule is
        // Spell_Unequip at cast start (CCharSpell.cpp:3609).

        void BounceWornItem(Item worn)
        {
            target.Unequip(worn.EquipLayer);
            var removed = new PacketDeleteObject(worn.Uid.Value);
            _netState.Send(removed);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, removed, _character.Uid.Value);
            PlaceItemInPack(target, worn);
        }

        // Hands mutual exclusion (UO rules): a true two-handed weapon needs
        // BOTH hands, so it bounces whatever the other hand holds — weapon or
        // shield. Conversely any one-hand-layer equip bounces a held
        // two-handed weapon (it coexists with a shield, which is not
        // two-handed). The old check only covered the shield cases, so a
        // 1H weapon + 2H weapon could end up held together.
        if ((Layer)layer == Layer.TwoHanded && (item.IsWeaponType || item.IsTwoHanded))
        {
            var offhand = target.GetEquippedItem(Layer.OneHanded);
            if (offhand != null)
            {
                BounceWornItem(offhand);
            }
        }
        else if ((Layer)layer == Layer.OneHanded)
        {
            var oldWeapon = target.GetEquippedItem(Layer.TwoHanded);
            if (oldWeapon != null && oldWeapon.IsWeaponType)
            {
                BounceWornItem(oldWeapon);
            }
        }

        // Whatever holds the layer goes to the pack before the new item takes it
        // (Source-X CanEquipLayer bounces pItemPrev, CCharStatus.cpp:470). This ran
        // in the double-click caller BEFORE the gates above, so a refused equip -
        // too weak, or vetoed - still disarmed the wearer.
        var occupant = target.GetEquippedItem((Layer)layer);
        if (occupant != null && !ReferenceEquals(occupant, item))
        {
            BounceWornItem(occupant);
        }

        target.Equip(item, (Layer)layer);
        // Equip may promote a two-handed weapon from the OneHanded layer to
        // TwoHanded; reflect the actual layer to the client so it renders/animates
        // the weapon correctly (a bow on the wrong layer animates as a punch).
        byte actualLayer = (byte)item.EquipLayer;
        if (item.EquipLayer is Layer.OneHanded or Layer.TwoHanded && IsCombatEquipItem(item))
        {
            bool noWait = (Character.CombatFlags & (int)CombatFlags.FirstHitInstant) != 0;
            // A weapon change restarts the fight skill in WAR_SWING_EQUIPPING: the
            // recoil runs before the next swing animation (CCharSkill.cpp:4559-4563).
            int delayMs = CombatHelper.GetInitialSwingWaitMs(CombatEngine.GetSwingDelayMs(target, item));
            // The swing in flight belongs to the weapon that was in hand (:306-316).
            target.ClearPendingHit();
            target.BeginEquipSwingWait(Environment.TickCount64, delayMs, noWait);
        }

        // Wearing something is heard: upstream plays EQUIPSOUND (0x057 by default) for
        // any layer that actually shows (CCharAct.cpp:3355). SphereNet played nothing at
        // all, so armour and weapons went on in silence.
        if (Item.IsVisibleLayer((Layer)actualLayer))
            BroadcastNearby?.Invoke(target.Position, UpdateRange,
                new PacketSound(item.GetEquipSound(), target.X, target.Y, target.Z), 0);

        var wornPkt = new PacketWornItem(
            item.Uid.Value, item.DispIdFull, actualLayer,
            target.Uid.Value, item.Hue);
        // A double-click has no client-side lift to detach the incoming item.
        // Source-X ItemEquip(fFromDClick) uses ResendOnEquip (CObjBase.cpp):
        // remove the item's old client representation, then send the worn item.
        // Extend that redraw to the initiating ClassicUO client as well as EC;
        // the drag path already detached its item and needs no extra removal.
        // Both packets are Normal priority, preserving remove-before-wear order.
        if (fromDoubleClick)
            _netState.Send(new PacketDeleteObject(item.Uid.Value));
        _netState.Send(wornPkt);
        BroadcastNearby?.Invoke(target.Position, UpdateRange, wornPkt, _character.Uid.Value);

        // SRC is the wearer (OnTrigger(ITRIG_EQUIP, args, this), CCharAct.cpp:3346).
        _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = target, ItemSrc = item });

        return SettleEquipDrag(item, true);
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

        // Only a player has a profile, and an unknown serial is not the requester's
        // own (Event_Profile, CClientEvent.cpp:1598-1600).
        Character? ch = _world.FindChar(new Serial(serial));
        if (ch == null || !ch.IsPlayer)
            return;

        // @Profile runs first, for a read and a write alike (CClientEvent.cpp:1602-1606).
        if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Profile, new TriggerArgs
        {
            CharSrc = _character,
            S1 = mode == 1 ? bioText : "",
            N1 = mode
        }) == TriggerResult.True)
            return;

        if (mode != 0)
        {
            // Someone else's profile needs GM, and a plevel no lower than the owner's
            // (CClientEvent.cpp:1611-1617).
            if (ch != _character)
            {
                if (_character.PrivLevel < PrivLevel.GM)
                    return;
                if (_character.PrivLevel < ch.PrivLevel)
                    return;
            }

            // A text carrying a line feed is not stored (CClientEvent.cpp:1619-1620).
            // The text lives in PROFILE, the key the save writes; it used to go to a
            // tag nothing else read.
            if (bioText != null && !bioText.Contains('\n'))
            {
                ch.Profile = bioText;
                ch.RemoveTag(LegacyProfileTag);
            }
            return;
        }

        // send.cpp:3828-3841: the name, then "NotoTitle, TradeTitle", then the body.
        // An incognito character shows its noto title and an empty body, unless the
        // viewer is a GM or it is the viewer's own.
        bool incognito = ch.IsStatFlag(StatFlag.Incognito) &&
            _character.PrivLevel < PrivLevel.GM && ch != _character;
        string notoTitle = PaperdollText.GetNotoTitle(ch);
        if (incognito)
        {
            _netState.Send(new PacketProfileResponse(ch.Uid.Value, ch.GetName(), notoTitle, ""));
            return;
        }

        _netState.Send(new PacketProfileResponse(ch.Uid.Value, ch.GetName(),
            $"{notoTitle}, {PaperdollText.GetTradeTitle(ch)}", ReadProfileText(ch)));
    }

    /// <summary>The tag an earlier build kept the profile text in.</summary>
    private const string LegacyProfileTag = "PROFILE_BIO";

    /// <summary>The profile body: PROFILE, or - once, for a character written before
    /// the text moved there - the old tag.</summary>
    internal static string ReadProfileText(Character ch)
    {
        if (!string.IsNullOrEmpty(ch.Profile))
            return ch.Profile;
        return ch.TryGetTag(LegacyProfileTag, out string? bio) && bio != null ? bio : "";
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
