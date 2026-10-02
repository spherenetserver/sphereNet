using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X RESPAWN (SERV.RESPAWN -> CWorld::RespawnDeadNPCs, SECTOR.RESPAWN ->
/// CSector::RespawnDeadNPCs, CSector.cpp:1122-1152): every dead NPC that has a home
/// is restocked from its script (NPC_LoadScript(true)), moved near home within its
/// wander distance (MoveNear), given its @Create events (NPC_CreateTrigger) and
/// resurrected. It does not touch spawners. SphereNet's RESPAWN topped spawners up
/// instead, which is not what the verb does.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class RespawnDeadNpcsParityTests
{
    private static Character DeadNpc(GameWorld world, Point3D at, Point3D home, short homeDist)
    {
        var npc = world.CreateCharacter();
        npc.MaxHits = 50;
        world.PlaceCharacter(npc, at);
        npc.Home = home;
        npc.HomeDist = homeDist;
        npc.SetStatFlag(StatFlag.Dead);
        npc.Hits = 0;
        return npc;
    }

    [Fact]
    public void ADeadNpcWithAHomeIsRestockedMovedHomeGivenItsCreateEventsAndRaised()
    {
        var world = TestHarness.CreateWorld();
        var home = new Point3D(1000, 1000, 0, 0);
        var npc = DeadNpc(world, new Point3D(1200, 1200, 0, 0), home, 3);
        var order = new List<string>();
        world.NpcRespawnLoadScript = c => order.Add($"load:{c.IsDead}:{c.X}");
        world.NpcRespawnCreateTrigger = c => order.Add($"create:{c.IsDead}:{(c.Position.GetDistanceTo(home) <= 3)}");

        Assert.Equal(1, world.RespawnDeadNpcs(rng: new Random(7)));

        Assert.False(npc.IsDead);
        Assert.InRange(npc.Position.GetDistanceTo(home), 0, 3);
        // NPC_LoadScript while still dead and where it fell; NPC_CreateTrigger after
        // the move and before the resurrection (CSector.cpp:1141-1147).
        Assert.Equal(["load:True:1200", "create:True:True"], order);
    }

    [Fact]
    public void WithAHomeDistanceOfZeroItComesBackOnItsHome()
    {
        var world = TestHarness.CreateWorld();
        var home = new Point3D(1000, 1000, 0, 0);
        var npc = DeadNpc(world, new Point3D(1200, 1200, 0, 0), home, 0);

        world.RespawnDeadNpcs();

        Assert.Equal(home.X, npc.X);
        Assert.Equal(home.Y, npc.Y);
    }

    [Fact]
    public void NoHomeNoPlayerNoLivingNpc_AndNoSpawnerIsTopped()
    {
        var world = TestHarness.CreateWorld();
        // A dead NPC with no home point stays dead (m_ptHome.IsValidPoint()).
        var homeless = DeadNpc(world, new Point3D(1100, 1100, 0, 0), new Point3D(0, 0, 0, 0), 3);
        // A player ghost is not an NPC.
        var ghost = DeadNpc(world, new Point3D(1101, 1100, 0, 0), new Point3D(1000, 1000, 0, 0), 3);
        ghost.IsPlayer = true;
        // A living NPC is left where it is.
        var alive = world.CreateCharacter();
        world.PlaceCharacter(alive, new Point3D(1102, 1100, 0, 0));
        alive.Home = new Point3D(1000, 1000, 0, 0);
        // A spawner with room: RESPAWN does not spawn into it.
        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, new Point3D(900, 900, 0, 0));
        spawner.SpawnChar = new SpawnComponent(spawner, world) { CharDefId = 0x0190, MaxCount = 3 };

        Assert.Equal(0, world.RespawnDeadNpcs());

        Assert.True(homeless.IsDead);
        Assert.True(ghost.IsDead);
        Assert.Equal(1102, alive.X);
        Assert.Equal(0, spawner.SpawnChar.CurrentCount);
    }

    [Fact]
    public void SectorRespawnCoversItsOwnSector_AndTheAArgumentTheWorld()
    {
        var world = TestHarness.CreateWorld();
        var here = DeadNpc(world, new Point3D(1000, 1000, 0, 0), new Point3D(1001, 1000, 0, 0), 0);
        var far = DeadNpc(world, new Point3D(3000, 3000, 0, 0), new Point3D(3001, 3000, 0, 0), 0);
        var sector = world.GetSector(here.Position)!;
        Assert.NotSame(sector, world.GetSector(far.Position));
        var console = new TestConsole();

        Assert.True(sector.TryExecuteCommand("RESPAWN", "", console));
        Assert.False(here.IsDead);
        Assert.True(far.IsDead);

        Assert.True(sector.TryExecuteCommand("RESPAWN", "A", console));
        Assert.False(far.IsDead);
    }

    private sealed class TestConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public void SysMessage(string message) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "test";
    }
}
