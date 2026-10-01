using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Trade;

/// <summary>
/// A trade item entry for vendor buy/sell lists.
/// </summary>
public readonly struct TradeEntry
{
    public Serial ItemUid { get; init; }
    public ushort ItemId { get; init; }
    public string Name { get; init; }
    public int Price { get; init; }
    public int Amount { get; init; }
}

/// <summary>
/// A secure trade session between two players (trade window).
/// Each side has a virtual container — the client shows items in these containers
/// inside the trade gump. The containers must exist as real Items so ClassicUO's
/// world.Get(serial) can find them when processing the 0x6F packet.
/// </summary>
public sealed class SecureTrade
{
    private readonly Serial _sessionId;
    private readonly Character _initiator;
    private readonly Character _partner;
    private readonly Item _initiatorContainer;
    private readonly Item _partnerContainer;

    private bool _initiatorAccepted;
    private bool _partnerAccepted;
    private bool _isCompleted;
    // Virtual gold each side has put in the window (TOL trade gold/platinum fields).
    private long _initiatorGold;
    private long _partnerGold;

    public long GetGoldOffer(Character ch) => ch == _initiator ? _initiatorGold : _partnerGold;

    public void SetGoldOffer(Character ch, long amount)
    {
        if (ch == _initiator) _initiatorGold = Math.Max(0, amount);
        else if (ch == _partner) _partnerGold = Math.Max(0, amount);
    }

    public Serial SessionId => _sessionId;
    public Character Initiator => _initiator;
    public Character Partner => _partner;
    public Item InitiatorContainer => _initiatorContainer;
    public Item PartnerContainer => _partnerContainer;
    public bool InitiatorAccepted => _initiatorAccepted;
    public bool PartnerAccepted => _partnerAccepted;
    public bool IsCompleted => _isCompleted;

    public SecureTrade(Serial sessionId, Character initiator, Character partner,
        Item initiatorContainer, Item partnerContainer)
    {
        _sessionId = sessionId;
        _initiator = initiator;
        _partner = partner;
        _initiatorContainer = initiatorContainer;
        _partnerContainer = partnerContainer;
    }

    public bool IsParticipant(Character ch) => ch == _initiator || ch == _partner;

    public Item GetOwnContainer(Character ch) =>
        ch == _initiator ? _initiatorContainer : _partnerContainer;

    public Item GetPartnerContainer(Character ch) =>
        ch == _initiator ? _partnerContainer : _initiatorContainer;

    public Character GetPartner(Character ch) =>
        ch == _initiator ? _partner : _initiator;

    /// <summary>
    /// Set one side's accept flag to the value the client sent, and report whether
    /// both sides now agree.
    ///
    /// Source-X CItemContainer::Trade_Status (CItemContainer.cpp:144) ASSIGNS the
    /// value from the packet and, when it is false, clears the partner's flag too —
    /// a change of mind puts the whole trade back to unaccepted. Flipping a stored
    /// bool instead meant the packet's own field was ignored: a repeated accept
    /// silently un-accepted, and an explicit "no" completed the trade.
    /// </summary>
    public bool SetAccept(Character from, bool accepted)
    {
        if (_isCompleted) return false;

        if (from == _initiator) _initiatorAccepted = accepted;
        else if (from == _partner) _partnerAccepted = accepted;
        else return false;

        if (!accepted)
        {
            // Withdrawing consent drops the other side's too, so nothing can
            // complete on a flag the partner set against a different offer.
            _initiatorAccepted = false;
            _partnerAccepted = false;
        }

        return _initiatorAccepted && _partnerAccepted;
    }

    public void ResetAcceptance()
    {
        _initiatorAccepted = false;
        _partnerAccepted = false;
    }

    public void Cancel()
    {
        if (_isCompleted) return;
        _isCompleted = true;
    }

    public void Complete()
    {
        if (_isCompleted) return;
        _isCompleted = true;
    }
}

/// <summary>
/// Vendor trade engine: buy/sell with NPCs.
/// Maps to CClient::Event_VendorBuy/Sell in Source-X.
/// </summary>
public static class VendorEngine
{
    /// <summary>Reference to the world for container lookups.</summary>
    public static GameWorld? World { get; set; }

    /// <summary>Source-X ePayGold reasons (game_enums.h:9).</summary>
    public const int PayGoldTrain = 0, PayGoldBuy = 1, PayGoldHire = 2;

    /// <summary>@PayGold: (payer, payee, amount, reason) -> the amount to charge.</summary>
    public static Func<Character, Character, long, int, long>? OnPayGold { get; set; }

    /// <summary>
    /// True when an NPC name carries a merchant role keyword. Legacy packs
    /// ship some trade NPCs as NPC=BRAIN_HUMAN (e.g. c_h_vendor keeps
    /// brain_vendor commented out) and lean on speech scripts, so the
    /// engine widens vendor detection by name.
    /// </summary>
    public static bool HasVendorNameKeyword(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        return lower.Contains("vendor") || lower.Contains("shopkeep") ||
               lower.Contains("merchant");
    }

    /// <summary>
    /// Single vendor-detection predicate shared by the dclick, context-menu,
    /// speech and trade-validation paths — they must agree, or a vendor that
    /// answers "buy" refuses the double-click (and vice versa).
    /// </summary>
    public static bool IsVendorLike(Character npc)
    {
        if (npc.IsPlayer) return false;
        // Upstream's own set, in full: a vendor is a HEALER, a BANKER, a VENDOR or a
        // STABLE brain (CCharNPC::IsVendor, CCharNPC.cpp:253). Accepting only VENDOR
        // left the stablemaster and the animal trainer - brain=Stable in the shipped
        // pack - out of every path that asks this question, so they answered nothing
        // when a customer said "buy" while a shop across the street did.
        if (IsVendorBrain(npc.NpcBrain)) return true;
        return npc.NpcBrain is Core.Enums.NpcBrainType.Human or Core.Enums.NpcBrainType.None
               && HasVendorNameKeyword(npc.Name);
    }

    /// <summary>The brains upstream counts as a vendor (CCharNPC.cpp:253).</summary>
    public static bool IsVendorBrain(Core.Enums.NpcBrainType brain) =>
        brain is Core.Enums.NpcBrainType.Vendor or Core.Enums.NpcBrainType.Healer
              or Core.Enums.NpcBrainType.Banker or Core.Enums.NpcBrainType.Stable;

    /// <summary>The vendor box worn on one of the three vendor layers (STOCK, EXTRA,
    /// BUYS), created on first use. Source-X CChar::GetBank (CCharStatus.cpp:141):
    /// only a vendor has these boxes, and each is an ITEMID_VENDOR_BOX.</summary>
    public static Item? GetVendorBox(Character vendor, Core.Enums.Layer layer)
    {
        if (layer is not (Core.Enums.Layer.VendorStock or Core.Enums.Layer.VendorExtra
                or Core.Enums.Layer.VendorBuy))
            return null;
        if (!IsVendorLike(vendor))
            return null;
        var box = vendor.GetEquippedItem(layer);
        if (box != null && !box.IsDeleted)
            return box;
        if (World == null)
            return null;
        box = World.CreateItem();
        box.BaseId = 0x408D; // ITEMID_VENDOR_BOX
        box.ItemType = Core.Enums.ItemType.EqVendorBox;
        vendor.Equip(box, layer);
        return box;
    }

    /// <summary>Is the vendor's STOCK box real goods rather than a template list?
    /// A player vendor is its owner's pet, and Source-X never restocks a pet
    /// (NPC_Vendor_Restock, CCharNPCAct_Vendor.cpp:41): what sits in its stock is what
    /// the owner put there. Only an ownerless vendor's stock is the virtual,
    /// template-rebuilt list.</summary>
    public static bool HasRealStock(Character vendor) => vendor.OwnerSerial.IsValid;

    /// <summary>Why <see cref="ProcessBuy(Character, Character, IReadOnlyList{TradeEntry}, out VendorBuyRefusal, Func{Character, Item, Character?}?)"/>
    /// refused a purchase, so the caller can speak the line Source-X speaks for it
    /// (Event_VendorBuy, CClientEvent.cpp:1158).</summary>
    public enum VendorBuyRefusal
    {
        None,
        /// <summary>Bad vendor, bad stock serial, out of stock, no pack (cheater codes 1-3).</summary>
        Invalid,
        /// <summary>The total exceeded the cost ceiling (cheater code 4, DEFMSG_NPC_VENDOR_CANTFULFILL).</summary>
        CostTooHigh,
        /// <summary>More than the vendor has, or none (cheater code 3, DEFMSG_NPC_VENDOR_CANTFULFILL).</summary>
        CantFulfill,
        /// <summary>Not enough gold (DEFMSG_NPC_VENDOR_NOMONEY1).</summary>
        NoMoney,
        /// <summary>Hair for a non-playable body, a beard for a body that grows none
        /// (DEFMSG_NPC_VENDOR_CANTBUY).</summary>
        CantBuy,
        /// <summary>A figurine whose creature would exceed the follower slots
        /// (DEFMSG_PETSLOTS_TRY_CONTROL).</summary>
        PetSlots,
    }

    /// <summary>The ceiling on one purchase's total. Source-X Event_VendorBuy
    /// (CClientEvent.cpp:1164): half of INT64 when FEATURE_TOL_VIRTUALGOLD pays from
    /// the 64-bit virtual purse, half of INT32 when coins are counted.</summary>
    public static long MaxBuyCost => VirtualGold.Enabled ? long.MaxValue / 2 : int.MaxValue / 2;

    /// <summary>
    /// Process a buy request from player to vendor.
    /// Returns total gold cost. Negative = refused.
    /// </summary>
    public static long ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items) =>
        ProcessBuy(player, vendor, items, out _, null);

    /// <summary>Process a buy request. <paramref name="useFigurine"/> is Use_Figurine
    /// (CCharUse.cpp:1115): a figurine bought from an NPC vendor is not handed over,
    /// it makes the buyer a pet - the client layer supplies the creature factory.</summary>
    public static long ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items,
        out VendorBuyRefusal refusal, Func<Character, Item, Character?>? useFigurine,
        Action? onHairCut = null)
    {
        refusal = VendorBuyRefusal.Invalid;
        if (!IsVendorLike(vendor))
            return -1;
        // Also checked in the packet handler; repeated here so no other caller can
        // commit a transaction for a ghost.
        if (player.IsDead)
            return -1;
        if (World == null)
            return -1;

        // Resolve every buy entry against an ACTUAL item inside this
        // vendor's stock container (LAYER 26 / 27). The client sends back
        // the stock item's serial; validating containment here prevents a
        // crafted packet from "buying" an arbitrary world item by serial,
        // and lets us decrement the virtual stock from the real entry.
        var stockA = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        var stockB = vendor.GetEquippedItem(Core.Enums.Layer.VendorExtra);
        uint stockUidA = stockA?.Uid.Value ?? 0;
        uint stockUidB = stockB?.Uid.Value ?? 0;

        // PacketVendorBuyReq::onReceive (receive.cpp:777-811): every line must name an
        // item that can change hands, lines naming the same item are combined, and
        // each is priced on the server from THAT item - GetVendorPrice with the
        // vendor's markup. An item worth nothing is not stocked ("Alas, I don't have
        // these goods currently stocked").
        var lines = new List<(Item Stock, long Amount, long Price)>(items.Count);
        foreach (var line in items)
        {
            var found = World.FindItem(line.ItemUid);
            if (found == null || found.IsDeleted || !IsValidSaleItem(found, buyFromVendor: true))
                return -1;
            // Kept on top of upstream: the serial must be in THIS vendor's stock
            // (LAYER 26 / 27). Upstream accepts any movable item on the shard by
            // serial, so a crafted packet could buy - and, from a player vendor, take
            // away - anyone's item at its vendor price.
            uint cont = found.ContainedIn.Value;
            if (cont == 0 || (cont != stockUidA && cont != stockUidB))
                return -1;
            int at = lines.FindIndex(l => ReferenceEquals(l.Stock, found));
            if (at >= 0)
            {
                lines[at] = (found, lines[at].Amount + line.Amount, lines[at].Price);
                continue;
            }
            long price = GetVendorPrice(found, GetVendorMarkup(vendor), forSelling: false);
            if (price <= 0)
                return -1;
            lines.Add((found, line.Amount, price));
        }

        // Event_VendorBuy (CClientEvent.cpp:1171): does the vendor really have that many?
        long totalCost = 0;
        long maxCost = MaxBuyCost;
        var resolved = new List<(Item Stock, int Amount, long Price)>(lines.Count);
        foreach (var (stockItem, lineAmount, serverPrice) in lines)
        {
            if (lineAmount <= 0 || lineAmount > stockItem.Amount)
            {
                refusal = VendorBuyRefusal.CantFulfill;
                return -1;
            }
            var entry = new TradeEntry { ItemUid = stockItem.Uid, Amount = (int)lineAmount };

            // The goods that are services, checked before anything is charged
            // (CClientEvent.cpp:1183-1222).
            switch (stockItem.ItemType)
            {
                case Core.Enums.ItemType.Figurine:
                    // OF_PetSlots: the figurine's FOLLOWERSLOTS times the amount must
                    // fit (FollowersUpdate check-only, which a GM ignores).
                    if (Character.FollowerCapApplies(player) && ResolveFigurineCreature(stockItem) != 0)
                    {
                        long slots = ReadDefNum(stockItem, "FOLLOWERSLOTS") * entry.Amount;
                        if (player.CurFollower + slots > player.MaxFollower)
                        {
                            refusal = VendorBuyRefusal.PetSlots;
                            return -1;
                        }
                    }
                    break;
                case Core.Enums.ItemType.Hair:
                    if (!IsPlayableBody(player.BodyId))
                    {
                        refusal = VendorBuyRefusal.CantBuy;
                        return -1;
                    }
                    break;
                case Core.Enums.ItemType.Beard:
                    // CREID_MAN or CREID_GARGMAN, or a GM.
                    if (player.BodyId != 0x0190 && player.BodyId != 0x029A &&
                        player.PrivLevel < Core.Enums.PrivLevel.GM)
                    {
                        refusal = VendorBuyRefusal.CantBuy;
                        return -1;
                    }
                    break;
            }

            totalCost += serverPrice * entry.Amount;
            if (totalCost > maxCost)
            {
                refusal = VendorBuyRefusal.CostTooHigh;
                return -1;
            }
            resolved.Add((stockItem, (int)entry.Amount, serverPrice));
        }

        var backpack = player.Backpack;
        if (backpack == null)
            return -1;

        // fBoss = NPC_IsOwnedBy(buyer) (CClientEvent.cpp:1240): the owner, or a GM in GM
        // mode above the vendor's plevel, takes the goods without paying.
        bool isBoss = IsVendorBoss(vendor, player);
        // A character name is chosen by the player, so it can never be an economic
        // privilege: "SphereBotanist" is a perfectly ordinary name, and it used to buy
        // for free. The exemption now requires the server to actually be driving this
        // character as a load-test bot.
        bool isBot = Diagnostics.BotEngine.IsLiveBotCharacter(player.Name);
        // @PayGold (CChar::PayGold, CCharAct.cpp:6082): the buyer's script may change
        // what this purchase costs - ARGN1 is the price, ARGN2 the reason (1 = buy).
        if (OnPayGold != null)
            totalCost = OnPayGold(player, vendor, totalCost, PayGoldBuy);

        if (!isBoss && !isBot)
        {
            // FEATURE_TOL_VIRTUALGOLD pays from the virtual purse (Event_VendorBuy,
            // CClientEvent.cpp:1251 and :1398); otherwise from the coins carried.
            if (VirtualGold.Enabled)
            {
                if (VirtualGold.Get(player) < totalCost)
                {
                    refusal = VendorBuyRefusal.NoMoney;
                    return -1;
                }
                VirtualGold.Set(player, VirtualGold.Get(player) - totalCost);
            }
            else
            {
                // Coins are counted and taken in 32-bit amounts. @PayGold may have
                // raised the price past what a pile of coins can be asked for; such a
                // price is simply more than anyone carries.
                // A negative price is read as the dword it is cast to
                // (ContentConsumeTest((dword)iCostTotal)): more than anyone has.
                long playerGold = CountGold(player);
                if (totalCost < 0 || totalCost > int.MaxValue || playerGold < totalCost)
                {
                    refusal = VendorBuyRefusal.NoMoney;
                    return -1;
                }

                RemoveGold(player, (int)totalCost);

                // Credit the vendor's purse, the bank box's MORE1, with what the
                // player paid: pVendor->GetBank()->m_itEqBankBox.m_Check_Amount +=
                // (uint)iCostTotal. Upstream does this only on the coin path - a
                // virtual-gold purchase never reaches the vendor's purse.
                if (totalCost > 0)
                    SetVendorGold(vendor, unchecked((uint)GetVendorGold(vendor) + (uint)totalCost));
            }
        }

        // A player vendor sells REAL objects out of its own store; an NPC vendor
        // sells from a virtual template list. Source-X Event_VendorBuy splits on
        // exactly this (CClientEvent.cpp:1352) and the two halves are not
        // interchangeable: cloning a player vendor's item hands the buyer a copy
        // with a new uid and an empty inside, and destroys the original.
        bool playerVendor = vendor.OwnerSerial.IsValid;

        foreach (var (stock, amount, _) in resolved)
        {
            if (playerVendor && stock.Amount <= amount)
            {
                // Whole item bought: hand over the object itself, so its contents,
                // uid and everything hanging off it survive the sale.
                World.FindItem(stock.ContainedIn)?.RemoveItem(stock);
                DeliverToBuyer(player, backpack, stock, allowStackMerge: false);
                continue;
            }

            if (!playerVendor)
            {
                switch (stock.ItemType)
                {
                    case Core.Enums.ItemType.Figurine:
                        // Use_Figurine once per unit, and nothing goes to the pack
                        // (CClientEvent.cpp:1304-1309, "goto do_consume").
                        for (int f = 0; f < amount; f++)
                            useFigurine?.Invoke(player, stock);
                        if (stock.Amount <= amount)
                            World.RemoveItem(stock);
                        else
                            stock.Amount -= (ushort)amount;
                        continue;
                    case Core.Enums.ItemType.Hair:
                    case Core.Enums.ItemType.Beard:
                        EquipBoughtHair(player, stock);
                        // The vendor slashes, the buyer hears the snip (CClientEvent.cpp:1319-1320).
                        onHairCut?.Invoke();
                        // Upstream BREAKS out of the switch here rather than skipping
                        // the delivery below, so the buyer also receives an ordinary
                        // copy in the pack (CClientEvent.cpp:1311-1322).
                        break;
                }
            }

            // Materialise the purchased item. Full-clone the stock entry (Source-X
            // CreateDupeItem) so per-instance state — tags, durability, price,
            // more-fields — travels with it, not just id/hue/name.
            //
            // Source-X delivers a non-stackable multi-buy as separate objects with
            // Amount=1 (CClientEvent.cpp:1328); one object carrying Amount=3 is not
            // three swords to anything downstream that counts objects.
            int perObject = stock.IsStackable ? amount : 1;
            int objects = stock.IsStackable ? 1 : amount;

            for (int n = 0; n < objects; n++)
            {
                var newItem = World.CreateItem();
                newItem.CopyStackInstanceStateFrom(stock);
                newItem.Amount = (ushort)Math.Max(1, Math.Min(perObject, ushort.MaxValue));
                DeliverToBuyer(player, backpack, newItem, allowStackMerge: true);
            }

            // Decrement the virtual stock; a depleted entry is removed.
            // When the whole container empties it is rebuilt from the
            // SELL template the next time the vendor is opened.
            if (stock.Amount <= amount)
                World.RemoveItem(stock);
            else
                stock.Amount -= (ushort)amount;
        }

        refusal = VendorBuyRefusal.None;
        return totalCost;
    }

    /// <summary>CChar::NPC_IsOwnedBy(buyer) with GMs allowed (CCharNPCStatus.cpp:449):
    /// the vendor itself, a GM in GM mode of a higher plevel than the vendor, or the
    /// owner of a pet vendor. Such a buyer takes goods without paying.</summary>
    public static bool IsVendorBoss(Character vendor, Character buyer)
    {
        if (ReferenceEquals(vendor, buyer))
            return true;
        if (buyer.IsGmMode)
            return buyer.PrivLevel > vendor.PrivLevel;
        return vendor.HasOwner(buyer.Uid);
    }

    /// <summary>Playable-character bodies (Source-X IsPlayableCharacter: human, elf
    /// or gargoyle, CCharStatus.cpp:99).</summary>
    private static bool IsPlayableBody(ushort bodyId) => bodyId is
        0x190 or 0x191 or 0x192 or 0x193 or
        0x25D or 0x25E or 0x25F or 0x260 or
        0x29A or 0x29B or 0x2B6 or 0x2B7;

    /// <summary>A hairdresser's service (CClientEvent.cpp:1311-1322): a copy of the
    /// stock hair or beard is worn at once - the previous one is deleted, as
    /// CanEquipLayer does for those two layers (CCharStatus.cpp:463) - and starts its
    /// 55000-second growing timer.</summary>
    private static void EquipBoughtHair(Character player, Item stock)
    {
        if (World == null) return;
        var layer = stock.ItemType == Core.Enums.ItemType.Beard
            ? Core.Enums.Layer.FacialHair
            : Core.Enums.Layer.Hair;
        var previous = player.GetEquippedItem(layer);
        if (previous != null && !previous.IsDeleted)
        {
            player.Unequip(layer);
            World.RemoveItem(previous);
        }
        var hair = World.CreateItem();
        hair.CopyStackInstanceStateFrom(stock);
        hair.Amount = 1;
        if (!player.Equip(hair, layer))
        {
            World.RemoveItem(hair);
            return;
        }
        hair.TrySetProperty("TIMER", "55000");
    }

    /// <summary>The creature a figurine names: its MORE1 (m_itFigurine.m_ID), else
    /// its definition's TDATA3 (FindCharTrack, CCharUse.cpp:1158-1166). 0 when it
    /// names none.</summary>
    public static int ResolveFigurineCreature(Item figurine)
    {
        if (figurine.More1 != 0)
            return (int)figurine.More1;
        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
            Definitions.ItemDefHelper.ResolveInstanceDefIndex(figurine));
        if (def == null)
            return 0;
        return def.TData3 != 0
            ? (int)def.TData3
            : SphereNet.Game.Definitions.DefinitionLoader.ResolveCharDefIndexByName(def.TData3Name);
    }

    /// <summary>A DEF number read the way GetDefNum(key, true) reads one: the
    /// instance first, then its definition; 0 when neither sets it.</summary>
    private static long ReadDefNum(Item item, string key)
    {
        if (item.TryGetTag(key, out string? own) && ScriptNumber.TryParseToken(own ?? "", out long v))
            return v;
        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
            Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
        return def?.TagDefs.Get(key) is { } dv && ScriptNumber.TryParseToken(dv, out long d) ? d : 0;
    }

    /// <summary>Hand a bought object to the buyer: into the pack when it fits and
    /// they can carry it, otherwise at their feet (Source-X ItemBounce). A paid item
    /// is never orphaned.</summary>
    private static void DeliverToBuyer(Character player, Item backpack, Item item, bool allowStackMerge)
    {
        if (World == null) return;

        bool overloaded = player.PrivLevel < Core.Enums.PrivLevel.GM && !player.CanCarry(item);
        Item? delivered = overloaded
            ? null
            : (allowStackMerge ? backpack.TryAddItemWithStack(item) : (backpack.TryAddItem(item) ? item : null));

        if (delivered == null)
        {
            item.ContainedIn = Serial.Invalid;
            World.PlaceItemWithDecay(item, player.Position);
        }
        else if (delivered != item)
        {
            // The clone merged into an existing pile. Remove the transient world
            // object that supplied the amount.
            World.RemoveItem(item);
        }
    }

    /// <summary>The container a player vendor keeps bought goods in (Source-X
    /// LAYER_VENDOR_EXTRA). Null when the vendor has none.</summary>
    private static Item? GetVendorExtraContainer(Character vendor) =>
        vendor.GetEquippedItem(Core.Enums.Layer.VendorExtra);

    /// <summary>Source-X sm_VendorLayers (CCharNPCAct_Vendor.cpp).</summary>
    private static readonly Core.Enums.Layer[] s_vendorLayers =
    [
        Core.Enums.Layer.VendorStock, Core.Enums.Layer.VendorExtra, Core.Enums.Layer.VendorBuy,
    ];

    /// <summary>Hand a dismissed player vendor's holdings back to its owner.
    ///
    /// Source-X does this the moment a vendor loses its owner: NPC_PetClearOwners
    /// moves the purse and every vendor-layer container's contents into the
    /// OWNER'S BANK and drops the vendor's invulnerability
    /// (CCharNPCPet.cpp:562-584). Releasing one here only cleared the ownership
    /// flags, so a shopkeeper's takings and everything it had bought from players
    /// went ownerless with it.
    ///
    /// Only the goods that REALLY exist come back: an ownerless vendor's SELL stock is
    /// a template rebuilt on demand and never persisted (see WorldSaver), but an owned
    /// vendor's stock is what its owner put there (<see cref="HasRealStock"/>).</summary>
    public static void ReturnHoldingsToOwner(Character vendor, Character owner)
    {
        if (World == null || vendor == owner)
            return;

        var bank = owner.GetEquippedItem(Core.Enums.Layer.BankBox);

        // Every vendor layer (sm_VendorLayers: STOCK, EXTRA, BUYS). The STOCK box is
        // included only when it holds real goods - an ownerless template list would be
        // minted, not returned.
        foreach (var layer in s_vendorLayers)
        {
            if (layer == Core.Enums.Layer.VendorStock && !HasRealStock(vendor))
                continue;
            if (vendor.GetEquippedItem(layer) is not { } extra || extra.IsDeleted)
                continue;
            foreach (var item in extra.Contents.ToList())
            {
                extra.RemoveItem(item);
                if (bank != null && !bank.IsDeleted && bank.TryAddItem(item))
                    continue;
                // No bank to put it in: at the owner's feet rather than nowhere.
                item.ContainedIn = Serial.Invalid;
                World.PlaceItemWithDecay(item, owner.Position);
            }
        }

        long purse = GetVendorGold(vendor);
        if (purse > 0)
        {
            SetVendorGold(vendor, 0);
            GiveGold(owner, (int)Math.Min(purse, int.MaxValue), bank);
        }

        vendor.ClearStatFlag(Core.Enums.StatFlag.Invul);
    }

    /// <summary>A vendor is being given to a new owner (Source-X NPC_PetSetOwner,
    /// CCharNPCPet.cpp:601). The previous owner, when there is one, first gets the
    /// purse and the goods back exactly as on a release (NPC_PetClearOwners, :562-584);
    /// then the purse is emptied - a wild vendor's restock cash is not the new
    /// owner's - and the vendor becomes invulnerable (:622-628). Must run while the
    /// vendor still records the previous owner, so its STOCK box counts as real goods.</summary>
    public static void ChangeVendorOwner(Character vendor, Character? previousOwner)
    {
        if (previousOwner != null && !previousOwner.IsDeleted)
            ReturnHoldingsToOwner(vendor, previousOwner);
        if (GetVendorGold(vendor) > 0)
            SetVendorGold(vendor, 0);
        vendor.SetStatFlag(Core.Enums.StatFlag.Invul);
    }

    /// <summary>Deliver gold into a chosen container (the reference hands the
    /// owner's BANK to AddGoldToPack when a vendor is dismissed), falling back to
    /// the pack when there is none.</summary>
    private static void GiveGold(Character ch, int amount, Item? container)
    {
        if (World == null || amount <= 0)
            return;
        if (container == null || container.IsDeleted)
        {
            GiveGoldToPack(ch, amount);
            return;
        }

        int remaining = amount;
        while (remaining > 0)
        {
            int pile = Math.Min(remaining, 60000);
            var gold = World.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = Core.Enums.ItemType.Gold;
            gold.Amount = (ushort)pile;
            gold.Name = "Gold";
            var delivered = container.TryAddItemWithStack(gold);
            if (delivered == null)
                World.PlaceItemWithDecay(gold, ch.Position);
            else if (delivered != gold)
                World.RemoveItem(gold);
            remaining -= pile;
        }
    }

    /// <summary>Move <paramref name="item"/> out of wherever it is and into the
    /// vendor's extra container. False when the container will not take it, in which
    /// case the caller falls back to Source-X's ownerless behaviour.</summary>
    private static bool MoveIntoVendorExtra(Item extra, Item item)
    {
        if (World == null) return false;

        var previous = World.FindItem(item.ContainedIn);
        previous?.RemoveItem(item);

        if (extra.TryAddItem(item))
            return true;

        // Put it back where it was rather than orphaning it mid-transfer.
        previous?.TryAddItem(item);
        return false;
    }

    /// <summary>
    /// Process a sell request from player to vendor.
    /// Returns total gold earned.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items) =>
        ProcessSell(player, vendor, items, out _);

    /// <summary>
    /// Sell with shortfall reporting: Source-X fills line by line and BREAKS
    /// with a shortfall flag when the vendor's purse runs out — a partial fill,
    /// not all-or-nothing. <paramref name="shortfall"/> lets the caller bark
    /// the "I have no money" line.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items, out bool shortfall)
    {
        shortfall = false;
        if (player.IsDead)
            return -1;
        if (!IsVendorLike(vendor))
            return 0;
        if (World == null)
            return 0;

        EnsureTemplateBuySamples(vendor);
        var buyBox = vendor.GetEquippedItem(Core.Enums.Layer.VendorBuy);
        bool stockLimit = (Clients.GameClient.ServerOptionFlags & Core.Enums.OptionFlags.VendorStockLimit) != 0;
        int convertFactor = -GetVendorMarkup(vendor);
        // Source-X tests STATF_PET; a player vendor is its owner's pet. It keeps what
        // it buys so it can resell it (CClientEvent.cpp:1521).
        var extra = vendor.OwnerSerial.IsValid ? GetVendorExtraContainer(vendor) : null;

        // Event_VendorSell (CClientEvent.cpp:1460-1545), line by line.
        long payout = 0;
        foreach (var entry in items)
        {
            // A serial that names nothing, or an item that cannot change hands, is a
            // cheat and ends the sale THERE (Event_VendorSell_Cheater, :1463): the
            // lines before it have already been taken and the purse debited, and the
            // seller is never paid for them - the gold is handed over after the loop.
            var found = World.FindItem(entry.ItemUid);
            if (found == null || found.IsDeleted || !IsValidSaleItem(found, buyFromVendor: true))
                return 0;

            // Do we still have it? Anything the seller holds counts - pack, bank,
            // worn - its top-level object must be the seller (:1468).
            if (!ReferenceEquals(found.GetTopLevelObj(), player))
                continue;

            // The sample is looked up for each line in turn, so an earlier line that
            // used up a sample under OF_VendorStockLimit leaves the later one without
            // a buyer.
            var sample = FindVendableItem(found, buyBox);
            if (sample == null)
                continue; // the vendor does not buy this (NEWBIE, wrong TYPE, not on its list)

            // Selling more than one holds sells what one holds (:1480).
            int amount = (ushort)Math.Clamp(entry.Amount, 0, ushort.MaxValue);
            if (found.Amount < amount)
                amount = found.Amount;
            long linePrice = SellUnitPrice(found, sample, convertFactor) * amount;

            // Can the vendor afford this line? The purse is the bank box's MORE1.
            long purse = GetVendorGold(vendor);
            if (linePrice > purse)
            {
                shortfall = true;
                break;
            }
            SetVendorGold(vendor, purse - linePrice);
            payout += linePrice;

            // The whole item goes - a container with whatever is in it (:1520).
            if (amount >= found.Amount)
            {
                int sold = found.Amount;
                if (extra == null || !MoveIntoVendorExtra(extra, found))
                    World.RemoveItem(found);
                if (stockLimit)
                    ConsumeSampleAmount(sample, sold);
            }
            else
            {
                if (extra != null)
                {
                    var kept = World.CreateItem();
                    kept.CopyStackInstanceStateFrom(found);
                    kept.Amount = (ushort)amount;
                    if (!MoveIntoVendorExtra(extra, kept))
                        World.RemoveItem(kept);
                }
                found.Amount -= (ushort)amount;
                if (stockLimit)
                    ConsumeSampleAmount(sample, amount);
            }
        }

        if (payout <= 0)
            return 0;

        // Pay the seller: into the virtual purse under FEATURE_TOL_VIRTUALGOLD
        // (Event_VendorSell, CClientEvent.cpp:1558), else as coins in the pack
        // (split into 60000-max piles).
        int gold = (int)Math.Min(payout, int.MaxValue);
        if (VirtualGold.Enabled)
            VirtualGold.Add(player, gold);
        else
            GiveGoldToPack(player, gold);
        return gold;
    }

    /// <summary>CItem::ConsumeAmount on a BUY sample (CItem.cpp:4344): what is left
    /// of the demand, the sample deleted once it is used up.</summary>
    private static void ConsumeSampleAmount(Item sample, int quantity)
    {
        if (World == null || sample.IsDeleted) return;
        if (quantity < sample.Amount)
            sample.Amount -= (ushort)quantity;
        else
            World.RemoveItem(sample);
    }

    /// <summary>The unit price a vendor pays for <paramref name="item"/>, matched
    /// against <paramref name="sample"/>: the sample's price when the sample carries
    /// an OVERRIDE.VALUE - "this NPC buys this item at a specific price" - else the
    /// player's own item's (CClientEvent.cpp:1488, send.cpp:3094).</summary>
    private static long SellUnitPrice(Item item, Item sample, int convertFactor) =>
        HasOverrideValue(sample)
            ? GetVendorPrice(sample, convertFactor, forSelling: true)
            : GetVendorPrice(item, convertFactor, forSelling: true);

    /// <summary>One row of the vendor's sell list: the player's item, the BUY
    /// sample it matched, how many may be sold and what one of them fetches.</summary>
    public readonly record struct VendorSellOffer(Item Item, Item Sample, int Amount, long UnitPrice);

    /// <summary>What a player may sell to a vendor: the sell list (Source-X
    /// PacketVendorSellList::fillSellList, send.cpp:3036). The pack is walked box by
    /// box - every searchable non-empty container is queued and walked after the
    /// one it sits in; a locked, bank, vendor or trade box is skipped whole - and each
    /// remaining item is offered when <see cref="FindVendableItem"/> finds a sample
    /// for it. Under OF_VendorStockLimit the amount offered is capped by the
    /// sample's.</summary>
    public static List<VendorSellOffer> GetSellOffers(Character player, Character vendor)
    {
        var offers = new List<VendorSellOffer>();
        var backpack = player.Backpack;
        if (backpack == null || !IsVendorLike(vendor))
            return offers;

        EnsureTemplateBuySamples(vendor);
        var buyBox = vendor.GetEquippedItem(Core.Enums.Layer.VendorBuy);
        bool stockLimit = (Clients.GameClient.ServerOptionFlags & Core.Enums.OptionFlags.VendorStockLimit) != 0;
        int convertFactor = -GetVendorMarkup(vendor);

        // The walk itself, as upstream writes it: when the row count reaches the
        // container limit only the box being read is abandoned (with whatever
        // sub-containers it had not yet queued); the boxes already queued are still
        // read, one row each (send.cpp:3110).
        int max = Item.MaxContainerItems;
        var boxes = new Queue<Item>();
        boxes.Enqueue(backpack);
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance) { backpack };
        while (boxes.Count > 0)
        {
            var box = boxes.Dequeue();
            foreach (var item in box.Contents.ToList())
            {
                if (item.IsDeleted)
                    continue;
                if (IsNonEmptyContainer(item))
                {
                    if (IsSearchable(item) && seen.Add(item))
                        boxes.Enqueue(item);
                    continue;
                }
                var sample = FindVendableItem(item, buyBox);
                if (sample == null)
                    continue;

                int amount = item.Amount;
                if (stockLimit && sample.Amount <= amount)
                    amount = sample.Amount;
                offers.Add(new VendorSellOffer(item, sample, amount, SellUnitPrice(item, sample, convertFactor)));
                if (offers.Count >= max)
                    break;
            }
        }
        return offers;
    }

    /// <summary>The quote a vendor gives for one item of the player's: the unit
    /// price and the sample it matched, or (0, null) when it does not buy it. The
    /// @Sell trigger is shown this same figure.</summary>
    public static (long UnitPrice, Item? Sample) GetSellQuote(Character vendor, Item item)
    {
        EnsureTemplateBuySamples(vendor);
        var sample = FindVendableItem(item, vendor.GetEquippedItem(Core.Enums.Layer.VendorBuy));
        return sample == null ? (0, null) : (SellUnitPrice(item, sample, -GetVendorMarkup(vendor)), sample);
    }

    private static bool IsNonEmptyContainer(Item item) =>
        Item.IsContainerItemType(item.ItemType) && item.Contents.Any(c => !c.IsDeleted);

    /// <summary>CItemContainer::IsSearchable (CItemContainer.cpp:760).</summary>
    private static bool IsSearchable(Item container) =>
        container.ItemType is not (Core.Enums.ItemType.ContainerLocked or Core.Enums.ItemType.EqBankBox
            or Core.Enums.ItemType.EqVendorBox or Core.Enums.ItemType.EqTradeWindow);

    /// <summary>CItemVendable::IsValidSaleItem (CItemVendable.cpp:237): can this item
    /// change hands with a vendor at all? It must be movable; going TO a vendor it
    /// must also be neither NEWBIE nor MOVE_NEVER; and coins are never traded.</summary>
    public static bool IsValidSaleItem(Item item, bool buyFromVendor)
    {
        if (!item.IsMovableType)
            return false;
        if (!buyFromVendor &&
            (item.IsAttr(Core.Enums.ObjAttributes.Newbie) || item.IsAttr(Core.Enums.ObjAttributes.Move_Never)))
            return false;
        if (item.ItemType == Core.Enums.ItemType.Coin)
            return false;
        return true;
    }

    /// <summary>Source-X CChar::NPC_FindVendableItem (CCharNPCStatus.cpp:603): the
    /// sample in the vendor's BUYS box that makes it want <paramref name="item"/>, or
    /// null. The sample is found by the item's full definition (ContentFind of
    /// RES_ITEMDEF, which walks searchable sub-containers) - two definitions that
    /// share one graphic are different goods - and must also have the same TYPE.</summary>
    public static Item? FindVendableItem(Item item, Item? buyBox)
    {
        if (buyBox == null || !IsValidSaleItem(item, buyFromVendor: false))
            return null;
        int defIndex = Objects.Items.ResourceMatch.ItemDefIndexOf(item);
        if (defIndex == 0)
            return null;
        var sample = FindResourceMatch(buyBox, defIndex, 255);
        if (sample == null || sample.ItemType != item.ItemType)
            return null;
        return sample;
    }

    /// <summary>CContainer::ContentFind for an RES_ITEMDEF id (CContainer.cpp:216).</summary>
    private static Item? FindResourceMatch(Item container, int defIndex, int descendLevels)
    {
        foreach (var candidate in container.Contents.ToList())
        {
            if (candidate.IsDeleted)
                continue;
            if (IsItemDefMatch(candidate, defIndex))
                return candidate;
            if (descendLevels <= 0 || !Item.IsContainerItemType(candidate.ItemType) || !IsSearchable(candidate))
                continue;
            var inner = FindResourceMatch(candidate, defIndex, descendLevels - 1);
            if (inner != null)
                return inner;
        }
        return null;
    }

    /// <summary>CItem::IsResourceMatch for an RES_ITEMDEF id (CItem.cpp:6027): the
    /// same definition, or - unless EF_Item_Strict_Comparison - boards standing in
    /// for logs and leather for hides.</summary>
    private static bool IsItemDefMatch(Item candidate, int defIndex) =>
        Objects.Items.ResourceMatch.IsMatch(candidate,
            Objects.Items.ResourceMatch.ForItemDef(defIndex));

    /// <summary>A BUY= template a vendor names but has not yet laid out as samples:
    /// a vendor saved before its BUYS box held the samples carries only the template
    /// name. Its samples are made the first time anyone sells to it - once; after
    /// that the box (even emptied by OF_VendorStockLimit) stays as it is until the
    /// next restock refills it.</summary>
    public static void EnsureTemplateBuySamples(Character vendor)
    {
        if (HasRealStock(vendor) || vendor.GetEquippedItem(Core.Enums.Layer.VendorBuy) != null)
            return;
        if (vendor.TryGetTag("VENDOR_BUY_LIST", out string? tpl) && !string.IsNullOrWhiteSpace(tpl))
            AddBuySamples(vendor, tpl!);
    }

    /// <summary>A BUY= line (Source-X ReadScriptReduced ITC_BUY, CChar.cpp:1343, and
    /// ReadTemplate, CItem.cpp:612): each entry of the template becomes a SAMPLE in
    /// the vendor's BUYS box, with the template's amount - the demand that
    /// OF_VendorStockLimit counts down. The samples are what
    /// <see cref="FindVendableItem"/> matches against.</summary>
    public static void AddBuySamples(Character vendor, string templateDefName)
    {
        var resources = SphereNet.Game.Definitions.DefinitionLoader.StaticResources;
        if (resources == null || World == null)
            return;
        var box = GetVendorBox(vendor, Core.Enums.Layer.VendorBuy);
        if (box == null)
            return;

        var tpl = SphereNet.Game.Definitions.DefinitionLoader.GetTemplateDef(templateDefName.Trim());
        if (tpl == null || tpl.Rows.Count == 0)
        {
            // Not a recipe (or one with only a random pool): a single definition.
            foreach (var (entryName, entryAmount) in SphereNet.Game.Definitions.TemplateEngine.EnumerateSequential(templateDefName))
                if (!AddBuySample(box, resources, entryName, entryAmount, out _))
                    break;
            return;
        }

        // ReadTemplate for a vendor (CItem.cpp:603-686), line by line: a BUY= or
        // ITEM= row lays a sample into the BUYS box, and the property and FUNC lines
        // after it apply to that sample - a recipe can give the vendor's sample its
        // own OVERRIDE.VALUE. A SELL= row belongs to the STOCK box, which the SELL
        // side lays out; here it only ends the previous sample's property lines.
        Item? current = null;
        foreach (var row in tpl.Rows)
        {
            switch (row.Kind)
            {
                case SphereNet.Scripting.Definitions.TemplateRowKind.Vendor
                    when string.Equals(row.Key, "BUY", StringComparison.OrdinalIgnoreCase):
                case SphereNet.Scripting.Definitions.TemplateRowKind.Item:
                    if (!AddBuySample(box, resources, row.Entry!.DefName, row.Entry.Amount, out current))
                        return;
                    break;
                case SphereNet.Scripting.Definitions.TemplateRowKind.Vendor:
                case SphereNet.Scripting.Definitions.TemplateRowKind.Container:
                    current = null;
                    break;
                case SphereNet.Scripting.Definitions.TemplateRowKind.Func:
                    if (current == null) break;
                    SphereNet.Game.Definitions.TemplateEngine.FunctionRowHook?.Invoke(current, row.Key, row.Value.Trim(), box);
                    if (current.IsDeleted) current = null;
                    break;
                default:
                    current?.TrySetProperty(row.Key, row.Value);
                    break;
            }
        }
    }

    /// <summary>One BUY sample: the definition (or a pick from a pool) made into the
    /// BUYS box with the row's amount. False when the box is full.</summary>
    private static bool AddBuySample(Item box, SphereNet.Scripting.Resources.ResourceHolder resources,
        string entryName, int entryAmount, out Item? sample)
    {
        sample = null;
        string picked = SphereNet.Game.Definitions.TemplateEngine.PickRandomItemDefName(entryName);
        if (string.IsNullOrWhiteSpace(picked))
            return true;
        int defIndex = SphereNet.Game.Definitions.TemplateEngine.ResolveItemDefIndex(resources, picked);
        ushort dispId = defIndex == 0 ? (ushort)0 : SphereNet.Game.Definitions.TemplateEngine.ResolveDispId(resources, picked);
        if (defIndex == 0 || dispId == 0)
            return true;
        var made = World!.CreateItem();
        made.BaseId = dispId;
        Definitions.ItemDefHelper.ApplyInstanceMetadata(made, defIndex, setDisplayId: false, setName: true);
        if (made.IsDeleted)
            return true;
        if (entryAmount > 1)
            made.Amount = (ushort)Math.Min(entryAmount, ushort.MaxValue);
        if (!box.TryAddItem(made))
        {
            World.RemoveItem(made);
            return false;
        }
        sample = made;
        return true;
    }

    // ---- Vendor money pool (Source-X m_itEqBankBox.m_Check_Amount): the vendor's
    // purse is its BANK BOX's MORE1 (CItem.h:193), the field VENDGOLD reads and
    // writes (CCharNPC.cpp:113/:198). Buying credits it, selling debits it, a
    // restock tops it up to the box's MORE2 (m_Check_Restock). A save written by
    // Source-X keeps the purse there and nowhere else. ----

    /// <summary>Gold the vendor purse is topped up to at each restock when the
    /// vendor names no amount of its own: m_Check_Restock defaults to 10000
    /// (CItemContainer.cpp:1081-1084).</summary>
    public static int RestockGold { get; set; } = 10000;

    /// <summary>The restock target for this vendor: its bank box's MORE2
    /// (m_itEqBankBox.m_Check_Restock, CItem.h:197) when set, else
    /// <see cref="RestockGold"/>.</summary>
    public static long RestockGoldFor(Character vendor)
    {
        var bank = vendor.GetEquippedItem(Core.Enums.Layer.BankBox);
        return bank is { More2: > 0 } ? bank.More2 : RestockGold;
    }

    /// <summary>The tag an earlier SphereNet kept the purse in, before the purse
    /// moved to the bank box's MORE1.</summary>
    public const string LegacyVendorGoldTag = "VENDOR_GOLD";

    /// <summary>Source-X always tracks vendor funds; kept for API compatibility.</summary>
    public static bool VendorTracksMoney(Character vendor) =>
        vendor.GetEquippedItem(Core.Enums.Layer.BankBox) != null ||
        vendor.TryGetTag(LegacyVendorGoldTag, out _);

    /// <summary>The vendor's purse: its bank box's MORE1, read as the unsigned
    /// m_Check_Amount it is. A purse still held in the legacy tag is folded in first.</summary>
    public static long GetVendorGold(Character vendor)
    {
        MigrateLegacyVendorGold(vendor);
        var bank = vendor.GetEquippedItem(Core.Enums.Layer.BankBox);
        return bank == null || bank.IsDeleted ? 0 : bank.More1;
    }

    /// <summary>Set the vendor's purse (the bank box's MORE1, made when the vendor
    /// has none, as GetBank makes it). The value is stored as the 32-bit unsigned
    /// field upstream stores: VENDGOLD=-1 reads back as 4294967295.</summary>
    internal static void SetVendorGold(Character vendor, long amount)
    {
        MigrateLegacyVendorGold(vendor);
        var bank = vendor.GetBankBoxSafe();
        if (bank == null)
            return;
        bank.More1 = amount > uint.MaxValue ? uint.MaxValue : unchecked((uint)amount);
    }

    /// <summary>Move a purse an earlier SphereNet kept in TAG.VENDOR_GOLD into the
    /// bank box's MORE1, once, and drop the tag so it is never counted again.
    ///
    /// When the save also carries a MORE1 the two were never the same money: that
    /// SphereNet read and wrote only the tag, so MORE1 is whatever the save brought
    /// with it from Source-X and the tag is what was earned and spent since. An
    /// owned vendor's purse is its owner's takings, so both are kept (added). An
    /// ownerless shopkeeper's purse is petty cash refilled to the same restock level
    /// on both sides, so adding would double it; the larger of the two is kept.
    /// Returns true when a tag was folded in.</summary>
    public static bool MigrateLegacyVendorGold(Character vendor)
    {
        if (!vendor.TryGetTag(LegacyVendorGoldTag, out string? raw))
            return false;
        vendor.RemoveTag(LegacyVendorGoldTag);
        if (vendor.IsPlayer)
            return true;
        long tagged = ScriptNumber.TryParseLong(raw, out long t) ? Math.Max(0, t) : 0;
        var bank = vendor.GetBankBoxSafe();
        if (bank == null)
            return true;
        long current = bank.More1;
        long merged = vendor.OwnerSerial.IsValid ? current + tagged : Math.Max(current, tagged);
        bank.More1 = (uint)Math.Min(merged, uint.MaxValue);
        return true;
    }

    /// <summary>Source-X NPC_VendorGetChkVerb PC_CASH (CCharNPCPet.cpp:304): the
    /// purse above one day's wage (the chardef's HIREDAYWAGE) goes to
    /// <paramref name="owner"/> and the wage stays in the purse. Returns the amount
    /// handed over (0 when the purse holds no more than the wage).</summary>
    public static int DispenseVendorGold(Character vendor, Character owner)
    {
        long purse = GetVendorGold(vendor);
        uint wage = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(vendor.CharDefIndex)?.HireDayWage ?? 0;
        if (purse <= wage)
            return 0;
        int amount = (int)Math.Min(purse - wage, int.MaxValue);
        SetVendorGold(vendor, wage);
        GiveGoldToPack(owner, amount);
        return amount;
    }

    /// <summary>Deliver <paramref name="amount"/> gold to a character's pack,
    /// split into piles (Source-X CItem::MakeGold), dropping overflow at the feet.</summary>
    /// <summary>The most gold one call may hand over. Upstream stops at the same
    /// number and logs when it has to (AddGoldToPack, CCharAct.cpp:222): the gold is
    /// paid out in stacks, so an unbounded amount is an unbounded number of items -
    /// sixty-five million arrives as more than a thousand of them, and a script that
    /// asks for a billion would make thirty-five thousand in one call.</summary>
    public const int MaxGoldPerGift = 25_000_000;

    /// <summary>Reported when a payout is capped, so the line that asked for it can be
    /// found. Wired to the server log.</summary>
    public static Action<string>? OnGoldCapped;

    public static void GiveGoldToPack(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;

        if (amount > MaxGoldPerGift)
        {
            OnGoldCapped?.Invoke(
                $"gold payout of {amount} to 0x{ch.Uid.Value:X8} capped at {MaxGoldPerGift}");
            amount = MaxGoldPerGift;
        }

        var backpack = ch.Backpack;
        int remaining = amount;
        while (remaining > 0 && backpack != null)
        {
            int pile = Math.Min(remaining, 60000);
            var gold = World.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = Core.Enums.ItemType.Gold;
            gold.Amount = (ushort)pile;
            gold.Name = "Gold";
            Item? delivered = ch.PrivLevel < Core.Enums.PrivLevel.GM && !ch.CanCarry(gold)
                ? null
                : backpack.TryAddItemWithStack(gold);
            if (delivered == null)
                World.PlaceItemWithDecay(gold, ch.Position);
            else if (delivered != gold)
                World.RemoveItem(gold);
            remaining -= pile;
        }
    }

    /// <summary>Default VENDORMARKUP percent (Source-X g_Cfg.m_iVendorMarkup).</summary>
    public static int DefaultVendorMarkup { get; set; } = 15;

    /// <summary>VENDORMAXSELL — most units of one line a vendor offers in a single
    /// transaction (Source-X m_iVendorMaxSell, default 255). Applied when the shop list
    /// is built, so the client never shows a number the shop would then refuse.
    /// Zero or below means no cap.</summary>
    public static int VendorMaxSell { get; set; } = 255;

    /// <summary>Source-X NPC_GetVendorMarkup: vendor tag → region tag → config
    /// default. The markup is the vendor's profit margin percent.</summary>
    public static int GetVendorMarkup(Character vendor)
    {
        // Source-X NPC_GetVendorMarkup (CCharNPCStatus.cpp:332): a hired/owned vendor
        // sells at its owner's prices, no markup; then the vendor's tag, the
        // region's, the chardef's, the ini default. The value is used as written
        // (negative is a discount); GetVendorPrice floors the factor at -100.
        if (vendor.IsStatFlag(Core.Enums.StatFlag.Pet))
            return 0;
        if (vendor.TryGetTag("VENDORMARKUP", out string? v) && ScriptNumber.TryParseInt(v, out int mv))
            return mv;
        var region = World?.FindRegion(vendor.Position);
        if (region != null && region.TryGetTag("VENDORMARKUP", out string? rv) &&
            ScriptNumber.TryParseInt(rv, out int rmv))
            return rmv;
        var cdef = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(vendor.CharDefIndex);
        if (cdef?.TagDefs.Get("VENDORMARKUP") is { } cv && int.TryParse(cv, out int cmv))
            return cmv;
        return DefaultVendorMarkup;
    }

    /// <summary>What the vendor pays the player for <paramref name="item"/> when the
    /// item is valued on its own, without a BUY sample's price: CItemVendable::
    /// GetVendorPrice with forselling set and iConvertFactor = -markup. The vendor's
    /// sell list and payment go through <see cref="GetSellQuote"/>, which picks the
    /// matched sample's OVERRIDE.VALUE first.</summary>
    internal static int GetServerSellPrice(Character vendor, Item item) =>
        (int)Math.Min(GetVendorPrice(item, -GetVendorMarkup(vendor), forSelling: true), int.MaxValue);

    /// <summary>Source-X IMulDivLL (common.h:207): a*b/c rounded half up, one lower
    /// again when a*b is negative.</summary>
    private static long IMulDivLL(long a, long b, long c)
    {
        long ab = a * b;
        return ((ab + c / 2) / c) - (ab < 0 ? 1 : 0);
    }

    /// <summary>Source-X IMulDiv (common.h:192): the same rounding in 32-bit int
    /// arithmetic, which wraps for a product past int range.</summary>
    private static int IMulDiv(int a, int b, int c)
    {
        int ab = unchecked(a * b);
        return ((ab + c / 2) / c) - (ab < 0 ? 1 : 0);
    }

    /// <summary>Does the item carry an OVERRIDE.VALUE - its own TAG or, as
    /// GetKey("OVERRIDE.VALUE", true) also looks, its definition's?</summary>
    private static bool HasOverrideValue(Item item) => TryGetOverrideValue(item, out _);

    private static bool TryGetOverrideValue(Item item, out long value)
    {
        value = 0;
        string? raw;
        if (!item.TryGetTag("OVERRIDE.VALUE", out raw))
        {
            var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
                Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
            raw = def?.TagDefs.Get("OVERRIDE.VALUE");
            if (raw == null)
                return false;
        }
        if (raw != null && ScriptNumber.TryParseToken(raw.Trim(), out long v))
            value = v;
        return true;
    }

    /// <summary>Source-X CItemVendable::GetVendorPrice (CItemVendable.cpp:172). The
    /// value is OVERRIDE.VALUE; else, when a vendor is SELLING, the item's own PRICE
    /// (a player vendor's price tag) - "when selling an item, you never check the
    /// price to avoid exploit"; else the make value of its definition at its quality.
    /// A deed is worth what it deeds: the definition its MORE1 names, and 1 when that
    /// names nothing. The convert factor (the vendor's markup, negated when buying
    /// from a player) is applied last, floored at -100%. The result is the dword
    /// upstream returns: capped at 4294967295, a negative total wrapped.</summary>
    public static long GetVendorPrice(Item item, int convertFactor, bool forSelling)
    {
        long price = 0;
        if (TryGetOverrideValue(item, out long ov))
            price = ov;
        else if (!forSelling)
            price = item.TryGetTag("PRICE", out string? ps) && ScriptNumber.TryParseLong(ps, out long pv) && pv > 0
                ? pv
                : item.Price;

        if (price <= 0)
        {
            SphereNet.Scripting.Definitions.ItemDef? def;
            if (item.ItemType == Core.Enums.ItemType.Deed)
            {
                def = ResolveDeedDef(item);
                if (def == null)
                    return 1;
            }
            else
            {
                def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
                    Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
            }
            price = def == null ? 0 : GetMakeValue(def, item.Quality, 0);
        }

        price += IMulDivLL(price, Math.Max(convertFactor, -100), 100);
        return price > uint.MaxValue ? uint.MaxValue : unchecked((uint)price);
    }

    /// <summary>The definition a deed stands for: m_itDeed.m_Type, its MORE1
    /// (CItemVendable.cpp:205). A name - MORE1=m_small_brick_house, kept by name when
    /// it does not reduce to a number - is resolved to the itemdef or the multi
    /// definition it names; a number is read as an itemdef index, a multi one past
    /// ITEMID_MULTI (0x10000). Null when it names nothing.</summary>
    internal static SphereNet.Scripting.Definitions.ItemDef? ResolveDeedDef(Item deed)
    {
        var resources = SphereNet.Game.Definitions.DefinitionLoader.StaticResources;
        foreach (var key in DeedMoreNameTags)
        {
            if (!deed.TryGetTag(key, out string? name) || string.IsNullOrWhiteSpace(name))
                continue;
            if (ScriptNumber.TryParseToken(name.Trim(), out _))
                continue;
            var rid = resources?.ResolveDefName(name.Trim()) ?? ResourceId.Invalid;
            if (rid.IsValid && rid.Type == Core.Enums.ResType.ItemDef)
                return SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
            if (rid.IsValid && rid.Type == Core.Enums.ResType.MultiDef)
                return SphereNet.Game.Definitions.DefinitionLoader.GetMultiItemDef(rid.Index);
            return null;
        }

        uint more1 = deed.More1;
        if (more1 == 0)
            return null;
        return SphereNet.Game.Definitions.DefinitionLoader.GetItemDef((int)more1)
            ?? (more1 is >= ItemIdMulti and < ItemIdMulti + 0x10000
                ? SphereNet.Game.Definitions.DefinitionLoader.GetMultiItemDef((int)(more1 - ItemIdMulti))
                : null);
    }

    /// <summary>ITEMID_MULTI (uofiles_enums_itemid.h:1100).</summary>
    private const uint ItemIdMulti = 0x10000;

    /// <summary>Where an item keeps a MORE1 that names a definition rather than a
    /// number: the setter's MORE1_DEFNAME, or the raw MORE an itemdef handed over.</summary>
    private static readonly string[] DeedMoreNameTags = ["MORE1_DEFNAME", "MORE1", "MORE"];

    /// <summary>Source-X CItemBase::GetMakeValue (CItemBase.cpp:1026) for an item: the
    /// definition's VALUE range read linearly at the item's QUALITY - which is not
    /// limited to 0-100, so an exceptional piece at quality 200 is worth past the top
    /// of the range.</summary>
    internal static int GetMakeValue(Item item)
    {
        var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
            Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
        return idef == null ? 0 : GetMakeValue(idef, item.Quality, 0);
    }

    /// <summary>The value of a definition at a quality: lo + IMulDiv(hi - lo,
    /// quality * 10, 1000) (CValueRangeDef::GetLinear, CValueDefs.cpp:10). The two
    /// ends are taken as written - a descending VALUE=20,10 falls with quality - and
    /// absolute (a negative end is a "floating" value upstream). With no VALUE the
    /// reference works it out from what the item is made of (CalculateMakeValue at
    /// quality 0 and 100, then read linearly), so a craftable without a price is not
    /// free.</summary>
    internal static int GetMakeValue(SphereNet.Scripting.Definitions.ItemDef def, int quality, int depth)
    {
        int lo, hi;
        if (def.ValueMin != 0 || def.ValueMax != 0)
        {
            lo = Math.Abs(def.ValueMin);
            hi = Math.Abs(def.ValueMax);
        }
        else
        {
            lo = CalculateMakeValue(def, 0, depth);
            hi = CalculateMakeValue(def, 100, depth);
        }
        return unchecked(lo + IMulDiv(hi - lo, quality * 10, 1000));
    }

    /// <summary>Source-X CItemBase::CalculateMakeValue (CItemBase.cpp:943): the value of
    /// each RESOURCES item times its amount, plus each SKILLMAKE skill's VALUES read at
    /// the skill the recipe needs. Circular lists stop at 32 levels.</summary>
    private static int CalculateMakeValue(SphereNet.Scripting.Definitions.ItemDef def, int quality, int depth)
    {
        if (depth > 32) return 0;
        var resources = SphereNet.Game.Definitions.DefinitionLoader.StaticResources;
        long value = 0;
        if (resources != null)
        {
            foreach (var entry in SphereNet.Scripting.Resources.ResourceQtyList.Parse(def.ResourcesRaw))
            {
                var rid = resources.ResolveDefName(entry.Name);
                if (!rid.IsValid || rid.Type != Core.Enums.ResType.ItemDef) continue;
                var part = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
                if (part == null) continue;
                value += (long)GetMakeValue(part, quality, depth + 1) * entry.Quantity;
            }
        }
        foreach (var entry in SphereNet.Scripting.Resources.ResourceQtyList.Parse(def.SkillMakeRaw))
        {
            if (!SphereNet.Game.Definitions.DefinitionLoader.TryGetSkillIndexByName(entry.Name, out int skillIdx))
                continue;
            var skillDef = SphereNet.Game.Definitions.DefinitionLoader.GetSkillDef(skillIdx);
            if (skillDef == null) continue;
            int level = Math.Max(quality, (int)Math.Clamp(entry.Quantity, 0, int.MaxValue));
            value += skillDef.ValueCurve.GetLinear(level);
        }
        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    /// <summary>What an NPC vendor asks a player for <paramref name="item"/>
    /// (CItemVendable::GetVendorPrice with +markup, send.cpp:2308): OVERRIDE.VALUE,
    /// else the item's PRICE, else its make value; the vendor's markup on top; and
    /// 100000 when all of that comes to nothing. The markup was never added, and a
    /// valueless item sold for 1 gold.</summary>
    internal static long GetVendorSellToPlayerPrice(Character vendor, Item item)
    {
        long price = GetVendorPrice(item, GetVendorMarkup(vendor), forSelling: false);
        if (price <= 0)
            price = 100000;
        return price;
    }

    /// <summary>Count gold in player's backpack recursively.</summary>
    /// <summary>PAYFROMPACKONLY. False, as upstream (CServerConfig.cpp:237): a character
    /// pays from everything they CARRY, and the bank box is worn, so banked gold is
    /// spendable. True restricts it to the backpack.
    ///
    /// This was pack-only unconditionally, which is the stricter setting applied without
    /// anyone choosing it: a player who banked their gold was treated as having none.</summary>
    public static bool PayFromPackOnly { get; set; }

    /// <summary>Is this a coin pile? Public because half a dozen client paths asked the
    /// same question in three different spellings, and the narrowest of them - the bare
    /// graphic test - missed a pack whose gold is TYPE=t_gold on another graphic.</summary>
    public static bool IsGold(Item item) =>
        item.ItemType == Core.Enums.ItemType.Gold || item.BaseId == 0x0EED;

    /// <summary>The gold a character can pay with (CClientEvent.cpp:1261): the pack,
    /// and unless PAYFROMPACKONLY everything carried - the bank box included - on the
    /// shared resource walk, which for gold skips only a locked container
    /// (CContainer.cpp:444). A locked box's coins are not spendable, wherever it sits.</summary>
    public static long CountGold(Character ch)
    {
        if (World == null) return 0;

        if (PayFromPackOnly)
        {
            var pack = ch.Backpack;
            return pack != null ? ResourceMatch.Count(pack, ResourceMatch.Gold) : 0;
        }
        return ResourceMatch.Count(ch, ResourceMatch.Gold);
    }

    /// <summary>Take gold from the character the way a purchase is paid
    /// (CClientEvent.cpp:1405): ContentConsume on the pack, then - unless
    /// PAYFROMPACKONLY - whatever is still owed from everything carried. Locked
    /// containers are never touched.</summary>
    public static void RemoveGold(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;

        var pack = ch.Backpack;
        long remaining = pack != null
            ? ResourceMatch.Consume(pack, ResourceMatch.Gold, amount)
            : amount;
        if (!PayFromPackOnly && remaining > 0)
            ResourceMatch.Consume(ch, ResourceMatch.Gold, remaining);
    }

    private static Item? FindItemInBackpack(Character ch, Serial itemUid)
    {
        if (World == null) return null;
        var backpack = ch.Backpack;
        if (backpack == null) return null;
        return EnumerateContainerContentsRecursive(backpack)
            .FirstOrDefault(item => item.Uid == itemUid);
    }

    private static IEnumerable<Item> EnumerateContainerContentsRecursive(Item container)
    {
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Item>(container.Contents.Reverse());
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (item.IsDeleted)
                continue;
            if (!seen.Add(item))
                continue;

            yield return item;
            for (int i = item.Contents.Count - 1; i >= 0; i--)
                pending.Push(item.Contents[i]);
        }
    }

    /// <summary>Default restock interval in milliseconds (10 minutes).</summary>
    public const int DefaultRestockInterval = 600_000;

    /// <summary>
    /// Restock a vendor's inventory from their TAG.VENDORINV definition.
    /// TAG.VENDORINV format: "itemId1:amount1,itemId2:amount2,..."
    /// Called periodically by NPC tick or on first vendor interaction.
    /// </summary>
    public static void RestockVendor(Character vendor)
    {
        if (World == null) return;
        if (!IsVendorLike(vendor)) return;

        // Restock refills the vendor's purse first (Source-X: the vendor bank
        // is the buy fund) — even for vendors with no VENDORINV stock list, so
        // a buy-only vendor can still purchase from players. This top-up is the
        // shopkeeper's infinite buy fund; an OWNED vendor (player/pet vendor)
        // must NOT be topped up, or dispensing its purse to the owner (CASH)
        // would be a free-gold faucet. Owned vendors keep only real earnings.
        // CItemContainer::Restock for the bank box (CItemContainer.cpp:1079): an unset
        // m_Check_Restock (MORE2) becomes 10000 first, then the purse is raised to it.
        if (!vendor.OwnerSerial.IsValid)
        {
            var bank = vendor.GetBankBoxSafe();
            if (bank != null && bank.More2 == 0)
                bank.More2 = (uint)RestockGold;
            long restockTo = RestockGoldFor(vendor);
            if (GetVendorGold(vendor) < restockTo)
                SetVendorGold(vendor, restockTo);
        }

        // A player vendor's stock is its owner's goods; a pet is never restocked
        // (NPC_Vendor_Restock, CCharNPCAct_Vendor.cpp:41).
        if (HasRealStock(vendor))
            return;

        if (!vendor.TryGetTag("VENDORINV", out string? invDef) || string.IsNullOrEmpty(invDef))
            return;

        // Stock the dedicated vendor STOCK container (LAYER 26), the same
        // container the buy gump reads from — not the regular backpack,
        // which the display path ignores. The container and its contents
        // are virtual (excluded from the world save, rebuilt on demand).
        var stock = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        if (stock == null)
        {
            stock = World.CreateItem();
            stock.BaseId = 0x408D; // i_vendor_box (vendor stock graphic)
            vendor.Equip(stock, Core.Enums.Layer.VendorStock);
        }

        // Parse "itemId:amount,itemId:amount,..."
        var entries = invDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existing = new Dictionary<ushort, int>();

        // Count existing stock
        foreach (var item in World.GetContainerContents(stock.Uid))
        {
            if (item.IsDeleted) continue;
            existing.TryGetValue(item.BaseId, out int count);
            existing[item.BaseId] = count + item.Amount;
        }

        foreach (var entry in entries)
        {
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            ushort itemId;
            if (parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                (parts[0].StartsWith('0') && parts[0].Length > 1))
                ushort.TryParse(parts[0].AsSpan(parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    System.Globalization.NumberStyles.HexNumber, null, out itemId);
            else
                ushort.TryParse(parts[0], out itemId);

            if (itemId == 0) continue;
            if (!int.TryParse(parts[1], out int maxAmount)) continue;

            existing.TryGetValue(itemId, out int currentAmount);
            int deficit = maxAmount - currentAmount;
            if (deficit <= 0) continue;

            // Create restocked items
            var newItem = World.CreateItem();
            newItem.BaseId = itemId;
            newItem.Amount = (ushort)Math.Min(deficit, 60000);

            // Ordinary stock reads ITEMDEF VALUE when quoted; do not freeze PRICE.
            if (!stock.TryAddItem(newItem))
                World.RemoveItem(newItem);
        }

        vendor.SetTag("RESTOCK_TIME", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    /// <summary>Check if vendor needs restocking (based on RESTOCK_TIME tag).</summary>
    public static bool NeedsRestock(Character vendor, int intervalMs = DefaultRestockInterval)
    {
        if (!vendor.TryGetTag("RESTOCK_TIME", out string? timeStr) || !ScriptNumber.TryParseLong(timeStr, out long lastRestock))
            return true;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastRestock >= intervalMs;
    }
}

/// <summary>
/// Manages active trade sessions.
/// </summary>
public sealed class TradeManager
{
    private readonly Dictionary<Serial, SecureTrade> _activeTrades = [];
    private readonly Dictionary<uint, SecureTrade> _containerIndex = [];
    private uint _nextSessionId;

    public SecureTrade? GetTrade(Serial sessionId) =>
        _activeTrades.GetValueOrDefault(sessionId);

    public SecureTrade? FindByContainer(uint containerSerial) =>
        _containerIndex.GetValueOrDefault(containerSerial);

    public SecureTrade StartTrade(Character initiator, Character partner,
        Item initiatorContainer, Item partnerContainer)
    {
        var sessionId = new Serial(++_nextSessionId | 0x80000000);
        var trade = new SecureTrade(sessionId, initiator, partner,
            initiatorContainer, partnerContainer);
        _activeTrades[sessionId] = trade;
        _containerIndex[initiatorContainer.Uid.Value] = trade;
        _containerIndex[partnerContainer.Uid.Value] = trade;
        return trade;
    }

    public void EndTrade(SecureTrade trade)
    {
        if (!trade.IsCompleted)
            trade.Cancel();
        _activeTrades.Remove(trade.SessionId);
        _containerIndex.Remove(trade.InitiatorContainer.Uid.Value);
        _containerIndex.Remove(trade.PartnerContainer.Uid.Value);
    }

    public SecureTrade? FindTradeFor(Character ch) =>
        _activeTrades.Values.FirstOrDefault(t =>
            !t.IsCompleted && (t.Initiator == ch || t.Partner == ch));

    /// <summary>Return all trade-container items to their owners' backpacks.</summary>
    public static void ReturnTradeItems(GameWorld world, SecureTrade trade)
    {
        ReturnContainerItems(world, trade.Initiator, trade.InitiatorContainer);
        ReturnContainerItems(world, trade.Partner, trade.PartnerContainer);
    }

    private static void ReturnContainerItems(GameWorld world, Character owner, Item container)
    {
        foreach (var item in container.Contents.ToList())
        {
            container.RemoveItem(item);
            ReturnItemToCharacter(world, owner, item);
        }
    }

    /// <summary>Move one item into the character backpack (or feet if no pack).</summary>
    public static void ReturnItemToCharacter(GameWorld world, Character owner, Item item)
    {
        var pack = EnsureBackpack(world, owner);
        if (pack != null && pack.TryAddItem(item))
        {
            item.Position = new Point3D(0, 0, 0, owner.MapIndex);
            return;
        }

        // ItemBounce semantics: a full/missing pack never destroys or strands
        // returned trade goods; they land at the owner's feet.
        world.PlaceItemWithDecay(item, owner.Position);
    }

    private static Item? EnsureBackpack(GameWorld world, Character ch)
    {
        if (!ch.IsPlayer)
            return ch.Backpack;

        Item? pack = ch.GetEquippedItem(Core.Enums.Layer.Pack) ?? ch.Backpack;
        if (pack == null || pack.IsDeleted || world.FindItem(pack.Uid) == null)
        {
            pack = world.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = Core.Enums.ItemType.Container;
            pack.Name = "Backpack";
        }

        ch.Backpack = pack;
        ch.Equip(pack, Core.Enums.Layer.Pack);
        return pack;
    }

    /// <summary>Sum incoming trade weight against recipient carry capacity.</summary>
    public static bool CanAcceptTradeItems(Character recipient, GameWorld world, Item sourceContainer,
        out string? reason)
    {
        reason = null;
        if (recipient.IsDeleted || sourceContainer.IsDeleted)
        {
            reason = "Trade is no longer valid.";
            return false;
        }

        int incomingSlots = sourceContainer.Contents.Count(i => !i.IsDeleted);
        if (incomingSlots == 0)
            return true;

        var pack = EnsureBackpack(world, recipient);
        if (pack == null && recipient.IsPlayer)
        {
            reason = "You have no backpack.";
            return false;
        }

        if (pack != null && pack.ContentCount + incomingSlots > Item.MaxContainerItems)
        {
            reason = "Your backpack cannot hold any more items.";
            return false;
        }

        long maxWeightTenths = (long)Math.Max(0, recipient.MaxWeight) * Item.WeightUnits;
        long incomingTenths = 0;
        foreach (var item in sourceContainer.Contents)
        {
            if (item.IsDeleted) continue;
            incomingTenths += item.TotalWeightTenths;
            if (incomingTenths > int.MaxValue)
                break;
        }

        if ((long)recipient.GetTotalWeightTenths() + incomingTenths > maxWeightTenths)
        {
            reason = "You cannot carry that much.";
            return false;
        }

        return true;
    }
}
