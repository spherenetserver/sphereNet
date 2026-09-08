using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The four triggers a real script pack asks for and the engine did not fire
/// (port plan İŞ-9, measured in PLAN-206).
///
/// @RegionResourceFound and @ResourceFound announce a vein the moment it is found;
/// @RegionResourceGather rides alongside @ResourceGather as a swing takes from it; and
/// @HitReactive describes what Reactive Armour is about to bounce back. A hook nothing
/// fires is a silent no-op - the shard's script is written, loaded, and simply never
/// runs - so each of these is checked by making a script observe or change something.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResourceAndReactiveTriggerTests
{
    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required GatheringEngine Engine { get; init; }
        public required Character Miner { get; init; }
    }

    private static Rig Setup(string resourceBlocks = "", string charEvents = "")
    {
        var lf = LoggerFactory.Create(_ => { });
        string script = $"""
            [ITEMDEF 019b9]
            NAME=iron ore

            [REGIONRESOURCE r_found_ore]
            DEFNAME=r_found_ore
            AMOUNT=10
            REAP=0x19B9
            REAPAMOUNT=4
            SKILL=0.0
            {resourceBlocks}

            [REGIONTYPE r_found_rock t_rock]
            DEFNAME=r_found_rock
            RESOURCES=100.0 r_found_ore

            [EVENTS e_found_watch]
            {charEvents}
            """;

        string path = Path.Combine(Path.GetTempPath(), $"sphnet_rf_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        // The host builds this after loading scripts; without it every trigger counts as
        // "might be hooked" and the two-trigger return contract cannot be observed.
        dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        var miner = world.CreateCharacter();
        miner.Events.Add(resources.ResolveDefName("e_found_watch"));
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        Item.CreateTriggerHook = _ => { };

        return new Rig
        {
            World = world,
            Engine = new GatheringEngine(world, dispatcher),
            Miner = miner,
        };
    }

    private static GatherResult Gather(Rig rig) =>
        rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, new Point3D(100, 100, 0, 0));

    // ------------------------------------------------- finding a vein

    [Fact]
    public void TheGathererIsToldWhenAVeinIsFound()
    {
        var rig = Setup(charEvents: """
            ON=@RegionResourceFound
            TAG.FOUND=1
            TAG.FOUND_UID=<ARGO.UID>
            """);

        Gather(rig);

        Assert.True(rig.Miner.TryGetTag("FOUND", out string? found));
        Assert.Equal("1", found);
        // ARGO is the vein marker itself, which is how a script recognises the spot.
        Assert.True(rig.Miner.TryGetTag("FOUND_UID", out string? uid));
        Assert.False(string.IsNullOrWhiteSpace(uid));
        Assert.NotEqual("0", uid);
    }

    [Fact]
    public void TheResourceDefinitionIsToldToo()
    {
        var rig = Setup(resourceBlocks: """
            ON=@ResourceFound
            SRC.TAG.DEF_SAW_IT=1
            """);

        Gather(rig);

        Assert.True(rig.Miner.TryGetTag("DEF_SAW_IT", out string? saw));
        Assert.Equal("1", saw);
    }

    [Fact]
    public void ReturningOneEmptiesTheVeinInsteadOfRemovingIt()
    {
        // The reference sets the bit's amount to zero: the spot was searched and holds
        // nothing, which is not the same as never having looked.
        var rig = Setup(resourceBlocks: """
            ON=@ResourceFound
            RETURN 1
            """);

        var result = Gather(rig);

        Assert.True(result.Handled);
        Assert.Null(result.Item);
        // A second swing finds the same spent vein rather than rolling a fresh one.
        var again = Gather(rig);
        Assert.True(again.Depleted);
    }

    [Fact]
    public void TheFoundTriggersOnlyRunWhenTheVeinIsNew()
    {
        var rig = Setup(charEvents: """
            ON=@RegionResourceFound
            TAG.TIMES=<EVAL <TAG0.TIMES>+1>
            """);

        Gather(rig);
        Gather(rig);
        Gather(rig);

        Assert.True(rig.Miner.TryGetTag("TIMES", out string? times));
        Assert.Equal("1", times);
    }

    // ------------------------------------------------- taking from it

    [Fact]
    public void TheGathererIsToldOnEverySwingThatTakes()
    {
        var rig = Setup(charEvents: """
            ON=@RegionResourceGather
            TAG.TOOK=<ARGN1>
            """);

        var result = Gather(rig);

        Assert.True(result.Success);
        Assert.True(rig.Miner.TryGetTag("TOOK", out string? took));
        Assert.Equal("4", took);          // ARGN1 is the amount, seeded from REAPAMOUNT
    }

    [Fact]
    public void TheCharSideTriggerCanCancelTheReap()
    {
        var rig = Setup(charEvents: """
            ON=@RegionResourceGather
            RETURN 1
            """);

        var result = Gather(rig);

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public void TheDefinitionsOwnGatherBlockHasTheLastWord()
    {
        // Both triggers share one return value and the later one overwrites it - the
        // reference's own structure (CCharSkill.cpp:1034-1038). A char-level veto is
        // therefore overruled by a definition that answers afterwards.
        var rig = Setup(
            resourceBlocks: """
            ON=@ResourceGather
            RETURN 0
            """,
            charEvents: """
            ON=@RegionResourceGather
            RETURN 1
            """);

        var result = Gather(rig);

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
    }

    [Fact]
    public void TheCharSideTriggerCanChangeTheAmount()
    {
        var rig = Setup(charEvents: """
            ON=@RegionResourceGather
            ARGN1=2
            """);

        var result = Gather(rig);

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.Equal((ushort)2, result.Item!.Amount);
    }

    // ------------------------------------------------- reactive armour

    private static (Character Attacker, Character Defender) Fighters(GameWorld world, int attackerX = 100)
    {
        var attacker = world.CreateCharacter();
        attacker.PrivLevel = PrivLevel.GM;          // a guaranteed hit
        attacker.Str = 100; attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D((short)attackerX, 100, 0, 0));
        var defender = world.CreateCharacter();
        defender.Str = 100; defender.MaxHits = 200; defender.Hits = 200;
        defender.SetSkill(SkillType.Parrying, 0);
        world.PlaceCharacter(defender, new Point3D(101, 100, 0, 0));
        return (attacker, defender);
    }

    /// <summary>Swing once for exactly <paramref name="damage"/>, so the reflection
    /// arithmetic is readable instead of rolled.</summary>
    private static void Swing(Character attacker, Character defender, int damage = 40)
    {
        CombatEngine.OnHitDamage = _ => damage;
        try
        {
            CombatEngine.ResolveAttack(attacker, defender, null);
        }
        finally { CombatEngine.OnHitDamage = null; }
    }

    [Fact]
    public void TheFlagAloneBouncesNothing()
    {
        // Upstream reads the percentage off the worn reactive memory; with no memory
        // (or a definition that asks for none) the blow passes through untouched.
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);

        Swing(attacker, defender);

        Assert.Equal(100, attacker.Hits);
        Assert.Equal(160, defender.Hits);
    }

    [Fact]
    public void ThePercentageComesOffTheBlowAndLandsOnTheAttacker()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        Swing(attacker, defender);

        // Half bounced: the attacker took it and the defender was spared it.
        Assert.Equal(80, attacker.Hits);
        Assert.Equal(180, defender.Hits);
    }

    [Fact]
    public void AScriptRewritesTheWholeBounce()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world);
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        CombatEngine.ReactiveArmorContext? seen = null;
        CombatEngine.OnReactiveArmorTrigger = ctx =>
        {
            seen = ctx;
            ctx.Reflect = 5;      // the attacker gets off lightly
            ctx.Reduce = 30;      // but the wearer is spared more
        };
        try
        {
            Swing(attacker, defender);
        }
        finally { CombatEngine.OnReactiveArmorTrigger = null; }

        Assert.NotNull(seen);
        Assert.Equal(40, seen!.Damage);      // the blow as it stood
        Assert.Equal(20, seen.Bounce);       // what the percentage worked out to
        Assert.Equal(95, attacker.Hits);     // 5 reflected
        Assert.Equal(190, defender.Hits);    // 30 taken off the blow
    }

    [Fact]
    public void AnAttackerOutOfReachIsNotTouched()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, defender) = Fighters(world, attackerX: 120);   // well away
        defender.SetStatFlag(StatFlag.Reactive);
        defender.ReactiveArmorPercent = 50;

        Swing(attacker, defender);

        Assert.Equal(100, attacker.Hits);    // upstream requires distance <= 2
        Assert.Equal(160, defender.Hits);    // and the blow is not reduced either
    }
}
