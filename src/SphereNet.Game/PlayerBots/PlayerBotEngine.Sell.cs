using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Network.Packets.Incoming;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Selling to vendors. What a bot made, gathered or took off a monster and does not
/// live by is goods: it carries them to a shop of the kind that buys such things
/// (<c>work.txt</c> <c>sell</c> lines), finds the vendor that offers the most for
/// them, opens that vendor's sell list and sells through its client - the vendor's
/// own buy list, prices and purse decide what changes hands. The gold lands in its
/// pack and goes the way all its gold goes: to the bank when there is much of it.
/// A shop where nobody bought the thing is not walked to for it again for half an
/// hour (by any bot).
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Goods are worth a trip from this much make value on...</summary>
    public int SaleWorthMin { get; set; } = 100;
    /// <summary>...or from this many kinds of them, or when they weigh the bot down.</summary>
    public int SaleCountMin { get; set; } = 5;
    /// <summary>Lines sold at once, as a hand fills in a sell list.</summary>
    private const int SaleLines = 12;

    private readonly List<BotSaleRule> _saleRules = [];

    public void AddSaleRule(BotSaleRule rule) => _saleRules.Add(rule);

    /// <summary>The kinds of place goods of a type are sold at, or null.</summary>
    private string[]? SalePlaces(ItemType type)
    {
        string[]? any = null;
        foreach (var rule in _saleRules)
        {
            if (rule.Type == type) return rule.Places;
            if (rule.Type == null) any ??= rule.Places;
        }
        return any;
    }

    /// <summary>Whether a piece of the bot's stock is something it keeps. Everybody
    /// keeps gear that came off it, scrolls, the tools of its trades and the
    /// materials of a craft it can ply. A fighter sells the rest (its spoils), but not
    /// its only weapon. Anyone else sells only what it made itself and the raw stuff
    /// it gathered: whatever else its pack holds is its kit, not merchandise.</summary>
    private bool KeepsForItself(BotMind mind, Item item)
    {
        var ch = mind.Char;
        var armorLayer = ArmorLayer(mind, item);
        if (armorLayer != Layer.None && ch.GetEquippedItem(armorLayer) is { } worn)
            return Skills.Information.InfoSkillExtensions.GetArmorDefense(item) >
                   Skills.Information.InfoSkillExtensions.GetArmorDefense(worn);
        if (item.TryGetTag("EQUIPLAYER", out _) || item.ItemType == ItemType.Scroll) return true;
        bool ownMake = item.Crafter == ch.Uid;
        foreach (var g in _gatherTrades)
            if (g.Tool == item.ItemType && !ownMake && HasSkillFor(ch, g.Skill)) return true;
        foreach (var c in _craftTrades)
        {
            if (!HasSkillFor(ch, c.Skill)) continue;
            if (c.Tool == item.ItemType && !ownMake) return true;
            if (Array.IndexOf(c.Materials, item.ItemType) >= 0 && CraftTool(ch, c) != null) return true;
        }
        if (mind.IsFighter)
            return IsWeaponType(item.ItemType) && !ownMake && EquippedWeapon(ch) == null;
        return !ownMake && Array.IndexOf(MaterialTypes, item.ItemType) < 0;
    }

    /// <summary>What the bot would sell to a vendor: its stock (loose in its pack,
    /// worth something, no supplies, nothing it holds for a player) less what it keeps
    /// for itself - ore too, while a forge it can smelt it at is within reach.</summary>
    internal List<Item> GoodsToSell(BotMind mind)
    {
        var ch = mind.Char;
        var goods = new List<Item>();
        if (ch.Backpack is not { } pack) return goods;
        bool miner = HasSkillFor(ch, SkillType.Mining);
        bool? forgeNear = null;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            // A box of its own make is merchandise too, while it is empty.
            bool stock = IsStock(item) ||
                         (!item.IsDeleted && item.ItemType == ItemType.Container && item.Crafter == ch.Uid &&
                          item.ContentCount == 0 && ValueOf(item) > 0);
            if (!stock || HeldFor(ch, item) != 0 || KeepsForItself(mind, item)) continue;
            if (item.ItemType == ItemType.Ore && miner && CanSmeltOre(ch, item))
            {
                forgeNear ??= WorkPlace(mind, _smeltPlaces, MemoKey(BotJob.Smelt, SkillType.Mining), NowMs(), WorkTripRange) != null;
                if (forgeNear.Value) continue;
            }
            goods.Add(item);
        }
        return goods;
    }

    /// <summary>Whether the bot carries goods worth a trip to a vendor: by their make
    /// value, by how many kinds, or because they weigh it down.</summary>
    public bool HasGoodsToSell(BotMind mind)
    {
        if (mind.Client == null || mind.Char.IsDead || _saleRules.Count == 0) return false;
        var goods = GoodsToSell(mind);
        if (goods.Count == 0) return false;
        if (goods.Count >= SaleCountMin) return true;
        long worth = 0;
        foreach (var item in goods) worth += Worth(item);
        return worth >= SaleWorthMin || PackLoadPercent(mind.Char) >= 60;
    }

    /// <summary>Send the bot to sell: to the nearest shop on its road of a kind that
    /// buys its most valuable kind of goods (the next kind when that has no shop
    /// within reach). False when there is nothing to sell or nowhere to sell it; then
    /// nothing has changed.</summary>
    public bool StartSelling(BotMind mind, long now)
    {
        if (!MayWork(mind)) return false;
        var goods = GoodsToSell(mind);
        if (goods.Count == 0) return false;
        var kinds = new Dictionary<ItemType, long>();
        foreach (var item in goods)
            kinds[item.ItemType] = kinds.GetValueOrDefault(item.ItemType) + Worth(item);
        foreach (var (type, _) in kinds.OrderByDescending(k => k.Value))
        {
            if (SalePlaces(type) is not { } places) continue;
            // Not to a shop that is known to buy none of these pieces.
            var pieces = new List<int>();
            foreach (var item in goods)
                if (item.ItemType == type && !pieces.Contains(ResourceMatch.ItemDefIndexOf(item)))
                    pieces.Add(ResourceMatch.ItemDefIndexOf(item));
            if (WorkPlace(mind, places, MemoKey(BotJob.Sell, SkillType.None, type), now, WorkErrandRange,
                    except: d => pieces.TrueForAll(def => IsUnsoldAt(d, def, now))) is not { } shop)
                continue;
            return BeginJob(mind, BotJob.Sell, SkillType.None, shop, now, type);
        }
        return false;
    }

    /// <summary>Pieces (by definition) a shop was found to buy none of, and until when
    /// that is taken as known; and pieces of a bot's own make that found no buyer
    /// somewhere, which craftsmen then make only when they can make nothing else.</summary>
    private readonly Dictionary<(string Place, int Def), long> _unsold = [];
    private readonly Dictionary<int, long> _poorSellers = [];

    private void RememberUnsold(BotDestination place, Item item, bool ownMake, long now)
    {
        int def = ResourceMatch.ItemDefIndexOf(item);
        if (_unsold.Count > 1024)
            foreach (var old in _unsold.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                _unsold.Remove(old);
        _unsold[(place.Name, def)] = now + WorkMemoMs;
        if (ownMake) _poorSellers[def] = now + 4 * WorkMemoMs;
    }

    private bool IsUnsoldAt(BotDestination place, int def, long now) =>
        _unsold.TryGetValue((place.Name, def), out long until) && now < until;

    /// <summary>A piece that lately found no buyer: better something else is made.</summary>
    private bool IsPoorSeller(int def, long now) =>
        _poorSellers.Count > 0 && _poorSellers.TryGetValue(def, out long until) && now < until;

    /// <summary>What a vendor offers for the bot's goods (its sell list, less what the
    /// bot keeps).</summary>
    private List<VendorEngine.VendorSellOffer> SaleOffers(Character ch, Character vendor, List<Item> goods)
    {
        var offers = VendorEngine.GetSellOffers(ch, vendor);
        offers.RemoveAll(o => o.Amount <= 0 || o.UnitPrice <= 0 || !goods.Contains(o.Item));
        return offers;
    }

    /// <summary>The vendor within twelve tiles that offers the most for the goods.</summary>
    private Character? FindBuyer(Character ch, List<Item> goods)
    {
        Character? best = null;
        long bestTotal = 0;
        foreach (var c in _world.GetCharsInRange(ch.Position, 12))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || c.MapIndex != ch.MapIndex || !VendorEngine.IsVendorLike(c)) continue;
            long total = 0;
            foreach (var offer in SaleOffers(ch, c, goods))
                total += offer.UnitPrice * offer.Amount;
            if (total > bestTotal)
            {
                bestTotal = total;
                best = c;
            }
        }
        return best;
    }

    private void ActSell(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        var w = mind.Work;
        if (w == null || client == null || w.Job != BotJob.Sell || w.Stage != BotWorkStage.Site)
        {
            EndJob(mind, now, "no sale in hand");
            return;
        }
        if (!FreeForWork(mind) || now >= w.UntilMs)
        {
            EndJob(mind, now, "called away");
            return;
        }
        var goods = GoodsToSell(mind);
        if (w.Vendor == null)
        {
            // Who buys it here: the one that offers the most, as a player asks about.
            w.Vendor = goods.Count > 0 ? FindBuyer(ch, goods) : null;
            w.ListOpen = false;
            if (w.Vendor == null)
            {
                // What is left of the kind it came to sell, nobody here buys: learned
                // piece by piece (the shop may well buy the next thing of the kind).
                foreach (var item in goods)
                    if (item.ItemType == w.SaleType)
                        RememberUnsold(w.Place, item, item.Crafter == ch.Uid, now);
                EndJob(mind, now, goods.Count == 0 ? "nothing left to sell" : "nobody here buys the rest");
                return;
            }
        }
        var vendor = w.Vendor;
        if (vendor.IsDeleted || vendor.IsDead || vendor.MapIndex != ch.MapIndex)
        {
            w.Vendor = null;
            mind.NextActMs = now + 500;
            return;
        }
        int dist = ch.Position.GetDistanceTo(vendor.Position);
        if (dist > 2)
        {
            bool walked = StepToward(mind, vendor.Position, run: false, searchWithin: 20);
            if (mind.NoGainMs("buyer", vendor.Position, dist, now) > 20_000)
            {
                // Behind a counter it cannot come round: no sale here.
                WindowGiveUps++;
                RememberUseless(w.Place, MemoKey(w), now);
                EndJob(mind, now, $"cannot get to {vendor.GetName()}");
                return;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return;
        }
        var offers = SaleOffers(ch, vendor, goods);
        if (offers.Count == 0)
        {
            w.Vendor = null;
            mind.NextActMs = now + 500;
            return;
        }
        if (offers.Count > SaleLines) offers.RemoveRange(SaleLines, offers.Count - SaleLines);
        if (!w.ListOpen)
        {
            // As a player does: the sell list first, the sale once it is filled in.
            client.OpenVendorSell(vendor);
            w.ListOpen = true;
            if (w.Rounds == 0 && IsTradesman(mind) && _rng.Next(100) < 30) Speak(mind, "gather_deliver", fallback: null);
            mind.NextActMs = now + Math.Max(1500, offers.Count * 400) + _rng.Next(0, 2000);
            return;
        }
        long before = VendorEngine.CountGold(ch);
        var lines = new List<VendorSellEntry>(offers.Count);
        foreach (var offer in offers)
            lines.Add(new VendorSellEntry { ItemSerial = offer.Item.Uid.Value, Amount = (ushort)Math.Min(offer.Amount, ushort.MaxValue) });
        client.HandleVendorSell(vendor.Uid.Value, lines);
        long gained = VendorEngine.CountGold(ch) - before;
        w.Rounds++;
        w.ListOpen = false;
        if (gained > 0)
        {
            w.Done++;
            WindowSales++;
            WindowSaleGold += gained;
            mind.Note(now, $"sold {lines.Count} kinds of goods to {vendor.GetName()} for {gained} gold");
            if (w.Rounds < 4)
            {
                // What is left goes on the next list (to this vendor or the one beside it).
                w.Vendor = null;
                mind.NextActMs = now + _rng.Next(1500, 3500);
                return;
            }
            EndJob(mind, now, "sold");
            return;
        }
        // Its purse is empty, or the sale was refused: not here, for a while.
        RememberUseless(w.Place, MemoKey(w), now);
        EndJob(mind, now, $"{vendor.GetName()} did not pay");
    }

    /// <summary>What is gathered, or made of it, to be worked or sold on.</summary>
    private static readonly ItemType[] MaterialTypes =
    [
        ItemType.Ingot, ItemType.Ore, ItemType.Log, ItemType.Board, ItemType.Kindling, ItemType.Cloth,
        ItemType.ClothBolt, ItemType.Hide, ItemType.Leather, ItemType.Fur, ItemType.Wool, ItemType.Feather,
        ItemType.Fish, ItemType.MeatRaw, ItemType.Gem,
    ];

    /// <summary>Talk about wanting a thing: there a bot's own goods are not what it names.</summary>
    private static bool IsBuyingTalk(string category) =>
        category.StartsWith("wtb", StringComparison.OrdinalIgnoreCase) ||
        category.StartsWith("buy_", StringComparison.OrdinalIgnoreCase) ||
        category.EndsWith("_buy", StringComparison.OrdinalIgnoreCase) ||
        category.EndsWith("_need", StringComparison.OrdinalIgnoreCase);

    /// <summary>Selling talk names what the bot really carries: <c>{item}</c> is a piece
    /// of its goods (of its stock, when it has nothing it would take to a vendor),
    /// <c>{mat}</c> a material it carries with the amount in <c>{count}</c>. A bot with
    /// nothing to sell, and any line about buying, keeps the words of the token lists.</summary>
    private void NameCarriedGoods(BotMind mind, string category, Dictionary<string, string> tokens)
    {
        if (mind.Char.Backpack == null || IsBuyingTalk(category)) return;
        var stock = GoodsToSell(mind);
        if (stock.Count == 0) stock = Stock(mind.Char);
        stock.RemoveAll(i => GoodsWord(i).Length == 0);
        if (stock.Count == 0) return;
        tokens["item"] = GoodsWord(stock[_rng.Next(stock.Count)]);
        foreach (var item in stock)
        {
            if (Array.IndexOf(MaterialTypes, item.ItemType) < 0) continue;
            tokens["mat"] = GoodsWord(item);
            tokens["count"] = Math.Max(1, (int)item.Amount).ToString();
            break;
        }
    }

    /// <summary>A piece of goods as it is named in talk: its name, no amount.</summary>
    private static string GoodsWord(Item item) => (item.GetName() ?? "").Trim().ToLowerInvariant();
}
