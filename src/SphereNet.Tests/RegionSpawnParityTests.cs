using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

// Source-X region/spawn parity (wiki/8.txt audit): RegionFlag numeric values match
// REGION_FLAG_* so legacy numeric FLAGS parse correctly; placement reports success
// so spawners don't orphan items; NODECAY regions stop ground decay.
public class RegionSpawnParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ---- RegionFlag numeric alignment with Source-X CRegion.h ----

    [Fact]
    public void RegionFlag_MatchesSourceXNumericValues()
    {
        Assert.Equal(0x00000001u, (uint)RegionFlag.NoMagic);     // REGION_ANTIMAGIC_ALL
        Assert.Equal(0x00000008u, (uint)RegionFlag.Gate);        // REGION_ANTIMAGIC_GATE
        Assert.Equal(0x00000080u, (uint)RegionFlag.NoBuild);     // REGION_FLAG_NOBUILDING
        Assert.Equal(0x00000200u, (uint)RegionFlag.Announce);    // REGION_FLAG_ANNOUNCE
        Assert.Equal(0x00000800u, (uint)RegionFlag.Underground); // REGION_FLAG_UNDERGROUND
        Assert.Equal(0x00001000u, (uint)RegionFlag.NoDecay);     // REGION_FLAG_NODECAY
        Assert.Equal(0x00002000u, (uint)RegionFlag.Safe);        // REGION_FLAG_SAFE
        Assert.Equal(0x00004000u, (uint)RegionFlag.Guarded);     // REGION_FLAG_GUARDED
        Assert.Equal(0x00008000u, (uint)RegionFlag.NoPvP);       // REGION_FLAG_NO_PVP
        Assert.Equal(0x00010000u, (uint)RegionFlag.Arena);       // REGION_FLAG_ARENA

        // The headline legacy bug: numeric 0x4000 must be Guarded, not Safe.
        Assert.Equal(RegionFlag.Guarded, (RegionFlag)0x4000);
        Assert.NotEqual(RegionFlag.Safe, (RegionFlag)0x4000);
    }

    // ---- Placement returns success so spawners can avoid orphans ----

    [Fact]
    public void PlaceItem_ReportsSuccessInBounds_AndFailureOutOfBounds()
    {
        var world = CreateWorld();

        var ok = world.CreateItem();
        ok.BaseId = 0x1F03;
        Assert.True(world.PlaceItem(ok, new Point3D(100, 100, 0, 0)));

        var bad = world.CreateItem();
        bad.BaseId = 0x1F03;
        Assert.False(world.PlaceItem(bad, new Point3D(30000, 30000, 0, 0))); // out of map bounds
    }

    // ---- NODECAY region stops ground item decay ----

    [Fact]
    public void NoDecayRegion_GroundItemDoesNotDecay()
    {
        var world = CreateWorld();
        var region = new Region { Name = "vault", Flags = RegionFlag.NoDecay, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);

        var inside = world.CreateItem();
        inside.BaseId = 0x1F03;
        world.PlaceItem(inside, new Point3D(100, 100, 0, 0));
        inside.DecayTime = Environment.TickCount64 - 1; // already due

        inside.OnTick();
        Assert.False(inside.IsDeleted); // re-armed, not decayed

        var outside = world.CreateItem();
        outside.BaseId = 0x1F03;
        world.PlaceItem(outside, new Point3D(500, 500, 0, 0)); // no region here
        outside.DecayTime = Environment.TickCount64 - 1;

        outside.OnTick();
        Assert.True(outside.IsDeleted); // decays normally
    }

    // ---- NoMining region flag ----

    private sealed class MiningSink : SphereNet.Game.Skills.Information.IActiveSkillSink
    {
        public SphereNet.Game.Objects.Characters.Character Self { get; }
        public System.Random Random { get; } = new(1);
        public GameWorld World { get; }
        public System.Collections.Generic.List<string> Messages { get; } = new();
        public MiningSink(SphereNet.Game.Objects.Characters.Character self, GameWorld world)
        { Self = self; World = world; }
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) => Messages.Add(text);
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    [Fact]
    public void NoMiningRegion_RefusesMining()
    {
        var world = CreateWorld();
        var region = new Region { Name = "nomine", Flags = RegionFlag.NoMining, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var sink = new MiningSink(ch, world);

        bool ok = SphereNet.Game.Skills.Information.ActiveSkillEngine.Mining(
            sink, new Point3D(100, 100, 0, 0), null, world);

        Assert.False(ok); // REGION_FLAG_NOMINING refused it (before the tile / pickaxe checks)
    }

    // ---- Sector.Number from map columns (not hardcoded 96) ----

    [Fact]
    public void SectorNumber_UsesMapColumns_NotHardcoded96()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 4096, 4096); // 4096 / 64 = 64 columns, not 96
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        const int size = SphereNet.Game.World.Sectors.Sector.SectorSize;
        var sector = world.GetSector(new Point3D(2 * size, 3 * size, 0, 0));

        Assert.NotNull(sector);
        Assert.Equal(3 * 64 + 2, sector!.Number); // row*cols + col with cols = 64
    }

    // ---- item spawn TIMELO/TIMEHI/MAXDIST ----

    [Fact]
    public void ItemSpawn_HonorsMaxDistAndTimeProperties()
    {
        var world = CreateWorld();
        var spawner = world.CreateItem();
        spawner.SpawnItem = new SphereNet.Game.Components.ItemSpawnComponent(spawner, world);

        Assert.True(spawner.TrySetProperty("MAXDIST", "5"));
        Assert.Equal(5, spawner.SpawnItem.SpawnRange);

        Assert.True(spawner.TrySetProperty("TIMELO", "10"));
        Assert.True(spawner.TrySetProperty("TIMEHI", "20"));
        Assert.True(spawner.TryGetTag("TIMELO", out var lo) && lo == "10");
    }
}
