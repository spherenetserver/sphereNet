using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects;
using SphereNet.Game.World;
using SphereNet.Core.Enums;

namespace SphereNet.Tests;

/// <summary>
/// A mount you step off follows you.
///
/// Upstream's dismount re-owns the creature to the rider and leaves it idle
/// (Use_Figurine: NPC_PetSetOwner then Skill_Start(SKILL_NONE), CCharUse.cpp:1197-1206);
/// an idle owned pet that sees its owner takes it as its guard target
/// (NPC_LookAtChar, CCharNPCAct.cpp:1036) and NPC_Act_Guard keeps station on it. So a
/// player who dismounts and walks off is followed by the horse.
///
/// Reported from a shard the other way round: the player dismounted, started running,
/// and the horse stood where it was left.
/// </summary>
public sealed class DismountedMountFollowsTests
{
    private sealed record Bench(GameWorld World, NpcAI Ai,
                                SphereNet.Game.Objects.Characters.Character Rider,
                                SphereNet.Game.Objects.Characters.Character Horse,
                                MountEngine Mounts);

    private static Bench Build()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var rider = world.CreateCharacter();
        rider.IsPlayer = true; rider.IsOnline = true;
        rider.Hits = rider.MaxHits = 100;
        rider.Str = rider.Dex = rider.Int = 100;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(rider);

        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;                 // a horse body, so it has a mount item
        horse.Hits = horse.MaxHits = 50;
        horse.Stam = horse.MaxStam = 50;
        horse.Dex = 100;
        world.PlaceCharacter(horse, new Point3D(100, 100, 0, 0));
        // A bought or tamed mount: upstream refuses to mount a creature the rider does
        // not own (Horse_Mount requires NPC_IsOwnedBy), and left standing is the state
        // the report describes.
        Assert.True(horse.TryAssignOwnership(rider, rider));
        horse.PetAIMode = PetAIMode.Stay;

        return new Bench(world, new NpcAI(world, new SphereConfig()),
                         rider, horse, new MountEngine(world));
    }

    /// <summary>Tick the horse's AI enough times for it to take several steps.</summary>
    private static void RunAi(Bench b, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            b.Horse.NextNpcActionTime = 0;     // the cadence is not what this measures
            b.Ai.OnTickAction(b.Horse);
        }
    }

    /// <summary>The reported case: dismount, walk away, and the horse closes the gap.</summary>
    [Fact]
    public void ADismountedMountClosesTheGap()
    {
        var b = Build();
        Assert.True(b.Mounts.TryMount(b.Rider, b.Horse));
        Assert.NotNull(b.Mounts.Dismount(b.Rider));

        b.World.MoveCharacter(b.Rider, new Point3D(112, 100, 0, 0));
        int before = b.Horse.Position.GetDistanceTo(b.Rider.Position);
        Assert.True(before > 3, $"setup: the horse should be left behind, distance {before}");

        RunAi(b, 40);

        int after = b.Horse.Position.GetDistanceTo(b.Rider.Position);
        Assert.True(after < before,
            $"the horse did not follow: {before} tiles away before, {after} after");
        Assert.True(after <= 3, $"the horse stopped {after} tiles short");
    }

    /// <summary>It is the rider's pet afterwards, which is what makes it follow at
    /// all - upstream re-owns it on every dismount, unconditionally.</summary>
    [Fact]
    public void ADismountedMountIsOwnedByTheRider()
    {
        var b = Build();
        Assert.True(b.Mounts.TryMount(b.Rider, b.Horse));
        b.Mounts.Dismount(b.Rider);

        Assert.Equal(b.Rider.Uid, b.Horse.NpcMaster);
        Assert.False(b.Horse.IsStatFlag(StatFlag.Ridden));
    }

    /// <summary>The measurement the engine test above cannot make: whether the creature
    /// is still ON THE SCHEDULE when it comes back. Its AI following correctly is worth
    /// nothing if nothing ever calls it.
    ///
    /// A ridden creature keeps the position it was mounted at - the rider carries it, and
    /// upstream makes it a contained, disconnected object whose own point means nothing
    /// (Horse_Mount: STATF_RIDDEN + SetDisconnected, and disconnected objects keep
    /// ticking off the world's own list, not off a sector). Here the schedule asked
    /// whether a PLAYER was near the creature's stale position, so riding away from the
    /// spot dropped the mount out of the wheel - and after dismounting, nothing put it
    /// back. That is the horse standing still.</summary>
    [Fact]
    public void ARiddenMountStaysOnTheSchedule()
    {
        var b = Build();
        // A creature nobody owns, which is what a staff mount or a restored figurine
        // can be: the owner check is the only other thing keeping it scheduled.
        b.Horse.NpcMaster = Serial.Invalid;
        b.Horse.PrivLevel = PrivLevel.Player;
        b.Rider.PrivLevel = PrivLevel.Owner;      // upstream lets staff mount anything
        Assert.True(b.Mounts.TryMount(b.Rider, b.Horse));

        // Ride far enough that no player is anywhere near where the horse was mounted.
        b.World.MoveCharacter(b.Rider, new Point3D(400, 400, 0, 0));

        Assert.False(b.World.IsInActiveArea(b.Horse.MapIndex, b.Horse.X, b.Horse.Y),
            "setup: nobody should be near the spot the horse was mounted at");
        Assert.True(SphereNet.Server.Program.ShouldStayScheduled(b.World, b.Horse),
            "a ridden creature was dropped from the schedule for standing where it was left");
    }

    /// <summary>And the belt-and-braces half: a creature that re-enters the world is put
    /// back to work, whichever way it got there - a dismount, a figurine, a stable
    /// retrieval, a script placement. Upstream has no schedule to fall off: a char
    /// placed in the world is awake there.</summary>
    [Fact]
    public void ACreatureThatReEntersTheWorldIsAnnounced()
    {
        var b = Build();
        Assert.True(b.Mounts.TryMount(b.Rider, b.Horse));

        var placed = new List<SphereNet.Game.Objects.Characters.Character>();
        b.World.CharacterPlaced += placed.Add;
        Assert.NotNull(b.Mounts.Dismount(b.Rider));

        Assert.Contains(b.Horse, placed);
    }
}
