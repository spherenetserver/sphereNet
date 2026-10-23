using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Favours and friendship: a gate opened on the bot's own rune or on one the
/// player hands over (and handed back), the reagent money asked of a stranger, a way
/// shown, a heal offered unasked, a bond built by talk, a companion that follows and
/// waits, a bot raised by a player thanking them, and the shipped words for all of it.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotFavorTests
{
    // Road W (60,100) - A (100,100) - B (110,100) - C (120,100); an inn by W, a bank by C;
    // Farton's bank far off with no road to it (a rune's place).
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "W", "X": 60, "Y": 100, "Z": 0, "Connects": [ "A" ] },
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ ] },
          { "Name": "F", "X": 400, "Y": 400, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] },
          { "Name": "Town Inn", "X": 60, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "W", "Arrivals": [ { "X": 61, "Y": 101, "Z": 0 } ] },
          { "Name": "Farton Bank", "X": 400, "Y": 401, "Z": 0, "Type": "Bank", "City": "Farton",
            "NearestWaypoint": "F" }
        ] }
        """;

    private static readonly string[] IntentLines =
    [
        "greet: hi hello selam",
        "yes: yes evet",
        "no: no hayır",
        "not: don+t not",
        "cancel: nevermind boşver",
        "can: what+can+you+do ne+yapabilirsin",
        "gate: gate+to gate+pls open+a+gate gate+aç",
        "guide: take+me+to götür",
        "companion: travel+with+me benimle+gezer",
        "dismiss: you+can+go",
        "stay: wait+here bekle",
        "follow: follow+me you+too",
        "friend: be+my+friend",
        "remember: remember+me",
        "carry: hold+this",
        "giveback: give+back",
        "selling: what+are+you+selling",
        "where: where nerde",
        "place.Inn: inn han",
        "place.Bank: bank banka",
        "place.Moongate: moongate gate",
        "town.Town: town",
        "town.Farton: farton",
    ];

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["gate_where"] = ["where? {runes}"],
        ["gate_ok"] = ["opening {target}"],
        ["gate_open"] = ["open {target}"],
        ["gate_no_runes"] = ["no runes"],
        ["gate_no_rune"] = ["no rune for {target}"],
        ["favor_fee"] = ["{price}gp"],
        ["favor_cancel"] = ["ok nvm"],
        ["giveback_done"] = ["all back"],
        ["guide_ok"] = ["follow me"],
        ["guide_here"] = ["here, {target}"],
        ["respond_can"] = ["i can {abilities}"],
        ["token_ability"] = ["gate=gate", "heal=heal", "guide=guide"],
        ["companion_yes"] = ["lets go"],
        ["companion_not_yet"] = ["not yet"],
        ["companion_bye"] = ["bye then"],
        ["party_stay"] = ["holding"],
        ["bond_friend"] = ["friends now"],
        ["thanks_raised"] = ["thanks for the res"],
        ["offer_heal"] = ["want a heal?"],
        ["offer_heal_yes"] = ["hold still"],
        ["respond_chat"] = ["yeah"],
        ["respond_greet"] = ["hi"],
        ["ack_yes"] = ["ok"],
        ["carry_give"] = ["hand it over"],
        ["carry_ok"] = ["got it"],
        ["giveback_yes"] = ["here"],
        ["trade_stock"] = ["got {items}"],
        ["trade_stock_none"] = ["nothing"],
        ["companion_back"] = ["back again"],
        ["buff_ok"] = ["{spell} coming"],
        ["gift_ok"] = ["for me?"],
        ["gift_thanks"] = ["thanks!"],
        ["appraise_show"] = ["show me"],
        ["appraise_value"] = ["{items}"],
    };

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public required TradeManager Trades { get; init; }
        public required Character Bot { get; init; }
        public required Character Player { get; init; }
        public required GameClient PlayerClient { get; init; }
        public List<(string Category, string Line)> Said { get; } = [];
        public long Now { get; set; } = 1_000_000;

        /// <summary>Ticks; with <paramref name="realTime"/> a little real time passes per
        /// tick (spell timers run on the real clock).</summary>
        public void Run(int ticks, Func<bool>? done = null, bool realTime = false)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = 2 };
            for (int i = 0; i < ticks && (done == null || !done()); i++)
            {
                Engine.RunSearches(po);
                Engine.Tick(Now);
                Now += 100;
                if (realTime) Thread.Sleep(10);
            }
        }

        public bool SaidCategory(string category) => Said.Any(s => s.Category == category);

        public string Trace() =>
            string.Join(" | ", Said.Select(s => $"{s.Category}:{s.Line}")) + Environment.NewLine +
            string.Join(Environment.NewLine, Engine.TraceOf(Bot.Uid.Value.ToString()));
    }

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

    private static Item Rune(GameWorld world, Item container, Point3D? mark)
    {
        var rune = world.CreateItem();
        rune.BaseId = 0x1F14;
        rune.ItemType = ItemType.Rune;
        if (mark is { } at)
        {
            rune.SetRuneMark(at);
            rune.More1 = 10;
        }
        Assert.True(container.TryAddItem(rune));
        return rune;
    }

    private static SpellEngine Spells(GameWorld world)
    {
        Character.SpellbookRequiredEnabled = false;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.GateTravel, Name = "Gate Travel", Flags = SpellFlag.TargItem, ManaCost = 40, CastTimeBase = 1,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.Mark, Name = "Mark", Flags = SpellFlag.TargItem, ManaCost = 14, CastTimeBase = 1,
            EffectBase = 10, EffectScale = 10,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.Bless, Name = "Bless", Flags = SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless,
            ManaCost = 9, CastTimeBase = 1, EffectBase = 10, EffectScale = 10, DurationBase = 600, DurationScale = 600,
        });
        foreach (var (id, mana) in new[] { (SpellType.Heal, 4), (SpellType.GreaterHeal, 11) })
            registry.Register(new SpellDef
            {
                Id = id, Name = id.ToString(), Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
                ManaCost = (ushort)mana, CastTimeBase = 1, EffectBase = 10, EffectScale = 10,
            });
        return new SpellEngine(world, registry);
    }

    /// <summary>A mage bot at A, a player beside it; the bot carries a rune of Farton
    /// unless <paramref name="fartonRune"/> is false.</summary>
    private static Rig Make(bool fartonRune = true, bool friends = true, bool mage = true)
    {
        var world = World();
        var trades = new TradeManager();
        var spells = Spells(world);
        var bot = world.CreateCharacter();
        bot.IsPlayer = true;
        bot.Name = "Wanderer";
        bot.Str = 60; bot.Dex = 60; bot.Int = 100;
        bot.MaxStam = 60; bot.Stam = 60; bot.MaxHits = 60; bot.Hits = 60;
        bot.MaxMana = 100; bot.Mana = 100;
        if (mage) bot.SetSkill(SkillType.Magery, 1000);
        bot.SetTag(PlayerBotEngine.MarkerTag, "1");
        bot.SetTag(PlayerBotEngine.HomeTag, "Town");
        bot.SetTag(PlayerBotEngine.ArchetypeTag, mage ? "c_playerbot_mage_m" : "c_playerbot_townsfolk_m");
        bot.SetTag(PlayerBotEngine.TraitTag, "chatty");
        world.PlaceCharacter(bot, new Point3D(100, 100, 0, 0));
        var botPack = Pack(world, bot);
        if (fartonRune) Rune(world, botPack, new Point3D(400, 402, 0, 0));

        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", Lines), NullLogger.Instance, seed: 5)
        {
            TargetCount = 1,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            Trades = trades,
            Spells = spells,
            Gravity = 0,
            ValueOf = _ => 0,
            Intents = BotIntents.Parse(IntentLines),
        };
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), spellEngine: spells, tradeManager: trades);
            c.AttachServerDriven(ch);
            return c;
        };
        engine.AdoptSaved();
        var mind = engine.MindOf(bot)!;
        mind.LingerUntilMs = long.MaxValue;

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        player.Str = 60; player.Dex = 60; player.MaxStam = 60; player.Stam = 60;
        player.MaxHits = 60; player.Hits = 60;
        world.PlaceCharacter(player, new Point3D(102, 100, 0, 0));
        world.AddOnlinePlayer(player);
        Pack(world, player);
        if (friends) SetBond(bot, player, 30);

        var pc = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
            new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        pc.SetEngines(movement: new MovementEngine(world), tradeManager: trades);
        pc.AttachServerDriven(player);

        var rig = new Rig { World = world, Engine = engine, Trades = trades, Bot = bot, Player = player, PlayerClient = pc };
        engine.OnLine = (ch, category, line) => { if (ch == bot) rig.Said.Add((category, line)); };
        rig.Run(20);
        return rig;
    }

    private static void SetBond(Character bot, Character player, int score) =>
        bot.SetTag(PlayerBotEngine.BondsTag,
            $"{player.Uid.Value}:{score}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60}");

    private static Item? GateNear(GameWorld world, Point3D at)
    {
        foreach (var item in world.GetItemsInRange(at, 2))
            if (!item.IsDeleted && item.ItemType == ItemType.Telepad) return item;
        return null;
    }

    private static bool BotAccepted(SecureTrade t, Character bot) =>
        t.Initiator == bot ? t.InitiatorAccepted : t.PartnerAccepted;

    // ------------------------------------------------------------------
    //  Gate
    // ------------------------------------------------------------------

    [Fact]
    public void AFriendOpensAGateOnItsOwnRuneAndStepsOffIt()
    {
        var r = Make();
        r.Engine.OnHear(r.Player, "wanderer gate to farton");
        r.Run(400, () => r.Engine.WindowGates > 0, realTime: true);
        Assert.True(r.Engine.WindowGates == 1, r.Trace());
        var gate = GateNear(r.World, new Point3D(100, 100, 0, 0)) ?? GateNear(r.World, r.Bot.Position);
        Assert.NotNull(gate);
        Assert.True(gate!.MoreP.GetDistanceTo(new Point3D(400, 402, 0, 0)) <= 1);
        Assert.True(r.SaidCategory("gate_ok") && r.SaidCategory("gate_open"), r.Trace());
        // No reagent money from a friend.
        Assert.False(r.SaidCategory("favor_fee"), r.Trace());
        r.Run(20);
        Assert.False(r.Bot.X == gate.X && r.Bot.Y == gate.Y, "the bot still stands on its gate" + r.Trace());
    }

    [Fact]
    public void AGateAskedForWithNoPlaceAsksWhereThenOpens()
    {
        var r = Make();
        r.Engine.OnHear(r.Player, "wanderer gate pls");
        r.Run(50, () => r.SaidCategory("gate_where"));
        Assert.Contains(r.Said, s => s.Category == "gate_where" && s.Line.Contains("farton"));
        r.Engine.OnHear(r.Player, "farton");
        r.Run(400, () => r.Engine.WindowGates > 0, realTime: true);
        Assert.True(r.Engine.WindowGates == 1, r.Trace());
    }

    [Fact]
    public void NeverMindDropsTheFavour()
    {
        var r = Make();
        r.Engine.OnHear(r.Player, "wanderer gate pls");
        r.Run(50, () => r.SaidCategory("gate_where"));
        r.Engine.OnHear(r.Player, "nevermind");
        r.Run(50, () => r.SaidCategory("favor_cancel"));
        Assert.True(r.SaidCategory("favor_cancel"), r.Trace());
        Assert.Null(r.Engine.MindOf(r.Bot)!.Favor);
    }

    [Fact]
    public void AStrangerIsAskedTheReagentsWorthAndTheGateOpensOncePaid()
    {
        var r = Make(friends: false);
        r.Engine.ReagentWorthOf = _ => 30;
        var gold = r.World.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = 100;
        Assert.True(r.Player.Backpack!.TryAddItem(gold));

        r.Engine.OnHear(r.Player, "wanderer gate to farton");
        r.Run(60, () => r.SaidCategory("favor_fee"));
        Assert.Contains(r.Said, s => s.Category == "favor_fee" && s.Line == "30gp");
        Assert.Equal(0, r.Engine.WindowGates);

        // The money in a trade window.
        r.PlayerClient.HandleItemPickup(gold.Uid.Value, 30);
        r.PlayerClient.HandleItemDrop(gold.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot);
        Assert.True(trade != null, r.Trace());
        r.Run(60, () => BotAccepted(trade!, r.Bot));
        Assert.True(BotAccepted(trade!, r.Bot), r.Trace());
        r.PlayerClient.HandleSecureTrade(2, trade!.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(trade.IsCompleted);

        r.Run(400, () => r.Engine.WindowGates > 0, realTime: true);
        Assert.True(r.Engine.WindowGates == 1, r.Trace());
    }

    [Fact]
    public void AGateOnTheRuneAPlayerHandsOverAndTheRuneComesBack()
    {
        var r = Make(fartonRune: false);
        r.Engine.OnHear(r.Player, "wanderer gate pls");
        r.Run(50, () => r.SaidCategory("gate_no_runes"));
        Assert.True(r.SaidCategory("gate_no_runes"), r.Trace());

        var rune = Rune(r.World, r.Player.Backpack!, new Point3D(400, 402, 0, 0));
        r.PlayerClient.HandleItemPickup(rune.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(rune.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot);
        Assert.True(trade != null, r.Trace());
        r.Run(60, () => BotAccepted(trade!, r.Bot));
        Assert.True(BotAccepted(trade!, r.Bot), r.Trace());
        r.PlayerClient.HandleSecureTrade(2, trade!.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(trade.IsCompleted);
        Assert.Equal(r.Bot.Backpack!.Uid, rune.ContainedIn);

        r.Run(400, () => r.Engine.WindowGates > 0, realTime: true);
        Assert.True(r.Engine.WindowGates == 1, r.Trace());

        // The rune goes back in a trade the bot opens.
        r.Run(200, () => r.Trades.FindTradeFor(r.Bot) is { } t && t.GetOwnContainer(r.Bot).Contents.Contains(rune));
        var back = r.Trades.FindTradeFor(r.Bot);
        Assert.True(back != null, r.Trace());
        r.Run(60, () => BotAccepted(back!, r.Bot));
        Assert.True(BotAccepted(back!, r.Bot), r.Trace());
        r.PlayerClient.HandleSecureTrade(2, back!.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(back.IsCompleted);
        Assert.Equal(r.Player.Backpack!.Uid, rune.ContainedIn);
        r.Run(20);
        Assert.False(r.Bot.TryGetTag(PlayerBotEngine.HeldTag, out string? held) && !string.IsNullOrEmpty(held), held);
    }

    [Fact]
    public void ABotWithoutTheSpellSaysSo()
    {
        var r = Make(mage: false);
        r.Engine.OnHear(r.Player, "wanderer gate to farton");
        r.Run(50, () => r.Said.Count > 0);
        Assert.Equal(0, r.Engine.WindowGates);
        Assert.Null(r.Engine.MindOf(r.Bot)!.Favor);
    }

    // ------------------------------------------------------------------
    //  What it can do, the way shown
    // ------------------------------------------------------------------

    [Fact]
    public void WhatCanYouDoListsTheGateWithItsRunes()
    {
        var r = Make();
        r.Engine.OnHear(r.Player, "wanderer what can you do");
        r.Run(50, () => r.SaidCategory("respond_can"));
        Assert.Contains(r.Said, s => s.Category == "respond_can" && s.Line.Contains("gate (farton)") && s.Line.Contains("guide"));
    }

    [Fact]
    public void TheWayIsShownAndThePlayerWaitedFor()
    {
        var r = Make();
        r.Engine.OnHear(r.Player, "wanderer take me to the inn");
        r.Run(50, () => r.SaidCategory("guide_ok"));
        Assert.True(r.SaidCategory("guide_ok"), r.Trace());
        var mind = r.Engine.MindOf(r.Bot)!;
        Assert.Equal("Town Inn", mind.Goal?.Name);

        // The player stays put: the bot does not run off with nobody behind it.
        r.Run(80);
        Assert.True(r.Bot.Position.GetDistanceTo(r.Player.Position) <= 12, r.Trace());

        // The player keeps up: they arrive together.
        for (int i = 0; i < 600 && !r.SaidCategory("guide_here"); i++)
        {
            var b = r.Bot.Position;
            r.World.MoveCharacter(r.Player, new Point3D((short)(b.X + 1), b.Y, b.Z, b.Map));
            r.Run(1);
        }
        Assert.True(r.SaidCategory("guide_here"), r.Trace());
        Assert.True(r.Bot.Position.GetDistanceTo(new Point3D(61, 101, 0, 0)) <= 3, r.Trace());
    }

    // ------------------------------------------------------------------
    //  Friends and companions
    // ------------------------------------------------------------------

    [Fact]
    public void TalkBuildsABondUntilTheyAreFriends()
    {
        var r = Make(friends: false);
        r.Engine.SetBondRule("talk_gap", 0);
        r.Engine.SetBondRule("talk", 5);
        var mind = r.Engine.MindOf(r.Bot)!;
        for (int i = 0; i < 6; i++)
        {
            r.Engine.OnHear(r.Player, "wanderer hi");
            r.Run(40);
        }
        Assert.True(r.Engine.BondScore(mind, r.Player) >= 25, $"score {r.Engine.BondScore(mind, r.Player)} {r.Trace()}");
        Assert.Equal(BotBondLevel.Friend, r.Engine.BondLevel(mind, r.Player));
        Assert.Equal(1, r.Engine.WindowNewFriends);
    }

    [Fact]
    public void AStrangerIsNoCompanionYet()
    {
        var r = Make(friends: false);
        r.Engine.OnHear(r.Player, "wanderer travel with me");
        r.Run(50, () => r.SaidCategory("companion_not_yet"));
        Assert.True(r.SaidCategory("companion_not_yet"), r.Trace());
        Assert.Null(r.Engine.MindOf(r.Bot)!.Companion);
    }

    [Fact]
    public void ACompanionFollowsWaitsWhenToldAndGoesWhenLetGo()
    {
        var r = Make();
        var mind = r.Engine.MindOf(r.Bot)!;
        r.Engine.OnHear(r.Player, "wanderer travel with me");
        r.Run(50, () => r.SaidCategory("companion_yes"));
        Assert.Same(r.Player, mind.Companion);
        Assert.True(r.Bot.TryGetTag(PlayerBotEngine.CompanionTag, out string? tag) && tag == r.Player.Uid.Value.ToString());

        r.World.MoveCharacter(r.Player, new Point3D(115, 100, 0, 0));
        r.Run(150, () => r.Bot.Position.GetDistanceTo(r.Player.Position) <= 3);
        Assert.True(r.Bot.Position.GetDistanceTo(r.Player.Position) <= 3, r.Trace());

        // "Wait here" needs no name: a companion is who the player talks to.
        r.Engine.OnHear(r.Player, "wait here");
        r.Run(40, () => r.SaidCategory("party_stay"));
        var spot = r.Bot.Position;
        r.World.MoveCharacter(r.Player, new Point3D(105, 100, 0, 0));
        r.Run(80);
        Assert.True(r.Bot.Position.GetDistanceTo(spot) <= 1, r.Trace());

        r.Engine.OnHear(r.Player, "wanderer you can go");
        r.Run(40, () => r.SaidCategory("companion_bye"));
        Assert.Null(mind.Companion);
        Assert.False(r.Bot.TryGetTag(PlayerBotEngine.CompanionTag, out _));
    }

    [Fact]
    public void ACompanionIsTakenBackFromTheSave()
    {
        var r = Make();
        r.Bot.SetTag(PlayerBotEngine.CompanionTag, r.Player.Uid.Value.ToString());
        r.Engine.Release(r.Bot);
        r.Engine.AdoptSaved();
        Assert.Same(r.Player, r.Engine.MindOf(r.Bot)!.Companion);
    }

    [Fact]
    public void ABotRaisedByAPlayerThanksThemAndWarmsToThem()
    {
        var r = Make(friends: false);
        var mind = r.Engine.MindOf(r.Bot)!;
        // It died a while ago and was raised where it lay, not carried to a healer.
        mind.DeadSinceMs = r.Now - 10_000;
        r.Run(150, () => r.SaidCategory("thanks_raised"));
        Assert.True(r.SaidCategory("thanks_raised"), r.Trace());
        Assert.Equal(0, mind.DeadSinceMs);
        Assert.True(r.Engine.BondScore(mind, r.Player) >= 8);
    }

    [Fact]
    public void AFriendOffersAHealAndCastsItOnYes()
    {
        var r = Make();
        r.Player.Hits = 20;
        r.Run(200, () => r.SaidCategory("offer_heal"));
        Assert.True(r.SaidCategory("offer_heal"), r.Trace());
        r.Engine.OnHear(r.Player, "yes");
        r.Run(300, () => r.Player.Hits > 20, realTime: true);
        Assert.True(r.Player.Hits > 20, $"hits {r.Player.Hits} {r.Trace()}");
    }

    [Fact]
    public void ACompanionHoldsThingsNeverSellsThemAndHandsThemBack()
    {
        var r = Make();
        r.Engine.ValueOf = _ => 100;
        var mind = r.Engine.MindOf(r.Bot)!;
        r.Engine.OnHear(r.Player, "wanderer travel with me");
        r.Run(50, () => mind.Companion != null);

        r.Engine.OnHear(r.Player, "wanderer hold this");
        r.Run(50, () => r.SaidCategory("carry_give"));
        Assert.True(r.SaidCategory("carry_give"), r.Trace());
        var sword = r.World.CreateItem();
        sword.BaseId = 0x13FF;
        sword.Name = "katana";
        Assert.True(r.Player.Backpack!.TryAddItem(sword));
        r.PlayerClient.HandleItemPickup(sword.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(sword.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot)!;
        r.Run(60, () => BotAccepted(trade, r.Bot));
        Assert.True(BotAccepted(trade, r.Bot), r.Trace());
        // It puts in no gold for what it only holds.
        Assert.DoesNotContain(trade.GetOwnContainer(r.Bot).Contents, i => i.ItemType == ItemType.Gold);
        r.PlayerClient.HandleSecureTrade(2, trade.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(trade.IsCompleted);
        Assert.Equal(r.Bot.Backpack!.Uid, sword.ContainedIn);
        r.Run(20);
        Assert.True(r.SaidCategory("carry_ok"), r.Trace());

        r.Engine.OnHear(r.Player, "wanderer what are you selling");
        r.Run(50, () => r.SaidCategory("trade_stock_none") || r.SaidCategory("trade_stock"));
        Assert.True(r.SaidCategory("trade_stock_none"), r.Trace());

        r.Engine.OnHear(r.Player, "wanderer give back my stuff");
        r.Run(200, () => r.Trades.FindTradeFor(r.Bot) is { } t && t.GetOwnContainer(r.Bot).Contents.Contains(sword));
        var back = r.Trades.FindTradeFor(r.Bot)!;
        r.Run(60, () => BotAccepted(back, r.Bot));
        r.PlayerClient.HandleSecureTrade(2, back.GetOwnContainer(r.Player).Uid.Value, 1);
        Assert.True(back.IsCompleted, r.Trace());
        Assert.Equal(r.Player.Backpack!.Uid, sword.ContainedIn);
    }

    [Fact]
    public void ACompanionWaitsWhenThePlayerLogsOutAndComesToThemWhenTheyAreBack()
    {
        var r = Make();
        var mind = r.Engine.MindOf(r.Bot)!;
        r.Engine.OnHear(r.Player, "wanderer travel with me");
        r.Run(50, () => mind.Companion != null);
        Assert.Same(r.Player, mind.Companion);

        // Logged out: it stays where they parted.
        r.Player.IsOnline = false;
        r.World.RemoveOnlinePlayer(r.Player);
        var parted = r.Bot.Position;
        r.Run(300);
        Assert.True(r.Bot.Position.GetDistanceTo(parted) <= 3, r.Trace());
        Assert.True(mind.CompanionMissed);

        // Back, far off along the road: it comes, and says so.
        r.World.MoveCharacter(r.Player, new Point3D(59, 101, 0, 0));
        r.Player.IsOnline = true;
        r.World.AddOnlinePlayer(r.Player);
        r.Run(900, () => r.SaidCategory("companion_back") && r.Bot.Position.GetDistanceTo(r.Player.Position) <= 4);
        Assert.True(r.SaidCategory("companion_back"), r.Trace());
        Assert.True(r.Bot.Position.GetDistanceTo(r.Player.Position) <= 4, r.Trace());
    }

    [Fact]
    public void ACasterMarksABlankRuneAtATownBankItHasNoRuneFor()
    {
        var r = Make(fartonRune: false);
        var mind = r.Engine.MindOf(r.Bot)!;
        var blank = Rune(r.World, r.Bot.Backpack!, null);
        r.World.MoveCharacter(r.Bot, new Point3D(122, 101, 0, 0));
        mind.At = r.Engine.Nav.FindDestination("Town Bank");
        mind.LingerAnchor = r.Bot.Position;
        mind.NextActMs = 0;
        r.Run(300, () => r.Engine.WindowMarksTaken > 0, realTime: true);
        Assert.True(r.Engine.WindowMarksTaken == 1, r.Trace());
        Assert.True(blank.TryGetRuneMark(out var mark) && mark.GetDistanceTo(new Point3D(122, 101, 0, 0)) <= 3);
    }

    [Fact]
    public void ARuneMarkedOffTheMapIsBlank()
    {
        // The pack's i_rune_marker is created with MOREP=-1,-1: Source-X's
        // IsValidPoint fails, so the rune is blank, as one bought from a mage is.
        var world = World();
        var rune = world.CreateItem();
        rune.ItemType = ItemType.Rune;
        rune.SetRuneMark(new Point3D(-1, -1, 0, 0));
        Assert.False(rune.HasRuneMark);
        rune.SetRuneMark(new Point3D(400, 402, 0, 0));
        Assert.True(rune.HasRuneMark);
    }

    [Fact]
    public void ACasterWithNoBlankRuneNeedsToBuyOne()
    {
        var r = Make(fartonRune: true);
        r.Engine.AddNeed(new BotNeed("rune", ItemType.Rune, 1, 2, ["VendorMage"]));
        var mind = r.Engine.MindOf(r.Bot)!;
        Assert.Equal("rune", r.Engine.FirstNeed(mind)?.Name);
        Rune(r.World, r.Bot.Backpack!, null);
        Assert.Null(r.Engine.FirstNeed(mind));
        // A full library needs none.
        r.Engine.RuneCap = 1;
        r.World.DeleteObject(r.Bot.Backpack!.Contents.First(i => i.ItemType == ItemType.Rune && !i.HasRuneMark));
        Assert.Null(r.Engine.FirstNeed(mind));
    }

    [Fact]
    public void ABlessAskedForIsCast()
    {
        var r = Make();
        r.Engine.Intents = BotIntents.Parse([.. IntentLines, "buff.Bless: bless"]);
        int mana = r.Bot.Mana;
        r.Engine.OnHear(r.Player, "wanderer bless me pls");
        r.Run(300, () => r.Engine.WindowFavors > 0 && r.Bot.Mana < mana, realTime: true);
        Assert.True(r.Engine.WindowFavors >= 1 && r.Bot.Mana < mana, $"mana {r.Bot.Mana} {r.Trace()}");
    }

    [Fact]
    public void APresentIsTakenForNothingAndWarmsTheBot()
    {
        var r = Make(friends: false);
        r.Engine.ValueOf = _ => 300;
        r.Engine.Intents = BotIntents.Parse([.. IntentLines, "gift: gift"]);
        var mind = r.Engine.MindOf(r.Bot)!;
        r.Engine.OnHear(r.Player, "wanderer a gift for you");
        r.Run(50, () => mind.Favor != null);
        var sword = r.World.CreateItem();
        sword.BaseId = 0x13FF;
        sword.Name = "katana";
        Assert.True(r.Player.Backpack!.TryAddItem(sword));
        r.PlayerClient.HandleItemPickup(sword.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(sword.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot)!;
        r.Run(60, () => BotAccepted(trade, r.Bot));
        Assert.True(BotAccepted(trade, r.Bot), r.Trace());
        Assert.DoesNotContain(trade.GetOwnContainer(r.Bot).Contents, i => i.ItemType == ItemType.Gold);
        r.PlayerClient.HandleSecureTrade(2, trade.GetOwnContainer(r.Player).Uid.Value, 1);
        r.Run(20);
        Assert.Equal(1, r.Engine.WindowGifts);
        Assert.Equal(r.Bot.Backpack!.Uid, sword.ContainedIn);
        Assert.False(r.Bot.TryGetTag(PlayerBotEngine.HeldTag, out string? held) && !string.IsNullOrEmpty(held));
        Assert.True(r.Engine.BondScore(mind, r.Player) >= 3, r.Trace());
    }

    [Fact]
    public void APriceIsToldAndNothingIsBought()
    {
        var r = Make();
        r.Engine.ValueOf = _ => 100;
        r.Engine.Intents = BotIntents.Parse([.. IntentLines, "appraise: what+is+this+worth"]);
        var mind = r.Engine.MindOf(r.Bot)!;
        r.Engine.OnHear(r.Player, "wanderer what is this worth");
        r.Run(50, () => mind.Favor != null);
        var sword = r.World.CreateItem();
        sword.BaseId = 0x13FF;
        sword.Name = "katana";
        Assert.True(r.Player.Backpack!.TryAddItem(sword));
        r.PlayerClient.HandleItemPickup(sword.Uid.Value, 1);
        r.PlayerClient.HandleItemDrop(sword.Uid.Value, 0, 0, 0, r.Bot.Uid.Value);
        var trade = r.Trades.FindTradeFor(r.Bot)!;
        r.Run(150, () => r.Trades.FindTradeFor(r.Bot) == null);
        Assert.Null(r.Trades.FindTradeFor(r.Bot));
        Assert.Contains(r.Said, s => s.Category == "appraise_value" && s.Line.Contains("katana 100gp"));
        Assert.Equal(r.Player.Backpack!.Uid, sword.ContainedIn);
    }

    // ------------------------------------------------------------------
    //  Shipped data
    // ------------------------------------------------------------------

    private static string DataDir() => TestRepo.Tracked("playerbots");

    [Fact]
    public void ShippedIntentsReadTheFavours()
    {
        var intents = BotIntents.Load(DataDir());
        Assert.Equal((BotIntentKind.Gate, "town:Britain"), Kind(intents.Parse("britain'e gate açar mısın")));
        Assert.Equal((BotIntentKind.Gate, "town:Trinsic"), Kind(intents.Parse("can you open a gate to trinsic?")));
        Assert.Equal((BotIntentKind.Gate, null), Kind(intents.Parse("gate atar mısın")));
        Assert.Equal((BotIntentKind.Where, "Moongate"), Kind(intents.Parse("gate nerede")));
        Assert.Equal(BotIntentKind.Buff, intents.Parse("bless ver").Kind);
        Assert.Equal(SpellType.Bless, intents.Parse("bless ver").Spell);
        Assert.Equal(SpellType.NightSight, intents.Parse("gece görüşü atar mısın").Spell);
        Assert.Equal((BotIntentKind.Guide, "Bank"), Kind(intents.Parse("beni bankaya götürür müsün")));
        Assert.Equal((BotIntentKind.Guide, "Inn"), Kind(intents.Parse("take me to the inn")));
        Assert.Equal(BotIntentKind.Can, intents.Parse("ne yapabilirsin").Kind);
        Assert.Equal(BotIntentKind.Can, intents.Parse("what can you do?").Kind);
        Assert.Equal(BotIntentKind.Cancel, intents.Parse("boşver").Kind);
        Assert.Equal(BotIntentKind.Companion, intents.Parse("benimle gezer misin").Kind);
        Assert.Equal(BotIntentKind.Friend, intents.Parse("arkadaş olalım mı").Kind);
        Assert.Equal(BotIntentKind.Remember, intents.Parse("beni hatırlıyor musun").Kind);
        Assert.Equal(BotIntentKind.GiveBack, intents.Parse("eşyalarımı ver").Kind);
        Assert.Equal(BotIntentKind.Carry, intents.Parse("bunu taşır mısın").Kind);
        Assert.Equal(BotIntentKind.Appraise, intents.Parse("bu ne eder").Kind);
        Assert.Equal(BotIntentKind.Mark, intents.Parse("rün işaretler misin").Kind);
        Assert.Equal(BotIntentKind.Protect, intents.Parse("beni koru").Kind);
        Assert.Equal(BotIntentKind.Dismiss, intents.Parse("serbestsin").Kind);
        Assert.Equal(BotIntentKind.Follow, intents.Parse("sen de gel").Kind);
        Assert.Equal(BotIntentKind.Gift, intents.Parse("bu senin, hediye").Kind);
        Assert.Equal(BotIntentKind.No, intents.Parse("gate açma, istemiyorum").Kind);

        static (BotIntentKind, string?) Kind(BotIntent i) => (i.Kind, i.PlaceType);
    }

    [Fact]
    public void ShippedFavourWordsExistInEveryLanguage()
    {
        string dir = DataDir();
        string[] categories =
        [
            "gate_cant", "gate_where", "gate_no_runes", "gate_no_rune", "gate_ok", "gate_open", "gate_along",
            "gate_blocked_here", "gate_blocked_there", "mark_cant", "mark_give", "mark_ok", "mark_done",
            "buff_cant", "buff_wait", "buff_ok", "guide_where", "guide_unknown", "guide_far", "guide_far_gate",
            "guide_ok", "guide_wait", "guide_here", "carry_no", "carry_give", "carry_heavy", "carry_ok",
            "gift_ok", "gift_thanks", "appraise_show", "appraise_value", "giveback_none", "giveback_yes",
            "giveback_done", "favor_refuse", "favor_refuse_grudge", "favor_refuse_red", "favor_busy", "favor_fee",
            "favor_cancel", "favor_mana", "favor_no_regs", "favor_failed", "favor_retry", "favor_cant_reach",
            "favor_rune_wrong", "favor_rune_blank", "respond_can", "respond_can_none", "token_ability",
            "friend_already", "friend_soon", "friend_new", "bond_friend", "thanks_raised", "remember_no",
            "remember_yes", "memory_met", "memory_hunt", "memory_raised", "memory_gate", "memory_gift",
            "memory_guide", "token_ago_today", "token_ago_days", "token_ago_long", "companion_yes",
            "companion_already", "companion_busy", "companion_taken", "companion_not_yet", "companion_bye",
            "dismiss_none", "companion_back", "companion_ghost", "companion_res_soon", "protect_yes", "protect_no",
            "offer_heal", "offer_cure", "offer_help", "warn_red",
        ];
        foreach (string lang in new[] { "en", "tr" })
            foreach (string c in categories)
                Assert.True(File.Exists(Path.Combine(dir, "chat", lang, c + ".txt")), $"chat/{lang}/{c}.txt missing");
        var book = BotChatBook.Load(dir, "tr");
        foreach (string lang in new[] { "en", "tr" })
        {
            // Offers are questions: the yes is read against them.
            foreach (string offer in new[] { "offer_heal", "offer_cure", "offer_help", "guide_far_gate" })
                for (int i = 0; book.Line(offer, i, lang) is { } line; i++)
                    Assert.True(line.TrimEnd().EndsWith('?'), $"{lang}/{offer}: {line}");
            foreach (string key in new[] { "gate", "heal", "guide", "companion" })
                Assert.True(Enumerable.Range(0, 20).Select(i => book.Line("token_ability", i, lang))
                    .Any(l => l != null && l.StartsWith(key + "=")), $"{lang} token_ability {key}");
        }
        Assert.True(File.Exists(Path.Combine(dir, "bonds.txt")));
    }
}
