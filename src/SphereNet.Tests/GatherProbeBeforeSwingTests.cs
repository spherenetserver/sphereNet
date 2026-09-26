using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Asking the vein before swinging at it.
///
/// Upstream runs the resource check at EVERY stage of a gathering skill and refuses
/// outright at SKTRIG_START when the tile holds no node or an empty one
/// (Skill_Mining, CCharSkill.cpp:1448-1459) - so a spent vein is answered before a
/// single stroke is scheduled and nothing is animated.
///
/// This engine consulted the resource only when the swing FINISHED. Working an
/// exhausted vein therefore played the whole two-to-six stroke animation, sound and
/// all, and only then said there was nothing there - the same for fishing and
/// lumberjacking, which share the stroke machinery.
///
/// The probe is the START half: it binds the node exactly as the real gather does -
/// upstream creates the resource bit at START too - but rolls no skill, fires no
/// @ResourceGather and takes nothing out of the pool.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GatherProbeBeforeSwingTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_probe_{Guid.NewGuid():N}.scp");

    public GatherProbeBeforeSwingTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static readonly Point3D Tile = new(100, 100, 0, 0);

    private (GameWorld World, GatheringEngine Engine, Character Miner) Setup(string reap, int amount)
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, $$"""
            [ITEMDEF 019b7]
            DEFNAME=i_ore_iron
            NAME=Iron Ore
            TYPE=t_ore

            [REGIONRESOURCE r_probe_ore]
            DEFNAME=r_probe_ore
            AMOUNT={{amount}}
            REAP={{reap}}
            REAPAMOUNT=1
            SKILL=0.0
            REGEN=60*60*10

            [REGIONTYPE r_probe_rock t_rock]
            DEFNAME=r_probe_rock
            RESOURCES=100.0 r_probe_ore
            """);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        TestHarness.AttachLoadedRegionTypes(world);
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return (world, new GatheringEngine(world), miner);
    }

    [Fact]
    public void AFullVeinAnswersYes()
    {
        var (_, engine, miner) = Setup("i_ore_iron", amount: 5);

        var probe = engine.ProbeResource(miner, SkillType.Mining, Tile);

        _out.WriteLine($"handled={probe.Handled} success={probe.Success} depleted={probe.Depleted}");
        Assert.True(probe.Handled);
        Assert.True(probe.Success);
        Assert.False(probe.Depleted);
    }

    [Fact]
    public void TheProbeTakesNothingOutOfThePool()
    {
        // Ten probes then a real swing: the swing must still find ore. A probe that
        // consumed would be worse than the bug it fixes.
        var (_, engine, miner) = Setup("i_ore_iron", amount: 5);
        for (int i = 0; i < 10; i++)
            engine.ProbeResource(miner, SkillType.Mining, Tile);

        var real = engine.TryGatherForSink(miner, SkillType.Mining, Tile);
        _out.WriteLine($"after ten probes the swing gave: success={real.Success}");
        Assert.True(real.Success);
        Assert.NotNull(real.Item);
    }

    [Fact]
    public void AnExhaustedVeinAnswersDepleted()
    {
        var (_, engine, miner) = Setup("i_ore_iron", amount: 1);
        // Take the one thing it had.
        Assert.True(engine.TryGatherForSink(miner, SkillType.Mining, Tile).Success);

        var probe = engine.ProbeResource(miner, SkillType.Mining, Tile);
        _out.WriteLine($"spent vein -> handled={probe.Handled} depleted={probe.Depleted}");
        Assert.True(probe.Handled);
        Assert.True(probe.Depleted);
    }

    [Fact]
    public void ABarrenSpotAnswersNo()
    {
        // REAP=0 is the pack's "nothing can be found here" node; it is bound to the
        // tile for as long as the node lives, and no swing should be started on it.
        var (_, engine, miner) = Setup("0", amount: 5);

        var probe = engine.ProbeResource(miner, SkillType.Mining, Tile);
        _out.WriteLine($"barren -> handled={probe.Handled} success={probe.Success}");
        Assert.True(probe.Handled);
        Assert.False(probe.Success);
    }
}
