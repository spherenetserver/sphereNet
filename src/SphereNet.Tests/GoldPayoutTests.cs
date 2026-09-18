using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Tests;

/// <summary>
/// Gold is handed over in stacks, and one call will only hand over so much.
///
/// A coin stack holds at most ITEMSMAXAMOUNT, so a payout is a number of items, not a
/// number: sixty-five thousand is two stacks and sixty-five million would be more than
/// a thousand. Upstream stops at twenty-five million and logs when it has to
/// (AddGoldToPack, CCharAct.cpp:222) for exactly that reason - the amount is an item
/// count in disguise, and a script asking for a billion would make thirty-five thousand
/// items in one call.
///
/// The splitting was already right here; the ceiling was not, so the cost of a line
/// like that was unbounded.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class GoldPayoutTests
{
    private static SphereNet.Game.Objects.Characters.Character Payee()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;          // never bounced for weight
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    /// <summary>Gold the payee ended up with, pack and ground alike: a pack holds a
    /// fixed number of slots, so a large payout fills it and the rest is put at their
    /// feet rather than dropped on the floor of the engine.</summary>
    private static int GoldIn(SphereNet.Game.Objects.Characters.Character ch)
    {
        int inPack = ch.Backpack!.Contents.Where(i => i.ItemType == ItemType.Gold)
                                          .Sum(i => (int)i.Amount);
        int atFeet = VendorEngine.World!.GetItemsInRange(ch.Position, 2)
                                        .Where(i => i.ItemType == ItemType.Gold)
                                        .Sum(i => (int)i.Amount);
        return inPack + atFeet;
    }

    /// <summary>An amount past one stack arrives as several, adding up to what was
    /// asked for.</summary>
    [Fact]
    public void AnAmountPastOneStackArrivesAsSeveral()
    {
        var ch = Payee();

        VendorEngine.GiveGoldToPack(ch, 65_000);

        Assert.Equal(65_000, GoldIn(ch));
        Assert.True(ch.Backpack!.Contents.Count(i => i.ItemType == ItemType.Gold) >= 2);
    }

    /// <summary>The ceiling holds, and says so rather than quietly paying less.</summary>
    [Fact]
    public void APayoutPastTheCeilingIsCappedAndReported()
    {
        var ch = Payee();
        string? reported = null;
        VendorEngine.OnGoldCapped = m => reported = m;
        try
        {
            VendorEngine.GiveGoldToPack(ch, 65_000_000);

            Assert.Equal(VendorEngine.MaxGoldPerGift, GoldIn(ch));
            Assert.NotNull(reported);
            Assert.Contains("65000000", reported);
        }
        finally { VendorEngine.OnGoldCapped = null; }
    }

    /// <summary>An amount inside the ceiling is paid in full and reports nothing.</summary>
    [Fact]
    public void AnAmountInsideTheCeilingIsPaidInFull()
    {
        var ch = Payee();
        string? reported = null;
        VendorEngine.OnGoldCapped = m => reported = m;
        try
        {
            VendorEngine.GiveGoldToPack(ch, 1_000_000);

            Assert.Equal(1_000_000, GoldIn(ch));
            Assert.Null(reported);
        }
        finally { VendorEngine.OnGoldCapped = null; }
    }
}
