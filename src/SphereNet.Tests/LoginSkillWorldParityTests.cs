using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Character creation, login, skill and sector-environment rules read off the
/// Source-X reference; each test names the line it is taken from.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LoginSkillWorldParityTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string f in _files)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private ResourceHolder Load(string contents)
    {
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_lsw_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, contents);
        _files.Add(file);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? ""
        };
        resources.LoadResourceFile(file);
        return resources;
    }

    private sealed class Sink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; }
        public GameWorld World { get; }
        public List<string> Messages { get; } = new();
        public List<ushort> Sounds { get; } = new();
        public List<Item> Consumed { get; } = new();
        public Dictionary<ItemType, Item> Pack { get; } = new();

        public Sink(Character self, GameWorld world)
        {
            Self = self; World = world; Random = new Random(7);
        }

        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Messages.Add(text);
        public void Emote(string text) => Messages.Add(text);
        public void Sound(ushort soundId) => Sounds.Add(soundId);
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) => Consumed.Add(item);
        public void DeliverItem(Item item) { }
    }

    private static Character Place(GameWorld world, short x = 100, short y = 100, bool player = true,
        ushort body = 0x0190)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.BodyId = body;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static Character Create(CharCreateInfo info, string? name = null)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, 19960);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.SetPrivateField(client, "_account", new Account { Name = "parity" });
        client.PendingCharCreate = info;
        client.HandleCharSelect(-1, name ?? info.Name);
        return client.Character!;
    }

    // ------------------------------------------------------------ creation

    /// <summary>[NEWBIE name ELF|GARG] is the race's own block
    /// (CServerConfig.cpp:4304-4322) and is asked for first; the plain one is the
    /// fallback (CChar.cpp:2103-2106). Creation used to read the plain block for
    /// every race.</summary>
    [Fact]
    public void NewbieBlocks_AreFiledUnderTheirRace()
    {
        var r = Load("""
            [NEWBIE MALE_DEFAULT]
            ITEM=i_shirt_plain

            [NEWBIE MALE_DEFAULT ELF]
            ITEM=i_elven_shirt

            [NEWBIE MALE_DEFAULT GARG]
            ITEM=i_gargish_robe

            [NEWBIE PROFESSION_NINJA]
            ITEM=i_ninja_hood
            """);

        string Item(string section, int race) =>
            GameClient.ResolveNewbieSection(r, section, race)!.StoredKeys!.Single().Arg;

        Assert.Equal("i_shirt_plain", Item("MALE_DEFAULT", 1));
        Assert.Equal("i_elven_shirt", Item("MALE_DEFAULT", 2));
        Assert.Equal("i_gargish_robe", Item("MALE_DEFAULT", 3));

        // A block with no race variant: every race takes the plain one (the fallback
        // of CChar.cpp:2110-2113).
        Assert.Equal("i_ninja_hood", Item("PROFESSION_NINJA", 1));
        Assert.Equal("i_ninja_hood", Item("PROFESSION_NINJA", 3));
        Assert.Null(GameClient.ResolveNewbieSection(r, "PROFESSION_MAGE", 1));
    }

    /// <summary>CChar.cpp:2069-2099: the profession names its block, and necromancer,
    /// paladin, samurai and ninja switch the skill kits off.</summary>
    [Fact]
    public void Professions_NameTheirBlock_AndFourSuppressSkillKits()
    {
        Assert.Equal("PROFESSION_ADVANCED", GameClient.NewbieProfessionSection(0));
        Assert.Equal("PROFESSION_WARRIOR", GameClient.NewbieProfessionSection(1));
        Assert.Equal("PROFESSION_NINJA", GameClient.NewbieProfessionSection(7));
        Assert.Null(GameClient.NewbieProfessionSection(8));

        for (int prof = 0; prof <= 3; prof++)
            Assert.False(GameClient.ProfessionSuppressesSkillKits(prof));
        for (int prof = 4; prof <= 7; prof++)
            Assert.True(GameClient.ProfessionSuppressesSkillKits(prof));
    }

    /// <summary>CChar.cpp:1768-1773 then :1804-1814: every defined skill starts at
    /// Rand(MAXBASESKILL), and the chosen ones are written over it - a chosen 0
    /// included.</summary>
    [Fact]
    public void UnchosenSkills_StartBelowMaxBaseSkill_ChosenOnesAreExact()
    {
        for (int i = 0; i <= (int)SkillType.Throwing; i++)
            DefinitionLoader.SetSkillDef(i, new SkillDef(new ResourceId(ResType.SkillDef, i)));
        GameClient.MaxBaseSkill = 200;

        bool sawNonZero = false;
        for (int round = 0; round < 8; round++)
        {
            var ch = Create(new CharCreateInfo
            {
                Name = "Skilled", Race = 1, Str = 30, Dex = 30, Int = 20,
                Skills = [((byte)SkillType.Magery, 50), ((byte)SkillType.Hiding, 30), ((byte)SkillType.Anatomy, 0)],
            });
            Assert.Equal(500, ch.GetSkill(SkillType.Magery));
            Assert.Equal(300, ch.GetSkill(SkillType.Hiding));
            Assert.Equal(0, ch.GetSkill(SkillType.Anatomy));
            for (int i = 0; i <= (int)SkillType.Throwing; i++)
            {
                if ((SkillType)i is SkillType.Magery or SkillType.Hiding or SkillType.Anatomy) continue;
                Assert.InRange(ch.GetSkill((SkillType)i), 0, 199);
                sawNonZero |= ch.GetSkill((SkillType)i) > 0;
            }
        }
        Assert.True(sawNonZero);

        // MAXBASESKILL=0 leaves them at zero.
        GameClient.MaxBaseSkill = 0;
        var flat = Create(new CharCreateInfo { Name = "Flat", Race = 1, Str = 30, Dex = 30, Int = 20, Skills = [] });
        Assert.Equal(0, flat.GetSkill(SkillType.Tactics));
    }

    /// <summary>CChar.cpp:1737-1742 and Str_Untrusted_InvalidName (sstring.cpp:1667).</summary>
    [Theory]
    [InlineData("Aldric", true)]
    [InlineData("Jean-Luc O'Neil Jr.", true)]
    [InlineData("", false)]
    [InlineData("Bad_Name", false)]
    [InlineData("Ünal", false)]
    [InlineData("Lord British", false)]
    [InlineData("lady Jane", false)]
    [InlineData("SEER Bob", false)]
    [InlineData("gm Bob", false)]
    [InlineData("Admin Bob", false)]
    [InlineData("Counselor Bob", false)]
    [InlineData("Lordly", true)]
    [InlineData("Gmork", true)]
    public void CreationName_Rules(string name, bool accepted)
    {
        Assert.Equal(accepted, GameClient.IsCreateNameAccepted(name, null));
    }

    [Fact]
    public void CreationName_ObsceneIsRefused()
    {
        Assert.False(GameClient.IsCreateNameAccepted("Darnit", s => s.Contains("darn", StringComparison.OrdinalIgnoreCase)));
        Assert.True(GameClient.IsCreateNameAccepted("Daniel", s => s.Contains("darn", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A refused name becomes a pick from the human name pool
    /// (CChar.cpp:1754-1757), not the name with its bad characters dropped.</summary>
    [Fact]
    public void RefusedCreationName_IsReplacedByTheNamePool()
    {
        var info = new CharCreateInfo { Name = "Lord Evil", Race = 1, Str = 30, Dex = 30, Int = 20, Skills = [] };
        var ch = Create(info);
        Assert.NotEqual("Lord Evil", ch.Name);
        Assert.Contains("NAMES_HUMANMALE", ch.Name);   // no pool loaded: its bare name (SetNamePool_Fail)

        var female = Create(new CharCreateInfo
        {
            Name = "x_x", Female = true, Race = 1, Str = 30, Dex = 30, Int = 20, Skills = [],
        });
        Assert.Contains("NAMES_HUMANFEMALE", female.Name);

        var fine = Create(new CharCreateInfo { Name = "Aldric", Race = 1, Str = 30, Dex = 30, Int = 20, Skills = [] });
        Assert.Equal("Aldric", fine.Name);
    }

    /// <summary>addTime(true) at login (CClientMsg.cpp:1505): 0x5B with the game
    /// clock's hour, minute and second.</summary>
    [Fact]
    public void Login_SendsTheGameTime()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.SetGameClockMs((5 * 3600 + 7 * 60 + 9) * 1000L);
        var state = TestHarness.CreateActiveNetState(lf, 19961);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        TestHarness.SetPrivateField(client, "_account", new Account { Name = "clock" });
        client.PendingCharCreate = new CharCreateInfo { Name = "Clock", Race = 1, Str = 30, Dex = 30, Int = 20, Skills = [] };
        client.HandleCharSelect(-1, "Clock");

        var time = TestHarness.GetQueuedPackets(state).Select(p => p.Span.ToArray())
            .FirstOrDefault(p => p.Length == 4 && p[0] == 0x5B);
        Assert.NotNull(time);
        Assert.Equal(5, time![1]);
        Assert.Equal(7, time[2]);
        Assert.InRange(time[3], 9, 12);
    }

    // -------------------------------------------------------------- skills

    /// <summary>4*60*MSECS_PER_TENTH tenths, times MSECS_PER_TENTH (CCharSkill.cpp:2644,
    /// CCharSpell.cpp:2139): forty minutes, not twenty-four seconds.</summary>
    [Fact]
    public void SpiritSpeak_LastsFortyMinutes()
    {
        Assert.Equal(2_400_000L, ActiveSkillEngine.SpiritSpeakDurationMs);

        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        ch.SetSkill(SkillType.SpiritSpeak, 1000);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.True(ActiveSkillEngine.SpiritSpeak(new Sink(ch, world), startDifficulty: 10));

        Assert.True(ch.TryGetTag("SPIRITSPEAK_UNTIL", out string? raw));
        long until = long.Parse(raw!);
        Assert.InRange(until - before, 2_400_000L, 2_400_000L + 5_000);
    }

    private static (GameWorld World, Character Tamer, Character Animal, Sink Sink) TamingSetup()
    {
        var world = TestHarness.CreateWorld();
        var tamer = Place(world);
        tamer.SetSkill(SkillType.Taming, 1000);
        var animal = Place(world, 101, 100, player: false, body: 0x00E1);
        animal.SetSkill(SkillType.Taming, 300);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        return (world, tamer, animal, new Sink(tamer, world));
    }

    /// <summary>m_Act_UID = the tamer; Skill_Start(NPCACT_FOLLOW_TARG)
    /// (CCharSkill.cpp:2397-2398): a tamed creature stops fighting and follows.</summary>
    [Fact]
    public void TamedCreature_DropsItsFight_AndFollowsTheTamer()
    {
        var (world, tamer, animal, sink) = TamingSetup();
        var foe = Place(world, 102, 100, player: false, body: 0x00E1);
        animal.PetAIMode = PetAIMode.Stay;
        animal.FightTarget = foe.Uid;
        animal.SetStatFlag(StatFlag.War);

        Assert.True(ActiveSkillEngine.Taming(sink, animal, startDifficulty: 10));

        Assert.True(animal.HasOwner(tamer.Uid));
        Assert.Equal(PetAIMode.Follow, animal.PetAIMode);
        Assert.False(animal.FightTarget.IsValid);
        Assert.False(animal.IsStatFlag(StatFlag.War));
        Assert.True(animal.TryGetTag("FOLLOW_TARGET", out string? follow));
        Assert.Equal(tamer.Uid.Value.ToString(), follow);
    }

    /// <summary>OF_PetSlots with no room: DEFMSG_PETSLOTS_TRY_TAMING before anything
    /// is rolled (CCharSkill.cpp:2332-2340).</summary>
    [Fact]
    public void Taming_WithFullPetSlots_IsRefusedUpFront()
    {
        var (_, tamer, animal, sink) = TamingSetup();
        var rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, _) => { rolls++; return 1; };
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
        tamer.MaxFollower = 0;

        Assert.Equal(-3, ActiveSkillEngine.TamingStart(sink, animal));
        Assert.False(ActiveSkillEngine.Taming(sink, animal, startDifficulty: 10));

        Assert.Contains(ServerMessages.Get(Msg.PetslotsTryTaming), sink.Messages);
        Assert.Equal(0, rolls);
        Assert.False(animal.HasOwner(tamer.Uid));

        // With the slot system off the cap does not bind (OF_PetSlots).
        GameClient.ServerOptionFlags &= ~OptionFlags.PetSlots;
        sink.Messages.Clear();
        Assert.True(ActiveSkillEngine.Taming(sink, animal, startDifficulty: 10));
    }

    /// <summary>NPC_WalkToPoint is one step (CCharSkill.cpp:1988): the creature is sent
    /// to the tile beside it on the way to the bard, not all the way.</summary>
    [Fact]
    public void Enticement_MovesTheCreatureOneStep()
    {
        var world = TestHarness.CreateWorld();
        var bard = Place(world, 100, 100);
        bard.SetSkill(SkillType.Enticement, 1000);
        var creature = Place(world, 106, 103, player: false, body: 0x00E1);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        var ai = new SphereNet.Game.AI.NpcAI(world, new SphereConfig());
        ActiveSkillEngine.NpcWalkToPoint = ai.WalkOneStepToward;

        Assert.True(ActiveSkillEngine.Enticement(new Sink(bard, world), creature, startDifficulty: 10));

        Assert.Equal(bard.Position, creature.ActP);                 // m_Act_p = the bard
        Assert.Equal(new Point3D(105, 102, 0, 0), creature.Position);   // one tile closer
        Assert.Equal(SkillType.None, creature.Action);              // and no walk left running
    }

    /// <summary>Skill_Poisoning (CCharSkill.cpp:2153-2213) asks only for a poison potion
    /// and a thing it coats; neither has to be carried.</summary>
    [Fact]
    public void Poisoning_HasNoPossessionRule()
    {
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var potion = world.CreateItem();
        potion.ItemType = ItemType.Potion;
        potion.SetTag("POTION_SPELL", "Poison");
        potion.More2 = 400;
        world.PlaceItem(potion, new Point3D(101, 100, 0, 0));
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        world.PlaceItem(sword, new Point3D(101, 101, 0, 0));
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        var sink = new Sink(ch, world);

        Assert.True(ActiveSkillEngine.Poisoning(sink, sword, potion));

        Assert.Equal(40, sword.MoreP.Z);
        Assert.DoesNotContain(sink.Messages, m => m.Contains("possession", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ServerMessages.Get(Msg.PoisoningSuccess), sink.Messages);
    }

    /// <summary>Event_Skill_Use has no case for Herding or Musicianship
    /// (CClientEvent.cpp:670-725): the button gets the default answer.</summary>
    [Fact]
    public void HerdingAndMusicianship_HaveNoSkillButton()
    {
        Assert.False(SkillHandlers.IsClientUsable(SkillType.Herding));
        Assert.False(SkillHandlers.IsClientUsable(SkillType.Musicianship));
        Assert.True(SkillHandlers.IsClientUsable(SkillType.Peacemaking));
        Assert.True(SkillHandlers.IsClientUsable(SkillType.Taming));
    }

    /// <summary>IT_CAMPFIRE (CItem.cpp:6403-6412): the fire burns down to embers for two
    /// minutes, and the embers then go.</summary>
    [Fact]
    public void Campfire_BurnsDownToEmbers_ThenDecays()
    {
        var world = TestHarness.CreateWorld();
        var fire = world.CreateItem();
        fire.BaseId = 0x0DE3;
        fire.ItemType = ItemType.Campfire;
        world.PlaceItem(fire, new Point3D(100, 100, 0, 0));
        fire.SetDecayAt(Environment.TickCount64 - 1);

        Assert.True(fire.OnTick());
        Assert.False(fire.IsDeleted);
        Assert.Equal((ushort)0x0DE9, fire.BaseId);
        Assert.Equal(ItemType.Campfire, fire.ItemType);
        Assert.InRange(fire.DecayTime - Environment.TickCount64, 115_000, 120_000);

        fire.SetDecayAt(Environment.TickCount64 - 1);
        Assert.False(fire.OnTick());
        Assert.True(fire.IsDeleted);
    }

    // -------------------------------------------------------------- hunger

    private static void FoodTick(Character ch, int amount, int hungerLoss)
    {
        typeof(Character).GetMethod("ApplyFoodDecay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ch, [amount, hungerLoss]);
    }

    /// <summary>OnTickFood (CCharAct.cpp:5790-5812): at 40% food and below, with
    /// HITSHUNGERLOSS set, the player is told on every food tick and looks hungry;
    /// the damage waits for food 0.</summary>
    [Fact]
    public void HungryPlayer_IsToldOnEveryFoodTick_AndBittenOnlyAtZero()
    {
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var heard = new List<string>();
        Character.SendOwnerMessage = (who, text) => { if (who == ch) heard.Add(text); };
        string hungerPrefix = ServerMessages.Get(Msg.MsgHunger).Split("%s")[0];

        ch.Food = 60;
        FoodTick(ch, 1, hungerLoss: 5);
        Assert.Equal(59, ch.Food);
        Assert.Empty(heard);                       // above 40%

        ch.Food = 21;                              // 20/60 = 33% after the tick
        FoodTick(ch, 1, hungerLoss: 5);
        Assert.Single(heard);
        Assert.StartsWith(hungerPrefix, heard[0]);
        Assert.Equal(100, ch.Hits);                // not starving yet

        FoodTick(ch, 1, hungerLoss: 5);
        Assert.Equal(2, heard.Count);              // every tick

        heard.Clear();
        ch.Food = 21;
        FoodTick(ch, 1, hungerLoss: 0);            // HITSHUNGERLOSS off: silence
        Assert.Empty(heard);

        ch.Food = 1;
        FoodTick(ch, 1, hungerLoss: 5);
        Assert.Equal(0, ch.Food);
        Assert.Single(heard);
        Assert.True(ch.Hits < 100);                // the bite, at food 0
    }

    /// <summary>The guards of OnTickFood (CCharAct.cpp:5773-5780): a GM and the dead
    /// do not get hungrier.</summary>
    [Fact]
    public void GmAndDead_DoNotGetHungrier()
    {
        var world = TestHarness.CreateWorld();
        var gm = Place(world);
        gm.PrivLevel = PrivLevel.GM;
        gm.Food = 30;
        FoodTick(gm, 5, hungerLoss: 5);
        Assert.Equal(30, gm.Food);
    }

    // ------------------------------------------------------------- seasons

    /// <summary>Every sector starts in SEASON_Summer and nothing cycles it
    /// (CSectorEnviron.cpp:8); the ini can still ask for a cycle.</summary>
    [Fact]
    public void Season_DefaultsToAFixedSummer()
    {
        var cfg = new SphereConfig();
        Assert.Equal(SeasonMode.Manual, cfg.SeasonMode);
        Assert.Equal(1, cfg.SeasonDefault);

        var world = TestHarness.CreateWorld();
        Assert.Equal((byte)SeasonType.Summer, world.CurrentSeason);
        var engine = new WeatherEngine(world);
        Assert.Equal(SeasonType.Summer, engine.CurrentSeason);
        Assert.Equal(SeasonMode.Manual, engine.CurrentSeasonMode);

        typeof(WeatherEngine).GetField("_lastSeasonChangeTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, Environment.TickCount64 - 24L * 60 * 60 * 1000);
        Assert.False(engine.OnTick());
        Assert.Equal(SeasonType.Summer, engine.CurrentSeason);

        // SeasonMode=Auto keeps working.
        engine.Configure(SeasonMode.Auto, SeasonType.Spring, intervalMs: 1);
        typeof(WeatherEngine).GetField("_lastSeasonChangeTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, Environment.TickCount64 - 1000);
        Assert.True(engine.OnTick());
        Assert.Equal(SeasonType.Summer, engine.CurrentSeason);
    }

    /// <summary>addSeason(pSector->GetSeason()) (CClientMsg.cpp:1506): a sector's own
    /// season is what the players in it are shown.</summary>
    [Fact]
    public void SectorSeason_WinsOverTheShardWideOne()
    {
        var world = TestHarness.CreateWorld();
        var here = new Point3D(100, 100, 0, 0);
        var far = new Point3D(3000, 2000, 0, 0);
        Assert.Equal((byte)SeasonType.Summer, world.SeasonAt(here));

        world.GetSector(here)!.SetSeason((byte)SeasonType.Winter);

        Assert.Equal((byte)SeasonType.Winter, world.SeasonAt(here));
        Assert.Equal((byte)SeasonType.Summer, world.SeasonAt(far));
    }

    // --------------------------------------------------------------- light

    /// <summary>The sector light moves one level per sector tick toward the calculated
    /// one (CSector.cpp:778-782, :1238), and that stored level is what is sent.</summary>
    [Fact]
    public void SectorLight_FadesOneLevelPerTick()
    {
        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 256, 64);
        world.LightDay = 0;
        world.LightNight = 20;
        var pt = new Point3D(1, 1, 0, 0);
        var sector = world.GetSector(pt)!;

        world.SetWorldClockMinutes(12 * 60);          // noon
        Assert.Equal((byte)0, world.GetLightLevel(pt));   // first read: set outright

        world.SetWorldClockMinutes(23 * 60);          // night
        int target = sector.GetLightCalc(quickSet: true);
        Assert.True(target > 3);
        Assert.Equal((byte)0, world.GetLightLevel(pt));   // no snap

        Assert.True(sector.TickLight());
        Assert.Equal((byte)1, world.GetLightLevel(pt));
        Assert.True(sector.TickLight());
        Assert.Equal((byte)2, world.GetLightLevel(pt));

        for (int i = 0; i < 40; i++)
            sector.TickLight();
        Assert.Equal((byte)target, world.GetLightLevel(pt));
        Assert.False(sector.TickLight());             // arrived
    }

    /// <summary>A sector nobody has been ticking is not faded from where it was left:
    /// it takes the light of a neighbour that is being ticked, else the calculated
    /// level.</summary>
    [Fact]
    public void UntickedSector_TakesItsNeighboursLight_OrTheCalculatedOne()
    {
        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 1024, 1024);
        world.LightDay = 0;
        world.LightNight = 20;
        var a = new Point3D(1, 1, 0, 0);
        var b = new Point3D((short)(Sector.SectorSize + 1), 1, 0, 0);
        var far = new Point3D(900, 900, 0, 0);

        world.SetWorldClockMinutes(12 * 60);
        Assert.Equal((byte)0, world.GetLightLevel(a));
        world.SetWorldClockMinutes(23 * 60);
        world.GetSector(a)!.TickLight();
        world.GetSector(a)!.TickLight();

        Assert.Equal((byte)2, world.GetLightLevel(b));    // the fade carries over the line
        Assert.Equal(world.GetSector(far)!.GetLightCalc(quickSet: true), world.GetLightLevel(far));
    }

    /// <summary>The sector tick tells the living players without Night Sight
    /// (CSector.cpp:1330).</summary>
    [Fact]
    public void SectorTick_SendsTheNewLightToThePlayersThere()
    {
        var world = TestHarness.CreateWorld();
        world.LightDay = 0;
        world.LightNight = 20;
        var ch = Place(world);
        ch.IsOnline = true;
        world.AddOnlinePlayer(ch);
        var sent = new List<byte[]>();
        Character.SendPacketToOwner = (who, packet) =>
        {
            if (who == ch) sent.Add(packet.Build().Span.ToArray());
        };
        var engine = new WeatherEngine(world);
        var sector = world.GetSector(ch.Position)!;

        world.SetWorldClockMinutes(12 * 60);
        world.GetLightLevel(ch.Position);
        // Local time follows the sector's longitude; find an hour that is night here.
        long night = Enumerable.Range(0, 24).Select(h => (long)h * 60)
            .First(m => { world.SetWorldClockMinutes(m); return sector.GetLightCalc(quickSet: true) > 3; });
        world.SetWorldClockMinutes(night);
        byte before = world.GetLightLevel(ch.Position);

        engine.OnSectorTick(sector);

        byte after = world.GetLightLevel(ch.Position);
        Assert.Equal(1, Math.Abs(after - before));
        var light = Assert.Single(sent, p => p[0] == 0x4F);
        Assert.Equal(after, light[1]);

        sent.Clear();
        ch.SetStatFlag(StatFlag.NightSight);
        engine.OnSectorTick(sector);
        Assert.DoesNotContain(sent, p => p[0] == 0x4F);
    }

    /// <summary>CChar::GetLightLevel (CCharStatus.cpp:757-760): the dead and, under
    /// RACIALF_ELF_NIGHTSIGHT, elves see LIGHT_BRIGHT.</summary>
    [Fact]
    public void ElfNightSight_ShowsFullBrightness()
    {
        var world = TestHarness.CreateWorld();
        var elf = Place(world, body: 0x025D);
        var human = Place(world, 101, 100);
        world.GetSector(elf.Position)!.SetLight(18);

        Assert.Equal((byte)18, world.GetLightLevelFor(elf));     // flag off
        Character.RacialFlags = (int)RacialFlags.ElfNightSight;
        Assert.Equal((byte)0, world.GetLightLevelFor(elf));
        Assert.Equal((byte)18, world.GetLightLevelFor(human));
    }

    // ------------------------------------------------------------- weather

    /// <summary>CSector::_OnTick "random area noises" (CSector.cpp:1277-1311): rain
    /// plays thunder (with the light flash), rain or wind; snow only wind; a dry or
    /// empty sector nothing.</summary>
    [Fact]
    public void WeatherSounds_FollowTheSectorsWeather()
    {
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        ch.IsOnline = true;
        var sector = world.GetSector(ch.Position)!;
        var engine = new WeatherEngine(world);
        var heard = new List<ushort>();
        Character.SendPacketToOwner = (who, packet) =>
        {
            var bytes = packet.Build().Span.ToArray();
            if (who == ch && bytes[0] == 0x54)
                heard.Add((ushort)((bytes[2] << 8) | bytes[3]));
        };
        int flashes = 0;
        world.OnSectorLight = (_, level) => { if (level == 0) flashes++; };

        sector.SetWeather((byte)WeatherType.Rain);
        for (int i = 0; i < 400; i++)
            Assert.Equal(0, engine.PlayWeatherEffects(sector));   // nobody online here
        world.AddOnlinePlayer(ch);

        var rainSounds = new HashSet<ushort>();
        for (int i = 0; i < 600; i++)
            rainSounds.Add(engine.PlayWeatherEffects(sector));
        Assert.Subset(new HashSet<ushort> { 0, 0x10, 0x11, 0x14, 0x15, 0x16, 0x28, 0x29, 0x206 }, rainSounds);
        Assert.Contains((ushort)0x10, rainSounds);
        Assert.Contains((ushort)0x206, rainSounds);
        Assert.True(flashes > 0);                                 // lightning
        Assert.NotEmpty(heard);

        sector.SetWeather((byte)WeatherType.Snow);
        var snowSounds = new HashSet<ushort>();
        for (int i = 0; i < 400; i++)
            snowSounds.Add(engine.PlayWeatherEffects(sector));
        Assert.Subset(new HashSet<ushort> { 0, 0x14, 0x15, 0x16 }, snowSounds);
        Assert.Contains((ushort)0x14, snowSounds);

        sector.SetWeather(Sector.WeatherDry);
        for (int i = 0; i < 200; i++)
            Assert.Equal(0, engine.PlayWeatherEffects(sector));
    }

    /// <summary>SetDefaultWeatherChance (CSector.cpp:836-855): snow is likely in the
    /// north and all but absent in the south, where it rains a little more. A sector
    /// whose chances were set keeps them.</summary>
    [Fact]
    public void DefaultWeatherChance_FollowsTheLatitude()
    {
        var world = TestHarness.CreateWorld();   // 6144 x 4096
        var north = world.GetSector(new Point3D(100, 1, 0, 0))!;
        var south = world.GetSector(new Point3D(100, 4090, 0, 0))!;

        Assert.Equal(15, north.EffectiveRainChance);
        Assert.Equal(99, north.EffectiveColdChance);              // 1 + (49 - 0) * 2
        Assert.Equal(1, south.EffectiveColdChance);
        int southPercent = south.SectorY * 100 / south.MapSectorRows;
        Assert.Equal(15 + (southPercent - 50) / 10, south.EffectiveRainChance);

        // The stored pair is untouched, so nothing new reaches the save.
        Assert.Equal(Sector.DefaultRainChance, north.RainChance);
        Assert.Equal(Sector.DefaultColdChance, north.ColdChance);

        north.ColdChance = 40;
        Assert.Equal(40, north.EffectiveColdChance);
        Assert.Equal(15, north.EffectiveRainChance);
    }
}
