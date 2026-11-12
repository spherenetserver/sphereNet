using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotActionSafetyTests
{
    private sealed class Rig
    {
        public GameWorld World = TestHarness.CreateWorld();
        public GameClient Client;
        public Character Bot, Friend;
        public PlayerBotEngine Engine;
        public BotMind Mind;
        public Rig()
        {
            Client = TestHarness.CreateClient(NullLoggerFactory.Instance, World, new AccountManager(NullLoggerFactory.Instance), 1);
            Bot = World.CreateCharacter(); Bot.IsPlayer = true; Bot.BodyId = 0x190;
            Bot.Str = 100; Bot.Hits = 30; Bot.MaxHits = 100; Bot.SetSkill(SkillType.Healing, 800);
            World.PlaceCharacter(Bot, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(Client, Bot);
            var pack = World.CreateItem(); pack.BaseId = 0xE75; pack.ItemType = ItemType.Container;
            Bot.Backpack = pack; Bot.Equip(pack, Layer.Pack);
            var bandage = World.CreateItem(); bandage.BaseId = 0xE21; bandage.ItemType = ItemType.Bandage;
            bandage.Amount = 20; pack.AddItem(bandage);
            Friend = World.CreateCharacter(); Friend.IsPlayer = true; Friend.IsOnline = true; Friend.BodyId = 0x190;
            Friend.Str = 100; Friend.Hits = 20; Friend.MaxHits = 100;
            World.PlaceCharacter(Friend, new Point3D(101, 100, 0, 0));
            Engine = new PlayerBotEngine(World, new MovementEngine(World), null, new BotNavGraph(),
                BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 7);
            Mind = new BotMind(Bot, new Random(1)) { Client = Client, Companion = Friend };
            Client.SetEngines(skillHandlers: new SkillHandlers(World));
            Delay(SkillType.Healing, "11.0");
        }
        public object? Call(string method, params object[] args) => typeof(PlayerBotEngine)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Engine, args);
    }
    private static void Delay(SkillType skill, string seconds)
    {
        var def = new SkillDef(ResourceId.Invalid); def.LoadFromKey("DELAY", seconds);
        DefinitionLoader.SetSkillDef((int)skill, def);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ARunningBandageKeepsItsPatientAndDeadline(bool selfFirst)
    {
        var r = new Rig(); long now = Environment.TickCount64;
        Assert.Equal(true, r.Call(selfFirst ? "TryBandageSelf" : "TryTendParty", r.Mind, now));
        var patient = r.Bot.SkillPendingTarget; var deadline = r.Bot.SkillDelayEnd;
        Assert.True(r.Bot.HasActiveSkillPending());
        Assert.Equal(false, r.Call(selfFirst ? "TryTendParty" : "TryBandageSelf", r.Mind, now + 1000));
        // Even the bot's own nine-second estimate cannot restart an eleven-second skill.
        Assert.Equal(false, r.Call("TryBandageSelf", r.Mind, now + 9500));
        Assert.Equal(patient, r.Bot.SkillPendingTarget);
        Assert.Equal(deadline, r.Bot.SkillDelayEnd);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GroundSpellOnlyAnswersItsOwnCursor(bool sameSpell)
    {
        var r = new Rig(); bool answered = false;
        r.Mind.Tac.GroundSpell = SpellType.Teleport;
        r.Mind.Tac.GroundAt = new Point3D(110, 111, 0, 0);
        r.Mind.Tac.GroundUntilMs = 8000;
        r.Client.SetPendingTarget((uid, x, y, z, graphic) =>
        {
            answered = true; Assert.Equal(110, x); Assert.Equal(111, y);
        });
        if (sameSpell) r.Client.Targets.Spell = SpellType.Teleport;
        r.Call("AnswerGroundCursor", r.Mind, 1000L);
        Assert.Equal(sameSpell, answered);
        Assert.Equal(!sameSpell, r.Client.HasPendingTarget);
        Assert.Equal(SpellType.None, r.Mind.Tac.GroundSpell);
    }

    [Theory]
    [InlineData(7000)] [InlineData(10000)] [InlineData(1800)]
    public void AThiefFitsTheConfiguredTotalWithoutLoweringItTo700ByAssumption(int cap)
    {
        var r = new Rig(); r.Bot.SetTag("OVERRIDE.SKILLSUM", cap.ToString());
        foreach (var skill in new[] { SkillType.Swordsmanship, SkillType.Tactics, SkillType.Anatomy,
                     SkillType.Healing, SkillType.MagicResistance, SkillType.Parrying, SkillType.MaceFighting })
            r.Bot.SetSkill(skill, 900);
        r.Engine.ThiefPercent = 100;
        Assert.Equal("thief", r.Call("RoleOf", r.Mind));
        int total = SkillEngine.GetSkillSum(r.Bot);
        Assert.InRange(total, cap == 10000 ? 8000 : cap - 60, cap);
        var values = Enumerable.Range(0, (int)SkillType.Qty).Select(i => r.Bot.GetSkill((SkillType)i)).ToArray();
        r.Mind.Role = null; // save/adopt must not roll or shrink a second time
        r.Call("RoleOf", r.Mind);
        Assert.Equal(values, Enumerable.Range(0, (int)SkillType.Qty).Select(i => r.Bot.GetSkill((SkillType)i)));
    }

    [Fact]
    public void AThiefWaitsForTheSkillBeforeTryingToHide()
    {
        var r = new Rig(); r.Mind.Role = "thief"; r.Mind.Mark = r.Friend; r.Mind.RogueStage = 2;
        r.Bot.BeginSkillPending((int)SkillType.Stealing, Environment.TickCount64 + 2500,
            long.MaxValue, r.Friend.Uid, null);
        r.Call("ActThief", r.Mind, 1000L);
        Assert.Same(r.Friend, r.Mind.Mark);
        Assert.Equal(2, r.Mind.RogueStage);
        Assert.Equal((int)SkillType.Stealing, r.Bot.SkillPendingId);
        r.Bot.ClearActiveSkillPending(); Delay(SkillType.Hiding, "1.0");
        r.Call("ActThief", r.Mind, 3000L);
        Assert.Null(r.Mind.Mark);
        Assert.Equal((int)SkillType.Hiding, r.Bot.SkillPendingId);
    }

    [Theory]
    [InlineData(30, 0, true)] [InlineData(5, 0, false)] [InlineData(30, 200, false)]
    public void ArmorUpgradeUsesDefenseAndTheNormalEquipRequirements(int defense, int strength, bool upgrade)
    {
        var r = new Rig();
        Item Armor(int id, int ar, int req)
        {
            DefinitionLoader.SetItemDef(id, new ItemDef(ResourceId.Invalid)
                { DispIndex = (ushort)id, Type = ItemType.Armor, Layer = Layer.Chest, ReqStr = req, DefenseMin = ar });
            var item = r.World.CreateItem(); item.BaseId = (ushort)id; item.ItemType = ItemType.Armor;
            r.Bot.Backpack!.AddItem(item); return item;
        }
        var old = Armor(0x1415, 10, 0); r.Client.HandleEquipMacro([old.Uid.Value]);
        var candidate = Armor(0x13BF, defense, strength);
        Assert.Equal(upgrade, r.Call("CheckArmor", r.Mind, 1000L));
        Assert.Same(upgrade ? candidate : old, r.Bot.GetEquippedItem(Layer.Chest));
        Assert.Equal(r.Bot.Backpack!.Uid, (upgrade ? old : candidate).ContainedIn);
        if (upgrade) Assert.Equal(false, r.Call("KeepsForItself", r.Mind, old));
    }
}
