using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

// Field-report fixes (2026-07-03 in-game session): a following pet attacked by
// a monster never fought back (Follow/Come/Stay ignored FightTarget), and NPC
// movement hiked over mountains because IsPassable never consulted the LAND
// impassable flag (each slope step passes the ±12 z gate on its own).
public class NpcAiFieldReportTests
{
    [Fact]
    public void FollowingPet_FightsBack_WhenAttacked()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ai = new NpcAI(world, new SphereConfig());

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.IsOnline = true;
        owner.Hits = owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(owner);

        var pet = world.CreateCharacter();
        pet.NpcMaster = owner.Uid;
        pet.Hits = pet.MaxHits = 50;
        pet.Stam = pet.MaxStam = 50;
        pet.PetAIMode = PetAIMode.Follow;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        var skeleton = world.CreateCharacter();
        skeleton.Hits = skeleton.MaxHits = 50;
        world.PlaceCharacter(skeleton, new Point3D(102, 100, 0, 0));

        // The melee-hit retaliation path stamps the aggressor on the victim.
        pet.FightTarget = skeleton.Uid;

        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);

        // Self-defense: the following pet keeps the aggressor engaged instead
        // of silently resuming the follow.
        Assert.Equal(skeleton.Uid, pet.FightTarget);

        // An explicit order (all follow/stay) calls the pet off — the AI drops
        // an invalid/dead target on the next tick.
        skeleton.Kill();
        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
        Assert.False(pet.FightTarget.IsValid);
    }

    [Fact]
    public void RiddenMount_IsNotAValidMonsterTarget()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ai = new NpcAI(world, new SphereConfig());

        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        rider.IsOnline = true;
        rider.Hits = rider.MaxHits = 100;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(rider);

        var mount = world.CreateCharacter();
        mount.Hits = mount.MaxHits = 50;
        mount.SetStatFlag(StatFlag.Ridden);
        world.PlaceCharacter(mount, new Point3D(100, 100, 0, 0));

        var skeleton = world.CreateCharacter();
        skeleton.Hits = skeleton.MaxHits = 50;
        skeleton.NpcBrain = NpcBrainType.Monster;
        skeleton.Int = 30;
        world.PlaceCharacter(skeleton, new Point3D(102, 100, 0, 0));
        world.OnTick(); // activate the sector

        skeleton.NextNpcActionTime = 0;
        ai.OnTickAction(skeleton);

        // The monster must acquire the RIDER, never the ridden mount —
        // targeting the mount produced an invisible one-sided fight.
        Assert.NotEqual(mount.Uid, skeleton.FightTarget);
    }

    [Fact]
    public void DismountedMount_IsAValidMonsterTarget()
    {
        // Field report: after dismounting, hostiles ignored the now-standing
        // mount. A dismounted mount is an owned pet — hostility is evaluated
        // toward its owner (Source-X NPC_GetHostilityLevelToward), so an evil
        // monster must be able to acquire it like any other pet.
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ai = new NpcAI(world, new SphereConfig());

        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        rider.IsOnline = true;
        rider.Hits = rider.MaxHits = 100;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(rider);

        var mount = world.CreateCharacter();
        mount.NpcMaster = rider.Uid;
        mount.NpcBrain = NpcBrainType.Animal;
        mount.Hits = mount.MaxHits = 50;
        mount.Stam = mount.MaxStam = 50;
        world.PlaceCharacter(mount, new Point3D(103, 100, 0, 0)); // adjacent to the monster

        var skeleton = world.CreateCharacter();
        skeleton.Hits = skeleton.MaxHits = 50;
        skeleton.NpcBrain = NpcBrainType.Monster;
        skeleton.Int = 30;
        world.PlaceCharacter(skeleton, new Point3D(104, 100, 0, 0));
        world.OnTick(); // activate the sector

        skeleton.NextNpcActionTime = 0;
        ai.OnTickAction(skeleton);

        // Nearest valid hostile is the dismounted mount (same owner-derived
        // hostility as the rider, smaller distance penalty).
        Assert.Equal(mount.Uid, skeleton.FightTarget);
    }

    [Fact]
    public void OpenedDoor_SwingsShutOnItsOwn()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var door = world.CreateItem();
        door.BaseId = 0x06A5; // wooden_1 slot 0
        door.ItemType = Core.Enums.ItemType.Door;
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.True(SphereNet.Game.World.DoorHelper.TryOpenDoorState(door));
        Assert.True(door.Timeout > 0); // the 20s auto-close armed

        // Force the timer due and tick: the door closes, un-shifts the
        // hinge and clears the state (Source-X _SetTimeoutS(20)).
        door.SetTimeout(1);
        door.OnTick();
        Assert.Equal(0x06A5, door.BaseId);
        Assert.Equal(1000, door.X);
        Assert.Equal(1000, door.Y);
        Assert.False(door.TryGetTag("DOOR_OPEN", out _));
    }

    [Fact]
    public void ImpassableMountainLand_BlocksNpcMovement()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 0x0244);
        map.SetSyntheticLandTile(0x0244, new LandTileData
        { Flags = TileFlag.Impassable, Name = "mountain rock" });

        // Bare rock: blocked at every z.
        Assert.False(map.IsPassable(0, 100, 100, 0));

        // A bridge/surface static over the rock makes the spot walkable
        // (mountain pass, carved stair).
        map.SetSyntheticItemTile(0x0709, new ItemTileData
        { Flags = TileFlag.Surface | TileFlag.Bridge, Height = 0, Name = "stone stair" });
        map.AddSyntheticStatic(0, 100, 100, 0x0709, 0);
        Assert.True(map.IsPassable(0, 100, 100, 0));

        // Plain walkable land elsewhere stays walkable.
        map.SetSyntheticLandTile(0x0003, default);
        var map2 = new MapDataManager("");
        map2.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        Assert.True(map2.IsPassable(0, 100, 100, 0));
    }
}
