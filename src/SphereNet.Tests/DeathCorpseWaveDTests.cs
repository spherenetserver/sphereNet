using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Death/corpse waves D1-D2 (wiki/death-corpse.txt): player death penalties,
// death-time state cleanup, carve crime and birth-gates, the configurable
// resurrection HP percent and the trade-cancel hook.
public class DeathCorpseWaveDTests
{
    private static (GameWorld World, DeathEngine Engine) Setup()
    {
        var world = TestHarness.CreateWorld();
        return (world, new DeathEngine(world));
    }

    private static Character MakePlayer(GameWorld world, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- D1: player death penalties ----

    [Fact]
    public void PlayerDeath_LosesExpAndFame_AndCountsTheDeath()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        victim.Exp = 100;
        victim.Fame = 1000;
        victim.Deaths = 0;

        engine.ProcessDeath(victim);

        Assert.Equal(90, victim.Exp);      // -exp/10 (min 1)
        Assert.Equal(900, victim.Fame);    // -fame/10
        Assert.Equal(1, victim.Deaths);
    }

    [Fact]
    public void DeathNoFameChangeFlag_PreservesFame()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        victim.Fame = 1000;
        victim.SetTag("DEATHFLAGS", "0x01"); // DEATH_NOFAMECHANGE

        engine.ProcessDeath(victim);

        Assert.Equal(1000, victim.Fame);
        Assert.Equal(1, victim.Deaths); // the counter still ticks
    }

    // ---- D1: Kill() clears held states (Source-X Reveal + StatFlag_Clear) ----

    [Fact]
    public void Kill_ClearsHiddenFrozenAndSleepStates()
    {
        var ch = new Character();
        ch.SetStatFlag(StatFlag.Hidden);
        ch.SetStatFlag(StatFlag.Freeze);
        ch.SetStatFlag(StatFlag.Stone);
        ch.SetStatFlag(StatFlag.Sleeping);

        ch.Kill();

        Assert.True(ch.IsStatFlag(StatFlag.Dead));
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
        Assert.False(ch.IsStatFlag(StatFlag.Freeze));
        Assert.False(ch.IsStatFlag(StatFlag.Stone));
        Assert.False(ch.IsStatFlag(StatFlag.Sleeping));
    }

    // ---- D1: carve crime + birth-gates ----

    [Fact]
    public void CarvingAnInnocentPlayersCorpse_FlagsTheCarverCriminal()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim);
        Assert.NotNull(corpse);

        var carver = MakePlayer(world, 101);
        engine.CarveCorpse(carver, corpse!);

        Assert.True(carver.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void CarvingYourOwnCorpse_IsNotACrime()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var corpse = engine.ProcessDeath(victim);

        engine.CarveCorpse(victim, corpse!);

        Assert.False(victim.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void BondedPetCorpse_IsBornUncarvable()
    {
        var (world, engine) = Setup();
        var pet = world.CreateCharacter();
        pet.IsBonded = true;
        pet.MaxHits = 50; pet.Hits = 50;
        world.PlaceCharacter(pet, new Point3D(100, 100, 0, 0));

        var corpse = engine.ProcessDeath(pet);

        Assert.NotNull(corpse);
        Assert.True(corpse!.TryGetTag("CORPSE_CARVED", out var c) && c == "1");
        // ...and carving it yields nothing.
        var carver = MakePlayer(world, 101);
        Assert.Empty(engine.CarveCorpse(carver, corpse));
    }

    // ---- D1: victim memories cleared on death ----

    [Fact]
    public void Death_ClearsTheVictimsFightMemories()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var foe = MakePlayer(world, 101);
        victim.Memory_AddObjTypes(foe.Uid, MemoryType.Fight | MemoryType.HarmedBy);
        Assert.NotNull(victim.Memory_FindObjTypes(foe.Uid, MemoryType.HarmedBy));

        engine.ProcessDeath(victim);

        Assert.Null(victim.Memory_FindObjTypes(foe.Uid, MemoryType.Fight | MemoryType.HarmedBy));
    }

    // ---- D2: configurable resurrection HP percent ----

    [Fact]
    public void Resurrect_UsesTheConfiguredHitpointPercent()
    {
        int saved = Character.HitpointPercentOnRez;
        try
        {
            var ch = new Character { MaxHits = 100 };
            ch.Kill();

            Character.HitpointPercentOnRez = 25;
            ch.Resurrect();
            Assert.Equal(25, ch.Hits);

            // Floor at 1 HP even at tiny percentages/max hits.
            ch.Kill();
            Character.HitpointPercentOnRez = 1;
            ch.Resurrect();
            Assert.Equal(1, ch.Hits);
        }
        finally
        {
            Character.HitpointPercentOnRez = saved;
        }
    }

    // ---- D2: open trades cancelled at death ----

    [Fact]
    public void Death_InvokesTheTradeCancelHook()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        Character? cancelledFor = null;
        engine.CancelTradesHook = ch => cancelledFor = ch;

        engine.ProcessDeath(victim);

        Assert.Equal(victim, cancelledFor);
    }

    // ---- D3: kill record, hair copy, antimagic rez, conjured effect ----

    [Fact]
    public void Death_EmitsTheKillRecord_WithKillersOrAccident()
    {
        var (world, engine) = Setup();
        string? message = null;
        engine.KillMessageHook = (_, msg) => message = msg;

        var killer = MakePlayer(world, 101);
        killer.Name = "Slayer";
        var victim = MakePlayer(world);
        victim.Name = "Victim";
        engine.ProcessDeath(victim, killer);
        Assert.NotNull(message);
        Assert.Contains("'Slayer'", message);

        message = null;
        var lonely = MakePlayer(world, 102);
        engine.ProcessDeath(lonely); // no killer
        Assert.NotNull(message);
        Assert.Contains("accident", message);
    }

    [Fact]
    public void PlayerCorpse_CarriesHairCopies_ThatAreDiscardedOnRejoin()
    {
        var (world, engine) = Setup();
        var victim = MakePlayer(world);
        var hair = world.CreateItem();
        hair.BaseId = 0x203C;
        victim.Equip(hair, Layer.Hair);

        var corpse = engine.ProcessDeath(victim);
        Assert.NotNull(corpse);

        // The corpse renders the hair via a marked copy; the original stays
        // on the ghost (Source-X CreateDupeItem).
        var copy = corpse!.Contents.FirstOrDefault(i => i.TryGetTag("CORPSE_HAIR", out _));
        Assert.NotNull(copy);
        Assert.Equal(hair, victim.GetEquippedItem(Layer.Hair));

        // On rejoin the copy is discarded, not restored as loot.
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        victim.Equip(pack, Layer.Pack);
        Assert.True(engine.RestoreFromCorpse(victim));
        Assert.True(copy!.IsDeleted);
        Assert.DoesNotContain(copy, pack.Contents);
    }

    [Fact]
    public void Resurrect_IsRefusedInAnAntimagicRegion()
    {
        var world = TestHarness.CreateWorld();
        var region = new SphereNet.Game.World.Regions.Region
        { Name = "am_test", Flags = RegionFlag.NoMagic, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);

        var ghost = MakePlayer(world);
        ghost.Kill();

        ghost.Resurrect();
        Assert.True(ghost.IsDead); // refused (Source-X DEFMSG_SPELL_RES_AM)

        // Outside the region the rez proceeds.
        world.MoveCharacter(ghost, new Point3D(150, 150, 0, 0));
        ghost.Resurrect();
        Assert.False(ghost.IsDead);
    }

    [Fact]
    public void CorpselessSummonDeath_BurstsTheVanishEffect()
    {
        var (world, engine) = Setup();
        Character? burstFor = null;
        engine.ConjuredVanishEffectHook = ch => burstFor = ch;

        var summon = world.CreateCharacter();
        summon.MaxHits = 50; summon.Hits = 50;
        summon.SetTag("SUMMON_EXPIRE_TICK", "1"); // marks it summoned
        world.PlaceCharacter(summon, new Point3D(100, 100, 0, 0));

        var corpse = engine.ProcessDeath(summon);

        Assert.Null(corpse);          // summons leave no corpse
        Assert.Equal(summon, burstFor);
    }
}
