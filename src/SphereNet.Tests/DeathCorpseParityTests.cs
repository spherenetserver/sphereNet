using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X death/corpse parity (wiki/6.txt audit): @Death/@Kill RETURN 1 honored
// (cancel death / skip killer credit), forensics+carve tag alignment, expanded
// loot retention, and corpse-looting criminality gated on a still-present innocent
// player owner.
public class DeathCorpseParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, short karma = 0, short fame = 0)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.Karma = karma; ch.Fame = fame;
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

    // ---- A: @Death / @Kill RETURN 1 ----

    [Fact]
    public void Death_TriggerReturnsTrue_CancelsDeath()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Death", (_, _) => TriggerResult.True);
        death.TriggerDispatcher = dispatcher;

        var victim = MakePlayer(world, 100);
        var killer = MakePlayer(world, 101);

        var corpse = death.ProcessDeath(victim, killer);

        Assert.Null(corpse);             // no corpse made
        Assert.False(victim.IsDead);     // death vetoed — victim still alive
    }

    [Fact]
    public void Kill_TriggerReturnsTrue_SkipsKillerCredit_ButDeathProceeds()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var dispatcher = new TriggerDispatcher();
        // @Kill fires on the killer (a player → EVENTSPLAYER global set).
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", (_, _) => TriggerResult.True);
        death.TriggerDispatcher = dispatcher;

        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000); // innocent

        var corpse = death.ProcessDeath(victim, killer);

        Assert.NotNull(corpse);          // death still happened
        Assert.True(victim.IsDead);
        Assert.Equal(0, killer.Kills);   // murder credit skipped
        Assert.Equal(0, killer.Fame);    // fame credit skipped
        Assert.False(killer.IsCriminal);
    }

    // ---- B: forensics / carve tags ----

    [Fact]
    public void Corpse_HasDeathTimeTag_AndCarveSetsCorpseCarved()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 100);
        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);
        Assert.True(corpse!.TryGetTag("DEATH_TIME", out _)); // forensics reads this

        death.CarveCorpse(victim, corpse);
        Assert.True(corpse.TryGetTag("CORPSE_CARVED", out string? cv) && cv == "1");
    }

    // ---- C: looting criminality ----

    [Fact]
    public void Looting_MonsterCorpse_IsNotCriminal()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world) { LootingIsACrime = true };

        var npc = world.CreateCharacter(); // not a player → deleted on death
        npc.Str = 50; npc.MaxHits = npc.Hits = 50;
        world.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));
        var corpse = death.ProcessDeath(npc);
        Assert.NotNull(corpse);

        var looter = MakePlayer(world, 1001);
        Assert.False(death.IsLootingCriminal(looter, corpse!)); // owner gone → free loot
    }

    [Fact]
    public void Looting_InnocentPlayerCorpse_IsCriminal()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world) { LootingIsACrime = true };

        var owner = MakePlayer(world, 1000); // innocent player victim (stays as ghost)
        var corpse = death.ProcessDeath(owner);
        Assert.NotNull(corpse);

        var looter = MakePlayer(world, 1002);
        Assert.True(death.IsLootingCriminal(looter, corpse!));
        Assert.False(death.IsLootingCriminal(owner, corpse!)); // own corpse exempt
    }

    // ---- D: loot retention ----

    [Fact]
    public void DropLoot_KeepsMoveNeverAndNoTradeItems_OnOwner()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 100);

        // A move-never ring stays equipped; a no-trade item in the pack is kept;
        // a plain item drops to the corpse.
        var ring = world.CreateItem();
        ring.BaseId = 0x108A;
        ring.SetAttr(ObjAttributes.Move_Never);
        victim.Equip(ring, Layer.Ring);

        var bound = world.CreateItem();
        bound.BaseId = 0x1F03;
        bound.SetAttr(ObjAttributes.NotRading);
        victim.Backpack!.AddItem(bound);

        var loose = world.CreateItem();
        loose.BaseId = 0x1F04;
        victim.Backpack!.AddItem(loose);

        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);

        Assert.Equal(Layer.Ring, victim.GetEquippedItem(Layer.Ring)?.EquipLayer); // ring kept on owner
        Assert.DoesNotContain(corpse!.Contents, i => i.Uid == ring.Uid);
        Assert.DoesNotContain(corpse.Contents, i => i.Uid == bound.Uid);
        Assert.Contains(corpse.Contents, i => i.Uid == loose.Uid);
    }
}
