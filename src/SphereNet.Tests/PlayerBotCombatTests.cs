using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>How bots fight: reading the fight (a better foe, the trend, too many on
/// it), a mage's repertoire branch by branch, what a warrior holds and drinks, an
/// archer's footwork and its empty quiver, sizing prey up and remembering what killed
/// it, joining somebody else's fight, and the ways out. Every action is the bot's own
/// client's: the casts are the spell engine's, the swaps the equip macro's.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotCombatTests(Xunit.Abstractions.ITestOutputHelper outp)
{
    // A road A (100,100) - B (110,100) - C (120,100); with a town, B and C are guarded.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "W", "X": 80, "Y": 100, "Z": 0, "Connects": [ "A" ] },
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

    private const ushort WallTile = 0x0080;
    private const ushort SwordId = 0x0F5E, MaceId = 0x0F5C, ShieldId = 0x1B76, BowId = 0x13B2, DaggerId = 0x0F51;

    private static readonly SpellType[] AllSpells =
    [
        SpellType.ReactiveArmor, SpellType.Protection, SpellType.Bless, SpellType.MagicReflect,
        SpellType.Heal, SpellType.GreaterHeal, SpellType.Cure,
        SpellType.MagicArrow, SpellType.Harm, SpellType.Fireball, SpellType.Lightning, SpellType.EnergyBolt,
        SpellType.Explosion, SpellType.Flamestrike,
        SpellType.Weaken, SpellType.Curse, SpellType.Paralyze, SpellType.Poison,
        SpellType.Teleport, SpellType.Invisibility, SpellType.Recall,
    ];

    private static readonly int[] CircleMana = [4, 6, 9, 11, 14, 20, 40, 50];

    private static SpellDef Def(SpellType id)
    {
        SpellFlag flags = id switch
        {
            SpellType.Teleport or SpellType.WallOfStone => SpellFlag.TargXYZ,
            SpellType.BladeSpirit or SpellType.EnergyVortex or SpellType.EarthElemental =>
                SpellFlag.TargXYZ | SpellFlag.Summon,
            SpellType.Recall => SpellFlag.TargItem,
            SpellType.Heal or SpellType.GreaterHeal => SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            SpellType.Cure or SpellType.ReactiveArmor or SpellType.Protection or SpellType.Bless or
                SpellType.MagicReflect or SpellType.Invisibility or SpellType.Refresh => SpellFlag.TargChar | SpellFlag.Good,
            SpellType.Weaken or SpellType.Curse or SpellType.Paralyze or SpellType.Poison =>
                SpellFlag.TargChar | SpellFlag.Harm,
            _ => SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage,
        };
        return new SpellDef
        {
            Id = id, Name = id.ToString(), Flags = flags,
            ManaCost = (ushort)CircleMana[Math.Clamp(((int)id - 1) / 8, 0, 7)],
            CastTimeBase = 1, EffectBase = 4, EffectScale = 8, DurationBase = 300, DurationScale = 300,
        };
    }

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public required SpellEngine Spells { get; init; }
        public List<(Character Who, SpellType Spell)> Casts { get; } = [];
        public List<(string Category, string Line)> Said { get; } = [];
        public long Now { get; set; } = 1_000_000;

        /// <summary>Ticks. A cast under way is ended at the next tick (the cast timer
        /// runs on the real clock; the test's clock is its own).</summary>
        public void Run(int ticks, Func<bool>? done = null, Action? each = null)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = 2 };
            for (int i = 0; i < ticks && (done == null || !done()); i++)
            {
                Engine.RunSearches(po);
                Engine.Tick(Now);
                Now += 100;
                each?.Invoke();
                foreach (var ch in World.GetAllCharactersSnapshot())
                    if (ch.IsPlayerBot && ch.IsCasting) ch.SetCastTimerEnd(1);
            }
        }

        public List<SpellType> CastsOf(Character who) => Casts.Where(c => c.Who == who).Select(c => c.Spell).ToList();

        public string Trace(Character bot) =>
            $"casts: {string.Join(", ", CastsOf(bot))}{Environment.NewLine}" +
            string.Join(Environment.NewLine, Engine.TraceOf(bot.Uid.Value.ToString()));
    }

    private static Rig Make(bool town = false, SpellType[]? without = null, SpellType[]? also = null,
        Dictionary<string, string[]>? lines = null)
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        map.SetSyntheticItemTile(WallTile, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Wall,
            Height = 20,
        });
        TestHarness.SeedItemDefs(WallTile);
        if (town)
        {
            var region = new Region { Name = "Town", Flags = RegionFlag.Guarded, MapIndex = 0 };
            region.AddRect(108, 80, 140, 120);
            world.AddRegion(region);
        }

        Character.SpellbookRequiredEnabled = false;
        var registry = new SpellRegistry();
        foreach (var id in AllSpells.Concat(also ?? []))
            if (without == null || !without.Contains(id))
                registry.Register(Def(id));
        var spells = new SpellEngine(world, registry);

        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", lines ?? new Dictionary<string, string[]>()), NullLogger.Instance, seed: 11)
        {
            TargetCount = 50,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            Spells = spells,
        };
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), spellEngine: spells);
            c.AttachServerDriven(ch);
            return c;
        };
        var rig = new Rig { World = world, Engine = engine, Spells = spells };
        spells.OnCastResolved = (who, spell, _) => rig.Casts.Add((who, spell));
        engine.OnLine = (_, category, line) => rig.Said.Add((category, line));
        return rig;
    }

    /// <summary>A bot of an archetype ("warrior", "mage", "archer"), adopted, staying put.</summary>
    private static (Character Bot, BotMind Mind) Bot(Rig r, Point3D at, string kind, Action<Character>? setup = null,
        double nerve = 1.4, int fleeAt = 20)
    {
        var ch = r.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Bot" + ch.Uid.Value;
        ch.Str = 80; ch.Dex = 80; ch.Int = 40;
        ch.MaxStam = 80; ch.Stam = 80; ch.MaxHits = 100; ch.Hits = 100;
        if (kind == "mage")
        {
            ch.Int = 100; ch.MaxMana = 100; ch.Mana = 100;
            ch.SetSkill(SkillType.Magery, 1000);
            ch.SetSkill(SkillType.Meditation, 800);
        }
        else if (kind == "archer")
            ch.SetSkill(SkillType.Archery, 900);
        else
            ch.SetSkill(SkillType.Swordsmanship, 900);
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        ch.SetTag(PlayerBotEngine.RoleTag, "-");
        ch.SetTag(PlayerBotEngine.ArchetypeTag, $"c_playerbot_{kind}_m");
        var pack = r.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        setup?.Invoke(ch);
        r.World.PlaceCharacter(ch, at);
        r.Engine.AdoptSaved();
        var mind = r.Engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        mind.Nerve = nerve;
        mind.FleeAt = fleeAt;
        // Gear, mounts and errands stay out of a fight test's way.
        mind.NextGearCheckMs = long.MaxValue;
        return (ch, mind);
    }

    private static Character Monster(Rig r, Point3D at, Character? target, int hits = 200, string name = "orc",
        int damLo = 0, int damHi = 0, int skill = 0)
    {
        var m = r.World.CreateCharacter();
        m.Name = name;
        m.BodyId = 0x11;
        m.NpcBrain = NpcBrainType.Monster;
        m.Str = 60; m.Dex = 40; m.MaxHits = (short)hits; m.Hits = (short)hits;
        if (damHi > 0) m.SetAttackRating(damLo, damHi);
        if (skill > 0) m.SetSkill(SkillType.Wrestling, (ushort)skill);
        r.World.PlaceCharacter(m, at);
        if (target != null) m.FightTarget = target.Uid;
        return m;
    }

    private static Item Give(Rig r, Character ch, ushort baseId, ItemType type, int amount = 1, uint more1 = 0)
    {
        var item = r.World.CreateItem();
        item.BaseId = baseId;
        item.ItemType = type;
        item.Amount = (ushort)amount;
        item.More1 = more1;
        Assert.True(ch.Backpack!.TryAddItem(item));
        return item;
    }

    private static Item Weapon(Rig r, ushort baseId, ItemType type, Layer layer, int lo, int hi, bool twoHands = false)
    {
        DefinitionLoader.SetItemDef(baseId, new ItemDef(ResourceId.Invalid)
        {
            DispIndex = baseId, Type = type, Layer = layer, AttackMin = lo, AttackMax = hi, Speed = 40,
            TwoHands = twoHands,
            // A bow shoots what its TDATA3 names (arrows).
            TData3 = type == ItemType.WeaponBow ? 0x0F3Fu : 0u,
        });
        var item = r.World.CreateItem();
        item.BaseId = baseId;
        item.ItemType = type;
        item.SetAttackRating(lo, hi);
        return item;
    }

    private static int Dist(Character a, Character b) => a.Position.GetDistanceTo(b.Position);

    // ------------------------------------------------------------------
    //  Reading the fight
    // ------------------------------------------------------------------

    [Fact]
    public void ANewAttackerThatHurtsMoreIsTurnedOn()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        var first = Monster(r, new Point3D(101, 100, 0, 0), bot);
        r.Run(40, () => mind.Foe == first);
        Assert.Same(first, mind.Foe);

        var second = Monster(r, new Point3D(100, 101, 0, 0), bot, name: "troll");
        // It hits hard, blow after blow; the first has done nothing yet.
        int tick = 0;
        r.Run(80, () => mind.Foe == second, each: () =>
        {
            if (++tick % 10 == 0) bot.RecordAttack(second.Uid, 7);
        });
        Assert.True(mind.Foe == second, r.Trace(bot));
        Assert.Equal(second.Uid, bot.FightTarget);
        Assert.Equal(2, mind.Tac.Attackers);
        Assert.True(r.Engine.WindowTargetSwitches >= 1);
    }

    [Fact]
    public void ANearlyDeadAttackerIsFinishedFirstAndANearlyDeadFoeIsNotLeft()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        var first = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 400);
        r.Run(40, () => mind.Foe == first);

        // Another one, all but down, right beside it: that one first.
        var weak = Monster(r, new Point3D(100, 101, 0, 0), bot, hits: 400, name: "wounded orc");
        weak.Hits = 40;
        r.Run(60, () => mind.Foe == weak);
        Assert.True(mind.Foe == weak, r.Trace(bot));

        // Now the first hits harder - but the one it is on is nearly down: it stays.
        bot.RecordAttack(first.Uid, 30);
        r.Run(50);
        Assert.True(mind.Foe == weak, r.Trace(bot));
    }

    [Fact]
    public void PreyIsLeftForTheOneThatAttacks()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", ch =>
            Assert.True(ch.Equip(Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 10, 24), Layer.OneHanded)));
        var prey = Monster(r, new Point3D(104, 100, 0, 0), target: null, hits: 400);
        prey.SetStatFlag(StatFlag.Freeze);
        r.Run(200, () => mind.Foe == prey);
        Assert.True(mind.Foe == prey, r.Trace(bot));

        var attacker = Monster(r, new Point3D(bot.X, (short)(bot.Y + 1), 0, 0), bot, name: "troll");
        r.Run(80, () => mind.Foe == attacker);
        Assert.True(mind.Foe == attacker, r.Trace(bot));
    }

    [Fact]
    public void ALosingTrendMakesItBreakOffWellAboveItsHitPointMark()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 1.0, fleeAt: 20);
        var foe = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 2000);
        foe.SetStatFlag(StatFlag.Freeze);
        r.Run(20, () => mind.Mode == BotMode.Fight);
        Assert.Equal(BotMode.Fight, mind.Mode);

        // Four hit points a second out of a hundred, and the foe barely scratched.
        int tick = 0;
        r.Run(400, () => mind.Mode == BotMode.Flee, each: () =>
        {
            if (++tick % 5 == 0 && bot.Hits > 5) bot.Hits -= 2;
        });
        Assert.True(mind.Mode == BotMode.Flee, r.Trace(bot));
        Assert.True(bot.Hits >= 45, $"left at {bot.Hits} hit points{Environment.NewLine}{r.Trace(bot)}");
        Assert.Equal(1, r.Engine.WindowTrendRetreats);
    }

    [Fact]
    public void AWinningTrendKeepsItInTheFight()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 1.0, fleeAt: 20);
        var foe = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 2000);
        foe.SetStatFlag(StatFlag.Freeze);
        foe.Hits = 300;
        r.Run(20, () => mind.Mode == BotMode.Fight);

        // The same loss, but the foe goes down four times as fast: it stays.
        int tick = 0;
        r.Run(100, each: () =>
        {
            if (++tick % 5 != 0) return;
            if (bot.Hits > 50) bot.Hits -= 2;
            if (foe.Hits > 40) foe.Hits -= 12;
        });
        Assert.True(mind.Mode == BotMode.Fight, r.Trace(bot));
        Assert.Equal(0, r.Engine.WindowRetreats);
    }

    [Fact]
    public void ThreeOnItIsTooManyForATimidBotButOneIsNot()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 0.8, fleeAt: 20);
        var a = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 2000);
        r.Run(20, () => mind.Mode == BotMode.Fight);
        bot.Hits = 65;
        r.Run(40);
        Assert.True(mind.Mode == BotMode.Fight, r.Trace(bot));

        Monster(r, new Point3D(100, 101, 0, 0), bot, hits: 2000, name: "troll");
        Monster(r, new Point3D(99, 100, 0, 0), bot, hits: 2000, name: "ogre");
        r.Run(60, () => mind.Mode == BotMode.Flee);
        Assert.True(mind.Mode == BotMode.Flee, r.Trace(bot));
        Assert.Contains(r.Engine.TraceOf(bot.Uid.Value.ToString()), l => l.Contains("outnumbered"));
        Assert.NotNull(a);
    }

    [Fact]
    public void ABoldBotStaysForTheLastBlowATimidOneLeaves()
    {
        string? why(double nerve)
        {
            var r = Make();
            var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: nerve, fleeAt: 30);
            var foe = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 200);
            mind.Foe = foe;
            mind.Tac.Attackers = 1;
            // Both near the end; the foe falls within a second or two at this rate.
            bot.Hits = 20;
            foe.Hits = 6;
            mind.Tac.LossRate = 2;
            mind.Tac.FoeLossRate = 6;
            return r.Engine.WhyRetreat(mind, foe, r.Now, 20);
        }
        Assert.Null(why(2.0));
        Assert.Equal("badly hurt", why(0.8));
    }

    // ------------------------------------------------------------------
    //  A mage's fight
    // ------------------------------------------------------------------

    [Fact]
    public void AMageWardsItselfWhileTheFoeIsStillFar()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(109, 100, 0, 0), bot);
        foe.SetStatFlag(StatFlag.Freeze);
        r.Run(200, () => r.CastsOf(bot).Count >= 3);
        var casts = r.CastsOf(bot);
        Assert.True(casts.Count >= 3, r.Trace(bot));
        // Against something that strikes: Reactive Armor, then Protection; then the fight.
        Assert.Equal(SpellType.ReactiveArmor, casts[0]);
        Assert.Equal(SpellType.Protection, casts[1]);
        Assert.DoesNotContain(casts[2], new[] { SpellType.ReactiveArmor, SpellType.Protection, SpellType.Bless });
        Assert.Equal(BotMode.Fight, mind.Mode);
    }

    [Fact]
    public void AgainstACasterTheFirstWardIsMagicReflection()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var lich = Monster(r, new Point3D(109, 100, 0, 0), bot, name: "lich");
        lich.SetSkill(SkillType.Magery, 900);
        lich.MaxMana = 100; lich.Mana = 100;
        lich.SetStatFlag(StatFlag.Freeze);
        r.Run(200, () => r.CastsOf(bot).Count >= 1);
        Assert.Equal(SpellType.MagicReflect, r.CastsOf(bot).FirstOrDefault());
    }

    [Fact]
    public void AFoeAlreadyOnItGetsNoWards()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(102, 100, 0, 0), bot, hits: 500);
        r.Run(200, () => r.CastsOf(bot).Count >= 2);
        var casts = r.CastsOf(bot);
        Assert.DoesNotContain(SpellType.ReactiveArmor, casts);
        Assert.DoesNotContain(SpellType.Protection, casts);
    }

    [Fact]
    public void ADangerousMeleeFoeIsParalyzedBeforeItArrives()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var ogre = Monster(r, new Point3D(105, 100, 0, 0), bot, hits: 400, name: "ogre lord", damLo: 30, damHi: 40, skill: 900);
        r.Run(300, () => r.CastsOf(bot).Contains(SpellType.Paralyze));
        var casts = r.CastsOf(bot);
        Assert.True(casts.Contains(SpellType.Paralyze), r.Trace(bot));
        // Before any damage spell.
        int at = casts.IndexOf(SpellType.Paralyze);
        Assert.DoesNotContain(casts.Take(at), s => s is SpellType.EnergyBolt or SpellType.Explosion or
            SpellType.Flamestrike or SpellType.Lightning or SpellType.Fireball or SpellType.MagicArrow);
        Assert.NotNull(ogre);
    }

    [Fact]
    public void WithoutParalyzeTheOpenerIsACurseAndAHarmlessFoeGetsNone()
    {
        var r = Make(without: [SpellType.Paralyze]);
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 400, name: "ogre lord", damLo: 30, damHi: 40, skill: 900);
        r.Run(300, () => r.CastsOf(bot).Contains(SpellType.Curse));
        Assert.True(r.CastsOf(bot).Contains(SpellType.Curse), r.Trace(bot));

        var r2 = Make();
        var (bot2, _) = Bot(r2, new Point3D(100, 100, 0, 0), "mage");
        Monster(r2, new Point3D(103, 100, 0, 0), bot2, hits: 400);
        r2.Run(150, () => r2.CastsOf(bot2).Count >= 4);
        Assert.DoesNotContain(SpellType.Paralyze, r2.CastsOf(bot2));
        Assert.DoesNotContain(SpellType.Curse, r2.CastsOf(bot2));
    }

    [Fact]
    public void AFoeNotPoisonedIsPoisonedAndOnePoisonedIsNot()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 500);
        r.Run(200, () => r.CastsOf(bot).Count >= 1);
        Assert.Equal(SpellType.Poison, r.CastsOf(bot).FirstOrDefault());

        var r2 = Make();
        var (bot2, _) = Bot(r2, new Point3D(100, 100, 0, 0), "mage");
        var sick = Monster(r2, new Point3D(103, 100, 0, 0), bot2, hits: 500);
        sick.SetStatFlag(StatFlag.Poisoned);
        r2.Run(200, () => r2.CastsOf(bot2).Count >= 3);
        Assert.DoesNotContain(SpellType.Poison, r2.CastsOf(bot2));
    }

    [Fact]
    public void ExplosionIsFollowedByEnergyBolt()
    {
        var r = Make(without: [SpellType.Poison]);
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage", ch => ch.SetSkill(SkillType.Magery, 850));
        Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 3000);
        r.Run(300, () => r.CastsOf(bot).Count >= 2);
        var casts = r.CastsOf(bot);
        Assert.True(casts.Count >= 2, r.Trace(bot));
        Assert.Equal(SpellType.Explosion, casts[0]);
        Assert.Equal(SpellType.EnergyBolt, casts[1]);
        Assert.True(r.Engine.WindowCombos >= 1);
    }

    [Theory]
    [InlineData(1000, SpellType.Flamestrike)]
    [InlineData(850, SpellType.Explosion)]
    [InlineData(450, SpellType.Lightning)]
    [InlineData(200, SpellType.Fireball)]
    [InlineData(50, SpellType.MagicArrow)]
    public void TheDamageSpellFollowsTheCircleItsMageryReaches(int magery, SpellType expected)
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage", ch => ch.SetSkill(SkillType.Magery, (ushort)magery));
        var foe = Monster(r, new Point3D(105, 100, 0, 0), null, hits: 3000);
        Assert.Equal(expected, r.Engine.BestNuke(mind, foe));
        // A foe one small spell from the end gets the small spell.
        foe.Hits = 2;
        Assert.Equal(SpellType.MagicArrow, r.Engine.BestNuke(mind, foe));
        Assert.NotNull(bot);
    }

    [Fact]
    public void ASpellItCannotPayForIsNotChosen()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(105, 100, 0, 0), null, hits: 3000);
        bot.Mana = 30; // Flamestrike is 40, Explosion + Energy Bolt 40
        Assert.Equal(SpellType.EnergyBolt, r.Engine.BestNuke(mind, foe));
        bot.Mana = 10;
        Assert.Equal(SpellType.Fireball, r.Engine.BestNuke(mind, foe));
        bot.Mana = 3;
        Assert.Null(r.Engine.BestNuke(mind, foe));
        Assert.False(r.Engine.CanCast(mind, SpellType.Heal));
    }

    [Fact]
    public void ASpellNotInItsBookIsNotCast()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Assert.True(r.Engine.CanCast(mind, SpellType.EnergyBolt));
        // With books required and a book that holds only the first circle.
        Character.SpellbookRequiredEnabled = true;
        var book = Give(r, bot, 0x0EFA, ItemType.Spellbook, more1: 0xFF);
        Assert.True(book.ContainsSpell((int)SpellType.MagicArrow));
        Assert.True(r.Engine.CanCast(mind, SpellType.MagicArrow));
        Assert.False(r.Engine.CanCast(mind, SpellType.EnergyBolt));
        var foe = Monster(r, new Point3D(105, 100, 0, 0), null, hits: 3000);
        Assert.Equal(SpellType.MagicArrow, r.Engine.BestNuke(mind, foe));
    }

    [Fact]
    public void HealOrGreaterHealByTheWoundAndTheMana()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(105, 100, 0, 0), null, hits: 300);
        // A deep wound: the big heal.
        Assert.Equal(SpellType.GreaterHeal, r.Engine.HealChoice(mind, 30, foe));
        // A scratch: the small one.
        Assert.Equal(SpellType.Heal, r.Engine.HealChoice(mind, 60, foe));
        // In between: the big one while a spell to fight with is still paid for after it.
        Assert.Equal(SpellType.GreaterHeal, r.Engine.HealChoice(mind, 45, foe));
        bot.Mana = 20;
        Assert.Equal(SpellType.Heal, r.Engine.HealChoice(mind, 45, foe));
        // Mana for the small one only.
        bot.Mana = 5;
        Assert.Equal(SpellType.Heal, r.Engine.HealChoice(mind, 30, foe));
        bot.Mana = 100;
        // Not hurt enough; and not while the foe is one blow from the end.
        Assert.Null(r.Engine.HealChoice(mind, 70, foe));
        foe.Hits = 20;
        Assert.Null(r.Engine.HealChoice(mind, 40, foe));
        Assert.Equal(SpellType.GreaterHeal, r.Engine.HealChoice(mind, 25, foe));
    }

    [Fact]
    public void AHurtMageHealsAndAPoisonedOneCuresBeforeAnythingElse()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 500);
        bot.Hits = 30;
        r.Run(200, () => r.CastsOf(bot).Count >= 1);
        Assert.Equal(SpellType.GreaterHeal, r.CastsOf(bot).FirstOrDefault());

        var r2 = Make();
        var (bot2, _) = Bot(r2, new Point3D(100, 100, 0, 0), "mage");
        Monster(r2, new Point3D(103, 100, 0, 0), bot2, hits: 500);
        bot2.SetStatFlag(StatFlag.Poisoned);
        r2.Run(200, () => r2.CastsOf(bot2).Count >= 1);
        Assert.Equal(SpellType.Cure, r2.CastsOf(bot2).FirstOrDefault());
    }

    [Fact]
    public void OutOfManaWithTheFoeHeldItBacksOffAndMeditates()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(104, 100, 0, 0), bot, hits: 500);
        foe.SetStatFlag(StatFlag.Freeze);
        bot.Mana = 2;
        r.Run(100, () => r.Engine.WindowManaRests > 0);
        Assert.True(r.Engine.WindowManaRests > 0, r.Trace(bot));
        Assert.Equal(BotMode.Fight, mind.Mode);
        Assert.Empty(r.CastsOf(bot));
    }

    [Fact]
    public void OutOfManaWithAFoeOnItItFightsHandToHand()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage", ch => ch.SetSkill(SkillType.Wrestling, 800));
        var foe = Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 500);
        bot.Mana = 2;
        r.Run(150, () => mind.Tac.Brawling && Dist(bot, foe) <= 1);
        Assert.True(mind.Tac.Brawling, r.Trace(bot));
        Assert.Equal(BotMode.Fight, mind.Mode);
        Assert.True(Dist(bot, foe) <= 1, $"stands {Dist(bot, foe)} off{Environment.NewLine}{r.Trace(bot)}");
        Assert.Equal(foe.Uid, bot.FightTarget);
    }

    [Fact]
    public void OutOfManaWithNothingToFightWithItLeaves()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(102, 100, 0, 0), bot, hits: 500);
        bot.Mana = 2;
        r.Run(100, () => mind.Mode == BotMode.Flee);
        Assert.True(mind.Mode == BotMode.Flee, r.Trace(bot));
        Assert.Contains(r.Engine.TraceOf(bot.Uid.Value.ToString()), l => l.Contains("out of mana"));
        // And it does not stand there: it gets away from the foe.
        r.Run(60);
        Assert.True(Dist(bot, foe) >= 5, $"only {Dist(bot, foe)} away{Environment.NewLine}{r.Trace(bot)}");
    }

    [Fact]
    public void AMageWithABladeAtItsThroatTeleportsAway()
    {
        var r = Make();
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var foe = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 500);
        bot.Hits = 70;
        r.Run(200, () => r.CastsOf(bot).Contains(SpellType.Teleport));
        Assert.True(r.CastsOf(bot).Contains(SpellType.Teleport), r.Trace(bot));
        Assert.True(Dist(bot, foe) >= 5, $"still {Dist(bot, foe)} off{Environment.NewLine}{r.Trace(bot)}");
    }

    [Fact]
    public void ALosingMageVanishes()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 500);
        r.Run(10, () => mind.Mode == BotMode.Fight);
        bot.Hits = 8;
        bot.Mana = 30; // Invisibility is 20
        r.Run(200, () => r.CastsOf(bot).Contains(SpellType.Invisibility));
        Assert.True(r.CastsOf(bot).Contains(SpellType.Invisibility), r.Trace(bot));
        Assert.Equal(1, r.Engine.WindowVanishes);
        Assert.True(bot.IsInvisible, "the spell left it in plain sight");
        Assert.False(bot.IsInWarMode);
    }

    [Fact]
    public void ALosingMageWithARuneRecallsOut()
    {
        var r = Make(without: [SpellType.Invisibility, SpellType.Teleport]);
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var rune = Give(r, bot, 0x1F14, ItemType.Rune);
        rune.SetRuneMark(new Point3D(300, 300, 0, 0));
        rune.More1 = 10;
        var foe = Monster(r, new Point3D(102, 100, 0, 0), bot, hits: 500);
        foe.SetStatFlag(StatFlag.Freeze);
        r.Run(10, () => mind.Mode == BotMode.Fight);
        bot.Hits = 8;
        r.Run(200, () => r.CastsOf(bot).Contains(SpellType.Recall));
        Assert.True(r.CastsOf(bot).Contains(SpellType.Recall), r.Trace(bot));
        Assert.Equal(1, r.Engine.WindowEscapeRecalls);
        Assert.NotEqual(BotMode.Fight, mind.Mode);
        r.Run(20, () => bot.Position.GetDistanceTo(new Point3D(300, 300, 0, 0)) <= 2);
        Assert.True(bot.Position.GetDistanceTo(new Point3D(300, 300, 0, 0)) <= 2, $"still at {bot.Position}{Environment.NewLine}{r.Trace(bot)}");
        // Away and alone: it is not fleeing or fighting any more.
        r.Run(20);
        Assert.Equal(BotMode.Linger, mind.Mode);
    }

    [Fact]
    public void ToughPreyGetsSomethingSummonedAgainstItButNotAmongTheGuards()
    {
        var r = Make(also: [SpellType.EnergyVortex, SpellType.BladeSpirit]);
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var troll = Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 600, name: "troll");
        r.Run(300, () => r.CastsOf(bot).Count >= 2);
        var casts = r.CastsOf(bot);
        Assert.True(casts.Count >= 2, r.Trace(bot));
        // The strongest it can call, before anything else is thrown.
        Assert.Equal(SpellType.EnergyVortex, casts[0]);
        Assert.Equal(1, r.Engine.WindowSummons);
        Assert.Contains(r.World.GetCharsInRange(troll.Position, 3), c => c.IsSummoned && c.OwnerSerial == bot.Uid);
        // Once per foe.
        r.Run(60);
        Assert.Equal(1, r.CastsOf(bot).Count(s => s is SpellType.EnergyVortex or SpellType.BladeSpirit));

        // Without the mana for a vortex: blade spirits.
        var low = Make(also: [SpellType.EnergyVortex, SpellType.BladeSpirit]);
        var (poor, _) = Bot(low, new Point3D(100, 100, 0, 0), "mage", ch => ch.Mana = 40);
        Monster(low, new Point3D(103, 100, 0, 0), poor, hits: 600, name: "troll");
        low.Run(300, () => low.CastsOf(poor).Count >= 1);
        Assert.Equal(SpellType.BladeSpirit, low.CastsOf(poor).FirstOrDefault());

        // In a guarded town nothing is summoned.
        var town = Make(town: true, also: [SpellType.EnergyVortex, SpellType.BladeSpirit]);
        var (citizen, _) = Bot(town, new Point3D(120, 100, 0, 0), "mage");
        Monster(town, new Point3D(123, 100, 0, 0), citizen, hits: 600, name: "troll");
        town.Run(300, () => town.CastsOf(citizen).Count >= 3);
        Assert.True(town.CastsOf(citizen).Count >= 3, town.Trace(citizen));
        Assert.DoesNotContain(town.CastsOf(citizen), s => s is SpellType.EnergyVortex or SpellType.BladeSpirit);
    }

    [Fact]
    public void WhatItSummonsIsSetOnTheFoe()
    {
        var r = Make(also: [SpellType.EarthElemental]);
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        var troll = Monster(r, new Point3D(103, 100, 0, 0), bot, hits: 600, name: "troll");
        Character? pet = null;
        r.Run(300, () =>
        {
            pet ??= r.World.GetCharsInRange(troll.Position, 4).FirstOrDefault(c => c.IsSummoned && c.OwnerSerial == bot.Uid);
            return pet != null && pet.FightTarget == troll.Uid;
        });
        Assert.Equal(SpellType.EarthElemental, r.CastsOf(bot).FirstOrDefault());
        Assert.True(pet != null, r.Trace(bot));
        // "all kill" and the target, as its owner would say it.
        Assert.True(pet!.FightTarget == troll.Uid, $"the summon fights {pet.FightTarget}{Environment.NewLine}{r.Trace(bot)}");
    }

    [Fact]
    public void AHurtMageWallsOffAFoeThatIsComing()
    {
        var r = Make(also: [SpellType.WallOfStone], without: [SpellType.Poison]);
        var (bot, _) = Bot(r, new Point3D(100, 100, 0, 0), "mage");
        Monster(r, new Point3D(104, 100, 0, 0), bot, hits: 600);
        bot.Hits = 35;
        r.Run(200, () => r.CastsOf(bot).Contains(SpellType.WallOfStone));
        var casts = r.CastsOf(bot);
        Assert.True(casts.Contains(SpellType.WallOfStone), r.Trace(bot));
        // The wound first, then the wall; no damage spell before either.
        Assert.Equal(SpellType.GreaterHeal, casts[0]);
        Assert.Equal(1, casts.IndexOf(SpellType.WallOfStone));
    }

    // ------------------------------------------------------------------
    //  What a warrior holds and drinks
    // ------------------------------------------------------------------

    [Fact]
    public void ASwordsmanPutsDownTheMaceForTheSwordInItsPack()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", ch =>
        {
            ch.SetSkill(SkillType.Swordsmanship, 950);
            ch.SetSkill(SkillType.MaceFighting, 300);
            ch.SetSkill(SkillType.Parrying, 700);
        });
        // The same damage on both: the skill decides.
        var mace = Weapon(r, MaceId, ItemType.WeaponMaceSmith, Layer.OneHanded, 8, 20);
        Assert.True(bot.Equip(mace, Layer.OneHanded));
        var sword = Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 8, 20);
        Assert.True(bot.Backpack!.TryAddItem(sword));
        TestHarness.SeedWearable(ShieldId, Layer.TwoHanded);
        var shield = Give(r, bot, ShieldId, ItemType.Shield);
        Assert.True(r.Engine.WeaponWorth(bot, sword) > r.Engine.WeaponWorth(bot, mace));

        mind.NextGearCheckMs = 0;
        r.Run(20, () => bot.GetEquippedItem(Layer.OneHanded) == sword);
        Assert.True(bot.GetEquippedItem(Layer.OneHanded) == sword, r.Trace(bot));
        Assert.Equal(bot.Backpack.Uid, mace.ContainedIn);
        // And with Parrying and a hand free, the shield goes on at the next look.
        mind.NextGearCheckMs = 0;
        r.Run(20, () => bot.GetEquippedItem(Layer.TwoHanded) == shield);
        Assert.True(bot.GetEquippedItem(Layer.TwoHanded) == shield, r.Trace(bot));
        Assert.Equal(1, r.Engine.WindowWeaponSwaps);
        Assert.Equal(1, r.Engine.WindowShieldsUp);
    }

    [Fact]
    public void AWeakerWeaponOfItsBestSkillIsNotWorthTheChange()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", ch =>
        {
            ch.SetSkill(SkillType.Swordsmanship, 900);
            ch.SetSkill(SkillType.MaceFighting, 850);
        });
        var mace = Weapon(r, MaceId, ItemType.WeaponMaceSmith, Layer.OneHanded, 20, 40);
        Assert.True(bot.Equip(mace, Layer.OneHanded));
        var sword = Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 3, 6);
        Assert.True(bot.Backpack!.TryAddItem(sword));
        mind.NextGearCheckMs = 0;
        r.Run(20);
        Assert.Same(mace, bot.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void ABandageOnlyWithTheHealingForIt()
    {
        var r = Make();
        var (healer, hm) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", ch => ch.SetSkill(SkillType.Healing, 800));
        Give(r, healer, 0x0E21, ItemType.Bandage, 20);
        Monster(r, new Point3D(101, 100, 0, 0), healer, hits: 2000);
        r.Run(10, () => hm.Mode == BotMode.Fight);
        healer.Hits = 55;
        r.Run(30, () => r.Engine.WindowBandages > 0);
        Assert.Equal(1, r.Engine.WindowBandages);

        var r2 = Make();
        var (clumsy, cm) = Bot(r2, new Point3D(100, 100, 0, 0), "warrior");
        var bandages = Give(r2, clumsy, 0x0E21, ItemType.Bandage, 20);
        Monster(r2, new Point3D(101, 100, 0, 0), clumsy, hits: 2000);
        r2.Run(10, () => cm.Mode == BotMode.Fight);
        clumsy.Hits = 55;
        r2.Run(40);
        Assert.Equal(0, r2.Engine.WindowBandages);
        Assert.Equal(20, bandages.Amount);
    }

    [Fact]
    public void AHurtFighterDrinksItsHealPotionAndAWindedOneItsRefresh()
    {
        var r = Make(also: [SpellType.Refresh]);
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        var heal = Give(r, bot, 0x0F0C, ItemType.Potion, 2, more1: (uint)SpellType.GreaterHeal);
        heal.More2 = 600;
        var refresh = Give(r, bot, 0x0F0B, ItemType.Potion, 2, more1: (uint)SpellType.Refresh);
        refresh.More2 = 1000;
        Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 2000);
        r.Run(10, () => mind.Mode == BotMode.Fight);

        bot.Hits = 40;
        r.Run(40, () => heal.Amount < 2);
        Assert.True(heal.Amount == 1, r.Trace(bot));
        Assert.True(bot.Hits > 40, "the heal potion did nothing");

        // Out of breath: the refresh, once the potion delay is over.
        bot.Hits = 100;
        bot.Stam = 5;
        bot.GetEquippedItem(Layer.FlagPotionUsed)?.Delete();
        r.Run(60, () => refresh.Amount < 2);
        Assert.True(refresh.Amount == 1, r.Trace(bot));
        Assert.True(bot.Stam > 5, "the refresh potion did nothing");
        Assert.Equal(2, r.Engine.WindowPotions);
    }

    [Fact]
    public void APoisonedFighterDrinksItsCurePotionOnce()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        var cure = Give(r, bot, 0x0F07, ItemType.Potion, 3, more1: (uint)SpellType.Cure);
        cure.More2 = 600;
        Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 2000);
        r.Run(10, () => mind.Mode == BotMode.Fight);
        bot.SetStatFlag(StatFlag.Poisoned);
        r.Run(40, () => cure.Amount < 3);
        Assert.Equal(2, cure.Amount);
        // The potion delay holds the next one back.
        bot.SetStatFlag(StatFlag.Poisoned);
        r.Run(30);
        Assert.Equal(2, cure.Amount);
    }

    /// <summary>The engine side of the potions: a bottle whose MORE1 is one of the
    /// Sphere spells (1000 and up - refresh, mana, restore) conveys it when drunk, as
    /// Use_Drink does with any spell number (CCharUse.cpp:1083).</summary>
    [Fact]
    public void APotionOfASphereSpellNumberIsDrunkLikeAnyOther()
    {
        var r = Make(also: [SpellType.Refresh]);
        var player = r.World.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        player.Str = 60; player.Dex = 80; player.MaxStam = 80; player.Stam = 5; player.MaxHits = 60; player.Hits = 60;
        var pack = r.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        r.World.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pc = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), r.World,
            new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        pc.SetEngines(movement: new MovementEngine(r.World), spellEngine: r.Spells);
        pc.AttachServerDriven(player);
        var potion = Give(r, player, 0x0F0B, ItemType.Potion, 2, more1: (uint)SpellType.Refresh);
        potion.More2 = 1000;
        Assert.Equal(SpellType.Refresh, PlayerBotEngine.PotionSpell(potion));

        pc.HandleDoubleClick(potion.Uid.Value);
        Assert.Equal(1, potion.Amount);
        Assert.True(player.Stam > 5, $"stamina still {player.Stam}");
    }

    // ------------------------------------------------------------------
    //  An archer
    // ------------------------------------------------------------------

    private static Item Bow(Rig r, Character ch)
    {
        var bow = Weapon(r, BowId, ItemType.WeaponBow, Layer.TwoHanded, 9, 25, twoHands: true);
        Assert.True(ch.Equip(bow, Layer.TwoHanded));
        return bow;
    }

    [Fact]
    public void AnArcherStepsBackFromAFoeThatCloses()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "archer", ch =>
        {
            Bow(r, ch);
            Give(r, ch, 0x0F3F, ItemType.WeaponArrow, 50);
        });
        var foe = Monster(r, new Point3D(102, 100, 0, 0), bot, hits: 2000);
        r.Run(20, () => mind.Mode == BotMode.Fight);
        Assert.Equal(BotMode.Fight, mind.Mode);
        // The foe two tiles off and the bow ready: it stands and looses.
        r.World.MoveCharacter(foe, new Point3D((short)(bot.X + 2), bot.Y, bot.Z, 0));
        bot.ClearPendingHit();
        bot.NextAttackTime = 0;
        bot.SetCombatSwingState(SwingState.Ready); // the delay of taking the bow up is over
        r.Run(30);
        Assert.Equal(2, Dist(bot, foe));
        Assert.True(bot.HasPendingHit || bot.NextAttackTime > Environment.TickCount64, $"no shot was loosed: target {bot.FightTarget}, war {bot.IsInWarMode}, next {bot.NextAttackTime - Environment.TickCount64}, swing {bot.CombatSwingState}{Environment.NewLine}{r.Trace(bot)}");
        // The arrow is gone and the next is not nocked yet (the swing timer runs on the
        // real clock): it uses the time to get its distance back.
        bot.ClearPendingHit();
        bot.NextAttackTime = Environment.TickCount64 + 60_000;
        r.Run(60, () => Dist(bot, foe) >= 4, each: bot.ClearPendingHit);
        Assert.True(Dist(bot, foe) >= 4, $"stands {Dist(bot, foe)} off{Environment.NewLine}{r.Trace(bot)}");
        // Ready again: it stands, turns back to its foe (it stepped away with its back
        // to it; where the rules ask an attacker to face its target no shot starts
        // otherwise) and shoots rather than running off.
        Character.CombatFlags |= (int)CombatFlags.FaceCombat;
        bot.NextAttackTime = 0;
        bot.SetCombatSwingState(SwingState.Ready);
        int at = Dist(bot, foe);
        r.Run(40);
        Assert.Equal(at, Dist(bot, foe));
        Assert.Equal(BotMode.Fight, mind.Mode);
        Assert.Equal(bot.Position.GetDirectionTo(foe.Position), bot.Direction);
        Assert.True(bot.HasPendingHit || bot.NextAttackTime > Environment.TickCount64, "no second shot was loosed");
    }

    [Fact]
    public void AnArcherOutOfArrowsTakesTheBladeItCarries()
    {
        var r = Make();
        Item? bow = null, dagger = null;
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "archer", ch =>
        {
            ch.SetSkill(SkillType.Fencing, 500);
            bow = Bow(r, ch);
            dagger = Weapon(r, DaggerId, ItemType.WeaponFence, Layer.OneHanded, 3, 9);
            Assert.True(ch.Backpack!.TryAddItem(dagger));
        });
        var foe = Monster(r, new Point3D(104, 100, 0, 0), bot, hits: 2000);
        foe.SetStatFlag(StatFlag.Freeze);
        r.Run(150, () => bot.GetEquippedItem(Layer.OneHanded) == dagger && Dist(bot, foe) <= 1);
        Assert.True(bot.GetEquippedItem(Layer.OneHanded) == dagger, r.Trace(bot));
        Assert.Equal(bot.Backpack!.Uid, bow!.ContainedIn);
        Assert.Equal(BotMode.Fight, mind.Mode);
        Assert.True(Dist(bot, foe) <= 1, $"stands {Dist(bot, foe)} off{Environment.NewLine}{r.Trace(bot)}");
        Assert.True(mind.Tac.OutOfAmmo);
    }

    [Fact]
    public void AnArcherWithNeitherArrowsNorBladeLeavesAndWantsArrows()
    {
        var r = Make();
        r.Engine.AddNeed(new BotNeed("arrow", ItemType.WeaponArrow, 30, 100, ["VendorBowyer"]));
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "archer", ch => Bow(r, ch));
        var foe = Monster(r, new Point3D(104, 100, 0, 0), bot, hits: 2000);
        r.Run(60, () => mind.Mode == BotMode.Flee);
        Assert.True(mind.Mode == BotMode.Flee, r.Trace(bot));
        Assert.Contains(r.Engine.TraceOf(bot.Uid.Value.ToString()), l => l.Contains("out of ammunition"));
        Assert.Equal("arrow", r.Engine.FirstNeed(mind)?.Name);
        // It never stands at range with an empty bow.
        r.Run(80);
        Assert.True(Dist(bot, foe) >= 8, $"only {Dist(bot, foe)} away{Environment.NewLine}{r.Trace(bot)}");

        // Nor does it pick a fight like that.
        var r2 = Make();
        var (hunter, hm) = Bot(r2, new Point3D(100, 100, 0, 0), "archer", ch => Bow(r2, ch));
        Monster(r2, new Point3D(104, 100, 0, 0), null, hits: 20);
        r2.Run(150);
        Assert.NotEqual(BotMode.Fight, hm.Mode);
        Assert.NotNull(hunter);
    }

    // ------------------------------------------------------------------
    //  Prey, memory, other people's fights
    // ------------------------------------------------------------------

    [Fact]
    public void PreyIsJudgedByWhatItDoesNotByItsHitPointsAlone()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 1.2);
        var sword = Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 10, 24);
        Assert.True(bot.Equip(sword, Layer.OneHanded));
        // Many hit points, a feeble bite: fair game. Few, a terrible one: not.
        var cow = Monster(r, new Point3D(104, 100, 0, 0), null, hits: 250, name: "cow", damLo: 1, damHi: 3, skill: 200);
        var wisp = Monster(r, new Point3D(100, 104, 0, 0), null, hits: 90, name: "wisp", damLo: 40, damHi: 60, skill: 1000);
        Assert.True(r.Engine.TakesOn(mind, cow, r.Now));
        Assert.False(r.Engine.TakesOn(mind, wisp, r.Now));
        // A caster or a poisoner of the same build is the worse of two.
        var plain = r.Engine.ThreatOf(wisp);
        wisp.SetSkill(SkillType.Magery, 900);
        wisp.MaxMana = 80;
        wisp.SetSkill(SkillType.Poisoning, 600);
        var nasty = r.Engine.ThreatOf(wisp);
        Assert.True(nasty.Casts && nasty.Poisons && nasty.Dps > plain.Dps);

        r.Run(300, () => mind.Mode == BotMode.Fight);
        Assert.Same(cow, mind.Foe);
        // The more nerve, the more even a fight it takes.
        var even = Monster(r, new Point3D(96, 100, 0, 0), null, hits: 160, name: "ettin", damLo: 12, damHi: 20, skill: 800);
        mind.Tac.Power = null;
        mind.Nerve = 0.8;
        bool timid = r.Engine.TakesOn(mind, even, r.Now);
        mind.Nerve = 2.0;
        bool bold = r.Engine.TakesOn(mind, even, r.Now);
        Assert.True(bold && !timid, $"timid {timid}, bold {bold}");
    }

    [Fact]
    public void WhatKilledItIsRememberedAcrossARestartAndLeftAloneUntilItIsStronger()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 2.0);
        var troll = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 60, name: "troll");
        r.Run(20, () => mind.Mode == BotMode.Fight);
        Assert.Equal("troll", mind.Tac.FoeKind);

        // It dies of it (the death hook, as the death engine calls it).
        r.Engine.OnDeath(bot, troll);
        Assert.True(bot.TryGetTag(PlayerBotEngine.KillersTag, out string? tag));
        Assert.StartsWith("troll:", tag);
        Assert.True(r.Engine.Dreads(mind, "troll", r.Now));

        // A restart: the mind is new, the tag is what is left.
        r.Engine.Release(bot);
        troll.FightTarget = Serial.Invalid;
        r.Engine.AdoptSaved();
        var again = r.Engine.MindOf(bot)!;
        again.LingerUntilMs = long.MaxValue;
        again.NextGearCheckMs = long.MaxValue;
        again.Nerve = 2.0;
        Assert.NotSame(mind, again);
        Assert.True(r.Engine.Dreads(again, "troll", r.Now));
        // An easy troll stands right there and is not hunted; another kind is.
        Assert.True(r.Engine.TakesOn(again, troll, r.Now));
        r.Run(150);
        Assert.NotEqual(BotMode.Fight, again.Mode);
        var rat = Monster(r, new Point3D(100, 103, 0, 0), null, hits: 30, name: "rat");
        r.Run(300, () => again.Foe == rat);
        Assert.Same(rat, again.Foe);

        // Clearly stronger than when it died: no longer dreaded.
        var sword = Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 30, 60);
        Assert.True(bot.Equip(sword, Layer.OneHanded));
        again.Tac.Power = null;
        Assert.False(r.Engine.Dreads(again, "troll", r.Now));
    }

    [Fact]
    public void TheKindThatKilledItIsGivenLessRope()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 1.4, fleeAt: 20);
        var troll = Monster(r, new Point3D(101, 100, 0, 0), bot, hits: 300, name: "troll");
        r.Engine.RememberKiller(mind, troll, r.Now);
        mind.Foe = troll;
        mind.Tac.Attackers = 1;
        bot.Hits = 40;
        // Nothing to heal with: its mark is 30. A stranger is fought on at 40...
        mind.Tac.FoeKind = "ettin";
        Assert.Null(r.Engine.WhyRetreat(mind, troll, r.Now, 40));
        // ...the kind it died of is not.
        mind.Tac.FoeKind = "troll";
        Assert.Equal("badly hurt", r.Engine.WhyRetreat(mind, troll, r.Now, 40));
    }

    [Fact]
    public void TheMemoryOfKillersIsBoundedAndBeatenKindsArePreferred()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior", nerve: 2.0);
        for (int i = 0; i < 10; i++)
        {
            var m = Monster(r, new Point3D(120, 120, 0, 0), null, name: "kind" + i);
            r.Engine.RememberKiller(mind, m, r.Now);
            r.World.DeleteObject(m);
        }
        Assert.True(bot.TryGetTag(PlayerBotEngine.KillersTag, out string? tag));
        var kinds = tag!.Split(';');
        Assert.Equal(6, kinds.Length);
        Assert.StartsWith("kind4:", kinds[0]);
        Assert.StartsWith("kind9:", kinds[^1]);

        // Two of a size: the kind it has beaten before, though the other is nearer.
        var beaten = Monster(r, new Point3D(105, 100, 0, 0), bot, hits: 30, name: "mongbat");
        r.Run(20, () => mind.Mode == BotMode.Fight);
        beaten.Hits = 0;
        beaten.SetStatFlag(StatFlag.Dead);
        r.Run(20, () => mind.Mode != BotMode.Fight);
        Assert.True(bot.TryGetTag(PlayerBotEngine.BeatenTag, out string? won));
        Assert.Equal("mongbat:1", won);
        Assert.True(r.Engine.HasBeaten(mind, "mongbat"));
        r.World.DeleteObject(beaten);

        var known = Monster(r, new Point3D((short)(bot.X + 5), bot.Y, 0, 0), null, hits: 30, name: "mongbat");
        var stranger = Monster(r, new Point3D((short)(bot.X - 3), bot.Y, 0, 0), null, hits: 30, name: "slime");
        r.Run(400, () => mind.Mode == BotMode.Fight);
        Assert.True(mind.Foe == known, r.Trace(bot));
        Assert.NotNull(stranger);
    }

    [Fact]
    public void ABoldBotJoinsAHardPressedFightAndATimidOneKeepsClear()
    {
        var lines = new Dictionary<string, string[]> { ["combat_assist"] = ["hold on!"] };
        var r = Make(lines: lines);
        var (victim, vm) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        vm.NextScanMs = long.MaxValue; // it stands and takes it
        var ogre = Monster(r, new Point3D(101, 100, 0, 0), victim, hits: 120, name: "ogre", damLo: 6, damHi: 12, skill: 600);
        var (bold, bm) = Bot(r, new Point3D(100, 105, 0, 0), "warrior", nerve: 1.9, setup: ch =>
            Assert.True(ch.Equip(Weapon(r, SwordId, ItemType.WeaponSword, Layer.OneHanded, 10, 24), Layer.OneHanded)));
        var (timid, tm) = Bot(r, new Point3D(103, 103, 0, 0), "warrior", nerve: 0.85);
        // Too much for the timid one alone, so it has no wish to be near it.
        ogre.SetAttackRating(40, 60);
        ogre.SetSkill(SkillType.Wrestling, 1000);
        tm.Tac.Power = null;
        Assert.False(r.Engine.TakesOn(tm, ogre, r.Now));
        ogre.SetAttackRating(6, 12);
        ogre.SetSkill(SkillType.Wrestling, 600);

        r.Run(100, () => bm.Foe == ogre);
        Assert.True(bm.Foe == ogre, r.Trace(bold));
        Assert.True(bm.Tac.Assisting);
        Assert.True(r.Engine.WindowAssists >= 1);
        Assert.NotEqual(ogre, tm.Foe);
        Assert.NotNull(timid);
    }

    [Fact]
    public void ATimidBotStepsAwayFromAFightItWouldNotTake()
    {
        var r = Make();
        var (victim, vm) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        vm.NextScanMs = long.MaxValue;
        var ogre = Monster(r, new Point3D(101, 100, 0, 0), victim, hits: 600, name: "ogre lord", damLo: 40, damHi: 60, skill: 1000);
        var (timid, tm) = Bot(r, new Point3D(103, 102, 0, 0), "warrior", nerve: 0.85);
        int before = Dist(timid, ogre);
        r.Run(100, () => Dist(timid, ogre) >= before + 4);
        Assert.True(Dist(timid, ogre) >= before + 4, r.Trace(timid));
        Assert.NotEqual(BotMode.Fight, tm.Mode);
        // And then gets on with its life.
        r.Run(120, () => tm.Mode == BotMode.Linger);
        Assert.Equal(BotMode.Linger, tm.Mode);
    }

    [Fact]
    public void APlayerWhoHasItsFightInHandIsLeftToIt()
    {
        var r = Make();
        var player = r.World.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Hero";
        player.MaxHits = 100; player.Hits = 100;
        r.World.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        r.World.AddOnlinePlayer(player);
        var pc = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), r.World,
            new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
        pc.AttachServerDriven(player);
        var orc = Monster(r, new Point3D(101, 100, 0, 0), player, hits: 80);
        player.FightTarget = orc.Uid;
        player.SetStatFlag(StatFlag.War);
        var (bold, bm) = Bot(r, new Point3D(100, 104, 0, 0), "warrior", nerve: 1.9);
        if (!player.HasClient) return; // a harness without the client registry cannot tell
        r.Run(80);
        Assert.NotEqual(orc, bm.Foe);
        // Hard pressed: now it is help, not a stolen kill.
        player.Hits = 40;
        r.Run(100, () => bm.Foe == orc);
        Assert.True(bm.Foe == orc, r.Trace(bold));
    }

    // ------------------------------------------------------------------
    //  Getting away
    // ------------------------------------------------------------------

    [Fact]
    public void ABotBreakingOffNearATownRunsForItsGuardsAndCallsThem()
    {
        var lines = new Dictionary<string, string[]> { ["guards_call"] = ["guards!"] };
        var r = Make(town: true, lines: lines);
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        var foe = Monster(r, new Point3D(99, 100, 0, 0), bot, hits: 2000);
        r.Run(10, () => mind.Mode == BotMode.Fight);
        bot.Hits = 8;
        // The orc follows it, as it would.
        r.Run(200, () => r.Said.Any(s => s.Category == "guards_call"), each: () =>
        {
            if (Dist(bot, foe) > 2)
                r.World.MoveCharacter(foe, new Point3D((short)(bot.X - 2), bot.Y, bot.Z, 0));
        });
        Assert.Equal(1, r.Engine.WindowGuardRuns);
        Assert.True(r.World.FindRegion(bot.Position)?.IsGuarded == true, $"at {bot.Position}{Environment.NewLine}{r.Trace(bot)}");
        Assert.Contains(r.Said, s => s.Category == "guards_call");
    }

    [Fact]
    public void WithItsPursuerOutOfSightItHides()
    {
        var r = Make();
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "archer", ch => ch.SetSkill(SkillType.Hiding, 1000));
        var foe = Monster(r, new Point3D(99, 100, 0, 0), bot, hits: 2000);
        foe.SetStatFlag(StatFlag.Freeze);
        // A stretch of wall across the road east of it: it has to go round the end,
        // and once behind it the thing after it cannot see it.
        for (int y = 96; y <= 104; y++)
            r.World.MapData!.AddSyntheticStatic(0, 103, y, WallTile, 0);
        r.Run(10, () => mind.Mode == BotMode.Fight);
        bot.Hits = 8;
        r.Run(300, () => r.Engine.WindowHides > 0);
        Assert.True(r.Engine.WindowHides > 0, r.Trace(bot));
        Assert.False(r.World.CanSeeLOS(foe.Position, bot.Position));
    }

    // ------------------------------------------------------------------
    //  Player killers fight the same way
    // ------------------------------------------------------------------

    [Fact]
    public void APlayerKillerMageUsesItsRepertoireOnItsVictim()
    {
        var r = Make();
        r.Engine.PkSpareSkill = 0;
        var (pk, mind) = Bot(r, new Point3D(100, 100, 0, 0), "mage", ch => ch.SetTag(PlayerBotEngine.RoleTag, "pk"));
        var victim = r.World.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Name = "Lonely";
        victim.MaxHits = 2000; victim.Hits = 2000;
        r.World.PlaceCharacter(victim, new Point3D(106, 100, 0, 0));
        r.World.AddOnlinePlayer(victim);
        r.Run(400, () => r.CastsOf(pk).Count >= 3);
        var casts = r.CastsOf(pk);
        Assert.True(casts.Count >= 3, r.Trace(pk));
        Assert.Same(victim, mind.Foe);
        Assert.Contains(casts, s => s is SpellType.Poison or SpellType.Explosion or SpellType.Flamestrike or SpellType.EnergyBolt);
    }

    // ------------------------------------------------------------------
    //  Shopping for what a fight uses up
    // ------------------------------------------------------------------

    [Fact]
    public void PotionNeedsNameTheSpellInTheBottle()
    {
        var r = Make();
        string dir = Path.Combine(Path.GetTempPath(), "sn_botcombat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "shopping.txt"),
            [
                "heal  Potion:GreaterHeal  2  3  VendorAlchemist",
                "cure  Potion:Cure  1  2  VendorAlchemist",
            ]);
            r.Engine.LoadShopping(dir);
        }
        finally { Directory.Delete(dir, true); }
        var (bot, mind) = Bot(r, new Point3D(100, 100, 0, 0), "warrior");
        Assert.Equal("heal", r.Engine.FirstNeed(mind)?.Name);
        // A lesser heal bottle counts as a heal potion; a cure does not.
        Give(r, bot, 0x0F0C, ItemType.Potion, 2, more1: (uint)SpellType.Heal);
        Give(r, bot, 0x0F06, ItemType.Potion, 5, more1: (uint)SpellType.NightSight);
        Assert.Equal("cure", r.Engine.FirstNeed(mind)?.Name);
        var cure = Give(r, bot, 0x0F07, ItemType.Potion, 1, more1: (uint)SpellType.Cure);
        Assert.Null(r.Engine.FirstNeed(mind));
        Assert.True(PlayerBotEngine.AnswersNeed(new BotNeed("cure", ItemType.Potion, 1, 2, [], SpellType.Cure), cure));
        // A mage cures and heals by spell: no bottles on its list.
        var (mage, mm) = Bot(r, new Point3D(100, 102, 0, 0), "mage");
        Assert.Null(r.Engine.FirstNeed(mm));
        Assert.NotNull(mage);
    }

    /// <summary>The shipped kits against the Scripts-X pack they are written for: a
    /// warrior carries a weapon for each of its two weapon skills and the potions it
    /// drinks, an archer a blade beside its bow - and each bottle names the spell the
    /// bot takes it for (the pack's refresh potion is Sphere spell 1008). Skips when the
    /// pack is not in the repository.</summary>
    [Theory]
    [InlineData("c_playerbot_warrior_m")]
    [InlineData("c_playerbot_warrior_f")]
    [InlineData("c_playerbot_archer_m")]
    [InlineData("c_playerbot_archer_f")]
    public void TheShippedKitsCarryWhatTheFightingUses(string archetype)
    {
        string? pack = TestRepo.Optional("oldSphere/Scripts-X-main");
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var lf = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            lf.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>()) { ScpBaseDir = pack! };
        foreach (string file in Directory.EnumerateFiles(pack!, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        foreach (string file in Directory.EnumerateFiles(TestRepo.Tracked("playerbots/scripts"), "*.scp"))
            resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(interpreter, resources,
            lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = defname =>
        {
            var id = resources.ResolveDefName(defname);
            if (!id.IsValid || id.Type != ResType.ItemDef) return 0;
            var d = DefinitionLoader.GetItemDef(id.Index);
            return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)id.Index;
        };
        try
        {
            var rid = resources.ResolveDefName(archetype);
            Assert.True(rid.IsValid);
            var ch = world.CreateCharacter();
            ch.CharDefIndex = rid.Index;
            ch.BaseId = 0x190; ch.BodyId = 0x190;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            dispatcher.FireNpcLoadScript(ch, restock: true);
            var things = Enum.GetValues<Layer>().Select(ch.GetEquippedItem).OfType<Item>()
                .Concat(world.GetContainerContents(ch.Backpack?.Uid ?? Serial.Invalid))
                .ToList();
            var potions = things.Where(i => i.ItemType == ItemType.Potion).Select(PlayerBotEngine.PotionSpell).ToHashSet();
            var weaponSkills = things.Where(i => i.IsWeaponType).Select(i => CombatEngine.GetWeaponSkill(ch, i)).ToHashSet();
            outp.WriteLine($"{archetype}: potions {string.Join(",", potions)}; weapon skills {string.Join(",", weaponSkills)}");
            Assert.Contains(SpellType.GreaterHeal, potions);
            Assert.Contains(SpellType.Refresh, potions);
            if (archetype.Contains("warrior"))
            {
                Assert.Contains(SpellType.Cure, potions);
                Assert.Contains(SpellType.Strength, potions);
                Assert.Contains(SkillType.Swordsmanship, weaponSkills);
                Assert.True(weaponSkills.Count >= 2, "one weapon skill only");
            }
            else
            {
                Assert.Contains(SpellType.Agility, potions);
                Assert.Contains(SkillType.Archery, weaponSkills);
                Assert.Contains(SkillType.Fencing, weaponSkills);
                Assert.True(ch.GetSkill(SkillType.Fencing) >= 300);
            }
        }
        finally
        {
            SphereNet.Game.Objects.ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
            Item.ResolveDefName = null;
        }
    }
}
