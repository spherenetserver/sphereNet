using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Trading with players through the secure trade window, as a client trades: what
/// goes in or out of the bot's side is picked up and dropped through its client, and
/// it agrees with the window's own accept. A bot prices goods by their make value
/// (the VALUE of their definition at their quality): it buys at
/// <see cref="TradeBuyPercent"/> of that, sells at <see cref="TradeSellPercent"/>,
/// and spends at most half the gold it carries. It agrees only while what it gets
/// is worth what it gives, and looks again at every change - the window itself
/// clears both check marks whenever either side changes.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>The trade engine, to find a bot's open window.</summary>
    public TradeManager? Trades { get; set; }
    /// <summary>What one of an item is worth.</summary>
    public Func<Item, int> ValueOf { get; set; } = VendorEngine.GetMakeValue;
    public int TradeBuyPercent { get; set; } = 60;
    public int TradeSellPercent { get; set; } = 130;
    /// <summary>A trade nobody changes for this long is closed.</summary>
    public long TradeIdleMs { get; set; } = 90_000;
    /// <summary>Picked up off a monster's body for its worth (at least this much).</summary>
    public int LootWorth { get; set; } = 20;

    public long WindowTrades { get; private set; }

    private long Worth(Item item) => (long)Math.Max(0, ValueOf(item)) * Math.Max(1, (int)item.Amount);
    private long BuyPrice(Item item) => Worth(item) * TradeBuyPercent / 100;
    private long SellPrice(Item item) => Math.Max(1, Worth(item) * TradeSellPercent / 100);

    /// <summary>Something it would sell: in its pack, worth something, and nothing it
    /// lives by - gold, supplies, its spellbook, bags, runes, keys, blessed things.</summary>
    private bool IsStock(Item item) =>
        !item.IsDeleted && !IsSupply(item) && !item.IsAttr(ObjAttributes.Newbie) &&
        item.ItemType is not (ItemType.Spellbook or ItemType.Container or ItemType.ContainerLocked or ItemType.Rune or
            ItemType.Key) &&
        ValueOf(item) > 0;

    private List<Item> Stock(Character ch)
    {
        var list = new List<Item>();
        if (ch.Backpack is not { } pack) return list;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (IsStock(item)) list.Add(item);
        return list;
    }

    /// <summary>A piece of its stock a player's words name ("wtb katana").</summary>
    private Item? StockNamed(Character ch, string words)
    {
        var wanted = BotIntents.Words(words).Where(w => w.Length >= 3).ToList();
        if (wanted.Count == 0) return null;
        return Stock(ch).FirstOrDefault(i =>
        {
            string name = i.GetDisplayName().ToLowerInvariant();
            return wanted.Any(name.Contains);
        });
    }

    private long PackGold(Character ch)
    {
        if (ch.Backpack is not { } pack) return 0;
        long n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == ItemType.Gold) n += item.Amount;
        return n;
    }

    /// <summary>One act of trading. False when the bot has no trade (and is taking no
    /// piece to a buyer), so its own plans go on.</summary>
    private bool ActTrade(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        if (Trades == null || client == null) return false;
        var trade = Trades.FindTradeFor(ch);
        if (trade == null)
        {
            if (mind.Trade is { } last) TradeEnded(mind, last, now);
            return ActSellTo(mind, now);
        }
        var partner = trade.GetPartner(ch);
        var mine = trade.GetOwnContainer(ch);
        var theirs = trade.GetPartnerContainer(ch);
        if (mind.Trade != trade)
        {
            mind.Trade = trade;
            mind.TradeKey = null;
            mind.TradeChangedMs = now;
            mind.Note(now, $"trading with {partner.GetName()}");
        }
        if (mind.Mode is BotMode.Fight or BotMode.Flee || partner.IsDead || partner.IsDeleted ||
            partner.MapIndex != ch.MapIndex || partner.Position.GetDistanceTo(ch.Position) > 8 ||
            now - mind.TradeChangedMs > TradeIdleMs)
        {
            mind.Note(now, "closes the trade");
            client.HandleSecureTrade(1, mine.Uid.Value, 0);
            return false;
        }

        long theirGold = trade.GetGoldOffer(partner), theirValue = 0, myGold = 0, myValue = 0;
        Item? worthless = null;
        bool theirGoods = false, myGoods = false;
        foreach (var item in theirs.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == ItemType.Gold) { theirGold += item.Amount; continue; }
            theirGoods = true;
            long v = BuyPrice(item);
            if (v <= 0) worthless ??= item;
            theirValue += v;
        }
        foreach (var item in mine.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == ItemType.Gold) { myGold += item.Amount; continue; }
            myGoods = true;
            myValue += SellPrice(item);
        }

        // The gold it puts in: what their goods are worth to it, or - when it is the
        // one selling - the change from what they put in.
        long budget = PackGold(ch) / 2 + myGold;
        long target = worthless != null ? 0
            : myGoods ? Math.Max(0, theirGold + theirValue - myValue)
            : theirGoods ? theirValue : 0;
        bool tooDear = target > budget;
        if (tooDear) target = 0;

        // Its own gold going in or out is no new offer to answer.
        string key = TradeKey(theirs, myGoods ? mine : null, trade.GetGoldOffer(partner));
        if (key != mind.TradeKey)
        {
            mind.TradeKey = key;
            mind.TradeChangedMs = now;
            mind.TradeAcceptAtMs = now + _rng.Next(1500, 3000);
            QuoteTrade(mind, partner, worthless, tooDear, myGoods, theirGoods, theirGold, theirValue, myValue, target);
        }

        if (myGold != target && ch.Backpack is { } pack)
        {
            if (myGold > target && mine.Contents.FirstOrDefault(i => !i.IsDeleted && i.ItemType == ItemType.Gold) is { } back)
                MoveGold(client, back, (ushort)Math.Min(back.Amount, myGold - target), pack, mine);
            else if (myGold < target && FindInPack(ch, i => i.ItemType == ItemType.Gold && i.Amount > 0) is { } gold)
                MoveGold(client, gold, (ushort)Math.Min(gold.Amount, target - myGold), mine, pack);
            mind.NextActMs = now + _rng.Next(400, 800);
            return true;
        }

        bool accepted = trade.Initiator == ch ? trade.InitiatorAccepted : trade.PartnerAccepted;
        bool fair = (theirGoods || myGoods || theirGold > 0) && worthless == null && !tooDear &&
                    theirGold + theirValue >= myGold + myValue;
        if (fair && !accepted && now >= mind.TradeAcceptAtMs)
            client.HandleSecureTrade(2, mine.Uid.Value, 1);
        else if (!fair && accepted)
            client.HandleSecureTrade(2, mine.Uid.Value, 0);
        mind.NextActMs = now + 400;
        return true;
    }

    private static string TradeKey(Item theirs, Item? mine, long goldOffer)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in theirs.Contents)
            if (!item.IsDeleted) sb.Append(item.Uid.Value).Append(':').Append(item.Amount).Append(',');
        sb.Append('|');
        if (mine != null)
            foreach (var item in mine.Contents)
                if (!item.IsDeleted && item.ItemType != ItemType.Gold) sb.Append(item.Uid.Value).Append(',');
        return sb.Append('|').Append(goldOffer).ToString();
    }

    /// <summary>Lift <paramref name="amount"/> of a gold pile and drop it in
    /// <paramref name="into"/>; a drop that does not land goes back where it was.</summary>
    private static void MoveGold(Clients.GameClient client, Item pile, ushort amount, Item into, Item from)
    {
        if (amount == 0) return;
        client.HandleItemPickup(pile.Uid.Value, amount);
        client.HandleItemDrop(pile.Uid.Value, 30, 30, 0, into.Uid.Value);
        if (!pile.IsDeleted && pile.ContainedIn != into.Uid && pile.ContainedIn != from.Uid)
            client.ReleaseDraggedItem(toGround: false);
    }

    /// <summary>What it says to an offer: the price it gives for their goods, the
    /// price of its own, or why it will not deal.</summary>
    private void QuoteTrade(BotMind mind, Character partner, Item? worthless, bool tooDear, bool myGoods,
        bool theirGoods, long theirGold, long theirValue, long myValue, long target)
    {
        string? category = null;
        var tokens = new Dictionary<string, string>();
        if (worthless != null)
        {
            category = "trade_worthless";
            tokens["item"] = worthless.GetDisplayName();
        }
        else if (myGoods && theirGold + theirValue < myValue)
        {
            category = "trade_price";
            tokens["price"] = (myValue - theirValue).ToString();
        }
        else if (!myGoods && theirGoods)
        {
            category = tooDear ? "trade_too_dear" : "trade_offer";
            tokens["price"] = (tooDear ? theirValue : target).ToString();
        }
        if (category != null)
            Speak(mind, category, fallback: null, to: partner, extra: tokens, force: true);
    }

    private void TradeEnded(BotMind mind, SecureTrade last, long now)
    {
        mind.Trade = null;
        mind.TradeKey = null;
        if (last.IsCompleted && last.InitiatorAccepted && last.PartnerAccepted)
        {
            WindowTrades++;
            mind.Note(now, $"traded with {last.GetPartner(mind.Char).GetName()}");
            if (_rng.Next(100) < 70) Speak(mind, "trade_done", fallback: null, to: last.GetPartner(mind.Char));
        }
    }

    /// <summary>Take a piece a player asked for over to them and open a trade with it
    /// in the window. False when there is no such errand.</summary>
    private bool ActSellTo(BotMind mind, long now)
    {
        var ch = mind.Char;
        var buyer = mind.SellTo;
        if (buyer == null) return false;
        var item = mind.SellItem;
        int dist = buyer.MapIndex == ch.MapIndex ? buyer.Position.GetDistanceTo(ch.Position) : int.MaxValue;
        if (now > mind.SellUntilMs || item == null || item.IsDeleted || ch.Backpack == null ||
            item.ContainedIn != ch.Backpack.Uid || buyer.IsDeleted || buyer.IsDead || !buyer.IsOnline || dist > 20 ||
            mind.Mode is BotMode.Fight or BotMode.Flee || Trades!.FindTradeFor(buyer) != null)
        {
            mind.SellTo = null;
            mind.SellItem = null;
            return false;
        }
        if (dist > 2)
        {
            bool stepped = StepToward(mind, buyer.Position, run: false, searchWithin: 20);
            mind.NextActMs = now + StepDelay(mind, stepped, false, now);
            return true;
        }
        mind.SellTo = null;
        mind.SellItem = null;
        var client = mind.Client!;
        if (!client.InitiateTrade(buyer) || Trades.FindTradeFor(ch) is not { } trade) return false;
        var mine = trade.GetOwnContainer(ch);
        client.HandleItemPickup(item.Uid.Value, item.Amount);
        client.HandleItemDrop(item.Uid.Value, 30, 30, 0, mine.Uid.Value);
        if (item.ContainedIn != mine.Uid && item.ContainedIn != ch.Backpack.Uid)
            client.ReleaseDraggedItem(toGround: false);
        mind.Note(now, $"opens a trade with {buyer.GetName()} for {item.GetDisplayName()}");
        mind.NextActMs = now + _rng.Next(600, 1200);
        return true;
    }

    /// <summary>"What do you sell?": up to three pieces with their prices.</summary>
    private (string, Dictionary<string, string>?) AnswerStock(BotMind m)
    {
        if (Trades == null || m.Client == null) return ("trade_stock_none", null);
        var stock = Stock(m.Char);
        if (stock.Count == 0) return ("trade_stock_none", null);
        string list = string.Join(", ", stock.OrderByDescending(SellPrice).Take(3)
            .Select(i => $"{i.GetDisplayName()} {SellPrice(i)}gp"));
        return ("trade_stock", new Dictionary<string, string> { ["items"] = list });
    }

    /// <summary>"WTB x": a bot carrying one says its price and brings it over.</summary>
    private string? AnswerWtb(BotMind m, Character player, string? words, long now,
        ref Dictionary<string, string>? tokens)
    {
        if (words == null || Trades == null || m.Client == null || IsPk(m)) return null;
        if (StockNamed(m.Char, words) is not { } piece) return null;
        m.SellTo = player;
        m.SellItem = piece;
        m.SellUntilMs = now + 60_000;
        // Over to them once the answer is said.
        m.NextActMs = Math.Min(m.NextActMs, now + 3500);
        tokens = new Dictionary<string, string>
        {
            ["item"] = piece.GetDisplayName(),
            ["price"] = SellPrice(piece).ToString(),
        };
        return "trade_wtb_have";
    }
}
