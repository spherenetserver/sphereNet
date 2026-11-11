using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The planner: what a bot does when a stay ends, from what it needs. A poor
/// mage buys reagents (fetching the gold from the bank when the vendor takes only what it
/// carries) before it hunts; a fighter with a full pack sells before it goes out again; a
/// wounded bot rests; a tradesman works and sells once loaded; its nature changes its
/// choice; the plan is kept across a restart, dropped when the bot is carried off, kept
/// against a near rival (hysteresis) but not against a pressing need; and the bot says
/// what its plan is.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotPlannerTests
{
    // A road A (100,100) - B (110,100) - C (120,100) - D (130,100) - E (200,100):
    // the town's bank, mage shop, smith, inn and mine along it, a cave mouth at its end.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ "D" ] },
          { "Name": "D", "X": 130, "Y": 100, "Z": 0, "Connects": [ "E" ] },
          { "Name": "E", "X": 200, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 100, "Y": 96, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 100, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Mage", "X": 110, "Y": 96, "Z": 0, "Type": "VendorMage", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 110, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Smith", "X": 110, "Y": 104, "Z": 0, "Type": "VendorSmith", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 110, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Inn", "X": 120, "Y": 96, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 120, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Mine", "X": 131, "Y": 100, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "D", "Arrivals": [ { "X": 131, "Y": 101, "Z": 0 } ] },
          { "Name": "Cave Mouth", "X": 201, "Y": 100, "Z": 0, "Type": "DungeonEntrance", "City": "",
            "NearestWaypoint": "E", "Arrivals": [ { "X": 201, "Y": 101, "Z": 0 } ] }
        ] }
        """;

    private const string Definitions = """
        [SKILL 45]
        DEFNAME=Skill_Mining
        KEY=Mining
        FLAGS=skf_gather
        RANGE=2

        [ITEMDEF 019b7]
        DEFNAME=i_ore_iron
        NAME=iron ore
        TYPE=t_ore
        VALUE=4
        WEIGHT=2

        [ITEMDEF 0f51]
        DEFNAME=i_dagger
        NAME=dagger
        TYPE=t_weapon_fence
        VALUE=21
        WEIGHT=1

        [ITEMDEF 0e85]
        DEFNAME=i_pickaxe
        NAME=pickaxe
        TYPE=t_weapon_mace_pick
        VALUE=24
        WEIGHT=5

        [ITEMDEF 0f7a]
        DEFNAME=i_reag_black_pearl
        NAME=black pearl
        TYPE=t_reagent
        VALUE=5
        WEIGHT=0.1
        """;

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
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public required ListLogger Log { get; init; }
        public long Now { get; set; } = 1_000_000;

        public bool Run(int ms, Func<bool>? done = null)
        {
            for (int t = 0; t < ms; t += 100)
            {
                if (done?.Invoke() == true) break;
                Engine.Tick(Now);
                Now += 100;
            }
            Assert.Empty(Log.Warnings);
            return done?.Invoke() ?? true;
        }
    }

    private static Rig MakeRig(Dictionary<string, string[]>? chat = null, string[]? intents = null)
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_botplan_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Definitions);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = Path.GetDirectoryName(file) ?? "" };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        File.Delete(file);

        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;

        var crafting = new CraftingEngine(world);
        crafting.LoadRecipesFromDefs(resources);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var log = new ListLogger();
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources, nav,
            BotChatBook.FromLines("en", chat ?? new Dictionary<string, string[]>()), log, seed: 7)
        {
            TargetCount = 1000,
            Gravity = 0,
            Intents = BotIntents.Parse(intents ?? []),
        };
        engine.AddGatherTrade(new BotGatherTrade(SkillType.Mining, ItemType.WeaponMacePick, ["MiningSpot"]));
        engine.AddSaleRule(new BotSaleRule(ItemType.WeaponFence, ["VendorSmith"]));
        engine.AddNeed(new BotNeed("reagent", ItemType.Reagent, 15, 30, ["VendorMage"]));
        // Plans of three steps, so what follows the first step can be looked at.
        engine.Plans.Apply(["set minsteps 3", "set maxsteps 3"]);
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
                world, new SphereNet.Game.Accounts.AccountManager(lf), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            c.SetEngines(skillHandlers: new SkillHandlers(world, new GatheringEngine(world)), craftingEngine: crafting,
                triggerDispatcher: new TriggerDispatcher());
            c.AttachServerDriven(ch);
            return c;
        };
        var rig = new Rig { World = world, Engine = engine, Log = log };
        // The engine's clock is the rig's from the start: a bot adopted before the first
        // tick would otherwise be timed on the machine's uptime, and its drives with it.
        engine.SetClock(rig.Now);
        return rig;
    }

    private static Character Bot(Rig rig, Point3D at, Action<Character>? setup = null, string trait = "kind")
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Bot" + ch.Uid.Value;
        ch.Str = 80; ch.Dex = 80; ch.Int = 60;
        ch.MaxStam = 80; ch.Stam = 80; ch.MaxHits = 80; ch.Hits = 80; ch.MaxMana = 60; ch.Mana = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        ch.SetTag(PlayerBotEngine.RoleTag, "-");
        ch.SetTag(PlayerBotEngine.TraitTag, trait);
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        setup?.Invoke(ch);
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        // It stands where it was put until the test asks for its choice; neither bold
        // nor timid, so its nerve weighs nothing.
        var mind = rig.Engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        mind.Nerve = 1.2;
        return ch;
    }

    private static Character Mage(Rig rig, Point3D at) => Bot(rig, at, ch =>
    {
        ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_mage_m");
        ch.SetSkill(SkillType.Magery, 800);
    });

    private static Character Warrior(Rig rig, Point3D at, string trait = "kind") => Bot(rig, at, ch =>
    {
        ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_warrior_m");
        ch.SetSkill(SkillType.Swordsmanship, 900);
    }, trait);

    private static Item Give(Rig rig, Character ch, string defName, int amount = 1, Layer hand = Layer.None)
    {
        var item = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, DefinitionLoader.ResolveItemDefIndexByName(defName)));
        item.Amount = (ushort)amount;
        if (hand != Layer.None) Assert.True(ch.Equip(item, hand));
        else Assert.True(ch.Backpack!.TryAddItem(item));
        return item;
    }

    private static void Gold(Rig rig, Item into, int amount)
    {
        var g = rig.World.CreateItem();
        g.BaseId = 0x0EED;
        g.ItemType = ItemType.Gold;
        g.Amount = (ushort)amount;
        Assert.True(into.TryAddItem(g));
    }

    private static Item BankBox(Rig rig, Character ch)
    {
        var bank = rig.World.CreateItem();
        bank.BaseId = 0x09AB;
        bank.ItemType = ItemType.EqBankBox;
        Assert.True(ch.Equip(bank, Layer.BankBox));
        return bank;
    }

    private static List<BotActivity> Steps(BotMind mind) => mind.Plan.Steps.Select(s => s.Activity).ToList();

    private static string Why(BotMind mind) => string.Join(" | ", mind.Trace().Select(t => t.Text)) + " || " + mind.Plan.LastChoice;

    [Fact]
    public void APoorMageBuysReagentsBeforeItGoesHunting()
    {
        var rig = MakeRig();
        var bot = Mage(rig, new Point3D(100, 100, 0, 0));
        Gold(rig, bot.Backpack!, 300);
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(mind.IsCaster);

        var goal = rig.Engine.PlanNext(mind, rig.Now, out bool started);
        Assert.False(started);
        Assert.Equal("Town Mage", goal?.Name);
        Assert.Equal("reagent", mind.ShoppingFor?.Name);
        var steps = Steps(mind);
        Assert.Equal(BotActivity.Shop, steps[0]);
        Assert.Contains(BotActivity.Hunt, steps);
        Assert.Equal(BotDrive.Supplies, mind.Plan.Why);
        Assert.Equal("reagent", mind.Plan.WhyNeed);
        // Hunting was weighed, and lost for want of reagents.
        var options = rig.Engine.Options(mind, rig.Engine.Situation(mind, rig.Now), rig.Now);
        Assert.Contains(options, o => o.Activity == BotActivity.Hunt);
        Assert.True(options.First(o => o.Activity == BotActivity.Hunt).Utility < 0, Why(mind));
    }

    [Fact]
    public void AMageWhoseGoldIsInTheBankFetchesItWhenVendorsTakeOnlyThePack()
    {
        var rig = MakeRig();
        bool before = VendorEngine.PayFromPackOnly;
        VendorEngine.PayFromPackOnly = true;
        try
        {
            var bot = Mage(rig, new Point3D(120, 100, 0, 0));
            Gold(rig, BankBox(rig, bot), 600);
            var mind = rig.Engine.MindOf(bot)!;

            var goal = rig.Engine.PlanNext(mind, rig.Now, out _);
            Assert.Equal("Town Bank", goal?.Name);
            Assert.Equal([BotActivity.Bank, BotActivity.Shop, BotActivity.Hunt], Steps(mind));
            Assert.Equal(BotDrive.Supplies, mind.Plan.Why);
            // Once there, the bank errand runs at once.
            Assert.Equal(0, mind.NextBankMs);
        }
        finally { VendorEngine.PayFromPackOnly = before; }
    }

    [Fact]
    public void AFighterWithAFullPackSellsBeforeItGoesOutAgain()
    {
        var rig = MakeRig();
        var bot = Warrior(rig, new Point3D(100, 100, 0, 0));
        Give(rig, bot, "i_dagger", hand: Layer.OneHanded);
        for (int i = 0; i < 12; i++) Give(rig, bot, "i_dagger");
        Gold(rig, bot.Backpack!, 2400);
        var mind = rig.Engine.MindOf(bot)!;
        // It has been in town long enough to want to go out.
        var plan = rig.Engine.PlanOf(mind, rig.Now);
        plan.Town = "Town";
        plan.TownSinceMs = rig.Now - 1_500_000;
        var options = rig.Engine.Options(mind, rig.Engine.Situation(mind, rig.Now), rig.Now);
        Assert.True(options.First(o => o.Activity == BotActivity.Hunt).Utility < options.First(o => o.Activity == BotActivity.Sell).Utility);

        rig.Engine.PlanNext(mind, rig.Now, out bool started);
        Assert.True(started, Why(mind));
        Assert.Equal(BotJob.Sell, mind.Work?.Job);
        var steps = Steps(mind);
        Assert.Equal(BotActivity.Sell, steps[0]);
        Assert.Contains(BotActivity.Hunt, steps);
        Assert.Contains(BotActivity.Bank, steps);
        Assert.Equal("Cave Mouth", mind.Plan.Steps.First(s => s.Activity == BotActivity.Hunt).Target);
    }

    [Fact]
    public void AWoundedBotRestsAtTheInn()
    {
        var rig = MakeRig();
        var bot = Warrior(rig, new Point3D(100, 100, 0, 0));
        bot.Hits = 20;
        var mind = rig.Engine.MindOf(bot)!;

        var goal = rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal("Town Inn", goal?.Name);
        Assert.Equal(BotActivity.Rest, Steps(mind)[0]);
        Assert.Equal(BotDrive.Safety, mind.Plan.Why);

        // Standing at the inn already, it stays there a good while.
        mind.BeginLinger(rig.Engine.Nav.FindDestination("Town Inn"), rig.Now, new Random(1), short_: true);
        mind.Plan.Clear();
        Assert.Null(rig.Engine.PlanNext(mind, rig.Now, out bool started));
        Assert.True(started);
        Assert.True(mind.LingerUntilMs >= rig.Now + 120_000);
    }

    [Fact]
    public void ATradesmanWorksAndSellsOnceItIsLoaded()
    {
        var rig = MakeRig();
        var miner = Bot(rig, new Point3D(100, 100, 0, 0), ch => ch.SetSkill(SkillType.Mining, 800));
        Give(rig, miner, "i_pickaxe");
        var mind = rig.Engine.MindOf(miner)!;
        mind.LingerUntilMs = 0;
        // The stay ends through the ordinary act: off to the mine.
        Assert.True(rig.Run(2_000, () => mind.Work != null), Why(mind));
        Assert.Equal(BotJob.Gather, mind.Work!.Job);
        Assert.Equal(BotActivity.Work, Steps(mind)[0]);

        // A miner with goods of its own make in the pack takes them to a vendor first.
        var other = Bot(rig, new Point3D(100, 100, 0, 0), ch => ch.SetSkill(SkillType.Mining, 800));
        Give(rig, other, "i_pickaxe");
        for (int i = 0; i < 6; i++) Give(rig, other, "i_dagger").Crafter = other.Uid;
        var loaded = rig.Engine.MindOf(other)!;
        Assert.True(rig.Engine.CanWork(loaded));
        rig.Engine.PlanNext(loaded, rig.Now, out bool started);
        Assert.True(started);
        Assert.Equal(BotJob.Sell, loaded.Work?.Job);
        Assert.Equal(BotDrive.Load, loaded.Plan.Why);
    }

    [Fact]
    public void ItsNatureChangesWhatItChooses()
    {
        var rig = MakeRig();
        BotMind Make(string trait)
        {
            var ch = Bot(rig, new Point3D(100, 100, 0, 0), trait: trait);
            for (int i = 0; i < 6; i++) Give(rig, ch, "i_dagger").Crafter = ch.Uid;
            var m = rig.Engine.MindOf(ch)!;
            var plan = rig.Engine.PlanOf(m, rig.Now);
            plan.LastSocialMs = rig.Now - 1_500_000; // alone a long while
            return m;
        }
        var chatty = Make("chatty");
        var shy = Make("shy");
        var a = rig.Engine.Options(chatty, rig.Engine.Situation(chatty, rig.Now), rig.Now);
        var b = rig.Engine.Options(shy, rig.Engine.Situation(shy, rig.Now), rig.Now);
        double Social(List<PlayerBotEngine.BotOption> list) => list.First(o => o.Activity == BotActivity.Social).Utility;
        Assert.True(Social(a) > Social(b) + 1);

        rig.Engine.PlanNext(chatty, rig.Now, out _);
        rig.Engine.PlanNext(shy, rig.Now, out _);
        Assert.Equal(BotActivity.Social, Steps(chatty)[0]);
        Assert.Equal(BotActivity.Sell, Steps(shy)[0]);

        // A thief keeps to town: hunting is not among its options.
        var thief = Warrior(rig, new Point3D(100, 100, 0, 0));
        thief.SetTag(PlayerBotEngine.RoleTag, "thief");
        var tm = rig.Engine.MindOf(thief)!;
        tm.Role = null;
        Assert.DoesNotContain(rig.Engine.Options(tm, rig.Engine.Situation(tm, rig.Now), rig.Now),
            o => o.Activity == BotActivity.Hunt);
    }

    [Fact]
    public void ThePlanSurvivesARestartAndGoesOn()
    {
        var rig = MakeRig();
        var bot = Mage(rig, new Point3D(100, 100, 0, 0));
        Gold(rig, bot.Backpack!, 300);
        var mind = rig.Engine.MindOf(bot)!;
        mind.LingerUntilMs = 0;
        Assert.True(rig.Run(2_000, () => mind.Mode == BotMode.Travel), Why(mind));
        Assert.Equal("Town Mage", mind.Goal?.Name);
        var steps = Steps(mind);
        Assert.True(bot.TryGetTag(PlayerBotEngine.PlanTag, out string? tag));
        Assert.Contains("Shop:Town Mage", tag);

        // The server stops on the way to the shop.
        rig.Engine.Release(bot);
        Assert.Equal(1, rig.Engine.AdoptSaved());
        var again = rig.Engine.MindOf(bot)!;
        Assert.NotSame(mind, again);
        Assert.Equal(steps, Steps(again));
        Assert.Equal(0, again.Plan.Step);
        // Still going to buy: the shop's need is known again.
        Assert.Equal("Town Mage", again.Goal?.Name);
        Assert.Equal("reagent", again.ShoppingFor?.Name);

        // Bought: the plan's next step comes next, not a new plan.
        Give(rig, bot, "i_reag_black_pearl", 30);
        long kept = rig.Engine.WindowPlansKept;
        var goal = rig.Engine.PlanNext(again, rig.Now, out _);
        Assert.Equal(steps[1], again.Plan.Current?.Activity);
        Assert.Equal(1, again.Plan.Step);
        Assert.Equal(kept + 1, rig.Engine.WindowPlansKept);
        if (steps[1] == BotActivity.Hunt) Assert.Equal("Cave Mouth", goal?.Name);
    }

    [Fact]
    public void ABotCarriedOffMakesANewPlan()
    {
        var rig = MakeRig();
        var bot = Mage(rig, new Point3D(100, 100, 0, 0));
        Gold(rig, bot.Backpack!, 300);
        var mind = rig.Engine.MindOf(bot)!;
        rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.NotEmpty(mind.Plan.Steps);
        mind.Mode = BotMode.Linger;
        mind.LingerUntilMs = long.MaxValue;

        // Staff move it to the other end of the road.
        Assert.True(rig.World.MoveCharacter(bot, new Point3D(190, 100, 0, 0)));
        rig.Run(300);
        Assert.True(mind.Plan.Interrupted);
        Assert.Empty(mind.Plan.Steps);
        Assert.Contains(mind.Trace(), t => t.Text.Contains("plan interrupted: carried"));

        long made = rig.Engine.WindowPlansMade, kept = rig.Engine.WindowPlansKept;
        rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal(1, rig.Engine.WindowPlanInterrupts);
        Assert.Equal(made + 1, rig.Engine.WindowPlansMade);
        Assert.Equal(kept, rig.Engine.WindowPlansKept);
        Assert.False(mind.Plan.Interrupted);
        Assert.Equal(BotActivity.Shop, Steps(mind)[0]);

        // A death drops the plan too, and leaves the bot wary for a while.
        rig.Engine.InterruptPlan(mind, rig.Now, "died");
        Assert.Empty(mind.Plan.Steps);
        Assert.True(rig.Engine.Situation(mind, rig.Now).Drives[(int)BotDrive.Safety] > 0.5);
    }

    [Fact]
    public void ThePlannedStepIsKeptAgainstANearRivalButNotAgainstAPressingNeed()
    {
        var rig = MakeRig();
        var bot = Bot(rig, new Point3D(100, 100, 0, 0));
        var mind = rig.Engine.MindOf(bot)!;
        var plan = rig.Engine.PlanOf(mind, rig.Now);
        void Planned()
        {
            plan.Clear();
            plan.Steps.Add(new BotPlanStep(BotActivity.Wander));
            plan.Steps.Add(new BotPlanStep(BotActivity.Social));
            plan.Step = 0;
            plan.MadeMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        }
        Planned();
        // Nothing presses: looking about scores a little above company, but company
        // is what it planned, and it keeps to it.
        var options = rig.Engine.Options(mind, rig.Engine.Situation(mind, rig.Now), rig.Now);
        Assert.True(options.First(o => o.Activity == BotActivity.Wander).Utility >
                    options.First(o => o.Activity == BotActivity.Social).Utility);
        for (int i = 0; i < 5; i++)
        {
            Planned();
            rig.Engine.PlanNext(mind, rig.Now, out _);
            Assert.Equal(1, mind.Plan.Step);
            Assert.Equal(BotActivity.Social, mind.Plan.Current?.Activity);
        }
        Assert.Equal(5, rig.Engine.WindowPlansKept);

        // Badly hurt, the plan is set aside for a rest.
        bot.Hits = 8;
        Planned();
        var goal = rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal(1, rig.Engine.WindowPlansUrgent);
        Assert.Equal(BotActivity.Rest, Steps(mind)[0]);
        Assert.Equal("Town Inn", goal?.Name);
    }

    [Fact]
    public void ABotSaysWhatItsPlanIs()
    {
        var rig = MakeRig(
            chat: new Dictionary<string, string[]>
            {
                ["respond_plan"] = ["{now}, then {next}. {why}"],
                ["respond_plan_none"] = ["no plan"],
                ["respond_doing_plan"] = ["{goal}, {why}"],
                ["token_activity"] = ["Shop=buying supplies", "Hunt=hunting", "Bank=a bank run"],
                ["token_need"] = ["Supplies=low on supplies", "Supplies.reagent=out of reagents"],
                ["token_placetype"] = ["VendorMage=mage shop"],
            },
            intents: ["plan: what+is+your+plan", "doing: what+are+you+doing"]);
        var said = new List<(string Category, string Line)>();
        rig.Engine.OnLine = (_, category, line) => said.Add((category, line));
        var player = rig.World.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        rig.World.PlaceCharacter(player, new Point3D(102, 100, 0, 0));
        rig.World.AddOnlinePlayer(player);

        var bot = Mage(rig, new Point3D(100, 100, 0, 0));
        string name = PlayerBotEngine.FirstName(bot).ToLowerInvariant();
        var mind = rig.Engine.MindOf(bot)!;
        rig.Run(1_200);
        // No plan yet.
        rig.Engine.OnHear(player, $"{name} what is your plan");
        Assert.True(rig.Run(10_000, () => said.Count > 0));
        Assert.Equal(("respond_plan_none", "no plan"), said[0]);

        Gold(rig, bot.Backpack!, 300);
        rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal(BotActivity.Shop, Steps(mind)[0]);
        mind.Mode = BotMode.Linger; // it stands to talk
        said.Clear();
        rig.Engine.OnHear(player, $"{name} what is your plan");
        Assert.True(rig.Run(10_000, () => said.Count > 0));
        Assert.Equal("respond_plan", said[0].Category);
        Assert.Equal($"buying supplies, then {(Steps(mind)[1] == BotActivity.Hunt ? "hunting" : Steps(mind)[1].ToString().ToLowerInvariant())}. out of reagents",
            said[0].Line);

        // On its way, "what are you doing" gets the reason too.
        mind.Mode = BotMode.Travel;
        mind.Goal = rig.Engine.Nav.FindDestination("Town Mage");
        said.Clear();
        rig.Engine.OnHear(player, $"{name} what are you doing");
        Assert.True(rig.Run(10_000, () => said.Count > 0));
        Assert.Equal(("respond_doing_plan", "Town mage shop, out of reagents"), said[0]);
    }

    [Fact]
    public void ThePlanTagReadsBackAsWritten()
    {
        var plan = new BotPlan { MadeMinute = 29_000_000, Step = 1, Why = BotDrive.Supplies, WhyNeed = "reagent", DeathMinute = 28_999_990 };
        plan.Steps.Add(new BotPlanStep(BotActivity.Shop, "Britain Mage"));
        plan.Steps.Add(new BotPlanStep(BotActivity.Hunt, "Destard L1 Entrance"));
        plan.Steps.Add(new BotPlanStep(BotActivity.Sell));
        string tag = plan.Write();
        Assert.Equal("29000000|1|Supplies:reagent|Shop:Britain Mage;Hunt:Destard L1 Entrance;Sell|28999990", tag);
        var back = new BotPlan();
        Assert.True(back.Read(tag));
        Assert.Equal(plan.Steps, back.Steps);
        Assert.Equal(1, back.Step);
        Assert.Equal(BotDrive.Supplies, back.Why);
        Assert.Equal("reagent", back.WhyNeed);
        Assert.Equal(28_999_990, back.DeathMinute);
        Assert.False(new BotPlan().Read("garbage"));
    }

    [Fact]
    public void TheShippedPlanDataLoadsAndItsLinesExistInEveryLanguage()
    {
        string dir = TestRepo.Tracked("playerbots");
        var book = BotPlanBook.Load(dir);
        Assert.Empty(book.Rejected);
        Assert.True(book.Does(BotActivity.Hunt, BotDrive.Supplies) < 0);
        Assert.True(book.Does(BotActivity.Shop, BotDrive.Supplies) > 0);
        foreach (string lang in new[] { "en", "tr" })
            foreach (string c in new[] { "respond_plan", "respond_plan_none", "respond_doing_plan", "token_activity", "token_need" })
                Assert.True(File.Exists(Path.Combine(dir, "chat", lang, c + ".txt")), $"chat/{lang}/{c}.txt missing");
        var chat = BotChatBook.Load(dir, "en");
        foreach (string lang in new[] { "en", "tr" })
            foreach (var a in Enum.GetValues<BotActivity>().Where(a => a != BotActivity.None))
            {
                bool found = false;
                for (int i = 0; chat.Line("token_activity", i, lang) is { } line; i++)
                    found |= line.StartsWith(a + "=", StringComparison.Ordinal);
                Assert.True(found, $"token_activity ({lang}) has no {a}");
            }
        var intents = BotIntents.Load(dir);
        Assert.Equal(BotIntentKind.Plan, intents.Parse("what is your plan?").Kind);
        Assert.Equal(BotIntentKind.Plan, intents.Parse("planın ne kanka").Kind);
        Assert.Equal(BotIntentKind.Doing, intents.Parse("where are you going?").Kind);
    }
}
