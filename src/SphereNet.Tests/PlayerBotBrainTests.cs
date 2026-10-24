using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The bots' brain as the engine uses it: which lines it is asked to answer,
/// what it is told, how long the bot waits, and what the bot says when the reply is
/// late, refused or unfit. A stand-in brain replaces the language model.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotBrainTests
{
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 120, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] }
        ] }
        """;

    private static readonly string[] IntentLines =
    [
        "where: where nerde nerede",
        "greet: hi hello selam",
        "howareyou: how+are+you naber",
        "question: why",
        "place.Bank: bank banka",
        "lang.tr: nerde abi kanka naber",
        "lang.en: where is the",
    ];

    /// <summary>A brain that answers at once, never, or not at all, and keeps what it was told.</summary>
    private sealed class StandIn : IBotBrain
    {
        public Func<BotThought, string?>? Answer { get; set; } = _ => "fine, and you?";
        public bool Busy { get; set; }
        public List<BotThought> Asked { get; } = [];

        public bool TryThink(BotThought thought)
        {
            if (Busy) return false;
            Asked.Add(thought);
            if (Answer != null) thought.Finish(Answer(thought));
            return true;
        }
    }

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public required StandIn Brain { get; init; }
        public List<(Character Who, string Line)> Said { get; } = [];
        public long Now { get; set; } = 1_000_000;

        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 100)
            {
                Engine.Tick(Now);
                Now += 100;
            }
        }
    }

    private static Rig MakeRig()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, map);
        nav.LoadDestinations(DestinationsJson, map);
        var en = new Dictionary<string, string[]>
        {
            ["respond_name"] = ["own line"],
            ["respond_chat"] = ["own line"],
            ["respond_what"] = ["own line"],
            ["respond_howareyou"] = ["own line"],
            ["respond_question"] = ["own line"],
            ["brain_thinking"] = ["*thinks...*"],
            ["respond_where"] = ["{target} {dir} {dist}"],
            ["token_dir"] = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"],
            ["token_placetype"] = ["Bank=bank"],
        };
        var brain = new StandIn();
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLanguages("en", new Dictionary<string, IReadOnlyDictionary<string, string[]>> { ["en"] = en, ["tr"] = en }),
            LoggerFactory.Create(_ => { }).CreateLogger("bots"), seed: 3)
        {
            TargetCount = 1000,
            Intents = BotIntents.Parse(IntentLines),
            Gravity = 0,
            Brain = brain,
        };
        var rig = new Rig { World = world, Engine = engine, Brain = brain };
        engine.Say = (ch, line) => rig.Said.Add((ch, line));
        return rig;
    }

    private static Character Bot(Rig rig, string name, Point3D at)
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        var mind = rig.Engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        mind.At = rig.Engine.Nav.FindDestination("Town Bank");
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

    private static (Rig Rig, Character Bot, Character Hero) Scene()
    {
        var rig = MakeRig();
        var bot = Bot(rig, "Rowena", new Point3D(122, 101, 0, 0));
        var hero = Player(rig, "Hero", new Point3D(123, 101, 0, 0));
        rig.Run(1500);
        return (rig, bot, hero);
    }

    private static string? Reply(Rig rig, Character bot, Character hero, string text, int ms = 6000)
    {
        rig.Said.Clear();
        rig.Engine.OnHear(hero, text);
        rig.Run(ms);
        return rig.Said.Where(s => s.Who == bot).Select(s => s.Line).LastOrDefault();
    }

    [Fact]
    public void SmallTalkToABotIsAnsweredByTheBrain()
    {
        var (rig, bot, hero) = Scene();
        Assert.Equal("fine, and you?", Reply(rig, bot, hero, "how are you rowena"));
        var thought = Assert.Single(rig.Brain.Asked);
        Assert.Equal("how are you rowena", thought.Line);
        Assert.Contains("Rowena", thought.Persona);
        Assert.Contains("Hero", thought.Persona);
        Assert.Contains("English", thought.Persona);
        Assert.Contains("bank", thought.Persona);
        Assert.Empty(thought.History);
    }

    [Fact]
    public void ATurkishLineIsAnsweredInTurkish()
    {
        var (rig, bot, hero) = Scene();
        Reply(rig, bot, hero, "naber rowena");
        Assert.Contains("Turkish", Assert.Single(rig.Brain.Asked).Persona);
    }

    [Fact]
    public void AFactIsStillAnsweredByTheBot()
    {
        var (rig, bot, hero) = Scene();
        string? said = Reply(rig, bot, hero, "rowena where is the bank");
        Assert.Empty(rig.Brain.Asked);
        Assert.NotEqual("fine, and you?", said);
    }

    [Fact]
    public void ALateReplyLeavesTheBotItsOwnLine()
    {
        var (rig, bot, hero) = Scene();
        rig.Brain.Answer = null; // never finishes
        rig.Engine.BrainWaitMs = 3500;
        rig.Engine.OnHear(hero, "how are you rowena");
        rig.Run(3000);
        // Past its typing delay with no reply yet: it shows it is thinking, once.
        Assert.Equal(["*thinks...*"], rig.Said.Where(s => s.Who == bot).Select(s => s.Line).ToArray());
        rig.Run(2000);
        Assert.Equal(["*thinks...*", "own line"], rig.Said.Where(s => s.Who == bot).Select(s => s.Line).ToArray());
        Assert.True(Assert.Single(rig.Brain.Asked).Abandoned);
        Assert.Equal(1, rig.Engine.WindowThoughtsLate);
    }

    [Fact]
    public void AReplyInTimeIsSaidWithoutAThinkingEmote()
    {
        var (rig, bot, hero) = Scene();
        Reply(rig, bot, hero, "how are you rowena");
        Assert.Equal(["fine, and you?"], rig.Said.Where(s => s.Who == bot).Select(s => s.Line).ToArray());
    }

    [Fact]
    public void ABusyBrainLeavesTheBotItsOwnLine()
    {
        var (rig, bot, hero) = Scene();
        rig.Brain.Busy = true;
        Assert.Equal("own line", Reply(rig, bot, hero, "how are you rowena"));
        Assert.Equal(1, rig.Engine.WindowThoughtsRefused);
    }

    [Fact]
    public void AnUnfitReplyLeavesTheBotItsOwnLine()
    {
        var (rig, bot, hero) = Scene();
        rig.Brain.Answer = _ => "As an AI language model I cannot play games.";
        Assert.Equal("own line", Reply(rig, bot, hero, "how are you rowena"));
        Assert.Equal(1, rig.Engine.WindowThoughtsUnfit);
    }

    [Fact]
    public void TheConversationIsRememberedForTheNextReply()
    {
        var (rig, bot, hero) = Scene();
        rig.Brain.Answer = t => t.History.Count == 0 ? "fine, and you?" : "glad to hear it";
        Assert.Equal("fine, and you?", Reply(rig, bot, hero, "how are you rowena"));
        Assert.Equal("glad to hear it", Reply(rig, bot, hero, "why rowena, i am good too"));
        var second = rig.Brain.Asked[1];
        Assert.Equal([("how are you rowena", "fine, and you?")], second.History);
    }

    [Theory]
    [InlineData("\"Hello there, friend!\"", "Hello there, friend!")]
    [InlineData("Rowena: just banking, you?", "just banking, you?")]
    [InlineData("fine 😀 thanks", "fine thanks")]
    [InlineData("it’s fine\nsecond line", "it's fine")]
    [InlineData("they seem a little.unpredictable, no?", "they seem a little. unpredictable, no?")]
    [InlineData("As an AI, I can't.", null)]
    [InlineData("ben bir yapay zeka asistanıyım", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    public void RepliesAreCleanedOrRefused(string reply, string? expected)
    {
        Assert.Equal(expected, PlayerBotEngine.CleanReply(reply, "Rowena"));
    }

    [Fact]
    public void ALongReplyIsCutAtAWord()
    {
        string reply = string.Join(' ', Enumerable.Repeat("word", 60));
        string? clean = PlayerBotEngine.CleanReply(reply, "Rowena");
        Assert.NotNull(clean);
        Assert.True(clean!.Length <= 140);
        Assert.EndsWith("word", clean);
    }
}
