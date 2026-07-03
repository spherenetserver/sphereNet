using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// NPC AI wave N1 (wiki/npcai.txt) — behavior for the dormant NPC_AI_* flags
// (FOOD, EXTRA, COMBAT, MOVEOBSTACLES), the Source-X wand gate (ATTR_MAGIC),
// and the @NPCActCast ARGN2=wand-use contract. NPC_AI_VEND_TIME stays
// deferred — it is define-only in Source-X as well.
[Collection("DefinitionLoaderSerial")]
public class NpcAiWaveN1Tests
{
    private static GameWorld CreateWorld()
    {
        var world = TestHarness.CreateWorld();
        var observer = world.CreateCharacter();
        observer.IsPlayer = true;
        observer.IsOnline = true;
        world.PlaceCharacter(observer, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(observer);
        world.OnTick(); // activate the sector so masterless NPCs act
        return world;
    }

    private static void LoadN1Defs()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_n1defs_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [CHARDEF 0bba]
            DEFNAME=c_n1_humanoid
            CAN=0300

            [ITEMDEF 0baa]
            DEFNAME=i_n1_crate
            CAN=0008
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    // ---- NPC_AI_FOOD ----

    [Fact]
    public void FoodFlag_TriggersTheFeedingPass()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        ai.Flags |= NpcAIFlags.Food; // basic food search — previously a no-op

        var deer = world.CreateCharacter();
        deer.NpcBrain = NpcBrainType.Animal;
        deer.NpcFood = 10; // hungry
        world.PlaceCharacter(deer, new Point3D(100, 100, 0, 0));

        var apple = world.CreateItem();
        apple.ItemType = ItemType.Fruit;
        apple.BaseId = 0x09D0;
        apple.Amount = 1;
        world.PlaceItem(apple, new Point3D(101, 100, 0, 0)); // adjacent

        for (int i = 0; i < 10 && !apple.IsDeleted; i++)
        {
            deer.NextNpcActionTime = 0;
            ai.OnTickAction(deer);
        }

        Assert.True(apple.IsDeleted);
    }

    // ---- NPC_AI_COMBAT (ally support gate) ----

    [Fact]
    public void CombatFlag_GatesAllyHealTargeting()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var healer = world.CreateCharacter();
        healer.NpcBrain = NpcBrainType.Monster;
        healer.Hits = healer.MaxHits = 100; // healthy — no self-heal pull
        healer.Mana = healer.MaxMana = 100;
        healer.Int = 50;
        healer.NpcSpellAdd(SpellType.Heal);
        world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));

        var ally = world.CreateCharacter();
        ally.NpcBrain = NpcBrainType.Monster;
        ally.MaxHits = 100;
        ally.Hits = 20; // badly wounded
        world.PlaceCharacter(ally, new Point3D(101, 100, 0, 0));

        var enemy = world.CreateCharacter();
        enemy.IsPlayer = true;
        enemy.Hits = enemy.MaxHits = 100;
        world.PlaceCharacter(enemy, new Point3D(103, 100, 0, 0));

        // Without NPC_AI_COMBAT the friend scan never runs (Source-X friend
        // list is self-only) — the ally must never be picked as cast target.
        ai.Flags &= ~NpcAIFlags.Combat;
        for (int i = 0; i < 60; i++)
        {
            var (_, castTarget) = ai.ChooseBestSpell(healer, enemy, 3);
            Assert.NotEqual(ally, castTarget);
        }

        // With the flag the wounded ally is eventually chosen for the heal.
        ai.Flags |= NpcAIFlags.Combat;
        bool allyPicked = false;
        for (int i = 0; i < 200 && !allyPicked; i++)
        {
            var (spell, castTarget) = ai.ChooseBestSpell(healer, enemy, 3);
            allyPicked = castTarget == ally && spell == SpellType.Heal;
        }
        Assert.True(allyPicked, "NPC_AI_COMBAT never routed the heal to the wounded ally");
    }

    // ---- NPC_AI_EXTRA ----

    [Fact]
    public void ExtraFlag_WarHumanoid_EquipsWeaponAndShieldFromPack()
    {
        LoadN1Defs();
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        ai.Flags |= NpcAIFlags.Extra;

        var guardsman = world.CreateCharacter();
        guardsman.NpcBrain = NpcBrainType.Human;
        guardsman.CharDefIndex = 0x0BBA; // CAN=0300 (equip + usehands)
        guardsman.Hits = guardsman.MaxHits = 100;
        guardsman.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(guardsman, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        guardsman.Equip(pack, Layer.Pack);
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        pack.AddItem(sword);
        var shield = world.CreateItem();
        shield.ItemType = ItemType.Shield;
        pack.AddItem(shield);

        guardsman.NextNpcActionTime = 0;
        ai.OnTickAction(guardsman);

        Assert.Equal(sword, guardsman.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(shield, guardsman.GetEquippedItem(Layer.TwoHanded));
    }

    [Fact]
    public void ExtraFlag_PeacefulHumanoid_CarriesALightByNight_StowsItByDay()
    {
        LoadN1Defs();
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        ai.Flags |= NpcAIFlags.Extra;

        var townsman = world.CreateCharacter();
        townsman.NpcBrain = NpcBrainType.Human;
        townsman.CharDefIndex = 0x0BBA;
        townsman.Hits = townsman.MaxHits = 100;
        world.PlaceCharacter(townsman, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        townsman.Equip(pack, Layer.Pack);
        var torch = world.CreateItem();
        torch.ItemType = ItemType.LightOut;
        pack.AddItem(torch);

        // Night: the torch comes out of the pack into the free hand.
        ai.GetLightLevel = _ => 28;
        townsman.NextNpcActionTime = 0;
        ai.OnTickAction(townsman);
        Assert.Equal(torch, townsman.GetEquippedItem(Layer.TwoHanded));

        // Day: it goes back into the pack.
        ai.GetLightLevel = _ => 5;
        townsman.NextNpcActionTime = 0;
        ai.OnTickAction(townsman);
        Assert.Null(townsman.GetEquippedItem(Layer.TwoHanded));
        Assert.Contains(torch, pack.Contents);
    }

    // ---- NPC_AI_MOVEOBSTACLES ----

    [Fact]
    public void MoveObstacles_ShiftsABlockingMovableItem()
    {
        LoadN1Defs();
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var porter = world.CreateCharacter();
        porter.CharDefIndex = 0x0BBA; // CAN includes usehands
        porter.Int = 300;             // always beats the Source-X rand(100) roll
        world.PlaceCharacter(porter, new Point3D(100, 100, 0, 0));

        var crate = world.CreateItem();
        crate.BaseId = 0x0BAA;        // ITEMDEF CAN=0008 (blocking)
        world.PlaceItem(crate, new Point3D(101, 100, 0, 0));

        var blocked = new Point3D(101, 100, 0, 0);

        // Without the flag nothing moves.
        ai.Flags &= ~NpcAIFlags.MoveObstacles;
        Assert.False(ai.TryClearObstacle(porter, blocked));

        // With it the crate is shifted onto the NPC's own tile (Source-X).
        ai.Flags |= NpcAIFlags.MoveObstacles;
        Assert.True(ai.TryClearObstacle(porter, blocked));
        Assert.Equal(porter.X, crate.X);
        Assert.Equal(porter.Y, crate.Y);
    }

    // ---- Wand gate + @NPCActCast ARGN2 ----

    [Fact]
    public void NpcWand_RequiresTheMagicAttribute()
    {
        var world = TestHarness.CreateWorld();
        var mage = world.CreateCharacter();
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.More1 = (uint)SpellType.Fireball;
        mage.Equip(wand, Layer.OneHanded);

        // Source-X NPC_FightMagery requires ATTR_MAGIC on the wand.
        Assert.Null(NpcAI.FindNpcWand(mage));

        wand.Attributes |= ObjAttributes.Magic;
        Assert.NotNull(NpcAI.FindNpcWand(mage));
    }

    [Fact]
    public void NpcActCast_ReportsWandUse()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        bool? sawWandUse = null;
        ai.OnNpcActCast = (npc, target, spell, wandUse) =>
        {
            sawWandUse = wandUse;
            return new NpcAI.NpcCastDecision(Abort: true, spell, target);
        };
        ai.OnNpcTickSpellCast = _ => false;

        var caster = world.CreateCharacter();
        caster.NpcBrain = NpcBrainType.Monster;
        caster.Hits = caster.MaxHits = 200;
        caster.Stam = caster.MaxStam = 100;
        caster.Mana = caster.MaxMana = 200;
        caster.Int = 50;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        // No spell list — only an equipped charged magic wand, so the ONLY
        // cast path is the wand one (@NPCActCast must see ARGN2 = 1).
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.More1 = (uint)SpellType.Fireball;
        wand.Attributes |= ObjAttributes.Magic;
        wand.SetTag("CHARGES", "50");
        caster.Equip(wand, Layer.OneHanded);

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Hits = victim.MaxHits = 5000;
        world.PlaceCharacter(victim, new Point3D(103, 100, 0, 0));
        world.AddOnlinePlayer(victim);
        world.OnTick();

        caster.FightTarget = victim.Uid;
        for (int i = 0; i < 100 && sawWandUse == null; i++)
        {
            caster.NextNpcActionTime = 0;
            caster.NextAttackTime = 0;
            ai.OnTickAction(caster);
        }

        Assert.True(sawWandUse, "the wand cast never fired @NPCActCast with ARGN2 = wand-use");
    }
}
