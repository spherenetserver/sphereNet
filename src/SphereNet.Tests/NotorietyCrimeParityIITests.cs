using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X notoriety/crime parity II (wiki/notoriety-crime-remaining.txt):
//   #8 murder-decay countdown persistence,
//   #4 @MurderMark make-criminal toggle + @MurderDecay next-interval readback,
//   #5 multi-attacker murder marking (every unprovoked attacker of an innocent).
// Hooks are nulled between tests by ResetEngineStatics.
public class NotorietyCrimeParityIITests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, short karma = 0)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true; ch.BodyId = 0x0190; ch.Karma = karma;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- #8: murder-decay countdown persistence ----

    [Fact]
    public void MurderDecayRemaining_RoundTripsAndLoadsViaProperty()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        ch.MurderDecayRemainingSeconds = 300;
        Assert.InRange(ch.MurderDecayRemainingSeconds, 295, 300); // real-time getter

        // The load path applies it through TrySetProperty (WorldSaver writes MURDERDECAY).
        ch.TrySetProperty("MURDERDECAY", "250");
        Assert.InRange(ch.MurderDecayRemainingSeconds, 245, 250);

        ch.MurderDecayRemainingSeconds = 0;
        Assert.Equal(0, ch.MurderDecayRemainingSeconds);
    }

    // ---- #4: @MurderMark make-criminal toggle ----

    [Fact]
    public void MurderMark_MakeCriminalFalse_RecordsKillWithoutCriminalFlag()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101, karma: 1000); // innocent

        // Script records the murder but suppresses the temporary criminal flag.
        Character.OnMurderMark = (_, _, proposed) =>
            new Character.MurderMarkDecision(proposed, MakeCriminal: false);

        death.ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
        Assert.False(killer.IsCriminal); // ARGN2=0 suppressed the criminal flag
    }

    // ---- #4: @MurderDecay next-interval readback ----

    [Fact]
    public void MurderDecay_NextIntervalOverride_ShortensFollowingDecay()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Kills = 3;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Character.OnMurderDecay = (_, _) => 5; // next decay in 5 seconds

        long defaultMs = Character.MurderDecayTimeSeconds * 1000L;
        const long t0 = 1_000_000;

        ch.TickNotorietyDecay(t0);                    // arms the default window
        ch.TickNotorietyDecay(t0 + defaultMs + 1);    // first decay → re-arm with 5s override
        Assert.Equal(2, ch.Kills);

        // 5 seconds later the next kill ages off — proving the override (not the
        // full default window) governs the following interval.
        ch.TickNotorietyDecay(t0 + defaultMs + 1 + 5_000 + 1);
        Assert.Equal(1, ch.Kills);
    }

    // ---- #5: multi-attacker murder marking ----

    [Fact]
    public void GankingInnocent_MarksEveryAttacker_NotJustTheKiller()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100, karma: 1000); // innocent
        var k1 = MakePlayer(world, 101);
        var k2 = MakePlayer(world, 102);

        victim.RecordAttack(k1.Uid, 10);
        victim.RecordAttack(k2.Uid, 10);

        death.ProcessDeath(victim, k1); // k1 lands the final blow

        Assert.Equal(1, k1.Kills);
        Assert.Equal(1, k2.Kills); // the second ganker is marked too
        Assert.True(k1.IsCriminal);
        Assert.True(k2.IsCriminal);
    }

    [Fact]
    public void KillingCriminal_MarksNoAttacker()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100, karma: 1000);
        victim.SetStatFlag(StatFlag.Criminal); // not an innocent
        var k1 = MakePlayer(world, 101);
        var k2 = MakePlayer(world, 102);

        victim.RecordAttack(k1.Uid, 10);
        victim.RecordAttack(k2.Uid, 10);

        death.ProcessDeath(victim, k1);

        Assert.Equal(0, k1.Kills); // killing a criminal is not murder
        Assert.Equal(0, k2.Kills);
    }
}
