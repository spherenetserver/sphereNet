using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Bots looking after themselves and the rogues among them: taking what a
/// kill dropped, knowing what they run short of, running for the road, and a player
/// killer setting on a lone player.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotRogueSupplyTests
{
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] }
        ] }
        """;

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

    private static PlayerBotEngine Engine(GameWorld world)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 5)
        {
            TargetCount = 1000,
            PkSpareSkill = 0,
        };
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.AttachServerDriven(ch);
            return c;
        };
        return engine;
    }

    private static Character Bot(GameWorld world, PlayerBotEngine engine, Point3D at, Action<Character>? setup = null)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Bot" + ch.Uid.Value;
        ch.Str = 80; ch.Dex = 80; ch.Int = 40;
        ch.MaxStam = 80; ch.Stam = 80; ch.MaxHits = 80; ch.Hits = 80;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        setup?.Invoke(ch);
        world.PlaceCharacter(ch, at);
        engine.AdoptSaved();
        var mind = engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        return ch;
    }

    [Fact]
    public void AWonFightEndsWithTheGoldOffTheBody()
    {
        var world = World();
        var engine = Engine(world);
        var bot = Bot(world, engine, new Point3D(100, 100, 0, 0), ch => ch.SetTag(PlayerBotEngine.RoleTag, "-"));
        var orc = world.CreateCharacter();
        orc.Name = "orc";
        orc.NpcBrain = NpcBrainType.Monster;
        orc.MaxHits = 40; orc.Hits = 40;
        world.PlaceCharacter(orc, new Point3D(101, 100, 0, 0));
        orc.FightTarget = bot.Uid;

        long now = 1_000_000;
        for (int i = 0; i < 30; i++) { engine.Tick(now); now += 100; }
        var mind = engine.MindOf(bot)!;
        Assert.Equal(BotMode.Fight, mind.Mode);

        // The orc dies where it stood and leaves a body with gold on it.
        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", orc.Uid.Value.ToString());
        world.PlaceItem(corpse, orc.Position);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = 37;
        Assert.True(corpse.TryAddItem(gold));
        orc.Hits = 0;
        orc.SetStatFlag(StatFlag.Dead);

        for (int i = 0; i < 200 && gold.ContainedIn != bot.Backpack!.Uid; i++) { engine.Tick(now); now += 100; }
        Assert.Equal(bot.Backpack!.Uid, gold.ContainedIn);
        for (int i = 0; i < 100 && mind.Mode == BotMode.Loot; i++) { engine.Tick(now); now += 100; }
        Assert.NotEqual(BotMode.Loot, mind.Mode);
    }

    [Fact]
    public void ABotKnowsWhatItRunsShortOf()
    {
        var world = World();
        var engine = Engine(world);
        engine.AddNeed(new BotNeed("bandage", ItemType.Bandage, 10, 40, ["Healer"]));
        engine.AddNeed(new BotNeed("reagent", ItemType.Reagent, 15, 30, ["VendorMage"]));
        var healer = Bot(world, engine, new Point3D(100, 100, 0, 0), ch => ch.SetSkill(SkillType.Healing, 800));
        var mind = engine.MindOf(healer)!;
        Assert.Equal("bandage", engine.FirstNeed(mind)?.Name);

        var bandages = world.CreateItem();
        bandages.BaseId = 0x0E21;
        bandages.ItemType = ItemType.Bandage;
        bandages.Amount = 25;
        Assert.True(healer.Backpack!.TryAddItem(bandages));
        Assert.Null(engine.FirstNeed(mind));
    }

    [Fact]
    public void AFleeingBotRunsForTheRoadAwayFromTheThreat()
    {
        var world = World();
        var engine = Engine(world);
        // At node B with the threat east of it: node A, west, is the way out.
        var p = engine.FleePoint(new Point3D(110, 100, 0, 0), new Point3D(114, 100, 0, 0));
        Assert.Equal(new Point3D(100, 100, 0, 0), p);
    }

    [Fact]
    public void APlayerKillerSetsOnALonePlayerOutOfTown()
    {
        var world = World();
        var engine = Engine(world);
        engine.PkPercent = 100;
        var pk = Bot(world, engine, new Point3D(100, 100, 0, 0), ch =>
        {
            ch.SetSkill(SkillType.Swordsmanship, 900);
            ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_warrior_m");
        });
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Name = "Lonely";
        victim.MaxHits = 50; victim.Hits = 50;
        world.PlaceCharacter(victim, new Point3D(104, 100, 0, 0));

        long now = 1_000_000;
        for (int i = 0; i < 300 && pk.FightTarget != victim.Uid; i++) { engine.Tick(now); now += 100; }
        Assert.Equal("pk", pk.TryGetTag(PlayerBotEngine.RoleTag, out string? role) ? role : null);
        Assert.Equal(victim.Uid, pk.FightTarget);
    }

    [Fact]
    public void ACasterLowOnManaMeditates()
    {
        var world = World();
        var engine = Engine(world);
        var mage = Bot(world, engine, new Point3D(100, 100, 0, 0), ch =>
        {
            ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_mage_m");
            ch.SetTag(PlayerBotEngine.RoleTag, "-");
            ch.SetSkill(SkillType.Magery, 900);
            ch.SetSkill(SkillType.Meditation, 900);
            ch.Int = 100; ch.MaxMana = 100; ch.Mana = 10;
        });
        Assert.True(engine.MindOf(mage)!.IsCaster);
        long now = 1_000_000;
        for (int i = 0; i < 30; i++) { engine.Tick(now); now += 100; }
        Assert.True(engine.WindowMeditations > 0);
    }
}
