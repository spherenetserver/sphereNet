using System;
using System.IO;
using System.Linq;
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

namespace SphereNet.Tests;

/// <summary>
/// A spot that answered "nothing" stays that way (port plan İŞ-27 / PLAN-404).
///
/// The reference creates the resource bit even when the group drew mr_nothing, and
/// arms its decay from THAT definition's REGEN (CWorldMap.cpp:119/131/148); only
/// afterwards does Skill_NaturalResource_Create refuse, because the definition reaps
/// nothing (CCharSkill.cpp:1012). The barren draw is therefore bound to the tile for
/// as long as the node lives.
///
/// This engine threw the barren draw away and rolled the group again on the next
/// swing. The live pack counts on the other behaviour: its mr_nothing carries
/// REGEN=60*60*10 with the comment "Nothing can be found at this location for this
/// many seconds", and weights the draw at 60% for water - so a fisherman could stand
/// on one tile and re-roll that 60% every cast until it paid out.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BarrenResourceNodeParityTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_barren_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static readonly Point3D Tile = new(100, 100, 0, 0);

    private sealed record Rig(GameWorld World, GatheringEngine Engine, Character Miner);

    /// <summary>A region whose ONLY resource is the barren one, so the draw is not a
    /// matter of luck: every swing here must answer "nothing".</summary>
    private Rig SetupBarrenOnly()
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, """
            [REGIONRESOURCE r_barren_nothing]
            DEFNAME=r_barren_nothing
            REAP=0
            REGEN=60*60*10

            [REGIONTYPE r_barren_rock t_rock]
            DEFNAME=r_barren_rock
            RESOURCES=100.0 r_barren_nothing
            """);

        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return new Rig(world, new GatheringEngine(world), miner);
    }

    private static GatherResult Gather(Rig rig) =>
        rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, Tile);

    private static Item? Marker(Rig rig) =>
        rig.World.GetItemsInRange(Tile, 0)
            .FirstOrDefault(i => i.BaseId == GatheringEngine.MarkerGraphic);

    [Fact]
    public void ABarrenDrawLeavesANodeBehind()
    {
        var rig = SetupBarrenOnly();

        var result = Gather(rig);

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.NotNull(Marker(rig));      // the spot was written down, not forgotten
    }

    [Fact]
    public void TheBarrenNodeCarriesItsOwnRegenWindow()
    {
        // REGEN=60*60*10 is an hour in tenths, which is what the pack's comment
        // means by "nothing can be found here for this many seconds".
        var rig = SetupBarrenOnly();

        Gather(rig);

        var marker = Marker(rig);
        Assert.NotNull(marker);
        long remaining = marker!.DecayTime - Environment.TickCount64;
        Assert.InRange(remaining, 3_500_000L, 3_600_000L);
    }

    [Fact]
    public void TheSpotKeepsAnsweringNothingWhileThatNodeLives()
    {
        var rig = SetupBarrenOnly();

        for (int swing = 0; swing < 25; swing++)
        {
            var result = Gather(rig);
            Assert.True(result.Handled);
            Assert.False(result.Success, $"swing {swing} produced something on a barren tile");
        }

        // ...and it is still the SAME node, not a fresh one per swing.
        Assert.Single(rig.World.GetItemsInRange(Tile, 0),
            i => i.BaseId == GatheringEngine.MarkerGraphic);
    }
}
