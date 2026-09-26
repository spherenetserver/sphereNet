using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// NPC movement cadence, idle activity, walk-here checks, scripted actions, talk state
/// and the pet food tick, against Source-X CCharNPCAct.cpp / CCharNPCStatus.cpp /
/// CCharAct.cpp / CCharNPCPet.cpp.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcAiSourceXMovementIdleTests
{
    private static object? Call(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ai, args);

    private static (GameWorld World, NpcAI Ai) Make(SphereConfig? config = null)
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, config ?? new SphereConfig()) { Flags = NpcAIFlags.None };
        return (world, ai);
    }

    private static Character Npc(GameWorld world, short x = 100, short y = 100, int dex = 100)
    {
        var npc = world.CreateCharacter();
        npc.BodyId = 0x0033;
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Str = 50; npc.Dex = (short)dex; npc.Int = 50;
        npc.MaxHits = 50; npc.Hits = 50;
        npc.Stam = npc.MaxStam;
        world.PlaceCharacter(npc, new Point3D(x, y, 0, 0));
        return npc;
    }

    private static Character Blocker(GameWorld world, short x, short y)
    {
        var b = world.CreateCharacter();
        b.MaxHits = 50; b.Hits = 50;
        world.PlaceCharacter(b, new Point3D(x, y, 0, 0));
        return b;
    }

    private static void Surround(GameWorld world, Character npc)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    Blocker(world, (short)(npc.X + dx), (short)(npc.Y + dy));
    }

    // ---- 1. step delay: NPC_WalkToPoint "Speed counting" (:610-693) ----------

    [Fact]
    public void TheStepDelayFollowsWhetherThisStepRuns()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 100);
        // DEX 100 at move rate 100 leaves no random part: 1 s walking, 1/4 s running.
        Assert.Equal(1000, ai.ComputeStepDelayMs(npc, run: false));
        Assert.Equal(250, ai.ComputeStepDelayMs(npc, run: true));
    }

    [Fact]
    public void OverrideMoveDelayIsTheWalkingDelayHalvedRunningAndMounted()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.SetTag("OVERRIDE.MOVEDELAY", "800");
        Assert.Equal(800, ai.ComputeStepDelayMs(npc, run: false));
        Assert.Equal(400, ai.ComputeStepDelayMs(npc, run: true));
        npc.SetStatFlag(StatFlag.OnHorse);
        Assert.Equal(400, ai.ComputeStepDelayMs(npc, run: false));
        Assert.Equal(200, ai.ComputeStepDelayMs(npc, run: true));
    }

    [Fact]
    public void TheNewStyleDelayIsClampedToATenthAndFiveSeconds()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.SetTag("OVERRIDE.MOVEDELAY", "20000");
        Assert.Equal(5000, ai.ComputeStepDelayMs(npc, run: false));
        npc.SetTag("OVERRIDE.MOVEDELAY", "20");
        Assert.Equal(100, ai.ComputeStepDelayMs(npc, run: false));
    }

    [Fact]
    public void OverrideMoveRateFoldsIntoDex()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 50);
        npc.SetTag("OVERRIDE.MOVERATE", "200");   // 100 - 50*200/100 = 0
        Assert.Equal(1000, ai.ComputeStepDelayMs(npc, run: false));
        Assert.Equal(250, ai.ComputeStepDelayMs(npc, run: true));
    }

    [Fact]
    public void TheOldStyleScalesByMoveRateAndIsNotClamped()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 100);
        npc.SetTag("OVERRIDE.MOVESTYLE", "1");
        npc.SetTag("OVERRIDE.MOVERATE", "4");
        Assert.Equal(40, ai.ComputeStepDelayMs(npc, run: false));   // 1000 * 4 / 100
        Assert.Equal(10, ai.ComputeStepDelayMs(npc, run: true));    // 250 * 4 / 100

        var cfg = new SphereConfig { OptionFlags = (int)OptionFlags.NpcMovementOldStyle };
        var (world2, ai2) = Make(cfg);
        var other = Npc(world2, dex: 100);
        other.SetTag("OVERRIDE.MOVERATE", "50");
        Assert.Equal(500, ai2.ComputeStepDelayMs(other, run: false));
    }

    [Fact]
    public void ARunningPetCountsAsDex75()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 10);
        npc.SetStatFlag(StatFlag.Pet);
        for (int i = 0; i < 200; i++)
            Assert.InRange(ai.ComputeStepDelayMs(npc, run: true), 250, 650);   // rand((100-75)/5)
    }

    // ---- 2. non-move re-tick (:2385-2394) -------------------------------------

    [Fact]
    public void ANonMoveTickWaitsOnTheDexFormula()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 50);   // (150-50)/2 = 50 -> rand(25..50) tenths, +1
        for (int i = 0; i < 200; i++)
            Assert.InRange(ai.ComputeRetickDelayMs(npc), 2600, 5100);

        npc.Dex = 150;
        Assert.Equal(100, ai.ComputeRetickDelayMs(npc));
        npc.Dex = 148;                    // timeout 1: no roll
        Assert.Equal(200, ai.ComputeRetickDelayMs(npc));
    }

    [Fact]
    public void AnIdleTickNoLongerRunsOnTheWalkFormula()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 20);
        npc.NpcBrain = NpcBrainType.Vendor;   // the old 3-5 s service cadence is gone too
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        world.PlaceCharacter(player, new Point3D(110, 110, 0, 0));
        world.AddOnlinePlayer(player);
        world.OnTick();

        long before = Environment.TickCount64;
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        // (150-20)/2 = 65 -> 32..65 tenths, + 1: at least 3.3 s unless a step was taken.
        if (npc.Position == new Point3D(100, 100, 0, 0))
            Assert.True(npc.NextNpcActionTime - before >= 1000);
    }

    // ---- 3. idle: NPC_Act_Idle (:1954-2057) ------------------------------------

    [Fact]
    public void AJumpyCreatureStartsToWander()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 100);      // rand(100-100) = 0 < 25
        npc.Stam = 0;                        // no special action
        Call(ai, "ActIdleChoose", npc);
        Assert.Equal(NpcAI.IdleMode.Wander, ai.GetIdleMode(npc));
    }

    [Fact]
    public void TheFireElementalLaysSourceXFire()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.BodyId = 0x000F;
        npc.MaxStam = 100; npc.Stam = 100;

        Assert.True((bool)Call(ai, "TryNpcSpecialAction", npc)!);

        var fire = world.GetItemsInRange(npc.Position, 0).Single(i => i.ItemType == ItemType.Fire);
        Assert.Contains(fire.BaseId, new ushort[] { 0x398C, 0x3996 });
        Assert.Equal((short)SpellType.FireField, fire.MoreP.X);
        Assert.InRange(fire.MoreP.Y, 100, 599);
        Assert.Equal(npc.Uid, fire.Link);
        Assert.InRange(npc.Stam, 91, 95);    // 5 + rand(5)
        Assert.False(fire.TryGetTag("FIELD_DAMAGE", out _));

        // Fire already under it: nothing more.
        Assert.False((bool)Call(ai, "TryNpcSpecialAction", npc)!);
    }

    [Fact]
    public void NpcSpecialActionReturnOneSkipsTheHardcodedSpecial()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.BodyId = 0x001C;
        int fired = 0;
        ai.OnNpcSpecialAction = _ => { fired++; return true; };
        Assert.True((bool)Call(ai, "TryNpcSpecialAction", npc)!);
        Assert.Equal(1, fired);
        Assert.Empty(world.GetItemsInRange(npc.Position, 0));
    }

    [Fact]
    public void TheFidgetIsAnIdleFlavorExtra()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 100);
        npc.Stam = 0;
        int fidgets = 0;
        ai.OnNpcFidget = _ => fidgets++;
        for (int i = 0; i < 200; i++)
        {
            ai.SetIdleMode(npc, NpcAI.IdleMode.None);
            Call(ai, "ActIdleChoose", npc);
        }
        Assert.Equal(0, fidgets);

        ai.Extras = NpcAiExtraFlags.IdleFlavor;
        for (int i = 0; i < 400 && fidgets == 0; i++)
        {
            ai.SetIdleMode(npc, NpcAI.IdleMode.None);
            Call(ai, "ActIdleChoose", npc);
        }
        Assert.True(fidgets > 0);
    }

    // ---- 4. wander: NPC_Act_Wander (:1224-1289) --------------------------------

    [Fact]
    public void TheWanderTriggerSeesAndOverridesStopAndGoHome()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.Home = new Point3D(110, 100, 0, 0);
        npc.HomeDist = 1;
        int seenHome = -1;
        ai.OnNpcActWander = (_, args) =>
        {
            seenHome = args.ReturnHome;
            args.Stop = 0;
            args.ReturnHome = 1;
            return false;
        };
        ai.SetIdleMode(npc, NpcAI.IdleMode.Wander);
        Call(ai, "ActWander", npc);

        Assert.Equal(1, seenHome);   // the step lands 9+ tiles from home, HOMEDIST 1
        Assert.Equal(NpcAI.IdleMode.GoHome, ai.GetIdleMode(npc));
        Assert.Equal(new Point3D(100, 100, 0, 0), npc.Position);
    }

    [Fact]
    public void TheWanderTriggerCanStopTheWanderAndReturnOneTakesNoStep()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        ai.OnNpcActWander = (_, args) => { args.Stop = 1; return false; };
        ai.SetIdleMode(npc, NpcAI.IdleMode.Wander);
        Call(ai, "ActWander", npc);
        Assert.Equal(NpcAI.IdleMode.None, ai.GetIdleMode(npc));

        ai.OnNpcActWander = (_, _) => true;
        ai.SetIdleMode(npc, NpcAI.IdleMode.Wander);
        Call(ai, "ActWander", npc);
        Assert.Equal(new Point3D(100, 100, 0, 0), npc.Position);
        Assert.Equal(NpcAI.IdleMode.Wander, ai.GetIdleMode(npc));
    }

    [Fact]
    public void HomeDistZeroIsNoWanderLeash()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.Home = new Point3D(150, 100, 0, 0);
        npc.HomeDist = 0;
        int seenHome = -1;
        ai.OnNpcActWander = (_, args) => { seenHome = args.ReturnHome; return true; };
        Call(ai, "ActWander", npc);
        Assert.Equal(0, seenHome);
    }

    // ---- 5. NPC_CheckWalkHere (CCharNPCStatus.cpp:544) --------------------------

    private static Item ItemAt(GameWorld world, ItemType type, short x, short y)
    {
        var item = world.CreateItem();
        item.BaseId = 0x1000;
        item.ItemType = type;
        world.PlaceItem(item, new Point3D(x, y, 0, 0));
        return item;
    }

    [Theory]
    [InlineData(ItemType.Trap)]
    [InlineData(ItemType.TrapActive)]
    [InlineData(ItemType.Moongate)]
    [InlineData(ItemType.Telepad)]
    public void NobodyStepsOnTrapsGatesOrPads(ItemType type)
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        ItemAt(world, type, 101, 100);
        Assert.False(ai.CheckWalkHere(npc, new Point3D(101, 100, 0, 0)));
        Assert.True(ai.CheckWalkHere(npc, new Point3D(102, 100, 0, 0)));
    }

    [Fact]
    public void AWebStopsAllButAGiantSpider()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        ItemAt(world, ItemType.Web, 101, 100);
        Assert.False(ai.CheckWalkHere(npc, new Point3D(101, 100, 0, 0)));
        npc.BodyId = 0x001C;
        Assert.True(ai.CheckWalkHere(npc, new Point3D(101, 100, 0, 0)));
    }

    [Fact]
    public void FireStopsAnythingNotFireImmune()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        ItemAt(world, ItemType.Fire, 101, 100);
        Assert.False(ai.CheckWalkHere(npc, new Point3D(101, 100, 0, 0)));
        // and the step itself is refused
        Call(ai, "MoveToward", npc, new Point3D(101, 100, 0, 0), false);
        Assert.NotEqual(new Point3D(101, 100, 0, 0), npc.Position);
    }

    // ---- 6. path: persistent path / teleport on GOTO failure (:1665-1759) -------

    [Fact]
    public void ABlockedGotoTeleportsAPlayableBodyWithoutPersistentPath()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.BodyId = 0x0190;
        Surround(world, npc);
        npc.ActP = new Point3D(110, 100, 0, 0);
        npc.Action = (SkillType)NpcAction.GoTo;

        Assert.True((bool)Call(ai, "RunScriptedAction", npc)!);
        Assert.Equal(new Point3D(110, 100, 0, 0), npc.Position);
    }

    [Fact]
    public void ABlockedGotoGoesIdleForAnythingElse()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        Surround(world, npc);
        npc.ActP = new Point3D(110, 100, 0, 0);
        npc.Action = (SkillType)NpcAction.GoTo;

        Assert.False((bool)Call(ai, "RunScriptedAction", npc)!);
        Assert.Equal(new Point3D(100, 100, 0, 0), npc.Position);
        Assert.Equal(SkillType.None, npc.Action);
    }

    [Fact]
    public void PersistentPathRetriesInsteadOfTeleporting()
    {
        var (world, ai) = Make();
        ai.Flags = NpcAIFlags.PersistentPath;
        var npc = Npc(world);
        npc.BodyId = 0x0190;
        Surround(world, npc);
        npc.ActP = new Point3D(110, 100, 0, 0);
        npc.Action = (SkillType)NpcAction.RunTo;

        Assert.False((bool)Call(ai, "RunScriptedAction", npc)!);
        Assert.Equal(new Point3D(100, 100, 0, 0), npc.Position);   // never teleported
        Assert.Equal(SkillType.None, npc.Action);
    }

    // ---- 7. talk: NPC_ActStart_SpeakTo (:243) / NPC_Act_Talk (:1446) -----------

    private static (GameWorld World, NpcAI Ai, Character Npc, Character Speaker) TalkBench(int fame = 0)
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        npc.NpcBrain = NpcBrainType.Human;
        npc.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        var speaker = world.CreateCharacter();
        speaker.IsPlayer = true;
        speaker.Name = "Bob";
        speaker.Fame = (short)fame;
        world.PlaceCharacter(speaker, new Point3D(102, 100, 0, 0));
        return (world, ai, npc, speaker);
    }

    [Fact]
    public void SpeakingToAnNpcStartsTheTalkState()
    {
        var (_, ai, npc, speaker) = TalkBench();
        long before = Environment.TickCount64;
        ai.NpcStartSpeakTo(npc, speaker);

        Assert.Equal((SkillType)NpcAction.Talk, npc.Action);
        Assert.Equal(speaker.Uid, npc.Act);
        Assert.Equal(Direction.East, npc.Direction & (Direction)0x07);
        Assert.True(npc.NextNpcActionTime >= before + 3000);
    }

    [Fact]
    public void AFamousSpeakerIsFollowed()
    {
        var (_, ai, npc, speaker) = TalkBench(fame: 8000);
        ai.NpcStartSpeakTo(npc, speaker);
        Assert.Equal((SkillType)NpcAction.TalkFollow, npc.Action);
    }

    [Fact]
    public void TheTalkWaitsTwentyTicksThenSaysGone()
    {
        var (_, ai, npc, speaker) = TalkBench();
        var said = new List<string>();
        ai.OnNpcSay = (_, text) => said.Add(text);
        ai.NpcStartSpeakTo(npc, speaker);

        int ticks = 0;
        while ((bool)Call(ai, "RunScriptedAction", npc)! && ticks < 100)
            ticks++;

        Assert.Equal(19, ticks);
        Assert.Single(said);
        Assert.Equal(SkillType.None, npc.Action);
    }

    [Fact]
    public void UnknownSpeechEndsTheTalkAfterFourLines()
    {
        var (_, ai, npc, speaker) = TalkBench();
        ai.NpcStartSpeakTo(npc, speaker);
        for (int i = 0; i < 4; i++)
            ai.NpcHearUnknown(npc, speaker);
        Assert.Equal((SkillType)NpcAction.Talk, npc.Action);
        ai.NpcHearUnknown(npc, speaker);
        Assert.Equal(SkillType.None, npc.Action);
    }

    [Fact]
    public void ANewSpeakerHearsTheInterruptLine()
    {
        var (world, ai, npc, speaker) = TalkBench();
        var said = new List<string>();
        ai.OnNpcSay = (_, text) => said.Add(text);
        ai.NpcStartSpeakTo(npc, speaker);
        var other = world.CreateCharacter();
        other.IsPlayer = true;
        other.Name = "Ann";
        world.PlaceCharacter(other, new Point3D(99, 100, 0, 0));

        ai.NpcHearBegin(npc, other);

        Assert.Single(said);
    }

    // ---- 8. FOLLOW_TARG / GUARD_TARG for any NPC -------------------------------

    [Fact]
    public void AScriptedFollowTargWalksTowardTheActTarget()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        var leader = Blocker(world, 105, 100);
        npc.Act = leader.Uid;
        npc.Action = (SkillType)NpcAction.FollowTarg;

        Assert.True((bool)Call(ai, "RunScriptedAction", npc)!);
        Assert.Equal(101, npc.X);

        npc.Act = Serial.Invalid;
        Assert.False((bool)Call(ai, "RunScriptedAction", npc)!);
        Assert.Equal(SkillType.None, npc.Action);
    }

    [Fact]
    public void AScriptedGuardTargTakesOnWhatTheGuardedIsFighting()
    {
        var (world, ai) = Make();
        var npc = Npc(world);
        var ward = Blocker(world, 102, 100);
        var foe = Blocker(world, 104, 100);
        foe.IsPlayer = true;
        ward.FightTarget = foe.Uid;
        npc.Act = ward.Uid;
        npc.Action = (SkillType)NpcAction.GuardTarg;

        Call(ai, "RunScriptedAction", npc);

        Assert.Equal(foe.Uid, npc.FightTarget);
    }

    // ---- 10. extras -------------------------------------------------------------

    [Fact]
    public void ReturnHomeSendsAParkedStrayHomeThroughTheVeto()
    {
        var (world, ai) = Make();
        var npc = Npc(world, x: 1000, y: 1000);
        npc.Home = new Point3D(1100, 1000, 0, 0);
        npc.HomeDist = 10;

        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        Assert.Equal((short)1000, npc.X);                 // off by default

        ai.Extras = NpcAiExtraFlags.ReturnHome;
        Character.OnNpcLostTeleport = (_, _) => true;     // the script refuses
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        Assert.Equal((short)1000, npc.X);

        Character.OnNpcLostTeleport = null;
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        Assert.Equal(npc.Home, npc.Position);
    }

    [Fact]
    public void HurtSlowdownIsAnExtra()
    {
        var (world, ai) = Make();
        var npc = Npc(world, dex: 150);                   // non-move re-tick: 100 ms
        npc.NpcBrain = NpcBrainType.Human;
        npc.Hits = 5;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        world.PlaceCharacter(player, new Point3D(110, 110, 0, 0));
        world.AddOnlinePlayer(player);
        world.OnTick();
        npc.SetStatFlag(StatFlag.Freeze);                // no step, no idle wait

        long t0 = Environment.TickCount64;
        npc.NextNpcActionTime = 0;
        ai.Extras = NpcAiExtraFlags.HurtSlowdown;
        ai.OnTickAction(npc);
        Assert.True(npc.NextNpcActionTime - t0 >= 100 + 400);   // re-tick + the 400 ms extra
    }

    // ---- 9. pet food tick: OnTickFood (CCharAct.cpp:5748) ------------------------

    private static (GameWorld World, Character Owner, Character Pet) PetBench()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = world.CreateCharacter();
        pet.MaxHits = 50; pet.Hits = 50;
        pet.NpcBrain = NpcBrainType.Animal;
        // An NPC with no food ceiling never hungers (m_MaxFood, CCharBase.cpp:26).
        pet.SetTag("MAXFOOD", "60");
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        pet.TryAssignOwnership(owner, owner);
        return (world, owner, pet);
    }

    [Fact]
    public void WithoutHitsHungerLossAStarvingPetKeepsItsOwner()
    {
        var (_, owner, pet) = PetBench();
        pet.Food = 1;
        pet.SetNextFoodTick(1);
        pet.TickPetOwnershipTimers(1_000_000);
        Assert.Equal(0, pet.Food);
        Assert.True(pet.HasOwner(owner.Uid));
    }

    [Fact]
    public void TheFoodTickRunsOnTheRegenRateNotAMinute()
    {
        var (_, _, pet) = PetBench();
        pet.Food = 30;
        pet.TickPetOwnershipTimers(1_000_000);            // arms the clock
        pet.TickPetOwnershipTimers(1_000_000 + 61_000);   // a minute is not enough
        Assert.Equal(30, pet.Food);
        pet.TickPetOwnershipTimers(1_000_000 + 3_600_000);
        Assert.Equal(29, pet.Food);
    }

    [Fact]
    public void AHirelingPaysPerFoodTickAndLeavesWhenBroke()
    {
        var (_, owner, pet) = PetBench();
        pet.SetTag("HIRE_WAGE", "100");
        pet.SetTag("HIRE_BALANCE", "3");
        pet.Food = 30;

        pet.SetNextFoodTick(1);
        pet.TickPetOwnershipTimers(1_000_000);
        Assert.True(pet.TryGetTag("HIRE_BALANCE", out string? balance));
        Assert.Equal("2", balance);
        Assert.Equal(29, pet.Food);
        Assert.True(pet.HasOwner(owner.Uid));

        int deserted = 0;
        Character.OnPetDesert = (_, _) => { deserted++; return false; };
        pet.SetNextFoodTick(1);
        pet.TickPetOwnershipTimers(2_000_000);            // 2 > 1: pays
        pet.SetNextFoodTick(1);
        pet.TickPetOwnershipTimers(3_000_000);            // 1 is not > 1: leaves
        Assert.Equal(1, deserted);
        Assert.False(pet.OwnerSerial.IsValid);
    }
}
