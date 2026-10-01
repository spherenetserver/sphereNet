using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The NPC action tick and the numbers it reads, against Source-X:
/// <list type="bullet">
/// <item>NPC_StablePetSelect reads the stablemaster's MAXPLAYERPETS with GetKeyNum
/// (CCharNPCAct_Vendor.cpp:124): the stored text is evaluated as a Sphere number or
/// expression - 010 is 16, 0A is 10, 5+5 is 10 - and a number tag survives a save.</item>
/// <item>NPC_OnTickAction ends with the periodic restock of every NPC_IsVendor brain
/// (CCharNPCAct.cpp:2396, CCharNPC.cpp:251), whatever action ran before it - a
/// scripted RUNTO included - and only once per tick.</item>
/// <item>NPC_Vendor_Restock reads the region's RestockVendors with GetValNum in
/// tenths of a second (CCharNPCAct_Vendor.cpp:53): a present 0 is a zero delay, not
/// the 10 minute default, and Sphere hex is a number.</item>
/// <item>A dead NPC still runs the action dispatch (CCharAct.cpp:5944): a bonded
/// ghost carries out GOTO/RUNTO/WALK (CCharNPCAct.cpp:2371) but cannot fight
/// (Fight_Attack refuses a dead attacker, CCharFight.cpp:1403).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcTickRestockStableParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_npctick_" + Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory _logs = LoggerFactory.Create(_ => { });

    public void Dispose()
    {
        _logs.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    /// <summary>A world with an online player beside (100,100), so masterless NPCs
    /// there are in an awake sector and act.</summary>
    private static GameWorld AwakeWorld()
    {
        var world = TestHarness.CreateWorld();
        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.IsOnline = true;
        world.PlaceCharacter(observer, new Point3D(102, 108, 0, 0));
        world.AddOnlinePlayer(observer);
        world.OnTick();
        return world;
    }

    private static Character Npc(GameWorld world, NpcBrainType brain, Point3D at)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = brain;
        npc.Karma = 100;
        npc.Str = 100;
        npc.Hits = npc.MaxHits = 100;
        npc.Int = 100;
        npc.Dex = 100;
        npc.Stam = npc.MaxStam;
        world.PlaceCharacter(npc, at);
        return npc;
    }

    private static void Tick(NpcAI ai, Character npc)
    {
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
    }

    // ---- 4: MAXPLAYERPETS is GetKeyNum ---------------------------------------------

    [Theory]
    [InlineData("10", 10)]
    [InlineData("010", 16)]
    [InlineData("0A", 10)]
    [InlineData("5+5", 10)]
    [InlineData("(2*3)+1", 7)]
    [InlineData("0", 2)]      // zero: the skill formula (untrained owner = 2)
    [InlineData("-3", 2)]     // max(0, n): a negative value is no override
    public void StableCapacityReadsTheTagAsASphereNumber(string stored, int expected)
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        var stableMaster = world.CreateCharacter();
        stableMaster.NpcBrain = NpcBrainType.Stable;

        // Stored raw (a string var, as a quoted script assignment leaves it).
        stableMaster.SetTag("MAXPLAYERPETS", stored);
        Assert.Equal(expected, StableEngine.GetMaxStabledPets(owner, stableMaster));

        // And as an unquoted script assignment stores it (a number var).
        stableMaster.SetTagStr("MAXPLAYERPETS", quoted: false, stored);
        Assert.Equal(expected, StableEngine.GetMaxStabledPets(owner, stableMaster));
    }

    [Fact]
    public void StableCapacitySetByAScriptSurvivesATextSaveAndLoad()
    {
        var world = TestHarness.CreateWorld();
        var stableMaster = world.CreateCharacter();
        stableMaster.NpcBrain = NpcBrainType.Stable;
        world.PlaceCharacter(stableMaster, new Point3D(100, 100, 0, 0));
        var quoted = world.CreateCharacter();
        quoted.NpcBrain = NpcBrainType.Stable;
        world.PlaceCharacter(quoted, new Point3D(101, 100, 0, 0));

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        void Run(IScriptObj target, string line)
        {
            var key = new ScriptKey();
            key.Parse(line);
            stack.Interpreter.Execute([key], target, null, new TriggerArgs(), new ScriptScope());
        }
        Run(stableMaster, "TAG.MAXPLAYERPETS=10");
        Run(quoted, "TAG.MAXPLAYERPETS=\"5+5\"");

        var owner = world.CreateCharacter();
        Assert.Equal(10, StableEngine.GetMaxStabledPets(owner, stableMaster));
        Assert.Equal(10, StableEngine.GetMaxStabledPets(owner, quoted));

        string dir = Path.Combine(_dir, "save");
        var saver = new WorldSaver(_logs) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, dir));
        var loaded = TestHarness.CreateWorld();
        new WorldLoader(_logs).Load(loaded, dir);

        var loadedMaster = loaded.FindChar(stableMaster.Uid)!;
        var loadedQuoted = loaded.FindChar(quoted.Uid)!;
        var loadedOwner = loaded.CreateCharacter();
        Assert.Equal(10, StableEngine.GetMaxStabledPets(loadedOwner, loadedMaster));
        Assert.Equal(10, StableEngine.GetMaxStabledPets(loadedOwner, loadedQuoted));
    }

    // ---- 5: the restock check closes every action tick -----------------------------

    public static TheoryData<NpcBrainType, bool> VendorBrainsBusyAndIdle()
    {
        var data = new TheoryData<NpcBrainType, bool>();
        foreach (var brain in new[] { NpcBrainType.Vendor, NpcBrainType.Banker,
                     NpcBrainType.Healer, NpcBrainType.Stable })
        {
            data.Add(brain, false);
            data.Add(brain, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(VendorBrainsBusyAndIdle))]
    public void EveryVendorBrainRestocksAtTheEndOfItsTick(NpcBrainType brain, bool busy)
    {
        var world = AwakeWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        ai.OnVendorRestock = _ => restocks++;
        var npc = Npc(world, brain, new Point3D(100, 100, 0, 0));

        if (busy)
        {
            Assert.True(npc.TryExecuteCommand("RUNTO", "120,100,0,0", new Console(), out _));
            Assert.Equal((SkillType)NpcAction.RunTo, npc.Action);
        }

        Tick(ai, npc);

        if (busy)
        {
            Assert.Equal((SkillType)NpcAction.RunTo, npc.Action);   // still running its errand
            Assert.Equal(101, npc.X);
        }
        Assert.Equal(1, restocks);

        // Not due again inside the default ten minutes.
        Tick(ai, npc);
        Assert.Equal(1, restocks);
    }

    [Theory]
    [MemberData(nameof(VendorBrainsBusyAndIdle))]
    public void AZeroRestockDelayRestocksOncePerTickNotTwice(NpcBrainType brain, bool busy)
    {
        var world = AwakeWorld();
        var region = new Region { Name = "town", MapIndex = 0 };
        region.AddRect(0, 0, 300, 300);
        region.SetTag("RESTOCKVENDORS", "0");
        world.AddRegion(region);

        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        ai.OnVendorRestock = _ => restocks++;
        var npc = Npc(world, brain, new Point3D(100, 100, 0, 0));
        if (busy)
            Assert.True(npc.TryExecuteCommand("RUNTO", "120,100,0,0", new Console(), out _));

        for (int i = 1; i <= 3; i++)
        {
            Tick(ai, npc);
            Assert.Equal(i, restocks);
        }
    }

    [Fact]
    public void APetVendorIsNotRestockedByItsTick()
    {
        var world = AwakeWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        ai.OnVendorRestock = _ => restocks++;
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(99, 100, 0, 0));
        var npc = Npc(world, NpcBrainType.Vendor, new Point3D(100, 100, 0, 0));
        npc.NpcMaster = owner.Uid;
        npc.SetStatFlag(StatFlag.Pet);
        Assert.True(npc.TryExecuteCommand("RUNTO", "120,100,0,0", new Console(), out _));

        Tick(ai, npc);
        Tick(ai, npc);

        Assert.Equal(0, restocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANoRestockTagStillSuppressesTheTickRestock(bool busy)
    {
        var world = AwakeWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        ai.OnVendorRestock = _ => restocks++;
        var npc = Npc(world, NpcBrainType.Vendor, new Point3D(100, 100, 0, 0));
        npc.SetTag("NORESTOCK", "0");
        if (busy)
            Assert.True(npc.TryExecuteCommand("RUNTO", "120,100,0,0", new Console(), out _));

        Tick(ai, npc);

        Assert.Equal(0, restocks);
    }

    // ---- 6: RESTOCKVENDORS is a number of tenths, 0 included --------------------------

    [Theory]
    // (region tag or null for none, ms since the last restock, restocks now?)
    [InlineData(null, 9 * 60 * 1000, false)]        // default 10 minutes, not yet
    [InlineData(null, 10 * 60 * 1000 + 50, true)]
    [InlineData("1", 150, true)]                    // 1 tenth = 100 ms
    [InlineData("0", 0, true)]                      // a present zero: no delay at all
    [InlineData("00", 0, true)]
    [InlineData("0A", 500, false)]                  // Sphere hex: 10 tenths = 1 s
    [InlineData("0A", 1100, true)]
    [InlineData("010", 1500, false)]                // 0x10 = 16 tenths = 1.6 s
    [InlineData("010", 1700, true)]
    [InlineData("5+5", 1100, true)]                 // evaluated, like GetValNum
    [InlineData("5+5", 500, false)]
    public void RegionRestockDelayIsReadAsSphereTenths(string? tag, int sinceLastMs, bool restocks)
    {
        var world = TestHarness.CreateWorld();
        var region = new Region { Name = "town", MapIndex = 0 };
        region.AddRect(0, 0, 300, 300);
        if (tag != null)
            region.SetTag("RESTOCKVENDORS", tag);
        world.AddRegion(region);

        var ai = new NpcAI(world, new SphereConfig());
        int count = 0;
        ai.OnVendorRestock = _ => count++;
        var npc = Npc(world, NpcBrainType.Vendor, new Point3D(100, 100, 0, 0));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        npc.SetTag("RESTOCK_TIME", (now - sinceLastMs).ToString());

        Invoke(ai, "TryVendorRestock", npc);

        Assert.Equal(restocks ? 1 : 0, count);
    }

    // ---- 8: a dead bonded pet runs its action dispatch --------------------------------

    private static (GameWorld World, NpcAI Ai, Character Owner, Character Pet) GhostPet(bool dead = true)
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Str = 100; owner.Hits = owner.MaxHits = 100;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Str = 100; pet.Hits = pet.MaxHits = 100;
        pet.Dex = 100; pet.Stam = pet.MaxStam;
        pet.Int = 100;
        pet.BodyId = 0x00C8;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));
        pet.PetAIMode = PetAIMode.Stay;
        if (dead)
        {
            pet.IsBonded = true;
            pet.SetStatFlag(StatFlag.Dead);
            pet.Hits = 0;
        }
        return (world, ai, owner, pet);
    }

    [Theory]
    [InlineData(false, "RUNTO", "110,100,0,0")]
    [InlineData(true, "RUNTO", "110,100,0,0")]
    [InlineData(true, "GOTO", "110,100,0,0")]
    [InlineData(true, "WALK", "E")]
    [InlineData(true, "RUN", "E")]
    public void ABondedGhostCarriesOutAScriptedMove(bool dead, string verb, string args)
    {
        var (_, ai, _, pet) = GhostPet(dead);
        Assert.True(pet.TryExecuteCommand(verb, args, new Console(), out _));

        Tick(ai, pet);

        Assert.Equal(102, pet.X);
        Assert.Equal(dead, pet.IsDead);
    }

    [Fact]
    public void ABondedGhostGuardingSomeoneFollowsButDoesNotFight()
    {
        var (world, ai, owner, pet) = GhostPet();
        var foe = world.CreateCharacter();
        foe.Str = 100; foe.Hits = foe.MaxHits = 100;
        world.PlaceCharacter(foe, new Point3D(96, 100, 0, 0));
        owner.FightTarget = foe.Uid;
        world.MoveCharacter(pet, new Point3D(104, 100, 0, 0));

        pet.Act = owner.Uid;
        pet.Action = (SkillType)NpcAction.GuardTarg;

        Tick(ai, pet);

        Assert.False(pet.FightTarget.IsValid);   // Fight_Attack refuses a dead attacker
        Assert.Equal(103, pet.X);                // NPC_Act_Guard falls back to follow
    }

    [Fact]
    public void ABondedGhostFinishesItsScriptedMoveThenObeysItsOrder()
    {
        var (_, ai, _, pet) = GhostPet();
        pet.PetAIMode = PetAIMode.Follow;
        Assert.True(pet.TryExecuteCommand("GOTO", "103,100,0,0", new Console(), out _));

        // The script's move outranks the follow order until it arrives.
        Tick(ai, pet);
        Assert.Equal(102, pet.X);
        Tick(ai, pet);
        Assert.Equal(103, pet.X);
        Tick(ai, pet);
        Assert.Equal(SkillType.None, pet.Action);
    }
}
