using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// TAG.OVERRIDE.SPIDERWEB inverts the body check; it is not a flag.
///
/// With the key present a creature that is NOT a giant spider leaves webs, and
/// without it only a giant spider does (CCharNPCAct.cpp:2007-2022). The VALUE is
/// never read - which is why the shipped packs write both 0 and 1 and mean the same
/// thing: the reference distribution puts it on ten creatures to give them webs, and
/// the live pack puts it on a spider to take its webs away.
///
/// Nothing read the key at all. Webbing came from the body id and from this engine's
/// own WEBTRAIL tag, so the ten creatures never webbed and the one spider never
/// stopped.
/// </summary>
public sealed class SpiderWebOverrideTests
{
    private const ushort GiantSpiderBody = 0x001C;
    private const ushort WebTile = 0x10D5;

    /// <summary>Run the trail step until the 1-in-4 roll lands.
    ///
    /// The roll uses Random.Shared and cannot be seeded, so the count is the
    /// guarantee instead: 120 tries miss with probability (3/4)^120, which is about
    /// one in 10^15. The NEGATIVE direction needs no such argument - a creature that
    /// does not web returns before the roll is made, so it is decided, not sampled.</summary>
    private static bool LeavesAWebTrail(ushort bodyId, bool withOverride)
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ai = new NpcAI(world, new SphereConfig());

        var npc = world.CreateCharacter();
        npc.BodyId = bodyId;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        if (withOverride)
            npc.SetTag("OVERRIDE.SPIDERWEB", "0");   // the value is never read

        var step = typeof(NpcAI).GetMethod("TryDropSpecialTrail",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 120; i++)
            step.Invoke(ai, [npc]);

        return world.GetItemsInRange(npc.Position, 0).Any(it => it.BaseId == WebTile);
    }

    /// <summary>The default: a giant spider webs, nothing else does.</summary>
    [Fact]
    public void WithoutTheKeyOnlyAGiantSpiderWebs()
    {
        Assert.True(LeavesAWebTrail(GiantSpiderBody, withOverride: false));
        Assert.False(LeavesAWebTrail(0x0001, withOverride: false));
    }

    /// <summary>And the key turns it around, in both directions.</summary>
    [Fact]
    public void TheKeyInvertsTheBodyCheck()
    {
        // The reference pack's ten creatures: not spiders, key present, they web.
        Assert.True(LeavesAWebTrail(0x0001, withOverride: true));
        // The live pack's spider: key present, it stops.
        Assert.False(LeavesAWebTrail(GiantSpiderBody, withOverride: true));
    }
}
