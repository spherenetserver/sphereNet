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
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Presence and manners: a bot turns to whoever speaks to it (and the viewers
/// are told), bows on a greeting, does what people do at a bank, an inn, a smith or a
/// training dummy instead of shuffling, thanks a player who heals or blesses it, objects
/// to a thief, has a word for a player while it fights, walks to a healer as a ghost and
/// is raised there, gets no mount or kit out of nothing where a player looks, strolls in
/// town and runs on the open road, and does not repeat what another bot just said.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotMannersTests
{
    // Road W (60,100) - A (100,100) - B (110,100) - C (120,100).
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "W", "X": 60, "Y": 100, "Z": 0, "Connects": [ "A" ] },
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string Bank = """
        { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
          "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] }
        """;

    private const string Inn = """
        { "Name": "Town Inn", "X": 60, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town",
          "NearestWaypoint": "W", "Arrivals": [ { "X": 61, "Y": 101, "Z": 0 } ] }
        """;

    private const string Smith = """
        { "Name": "Town Smith", "X": 121, "Y": 100, "Z": 0, "Type": "VendorSmith", "City": "Town",
          "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] }
        """;

    private const string Healer = """
        { "Name": "Town Healer", "X": 121, "Y": 100, "Z": 0, "Type": "Healer", "City": "Town",
          "NearestWaypoint": "C", "Arrivals": [ { "X": 123, "Y": 100, "Z": 0 } ] }
        """;

    private static readonly string[] IntentLines =
    [
        "greet: hi hello selam",
        "thanks: ty thanks",
        "bye: bye cya",
        "yes: yes",
        "no: no",
        "not: don+t not",
    ];

    private static Dictionary<string, string[]> Lines() => new()
    {
        ["respond_greet"] = ["hi there"],
        ["respond_bye"] = ["bye"],
        ["respond_chat"] = ["yeah"],
        ["respond_name"] = ["yes?"],
        ["respond_what"] = ["what?"],
        ["thanks_heal"] = ["thanks for the heal"],
        ["thanks_cure"] = ["thanks for the cure"],
        ["thanks_buff"] = ["thanks for the buff"],
        ["thanks_help"] = ["thanks"],
        ["theft_steal"] = ["thief!"],
        ["theft_snoop"] = ["hands off"],
        ["theft_guards"] = ["GUARDS"],
        ["fight_busy"] = ["busy"],
        ["fight_help"] = ["help me"],
        ["fight_watch"] = ["a fight"],
        ["fight_scared"] = ["not near me"],
        ["bank_open"] = ["bank"],
        ["bye_leaving"] = ["ok bye then"],
        ["crowded"] = ["a little room"],
        ["followed"] = ["are you following me?"],
        ["gear_up"] = ["dressed again"],
        ["emote_eat"] = ["*eats*"],
        ["emote_drink"] = ["*drinks*"],
    };

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
        public List<(Character Who, string Category, string Line)> Said { get; } = [];
        /// <summary>Animation packets (0x6E) a bot's client sent the viewers.</summary>
        public List<Character> Animations { get; } = [];
        /// <summary>Speech a bot's client sent the viewers: who, the talk mode.</summary>
        public List<(Character Who, byte Mode)> Speech { get; } = [];
        /// <summary>Steps a bot's client sent the viewers: who, run or walk, where it stood.</summary>
        public List<(Character Who, bool Run, Point3D At)> Steps { get; } = [];
        /// <summary>Characters the engine's facing update told the viewers about.</summary>
        public List<Character> Turned { get; } = [];
        public long Now { get; set; } = 1_000_000;

        public void Run(int ms, Func<bool>? done = null, Action? each = null)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = 2 };
            for (int t = 0; t < ms && (done == null || !done()); t += 100)
            {
                Engine.RunSearches(po);
                Engine.Tick(Now);
                each?.Invoke();
                Now += 100;
            }
            Assert.Empty(Log.Warnings);
        }

        public bool SaidCategory(Character who, string category) => Said.Any(s => s.Who == who && s.Category == category);

        public string Trace(Character bot) =>
            string.Join(" | ", Said.Select(s => $"{s.Who.Name} {s.Category}:{s.Line}")) + Environment.NewLine +
            string.Join(Environment.NewLine, Engine.TraceOf(bot.Uid.Value.ToString()));
    }

    private static Rig MakeRig(string[] places, bool guarded = false, SpellEngine? spells = null,
        ResourceHolder? resources = null, string[]? scenes = null, Dictionary<string, string[]>? lines = null,
        string? waypoints = null, int width = 512, int height = 512)
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, width, height, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, width, height);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        if (guarded)
        {
            var town = new Region { Name = "Town", Flags = RegionFlag.Guarded, MapIndex = 0 };
            town.AddRect(105, 80, 140, 120);
            world.AddRegion(town);
        }
        var nav = new BotNavGraph();
        nav.LoadWaypoints(waypoints ?? WaypointsJson, world.MapData);
        nav.LoadDestinations("{ \"Destinations\": [" + string.Join(",", places) + "] }", world.MapData);
        var log = new ListLogger();
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources, nav,
            BotChatBook.FromLines("en", lines ?? Lines()), log, seed: 11)
        {
            TargetCount = 1000,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            Gravity = 0,
            Intents = BotIntents.Parse(IntentLines),
            Scenes = BotSceneBook.Parse(scenes ?? []),
            Spells = spells,
            // One pastime per test: no "just stands there" drawn by chance.
            IdleStandWeight = 0,
            GlancePercent = 0,
        };
        engine.SetClock(1_000_000); // the rig's clock, not the machine's uptime
        var rig = new Rig { World = world, Engine = engine, Log = log };
        engine.OnLine = (ch, category, line) => rig.Said.Add((ch, category, line));
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), spellEngine: spells);
            c.AttachServerDriven(ch);
            c.BroadcastNearby = (_, _, packet, _) =>
            {
                var bytes = packet.Build().Span.ToArray();
                if (bytes[0] == 0x6E) rig.Animations.Add(ch);
                else if (bytes[0] is 0xAE or 0x1C) rig.Speech.Add((ch, bytes[9]));
            };
            c.BroadcastMoveNearby = (_, _, packet, _, _) =>
                rig.Steps.Add((ch, (packet.Build().Span[12] & 0x80) != 0, ch.Position));
            return c;
        };
        Character.OnFacingChanged = ch => rig.Turned.Add(ch);
        return rig;
    }

    private static Character Bot(Rig rig, string name, Point3D at, string? place = "Town Bank",
        string arch = "c_playerbot_townsfolk_m", Action<Character>? setup = null)
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.ArchetypeTag, arch);
        ch.SetTag(PlayerBotEngine.TraitTag, "chatty");
        setup?.Invoke(ch);
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        var mind = rig.Engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        mind.At = place == null ? null : rig.Engine.Nav.FindDestination(place);
        // A test bot wears nothing: it is not sent off to dress unless the test is about that.
        mind.NextGearCheckMs = long.MaxValue;
        return ch;
    }

    private static Character Player(Rig rig, string name, Point3D at)
    {
        var p = rig.World.CreateCharacter();
        p.IsPlayer = true;
        p.IsOnline = true;
        p.Name = name;
        p.Str = 60; p.MaxHits = 60; p.Hits = 60;
        rig.World.PlaceCharacter(p, at);
        rig.World.AddOnlinePlayer(p);
        return p;
    }

    private static Character Npc(Rig rig, string name, Point3D at, NpcBrainType brain = NpcBrainType.None)
    {
        var c = rig.World.CreateCharacter();
        c.Name = name;
        c.BodyId = 0x0190;
        c.NpcBrain = brain;
        c.MaxHits = 50; c.Hits = 50;
        rig.World.PlaceCharacter(c, at);
        return c;
    }

    private static Item GroundItem(Rig rig, ItemType type, ushort art, Point3D at)
    {
        var item = rig.World.CreateItem();
        item.BaseId = art;
        item.ItemType = type;
        rig.World.PlaceItem(item, at);
        return item;
    }

    private static Item Pack(Rig rig, Character ch)
    {
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    private static void Kill(Character ch)
    {
        ch.Hits = 0;
        ch.SetStatFlag(StatFlag.Dead);
    }

    private static int Apart(Point3D a, Point3D b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // ------------------------------------------------------------------
    //  Facing and body language
    // ------------------------------------------------------------------

    [Fact]
    public void ABotSpokenToTurnsToTheSpeakerAndTheViewersAreTold()
    {
        var r = MakeRig([Bank]);
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        bot.Direction = Direction.North;
        var player = Player(r, "Hero", new Point3D(125, 101, 0, 0));
        r.Run(300);
        r.Turned.Clear();

        r.Engine.OnHear(player, "wanderer hello");

        // At once, before the reply is typed.
        Assert.Equal(Direction.East, bot.Direction);
        Assert.Contains(bot, r.Turned);
        // And it goes on facing the player it talks with, wherever they walk to.
        r.World.MoveCharacter(player, new Point3D(122, 104, 0, 0));
        r.Run(6000, () => bot.Direction == Direction.South);
        Assert.True(bot.Direction == Direction.South, r.Trace(bot));
    }

    [Fact]
    public void ABotOnTheRoadStopsAMomentForThePlayerWhoSpeaksToIt()
    {
        var r = MakeRig([Bank, Inn]);
        var bot = Bot(r, "Wanderer", new Point3D(100, 100, 0, 0), place: null);
        var mind = r.Engine.MindOf(bot)!;
        mind.Goal = r.Engine.Nav.FindDestination("Town Bank");
        mind.Mode = BotMode.Travel;
        var player = Player(r, "Hero", new Point3D(104, 103, 0, 0));
        r.Run(1500);
        var before = bot.Position;
        Assert.NotEqual(new Point3D(100, 100, 0, 0), before);

        r.Engine.OnHear(player, "wanderer hello");
        var stopped = bot.Position;
        r.Run(1200);

        Assert.Equal(stopped, bot.Position);
        Assert.Equal(bot.Position.GetDirectionTo(player.Position), bot.Direction);
        r.Run(6000);
        Assert.NotEqual(stopped, bot.Position);
    }

    [Fact]
    public void AGreetingComesWithABowTheViewersSee()
    {
        var r = MakeRig([Bank]);
        r.Engine.GesturePercent = 100;
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        var player = Player(r, "Hero", new Point3D(124, 101, 0, 0));
        r.Run(300);

        r.Engine.OnHear(player, "wanderer hello");
        r.Run(6000, () => r.Said.Count > 0 && r.Animations.Count > 0);

        Assert.True(r.Said.Any(s => s.Who == bot && s.Line == "hi there"), r.Trace(bot));
        // The client's animate request (0x12 type 0xC7 "bow") reached the viewers as 0x6E.
        Assert.Contains(bot, r.Animations);
        Assert.Equal(1, r.Engine.WindowGestures);
    }

    [Fact]
    public void AGestureWrittenOutIsSaidAsAnEmote()
    {
        var lines = Lines();
        lines["small_talk"] = ["*yawns*"];
        var r = MakeRig([Bank], scenes: ["small 10 : A:small_talk"], lines: lines);
        r.Engine.ChatPaceSeconds = 4;
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        Player(r, "Hero", new Point3D(124, 101, 0, 0));

        r.Run(60_000, () => r.Speech.Count > 0);

        Assert.Contains(r.Speech, s => s.Who == bot && s.Mode == 2);
        Assert.DoesNotContain(r.Speech, s => s.Mode != 2);
    }

    // ------------------------------------------------------------------
    //  Standing about with a purpose
    // ------------------------------------------------------------------

    [Fact]
    public void ABotAtABankStandsByTheBankerWithItsBoxOpenedTheWayPlayersOpenIt()
    {
        var r = MakeRig([Bank]);
        var banker = Npc(r, "the banker", new Point3D(129, 101, 0, 0), NpcBrainType.Banker);
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        Player(r, "Hero", new Point3D(124, 104, 0, 0));

        r.Run(40_000, () => r.SaidCategory(bot, "bank_open"));

        Assert.True(r.SaidCategory(bot, "bank_open"), r.Trace(bot));
        Assert.Contains(r.Said, s => s.Who == bot && s.Line == "bank");
        Assert.True(Apart(bot.Position, banker.Position) <= 4, $"bot at {bot.Position}");
        Assert.Equal(bot.Position.GetDirectionTo(banker.Position), bot.Direction);
        // Then it stays by it: no shuffling about.
        var stood = bot.Position;
        var seen = new HashSet<Point3D>();
        r.Run(40_000, each: () => seen.Add(bot.Position));
        Assert.Equal([stood], seen);
        Assert.Equal(1, r.Said.Count(s => s.Category == "bank_open"));
    }

    [Fact]
    public void ABotAtAnInnSitsOnAFreeChairAndHasABite()
    {
        var r = MakeRig([Inn]);
        // Two chairs: the nearer one is taken.
        GroundItem(r, ItemType.Normal, 0x0B4E, new Point3D(62, 102, 0, 0));
        Npc(r, "a patron", new Point3D(62, 102, 0, 0));
        var chair = GroundItem(r, ItemType.Normal, 0x0B4E, new Point3D(64, 103, 0, 0));
        var bot = Bot(r, "Wanderer", new Point3D(61, 101, 0, 0), place: "Town Inn");
        bot.Food = 0;
        var pack = Pack(r, bot);
        var bread = r.World.CreateItem();
        bread.BaseId = 0x103B;
        bread.ItemType = ItemType.Food;
        bread.Amount = 3;
        Assert.True(pack.TryAddItem(bread));
        Player(r, "Hero", new Point3D(66, 100, 0, 0));

        r.Run(40_000, () => r.Engine.WindowSeats > 0);

        // Standing on the chair's tile is what shows the sitting pose.
        Assert.True(bot.X == chair.X && bot.Y == chair.Y, $"bot at {bot.Position}{Environment.NewLine}{r.Trace(bot)}");
        Assert.Equal(chair.Z, bot.Z);
        Assert.Equal(1, r.Engine.WindowSeats);
        // It stays seated, and eats out of its pack with a double click.
        r.Run(60_000, () => r.Engine.WindowMeals > 0);
        Assert.True(bot.X == chair.X && bot.Y == chair.Y, $"left its seat for {bot.Position}");
        Assert.Equal(1, r.Engine.WindowMeals);
        Assert.True(bread.IsDeleted || bread.Amount < 3, $"bread amount {bread.Amount}");
    }

    [Theory]
    [InlineData(ItemType.Anvil, (ushort)0x0FAF)]
    [InlineData(ItemType.Forge, (ushort)0x0FB1)]
    [InlineData(ItemType.BBoard, (ushort)0x1E5E)]
    public void ABotAtASmithWatchesTheWorkAndReadsTheBoard(ItemType type, ushort art)
    {
        var r = MakeRig([Smith]);
        var thing = GroundItem(r, type, art, new Point3D(127, 103, 0, 0));
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0), place: "Town Smith");
        Player(r, "Hero", new Point3D(124, 104, 0, 0));

        r.Run(30_000, () => Apart(bot.Position, thing.Position) <= 2 &&
                            bot.Direction == bot.Position.GetDirectionTo(thing.Position));

        Assert.True(Apart(bot.Position, thing.Position) <= 2, $"bot at {bot.Position}{Environment.NewLine}{r.Trace(bot)}");
        Assert.Equal(bot.Position.GetDirectionTo(thing.Position), bot.Direction);
        Assert.Contains(bot, r.Turned);
    }

    [Fact]
    public void ABotAtAShopLooksOverTheVendorsWares()
    {
        var r = MakeRig([Smith]);
        var smith = Npc(r, "the smith", new Point3D(128, 100, 0, 0), NpcBrainType.Vendor);
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0), place: "Town Smith");
        Player(r, "Hero", new Point3D(124, 104, 0, 0));

        r.Run(30_000, () => Apart(bot.Position, smith.Position) <= 4 &&
                            bot.Direction == bot.Position.GetDirectionTo(smith.Position));

        Assert.True(Apart(bot.Position, smith.Position) <= 4, r.Trace(bot));
        Assert.Equal(bot.Position.GetDirectionTo(smith.Position), bot.Direction);
    }

    [Fact]
    public void AGreenFighterPractisesOnATrainingDummy()
    {
        var r = MakeRig([Smith]);
        var dummy = GroundItem(r, ItemType.TrainDummy, 0x1070, new Point3D(127, 102, 0, 0));
        var bot = Bot(r, "Recruit", new Point3D(122, 101, 0, 0), place: "Town Smith", arch: "c_playerbot_warrior_m");
        Player(r, "Hero", new Point3D(124, 104, 0, 0));

        r.Run(40_000, () => r.Engine.WindowPractice >= 2);

        Assert.True(r.Engine.WindowPractice >= 2, r.Trace(bot));
        Assert.True(Apart(bot.Position, dummy.Position) <= 1, $"bot at {bot.Position}");
        // The double click went through the item-use handler: the swing the viewers see.
        Assert.Contains(bot, r.Animations);
    }

    [Fact]
    public void AVeteranDoesNotPractiseAndAnUnseenBotOnlyShuffles()
    {
        var r = MakeRig([Smith]);
        GroundItem(r, ItemType.TrainDummy, 0x1070, new Point3D(127, 102, 0, 0));
        var veteran = Bot(r, "Veteran", new Point3D(122, 101, 0, 0), place: "Town Smith", arch: "c_playerbot_warrior_m",
            setup: ch => ch.SetSkill(SkillType.Wrestling, 900));
        // Nobody looks: nothing of the place is looked up, nothing is practised.
        r.Run(30_000);
        Assert.Equal(0, r.Engine.WindowPoiScans);
        Assert.True(Apart(veteran.Position, new Point3D(122, 101, 0, 0)) <= 3);

        Player(r, "Hero", new Point3D(124, 104, 0, 0));
        r.Run(30_000);
        // One look at the place serves the whole stay.
        Assert.Equal(1, r.Engine.WindowPoiScans);
        Assert.Equal(0, r.Engine.WindowPractice);
    }

    // ------------------------------------------------------------------
    //  Reactions
    // ------------------------------------------------------------------

    private static SpellEngine Spells(GameWorld world)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.GreaterHeal, Name = "Greater Heal", Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 11, CastTimeBase = 1, EffectBase = 10, EffectScale = 10,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.Bless, Name = "Bless", Flags = SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless,
            ManaCost = 9, CastTimeBase = 1, EffectBase = 10, EffectScale = 10, DurationBase = 600, DurationScale = 600,
        });
        return new SpellEngine(world, registry);
    }

    [Fact]
    public void ABotThanksThePlayerWhoHealsOrBlessesItAndRemembersTheFavour()
    {
        var r = MakeRig([Bank]);
        var spells = Spells(r.World);
        // The host's wiring: the engine events reach the bots through the static hooks.
        SpellEngine.OnHelpfulEffect = r.Engine.OnHelpfulSpell;
        ActiveSkillEngine.OnBandageHealed = r.Engine.OnBandaged;
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        bot.Hits = 20;
        bot.Direction = Direction.North;
        var player = Player(r, "Hero", new Point3D(125, 101, 0, 0));
        var mind = r.Engine.MindOf(bot)!;
        r.Run(300);
        Assert.Equal(0, r.Engine.BondScore(mind, player));

        Assert.True(spells.ApplyScriptSpellEffect(player, bot, SpellType.GreaterHeal, 800));
        r.Run(5000, () => r.SaidCategory(bot, "thanks_heal"));

        Assert.True(r.SaidCategory(bot, "thanks_heal"), r.Trace(bot));
        Assert.Equal(Direction.East, bot.Direction);
        Assert.True(r.Engine.BondScore(mind, player) > 0);
        Assert.True(bot.TryGetTag(PlayerBotEngine.MemoryTag, out string? memory) && memory!.Contains("|helped|"), memory);
        Assert.Equal(1, r.Engine.WindowThanks);

        // A blessing a while later is thanked for too; a bandage as well.
        r.Run(25_000);
        Assert.True(spells.ApplyScriptSpellEffect(player, bot, SpellType.Bless, 800));
        r.Run(5000, () => r.SaidCategory(bot, "thanks_buff"));
        Assert.True(r.SaidCategory(bot, "thanks_buff"), r.Trace(bot));
        r.Run(25_000);
        ActiveSkillEngine.OnBandageHealed!(player, bot, true);
        r.Run(5000, () => r.SaidCategory(bot, "thanks_cure"));
        Assert.True(r.SaidCategory(bot, "thanks_cure"), r.Trace(bot));

        // Its own spell on itself is nobody's favour.
        int thanks = r.Said.Count;
        r.Run(25_000);
        spells.ApplyScriptSpellEffect(bot, bot, SpellType.GreaterHeal, 800);
        r.Run(5000);
        Assert.Equal(thanks, r.Said.Count);
    }

    [Fact]
    public void ABotRobbedInTownObjectsCallsTheGuardsAndHoldsItAgainstThePlayer()
    {
        var r = MakeRig([Bank], guarded: true);
        ActiveSkillEngine.OnTheftAttempt = r.Engine.OnTheft;
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        var thief = Player(r, "Sly", new Point3D(123, 101, 0, 0));
        var mind = r.Engine.MindOf(bot)!;
        r.Run(300);

        // A clean theft nobody feels (the chance is the engine's dice: none here).
        r.Engine.TheftNoticePercent = 0;
        ActiveSkillEngine.OnTheftAttempt!(thief, bot, null, true, false);
        r.Run(3000);
        Assert.Empty(r.Said);
        Assert.Equal(0, r.Engine.BondScore(mind, thief));

        // A fumbled one is felt.
        ActiveSkillEngine.OnTheftAttempt!(thief, bot, null, false, false);
        r.Run(4000, () => r.SaidCategory(bot, "theft_guards"));

        Assert.True(r.SaidCategory(bot, "theft_steal"), r.Trace(bot));
        Assert.True(r.SaidCategory(bot, "theft_guards"), r.Trace(bot));
        Assert.True(r.Engine.BondScore(mind, thief) < 0);
        Assert.Equal(bot.Position.GetDirectionTo(thief.Position), bot.Direction);
        Assert.Equal(BotMode.Linger, mind.Mode);
        Assert.Equal(1, r.Engine.WindowTheftReacts);
    }

    [Fact]
    public void ABraveFighterRobbedOutOfTownGoesForAThiefTheLawKnows()
    {
        var r = MakeRig([Bank]);
        r.Engine.BraveNerve = 0;
        ActiveSkillEngine.OnTheftAttempt = r.Engine.OnTheft;
        var bot = Bot(r, "Guardian", new Point3D(122, 101, 0, 0), arch: "c_playerbot_warrior_m");
        var blue = Player(r, "Sly", new Point3D(123, 101, 0, 0));
        var mind = r.Engine.MindOf(bot)!;
        r.Run(300);

        // Somebody still innocent to it is not struck: that would be the bot's crime.
        ActiveSkillEngine.OnTheftAttempt!(blue, bot, null, false, true);
        r.Run(3000);
        Assert.True(r.SaidCategory(bot, "theft_snoop"), r.Trace(bot));
        Assert.NotEqual(BotMode.Fight, mind.Mode);

        // One the witnesses made a criminal is.
        blue.SetStatFlag(StatFlag.Criminal);
        r.Run(15_000);
        ActiveSkillEngine.OnTheftAttempt!(blue, bot, null, false, false);
        r.Run(3000, () => mind.Mode == BotMode.Fight);
        Assert.True(mind.Mode == BotMode.Fight && mind.Foe == blue, r.Trace(bot));
    }

    [Fact]
    public void ABotInAFightHasAWordForThePlayerWhoSpeaksToIt()
    {
        var r = MakeRig([Bank]);
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        var other = Bot(r, "Bystander", new Point3D(120, 103, 0, 0));
        var player = Player(r, "Hero", new Point3D(124, 101, 0, 0));
        var mind = r.Engine.MindOf(bot)!;
        r.Run(300);

        mind.Mode = BotMode.Fight;
        r.Engine.OnHear(player, "wanderer hello");
        r.Run(2000, () => r.Said.Count > 0);
        Assert.True(r.SaidCategory(bot, "fight_busy"), r.Trace(bot));
        Assert.DoesNotContain(r.Said, s => s.Line == "hi there");
        // Nobody else takes the line up for it.
        Assert.DoesNotContain(r.Said, s => s.Who == other);

        // Badly hurt, it calls for help instead.
        r.Run(9000);
        mind.Mode = BotMode.Fight;
        bot.Hits = 10;
        r.Engine.OnHear(player, "wanderer you ok");
        r.Run(2000, () => r.SaidCategory(bot, "fight_help"));
        Assert.True(r.SaidCategory(bot, "fight_help"), r.Trace(bot));
    }

    [Fact]
    public void AFightNearByTurnsAFightersHeadAndSendsTheTownsfolkAFewStepsBack()
    {
        var r = MakeRig([Bank]);
        var fighter = Bot(r, "Guardian", new Point3D(118, 100, 0, 0), arch: "c_playerbot_warrior_m");
        var towner = Bot(r, "Baker", new Point3D(120, 102, 0, 0));
        fighter.Direction = Direction.North;
        var brute = Npc(r, "a brute", new Point3D(124, 100, 0, 0));
        var victim = Npc(r, "a traveller", new Point3D(123, 100, 0, 0));
        Player(r, "Hero", new Point3D(121, 105, 0, 0));
        r.Run(1200);
        Assert.Equal(0, r.Engine.WindowFightReacts);
        int before = Apart(towner.Position, victim.Position);

        victim.RecordAttack(brute.Uid, 5);
        r.Run(6000, each: () => victim.RecordAttack(brute.Uid, 1));

        Assert.Equal(2, r.Engine.WindowFightReacts);
        Assert.Equal(fighter.Position.GetDirectionTo(victim.Position), fighter.Direction);
        Assert.Equal(new Point3D(118, 100, 0, 0), fighter.Position);
        Assert.True(Apart(towner.Position, victim.Position) >= before + 3,
            $"the townsman stands {Apart(towner.Position, victim.Position)} from the fight (was {before}){Environment.NewLine}{r.Trace(towner)}");
        // It looks back at what it walked away from.
        Assert.Equal(towner.Position.GetDirectionTo(victim.Position), towner.Direction);
    }

    [Fact]
    public void APlayerWalkingOffMidTalkGetsAGoodbyeAndOneStandingOnTheBotIsToldSo()
    {
        var r = MakeRig([Bank]);
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0));
        var player = Player(r, "Hero", new Point3D(124, 101, 0, 0));
        r.Run(300);
        r.Engine.OnHear(player, "wanderer hello");
        r.Run(5000, () => r.Said.Count > 0);
        Assert.Contains(r.Said, s => s.Who == bot && s.Line == "hi there");

        r.World.MoveCharacter(player, new Point3D(133, 101, 0, 0));
        r.Run(3000, () => r.SaidCategory(bot, "bye_leaving"));
        Assert.True(r.SaidCategory(bot, "bye_leaving"), r.Trace(bot));

        // Stood on for a while: a word, and a step aside.
        r.World.MoveCharacter(player, bot.Position);
        var stood = bot.Position;
        r.Run(15_000, () => r.SaidCategory(bot, "crowded") && bot.Position != stood);
        Assert.True(r.SaidCategory(bot, "crowded"), r.Trace(bot));
        Assert.NotEqual(stood, bot.Position);
    }

    [Fact]
    public void APlayerWalkingAfterABotForLongIsAskedAboutIt()
    {
        var r = MakeRig([Bank, Inn]);
        var bot = Bot(r, "Wanderer", new Point3D(61, 101, 0, 0), place: "Town Inn");
        var mind = r.Engine.MindOf(bot)!;
        mind.Goal = r.Engine.Nav.FindDestination("Town Bank");
        mind.Mode = BotMode.Travel;
        var player = Player(r, "Hero", new Point3D(60, 102, 0, 0));

        r.Run(60_000, () => r.SaidCategory(bot, "followed"), each: () =>
        {
            mind.Running = false;
            r.World.MoveCharacter(player, new Point3D((short)(bot.X - 2), (short)(bot.Y + 1), 0, 0));
        });

        Assert.True(r.SaidCategory(bot, "followed"), r.Trace(bot));
        // It stopped and turned to ask.
        Assert.Equal(BotMode.Travel, mind.Mode);
        Assert.Equal(bot.Position.GetDirectionTo(player.Position), bot.Direction);
        var asked = bot.Position;
        r.Run(1500);
        Assert.Equal(asked, bot.Position);
    }

    // ------------------------------------------------------------------
    //  Death
    // ------------------------------------------------------------------

    private static Item CorpseOf(Rig rig, Character bot)
    {
        var corpse = rig.World.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", bot.Uid.Value.ToString());
        rig.World.PlaceItem(corpse, bot.Position);
        var gem = rig.World.CreateItem();
        gem.BaseId = 0x0F0F;
        Assert.True(corpse.TryAddItem(gem));
        return corpse;
    }

    [Fact]
    public void AGhostWalksTheRoadToTheHealerIsRaisedAtTheAnkhAndWalksBackForItsBody()
    {
        var r = MakeRig([Healer]);
        var ankh = GroundItem(r, ItemType.Shrine, 0x0004, new Point3D(125, 100, 0, 0));
        var bot = Bot(r, "Wanderer", new Point3D(60, 100, 0, 0), place: null);
        Pack(r, bot);
        var mind = r.Engine.MindOf(bot)!;
        // A player watches the whole way: nothing may happen behind a curtain.
        var player = Player(r, "Hero", new Point3D(62, 102, 0, 0));
        var corpse = CorpseOf(r, bot);
        Kill(bot);
        r.Run(200);
        Assert.True(bot.IsDead);
        mind.GhostUntilMs = r.Now;       // done hovering over the body

        int farthestStep = 0;
        var last = bot.Position;
        r.Run(120_000, () => !bot.IsDead, each: () =>
        {
            farthestStep = Math.Max(farthestStep, Apart(last, bot.Position));
            last = bot.Position;
            r.World.MoveCharacter(player, new Point3D((short)(bot.X + 2), (short)(bot.Y + 4), 0, 0));
        });

        Assert.False(bot.IsDead, r.Trace(bot));
        Assert.True(farthestStep <= 1, $"the ghost jumped {farthestStep} tiles");
        Assert.True(Apart(bot.Position, ankh.Position) <= 2, $"raised at {bot.Position}");
        Assert.Equal(1, r.Engine.WindowGhostWalks);
        Assert.Equal(0, r.Engine.StuckRescues);
        // Alive again: it sets off for its body, 65 tiles back along the road, on foot.
        r.Run(2000);
        Assert.Equal(1, r.Engine.WindowGhostRaises);
        Assert.False(corpse.IsDeleted);
        Assert.Equal(0, r.Engine.WindowHandBacks);
        Assert.True(mind.Mode is BotMode.Travel or BotMode.Reclaim, $"mode {mind.Mode}{Environment.NewLine}{r.Trace(bot)}");
        Assert.Equal("Corpse", mind.Goal?.Type);
    }

    [Fact]
    public void AGhostStandsByTheHealerUntilItIsRaised()
    {
        var r = MakeRig([Healer]);
        var healer = Npc(r, "the healer", new Point3D(127, 100, 0, 0), NpcBrainType.Healer);
        var bot = Bot(r, "Wanderer", new Point3D(100, 100, 0, 0), place: null);
        var mind = r.Engine.MindOf(bot)!;
        Player(r, "Hero", new Point3D(120, 104, 0, 0));
        Kill(bot);
        r.Run(200);
        mind.GhostUntilMs = r.Now;

        r.Run(60_000, () => Apart(bot.Position, healer.Position) <= 2);

        // Within the three tiles at which a healer raises a ghost, and still a ghost:
        // the raising is the healer's to do.
        Assert.True(Apart(bot.Position, healer.Position) <= 2, $"ghost at {bot.Position}{Environment.NewLine}{r.Trace(bot)}");
        Assert.True(bot.IsDead);
        Assert.Equal(BotGhostStage.AtHealer, mind.Manners.Ghost);
        r.Run(5000);
        Assert.True(bot.IsDead);
        // The healer's own look at the ghost that came to it (the town-role AI, wired as
        // the host wires it: the look decides, the resurrection follows).
        var ai = new SphereNet.Game.AI.NpcAI(r.World, new SphereNet.Core.Configuration.SphereConfig());
        ai.OnHealerAction = (_, ghost, raise) => { if (raise && ghost == bot) mind.Client!.OnResurrect(); };
        Assert.True(ai.LookAtChar(healer, bot));
        r.Run(1000);
        Assert.False(bot.IsDead);
        Assert.Equal(1, r.Engine.WindowGhostRaises);
        Assert.Equal(BotGhostStage.None, mind.Manners.Ghost);
    }

    [Fact]
    public void AGhostWithNoRoadIsCarriedOffOnlyOnceNoPlayerLooks()
    {
        var r = MakeRig([Healer]);
        r.Engine.UnseenLiftWaitMs = 600_000;
        var bot = Bot(r, "Wanderer", new Point3D(300, 300, 0, 0), place: null);
        var mind = r.Engine.MindOf(bot)!;
        var player = Player(r, "Hero", new Point3D(303, 302, 0, 0));
        Kill(bot);
        r.Run(200);
        mind.GhostUntilMs = r.Now;

        r.Run(30_000);
        // No road within reach: the old lift - but not in front of a player.
        Assert.True(bot.IsDead, r.Trace(bot));
        Assert.True(Apart(bot.Position, new Point3D(300, 300, 0, 0)) <= 6);
        Assert.Equal(0, r.Engine.WindowGhostWalks);

        r.World.MoveCharacter(player, new Point3D(400, 400, 0, 0));
        r.Run(10_000, () => !bot.IsDead);
        Assert.False(bot.IsDead, r.Trace(bot));
        Assert.Equal(new Point3D(123, 100, 0, 0), bot.Position);
    }

    // A cave under a town. Above: road A (1000,1000) - B (1010,1000) - Mouth (1018,1000), the
    // town's healer by A. Three teleporter tiles at (1020..1022,1000) carry a walker down to
    // (5300..5302,1000), where the dungeon coordinates are; the way back up is two tiles south
    // of the landing. Another town's healer stands at (2700,2100), with no road to it: by raw
    // map coordinates it is the one nearest the cave.
    private const string CaveRoads = """
        { "Waypoints": [
          { "Name": "A", "X": 1000, "Y": 1000, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 1010, "Y": 1000, "Z": 0, "Connects": [ "Mouth" ] },
          { "Name": "Mouth", "X": 1018, "Y": 1000, "Z": 0 },
          { "Name": "Far", "X": 2700, "Y": 2098, "Z": 0 },
          { "Name": "Inside", "X": 5301, "Y": 996, "Z": 0, "Connects": [ "Deep" ] },
          { "Name": "Deep", "X": 5301, "Y": 980, "Z": 0 }
        ] }
        """;

    private static readonly string[] CavePlaces =
    [
        """
        { "Name": "Cave Town Healer", "X": 1000, "Y": 1002, "Z": 0, "Type": "Healer", "City": "Cave Town",
          "NearestWaypoint": "A", "Arrivals": [ { "X": 1000, "Y": 1003, "Z": 0, "Waypoints": [ "A" ] } ] }
        """,
        """
        { "Name": "Far Town Healer", "X": 2700, "Y": 2100, "Z": 0, "Type": "Healer", "City": "Far Town",
          "NearestWaypoint": "Far", "Arrivals": [ { "X": 2700, "Y": 2101, "Z": 0, "Waypoints": [ "Far" ] } ] }
        """,
        """
        { "Name": "Cave Entrance", "X": 1021, "Y": 1000, "Z": 0, "Type": "DungeonEntrance", "City": "",
          "NearestWaypoint": "Mouth", "Arrivals": [ { "X": 1021, "Y": 1000, "Z": 0, "Waypoints": [ "Mouth" ] } ],
          "TargetX": 5301, "TargetY": 1000, "TargetZ": 0 }
        """,
        """
        { "Name": "Cave Way Out", "X": 5301, "Y": 1002, "Z": 0, "Type": "DungeonAscend", "City": "",
          "NearestWaypoint": "Inside", "Arrivals": [ { "X": 5301, "Y": 1002, "Z": 0, "Waypoints": [ "Inside" ] } ],
          "TargetX": 1021, "TargetY": 1001, "TargetZ": 0 }
        """,
    ];

    private static Rig CaveRig()
    {
        var r = MakeRig(CavePlaces, waypoints: CaveRoads, width: 6144, height: 4096);
        var list = new List<GameWorld.MapTeleport>();
        for (int i = 0; i < 3; i++)
        {
            list.Add(new GameWorld.MapTeleport(new Point3D((short)(1020 + i), 1000, 0, 0), new Point3D((short)(5300 + i), 1000, 0, 0), false));
            list.Add(new GameWorld.MapTeleport(new Point3D((short)(5300 + i), 1002, 0, 0), new Point3D((short)(1020 + i), 1001, 0, 0), false));
        }
        r.World.SetMapTeleports(list);
        return r;
    }

    [Fact]
    public void ADeathInADungeonIsRaisedByItsOwnMouthAndTheBodyIsFetchedThroughThePortal()
    {
        var r = CaveRig();
        var bot = Bot(r, "Delver", new Point3D(5301, 984, 0, 0), place: null);
        bot.Str = 100;
        var pack = Pack(r, bot);
        var mind = r.Engine.MindOf(bot)!;
        var corpse = CorpseOf(r, bot);
        var gem = corpse.Contents.First();
        Kill(bot);
        r.Run(200);
        mind.GhostUntilMs = r.Now;

        r.Run(20_000, () => !bot.IsDead);

        // Not at the healer nearest by raw coordinates (2600 tiles off, 4300 to this
        // one), but at the one by the mouth the cave is entered from.
        Assert.False(bot.IsDead, r.Trace(bot));
        Assert.True(Apart(bot.Position, new Point3D(1000, 1003, 0, 0)) <= 2, $"raised at {bot.Position}");
        Assert.Equal(0, r.Engine.WindowGhostWalks);

        // And from there the body is in reach again: by road to the mouth, through
        // it, and onto the corpse - nothing handed back.
        r.Run(180_000, () => r.Engine.WindowReclaims > 0);
        Assert.True(r.Engine.WindowReclaims == 1, r.Trace(bot));
        Assert.Equal(1, r.Engine.WindowCorpseRuns);
        Assert.Equal(0, r.Engine.WindowHandBacks);
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.True(PlayerBotEngine.IsUnderground(bot.Position), $"bot at {bot.Position}");
        Assert.True(r.Steps.Where(s => s.Who == bot).All(s => s.Run), "it walked for its body");
    }

    [Fact]
    public void ABodyNoRoadLeadsBackToInTimeIsStillHandedBack()
    {
        var r = CaveRig();
        r.Engine.CorpseRunRange = 10;
        var bot = Bot(r, "Delver", new Point3D(5301, 984, 0, 0), place: null);
        var pack = Pack(r, bot);
        var mind = r.Engine.MindOf(bot)!;
        var corpse = CorpseOf(r, bot);
        var gem = corpse.Contents.First();
        Kill(bot);
        r.Run(200);
        mind.GhostUntilMs = r.Now;

        r.Run(20_000, () => !bot.IsDead);

        Assert.False(bot.IsDead);
        Assert.Equal(1, r.Engine.WindowHandBacks);
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.True(corpse.IsDeleted);
    }

    [Fact]
    public void AGhostTakenBackFromTheSaveGoesOnWithItsDeathInsteadOfDyingAgain()
    {
        var r = MakeRig([Healer]);
        r.Engine.UnseenLiftWaitMs = 600_000;
        var bot = Bot(r, "Wanderer", new Point3D(300, 300, 0, 0), place: null);
        // A player looks on, so the ghost stays a ghost while it is looked at.
        var player = Player(r, "Hero", new Point3D(303, 302, 0, 0));
        var corpse = CorpseOf(r, bot);
        Kill(bot);
        r.Run(300);
        Assert.Equal(1, r.Engine.WindowDeaths);
        Assert.True(bot.TryGetTag(PlayerBotEngine.DeadTag, out string? note), "no death noted on the character");
        long hover = r.Engine.MindOf(bot)!.GhostUntilMs - r.Now;
        Assert.InRange(hover, 25_000, 60_000);

        // The server goes down and comes back twenty seconds later (the note keeps wall time).
        var parts = note!.Split('|');
        parts[1] = (long.Parse(parts[1]) - 20).ToString();
        bot.SetTag(PlayerBotEngine.DeadTag, string.Join('|', parts));
        r.Engine.Release(bot);
        Assert.Equal(1, r.Engine.AdoptSaved());
        var mind = r.Engine.MindOf(bot)!;
        r.Run(300);

        Assert.True(bot.IsDead);
        Assert.Equal(1, r.Engine.WindowDeaths);            // no second death
        Assert.Equal(1, r.Engine.WindowDeathsResumed);
        Assert.Equal(corpse.Uid, mind.CorpseUid);
        // Twenty seconds of its hovering are behind it.
        Assert.InRange(mind.GhostUntilMs - r.Now, hover - 21_500, hover - 19_000);
        Assert.InRange(r.Now - mind.DeadSinceMs, 19_000, 21_500);

        // Raised (carried off once nobody looks), the note is gone; the next death is a death.
        r.World.MoveCharacter(player, new Point3D(450, 450, 0, 0));
        r.Run(90_000, () => !bot.IsDead);
        Assert.False(bot.IsDead, r.Trace(bot));
        Assert.False(bot.TryGetTag(PlayerBotEngine.DeadTag, out _));
        Kill(bot);
        CorpseOf(r, bot);
        r.Run(8000, () => r.Engine.WindowDeaths == 2);
        Assert.Equal(2, r.Engine.WindowDeaths);
        Assert.Equal(1, r.Engine.WindowDeathsResumed);
        Assert.True(bot.TryGetTag(PlayerBotEngine.DeadTag, out _));
    }

    // ------------------------------------------------------------------
    //  Nothing out of nothing where a player looks
    // ------------------------------------------------------------------

    private static ResourceHolder Defs(params (string Name, int Index)[] charDefs)
    {
        var resources = new ResourceHolder(NullLogger<ResourceHolder>.Instance);
        foreach (var (name, index) in charDefs)
        {
            Assert.True(resources.RegisterDefName(name, new ResourceId(ResType.CharDef, index)));
            TestHarness.SeedCharDef(index);
        }
        return resources;
    }

    [Fact]
    public void AMountIsMadeOnlyWhereNoPlayerLooks()
    {
        var r = MakeRig([Bank], guarded: true, resources: Defs(("c_test_horse", 0xC8)));
        r.Engine.AddMount("c_test_horse");
        var bot = Bot(r, "Rider", new Point3D(122, 101, 0, 0), setup: ch => ch.SetTag(PlayerBotEngine.RiderTag, "1"));
        var player = Player(r, "Hero", new Point3D(126, 103, 0, 0));

        r.Run(20_000);
        Assert.Equal(0, r.Engine.WindowMounts);
        Assert.True(r.Engine.WindowConjureWaits > 0);
        Assert.False(bot.TryGetTag(PlayerBotEngine.MountTag, out _));

        r.World.MoveCharacter(player, new Point3D(300, 300, 0, 0));
        r.Run(20_000, () => r.Engine.WindowMounts > 0);
        Assert.Equal(1, r.Engine.WindowMounts);
        Assert.True(bot.TryGetTag(PlayerBotEngine.MountTag, out _));
    }

    [Fact]
    public void AMountLeftBehindIsRemovedOnlyOnceNoPlayerSeesIt()
    {
        var r = MakeRig([Bank], resources: Defs(("c_test_horse", 0xC8)));
        r.Engine.AddMount("c_test_horse");
        var bot = Bot(r, "Rider", new Point3D(122, 101, 0, 0), setup: ch => ch.SetTag(PlayerBotEngine.RiderTag, "1"));
        var horse = Npc(r, "a horse", new Point3D(160, 101, 0, 0), NpcBrainType.Animal);
        Assert.True(horse.TryAssignOwnership(bot));
        bot.SetTag(PlayerBotEngine.MountTag, horse.Uid.Value.ToString());
        // The player stands by the horse, out of the bot's sight.
        var player = Player(r, "Hero", new Point3D(162, 102, 0, 0));

        r.Run(15_000);
        Assert.False(bot.TryGetTag(PlayerBotEngine.MountTag, out _));
        Assert.False(horse.IsDeleted);
        Assert.Equal(1, r.Engine.StrayMounts);

        r.World.MoveCharacter(player, new Point3D(300, 300, 0, 0));
        r.Run(10_000, () => horse.IsDeleted);
        Assert.True(horse.IsDeleted);
        Assert.Equal(0, r.Engine.StrayMounts);
    }

    [Fact]
    public void ABareBotAPlayerLooksAtWalksToTheBankAndDressesOutOfItsBox()
    {
        var r = MakeRig([Bank, Inn], resources: Defs(("c_test_kit", 0x0190)));
        var town = new Region { Name = "Town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(40, 80, 140, 120);
        r.World.AddRegion(town);
        r.World.MapData!.SetSyntheticItemTile(0x1517, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Wearable, Quality = (byte)Layer.Shirt });
        // The kit: a shirt on whoever the archetype's @Create dresses.
        r.Engine.RunCreateScript = ch =>
        {
            var shirt = r.World.CreateItem();
            shirt.BaseId = 0x1517;
            ch.Equip(shirt, Layer.Shirt);
        };
        Npc(r, "the banker", new Point3D(125, 101, 0, 0), NpcBrainType.Banker);
        var bot = Bot(r, "Wanderer", new Point3D(61, 101, 0, 0), place: "Town Inn", arch: "c_test_kit");
        var mind = r.Engine.MindOf(bot)!;
        mind.NextGearCheckMs = 0;
        Player(r, "Hero", new Point3D(64, 103, 0, 0));
        Player(r, "Clerk", new Point3D(124, 104, 0, 0));
        Assert.True(PlayerBotEngine.IsBare(bot));

        // Looked at, away from a bank: no kit; it sets off for the bank.
        r.Run(3000);
        Assert.Equal(0, r.Engine.WindowRegears);
        Assert.Equal(BotMode.Travel, mind.Mode);
        Assert.Equal("Town Bank", mind.Goal?.Name);

        r.Run(180_000, () => r.Engine.WindowRegears > 0);

        Assert.Equal(1, r.Engine.WindowRegears);
        Assert.NotNull(bot.GetEquippedItem(Layer.Shirt));
        Assert.True(Apart(bot.Position, new Point3D(122, 101, 0, 0)) <= 8, $"dressed at {bot.Position}");
        // It opened its box first, as a player dresses out of the bank.
        int opened = r.Said.FindIndex(s => s.Who == bot && s.Category == "bank_open");
        int dressed = r.Said.FindIndex(s => s.Who == bot && s.Category == "gear_up");
        Assert.True(opened >= 0 && dressed > opened, r.Trace(bot));
    }

    [Fact]
    public void ABareBotNobodySeesIsDressedWhereItStands()
    {
        var r = MakeRig([Bank], guarded: true, resources: Defs(("c_test_kit", 0x0190)));
        var bot = Bot(r, "Wanderer", new Point3D(122, 101, 0, 0), arch: "c_test_kit");
        r.Engine.MindOf(bot)!.NextGearCheckMs = 0;

        r.Run(2000);

        Assert.Equal(1, r.Engine.WindowRegears);
        Assert.True(Apart(bot.Position, new Point3D(122, 101, 0, 0)) <= 3, $"dressed at {bot.Position}");
    }

    // ------------------------------------------------------------------
    //  Pace
    // ------------------------------------------------------------------

    [Fact]
    public void ATripAPlayerSeesIsRunOnTheOpenRoadAndWalkedInTown()
    {
        // The town (guarded) starts at x 105; the road comes in from the west.
        var r = MakeRig([Bank, Inn], guarded: true);
        r.Engine.JunctionPausePercent = 0;
        r.Engine.PausePerMille = 0;
        var bot = Bot(r, "Wanderer", new Point3D(61, 101, 0, 0), place: "Town Inn");
        var mind = r.Engine.MindOf(bot)!;
        mind.Goal = r.Engine.Nav.FindDestination("Town Bank");
        mind.Mode = BotMode.Travel;
        var player = Player(r, "Hero", new Point3D(61, 107, 0, 0));

        r.Run(120_000, () => mind.Mode == BotMode.Linger, each: () =>
        {
            mind.Running = false;        // not a trip made in a hurry
            r.World.MoveCharacter(player, new Point3D(bot.X, (short)(bot.Y + 6), 0, 0));
        });

        Assert.Equal(BotMode.Linger, mind.Mode);
        var steps = r.Steps.Where(s => s.Who == bot).ToList();
        var road = steps.Where(s => s.At.X is > 70 and < 100).ToList();
        var town = steps.Where(s => s.At.X >= 118).ToList();
        Assert.True(road.Count > 10 && town.Count > 2, $"{road.Count} road steps, {town.Count} town steps");
        Assert.True(road.Count(s => s.Run) > road.Count * 8 / 10, $"{road.Count(s => s.Run)} of {road.Count} road steps run");
        Assert.DoesNotContain(town, s => s.Run);
    }

    [Fact]
    public void ATripNobodySeesKeepsItsOwnPace()
    {
        var r = MakeRig([Bank, Inn], guarded: true);
        var bot = Bot(r, "Wanderer", new Point3D(100, 100, 0, 0), place: null);
        var mind = r.Engine.MindOf(bot)!;
        mind.Goal = r.Engine.Nav.FindDestination("Town Bank");
        mind.Mode = BotMode.Travel;

        r.Run(60_000, () => mind.Mode == BotMode.Linger, each: () => mind.Running = true);

        var steps = r.Steps.Where(s => s.Who == bot).ToList();
        Assert.True(steps.Count > 10);
        // In a hurry and unseen: it runs through town, as it always did.
        Assert.True(steps.Count(s => s.Run) > steps.Count * 8 / 10);
    }

    [Fact]
    public void BotsLeavingOnePlaceTogetherDoNotStartOnTheSameTick()
    {
        var r = MakeRig([Bank, Inn]);
        var a = Bot(r, "Anna", new Point3D(122, 101, 0, 0));
        var b = Bot(r, "Bruno", new Point3D(123, 102, 0, 0));
        var c = Bot(r, "Cato", new Point3D(121, 102, 0, 0));
        var minds = new[] { a, b, c }.Select(x => r.Engine.MindOf(x)!).ToList();
        r.Engine.CompanyPercent = 0;
        foreach (var m in minds)
        {
            m.LingerUntilMs = r.Now;
            m.NextActMs = r.Now;
        }

        r.Engine.Tick(r.Now);

        Assert.All(minds, m => Assert.Equal(BotMode.Travel, m.Mode));
        var starts = minds.Select(m => m.NextActMs).OrderBy(t => t).ToList();
        Assert.True(starts[1] - starts[0] >= 500 && starts[2] - starts[1] >= 500,
            $"first steps at +{starts[0] - r.Now} +{starts[1] - r.Now} +{starts[2] - r.Now} ms");
    }

    // ------------------------------------------------------------------
    //  Speech hygiene
    // ------------------------------------------------------------------

    [Fact]
    public void TwoBotsByOnePlayerDoNotSayWhatTheOtherJustSaid()
    {
        var lines = Lines();
        lines["small_talk"] = Enumerable.Range(1, 12).Select(i => $"line {i}").ToArray();
        var r = MakeRig([Bank], scenes: ["small 10 : A:small_talk"], lines: lines);
        r.Engine.ChatPaceSeconds = 4;
        var a = Bot(r, "Anna", new Point3D(122, 101, 0, 0));
        var b = Bot(r, "Bruno", new Point3D(124, 102, 0, 0));
        Player(r, "Hero", new Point3D(123, 104, 0, 0));

        r.Run(170_000, () => r.Said.Count >= 11);

        var said = r.Said.Take(11).ToList();
        Assert.Equal(11, said.Count);
        Assert.Contains(said, s => s.Who == a);
        Assert.Contains(said, s => s.Who == b);
        // Twelve lines, eleven said by the two of them within three minutes: no line twice.
        Assert.Equal(said.Count, said.Select(s => s.Line).Distinct().Count());
    }

    [Fact]
    public void ABotRemembersItsLastTwentyLines()
    {
        var r = MakeRig([Bank]);
        var mind = r.Engine.MindOf(Bot(r, "Wanderer", new Point3D(122, 101, 0, 0)))!;
        for (int i = 0; i < 20; i++) mind.NoteSaid($"line {i}");
        Assert.True(mind.SaidLately("line 0"));
        Assert.True(mind.SaidLately("line 19"));
        mind.NoteSaid("line 20");
        Assert.False(mind.SaidLately("line 0"));
    }

    // ------------------------------------------------------------------
    //  The shipped data
    // ------------------------------------------------------------------

    private static string DataDir() => TestRepo.Tracked("playerbots");

    [Fact]
    public void ShippedMannersWordsExistInEveryLanguageAndEmotesAreEmotes()
    {
        string dir = DataDir();
        string[] categories =
        [
            "emotes", "emote_sit", "emote_drink", "emote_eat", "emote_bank", "emote_shop", "emote_smith", "emote_dock",
            "emote_board", "emote_train", "bank_open", "thanks_heal", "thanks_cure", "thanks_buff", "thanks_help",
            "theft_snoop", "theft_steal", "theft_guards", "fight_watch", "fight_scared", "fight_busy", "fight_help",
            "crowded", "followed", "bye_leaving", "memory_helped",
        ];
        foreach (string lang in new[] { "en", "tr" })
            foreach (string c in categories)
                Assert.True(File.Exists(Path.Combine(dir, "chat", lang, c + ".txt")), $"chat/{lang}/{c}.txt missing");
        var book = BotChatBook.Load(dir, "tr");
        foreach (string lang in new[] { "en", "tr" })
        {
            // Every emote line is a gesture in asterisks: nothing typed by mistake is said.
            foreach (string c in categories.Where(c => c.StartsWith("emote", StringComparison.Ordinal)))
                for (int i = 0; book.Line(c, i, lang) is { } raw; i++)
                {
                    string line = BotChatBook.Untag(raw, null)!;
                    Assert.True(line.Length > 2 && line[0] == '*' && line[^1] == '*', $"{lang}/{c}: {raw}");
                }
            // The guard call carries the word the guards hear.
            for (int i = 0; book.Line("theft_guards", i, lang) is { } call; i++)
                Assert.Contains("guards", call, StringComparison.OrdinalIgnoreCase);
            // A bot stays a bot: no line of these claims to be a person.
            foreach (string c in categories)
                for (int i = 0; book.Line(c, i, lang) is { } line; i++)
                    Assert.DoesNotContain("not a bot", line, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("bank", book.Line("bank_open", 0, "en"));
    }
}
