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
