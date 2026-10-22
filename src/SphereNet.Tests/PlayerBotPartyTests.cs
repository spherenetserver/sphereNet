using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A bot in a player's party: it waits and follows when told, tells its
/// health in party chat, stands up for its leader and heals it.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotPartyTests
{
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

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["party_stay"] = ["holding here"],
        ["party_follow"] = ["coming"],
        ["party_leave"] = ["im out"],
        ["party_status"] = ["{hp}/{maxhp} hp, {mana}/{maxmana} mana"],
    };

    private static BotNavGraph Nav(GameWorld world)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints("""
            { "Waypoints": [
              { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
              { "Name": "B", "X": 110, "Y": 100, "Z": 0 }
            ] }
            """, world.MapData);
        return nav;
    }

    private sealed record Setup(GameWorld World, PartyManager Parties, PlayerBotEngine Engine, Character Bot,
        Character Player, long Now);

    private static Setup Party(string? arch = null,
        Func<GameWorld, SphereNet.Game.Magic.SpellEngine>? spellsFor = null)
    {
        var world = World();
        var spells = spellsFor?.Invoke(world);
        var parties = new PartyManager();
        var bot = world.CreateCharacter();
        bot.IsPlayer = true;
        bot.Name = "Wanderer";
        bot.Str = 60; bot.Dex = 60; bot.Int = 40;
        bot.MaxStam = 60; bot.Stam = 60; bot.MaxHits = 60; bot.Hits = 60;
        bot.SetTag(PlayerBotEngine.MarkerTag, "1");
        bot.SetTag(PlayerBotEngine.HomeTag, "Town");
        if (arch != null) bot.SetTag(PlayerBotEngine.ArchetypeTag, arch);
        world.PlaceCharacter(bot, new Point3D(100, 100, 0, 0));

        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, Nav(world),
            BotChatBook.FromLines("en", Lines), NullLogger.Instance, seed: 7)
        {
            TargetCount = 1,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            Parties = parties,
            Intents = BotIntents.Parse([
                "stay: stay wait+here bekle",
                "leaveparty: leave+party",
                "status: status durum",
                "follow: come+here follow+me",
            ]),
        };
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), partyManager: parties, spellEngine: spells);
            c.AttachServerDriven(ch);
            return c;
        };
        engine.AdoptSaved();

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        player.Str = 60; player.Dex = 60; player.MaxStam = 60; player.Stam = 60;
        player.MaxHits = 60; player.Hits = 60;
        world.PlaceCharacter(player, new Point3D(102, 100, 0, 0));
        Assert.True(parties.Invite(player, bot, PartyIo.ForWorld(world, null)));
        long now = Run(engine, 1_000_000, 400, () => parties.FindParty(bot.Uid) != null);
        Assert.True(parties.FindParty(bot.Uid) != null, $"tag {(player.TryGetTag(PartyManager.LastInviteTag, out string? t) ? t : "none")} bot {bot.Uid.Value} invite {engine.MindOf(bot)?.InviteFrom?.Name} hasclient {player.HasClient} client {engine.MindOf(bot)?.Client != null}" + Trace(engine, bot));
        return new Setup(world, parties, engine, bot, player, now);
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
        string.Join(Environment.NewLine, engine.TraceOf(bot.Uid.Value.ToString()));

    [Fact]
    public void TheBotWaitsWhenToldAndFollowsAgainWhenCalled()
    {
        var s = Party();
        long now = s.Now;
        var spot = s.Bot.Position;
        s.Engine.OnHear(s.Player, "wait here");
        now = Run(s.Engine, now, 20);
        Assert.True(s.Engine.WindowPartyOrders >= 1);

        s.World.MoveCharacter(s.Player, new Point3D(112, 100, 0, 0));
        now = Run(s.Engine, now, 100);
        Assert.True(s.Bot.Position.GetDistanceTo(spot) <= 1, $"bot at {s.Bot.Position}{Environment.NewLine}{Trace(s.Engine, s.Bot)}");

        s.Engine.OnHear(s.Player, "come here");
        Run(s.Engine, now, 150, () => s.Bot.Position.GetDistanceTo(s.Player.Position) <= 3);
        Assert.True(s.Bot.Position.GetDistanceTo(s.Player.Position) <= 3, $"bot at {s.Bot.Position}{Environment.NewLine}{Trace(s.Engine, s.Bot)}");
    }

    [Fact]
    public void AStatusAskedInPartyChatIsAnsweredInPartyChat()
    {
        var s = Party();
        var said = new List<(Character, string)>();
        PartyManager.OnPartyMessage = (who, text, _) =>
        {
            said.Add((who, text));
            s.Engine.OnPartyChat(who, text, Serial.Invalid);
        };
        s.Bot.Hits = 41;
        PartyManager.OnPartyMessage(s.Player, "status?", Serial.Invalid);
        Run(s.Engine, s.Now, 100, () => said.Any(x => x.Item1 == s.Bot));
        var reply = said.FirstOrDefault(x => x.Item1 == s.Bot);
        Assert.True(reply.Item2 != null, Trace(s.Engine, s.Bot));
        Assert.Contains("41/60 hp", reply.Item2);
    }

    [Fact]
    public void TheBotTurnsOnWhatAttacksItsLeader()
    {
        var s = Party(arch: "warrior");
        var orc = s.World.CreateCharacter();
        orc.Name = "orc";
        orc.BodyId = 0x11;
        orc.NpcBrain = NpcBrainType.Monster;
        orc.Str = 40; orc.Dex = 40; orc.MaxHits = 40; orc.Hits = 40;
        orc.SetStatFlag(StatFlag.Freeze);
        s.World.PlaceCharacter(orc, new Point3D(104, 102, 0, 0));
        orc.FightTarget = s.Player.Uid;
        var mind = s.Engine.MindOf(s.Bot)!;
        Run(s.Engine, s.Now, 50, () => mind.Foe == orc);
        Assert.True(mind.Foe == orc, Trace(s.Engine, s.Bot));
    }

    [Fact]
    public void ACasterHealsItsWoundedLeader()
    {
        Character.SpellbookRequiredEnabled = false;
        var registry = new SphereNet.Game.Magic.SpellRegistry();
        registry.Register(new SphereNet.Game.Magic.SpellDef
        {
            Id = SpellType.Heal, Name = "Heal", Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            CastTimeBase = 1, EffectBase = 10, EffectScale = 10,
        });
        var s = Party(arch: "mage", spellsFor: w => new SphereNet.Game.Magic.SpellEngine(w, registry));
        s.Bot.SetSkill(SkillType.Magery, 1000);
        // Mana for Heal, not Greater Heal (which is not registered here).
        s.Bot.Int = 100; s.Bot.MaxMana = 100; s.Bot.Mana = 10;
        s.Player.Hits = 20;

        long now = s.Now;
        for (int i = 0; i < 200 && s.Player.Hits <= 20; i++)
        {
            now = Run(s.Engine, now, 1);
            Thread.Sleep(10);
        }
        Assert.True(s.Engine.WindowPartyTends >= 1, Trace(s.Engine, s.Bot));
        Assert.True(s.Player.Hits > 20, $"hits {s.Player.Hits}{Environment.NewLine}{Trace(s.Engine, s.Bot)}");
    }
}
