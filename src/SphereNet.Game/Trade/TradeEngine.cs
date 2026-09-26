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

    /// <summary>
    /// Process a buy request from player to vendor.
    /// Returns total gold cost. Negative = insufficient gold.
    /// </summary>
    public static int ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
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

        long totalCost = 0;
        var resolved = new List<(Item Stock, int Amount, int Price)>(items.Count);
        var seenSerials = new HashSet<uint>();
        foreach (var entry in items)
        {
            if (entry.Amount <= 0 || entry.Amount > 999) return -1;
            // A crafted packet may repeat the same stock row. Validating each
            // line independently would let the combined amount exceed stock.
            if (!seenSerials.Add(entry.ItemUid.Value)) return -1;

            var stockItem = World.FindItem(entry.ItemUid);
            if (stockItem == null || stockItem.IsDeleted)
                return -1;
            uint cont = stockItem.ContainedIn.Value;
            if (cont == 0 || (cont != stockUidA && cont != stockUidB))
                return -1; // not part of this vendor's stock
            if (stockItem.Amount < entry.Amount)
                return -1; // not enough in stock

            // Price from THIS stock entry's own PRICE/tag — not a GetServerBuyPrice
            // lookup by BaseId, which returns the first same-BaseId stock item's
            // price. With two same-id entries at different prices the server could
            // otherwise charge a different price than the client's selected row.
            int serverPrice = GetVendorSellToPlayerPrice(vendor, stockItem);
            if (serverPrice <= 0) return -1;
            totalCost += (long)serverPrice * entry.Amount;
            resolved.Add((stockItem, (int)entry.Amount, serverPrice));
        }
        if (totalCost > int.MaxValue)
            return -1;

        var backpack = player.Backpack;
        if (backpack == null)
            return -1;

        bool isStaff = player.PrivLevel >= Core.Enums.PrivLevel.GM;
        // A character name is chosen by the player, so it can never be an economic
        // privilege: "SphereBotanist" is a perfectly ordinary name, and it used to buy
        // for free. The exemption now requires the server to actually be driving this
        // character as a load-test bot.
        bool isBot = Diagnostics.BotEngine.IsLiveBotCharacter(player.Name);
        bool isOwner = vendor.HasOwner(player.Uid);
        // @PayGold (CChar::PayGold, CCharAct.cpp:6082): the buyer's script may change
        // what this purchase costs - ARGN1 is the price, ARGN2 the reason (1 = buy).
        if (OnPayGold != null)
            totalCost = Math.Max(0, OnPayGold(player, vendor, totalCost, PayGoldBuy));

        if (!isStaff && !isBot && !isOwner)
        {
            // FEATURE_TOL_VIRTUALGOLD pays from the virtual purse (Event_VendorBuy,
            // CClientEvent.cpp:1251 and :1398); otherwise from the coins carried.
            if (VirtualGold.Enabled)
            {
                if (VirtualGold.Get(player) < totalCost)
                    return -1;
                VirtualGold.Set(player, VirtualGold.Get(player) - totalCost);
            }
            else
            {
                long playerGold = CountGold(player);
                if (playerGold < totalCost)
                    return -1;

                RemoveGold(player, (int)totalCost);
            }
        }
        else
        {
            totalCost = 0;
        }

        // Credit the vendor's money pool with what the player paid (Source-X
        // pVendor->GetBank()->m_itEqBankBox.m_Check_Amount += iCostTotal).
        if (totalCost > 0)
        {
            long currentPurse = GetVendorGold(vendor);
            SetVendorGold(vendor, currentPurse > long.MaxValue - totalCost
                ? long.MaxValue
                : currentPurse + totalCost);
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

        return (int)totalCost;
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

        // Source-X NPC_FindVendableItem: a vendor with a BUY list only buys items on
        // it. No list = buys anything (legacy behaviour preserved). Authoritative —
        // rejects a crafted packet that submits an item the vendor wouldn't list.
        var buyFilter = GetVendorBuyFilter(vendor);

        long totalValue = 0;
        var validated = new List<(TradeEntry Entry, Item Item, int ServerPrice)>();
        var seenSerials = new HashSet<uint>();
        foreach (var entry in items)
        {
            if (entry.Amount <= 0 || entry.Amount > ushort.MaxValue)
                return 0;
            if (!seenSerials.Add(entry.ItemUid.Value))
                return 0;

            var found = FindItemInBackpack(player, entry.ItemUid);
            if (found == null || found.IsDeleted || found.Amount < entry.Amount ||
                found.ItemType == Core.Enums.ItemType.Gold || found.BaseId == 0x0EED)
                return 0;
            // A vendor never buys an immovable item, nor a NON-EMPTY container: the
            // sale deletes the item, which would silently destroy a bag's contents
            // for the bag's price (item-loss exploit, worse on a no-buy-list vendor
            // that buys anything). Mirrors ServUO BaseVendor's sell guard
            // (skip !Movable and (Container && Items.Count != 0)).
            if (found.IsAttr(Core.Enums.ObjAttributes.Move_Never) ||
                (found.ItemType == Core.Enums.ItemType.Container && found.Contents.Count > 0))
                return 0;
            if (!buyFilter.Contains(found.BaseId))
                return 0; // vendor does not buy this item type

            // A valueless item sells for nothing rather than blocking the whole
            // sale (the reference pays GetVendorPrice as it comes, 0 included).
            int serverPrice = GetServerSellPrice(vendor, found);
            totalValue += (long)serverPrice * entry.Amount;
            if (totalValue > int.MaxValue)
                return 0;

            validated.Add((entry, found, serverPrice));
        }

        // Source-X fills line by line against the vendor's purse and BREAKS
        // with a shortfall when the next line can't be paid — earlier lines
        // still complete (partial fill).
        long purse = GetVendorGold(vendor);
        long payout = 0;
        var affordable = new List<(TradeEntry Entry, Item Item, int ServerPrice)>(validated.Count);
        foreach (var line in validated)
        {
            long linePrice = (long)line.ServerPrice * line.Entry.Amount;
            if (payout + linePrice > purse)
            {
                shortfall = true;
                break;
            }
            payout += linePrice;
            affordable.Add(line);
        }
        if (affordable.Count == 0)
            return 0;

        if (World != null)
        {
            var backpack = player.Backpack;
            if (backpack == null)
                return 0;

            // Source-X Event_VendorSell (CClientEvent.cpp:1521): a PLAYER vendor
            // keeps what it buys so it can resell it - the whole item moves into the
            // vendor's extra container, and a partial sale puts a dupe of the sold
            // amount there. Only an ownerless NPC vendor destroys the goods.
            // Deleting it for every vendor left an owned vendor paying out gold and
            // ending up with nothing to sell.
            // Source-X tests STATF_PET; a player vendor is its owner's pet.
            var extra = vendor.OwnerSerial.IsValid ? GetVendorExtraContainer(vendor) : null;

            foreach (var (entry, found, _) in affordable)
            {
                if (found.Amount <= entry.Amount)
                {
                    if (extra != null && MoveIntoVendorExtra(extra, found))
                        continue;
                    World.RemoveItem(found);
                }
                else
                {
                    if (extra != null)
                    {
                        var kept = World.CreateItem();
                        kept.CopyStackInstanceStateFrom(found);
                        kept.Amount = (ushort)entry.Amount;
                        if (!MoveIntoVendorExtra(extra, kept))
                            World.RemoveItem(kept);
                    }
                    found.Amount -= (ushort)entry.Amount;
                }
            }

            // Debit the vendor's purse by what was actually paid out.
            SetVendorGold(vendor, purse - payout);

            // Pay the seller: into the virtual purse under FEATURE_TOL_VIRTUALGOLD
            // (Event_VendorSell, CClientEvent.cpp:1558), else as coins in the pack
            // (split into 60000-max piles).
            if (VirtualGold.Enabled)
                VirtualGold.Add(player, payout);
            else
                GiveGoldToPack(player, (int)payout);
        }

        return (int)payout;
    }

    // ---- Vendor money pool (Source-X m_Check_Amount): ALWAYS tracked. Buying
    // credits the vendor's purse, selling debits it; the purse is topped up to
    // RestockGold at every restock, so a freshly opened vendor can buy. A
    // vendor with no VENDOR_GOLD tag simply has an empty purse until then. ----

    /// <summary>Gold the vendor purse is topped up to at each restock.</summary>
    public static int RestockGold { get; set; } = 2000;

    /// <summary>Source-X always tracks vendor funds; kept for API compatibility.</summary>
    public static bool VendorTracksMoney(Character vendor) =>
        vendor.TryGetTag("VENDOR_GOLD", out string? s) && !string.IsNullOrWhiteSpace(s);

    public static long GetVendorGold(Character vendor) =>
        vendor.TryGetTag("VENDOR_GOLD", out string? s) && long.TryParse(s, out long g)
            ? Math.Max(0, g) : 0;

    private static void SetVendorGold(Character vendor, long amount) =>
        vendor.SetTag("VENDOR_GOLD", Math.Max(0, amount).ToString());

    /// <summary>Source-X NPC_VendorGetChkVerb PC_CASH: hand the whole vendor purse
    /// to <paramref name="owner"/> and zero it. Only real earnings accumulate on
    /// an owned vendor (restock never tops up an owned purse), so this cannot
    /// mint gold. Returns the amount dispensed (0 if the purse was empty).</summary>
    public static int DispenseVendorGold(Character vendor, Character owner)
    {
        int amount = (int)GetVendorGold(vendor);
        if (amount <= 0) return 0;
        SetVendorGold(vendor, 0);
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

    /// <summary>The item BaseIds a vendor will buy (Source-X NPC_FindVendableItem,
    /// CCharNPCStatus.cpp:603): the entries of its BUY= template (VENDOR_BUY_LIST)
    /// and the samples in its BUYS box (LAYER_VENDOR_BUYS), which is where upstream
    /// keeps the list and where an owner places samples through the SAMPLES verb.
    /// An empty set means the vendor buys nothing - with no list at all it used to
    /// buy anything a player offered.</summary>
    public static HashSet<ushort> GetVendorBuyFilter(Character vendor)
    {
        var set = new HashSet<ushort>();
        if (vendor.TryGetTag("VENDOR_BUY_LIST", out string? tpl) && !string.IsNullOrWhiteSpace(tpl))
        {
            var resources = SphereNet.Game.Definitions.DefinitionLoader.StaticResources;
            foreach (var (defName, _) in SphereNet.Game.Definitions.TemplateEngine.EnumerateSequential(tpl!))
            {
                ushort id = resources != null
                    ? SphereNet.Game.Definitions.TemplateEngine.ResolveDispId(resources, defName)
                    : (ushort)0;
                if (id != 0) set.Add(id);
            }
        }
        if (vendor.GetEquippedItem(Core.Enums.Layer.VendorBuy) is { } buys)
            foreach (var sample in buys.Contents)
                if (!sample.IsDeleted && sample.BaseId != 0)
                    set.Add(sample.BaseId);
        return set;
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
        if (vendor.TryGetTag("VENDORMARKUP", out string? v) && int.TryParse(v, out int mv))
            return mv;
        var region = World?.FindRegion(vendor.Position);
        if (region != null && region.TryGetTag("VENDORMARKUP", out string? rv) &&
            int.TryParse(rv, out int rmv))
            return rmv;
        var cdef = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(vendor.CharDefIndex);
        if (cdef?.TagDefs.Get("VENDORMARKUP") is { } cv && int.TryParse(cv, out int cmv))
            return cmv;
        return DefaultVendorMarkup;
    }

    /// <summary>Server-side sell price: what the vendor pays the player for
    /// <paramref name="item"/> (Source-X CItemVendable::GetVendorPrice with
    /// forselling set and iConvertFactor = -markup, CClientEvent.cpp:1456).
    ///
    /// The value is OVERRIDE.VALUE, else the itemdef VALUE scaled by quality. The
    /// item's own PRICE is never read when selling - "When selling an item, you
    /// never check the price to avoid exploit" (CItemVendable.cpp:195): a player
    /// could price anything in their pack and sell it at that figure. The payout
    /// is then value - value*markup/100.</summary>
    internal static int GetServerSellPrice(Character vendor, Item item)
    {
        long value = item.TryGetTag("OVERRIDE.VALUE", out string? ov) && long.TryParse(ov, out long ovv)
            ? ovv
            : 0;
        if (value <= 0)
            value = GetMakeValue(item);
        int markup = GetVendorMarkup(vendor);
        value += IMulDivLL(value, Math.Max(-markup, -100), 100);
        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    /// <summary>Source-X IMulDivLL (common.h:207): a*b/c rounded half up, one lower
    /// again when a*b is negative.</summary>
    private static long IMulDivLL(long a, long b, long c)
    {
        long ab = a * b;
        return ((ab + c / 2) / c) - (ab < 0 ? 1 : 0);
    }

    /// <summary>Source-X CItemBase::GetMakeValue: the itemdef VALUE range read
    /// linearly by the item's quality (0-100).</summary>
    internal static int GetMakeValue(Item item)
    {
        var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
            Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
        return idef == null ? 0 : GetMakeValue(idef, Math.Clamp((int)item.Quality, 0, 100), 0);
    }

    /// <summary>The value of a definition at a quality. With no VALUE the reference
    /// works it out from what the item is made of (CalculateMakeValue at quality 0
    /// and 100, then read linearly), so a craftable without a price is not free.</summary>
    private static int GetMakeValue(SphereNet.Scripting.Definitions.ItemDef def, int quality, int depth)
    {
        int lo, hi;
        if (def.ValueMin != 0 || def.ValueMax != 0)
        {
            lo = def.ValueMin;
            hi = Math.Max(def.ValueMin, def.ValueMax);
        }
        else
        {
            lo = CalculateMakeValue(def, 0, depth);
            hi = CalculateMakeValue(def, 100, depth);
        }
        return lo + (int)((long)(hi - lo) * Math.Clamp(quality, 0, 100) / 100);
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
    internal static int GetVendorSellToPlayerPrice(Character vendor, Item item)
    {
        long price = item.TryGetTag("OVERRIDE.VALUE", out string? ov) && long.TryParse(ov, out long ovv)
            ? ovv
            : 0;
        if (price <= 0)
            price = item.TryGetTag("PRICE", out string? ps) && long.TryParse(ps, out long pv) && pv > 0
                ? pv
                : item.Price;
        if (price <= 0)
            price = GetMakeValue(item);
        price += IMulDivLL(price, Math.Max(GetVendorMarkup(vendor), -100), 100);
        if (price <= 0)
            price = 100000;
        return (int)Math.Min(price, int.MaxValue);
    }

    /// <summary>Count gold in player's backpack recursively.</summary>
    /// <summary>PAYFROMPACKONLY. False, as upstream (CServerConfig.cpp:237): a character
    /// pays from everything they CARRY, and the bank box is worn, so banked gold is
    /// spendable. True restricts it to the backpack.
    ///
    /// This was pack-only unconditionally, which is the stricter setting applied without
    /// anyone choosing it: a player who banked their gold was treated as having none.</summary>
    public static bool PayFromPackOnly { get; set; }

    /// <summary>The containers a character pays from, nearest to hand first.
    ///
    /// The backpack always comes first so a purchase spends loose coin before it reaches
    /// into the bank. With PAYFROMPACKONLY the list stops there; otherwise it continues
    /// with every other container worn, which is what upstream's ContentCount over the
    /// CHARACTER covers (send.cpp:243).</summary>
    private static IEnumerable<Item> PaymentContainers(Character ch)
    {
        var backpack = ch.Backpack;
        if (backpack != null)
            yield return backpack;
        if (PayFromPackOnly)
            yield break;

        for (int layer = 0; layer < (int)Core.Enums.Layer.Qty; layer++)
        {
            var worn = ch.GetEquippedItem((Core.Enums.Layer)layer);
            if (worn == null || worn.IsDeleted || ReferenceEquals(worn, backpack))
                continue;
            if (worn.ContentCount > 0 || worn.ItemType is Core.Enums.ItemType.Container
                    or Core.Enums.ItemType.EqBankBox)
                yield return worn;
        }
    }

    /// <summary>Is this a coin pile? Public because half a dozen client paths asked the
    /// same question in three different spellings, and the narrowest of them - the bare
    /// graphic test - missed a pack whose gold is TYPE=t_gold on another graphic.</summary>
    public static bool IsGold(Item item) =>
        item.ItemType == Core.Enums.ItemType.Gold || item.BaseId == 0x0EED;

    public static long CountGold(Character ch)
    {
        if (World == null) return 0;

        long total = 0;
        foreach (var container in PaymentContainers(ch))
            foreach (var item in EnumerateContainerContentsRecursive(container))
                if (IsGold(item))
                    total += item.Amount;
        return total;
    }

    /// <summary>Take gold from the character, backpack first.</summary>
    public static void RemoveGold(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;

        int remaining = amount;
        foreach (var container in PaymentContainers(ch))
        {
            if (remaining <= 0) break;
            foreach (var item in EnumerateContainerContentsRecursive(container).ToList())
            {
                if (remaining <= 0) break;
                if (!IsGold(item))
                    continue;

                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    World.RemoveItem(item);
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
        }
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
        if (!vendor.OwnerSerial.IsValid && GetVendorGold(vendor) < RestockGold)
            SetVendorGold(vendor, RestockGold);

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
        if (!vendor.TryGetTag("RESTOCK_TIME", out string? timeStr) || !long.TryParse(timeStr, out long lastRestock))
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
