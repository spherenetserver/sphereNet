using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X death/corpse parity II (wiki/death-corpse-remaining.txt): no corpse for
// summons / DEATH_NOCORPSE, NOLOOTDROP, Move_Never corpses, and tightened
// resurrection corpse-rejoin (NOREJOIN / ownership / top-level).
public class DeathCorpseParityIITests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = ch.Hits = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static Character MakeNpc(GameWorld world, int x)
    {
        var npc = world.CreateCharacter();
        npc.Str = 50; npc.MaxHits = npc.Hits = 50;
        world.PlaceCharacter(npc, new Point3D((short)x, 100, 0, 0));
        return npc;
    }

    private static void ClearNotoHooks()
    {
        Character.OnFameChanging = null;
        Character.OnKarmaChanging = null;
        Character.OnMurderMark = null;
    }

    // ---- #7: no corpse for summons / DEATH_NOCORPSE ----

    [Fact]
    public void SummonedCreature_LeavesNoCorpse()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var summon = MakeNpc(world, 1000);
        summon.SetTag("SUMMON_DURATION", "60"); // IsSummoned
        Assert.True(summon.IsSummoned);

        var corpse = death.ProcessDeath(summon);

        Assert.Null(corpse);
        Assert.DoesNotContain(world.GetItemsInRange(new Point3D(1000, 100, 0, 0), 1),
            i => i.ItemType == ItemType.Corpse);
    }

    [Fact]
    public void DeathFlags_NoCorpse_LeavesNoCorpse()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var npc = MakeNpc(world, 1001);
        npc.SetTag("DEATHFLAGS", "0x02"); // DEATH_NOCORPSE

        Assert.Null(death.ProcessDeath(npc));
    }

    [Fact]
    public void DeathFlags_NoLootDrop_MakesEmptyCorpse()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var npc = MakeNpc(world, 1002);
        npc.SetTag("DEATHFLAGS", "0x04"); // DEATH_NOLOOTDROP
        var sword = world.CreateItem();
        sword.BaseId = 0x0F5E;
        npc.Equip(sword, Layer.OneHanded);

        var corpse = death.ProcessDeath(npc);

        Assert.NotNull(corpse);
        Assert.DoesNotContain(corpse!.Contents, i => i.Uid == sword.Uid); // loot not dropped
    }

    [Fact]
    public void Corpse_IsMoveNever()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var corpse = death.ProcessDeath(MakePlayer(world, 1003));

        Assert.NotNull(corpse);
        Assert.True(corpse!.IsAttr(ObjAttributes.Move_Never));
    }

    // ---- #3: resurrection corpse-rejoin gates ----

    [Fact]
    public void RestoreFromCorpse_NoRejoinTag_IsSkipped()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 1004);
        var glove = world.CreateItem(); glove.BaseId = 0x13C6;
        victim.Equip(glove, Layer.Gloves);
        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);

        // A decayed/NOREJOIN corpse (bones) can no longer be rejoined.
        corpse!.SetTag("NOREJOIN", "1");
        Assert.False(death.RestoreFromCorpse(victim));

        corpse.RemoveTag("NOREJOIN");
        Assert.True(death.RestoreFromCorpse(victim)); // a fresh owned corpse rejoins
    }

    [Fact]
    public void RestoreFromCorpse_OtherOwnersCorpse_IsSkipped()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 1005);
        death.ProcessDeath(victim);

        // A different player standing on the corpse cannot rejoin it.
        var stranger = MakePlayer(world, 1005); // same tile, different identity
        Assert.False(death.RestoreFromCorpse(stranger));
    }
}
