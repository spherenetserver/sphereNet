using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Network.Packets.Incoming;

namespace SphereNet.Game.PlayerBots;

/// <summary>Something a bot runs short of: the item type, how few is too few, how
/// many it buys, and the kinds of place that sell it (shopping.txt). A potion need
/// names the spell in the bottle (<c>Potion:GreaterHeal</c>).</summary>
public sealed record BotNeed(string Name, ItemType Type, int Low, int Buy, string[] Places,
    SpellType Spell = SpellType.None);

/// <summary>
/// Supplies. Bandages, reagents, arrows and bolts are used up; a bot that never got
/// more would end up a fighter who cannot heal and a mage who cannot cast. A bot
/// takes what its kills drop - gold and supplies off a monster's body, as anyone
/// may - and, short of something, walks to a vendor that sells it and buys it with
/// its own gold through its client (the vendor's buy list, then the buy). No gold
/// is made out of nothing.
/// </summary>
public sealed partial class PlayerBotEngine
{
    private readonly List<BotNeed> _needs = [];

    /// <summary>Read <c>shopping.txt</c>: <c>name itemtype low buy place...</c>
    /// (item type by its ItemType name: Bandage, Reagent, WeaponArrow, WeaponBolt).</summary>
    public void LoadShopping(string dataDir)
    {
        string path = Path.Combine(dataDir, "shopping.txt");
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5) continue;
            // "Potion:GreaterHeal": the item type, then the spell in the bottle.
            int colon = p[1].IndexOf(':');
            var spell = SpellType.None;
            if (colon > 0 && !Enum.TryParse(p[1][(colon + 1)..], true, out spell)) continue;
            if (!Enum.TryParse(colon > 0 ? p[1][..colon] : p[1], true, out ItemType type) ||
                !int.TryParse(p[2], out int low) || !int.TryParse(p[3], out int buy))
                continue;
            AddNeed(new BotNeed(p[0], type, low, buy, p[4..], spell));
        }
    }

    public void AddNeed(BotNeed need) => _needs.Add(need);

    /// <summary>Shops found with nobody selling a need (its vendors' lists do not hold
    /// it, or they are sold out), until when they are not walked to for it.</summary>
    private readonly Dictionary<(string Place, string Need), long> _soldOut = [];
    private const long SoldOutMs = 1_800_000;

    private bool SoldOut(BotDestination d, BotNeed need, long now)
    {
        if (!_soldOut.TryGetValue((d.Name, need.Name), out long until)) return false;
        if (now < until) return true;
        _soldOut.Remove((d.Name, need.Name));
        return false;
    }

    // ------------------------------------------------------------------
    //  Taking what a kill dropped
    // ------------------------------------------------------------------

    private static bool IsSupply(Item item) =>
        item.ItemType is ItemType.Gold or ItemType.Reagent or ItemType.Bandage or ItemType.Potion or
            ItemType.WeaponArrow or ItemType.WeaponBolt;

    private static bool IsWeaponType(ItemType t) =>
        t is ItemType.WeaponSword or ItemType.WeaponFence or ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or ItemType.WeaponAxe or ItemType.WeaponBow or
            ItemType.WeaponXBow;

    /// <summary>After a won fight: go over to the body. A monster's is anybody's to
    /// take from; a player's only a player killer goes through.</summary>
    private bool StartLoot(BotMind mind, Character foe, long now)
    {
        bool rogue = IsPk(mind);
        if (mind.Client == null || (foe.IsPlayer && !rogue)) return false;
        mind.LootVictim = foe.Uid;
        mind.LootFrom = foe.Position;
        mind.LootUntilMs = now + 45_000;
        mind.LootCorpse = null;
        mind.LootOpened = false;
        mind.LootAll = foe.IsPlayer;
        mind.LootTaken = 0;
        mind.LootSkipped.Clear();
        mind.Mode = BotMode.Loot;
        mind.NextActMs = now + _rng.Next(600, 1500);
        return true;
    }

    private Item? FindBody(BotMind mind)
    {
        foreach (var item in _world.GetItemsInRange(mind.LootFrom, 3))
            if (!item.IsDeleted && item.ItemType == ItemType.Corpse && item.IsOnGround &&
                item.TryGetTag("OWNER_UID", out string? owner) && ScriptNumber.TryParseUInt(owner, out uint uid) &&
                uid == mind.LootVictim.Value)
                return item;
        return null;
    }

    private void ActLoot(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        var corpse = mind.LootCorpse ??= FindBody(mind);
        if (client == null || now > mind.LootUntilMs || corpse == null || corpse.IsDeleted)
        {
            EndLoot(mind, now);
            return;
        }
        int dist = ch.Position.GetDistanceTo(corpse.Position);
        if (dist > 1)
        {
            bool walked = StepToward(mind, corpse.Position, run: false, searchWithin: 20);
            if (mind.NoGainMs("loot", corpse.Position, dist, now) > 15_000)
            {
                WindowGiveUps++;
                mind.Note(now, $"cannot get to the body at {corpse.Position}");
                EndLoot(mind, now);
                return;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return;
        }
        if (!mind.LootOpened)
        {
            client.HandleDoubleClick(corpse.Uid.Value);
            mind.LootOpened = true;
            mind.NextActMs = now + _rng.Next(500, 1000);
            return;
        }
        Item? next = null;
        foreach (var item in corpse.Contents)
        {
            if (item.IsDeleted || mind.LootSkipped.Contains(item.Uid.Value)) continue;
            // Gold and supplies, a victim's weapons, and - from a monster too - what
            // is worth selling on.
            if (IsSupply(item) || (mind.LootAll && IsWeaponType(item.ItemType)) ||
                (Trades != null && IsStock(item) && ValueOf(item) >= LootWorth))
            {
                next = item;
                break;
            }
        }
        if (next == null || mind.LootTaken >= 10 || ch.Backpack is not { } pack)
        {
            EndLoot(mind, now);
            return;
        }
        client.HandleItemPickup(next.Uid.Value, next.Amount);
        client.HandleItemDrop(next.Uid.Value, (short)_rng.Next(44, 140), (short)_rng.Next(65, 140), 0, pack.Uid.Value);
        if (next.ContainedIn == corpse.Uid)
        {
            client.ReleaseDraggedItem(toGround: false);
            mind.LootSkipped.Add(next.Uid.Value);
        }
        else
            mind.LootTaken++;
        mind.NextActMs = now + _rng.Next(400, 900);
    }

    private void EndLoot(BotMind mind, long now)
    {
        if (mind.LootTaken > 0 && mind.LootAll && _rng.Next(100) < 60)
            Speak(mind, "pk_loot", fallback: null);
        mind.LootCorpse = null;
        mind.LootVictim = Serial.Invalid;
        mind.Mode = mind.ResumeMode is BotMode.Fight or BotMode.Flee or BotMode.Loot ? BotMode.Linger : mind.ResumeMode;
        mind.ResetLeg(now);
        mind.NextActMs = now + _rng.Next(800, 2000);
    }

    // ------------------------------------------------------------------
    //  Buying
    // ------------------------------------------------------------------

    private int CountInPack(Character ch, ItemType type)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        int n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == type) n += Math.Max(1, (int)item.Amount);
        return n;
    }

    /// <summary>What this bot needs most: of the things it is short of (judged by what
    /// it fights or works with), the one that matters most to it
    /// (<see cref="NeedSeverity"/>, then how short it is; shopping.txt order breaks a
    /// tie). With <paramref name="buyable"/> (the planner's question), a need no shop on
    /// its road sells - none of that kind within <see cref="NeedShopRange"/>, every one
    /// found out of it, or (a blank rune) none near - is no need: it does not stand in
    /// the way of the next one, nor of a hunt, nor call for a new plan at every stay.</summary>
    internal BotNeed? FirstNeed(BotMind mind, bool buyable = false)
    {
        BotNeed? best = null;
        double bestWeight = 0;
        long now = NowMs();
        // Below ground, or off the roads, no shop is in reach either way: what it is
        // short of stays a need (a dungeon run ends early for it).
        var pos = mind.Char.Position;
        bool shopsKnown = buyable && !IsUnderground(pos) && Nav.Waypoints.Count > 0 && Nav.FindNearest(pos, 64) >= 0;
        foreach (var need in _needs)
        {
            if (!Uses(mind, need)) continue;
            int have = NeedHave(mind, need);
            if (have >= need.Low) continue;
            double weight = NeedSeverity(mind, need) * (0.5 + 0.5 * (1 - Math.Clamp(have / (double)Math.Max(1, need.Low), 0, 1)));
            if (weight <= bestWeight) continue;
            if (shopsKnown && FindShop(mind, need, now, note: false) == null) continue;
            best = need;
            bestWeight = weight;
        }
        return best;
    }

    /// <summary>Whether the bot has a use for a need at all.</summary>
    private bool Uses(BotMind mind, BotNeed need)
    {
        var ch = mind.Char;
        return need.Type switch
        {
            ItemType.Bandage => ch.GetSkill(SkillType.Healing) >= 300,
            ItemType.Reagent => mind.IsCaster,
            // The bow it carries: in hand, or put away for a blade for want of arrows.
            ItemType.WeaponArrow => CarriesBow(ch, ItemType.WeaponBow),
            ItemType.WeaponBolt => CarriesBow(ch, ItemType.WeaponXBow),
            // Potions (heal, cure, refresh) for one who fights without spells.
            ItemType.Potion => mind.IsFighter && !mind.IsCaster && need.Spell != SpellType.None,
            // Blank runes for a caster who keeps runes and has room for more.
            ItemType.Rune => KeepsRunes(mind) && !IsRogueBot(ch) && UsableRuneCount(ch) < RuneCap,
            // Something to eat: a bot that went to eat and found its pack bare.
            ItemType.Food => mind.Manners.WantsFood,
            // The tools and materials of its trade (the Work part).
            _ => WorkNeeds(mind, need.Type),
        };
    }

    /// <summary>How many of a need it carries: runes it can mark, potions of the
    /// spell, a tool its trade can really use (see <see cref="UsableToolCount"/>), else
    /// pieces of the type in the pack and the hands.</summary>
    internal int NeedHave(BotMind mind, BotNeed need)
    {
        var ch = mind.Char;
        if (need.Type == ItemType.Rune) return MarkableRuneCount(ch);
        if (need.Spell != SpellType.None) return CountPotions(ch, need.Spell);
        int tools = UsableToolCount(ch, need.Type);
        return tools >= 0 ? tools : CountInPack(ch, need.Type) + CountInHand(ch, need.Type);
    }

    /// <summary>A shop for a need further than this (straight tiles) is not walked to
    /// for it: the need is dropped rather than a day spent crossing the land.</summary>
    public int NeedShopRange { get; set; } = 1200;

    /// <summary>How much a missing thing matters to this bot, 0..1 (before how short of
    /// it it is): what a mage cannot cast without, a healer's bandages when it has no
    /// other way to mend, an archer's arrows weigh most; a potion it has another answer
    /// for (bandages and the skill, a spell) weighs little, a refresh potion less still;
    /// a trade's tool much, its material and food less, a blank rune least. The figures
    /// are <c>need</c> lines of plans.txt: the weight, and the weight when the bot has
    /// another answer.</summary>
    internal double NeedSeverity(BotMind mind, BotNeed need)
    {
        var ch = mind.Char;
        var (alone, covered) = Plans.Need(need.Name, need.Type);
        bool other = need.Type switch
        {
            // Another way to mend: a heal spell it can cast, or a heal potion.
            ItemType.Bandage => CanCastSelfHeal(mind) || CountPotions(ch, SpellType.GreaterHeal) > 0,
            // The bow is not its main weapon: it fights with a blade as well.
            ItemType.WeaponArrow or ItemType.WeaponBolt => !mind.IsArcher,
            ItemType.Potion => need.Spell switch
            {
                SpellType.Heal or SpellType.GreaterHeal =>
                    (ch.GetSkill(SkillType.Healing) >= 300 && CountInPack(ch, ItemType.Bandage) >= 5) || CanCastSelfHeal(mind),
                // A bandage cures with Healing and Anatomy 60.0 (the Healing skill's own rule).
                SpellType.Cure =>
                    (ch.GetSkill(SkillType.Healing) >= 600 && ch.GetSkill(SkillType.Anatomy) >= 600 &&
                     CountInPack(ch, ItemType.Bandage) >= 5) || (mind.IsCaster && KnowsSpell(mind, SpellType.Cure)),
                _ => true,
            },
            _ => false,
        };
        double severity = other ? covered : alone;
        // Nothing to pay with, pack or bank: it has to earn the gold first, and the
        // need does not hold up what earns it.
        if (VendorEngine.CountGold(ch) + (ch.GetEquippedItem(Layer.BankBox) is { } box ? ResourceMatch.Count(box, ResourceMatch.Gold) : 0) <
            Math.Max(1, Plans.Price(need.Name)))
            severity *= 0.5;
        return severity;
    }

    /// <summary>A heal spell in its book with the magery for it (what it may cast, not
    /// what its mana pays for this moment).</summary>
    private static bool CanCastSelfHeal(BotMind mind) =>
        mind.IsCaster && (KnowsSpell(mind, SpellType.Heal) || KnowsSpell(mind, SpellType.GreaterHeal));

    /// <summary>Arrived at a shop with a need: find the vendor that sells it.</summary>
    internal bool StartShopping(BotMind mind, long now)
    {
        var need = mind.ShoppingFor;
        if (need == null) return false;
        foreach (var c in _world.GetCharsInRange(mind.Char.Position, 10))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || !VendorEngine.IsVendorLike(c)) continue;
            if (StockOf(mind, c, need) == null) continue;
            mind.ShopVendor = c;
            mind.ShopUntilMs = now + 60_000;
            mind.ShopListOpen = false;
            mind.Mode = BotMode.Shop;
            mind.NextActMs = now + _rng.Next(800, 2000);
            return true;
        }
        mind.Note(now, $"nobody at {mind.At?.Name ?? "this place"} sells {need.Name}");
        // Learned for every bot: that shop is not tried for it again for a while.
        if (mind.At != null) _soldOut[(mind.At.Name, need.Name)] = now + SoldOutMs;
        mind.ShoppingFor = null;
        return false;
    }

    /// <summary>The vendor's piece that answers the need: of the type (the spell in a
    /// potion), in stock, and - for a trade's tool - one the trade can be plied with
    /// (<see cref="ToolStockFits"/>: the pack's i_shovel is a pick by type, but no hand
    /// holds it).</summary>
    private Item? StockOf(BotMind mind, Character vendor, BotNeed need)
    {
        var box = vendor.GetEquippedItem(Layer.VendorStock);
        if (box == null) return null;
        foreach (var item in box.Contents)
            if (!item.IsDeleted && AnswersNeed(need, item) && item.Amount > 0 && ToolStockFits(mind.Char, need, item))
                return item;
        return null;
    }

    private void ActShop(BotMind mind, long now)
    {
        var ch = mind.Char;
        var vendor = mind.ShopVendor;
        var need = mind.ShoppingFor;
        var client = mind.Client;
        if (vendor == null || need == null || client == null || vendor.IsDeleted || vendor.IsDead || now > mind.ShopUntilMs)
        {
            EndShopping(mind, now);
            return;
        }
        if (ch.Position.GetDistanceTo(vendor.Position) > 2)
        {
            bool walked = StepToward(mind, vendor.Position, run: false, searchWithin: 20);
            if (mind.NoGainMs("shop", vendor.Position, ch.Position.GetDistanceTo(vendor.Position), now) > 20_000)
            {
                // Behind a counter it cannot come round: no purchase this time, and the
                // shop is not walked to for this again for a while (by any bot), as a
                // shop nobody sells it at.
                WindowGiveUps++;
                mind.Note(now, $"cannot get to {vendor.GetName()} at {vendor.Position}");
                if (mind.At != null) _soldOut[(mind.At.Name, need.Name)] = now + SoldOutMs;
                EndShopping(mind, now);
                return;
            }
            mind.NextActMs = now + StepDelay(mind, walked, false, now);
            return;
        }
        if (!mind.ShopListOpen)
        {
            // As a player does: the buy list first, the purchase a moment later.
            client.OpenVendorBuy(vendor);
            mind.ShopListOpen = true;
            mind.NextActMs = now + _rng.Next(1500, 3500);
            return;
        }
        var stock = StockOf(mind, vendor, need);
        if (stock != null)
        {
            int price = Math.Max(1, GameClient.GetVendorItemPrice(vendor, stock));
            long gold = VendorEngine.CountGold(ch);
            int amount = (int)Math.Min(Math.Min(need.Buy, stock.Amount), gold / price);
            if (amount > 0)
            {
                int before = NeedHave(mind, need);
                client.HandleVendorBuy(vendor.Uid.Value, 2,
                    [new VendorBuyEntry { Layer = (byte)Layer.VendorStock, ItemSerial = stock.Uid.Value, Amount = (ushort)amount }]);
                // Counted when it went through: what the client was told otherwise
                // ("You are buying too fast", no gold) is in the trace.
                if (NeedHave(mind, need) > before)
                {
                    WindowPurchases++;
                    mind.Note(now, $"bought {need.Name} of {vendor.GetName()}");
                }
                else
                    mind.Note(now, $"{vendor.GetName()} would not sell it {need.Name}");
            }
            else
                mind.Note(now, $"cannot pay for {need.Name} at {vendor.GetName()}");
        }
        else if (mind.At != null)
        {
            // Sold out since the bot looked: not walked to for it again for a while.
            mind.Note(now, $"{vendor.GetName()} has no {need.Name} left");
            _soldOut[(mind.At.Name, need.Name)] = now + SoldOutMs;
        }
        EndShopping(mind, now);
    }

    private void EndShopping(BotMind mind, long now)
    {
        mind.ShopVendor = null;
        mind.ShoppingFor = null;
        mind.ShopListOpen = false;
        if (mind.Mode == BotMode.Shop)
            mind.Mode = BotMode.Linger;
        mind.LingerUntilMs = Math.Max(mind.LingerUntilMs, now + _rng.Next(10_000, 30_000));
        mind.NextActMs = now + _rng.Next(1000, 3000);
    }

    public long WindowPurchases { get; private set; }
}
