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
/// many it buys, and the kinds of place that sell it (shopping.txt).</summary>
public sealed record BotNeed(string Name, ItemType Type, int Low, int Buy, string[] Places);

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
            if (p.Length < 5 || !Enum.TryParse(p[1], true, out ItemType type) ||
                !int.TryParse(p[2], out int low) || !int.TryParse(p[3], out int buy))
                continue;
            AddNeed(new BotNeed(p[0], type, low, buy, p[4..]));
        }
    }

    public void AddNeed(BotNeed need) => _needs.Add(need);

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
            if (IsSupply(item) || (mind.LootAll && IsWeaponType(item.ItemType)))
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

    /// <summary>What this bot needs most, judged by what it fights with.</summary>
    internal BotNeed? FirstNeed(BotMind mind)
    {
        var ch = mind.Char;
        foreach (var need in _needs)
        {
            bool uses = need.Type switch
            {
                ItemType.Bandage => ch.GetSkill(SkillType.Healing) >= 300,
                ItemType.Reagent => mind.IsCaster,
                ItemType.WeaponArrow => EquippedWeapon(ch)?.ItemType == ItemType.WeaponBow,
                ItemType.WeaponBolt => EquippedWeapon(ch)?.ItemType == ItemType.WeaponXBow,
                _ => false,
            };
            if (uses && CountInPack(ch, need.Type) < need.Low) return need;
        }
        return null;
    }

    /// <summary>When a stay ends: short of something and with the gold for it, the
    /// next trip is to a place that sells it. Null when nothing is needed.</summary>
    private BotDestination? PickShopping(BotMind mind, long now)
    {
        if (_needs.Count == 0 || now < mind.NextSupplyCheckMs || mind.Client == null) return null;
        mind.NextSupplyCheckMs = now + 120_000;
        var need = FirstNeed(mind);
        if (need == null || VendorEngine.CountGold(mind.Char) < 20) return null;
        int here = Nav.FindNearest(mind.Char.Position, 64);
        if (here < 0) return null;
        int component = Nav.Waypoints[here].Component;
        var shop = Nav.Destinations
            .Where(d => Nav.ComponentOf(d) == component && d.City.Length > 0 &&
                        need.Places.Any(t => t.Equals(d.Type, StringComparison.OrdinalIgnoreCase)) &&
                        (d.Arrivals.Count == 0 || d.BadArrivals.Count < d.Arrivals.Count))
            .OrderBy(d => d.Pos.GetDistanceTo(mind.Char.Position))
            .FirstOrDefault();
        if (shop == null) return null;
        mind.ShoppingFor = need;
        return shop;
    }

    /// <summary>Arrived at a shop with a need: find the vendor that sells it.</summary>
    private bool StartShopping(BotMind mind, long now)
    {
        var need = mind.ShoppingFor;
        if (need == null) return false;
        foreach (var c in _world.GetCharsInRange(mind.Char.Position, 10))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || !VendorEngine.IsVendorLike(c)) continue;
            if (StockOf(c, need.Type) == null) continue;
            mind.ShopVendor = c;
            mind.ShopUntilMs = now + 60_000;
            mind.ShopListOpen = false;
            mind.Mode = BotMode.Shop;
            mind.NextActMs = now + _rng.Next(800, 2000);
            return true;
        }
        mind.ShoppingFor = null;
        return false;
    }

    private static Item? StockOf(Character vendor, ItemType type)
    {
        var box = vendor.GetEquippedItem(Layer.VendorStock);
        if (box == null) return null;
        foreach (var item in box.Contents)
            if (!item.IsDeleted && item.ItemType == type && item.Amount > 0)
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
                // Behind a counter it cannot come round: no purchase this time.
                WindowGiveUps++;
                mind.Note(now, $"cannot get to {vendor.GetName()} at {vendor.Position}");
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
        var stock = StockOf(vendor, need.Type);
        if (stock != null)
        {
            int price = Math.Max(1, GameClient.GetVendorItemPrice(vendor, stock));
            long gold = VendorEngine.CountGold(ch);
            int amount = (int)Math.Min(Math.Min(need.Buy, stock.Amount), gold / price);
            if (amount > 0)
            {
                client.HandleVendorBuy(vendor.Uid.Value, 2,
                    [new VendorBuyEntry { Layer = (byte)Layer.VendorStock, ItemSerial = stock.Uid.Value, Amount = (ushort)amount }]);
                WindowPurchases++;
            }
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
