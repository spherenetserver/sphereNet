using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C4 — the COMBAT_SLAYER system (Source-X CFactionDef +
// OnTakeDamage CCharFight.cpp:821): item SLAYER_GROUP/SLAYER_SPECIES vs NPC
// FACTION_GROUP/FACTION_SPECIES scale the post-trigger damage. The matching
// tables are a verbatim CFactionDef port, quirks included.
public class CombatWaveC4SlayerTests
{
    // ---- SlayerFaction matching tables ----

    [Fact]
    public void SlayerTables_MatchSourceXBonuses()
    {
        // Lesser ("single") slayer: same group + same species → +200% (x3).
        var mageSlayer = new SlayerFaction(FactionGroup.Undead, 5);
        var mageNpc = new SlayerFaction(FactionGroup.Undead, 5);
        Assert.Equal(200, mageSlayer.GetSlayerDamageBonusPercent(mageNpc));

        // Group-generic (species 1) weapon vs group-generic NPC resolves as a
        // Lesser match first — Source-X checks Lesser before Super.
        var undeadSlayer = new SlayerFaction(FactionGroup.Undead, 1);
        var genericUndead = new SlayerFaction(FactionGroup.Undead, 1);
        Assert.Equal(200, undeadSlayer.GetSlayerDamageBonusPercent(genericUndead));

        // Super row that survives the Lesser check: the Source-X table's
        // (Abyss slayer vs Elemental species-1 victim) entry → +100% (x2).
        var demonSlayer = new SlayerFaction(FactionGroup.Abyss, 1);
        var genericElemental = new SlayerFaction(FactionGroup.Elemental, 1);
        Assert.Equal(100, demonSlayer.GetSlayerDamageBonusPercent(genericElemental));

        // Wrong group entirely → no bonus.
        var reptile = new SlayerFaction(FactionGroup.Reptilian, 5);
        Assert.Equal(0, mageSlayer.GetSlayerDamageBonusPercent(reptile));

        // Opposite-group penalty: a Super Slayer vs its opposing group → -100.
        var humanoid = new SlayerFaction(FactionGroup.Humanoid, 3);
        Assert.Equal(-100, undeadSlayer.GetSlayerDamagePenaltyPercent(humanoid));
        // A Lesser slayer carries no opposite-group penalty.
        Assert.Equal(0, mageSlayer.GetSlayerDamagePenaltyPercent(humanoid));
    }

    // ---- ResolveAttack integration ----

    private static (Character Attacker, Character Target, SphereNet.Game.Objects.Items.Item Weapon)
        MakeSlayerFight(GameWorld world, bool attackerPlayer = true, bool targetPlayer = false)
    {
        var attacker = world.CreateCharacter();
        attacker.IsPlayer = attackerPlayer;
        attacker.PrivLevel = PrivLevel.GM; // era-0 roll: a GM always hits
        attacker.Str = 50; attacker.Dex = 50;
        attacker.Hits = attacker.MaxHits = 100;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = targetPlayer;
        target.Hits = target.MaxHits = 100;
        target.SetSkill(SkillType.Parrying, 0);
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.BaseId = 0x0F5E;
        attacker.Equip(sword, Layer.OneHanded);
        return (attacker, target, sword);
    }

    [Fact]
    public void ResolveAttack_SlayerWeapon_TriplesDamageAgainstMatchingNpc()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10); // fixed base damage

            var world = TestHarness.CreateWorld();
            var (attacker, npc, sword) = MakeSlayerFight(world);
            sword.SetTag("SLAYER_GROUP", "0x10");   // Undead
            sword.SetTag("SLAYER_SPECIES", "5");    // lesser: mage slayer
            npc.SetTag("FACTION_GROUP", "0x10");
            npc.SetTag("FACTION_SPECIES", "5");

            // Lesser slayer vs its species: 10 base → +200% = 30.
            Assert.Equal(30, CombatEngine.ResolveAttack(attacker, npc, sword, CombatFlags.Slayer));

            // Without COMBAT_SLAYER the same setup deals base damage.
            npc.Hits = npc.MaxHits;
            Assert.Equal(10, CombatEngine.ResolveAttack(attacker, npc, sword, CombatFlags.None));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void ResolveAttack_TalismanSlayer_IsTheFallbackWhenTheWeaponHasNone()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10);

            var world = TestHarness.CreateWorld();
            var (attacker, npc, sword) = MakeSlayerFight(world);
            npc.SetTag("FACTION_GROUP", "0x20");    // Arachnid
            npc.SetTag("FACTION_SPECIES", "2");     // scorpion-style lesser species

            var talisman = world.CreateItem();
            talisman.SetTag("SLAYER_GROUP", "0x20");
            talisman.SetTag("SLAYER_SPECIES", "2");
            attacker.Equip(talisman, Layer.Talisman);

            Assert.Equal(30, CombatEngine.ResolveAttack(attacker, npc, sword, CombatFlags.Slayer));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void ResolveAttack_NpcWithOppositeSuperSlayer_AppliesPenaltyToPlayerVictim()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10);

            var world = TestHarness.CreateWorld();
            var (npc, player, sword) = MakeSlayerFight(world, attackerPlayer: false, targetPlayer: true);
            sword.SetTag("SLAYER_GROUP", "0x10");   // Undead SUPER slayer
            sword.SetTag("SLAYER_SPECIES", "1");
            player.SetTag("FACTION_GROUP", "0x08"); // Humanoid — Undead's opposite
            player.SetTag("FACTION_SPECIES", "3");

            // Source-X penalty arithmetic: -100% folds the hit to zero.
            Assert.Equal(0, CombatEngine.ResolveAttack(npc, player, sword, CombatFlags.Slayer));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    // ---- Script surface ----

    [Fact]
    public void DefParsers_AcceptFactionAndSlayerKeys_AsDefTags()
    {
        var charDef = new SphereNet.Scripting.Definitions.CharDef(default);
        charDef.LoadFromKey("FACTION_GROUP", "0x10");
        charDef.LoadFromKey("FACTION_SPECIES", "1");
        Assert.Equal("0x10", charDef.TagDefs.Get("FACTION_GROUP"));
        Assert.Equal("1", charDef.TagDefs.Get("FACTION_SPECIES"));

        var itemDef = new SphereNet.Scripting.Definitions.ItemDef(default);
        itemDef.LoadFromKey("SLAYER_GROUP", "0x20");
        itemDef.LoadFromKey("SLAYER_SPECIES", "2");
        Assert.Equal("0x20", itemDef.TagDefs.Get("SLAYER_GROUP"));
        Assert.Equal("2", itemDef.TagDefs.Get("SLAYER_SPECIES"));
    }

    [Fact]
    public void RuntimeProperties_FactionAndSlayer_AreTagBacked()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TrySetProperty("FACTION_GROUP", "16"));
        Assert.True(ch.TryGetProperty("FACTION_GROUP", out var g) && g == "16");
        Assert.Equal(FactionGroup.Undead, SlayerFaction.FromChar(ch).Group);

        var item = world.CreateItem();
        Assert.True(item.TrySetProperty("SLAYER_GROUP", "0x20"));
        Assert.True(item.TrySetProperty("SLAYER_SPECIES", "1"));
        Assert.True(item.TryGetProperty("SLAYER_SPECIES", out var s) && s == "1");
        var slayer = SlayerFaction.FromItem(item);
        Assert.Equal(FactionGroup.Arachnid, slayer.Group);
        Assert.True(slayer.HasSuperSlayer);
    }
}
