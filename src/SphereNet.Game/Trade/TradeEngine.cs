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

    public bool ToggleAccept(Character from)
    {
        if (_isCompleted) return false;

        if (from == _initiator) _initiatorAccepted = !_initiatorAccepted;
        else if (from == _partner) _partnerAccepted = !_partnerAccepted;

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

    /// <summary>
    /// Process a buy request from player to vendor.
    /// Returns total gold cost. Negative = insufficient gold.
    /// </summary>
    public static int ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor)
            return -1;

        // Validate and calculate cost using server-side prices
        long totalCost = 0;
        foreach (var entry in items)
        {
            if (entry.Amount <= 0 || entry.Amount > 999) return -1;
            int serverPrice = GetServerBuyPrice(vendor, entry.ItemId);
            if (serverPrice <= 0) return -1;
            totalCost += (long)serverPrice * entry.Amount;
        }
        if (totalCost > int.MaxValue)
            return -1;

        var backpack = player.Backpack;
        if (backpack == null && World == null)
            return -1;

        bool isStaff = player.PrivLevel >= Core.Enums.PrivLevel.GM;
        bool isBot = Diagnostics.BotClient.IsBotCharName(player.Name ?? "");
        bool isOwner = vendor.HasOwner(player.Uid);
        if (!isStaff && !isBot && !isOwner)
        {
            long playerGold = CountGold(player);
            if (playerGold < totalCost)
                return -1;

            RemoveGold(player, (int)totalCost);
        }
        else
        {
            totalCost = 0;
        }

        if (World != null && backpack != null)
        {
            foreach (var entry in items)
            {
                var newItem = World.CreateItem();
                newItem.BaseId = entry.ItemId;
                newItem.Name = entry.Name;
                newItem.Amount = (ushort)Math.Max(1, Math.Min(entry.Amount, ushort.MaxValue));
                backpack.AddItemWithStack(newItem);
            }
        }

        return (int)totalCost;
    }

    /// <summary>
    /// Process a sell request from player to vendor.
    /// Returns total gold earned.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor)
            return 0;

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

            int serverPrice = GetServerSellPrice(vendor, found);
            if (serverPrice <= 0) return 0;
            totalValue += (long)serverPrice * entry.Amount;
            if (totalValue > int.MaxValue)
                return 0;

            validated.Add((entry, found, serverPrice));
        }

        if (World != null)
        {
            var backpack = player.Backpack;
            if (backpack == null)
                return 0;

            foreach (var (entry, found, _) in validated)
            {
                if (found.Amount <= entry.Amount)
                    found.Delete();
                else
                    found.Amount -= (ushort)entry.Amount;
            }

            // Add gold to player (split into 60000-max piles)
            int remaining = (int)totalValue;
            while (remaining > 0 && backpack != null)
            {
                int pile = Math.Min(remaining, 60000);
                var gold = World.CreateItem();
                gold.BaseId = 0x0EED;
                gold.ItemType = Core.Enums.ItemType.Gold;
                gold.Amount = (ushort)pile;
                gold.Name = "Gold";
                backpack.AddItem(gold);
                remaining -= pile;
            }
        }

        return (int)totalValue;
    }

    /// <summary>Look up server-side price for an item. Checks vendor stock PRICE tags, item def, fallback to baseId formula.</summary>
    internal static int GetServerBuyPrice(Character vendor, ushort itemId)
    {
        var stock = vendor.GetEquippedItem(Core.Enums.Layer.VendorStock);
        if (stock != null)
        {
            foreach (var item in stock.Contents)
            {
                if (item.BaseId == itemId &&
                    item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p))
                    return Math.Max(1, p);
            }
        }
        var pack = vendor.Backpack;
        if (pack != null)
        {
            foreach (var item in pack.Contents)
            {
                if (item.BaseId == itemId &&
                    item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p))
                    return Math.Max(1, p);
            }
        }
        return 0;
    }

    /// <summary>Get the server-side sell price (what vendor pays). Usually half of buy price.</summary>
    internal static int GetServerSellPrice(Character vendor, Item item)
    {
        if (item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int p))
            return Math.Max(1, p / 2);
        return Math.Max(1, GetServerBuyPrice(vendor, item.BaseId) / 2);
    }

    /// <summary>Count gold in player's backpack recursively.</summary>
    public static long CountGold(Character ch)
    {
        if (World == null) return 0;
        var backpack = ch.Backpack;
        if (backpack == null) return 0;

        long total = 0;
        foreach (var item in EnumerateContainerContentsRecursive(backpack))
        {
            if (item.ItemType == Core.Enums.ItemType.Gold || item.BaseId == 0x0EED)
                total += item.Amount;
        }
        return total;
    }

    /// <summary>Remove gold from player's backpack.</summary>
    public static void RemoveGold(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;
        var backpack = ch.Backpack;
        if (backpack == null) return;

        int remaining = amount;
        foreach (var item in EnumerateContainerContentsRecursive(backpack).ToList())
        {
            if (remaining <= 0) break;
            if (item.ItemType != Core.Enums.ItemType.Gold && item.BaseId != 0x0EED)
                continue;

            if (item.Amount <= remaining)
            {
                remaining -= item.Amount;
                item.Delete();
            }
            else
            {
                item.Amount -= (ushort)remaining;
                remaining = 0;
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
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted)
                continue;

            yield return item;

            foreach (var child in EnumerateContainerContentsRecursive(item))
                yield return child;
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
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor) return;

        var backpack = vendor.Backpack;
        if (backpack == null) return;

        if (!vendor.TryGetTag("VENDORINV", out string? invDef) || string.IsNullOrEmpty(invDef))
            return;

        // Parse "itemId:amount,itemId:amount,..."
        var entries = invDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existing = new Dictionary<ushort, int>();

        // Count existing stock
        foreach (var item in World.GetContainerContents(backpack.Uid))
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
            backpack.AddItem(newItem);
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
        if (pack != null)
        {
            pack.AddItem(item);
            item.Position = new Point3D(0, 0, 0, owner.MapIndex);
            return;
        }

        world.PlaceItem(item, owner.Position);
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
        int maxWeight = (recipient.Str * 7 / 2) + 40 + recipient.ModMaxWeight;
        int current = recipient.GetTotalWeight();
        int incoming = 0;
        foreach (var item in sourceContainer.Contents)
            incoming += Math.Max(1, item.Weight) * Math.Max(1, (int)item.Amount);
        // Item.Weight is in tenths of a stone; capacity math (current/maxWeight)
        // is in whole stones, so normalize incoming to stones too. Without this
        // the incoming weight was counted 10x and rejected most real trades.
        incoming /= 10;

        if (current + incoming > maxWeight)
        {
            reason = "You cannot carry that much.";
            return false;
        }

        return true;
    }
}
