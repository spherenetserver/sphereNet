using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Experience/level runtime under the Source-X settings (CChar::ChangeExperience,
/// CChar.cpp:5111; Calc_ExpGet_Level/Exp, :5062-5108; Noto_Kill reward,
/// CCharNotoriety.cpp:619-646): EXPERIENCEMODE gates every change, ALLOW_DOWN gates
/// losses, DOWN_NOLEVEL floors them at the level start, LEVELSYSTEM/LEVELMODE/
/// LEVELNEXTAT drive the level, EXPERIENCESYSTEM + RAISE_COMBAT and the PVP/PVM
/// percentages drive the kill reward.
/// </summary>
[Collection("DefinitionLoaderSerial")]
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

    private static void EnableSystem(int mode = Character.ExpModeRaiseCombat | Character.ExpModeAllowDown)
    {
        Character.ExperienceSystem = true;
        Character.ExperienceMode = mode;
        Character.LevelSystem = true;
        Character.LevelNextAt = 1000;
        Character.LevelModeDouble = true;
    }

    [Fact]
    public void ExperienceMode0_FreezesExperience()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        ch.ChangeExperience(100);

        Assert.Equal(0, ch.Exp);
    }

    [Fact]
    public void ChangeExperience_FiresExpChange_AndAppliesAdjustedDelta()
    {
        EnableSystem();
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
        EnableSystem();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        Character.OnExpChanging = (_, _) => null; // RETURN 1
        ch.ChangeExperience(500);

        Assert.Equal(0, ch.Exp);
    }

    [Fact]
    public void Loss_NeedsAllowDown()
    {
        EnableSystem(Character.ExpModeRaiseCombat);
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Exp = 500;

        ch.ChangeExperience(-100);
        Assert.Equal(500, ch.Exp);

        Character.ExperienceMode |= Character.ExpModeAllowDown;
        ch.ChangeExperience(-100);
        Assert.Equal(400, ch.Exp);
    }

    [Fact]
    public void DownNoLevel_StopsTheLossAtTheLevelFloor()
    {
        EnableSystem(Character.ExpModeAllowDown | Character.ExpModeDownNoLevel);
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TrySetProperty("EXP", "5500")); // double mode: level 3 starts at 5000
        Assert.Equal((short)3, ch.Level);

        ch.ChangeExperience(-2000);

        Assert.Equal(5000, ch.Exp);
        Assert.Equal((short)3, ch.Level);
    }

    [Fact]
    public void LevelChange_FiresExpLevelChangeWithTheDelta()
    {
        EnableSystem();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        var deltas = new List<int>();
        Character.OnExpLevelChanged = (_, d) => { deltas.Add(d); return d; };

        // Double mode, LEVELNEXTAT 1000: level 1 from 0, 2 from 2000, 3 from 5000.
        ch.ChangeExperience(1);
        ch.ChangeExperience(1998);
        Assert.Equal((short)1, ch.Level);
        ch.ChangeExperience(1);     // 2000
        ch.ChangeExperience(3000);  // 5000
        Assert.Equal(new[] { 1, 1, 1 }, deltas);
        Assert.Equal((short)3, ch.Level);
    }

    [Fact]
    public void LevelChange_CancelledByHook_KeepsTheLevel()
    {
        EnableSystem();
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        Character.OnExpLevelChanged = (_, _) => null;

        ch.ChangeExperience(3000);

        Assert.Equal(3000, ch.Exp);
        Assert.Equal((short)0, ch.Level);
    }

    [Fact]
    public void LevelSystemOff_LeavesTheLevelAlone()
    {
        EnableSystem();
        Character.LevelSystem = false;
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        ch.ChangeExperience(9000);

        Assert.Equal(9000, ch.Exp);
        Assert.Equal((short)0, ch.Level);
    }

    [Fact]
    public void ComputeLevel_LinearMode_IsOnePlusExpOverStep()
    {
        Character.LevelNextAt = 1000;
        Character.LevelModeDouble = false;
        Assert.Equal(1, Character.ComputeLevel(999));
        Assert.Equal(2, Character.ComputeLevel(1000));
        Assert.Equal(6, Character.ComputeLevel(5999));
        Assert.Equal(2000, Character.ExpForLevel(3));
    }

    [Fact]
    public void ComputeLevel_DoubleMode_NeedsStepTimesNextLevel()
    {
        Character.LevelNextAt = 1000;
        Character.LevelModeDouble = true;
        Assert.Equal(1, Character.ComputeLevel(1999));
        Assert.Equal(2, Character.ComputeLevel(2000));
        Assert.Equal(3, Character.ComputeLevel(5000));
        Assert.Equal(5000, Character.ExpForLevel(3));
    }

    [Fact]
    public void ComputeLevel_ZeroWhenLevelNextAtUnset()
    {
        Character.LevelNextAt = 0;
        Assert.Equal(0, Character.ComputeLevel(1_000_000));
    }

    [Fact]
    public void ScriptExpWrite_StoresTheValueAndResyncsTheLevel()
    {
        EnableSystem();
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        int? seenDelta = null;
        Character.OnExpChanging = (_, d) => { seenDelta = d; return d; };

        Assert.True(ch.TrySetProperty("EXP", "2500"));
        Assert.Null(seenDelta);   // CHC_EXP stores directly (CChar.cpp:4050)
        Assert.Equal(2500, ch.Exp);
        Assert.Equal((short)2, ch.Level);
    }

    [Fact]
    public void NpcKill_AwardsATenthScaledByTheExperienceGap()
    {
        EnableSystem();
        var world = CreateWorld();
        var death = new DeathEngine(world);

        var killer = world.CreateCharacter();
        killer.IsPlayer = true;
        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        npc.Exp = 750;
        npc.MaxHits = 50; npc.Hits = 50;

        death.ProcessDeath(npc, killer);

        // 750/10 = 75, killer below a quarter of the victim: doubled.
        Assert.Equal(150, killer.Exp);
    }

    [Fact]
    public void PlayerKill_UsesThePvpPercentage()
    {
        EnableSystem();
        Character.ExperienceKoefPVP = 50;
        var world = CreateWorld();
        var killer = world.CreateCharacter();
        killer.IsPlayer = true;
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.Exp = 750;

        // 75 * 50% = 37, doubled for the gap.
        Assert.Equal(74, Character.KillExperienceReward(killer, victim, 1));
    }

    [Fact]
    public void KillReward_NeedsExperienceSystemAndRaiseCombat()
    {
        var world = CreateWorld();
        var killer = world.CreateCharacter();
        var victim = world.CreateCharacter();
        victim.Exp = 750;

        Character.ExperienceMode = Character.ExpModeRaiseCombat;
        Assert.Equal(0, Character.KillExperienceReward(killer, victim, 1)); // system off

        Character.ExperienceSystem = true;
        Character.ExperienceMode = Character.ExpModeRaiseCraft;
        Assert.Equal(0, Character.KillExperienceReward(killer, victim, 1)); // no RAISE_COMBAT
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
}
