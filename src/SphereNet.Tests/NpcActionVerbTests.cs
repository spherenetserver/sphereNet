using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The NPC action verb table (CCharNPC::sm_szVerbKeys, CCharNPCAct.cpp:44).
///
/// Eleven of its sixteen names answered nothing at all, so every line the packs
/// write with one - I.LEAVE, RUNTO, SHRINK, PETRETRIEVE - was a no-op with no error
/// behind it. Four of them start an ACTION, which upstream parks in the same slot a
/// skill uses and runs before the brain decides anything; this engine had no such
/// channel, which is why they could not simply be "added as keys".
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcActionVerbTests
{
    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => Source;
        public Character? Source;
    }

    private static GameWorld NewWorld()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.IsOnline = true;
        world.PlaceCharacter(observer, new Point3D(102, 108, 0, 0));
        world.AddOnlinePlayer(observer);
        world.OnTick();   // wake the sector so masterless NPCs act
        return world;
    }

    private static Character NewNpc(GameWorld world, Point3D at)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        npc.Dex = 100;
        // Dex raises the stamina CEILING and leaves the pool empty, and an NPC with a
        // pool it cannot draw on counts as exhausted and never steps.
        npc.Stam = npc.MaxStam;
        world.PlaceCharacter(npc, at);
        return npc;
    }

    private static void Tick(NpcAI ai, Character npc, int times)
    {
        for (int i = 0; i < times; i++)
        {
            npc.NextNpcActionTime = 0;
            ai.OnTickAction(npc);
        }
    }

    /// <summary>Tick until the NPC stands on the point its action was given, and no
    /// further.
    ///
    /// The tick that ENDS an action also hands the NPC back to its brain - upstream's
    /// NPC_Act_Goto calls NPC_Act_Idle the moment it arrives (CCharNPCAct.cpp:1712),
    /// and this engine follows it: RunScriptedAction returns false on completion and
    /// OnTickAction carries on into the brain dispatch in that same tick. So the
    /// brain may take a wander step on the arrival tick, and a test that ticks until
    /// the action clears and THEN reads the position is measuring the wander. That is
    /// what made this class fail about one run in six.</summary>
    private static void TickUntilAt(NpcAI ai, Character npc, int x, int y, int limit = 40)
    {
        for (int i = 0; i < limit && (npc.X != x || npc.Y != y); i++)
        {
            npc.NextNpcActionTime = 0;
            ai.OnTickAction(npc);
        }
    }

    /// <summary>GOTO names a destination and the NPC walks to it. Before the action
    /// channel existed the verb had nowhere to put the request.</summary>
    [Fact]
    public void GotoWalksTheNpcToTheNamedPointAndStopsThere()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        Assert.True(npc.TryExecuteCommand("GOTO", "104,100,0,0", new Console(), out bool owned));
        Assert.True(owned);
        Assert.Equal((SkillType)NpcAction.GoTo, npc.Action);

        TickUntilAt(ai, npc, 104, 100);

        Assert.Equal(104, npc.X);
        Assert.Equal(100, npc.Y);

        // One more tick: standing on the destination, the action releases the NPC
        // back to its brain. The position is deliberately not read again - the brain
        // is free to move it from here, and that is the correct behaviour.
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        Assert.Equal(SkillType.None, npc.Action);
    }

    /// <summary>RUNTO is the same destination, taken at a run.
    ///
    /// The running BIT is not observable on a character here - the Direction setter
    /// keeps only the three facing bits - so what running means in this engine is the
    /// step cadence. A RUNTO left on the idle cadence would be a GOTO by another
    /// name, so the test is that it schedules itself on the active one.</summary>
    [Fact]
    public void RuntoMovesTheNpcOnTheRunningCadence()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var walker = NewNpc(world, new Point3D(100, 100, 0, 0));
        var runner = NewNpc(world, new Point3D(100, 104, 0, 0));

        walker.TryExecuteCommand("GOTO", "112,100,0,0", new Console(), out _);
        runner.TryExecuteCommand("RUNTO", "112,104,0,0", new Console(), out _);
        Assert.Equal((SkillType)NpcAction.GoTo, walker.Action);
        Assert.Equal((SkillType)NpcAction.RunTo, runner.Action);

        long before = Environment.TickCount64;
        walker.NextNpcActionTime = 0;
        ai.OnTickAction(walker);
        runner.NextNpcActionTime = 0;
        ai.OnTickAction(runner);

        Assert.True(runner.NextNpcActionTime - before < walker.NextNpcActionTime - before,
            "a RUNTO must come round again sooner than a GOTO");

        // And it still arrives, which is the part that matters to the script.
        TickUntilAt(ai, runner, 112, 104, 60);
        Assert.Equal(112, runner.X);
        Assert.Equal(104, runner.Y);

        runner.NextNpcActionTime = 0;
        ai.OnTickAction(runner);
        Assert.Equal(SkillType.None, runner.Action);
    }

    /// <summary>WALK and RUN name a DIRECTION, not a destination: one tile that way
    /// (CCharNPCAct.cpp:190/228).</summary>
    [Fact]
    public void WalkTakesOneStepInTheNamedDirection()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        npc.TryExecuteCommand("WALK", "E", new Console(), out _);
        TickUntilAt(ai, npc, 101, 100);

        Assert.Equal(101, npc.X);
        Assert.Equal(100, npc.Y);

        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
        Assert.Equal(SkillType.None, npc.Action);   // one tile, then done
    }

    /// <summary>LEAVE and FLEE start a step-counted retreat from whoever the NPC is
    /// dealing with. The argument is the step budget; zero means upstream's default
    /// of twenty (CCharNPCAct.cpp:165-174).</summary>
    [Fact]
    public void LeaveRetreatsFromTheCharacterTheNpcIsDealingWith()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        npc.Act = player.Uid;   // who it was talking to - upstream's m_Act_UID

        Assert.True(npc.TryExecuteCommand("LEAVE", "", new Console(), out _));
        Assert.Equal((SkillType)NpcAction.Flee, npc.Action);
        Assert.Equal(20, npc.FleeStepsMax);   // the default, not zero

        int before = npc.Position.GetDistanceTo(player.Position);
        Tick(ai, npc, 6);
        Assert.True(npc.Position.GetDistanceTo(player.Position) > before,
            "the NPC must actually put distance between itself and the speaker");
    }

    /// <summary>The budget is honoured: the retreat ends and the NPC is its own
    /// again, rather than fleeing forever.</summary>
    [Fact]
    public void AFleeEndsWhenItsStepBudgetRunsOut()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        npc.Act = player.Uid;

        npc.TryExecuteCommand("FLEE", "3", new Console(), out _);
        Assert.Equal(3, npc.FleeStepsMax);

        Tick(ai, npc, 10);
        Assert.Equal(SkillType.None, npc.Action);
        Assert.Equal(0, npc.FleeStepsCurrent);
    }

    /// <summary>With nobody to flee from the action simply ends - "free to do as I
    /// wish" (NPC_Act_Follow, CCharNPCAct.cpp:1349-1352) - instead of leaving the
    /// NPC stuck in an action it cannot carry out.</summary>
    [Fact]
    public void AFleeWithNobodyToFleeFromReleasesTheNpc()
    {
        var world = NewWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        npc.TryExecuteCommand("FLEE", "", new Console(), out _);
        Assert.Equal((SkillType)NpcAction.Flee, npc.Action);

        Tick(ai, npc, 1);
        Assert.Equal(SkillType.None, npc.Action);
    }

    /// <summary>BYE drops the running action and forgets who the NPC was dealing
    /// with (NV_BYE, CCharNPCAct.cpp:163).</summary>
    [Fact]
    public void ByeEndsTheInteraction()
    {
        var world = NewWorld();
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));

        npc.Act = player.Uid;
        npc.TryExecuteCommand("FLEE", "", new Console(), out _);

        Assert.True(npc.TryExecuteCommand("BYE", "", new Console(), out _));
        Assert.Equal(SkillType.None, npc.Action);
        Assert.False(npc.Act.IsValid);
    }

    /// <summary>A destination that names neither coordinates nor a region leaves the
    /// NPC alone. The verb name is still the NPC's - a mistyped GOTO is not an
    /// unknown key - but it must not send the NPC somewhere arbitrary.</summary>
    [Fact]
    public void AGotoToANameThatResolvesToNothingStartsNoAction()
    {
        var world = NewWorld();
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        Assert.True(npc.TryExecuteCommand("GOTO", "no_such_region_at_all", new Console(), out bool owned));
        Assert.True(owned);
        Assert.Equal(SkillType.None, npc.Action);
    }

    /// <summary>These are NPC verbs. On a player the name is not owned, so the
    /// interpreter keeps looking - which is what upstream's "return false" out of the
    /// NPC dispatcher means, and what keeps a player's own FLEE/RUN from being eaten
    /// here.</summary>
    [Theory]
    [InlineData("LEAVE")]
    [InlineData("FLEE")]
    [InlineData("GOTO")]
    [InlineData("RUNTO")]
    [InlineData("WALK")]
    [InlineData("RUN")]
    [InlineData("HIRE")]
    [InlineData("TRAIN")]
    [InlineData("SHRINK")]
    [InlineData("PETSTABLE")]
    [InlineData("PETRETRIEVE")]
    public void APlayerDoesNotOwnTheNpcActionVerbs(string verb)
    {
        var world = NewWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        player.TryExecuteCommand(verb, "1", new Console(), out bool owned);
        Assert.False(owned, $"{verb} must not be owned by a player");
    }

    /// <summary>And on an NPC every one of them IS owned - the measurement the sweep
    /// makes, stated directly.</summary>
    [Theory]
    [InlineData("LEAVE")]
    [InlineData("FLEE")]
    [InlineData("GOTO")]
    [InlineData("RUNTO")]
    [InlineData("WALK")]
    [InlineData("RUN")]
    [InlineData("HIRE")]
    [InlineData("TRAIN")]
    [InlineData("SHRINK")]
    [InlineData("PETSTABLE")]
    [InlineData("PETRETRIEVE")]
    public void AnNpcOwnsEveryNpcActionVerb(string verb)
    {
        var world = NewWorld();
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));

        npc.TryExecuteCommand(verb, "1", new Console(), out bool owned);
        Assert.True(owned, $"nothing owns {verb} on an NPC");
    }

    /// <summary>The engine-backed verbs reach their engine, carrying the speaker.
    /// Wired in the host, so what is checked here is that the object layer resolves
    /// the source character and hands it over rather than dropping it.</summary>
    [Fact]
    public void TheEngineBackedVerbsPassTheSpeakerThrough()
    {
        var world = NewWorld();
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        var console = new Console { Source = player };

        Character? hireSrc = null, stableSrc = null, retrieveSrc = null, shrinkSrc = null;
        string? trainArg = null;
        bool shrinkToPack = false;
        Character.NpcHireQuote = (_, s) => { hireSrc = s; return true; };
        Character.NpcStablePetSelect = (_, s) => { stableSrc = s; return true; };
        Character.NpcStablePetRetrieve = (_, s) => { retrieveSrc = s; return true; };
        Character.NpcTrainOffer = (_, _, a) => { trainArg = a; return true; };
        Character.NpcShrink = (_, s, pack) => { shrinkSrc = s; shrinkToPack = pack; return true; };

        npc.TryExecuteCommand("HIRE", "", console, out _);
        npc.TryExecuteCommand("PETSTABLE", "", console, out _);
        npc.TryExecuteCommand("PETRETRIEVE", "", console, out _);
        npc.TryExecuteCommand("TRAIN", "Blacksmithing", console, out _);
        npc.TryExecuteCommand("SHRINK", "1", console, out _);

        Assert.Same(player, hireSrc);
        Assert.Same(player, stableSrc);
        Assert.Same(player, retrieveSrc);
        Assert.Same(player, shrinkSrc);
        Assert.Equal("Blacksmithing", trainArg);
        // An argument means "into my pack" rather than onto the ground.
        Assert.True(shrinkToPack);

        npc.TryExecuteCommand("SHRINK", "", console, out _);
        Assert.False(shrinkToPack);
    }

    /// <summary>A bare BUY / SELL said to an NPC opens the shop for SRC (NV_BUY /
    /// NV_SELL, CCharNPCAct.cpp:147-211) - the verbs a pack's "buy"/"sell" SPEECH
    /// runs; with an argument they still name @NPCRestock's stock template.</summary>
    [Fact]
    public void BareBuyAndSellOpenTheShopForTheSpeaker()
    {
        var world = NewWorld();
        var npc = NewNpc(world, new Point3D(100, 100, 0, 0));
        npc.NpcBrain = NpcBrainType.Vendor;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        var console = new Console { Source = player };
        var opened = new List<(Character? Src, bool Buy)>();
        Character.NpcOpenShop = (_, s, buy) => { opened.Add((s, buy)); return true; };

        npc.TryExecuteCommand("BUY", "", console, out _);
        npc.TryExecuteCommand("SELL", "", console, out _);
        npc.TryExecuteCommand("SELL", "vendor_s_unknown", console, out _);

        Assert.Equal(2, opened.Count);
        Assert.Same(player, opened[0].Src);
        Assert.True(opened[0].Buy);
        Assert.False(opened[1].Buy);
    }
}
