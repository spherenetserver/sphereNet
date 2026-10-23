using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Game.PlayerBots;

/// <summary>What a trade window is for: an ordinary deal, or a favour's business -
/// things held handed back, a rune or the reagent money for a spell, things to hold,
/// a present, things to price.</summary>
public enum BotTradeMode { Deal, Return, Rune, Pay, Carry, Gift, Appraise }

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
            // What it holds for a player is not its to sell.
            if (IsStock(item) && HeldFor(ch, item) == 0) list.Add(item);
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

        // What their window holds, for when the trade completes.
        mind.TradeTheirs = theirs.Contents.Where(i => !i.IsDeleted).ToList();
        mind.TradeTheirGold = trade.GetGoldOffer(partner);
        // Not a deal: a favour's rune or money, things held or handed back, a present,
        // a price asked.
        mind.TradeMode = TradeModeOf(mind, partner, mine);
        if (mind.TradeMode != BotTradeMode.Deal)
            return ActFavorTrade(mind, trade, partner, mine, theirs, now);

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
        var mode = mind.TradeMode;
        var theirs = mind.TradeTheirs ?? [];
        long theirGold = mind.TradeTheirGold;
        mind.TradeMode = BotTradeMode.Deal;
        mind.TradeTheirs = null;
        mind.TradeTheirGold = 0;
        mind.AppraiseCloseMs = 0;
        var partner = last.GetPartner(mind.Char);
        if (!(last.IsCompleted && last.InitiatorAccepted && last.PartnerAccepted))
        {
            // Turned down: offered again later, not pressed.
            if (mode == BotTradeMode.Return)
            {
                mind.NextGiveBackMs = now + 120_000;
                mind.GiveBackTo = null;
            }
            return;
        }
        WindowTrades++;
        mind.Note(now, $"traded with {partner.GetName()} ({mode})");
        if (mode != BotTradeMode.Deal)
        {
            FavorTradeDone(mind, partner, mode, theirs, theirGold, now);
            return;
        }
        AddBond(mind, partner, Rule("trade"), now, "a fair trade");
        if (_rng.Next(100) < 70) Speak(mind, "trade_done", fallback: null, to: partner);
    }

    // ------------------------------------------------------------------
    //  Trades that are no deal
    // ------------------------------------------------------------------

    /// <summary>What a trade window with this player is for: a favour's rune or money,
    /// a present, things to hold or to price, things held being handed back; else an
    /// ordinary deal.</summary>
    private BotTradeMode TradeModeOf(BotMind mind, Character partner, Item mine)
    {
        var ch = mind.Char;
        bool anyMine = false, allHeld = true;
        foreach (var item in mine.Contents)
        {
            if (item.IsDeleted || item.ItemType == ItemType.Gold) continue;
            anyMine = true;
            if (HeldFor(ch, item) != partner.Uid.Value) allHeld = false;
        }
        if (anyMine && allHeld) return BotTradeMode.Return;
        if (mind.Favor is not { } f || f.Player != partner) return BotTradeMode.Deal;
        return f.Stage switch
        {
            BotFavorStage.AwaitRune => BotTradeMode.Rune,
            BotFavorStage.AwaitPay => BotTradeMode.Pay,
            BotFavorStage.AwaitGoods => f.Kind switch
            {
                BotFavorKind.Carry => BotTradeMode.Carry,
                BotFavorKind.Gift => BotTradeMode.Gift,
                BotFavorKind.Appraise => BotTradeMode.Appraise,
                _ => BotTradeMode.Deal,
            },
            _ => BotTradeMode.Deal,
        };
    }

    private bool ActFavorTrade(BotMind mind, SecureTrade trade, Character partner, Item mine, Item theirs, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        var mode = mind.TradeMode;
        var f = mind.Favor;
        // None of its own gold goes in.
        if (mine.Contents.FirstOrDefault(i => !i.IsDeleted && i.ItemType == ItemType.Gold) is { } own && ch.Backpack is { } pack)
        {
            MoveGold(client, own, own.Amount, pack, mine);
            mind.NextActMs = now + _rng.Next(400, 800);
            return true;
        }
        long theirGold = trade.GetGoldOffer(partner);
        var goods = new List<Item>();
        foreach (var item in theirs.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == ItemType.Gold) theirGold += item.Amount;
            else goods.Add(item);
        }
        string key = TradeKey(theirs, mine, trade.GetGoldOffer(partner));
        bool changed = key != mind.TradeKey;
        if (changed)
        {
            mind.TradeKey = key;
            mind.TradeChangedMs = now;
            mind.TradeAcceptAtMs = now + _rng.Next(1500, 3000);
        }
        bool ok = false;
        string? say = null;
        var tokens = new Dictionary<string, string>();
        switch (mode)
        {
            case BotTradeMode.Return:
                ok = true;
                break;
            case BotTradeMode.Rune:
            {
                var rune = goods.Count == 1 && goods[0].ItemType == ItemType.Rune ? goods[0] : null;
                // A gate needs a marked rune that is not worn out; a mark wants one to mark.
                ok = rune != null && theirGold == 0 &&
                     (f?.Kind != BotFavorKind.Gate || IsUsableRune(rune));
                if (!ok && changed && goods.Count > 0)
                    say = rune != null && f?.Kind == BotFavorKind.Gate ? "favor_rune_blank" : "favor_rune_wrong";
                break;
            }
            case BotTradeMode.Pay:
                ok = goods.Count == 0 && f != null && theirGold >= f.Fee;
                if (!ok && changed && f != null)
                {
                    say = "favor_fee";
                    tokens["price"] = f.Fee.ToString();
                }
                break;
            case BotTradeMode.Carry:
            {
                long tenths = goods.Sum(i => (long)i.TotalWeightTenths);
                bool fits = (long)ch.GetTotalWeightTenths() + tenths <= (long)Math.Max(0, ch.MaxWeight) * Item.WeightUnits;
                ok = goods.Count > 0 && theirGold == 0 && fits;
                if (!fits && changed) say = "carry_heavy";
                break;
            }
            case BotTradeMode.Gift:
                ok = goods.Count > 0 || theirGold > 0;
                break;
            case BotTradeMode.Appraise:
                if (changed && goods.Count > 0)
                {
                    say = "appraise_value";
                    tokens["items"] = string.Join(", ", goods.Take(3).Select(i => $"{i.GetDisplayName()} {Worth(i)}gp"));
                    mind.AppraiseCloseMs = now + 6000;
                }
                if (mind.AppraiseCloseMs != 0 && now >= mind.AppraiseCloseMs)
                {
                    // Looked at, priced: the window is closed and the things go back.
                    client.HandleSecureTrade(1, mine.Uid.Value, 0);
                    if (f != null) FavorDone(mind, f, now, null);
                    return false;
                }
                break;
        }
        if (say != null)
        {
            tokens["target"] = f?.PlaceWord ?? "";
            Speak(mind, say, fallback: null, to: partner, extra: tokens, force: true);
        }
        bool accepted = trade.Initiator == ch ? trade.InitiatorAccepted : trade.PartnerAccepted;
        if (ok && !accepted && now >= mind.TradeAcceptAtMs)
            client.HandleSecureTrade(2, mine.Uid.Value, 1);
        else if (!ok && accepted)
            client.HandleSecureTrade(2, mine.Uid.Value, 0);
        mind.NextActMs = now + 400;
        return true;
    }

    /// <summary>A trade for a favour went through: what came across is a rune to use,
    /// the money paid, things held, a present; things handed back are no longer held.</summary>
    private void FavorTradeDone(BotMind mind, Character partner, BotTradeMode mode, List<Item> theirs, long theirGold,
        long now)
    {
        var ch = mind.Char;
        var f = mind.Favor is { } fv && fv.Player == partner ? fv : null;
        var came = theirs.Where(i => !i.IsDeleted && i.GetTopLevelObj() == ch && i.ItemType != ItemType.Gold).ToList();
        switch (mode)
        {
            case BotTradeMode.Return:
            {
                // What went across is held no more (the entries of things it no longer has drop).
                HeldItems(ch, partner.Uid.Value);
                mind.GiveBackTo = null;
                Speak(mind, "giveback_done", fallback: null, to: partner);
                break;
            }
            case BotTradeMode.Rune when f != null:
            {
                var rune = came.FirstOrDefault(i => i.ItemType == ItemType.Rune);
                if (rune == null) break;
                Hold(ch, rune, partner);
                f.Rune = rune;
                f.RuneIsPlayers = true;
                if (f.Kind == BotFavorKind.Gate)
                {
                    if (string.IsNullOrEmpty(f.PlaceWord)) f.PlaceWord = RuneWord(rune);
                    var (category, tokens) = Proceed(mind, f, now);
                    Speak(mind, category, fallback: null, to: partner, lang: f.Language, extra: tokens, force: true);
                }
                else
                {
                    f.Stage = BotFavorStage.Working;
                    f.UntilMs = now + FavorWorkMs;
                    if (!f.Paid && FeeFor(mind, partner, SpellType.Mark) is > 0 and var fee)
                    {
                        f.Fee = fee;
                        f.Stage = BotFavorStage.AwaitPay;
                        f.UntilMs = now + FavorWaitMs;
                        Speak(mind, "favor_fee", fallback: null, to: partner, lang: f.Language,
                            extra: new Dictionary<string, string> { ["price"] = fee.ToString() }, force: true);
                    }
                    else
                        Speak(mind, "mark_ok", fallback: null, to: partner, lang: f.Language, force: true);
                }
                break;
            }
            case BotTradeMode.Pay when f != null:
                f.Paid = true;
                f.Stage = BotFavorStage.Working;
                f.UntilMs = now + FavorWorkMs;
                Speak(mind, f.Kind == BotFavorKind.Gate ? "gate_ok" : "mark_ok", fallback: null, to: partner,
                    lang: f.Language, extra: new Dictionary<string, string> { ["target"] = f.PlaceWord ?? "" }, force: true);
                break;
            case BotTradeMode.Carry when f != null:
                foreach (var item in came) Hold(ch, item, partner);
                Speak(mind, "carry_ok", fallback: null, to: partner, lang: f.Language, force: true);
                FavorDone(mind, f, now, null);
                break;
            case BotTradeMode.Gift when f != null:
            {
                long worth = came.Sum(Worth) + theirGold;
                int per = Math.Max(1, Rule("gift_per"));
                int points = (int)Math.Clamp(worth / per, 1, Math.Max(1, Rule("gift_max")));
                WindowGifts++;
                Memorize(mind, partner, "gift", came.FirstOrDefault()?.GetDisplayName() ?? (theirGold + "gp"));
                Speak(mind, "gift_thanks", fallback: null, to: partner, lang: f.Language, force: true);
                EndFavor(f, now, done: true);
                WindowFavors++;
                AddBond(mind, partner, points, now, $"a present worth {worth}");
                break;
            }
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
