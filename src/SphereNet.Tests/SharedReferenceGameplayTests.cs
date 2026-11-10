using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Gameplay the reference servers share and SphereNet had differently:
/// <list type="bullet">
/// <item>a harmed player turns on the attacker (OnHarmedBy, CCharFight.cpp:291-317);</item>
/// <item>an owned berserk creature still attacks on sight (NPC_LookAtChar,
/// CCharNPCAct.cpp:1030-1091);</item>
/// <item>only the owner rides a mount (Horse_Mount, CCharAct.cpp:3976);</item>
/// <item>a logged-out character is no area-spell target (CWorldSearch.cpp:271-275);</item>
/// <item>a target that hid during the cast is no target at completion
/// (CCharLOS.cpp:668);</item>
/// <item>mounts come from the pack's mount_0x list (Horse_GetMountItemID,
/// CCharAct.cpp:3940-3944).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SharedReferenceGameplayTests
{
    private static Character Player(GameWorld world, short x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = true;
        ch.BodyId = 0x0190;
        ch.Str = ch.Dex = ch.Int = 50;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    // ---- auto-defend ----

    [Fact]
    public void AHarmedPlayerWithNoFight_TurnsOnTheAttacker()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var victim = Player(world, 100);
        var attacker = Player(world, 101);
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 32001);
        TestHarness.AttachCharacter(client, victim);
        Character.OnPlayerAutoDefend = (v, a) => { if (v == victim) client.HandleAttack(a.Uid.Value); };

        Assert.False(victim.IsInWarMode);
        victim.OnHarmedBy(attacker);

        Assert.True(victim.IsInWarMode);
        Assert.Equal(attacker.Uid, victim.FightTarget);
    }

    [Fact]
    public void APlayerAlreadyFightingSomeone_KeepsTheirTarget()
    {
        var world = TestHarness.CreateWorld();
        var victim = Player(world, 100);
        var first = Player(world, 101);
        var second = Player(world, 102);
        int calls = 0;
        Character.OnPlayerAutoDefend = (_, _) => calls++;
        victim.SetStatFlag(StatFlag.War);
        victim.FightTarget = first.Uid;

        victim.OnHarmedBy(second);

        Assert.Equal(0, calls);
        Assert.Equal(first.Uid, victim.FightTarget);
    }

    // ---- berserk summon ----

    [Fact]
    public void AnOwnedBerserkCreature_AttacksWhatItSees()
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        var owner = Player(world, 100);
        var bystander = Player(world, 104);
        var vortex = world.CreateCharacter();
        vortex.BodyId = 0x00A4;
        vortex.NpcBrain = NpcBrainType.Berserk;
        vortex.Str = vortex.Dex = 100;
        vortex.Hits = vortex.MaxHits = 100;
        vortex.Stam = vortex.MaxStam;
        world.PlaceCharacter(vortex, new Point3D(103, 100, 0, 0));
        Assert.True(vortex.TryAssignOwnership(owner, owner));

        vortex.NextNpcActionTime = 0;
        ai.OnTickAction(vortex);

        Assert.Equal(bystander.Uid, vortex.FightTarget);
    }

    // ---- mount ownership ----

    [Fact]
    public void APetFriend_CannotRideTheOwnersMount()
    {
        var world = TestHarness.CreateWorld();
        var owner = Player(world, 100);
        var friend = Player(world, 100);
        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;
        horse.Hits = horse.MaxHits = 50;
        world.PlaceCharacter(horse, new Point3D(100, 100, 0, 0));
        Assert.True(horse.TryAssignOwnership(owner, owner));
        horse.AddFriend(friend);
        Assert.True(horse.IsFriendOf(friend.Uid));
        var mounts = new MountEngine(world);

        Assert.False(mounts.TryMount(friend, horse));
        Assert.True(horse.HasOwner(owner.Uid));
        Assert.True(mounts.TryMount(owner, horse));
    }

    // ---- mount list ----

    [Fact]
    public void WithNoPackList_HorsesAreMounts_AndPackAnimalsAreNot()
    {
        Assert.True(MountEngine.IsMountable(0x00C8));
        Assert.False(MountEngine.IsMountable(0x0123)); // pack horse
        Assert.False(MountEngine.IsMountable(0x0124)); // pack llama
    }

    [Fact]
    public void APacksMountList_IsTheWholeAnswer()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_mt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "m.scp");
            File.WriteAllLines(file,
            [
                "[DEFNAME mount_items]",
                "mount_0xc8\ti_mt_horse_tan",
                "mount_0x4e6\t03ecd",
                "",
                "[ITEMDEF i_mt_horse_tan]", "ID=03e9f", "TYPE=t_eq_horse", "",
            ]);
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.ScpBaseDir = dir;
            stack.Resources.LoadResourceFile(file);
            ScriptTestBootstrap.LoadDefinitions(stack.Resources);

            Assert.Equal(0x3E9F, MountEngine.GetMountItemId(0x00C8));   // by item defname
            Assert.Equal(0x3ECD, MountEngine.GetMountItemId(0x04E6));   // not in the built-in table
            Assert.Equal(0, MountEngine.GetMountItemId(0x00DC));        // llama: the pack does not list it
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- stat cap tag, swing, looting ----

    [Fact]
    public void StatCapOverrideTags_UseTheReferenceStatNumbers()
    {
        // STAT_TYPE: STR 0, INT 1, DEX 2 (uofiles_enums.h:447-449).
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);
        ch.SetTag("OVERRIDE.STATCAP_1", "77");
        ch.SetTag("OVERRIDE.STATCAP_2", "55");

        Assert.Equal(77, SphereNet.Game.Skills.SkillEngine.StatCapInt(ch));
        Assert.Equal(55, SphereNet.Game.Skills.SkillEngine.StatCapDex(ch));
    }

    [Fact]
    public void TakingTheWeaponOutOfHand_AbandonsTheSwingInFlight()
    {
        var world = TestHarness.CreateWorld();
        var fighter = Player(world, 100);
        var enemy = Player(world, 101);
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        fighter.Equip(sword, Layer.OneHanded);
        fighter.FightTarget = enemy.Uid;
        fighter.PendingHitTarget = enemy.Uid;

        fighter.Unequip(Layer.OneHanded);

        Assert.False(fighter.PendingHitTarget.IsValid);
        Assert.True(fighter.NextAttackTime > 0);
    }

    [Theory]
    [InlineData(false, true)]    // an innocent owner: a crime
    [InlineData(true, false)]    // a criminal owner: not one
    public void LootingIsACrime_OnlyWhenTheOwnerIsInnocentToTheLooter(bool ownerCriminal, bool crime)
    {
        var world = TestHarness.CreateWorld();
        var engine = new SphereNet.Game.Death.DeathEngine(world);
        var looter = Player(world, 100);
        var owner = Player(world, 101);
        if (ownerCriminal) owner.MakeCriminal();
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", owner.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));

        Assert.Equal(crime, engine.IsLootingCriminal(looter, corpse));
    }

    // ---- fame message, legacy BLOCK, character modifiers ----

    [Fact]
    public void AFameChange_IsToldToTheCharacter()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world, 100);
        ch.Fame = 1000;
        var lines = new List<string>();
        Character.SendOwnerMessage = (who, text) => { if (who == ch) lines.Add(text); };

        typeof(SphereNet.Game.Death.DeathEngine)
            .GetMethod("ApplyFame", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [ch, 100]);

        Assert.Equal(1100, ch.Fame);
        Assert.Single(lines);
    }

    [Theory]
    [InlineData("BLOCK", "1", true)]
    [InlineData("BANNED", "1", true)]
    [InlineData("BLOCK", "0", false)]
    public void ALegacyAccountsFile_KeepsItsBlocks(string key, string value, bool blocked)
    {
        var account = new SphereNet.Game.Accounts.Account();
        typeof(SphereNet.Persistence.Accounts.AccountPersistence)
            .GetMethod("ApplyProperty", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [account, key, value]);

        Assert.Equal(blocked, account.IsBanned);
    }

    // ---- script engine and region keys ----

    [Fact]
    public void ABraceRange_SplitsOnCommasAsWellAsSpaces()
    {
        var tokens = SphereNet.Scripting.Expressions.BraceRange.SplitTokens("3,7");
        Assert.Equal(["3", "7"], tokens);
        // A call's own commas stay inside its token.
        Assert.Equal(["f(1,2)", "9"], SphereNet.Scripting.Expressions.BraceRange.SplitTokens("f(1,2) 9"));
    }

    [Fact]
    public void TheLoopCap_AllowsOnePassFewerThanItsValue()
    {
        var scope = new SphereNet.Scripting.Execution.ScriptScope { MaxLoopIterations = 10 };
        Assert.False(scope.LoopLimitReached(8));   // the 9th pass still runs
        Assert.True(scope.LoopLimitReached(9));    // the 10th is cut off
    }

    [Fact]
    public void RegionFlagKeys_ReadAndWriteTheReferenceFlags()
    {
        var region = new SphereNet.Game.World.Regions.Region { Name = "r", MapIndex = 0 };

        Assert.True(region.TrySetProperty("RECALLOUT", "0"));
        Assert.True(region.IsFlag(RegionFlag.RecallOut));
        Assert.True(region.TryGetProperty("RECALL", out string recall));
        Assert.Equal("0", recall);

        Assert.True(region.TrySetProperty("NODECAY", "1"));
        Assert.True(region.IsFlag(RegionFlag.NoDecay));
        Assert.True(region.TrySetProperty("ARENA", "1"));
        Assert.True(region.IsFlag(RegionFlag.Arena));
        Assert.True(region.TrySetProperty("BUILDABLE", "0"));
        Assert.True(region.IsFlag(RegionFlag.NoBuild));
        Assert.True(region.TrySetProperty("GATE", "0"));
        Assert.True(region.IsFlag(RegionFlag.Gate));
    }

    [Fact]
    public void OFameAndOKarma_CanBeReadBack()
    {
        var ch = new Character { Fame = 1234, Karma = -55 };
        Assert.True(ch.TryGetProperty("OFAME", out string fame));
        Assert.Equal("1234", fame);
        Assert.True(ch.TryGetProperty("OKARMA", out string karma));
        Assert.Equal("-55", karma);
    }

    // ---- spells ----

    [Fact]
    public void ABeneficialSpellOnACriminal_FlagsTheCaster()
    {
        Character.HelpingCriminalsIsACrimeEnabled = true;
        try
        {
            using var b = new SpellBench();
            var bless = new SpellDef
            { Id = SpellType.Protection, ManaCost = 0, CastTimeBase = 1, Flags = SpellFlag.Good | SpellFlag.TargChar };
            b.Registry.Register(bless);
            foreach (var book in b.Caster.Backpack!.Contents)
                book.TryLearnSpell((int)SpellType.Protection);
            var criminal = Player(b.World, 102);
            criminal.MakeCriminal();

            Assert.True(b.Engine.CastStart(b.Caster, SpellType.Protection, criminal.Uid, criminal.Position) > 0);
            b.Caster.CastDifficulty = 0;
            Assert.True(b.Engine.CastDone(b.Caster));

            Assert.True(b.Caster.IsFlaggedAsCriminal);
        }
        finally { Character.HelpingCriminalsIsACrimeEnabled = false; }
    }

    private sealed class SpellBench : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly Character Caster;
        public readonly SpellEngine Engine;
        public readonly SpellRegistry Registry = new();
        public readonly SpellDef Heal = new()
        {
            Id = SpellType.Heal, ManaCost = 10, CastTimeBase = 5, Flags = SpellFlag.Heal | SpellFlag.TargChar,
            EffectCurve = SphereNet.Scripting.Definitions.ValueCurve.Parse("20,20"),
        };

        public SpellBench()
        {
            Caster = Player(World, 100);
            Caster.MaxMana = Caster.Mana = 100;
            Caster.SetSkill(SkillType.Magery, 1000);
            var pack = World.CreateItem(); pack.ItemType = ItemType.Container;
            Caster.Equip(pack, Layer.Pack);
            var book = World.CreateItem(); book.ItemType = ItemType.Spellbook;
            pack.AddItem(book); book.TryLearnSpell((int)SpellType.Heal);
            Registry.Register(Heal);
            Engine = new SpellEngine(World, Registry) { TriggerDispatcher = new TriggerDispatcher() };
            var client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 32002);
            TestHarness.AttachCharacter(client, Caster);
            client.SetEngines(spellEngine: Engine, triggerDispatcher: Engine.TriggerDispatcher);
            Caster.MaxMana = Caster.Mana = 100;
            Character.MagicFlags = 0;
        }
        public void Dispose() => _logs.Dispose();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ATargetThatHidDuringTheCast_IsNoLongerHit(bool hides, bool lands)
    {
        using var b = new SpellBench();
        var target = Player(b.World, 102);
        target.Hits = 40;

        Assert.True(b.Engine.CastStart(b.Caster, SpellType.Heal, target.Uid, target.Position) > 0);
        b.Caster.CastDifficulty = 0;
        if (hides) target.SetStatFlag(StatFlag.Hidden);

        Assert.Equal(lands, b.Engine.CastDone(b.Caster));
        if (!lands) Assert.Equal(40, target.Hits);
    }

    [Fact]
    public void AnAreaSpell_PassesOverALoggedOutCharacter()
    {
        using var b = new SpellBench();
        var online = Player(b.World, 101);
        var offline = Player(b.World, 102);
        offline.IsOnline = false;
        Assert.True(offline.IsLoggedOut);
        online.Hits = 40; offline.Hits = 40;

        typeof(SpellEngine).GetMethod("ApplyAreaEffect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(b.Engine, [b.Caster, b.Caster.Position, b.Heal, 1000, 5]);

        Assert.True(online.Hits > 40);
        Assert.Equal(40, offline.Hits);
    }
}
