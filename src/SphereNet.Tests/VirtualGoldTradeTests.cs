using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>FEATURE_TOL_VIRTUALGOLD (FeatureTOL 0x02): the trade window's gold field
/// moves virtual gold between the traders (Trade_UpdateGold / Trade_Status), capped
/// to what the offerer holds; GOLD reads the virtual purse. Off, the field does
/// nothing, as upstream's cap to an empty purse makes it.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VirtualGoldTradeTests
{
    private static (GameClient Client, Character Me, Character Partner, TradeManager Trades) Setup(int id)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var me = Player(world, 100);
        var partner = Player(world, 101);
        partner.IsOnline = true;
        TestHarness.AttachCharacter(client, me);
        var trades = new TradeManager();
        TestHarness.SetPrivateField(client, "_tradeManager", trades);
        return (client, me, partner, trades);
    }

    private static Character Player(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    [Fact]
    public void OfferedGoldChangesHandsWhenTheTradeCompletes()
    {
        VirtualGold.Enabled = true;
        var (client, me, partner, trades) = Setup(8301);
        VirtualGold.Set(me, 5000);
        VirtualGold.Set(partner, 100);
        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;
        uint mine = trade.GetOwnContainer(me).Uid.Value;

        client.HandleSecureTradeGold(mine, 3000, 0);
        trade.SetAccept(partner, true);
        client.HandleSecureTrade(2, mine, 1);

        Assert.Equal(2000, VirtualGold.Get(me));
        Assert.Equal(3100, VirtualGold.Get(partner));
    }

    [Fact]
    public void AnOfferIsCappedToThePurse()
    {
        VirtualGold.Enabled = true;
        var (client, me, partner, trades) = Setup(8302);
        VirtualGold.Set(me, 700);
        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;

        client.HandleSecureTradeGold(trade.GetOwnContainer(me).Uid.Value, 5, 1);

        Assert.Equal(700, trade.GetGoldOffer(me));
    }

    [Fact]
    public void ChangingTheOfferWithdrawsBothAcceptances()
    {
        VirtualGold.Enabled = true;
        var (client, me, partner, trades) = Setup(8303);
        VirtualGold.Set(me, 1000);
        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;
        uint mine = trade.GetOwnContainer(me).Uid.Value;
        client.HandleSecureTradeGold(mine, 1000, 0);
        trade.SetAccept(partner, true);

        client.HandleSecureTradeGold(mine, 0, 0);

        Assert.False(trade.PartnerAccepted);
    }

    [Fact]
    public void WithTheFeatureOffTheGoldFieldDoesNothing()
    {
        var (client, me, partner, trades) = Setup(8304);
        VirtualGold.Set(me, 1000);
        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;

        client.HandleSecureTradeGold(trade.GetOwnContainer(me).Uid.Value, 500, 0);

        Assert.Equal(0, trade.GetGoldOffer(me));
    }

    [Fact]
    public void GoldReadsTheVirtualPurse()
    {
        VirtualGold.Enabled = true;
        var (_, me, _, _) = Setup(8305);
        VirtualGold.Set(me, 1234);
        Assert.True(me.TryGetProperty("GOLD", out string gold));
        Assert.Equal("1234", gold);
    }
}
