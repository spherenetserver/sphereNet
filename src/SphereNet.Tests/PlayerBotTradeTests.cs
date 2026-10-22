using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A bot trading with a player through the secure trade window: buying what
/// the player drops on it, selling what the player asks for, turning down what is
/// worth nothing, and never agreeing to an offer emptied after it said yes.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotTradeTests
{
    private const ushort Katana = 0x13FF;
    private const ushort Junk = 0x0F0F;

    private sealed record Rig(GameWorld World, TradeManager Trades, PlayerBotEngine Engine, Character Bot,
        Character Player, GameClient PlayerClient, long Now);

    private static GameWorld World()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Pack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    private static Item Add(GameWorld world, Item container, ushort id, ItemType type = ItemType.Normal, ushort amount = 1)
    {
        var item = world.CreateItem();
        item.BaseId = id;
        item.ItemType = type;
        item.Amount = amount;
        if (id == Katana) item.Name = "katana";
        Assert.True(container.TryAddItem(item));
        return item;
    }

    private static long Gold(Item container) =>
        container.Contents.Where(i => !i.IsDeleted && i.ItemType == ItemType.Gold).Sum(i => (long)i.Amount);

    private static GameClient Client(GameWorld world, Character ch, TradeManager trades)
    {
        var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
            new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        c.SetEngines(movement: new MovementEngine(world), tradeManager: trades);
        c.AttachServerDriven(ch);
        return c;
    }

    private static Rig Make(ushort botGold = 1000, Dictionary<string, string[]>? lines = null)
    {
        var world = World();
        var trades = new TradeManager();
        var bot = world.CreateCharacter();
        bot.IsPlayer = true;
        bot.Name = "Wanderer";
        bot.Str = 60; bot.Dex = 60; bot.Int = 40;
        bot.MaxStam = 60; bot.Stam = 60; bot.MaxHits = 60; bot.Hits = 60;
        bot.SetTag(PlayerBotEngine.MarkerTag, "1");
        bot.SetTag(PlayerBotEngine.HomeTag, "Town");
        world.PlaceCharacter(bot, new Point3D(100, 100, 0, 0));
        var botPack = Pack(world, bot);
        if (botGold > 0) Add(world, botPack, 0x0EED, ItemType.Gold, botGold);

        var nav = new BotNavGraph();
        nav.LoadWaypoints("""
            { "Waypoints": [
              { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
              { "Name": "B", "X": 110, "Y": 100, "Z": 0 }
            ] }
            """, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", lines ?? new Dictionary<string, string[]>()), NullLogger.Instance, seed: 7)
        {
            TargetCount = 1,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            Trades = trades,
            ValueOf = i => i.BaseId == Katana ? 100 : 0,
            Intents = BotIntents.Parse(["wtb: wtb", "selling: what+are+you+selling"]),
        };
        engine.CreateClient = ch => Client(world, ch, trades);
        engine.AdoptSaved();

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        player.Str = 60; player.Dex = 60; player.MaxStam = 60; player.Stam = 60;
        player.MaxHits = 60; player.Hits = 60;
        world.PlaceCharacter(player, new Point3D(102, 100, 0, 0));
        world.AddOnlinePlayer(player);
        Pack(world, player);
        long now = Run(engine, 1_000_000, 20);
        return new Rig(world, trades, engine, bot, player, Client(world, player, trades), now);
    }

    private static long Run(PlayerBotEngine engine, long now, int ticks, Func<bool>? done = null)
    {
        var po = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        for (int i = 0; i < ticks && (done == null || !done()); i++)
        {
            engine.RunSearches(po);
            engine.Tick(now);
            now += 100;
        }
        return now;
    }

    private static string Trace(PlayerBotEngine engine, Character bot) =>
        $"faults {engine.WindowActFaults} next {engine.MindOf(bot)?.NextActMs} isbot {bot.IsPlayerBot} mode {engine.MindOf(bot)?.Mode}" +
        string.Join(Environment.NewLine, engine.TraceOf(bot.Uid.Value.ToString()));

    private static bool BotAccepted(SecureTrade t, Character bot) =>
        t.Initiator == bot ? t.InitiatorAccepted : t.PartnerAccepted;

    /// <summary>The player lifts gold out of their pack into their side of the window.</summary>
    private static void PayIn(Rig r, SecureTrade trade, ushort amount)
    {
        var pile = r.Player.Backpack!.Contents.First(i => i.ItemType == ItemType.Gold);
        r.PlayerClient.HandleItemPickup(pile.Uid.Value, amount);
        r.PlayerClient.HandleItemDrop(pile.Uid.Value, 30, 30, 0, trade.GetOwnContainer(r.Player).Uid.Value);
    }

    [Fact]
    public void ABotBuysWhatAPlayerDropsOnItAtItsPrice()
    {
        var r = Make();
        var katana = Add(r.World, r.Player.Backpack!, Katana);
        r.PlayerClient.HandleItemPickup(katana.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(katana.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot);
        Assert.NotNull(trade);

        long now = Run(r.Engine, r.Now, 100, () => BotAccepted(trade!, r.Bot));
        Assert.True(BotAccepted(trade!, r.Bot), Trace(r.Engine, r.Bot));
        Assert.Equal(60, Gold(trade!.GetOwnContainer(r.Bot)));

        r.PlayerClient.HandleSecureTrade(2, trade.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(trade.IsCompleted);
        Assert.Equal(60, Gold(r.Player.Backpack!));
        Assert.Equal(940, Gold(r.Bot.Backpack!));
        Assert.Equal(r.Bot.Backpack!.Uid, katana.ContainedIn);
        Run(r.Engine, now, 10);
        Assert.Equal(1, r.Engine.WindowTrades);
    }

    [Fact]
    public void ABotTurnsDownWhatIsWorthNothing()
    {
        var r = Make();
        var junk = Add(r.World, r.Player.Backpack!, Junk);
        r.PlayerClient.HandleItemPickup(junk.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(junk.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot)!;
        Run(r.Engine, r.Now, 60);
        Assert.False(BotAccepted(trade, r.Bot));
        Assert.Equal(0, Gold(trade.GetOwnContainer(r.Bot)));
    }

    [Fact]
    public void ABotBringsWhatAPlayerWantsAndSellsItForItsPrice()
    {
        var r = Make();
        var katana = Add(r.World, r.Bot.Backpack!, Katana);
        Add(r.World, r.Player.Backpack!, 0x0EED, ItemType.Gold, 500);

        r.Engine.OnHear(r.Player, "wanderer wtb katana");
        long now = Run(r.Engine, r.Now, 100, () => r.Trades.FindTradeFor(r.Bot) != null);
        var trade = r.Trades.FindTradeFor(r.Bot);
        Assert.True(trade != null, Trace(r.Engine, r.Bot));
        Assert.Equal(trade!.GetOwnContainer(r.Bot).Uid, katana.ContainedIn);

        // Short of the price: no.
        PayIn(r, trade, 100);
        now = Run(r.Engine, now, 40);
        Assert.False(BotAccepted(trade, r.Bot));

        PayIn(r, trade, 30);
        now = Run(r.Engine, now, 60, () => BotAccepted(trade, r.Bot));
        Assert.True(BotAccepted(trade, r.Bot), Trace(r.Engine, r.Bot));
        r.PlayerClient.HandleSecureTrade(2, trade.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(trade.IsCompleted);
        Assert.Equal(r.Player.Backpack!.Uid, katana.ContainedIn);
        Assert.Equal(1130, Gold(r.Bot.Backpack!));
        Assert.Equal(370, Gold(r.Player.Backpack!));
    }

    [Fact]
    public void GoldTakenBackOutOfTheWindowWithdrawsTheBotsYes()
    {
        var r = Make();
        Add(r.World, r.Bot.Backpack!, Katana);
        Add(r.World, r.Player.Backpack!, 0x0EED, ItemType.Gold, 500);
        r.Engine.OnHear(r.Player, "wanderer wtb katana");
        long now = Run(r.Engine, r.Now, 100, () => r.Trades.FindTradeFor(r.Bot) != null);
        var trade = r.Trades.FindTradeFor(r.Bot)!;
        PayIn(r, trade, 130);
        Run(r.Engine, now, 60, () => BotAccepted(trade, r.Bot));
        Assert.True(BotAccepted(trade, r.Bot));

        // The player lifts most of it back out, then says yes.
        var paid = trade.GetOwnContainer(r.Player).Contents.First(i => i.ItemType == ItemType.Gold);
        r.PlayerClient.HandleItemPickup(paid.Uid.Value, 100);
        Assert.False(BotAccepted(trade, r.Bot));
        r.PlayerClient.HandleItemDrop(paid.Uid.Value, 40, 40, 0, r.Player.Backpack!.Uid.Value);
        r.PlayerClient.HandleSecureTrade(2, trade.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.False(trade.IsCompleted);
    }

    [Fact]
    public void ABotNamesWhatItHasForSale()
    {
        var r = Make(lines: new Dictionary<string, string[]> { ["trade_stock"] = ["got {items}"] });
        Add(r.World, r.Bot.Backpack!, Katana);
        r.Engine.OnHear(r.Player, "wanderer what are you selling?");
        var mind = r.Engine.MindOf(r.Bot)!;
        Run(r.Engine, r.Now, 60, () => mind.LastLine != null);
        Assert.True(mind.LastLine?.StartsWith("got ") == true && mind.LastLine.EndsWith(" 130gp"), mind.LastLine ?? Trace(r.Engine, r.Bot));
    }
}
