using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the newly-wired death notoriety triggers (@FameChange, @KarmaChange,
// @MurderMark) fire at the DeathEngine mutation points with the right deltas and
// honour the cancel/adjust return contract. The production fire path is the thin
// Program.EngineWiring lambda over these Character.On* hooks; here the hooks are
// driven directly so the engine-side contract (when fired, with what, and how the
// return is applied) is locked. Hooks are nulled between tests by
// ResetEngineStatics so production state never leaks in.
public class DeathTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, short karma, short fame)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Karma = karma;
        ch.Fame = fame;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    [Fact]
    public void PlayerKill_FiresFameKarmaMurderHooks_AndApplies()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000); // innocent

        int? fameDelta = null, karmaDelta = null, murderProposed = null;
        Character? murderVictim = null;
        Character.OnFameChanging = (_, d) => { fameDelta = d; return d; };
        Character.OnKarmaChanging = (_, d) => { karmaDelta = d; return d; };
        Character.OnMurderMark = (_, v, n) => { murderProposed = n; murderVictim = v; return new Character.MurderMarkDecision(n, true); };

        death.ProcessDeath(victim, killer);

        // Fame: innocent player victim grants fame/10 = 100, applied from 0.
        Assert.Equal(100, fameDelta);
        Assert.Equal(100, killer.Fame);

        // Karma: a negative delta fired and was applied (killer loses karma for
        // killing an innocent).
        Assert.NotNull(karmaDelta);
        Assert.True(karmaDelta < 0);
        Assert.True(killer.Karma < 0);

        // Murder: proposed count is current+1, victim passed through, count applied.
        Assert.Equal(1, murderProposed);
        Assert.Same(victim, murderVictim);
        Assert.Equal(1, killer.Kills);
        Assert.True(killer.IsCriminal);
    }

    [Fact]
    public void MurderMark_ReturnNull_BlocksCountAndCriminalFlag()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000);

        Character.OnMurderMark = (_, _, _) => new Character.MurderMarkDecision(null, false); // block the mark

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
        Assert.False(killer.IsCriminal);
    }

    [Fact]
    public void KarmaChange_ReturnNull_CancelsKarmaButFameStillApplies()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000);

        Character.OnKarmaChanging = (_, _) => null; // cancel karma only

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Karma);   // karma change cancelled
        Assert.Equal(100, killer.Fame);  // fame unaffected by the karma cancel
    }

    [Fact]
    public void MurderMark_AdjustedCount_IsRecorded()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000);

        Character.OnMurderMark = (_, _, proposed) => new Character.MurderMarkDecision(proposed + 4, true); // script rewrites count

        death.ProcessDeath(victim, killer);

        Assert.Equal(5, killer.Kills); // 1 proposed + 4 adjustment
    }

    [Fact]
    public void ProcessDeath_WritesCorpseOwnerBeforeOnDeathCallback()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100, karma: 0, fame: 0);

        bool foundTaggedCorpse = false;
        death.OnDeath += (dead, _) =>
        {
            foreach (var item in world.GetItemsInRange(dead.Position, 0))
            {
                if (item.ItemType != ItemType.Corpse) continue;
                if (!item.TryGetTag("OWNER_UID", out string? owner)) continue;
                foundTaggedCorpse = owner == dead.Uid.Value.ToString();
            }
        };

        death.ProcessDeath(victim);

        Assert.True(foundTaggedCorpse);
    }
}
