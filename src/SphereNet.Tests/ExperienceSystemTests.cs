using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Experience/level runtime: Character.ChangeExperience pipeline —
/// @ExpChange may adjust or cancel the delta, level threshold crossings fire
/// @ExpLevelChange, script EXP writes route through the same path, and
/// DeathEngine awards the victim's EXP on an NPC kill.
/// </summary>
public class ExperienceSystemTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static void ResetLevelConfig()
    {
        Character.LevelNextAt = 1000;
        Character.LevelModeDouble = true;
    }

    [Fact]
    public void ChangeExperience_FiresExpChange_AndAppliesAdjustedDelta()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        int? proposed = null;
        Character.OnExpChanging = (_, d) => { proposed = d; return d * 2; }; // script doubles it
        ch.ChangeExperience(100);

        Assert.Equal(100, proposed);
        Assert.Equal(200, ch.Exp);
    }

    [Fact]
    public void ChangeExperience_CancelledByHook_LeavesExpUntouched()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        Character.OnExpChanging = (_, _) => null; // RETURN 1
        ch.ChangeExperience(500);

        Assert.Equal(0, ch.Exp);
    }

    [Fact]
    public void ChangeExperience_LevelThreshold_FiresExpLevelChange()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        var levels = new List<short>();
        Character.OnExpLevelChanged = (_, lvl) => levels.Add(lvl);

        // Double mode, step 1000: level 1 at 1000, level 2 at 3000, level 3 at 7000.
        ch.ChangeExperience(999);
        Assert.Empty(levels);
        ch.ChangeExperience(1);      // 1000 → level 1
        ch.ChangeExperience(6000);   // 7000 → level 3 (crosses 2 thresholds in one award)
        Assert.Equal(new short[] { 1, 3 }, levels);
        Assert.Equal((short)3, ch.Level);
    }

    [Fact]
    public void ComputeLevel_LinearMode_UsesConstantSteps()
    {
        Character.LevelNextAt = 1000;
        Character.LevelModeDouble = false;
        Assert.Equal((short)0, Character.ComputeLevel(999));
        Assert.Equal((short)1, Character.ComputeLevel(1000));
        Assert.Equal((short)2, Character.ComputeLevel(2000));
        Assert.Equal((short)5, Character.ComputeLevel(5999));
        ResetLevelConfig();
    }

    [Fact]
    public void ComputeLevel_Disabled_WhenLevelNextAtZero()
    {
        Character.LevelNextAt = 0;
        Assert.Equal((short)0, Character.ComputeLevel(1_000_000));
        ResetLevelConfig();
    }

    [Fact]
    public void ScriptExpWrite_RoutesThroughChangePipeline()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        int? seenDelta = null;
        Character.OnExpChanging = (_, d) => { seenDelta = d; return d; };

        Assert.True(ch.TrySetProperty("EXP", "250"));
        Assert.Equal(250, seenDelta);
        Assert.Equal(250, ch.Exp);

        Assert.True(ch.TrySetProperty("EXP", "100")); // decrease → negative delta
        Assert.Equal(-150, seenDelta);
        Assert.Equal(100, ch.Exp);
    }

    [Fact]
    public void NpcKill_AwardsVictimExpToKiller()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var death = new DeathEngine(world);

        var killer = world.CreateCharacter();
        killer.IsPlayer = true;
        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        npc.Exp = 750;
        npc.MaxHits = 50; npc.Hits = 50;

        death.ProcessDeath(npc, killer);

        Assert.Equal(750, killer.Exp);
    }

    [Fact]
    public void AttackerIgnore_FlagAndHitIgnoreHook()
    {
        var world = CreateWorld();
        var victim = world.CreateCharacter();
        var attacker = world.CreateCharacter();

        victim.RecordAttack(attacker.Uid, 10);
        Assert.True(victim.TryGetProperty("ATTACKER.0.IGNORE", out string? ig0));
        Assert.Equal("0", ig0);

        // Script sets the ignore flag; the next hit fires the hook.
        Assert.True(victim.TrySetProperty("ATTACKER.0.IGNORE", "1"));
        Assert.True(victim.TryGetProperty("ATTACKER.0.IGNORE", out string? ig1));
        Assert.Equal("1", ig1);

        Serial? hookAttacker = null;
        Character.OnHitIgnored = (_, uid) => { hookAttacker = uid; return true; }; // RETURN 1 → clear
        victim.RecordAttack(attacker.Uid, 5);

        Assert.Equal(attacker.Uid, hookAttacker);
        Assert.True(victim.TryGetProperty("ATTACKER.0.IGNORE", out string? ig2));
        Assert.Equal("0", ig2); // hook returning true cleared the flag
    }

    [Fact]
    public void PlayerKill_AwardsNoExp()
    {
        ResetLevelConfig();
        var world = CreateWorld();
        var death = new DeathEngine(world);

        var killer = world.CreateCharacter();
        killer.IsPlayer = true;
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.Exp = 750;
        victim.MaxHits = 50; victim.Hits = 50;

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Exp);
    }
}
