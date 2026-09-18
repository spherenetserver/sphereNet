using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Tests;

/// <summary>
/// A vendor is a HEALER, a BANKER, a VENDOR or a STABLE brain.
///
/// That is upstream's own set, in full (CCharNPC::IsVendor, CCharNPC.cpp:253). Accepting
/// only the brain literally called Vendor left the stablemaster and the animal trainer -
/// both brain=Stable in the shipped pack - out of every path that asks the question:
/// they answered nothing when a customer said "buy" standing right next to them, while a
/// shop down the street, whose brain happened to be Vendor, opened its window instead.
///
/// The name-keyword fallback stays for Human/None, which is how this pack writes most of
/// its shopkeepers.
/// </summary>
public sealed class VendorBrainSetTests
{
    private static SphereNet.Game.Objects.Characters.Character Npc(
        SphereNet.Game.World.GameWorld world, NpcBrainType brain, string name)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = false;
        ch.NpcBrain = brain;
        ch.Name = name;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    private static SphereNet.Game.World.GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        return world;
    }

    [Theory]
    [InlineData(NpcBrainType.Vendor)]
    [InlineData(NpcBrainType.Healer)]
    [InlineData(NpcBrainType.Banker)]
    [InlineData(NpcBrainType.Stable)]
    public void EveryBrainUpstreamCountsIsAVendor(NpcBrainType brain)
    {
        var world = World();
        Assert.True(VendorEngine.IsVendorLike(Npc(world, brain, "someone")));
        Assert.True(VendorEngine.IsVendorBrain(brain));
    }

    /// <summary>The case from the shard: the animal trainer carries brain=Stable.</summary>
    [Fact]
    public void TheAnimalTrainerIsAVendor()
    {
        var world = World();
        Assert.True(VendorEngine.IsVendorLike(
            Npc(world, NpcBrainType.Stable, "Keith the animal trainer")));
    }

    /// <summary>And the brains it does not count stay out.</summary>
    [Theory]
    [InlineData(NpcBrainType.Animal)]
    [InlineData(NpcBrainType.Monster)]
    [InlineData(NpcBrainType.Guard)]
    public void TheOtherBrainsAreNotVendors(NpcBrainType brain)
    {
        var world = World();
        Assert.False(VendorEngine.IsVendorBrain(brain));
        Assert.False(VendorEngine.IsVendorLike(Npc(world, brain, "a beast")));
    }

    /// <summary>The name fallback stays for a Human brain, which is how some packs
    /// write a shopkeeper (brain_vendor left commented out in the def).</summary>
    [Fact]
    public void AHumanBrainWithAVendorNameStillCounts()
    {
        var world = World();
        Assert.True(VendorEngine.IsVendorLike(
            Npc(world, NpcBrainType.Human, "a wandering merchant")));
        Assert.False(VendorEngine.IsVendorLike(
            Npc(world, NpcBrainType.Human, "Katrina")));
    }
}
