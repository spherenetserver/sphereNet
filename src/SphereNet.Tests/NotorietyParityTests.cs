using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X notoriety/murder/fame parity (wiki/7.txt audit): murderer threshold is
// strictly-greater-than, a PvP kill only counts as murder when it is an unprovoked
// kill of an innocent, and there is no artificial per-kill fame cap.
public class NotorietyParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, short fame = 0, short karma = 0)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.Fame = fame; ch.Karma = karma;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static void ClearNotoHooks()
    {
        Character.OnFameChanging = null;
        Character.OnKarmaChanging = null;
        Character.OnMurderMark = null;
    }

    // ---- #1: murderer threshold is strictly greater than MurderMinCount ----

    [Fact]
    public void Murderer_RequiresMoreThanThreshold()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);

        ch.Kills = (short)Character.MurderMinCount;       // exactly the threshold (5)
        Assert.False(ch.IsMurderer);

        ch.Kills = (short)(Character.MurderMinCount + 1);  // one over (6) → red
        Assert.True(ch.IsMurderer);
    }

    // ---- #2: murder mark only on an unprovoked kill of an innocent ----

    [Fact]
    public void Kill_InnocentVictim_CountsAsMurder()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101); // innocent, no prior combat

        death.ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
        Assert.True(killer.IsCriminal);
    }

    [Fact]
    public void Kill_CriminalVictim_IsNotMurder()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);
        victim.MakeCriminal(); // grey — killing is not murder
        Assert.True(victim.IsCriminal);

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
        Assert.False(killer.IsCriminal);
    }

    [Fact]
    public void Kill_VictimWhoAggressedFirst_IsSelfDefenseNotMurder()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);
        // The victim struck first: the killer (defender) holds a HarmedBy memory.
        killer.Memory_AddObjTypes(victim.Uid, MemoryType.HarmedBy);

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
        Assert.False(killer.IsCriminal);
    }

    // ---- #6a: no artificial per-kill fame cap ----

    [Fact]
    public void Fame_HighFamePlayerKill_ExceedsOldTwoHundredCap()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var killer = MakePlayer(world, 100, fame: 0);
        var victim = MakePlayer(world, 101, fame: 5000); // PC fame → gain = 5000/10 = 500

        death.ProcessDeath(victim, killer);

        Assert.Equal(500, killer.Fame); // was clamped to 200 before the cap removal
    }
}
