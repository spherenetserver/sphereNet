using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Tests;

/// <summary>
/// Gold in the bank is gold the character has.
///
/// Upstream's PAYFROMPACKONLY is false by default (CServerConfig.cpp:237), and the count
/// it then uses is ContentCount over the CHARACTER (send.cpp:243) - the bank box is worn,
/// so banked gold is both counted and spendable. The counting here was pack-only
/// unconditionally: the stricter setting applied without anyone choosing it, so a player
/// who banked their gold was treated as having none. Reported from a shard exactly that
/// way.
///
/// The backpack still comes first, so a purchase spends loose coin before reaching into
/// the bank.
/// </summary>
public sealed class BankedGoldIsSpendableTests : IDisposable
{
    private readonly bool _packOnlyBefore = VendorEngine.PayFromPackOnly;

    public void Dispose() => VendorEngine.PayFromPackOnly = _packOnlyBefore;

    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Objects.Characters.Character Me,
                                Item Pack, Item Bank);

    private static Bench Build()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        VendorEngine.PayFromPackOnly = false;

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);

        var bank = world.CreateItem();
        bank.BaseId = 0x09AB; bank.ItemType = ItemType.EqBankBox;
        me.Equip(bank, Layer.BankBox);

        return new Bench(world, me, pack, bank);
    }

    private static Item Gold(Bench b, Item into, ushort amount)
    {
        var g = b.World.CreateItem();
        g.BaseId = 0x0EED; g.ItemType = ItemType.Gold; g.Amount = amount;
        Assert.True(into.TryAddItem(g));
        return g;
    }

    /// <summary>The reported case.</summary>
    [Fact]
    public void BankedGoldIsCounted()
    {
        var b = Build();
        Gold(b, b.Pack, 100);
        Gold(b, b.Bank, 5000);

        Assert.Equal(5100, VendorEngine.CountGold(b.Me));
    }

    /// <summary>And spendable - with the pack spent first, so loose coin goes before
    /// the bank is touched.</summary>
    [Fact]
    public void ThePackIsSpentBeforeTheBank()
    {
        var b = Build();
        var loose = Gold(b, b.Pack, 100);
        var banked = Gold(b, b.Bank, 5000);

        VendorEngine.RemoveGold(b.Me, 300);

        Assert.True(loose.IsDeleted);          // the 100 went first
        Assert.Equal(4800, banked.Amount);     // then 200 from the bank
        Assert.Equal(4800, VendorEngine.CountGold(b.Me));
    }

    /// <summary>A purchase the pack alone covers never reaches the bank.</summary>
    [Fact]
    public void ThePurchaseThePackCoversLeavesTheBankAlone()
    {
        var b = Build();
        var loose = Gold(b, b.Pack, 500);
        var banked = Gold(b, b.Bank, 5000);

        VendorEngine.RemoveGold(b.Me, 200);

        Assert.Equal(300, loose.Amount);
        Assert.Equal(5000, banked.Amount);
    }

    /// <summary>With PAYFROMPACKONLY the bank is out of reach again, which is what the
    /// setting is for.</summary>
    [Fact]
    public void PackOnlyKeepsTheBankOutOfIt()
    {
        var b = Build();
        VendorEngine.PayFromPackOnly = true;
        Gold(b, b.Pack, 100);
        var banked = Gold(b, b.Bank, 5000);

        Assert.Equal(100, VendorEngine.CountGold(b.Me));
        VendorEngine.RemoveGold(b.Me, 100);
        Assert.Equal(5000, banked.Amount);
    }

    /// <summary>Gold in a pouch inside the bank counts too - the walk is recursive on
    /// both sides.</summary>
    [Fact]
    public void GoldInsideAPouchInTheBankCounts()
    {
        var b = Build();
        var pouch = b.World.CreateItem();
        pouch.BaseId = 0x0E76; pouch.ItemType = ItemType.Container;
        Assert.True(b.Bank.TryAddItem(pouch));
        Gold(b, pouch, 750);

        Assert.Equal(750, VendorEngine.CountGold(b.Me));
    }
}
