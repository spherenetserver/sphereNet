using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Core.Enums;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Bots talking with players and with each other: who hears, what a line
/// asks for, directions, a player's focus, greetings by name, scenes, news, haggling,
/// where bots gather, and that the shipped chat data holds together.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotChatTests
{
    // East-west road A (100,100) - B (110,100) - C (120,100); a bank by C, an inn far west.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "W", "X": 60, "Y": 100, "Z": 0, "Connects": [ "A" ] },
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] },
          { "Name": "Town Inn", "X": 60, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "W" }
        ] }
        """;

    private static readonly string[] IntentLines =
    [
        "res: res rez",
        "where: where nerde nerede",
        "party: lfg group grup",
        "wts: wts",
        "wtb: wtb",
        "thanks: ty thanks sağol",
        "bye: bye cya",
        "howareyou: how+are+you naber",
        "greet: hi hello selam",
        "question: why",
        "bot: are+you+a+bot",
        "heal: heal+me",
        "follow: follow+me",
        "doing: what+are+you+doing where+are+you+going napıyon",
        "here: where+are+we burası+neresi",
        "whoareyou: what+are+you",
        "news: any+news",
        "far: how+far",
        "yes: yes yeah evet",
        "no: no nope hayır",
        "not: don+t not",
        "more: what+about peki",
        "place.Inn: inn",
        "place.Bank: bank banka",
        "place.Healer: healer",
        "town.Town: town kasaba",
        "lang.tr: nerde abi kanka",
        "lang.en: where is the",
    ];

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

    private static BotNavGraph Nav(GameWorld world)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        return nav;
    }

    /// <summary>Collects what the engine logs, so a bot act that threw fails the test.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception) + " " + exception);
        }
    }

    private sealed class Rig
    {
        public required ListLogger Log { get; init; }
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public List<(Character Who, string Line)> Said { get; } = [];
        public long Now { get; set; } = 1_000_000;

        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 100)
            {
                Engine.Tick(Now);
                Now += 100;
            }
            Assert.Empty(Log.Warnings);
        }
    }

    private static Rig MakeRig(Dictionary<string, string[]> en, Dictionary<string, string[]>? tr = null,
        string[]? scenes = null)
    {
        var world = World();
        var log = new ListLogger();
        var langs = new Dictionary<string, IReadOnlyDictionary<string, string[]>> { ["en"] = en };
        if (tr != null) langs["tr"] = tr;
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, Nav(world),
            BotChatBook.FromLanguages("en", langs), log, seed: 11)
        {
            TargetCount = 1000, // above the bots made here: none is logged out as surplus
            Intents = BotIntents.Parse(IntentLines),
            Scenes = BotSceneBook.Parse(scenes ?? []),
            Gravity = 0,
        };
        var rig = new Rig { World = world, Engine = engine, Log = log };
        engine.Say = (ch, line) => rig.Said.Add((ch, line));
        return rig;
    }

    private static Character Bot(Rig rig, string name, Point3D at, bool stay = true, Action<Character>? setup = null)
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        setup?.Invoke(ch);
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        if (stay)
        {
            var mind = rig.Engine.MindOf(ch)!;
            mind.LingerUntilMs = long.MaxValue;
            mind.At = rig.Engine.Nav.FindDestination("Town Bank");
        }
        return ch;
    }

    private static Character Player(Rig rig, string name, Point3D at)
    {
        var p = rig.World.CreateCharacter();
        p.IsPlayer = true;
        p.IsOnline = true;
        p.Name = name;
        rig.World.PlaceCharacter(p, at);
        rig.World.AddOnlinePlayer(p);
        return p;
    }

    [Theory]
    [InlineData("where is the bank?", BotIntentKind.Where, "Bank")]
    [InlineData("bankaya nerden gidilir abi nerde", BotIntentKind.Where, "Bank")]
    [InlineData("town nerde", BotIntentKind.Where, "town:Town")]
    [InlineData("lfg anyone", BotIntentKind.Party, null)]
    [InlineData("rez pls", BotIntentKind.Res, null)]
    [InlineData("ty", BotIntentKind.Thanks, null)]
    [InlineData("how are you", BotIntentKind.HowAreYou, null)]
    [InlineData("selamlar", BotIntentKind.Greet, null)]
    [InlineData("why", BotIntentKind.Question, null)]
    [InlineData("nice weather", BotIntentKind.None, null)]
    [InlineData("where are you going?", BotIntentKind.Doing, null)]
    [InlineData("napıyon", BotIntentKind.Doing, null)]
    [InlineData("burası neresi", BotIntentKind.Here, null)]
    [InlineData("where are we", BotIntentKind.Here, null)]
    [InlineData("what are you", BotIntentKind.WhoAreYou, null)]
    [InlineData("any news?", BotIntentKind.News, null)]
    [InlineData("heal me pls", BotIntentKind.Heal, null)]
    [InlineData("where is the healer", BotIntentKind.Where, "Healer")]
    [InlineData("are you a bot", BotIntentKind.Bot, null)]
    [InlineData("bank?", BotIntentKind.Where, "Bank")]
    [InlineData("what about the inn", BotIntentKind.Where, "Inn")]
    [InlineData("i like the bank in this town a lot", BotIntentKind.None, null)]
    [InlineData("yes", BotIntentKind.Yes, null)]
    [InlineData("nope", BotIntentKind.No, null)]
    [InlineData("i don't want a group", BotIntentKind.No, null)]
    [InlineData("how far", BotIntentKind.Far, null)]
    public void LinesAreReadForWhatTheyAsk(string text, BotIntentKind kind, string? placeType)
    {
        var intent = BotIntents.Parse(IntentLines).Parse(text);
        Assert.Equal(kind, intent.Kind);
        Assert.Equal(placeType, intent.PlaceType);
    }

    [Fact]
    public void AHelloOnTheSameLineIsKept()
    {
        var intents = BotIntents.Parse(IntentLines);
        Assert.Equal(new BotIntent(BotIntentKind.Where, "Bank", "bank", AlsoGreets: true), intents.Parse("hi, where is the bank?"));
        Assert.False(intents.Parse("hi").AlsoGreets);
    }

    [Fact]
    public void TaggedLinesGoToTheBotsTheyFitAndASaidLineWaits()
    {
        var book = BotChatBook.FromLines("en", new Dictionary<string, string[]>
        {
            ["x"] = ["[mage] a mage line", "[!mage] not a mage", "[mage hurt] hurt mage"],
            ["y"] = ["one", "two"],
        });
        var rng = new Random(3);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("not a mage", book.Pick("x", rng, hasTag: _ => false));
            Assert.Equal("a mage line", book.Pick("x", rng, hasTag: t => t == "mage"));
            Assert.Equal("two", book.Pick("y", rng, isStale: l => l == "one"));
        }
        // Nothing fresh left: a said line is still better than silence.
        Assert.NotNull(book.Pick("y", rng, isStale: _ => true));
        // Without tag questions every line fits, tags taken off.
        Assert.Contains(book.Pick("x", rng), new[] { "a mage line", "not a mage", "hurt mage" });
    }

    [Fact]
    public void AConversationKeepsItsPlaceAndAnsweredQuestionsMeanSomething()
    {
        var rig = MakeRig(new()
        {
            ["respond_where"] = ["{target} {dir} {dist}"],
            ["respond_repeat"] = ["like i said"],
            ["respond_greet"] = ["hey"],
            ["respond_name"] = ["need something?"],
            ["respond_name_yes"] = ["what do u need"],
            ["ack_no"] = ["ok then"],
            ["respond_chat"] = ["yeah"],
            ["respond_doing_linger"] = ["hanging at {at}"],
            ["token_placetype"] = ["Bank=bank"],
            ["token_dir"] = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"],
        });
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        rig.Run(1000);

        string Hear(string text)
        {
            int before = rig.Said.Count;
            rig.Engine.OnHear(hero, text);
            rig.Run(4000);
            var said = rig.Said.Skip(before).ToList();
            Assert.Single(said);
            Assert.Equal(bot, said[0].Who);
            return said[0].Line;
        }

        // A hello and a question on one line get both.
        Assert.Equal("hey, bank east 20", Hear("hi wanderer, where is the bank?"));
        // Another place named after it asks the way there; directions are from the player.
        Assert.Equal("inn west 40", Hear("and the inn"));
        // "How far?" asks about the place last talked of; at once again: "like i said".
        Assert.Equal("like i said, inn west 40", Hear("how far?"));

        // What the bot is doing comes from what it does.
        Assert.Equal("hanging at Town bank", Hear("what are you doing"));

        // A yes or a no answers the bot's own question.
        Assert.Equal("need something?", Hear("wanderer"));
        Assert.Equal("what do u need", Hear("yes"));
        // With no question pending, a yes is just talk.
        Assert.Equal("yeah", Hear("yes"));
        Assert.Equal("need something?", Hear("wanderer"));
        Assert.Equal("ok then", Hear("nope"));
    }

    [Fact]
    public void ABotTellsWhatItIsAndWhetherItCanHeal()
    {
        var rig = MakeRig(new()
        {
            ["respond_whoami"] = ["[mage] mage {skill1}", "[warrior] warrior {skill1} {val1}, {skill2}"],
            ["respond_heal_fine"] = ["ur fine"],
            ["respond_heal_no"] = ["[!mage] no magery", "[mage] no mana"],
        });
        Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0), setup: ch =>
        {
            ch.SetSkill(SkillType.Swordsmanship, 950);
            ch.SetSkill(SkillType.Tactics, 800);
        });
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        hero.MaxHits = 100;
        hero.Hits = 100;
        rig.Run(1000);

        rig.Engine.OnHear(hero, "wanderer what are you");
        rig.Run(4000);
        rig.Engine.OnHear(hero, "heal me");
        rig.Run(4000);
        hero.Hits = 40;
        rig.Engine.OnHear(hero, "heal me");
        rig.Run(4000);
        Assert.Equal(["warrior swordsmanship 95, tactics", "ur fine", "no magery"], rig.Said.Select(s => s.Line));
    }

    [Fact]
    public void TradeShoutsCarryTheGoodsAndTheLanguageIsTold()
    {
        var intents = BotIntents.Parse(IntentLines);
        var wts = intents.Parse("wts gm katana 5k");
        Assert.Equal(BotIntentKind.Wts, wts.Kind);
        Assert.Equal("gm katana", wts.Rest);
        Assert.Equal(0, wts.At);
        Assert.Equal("tr", intents.DetectLanguage("bu ne kanka"));
        Assert.Equal("tr", intents.DetectLanguage("güzel"));
        Assert.Equal("en", intents.DetectLanguage("where is the inn"));
        Assert.Null(intents.DetectLanguage("asdf"));
        Assert.Equal(1500, PlayerBotEngine.ParsePrice("1.5k ok?"));
        Assert.Equal(700, PlayerBotEngine.ParsePrice("700gp"));
        Assert.Equal(0, PlayerBotEngine.ParsePrice("no price"));
    }

    [Fact]
    public void NobodyNearHearsNothingAndAListenerHearsScenes()
    {
        var rig = MakeRig(
            new() { ["friend_greet"] = ["hi {name}"] },
            scenes: ["greet 1 : A:friend_greet > B:friend_greet"]);
        var ann = Bot(rig, "Ann", new Point3D(120, 100, 0, 0));
        var bob = Bot(rig, "Bob", new Point3D(121, 100, 0, 0));

        rig.Run(60_000);
        Assert.Empty(rig.Said);

        Player(rig, "Hero", new Point3D(123, 100, 0, 0));
        rig.Run(40_000);
        Assert.Contains(rig.Said, s => s.Who == ann && s.Line == "hi Bob");
        Assert.Contains(rig.Said, s => s.Who == bob && s.Line == "hi Ann");
        // The first line opens a scene and the other bot answers it, in that order.
        int first = rig.Said.FindIndex(s => s.Line.StartsWith("hi "));
        Assert.NotEqual(rig.Said[first].Who, rig.Said[first + 1].Who);
    }

    [Fact]
    public void AskedTheWayABotPointsAtTheNearestPlaceOfThatKind()
    {
        var rig = MakeRig(new()
        {
            ["respond_where"] = ["{target} {dir} {dist}"],
            ["token_dir"] = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"],
        });
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        rig.Run(1000);

        rig.Engine.OnHear(hero, "Wanderer where is the bank?");
        rig.Run(4000);
        // The bank stands 21 tiles east of the bot.
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "bank east 20");
    }

    [Fact]
    public void APlayerKeepsTheBotTheySpokeToAndIsAnsweredInTheirLanguage()
    {
        var rig = MakeRig(
            new()
            {
                ["respond_greet"] = ["hello"],
                ["respond_howru"] = ["good u?"],
                ["respond_chat"] = ["yeah"],
            },
            new()
            {
                ["respond_howru"] = ["iyi sen"],
            });
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        Bot(rig, "Other", new Point3D(102, 101, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        rig.Run(1000);

        rig.Engine.OnHear(hero, "hi wanderer");
        rig.Run(4000);
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "hello");

        // Not named again: the bot spoken to answers, the other one does not.
        rig.Engine.OnHear(hero, "nice weather");
        rig.Run(4000);
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "yeah");

        rig.Engine.OnHear(hero, "naber kanka");
        rig.Run(4000);
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "iyi sen");
        Assert.DoesNotContain(rig.Said, s => s.Who != bot);

        // A player the bot talked with is remembered on the character.
        Assert.True(bot.TryGetTag(PlayerBotEngine.KnownTag, out string? known));
        Assert.Contains(hero.Uid.Value.ToString(), known!);
    }

    [Fact]
    public void AnAcquaintanceIsGreetedByName()
    {
        var rig = MakeRig(new() { ["friend_greet"] = ["yo {name}"], ["respond_greet"] = ["hi"] });
        var hero = Player(rig, "Hero Smith", new Point3D(200, 200, 0, 0));
        var bots = new List<Character>();
        for (int i = 0; i < 3; i++)
        {
            var b = Bot(rig, $"Bot{i}", new Point3D((short)(100 + i), 100, 0, 0));
            b.SetTag(PlayerBotEngine.KnownTag, hero.Uid.Value.ToString());
            bots.Add(b);
        }
        rig.Run(3000);
        Assert.Empty(rig.Said);

        rig.World.MoveCharacter(hero, new Point3D(101, 102, 0, 0));
        rig.Run(6000);
        Assert.Contains(rig.Said, s => bots.Contains(s.Who) && s.Line == "yo Hero");
        Assert.Single(rig.Said);
    }

    [Fact]
    public void AnOfferedPriceIsBargainedOnceThenWalkedAwayFrom()
    {
        var rig = MakeRig(new()
        {
            ["haggle_offer"] = ["{price} for {item}?"],
            ["haggle_walkaway"] = ["nvm"],
        });
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        rig.Run(1000);
        var mind = rig.Engine.MindOf(bot)!;
        mind.TalkTopic = "wts";
        mind.TalkItem = "gm katana";

        rig.Engine.OnHear(hero, "Wanderer 1000");
        rig.Run(4000);
        var offer = rig.Said.Single(s => s.Who == bot).Line;
        Assert.EndsWith(" for gm katana?", offer);
        int price = int.Parse(offer.Split(' ')[0]);
        Assert.InRange(price, 550, 750);
        Assert.Equal(0, price % 50);

        rig.Engine.OnHear(hero, "Wanderer 900");
        rig.Run(4000);
        Assert.Equal("nvm", rig.Said[^1].Line);
    }

    [Fact]
    public void ADeathIsNewsTheBotsTell()
    {
        var rig = MakeRig(
            new()
            {
                ["gossip_death"] = ["{actor} died to {other} at {place} {when}"],
                ["token_when_now"] = ["just now"],
            },
            scenes: ["news 1 event=death : A:gossip_death"]);
        var bot = Bot(rig, "Teller", new Point3D(120, 100, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(122, 100, 0, 0));
        var victim = Player(rig, "Victim", new Point3D(118, 101, 0, 0));
        var orc = rig.World.CreateCharacter();
        orc.Name = "an orc lord";
        rig.World.PlaceCharacter(orc, new Point3D(117, 101, 0, 0));

        rig.Run(15_000);
        Assert.Empty(rig.Said); // no news yet, no scene fits

        rig.Engine.OnDeath(victim, orc);
        rig.Run(60_000);
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "Victim died to orc lord at Town just now");
    }

    [Fact]
    public void TownTripsAreDrawnTowardPlayers()
    {
        var rig = MakeRig(new());
        rig.Engine.Gravity = 100;
        Player(rig, "Hero", new Point3D(124, 100, 0, 0));
        var bots = Enumerable.Range(0, 6)
            .Select(i => Bot(rig, $"Walker{i}", new Point3D((short)(100 + i), 101, 0, 0), stay: false))
            .ToList();
        rig.Run(1500);
        foreach (var b in bots)
            Assert.Equal("Town Bank", rig.Engine.MindOf(b)!.Goal?.Name);
    }

    [Fact]
    public void PlaceWeightsDecideWhereBotsGo()
    {
        var rig = MakeRig(new());
        rig.Engine.SetPlaceWeight("Bank", 0);
        rig.Engine.SetPlaceWeight("Inn", 5);
        var bots = Enumerable.Range(0, 6)
            .Select(i => Bot(rig, $"Walker{i}", new Point3D((short)(100 + i), 101, 0, 0), stay: false))
            .ToList();
        rig.Run(1500);
        foreach (var b in bots)
            Assert.Equal("Town Inn", rig.Engine.MindOf(b)!.Goal?.Name);
    }

    [Fact]
    public void ACasterRaisesAGhostNearIt()
    {
        var rig = MakeRig(new() { ["res_offer"] = ["hold still"] });
        var mage = Bot(rig, "Mage", new Point3D(100, 100, 0, 0));
        mage.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_mage_m");
        mage.SetSkill(SphereNet.Core.Enums.SkillType.Magery, 900);
        mage.Int = 100; mage.MaxMana = 100; mage.Mana = 100;
        // A caster casts what its book holds: a full book, filled the way kits do it.
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        mage.Backpack = pack;
        mage.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
        var book = rig.World.CreateItem();
        book.BaseId = 0x0EFA;
        book.ItemType = SphereNet.Core.Enums.ItemType.Spellbook;
        Assert.True(book.TrySetProperty("ADDCIRCLE", "8,1"));
        Assert.True(pack.TryAddItem(book));
        rig.Engine.Release(mage);
        rig.Engine.AdoptSaved();
        rig.Engine.MindOf(mage)!.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Engine.MindOf(mage)!.IsCaster);

        var ghost = Player(rig, "Ghosty", new Point3D(103, 100, 0, 0));
        ghost.SetStatFlag(SphereNet.Core.Enums.StatFlag.Dead);
        rig.Run(30_000);
        Assert.Contains(rig.Said, s => s.Who == mage && s.Line == "hold still");
    }

    [Fact]
    public void BotsCarryTheNamePrefixButAreCalledByTheirOwnName()
    {
        var rig = MakeRig(new() { ["respond_name"] = ["yes?"] });
        rig.Engine.NamePrefix = "[Bot] ";
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        Assert.Equal("[Bot] Wanderer", bot.Name);
        var hero = Player(rig, "Hero", new Point3D(101, 100, 0, 0));
        rig.Run(1000);
        rig.Engine.OnHear(hero, "wanderer");
        rig.Run(4000);
        Assert.Contains(rig.Said, s => s.Who == bot && s.Line == "yes?");

        // A changed prefix replaces the old one the next time the bot comes online.
        rig.Engine.Release(bot);
        rig.Engine.NamePrefix = "(b) ";
        rig.Engine.AdoptSaved();
        Assert.Equal("(b) Wanderer", bot.Name);
    }

    [Fact]
    public void ARaisedBotWithItsThingsBackTakesOffTheResurrectionRobe()
    {
        var rig = MakeRig(new());
        rig.Engine.ResRobeName = "Resurrection Robe";
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        bot.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
        var resRobe = rig.World.CreateItem();
        resRobe.BaseId = 0x1F03;
        resRobe.Name = "Resurrection Robe";
        bot.Equip(resRobe, SphereNet.Core.Enums.Layer.Robe);
        // Its own robe came back off the corpse into the pack, marked with its layer.
        var own = rig.World.CreateItem();
        own.BaseId = 0x1F03;
        own.Hue = new SphereNet.Core.Types.Color(0x0123);
        own.SetTag("EQUIPLAYER", ((byte)SphereNet.Core.Enums.Layer.Robe).ToString());
        Assert.True(pack.TryAddItem(own));

        rig.Run(2000);
        Assert.True(resRobe.IsDeleted);
        Assert.Same(own, bot.GetEquippedItem(SphereNet.Core.Enums.Layer.Robe));
    }

    [Fact]
    public void ABotWithNothingOfItsOwnKeepsTheRobe()
    {
        var rig = MakeRig(new());
        rig.Engine.ResRobeName = "Resurrection Robe";
        var bot = Bot(rig, "Wanderer", new Point3D(100, 100, 0, 0));
        var resRobe = rig.World.CreateItem();
        resRobe.BaseId = 0x1F03;
        resRobe.Name = "Resurrection Robe";
        bot.Equip(resRobe, SphereNet.Core.Enums.Layer.Robe);
        rig.Run(2000);
        Assert.Same(resRobe, bot.GetEquippedItem(SphereNet.Core.Enums.Layer.Robe));
    }

    [Fact]
    public void ATakenStoppingSpotIsReachedBesideIt()
    {
        var rig = MakeRig(new());
        var squatter = rig.World.CreateCharacter();
        squatter.Name = "squatter";
        rig.World.PlaceCharacter(squatter, new Point3D(122, 101, 0, 0));
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Walker";
        ch.Dex = 60; ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.GoalTag, "Town Bank");
        rig.World.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        rig.Engine.AdoptSaved();
        var mind = rig.Engine.MindOf(ch)!;
        for (int i = 0; i < 600 && mind.Mode == BotMode.Travel; i++) rig.Run(100);
        Assert.Equal(BotMode.Linger, mind.Mode);
        Assert.Equal("Town Bank", mind.At?.Name);
        Assert.Equal(1, ch.Position.GetDistanceTo(new Point3D(122, 101, 0, 0)));
        Assert.Equal(0, rig.Engine.StuckRescues);
    }

    [Fact]
    public void ABotInOnlyARobeIsBare()
    {
        var world = World();
        var ch = world.CreateCharacter();
        var robe = world.CreateItem();
        robe.BaseId = 0x1F03;
        ch.Equip(robe, SphereNet.Core.Enums.Layer.Robe);
        Assert.True(PlayerBotEngine.IsBare(ch));
        var boots = world.CreateItem();
        boots.BaseId = 0x170B;
        ch.Equip(boots, SphereNet.Core.Enums.Layer.Shoes);
        Assert.False(PlayerBotEngine.IsBare(ch));
    }

    // ------------------------------------------------------------------
    //  The shipped data
    // ------------------------------------------------------------------

    /// <summary>The shipped bot data: tracked in the repository, so a missing copy
    /// fails the test.</summary>
    private static string DataDir() => TestRepo.Tracked("playerbots");

    [Fact]
    public void ShippedScenesAndRepliesExistInEveryLanguage()
    {
        string dir = DataDir();
        var scenes = BotSceneBook.Load(dir);
        Assert.NotEmpty(scenes.Scenes);
        var categories = scenes.Scenes.SelectMany(s => s.Steps).SelectMany(st => st.Categories)
            .Where(c => c != "$place")
            .Concat([
                "respond_greet", "friend_greet", "respond_bye", "respond_thanks", "respond_howru", "respond_chat",
                "respond_where", "respond_where_far", "respond_where_here", "respond_where_unknown",
                "respond_party_yes", "respond_party_no", "respond_res_no", "respond_wtb_none", "respond_question",
                "respond_name", "respond_what", "buy_notice", "trade_not_buying", "haggle_offer", "haggle_walkaway",
                "res_offer", "guards_call", "red_alert", "token_when_now", "token_when_earlier",
                "respond_repeat", "respond_doing_party", "respond_doing_shop", "respond_doing_reclaim",
                "respond_doing_bank", "respond_doing_hunt", "respond_doing_company", "respond_doing_travel",
                "respond_doing_linger", "respond_doing_idle", "respond_here", "respond_here_unknown",
                "respond_whoami", "respond_news_none", "respond_heal_fine", "respond_heal_yes", "respond_heal_no",
                "respond_follow_yes", "respond_follow_no", "respond_follow_already", "respond_bot", "ack_yes",
                "ack_no", "token_placetype",
            ])
            .Distinct();
        foreach (string lang in new[] { "en", "tr" })
            foreach (string c in categories)
                Assert.True(File.Exists(Path.Combine(dir, "chat", lang, c + ".txt")), $"chat/{lang}/{c}.txt missing");

        var book = BotChatBook.Load(dir, "tr");
        foreach (string lang in new[] { "en", "tr" })
        {
            Assert.NotNull(book.Line("token_dir", 7, lang));
            Assert.Null(book.Line("token_dir", 8, lang));
        }
    }

    [Fact]
    public void ShippedIntentsReadBothLanguages()
    {
        string dir = DataDir();
        var intents = BotIntents.Load(dir);
        Assert.Equal((BotIntentKind.Where, "Bank"), Kind(intents.Parse("where is the bank?")));
        Assert.Equal((BotIntentKind.Where, "Bank"), Kind(intents.Parse("bankaya nasıl gidilir")));
        Assert.Equal((BotIntentKind.Where, "town:Britain"), Kind(intents.Parse("britain nerde")));
        Assert.Equal((BotIntentKind.Where, "dest:Destard L1 Entrance"), Kind(intents.Parse("destard nerede?")));
        Assert.Equal(BotIntentKind.Party, intents.Parse("lfg destard").Kind);
        Assert.Equal(BotIntentKind.Thanks, intents.Parse("sağol kanka").Kind);
        Assert.Equal(BotIntentKind.Greet, intents.Parse("sa").Kind);
        Assert.Equal(BotIntentKind.Doing, intents.Parse("where are you going?").Kind);
        Assert.Equal(BotIntentKind.Doing, intents.Parse("napıyon kanka").Kind);
        Assert.Equal(BotIntentKind.Here, intents.Parse("burası neresi").Kind);
        Assert.Equal(BotIntentKind.Heal, intents.Parse("heal me pls").Kind);
        Assert.Equal((BotIntentKind.Where, "Healer"), Kind(intents.Parse("where is the healer?")));
        Assert.Equal((BotIntentKind.Where, "Bank"), Kind(intents.Parse("banka?")));
        Assert.Equal(BotIntentKind.News, intents.Parse("any news?").Kind);
        Assert.Equal(BotIntentKind.Bot, intents.Parse("bot musun").Kind);
        Assert.Equal(BotIntentKind.No, intents.Parse("i dont want to group").Kind);
        Assert.True(intents.Parse("selam, banka nerede").AlsoGreets);
        Assert.Equal("tr", intents.DetectLanguage("bu ne abi"));
        Assert.Equal("en", intents.DetectLanguage("anyone want to hunt"));

        static (BotIntentKind, string?) Kind(BotIntent i) => (i.Kind, i.PlaceType);
    }
}
