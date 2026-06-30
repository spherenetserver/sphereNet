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

    // ---- #6: death shroud / resurrection robe ----

    [Fact]
    public void EquipDeathShroud_OnPlayer_EquipsMoveNeverShroud()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var player = MakePlayer(world, 1100); // Robe layer free (only Pack equipped)

        var shroud = death.EquipDeathShroud(player);

        Assert.NotNull(shroud);
        Assert.Same(shroud, player.GetEquippedItem(Layer.Robe));
        Assert.True(shroud!.IsAttr(ObjAttributes.Move_Never)); // looters can't strip the ghost
        Assert.True(shroud.TryGetTag("DEATHSHROUD", out _));
    }

    [Fact]
    public void EquipDeathShroud_OnNpc_DoesNothing()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var npc = MakeNpc(world, 1101);

        Assert.Null(death.EquipDeathShroud(npc));
        Assert.Null(npc.GetEquippedItem(Layer.Robe));
    }

    [Fact]
    public void EquipDeathShroud_WhenDisabled_DoesNothing()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var player = MakePlayer(world, 1102);

        DeathEngine.EnableDeathShroud = false; // restored by ResetEngineStatics
        Assert.Null(death.EquipDeathShroud(player));
        Assert.Null(player.GetEquippedItem(Layer.Robe));
    }

    [Fact]
    public void RemoveDeathShroud_RemovesShroud_KeepsRealRobe()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var player = MakePlayer(world, 1103);

        // A normal robe must survive (only the tagged shroud is removed).
        var realRobe = world.CreateItem(); realRobe.BaseId = 0x1F03;
        player.Equip(realRobe, Layer.Robe);
        death.RemoveDeathShroud(player);
        Assert.Same(realRobe, player.GetEquippedItem(Layer.Robe));

        // A shroud is removed, freeing the Robe layer.
        player.Unequip(Layer.Robe);
        death.EquipDeathShroud(player);
        death.RemoveDeathShroud(player);
        Assert.Null(player.GetEquippedItem(Layer.Robe));
    }

    [Fact]
    public void EnsureResurrectionRobe_GivesRobeOnlyWhenNaked()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var player = MakePlayer(world, 1104);

        var robe = death.EnsureResurrectionRobe(player);
        Assert.NotNull(robe);
        Assert.Same(robe, player.GetEquippedItem(Layer.Robe));

        // Already covered → no duplicate robe.
        Assert.Null(death.EnsureResurrectionRobe(player));
    }

    // ---- #5: bonded pet kept as a ghost (no server-side delete) ----

    [Fact]
    public void BondedPet_Death_KeepsMobileAliveAsGhost()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var pet = MakeNpc(world, 1110);
        pet.SetStatFlag(StatFlag.Pet);
        pet.IsBonded = true;
        Assert.True(pet.IsBonded);

        var corpse = death.ProcessDeath(pet);

        // Corpse exists, the pet is dead but NOT removed from the world — the
        // view-delta hides it from plain players and re-draws it on resurrect.
        Assert.NotNull(corpse);
        Assert.True(pet.IsDead);
        Assert.False(pet.IsDeleted);
        Assert.NotNull(world.FindObject(pet.Uid));

        // Resurrection brings it back alive, ready for the view-delta re-draw.
        pet.Resurrect();
        Assert.False(pet.IsDead);
        Assert.False(pet.IsDeleted);
    }

    [Fact]
    public void UnbondedNpc_Death_RemovesMobile()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var npc = MakeNpc(world, 1111); // not bonded

        var corpse = death.ProcessDeath(npc);

        Assert.NotNull(corpse);
        Assert.True(npc.IsDeleted); // ordinary NPC is deleted server-side
    }
}
