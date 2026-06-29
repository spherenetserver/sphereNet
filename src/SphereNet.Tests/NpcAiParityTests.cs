using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X NPC AI parity: NPCAI config + OVERRIDE.NPCAI, INT-gated pathfinding,
// breath/throw special-attack gating, and the generic (offset/maxspells)
// spellbook loader. See wiki/4.txt audit.
public class NpcAiParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item AddPack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // ---- Phase A: config parity ----

    [Fact]
    public void Config_NpcAiKeys_ParseAndDefault()
    {
        // Defaults (Source-X-compatible knobs, SphereNet no-regression NPCAI default).
        var fresh = new SphereConfig();
        Assert.Equal(0x0C41, fresh.NpcAi); // PATH|COMBAT|PERSISTENTPATH|THREAT
        Assert.Equal(30, fresh.NpcHealThreshold);
        Assert.Equal(30, fresh.NpcWanderLookAroundChance);

        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_npcai_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, """
            [SPHERE]
            NPCAI=0
            NPCHealthreshold=55
            NPCWanderLookAroundChance=10
            """);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);

            Assert.Equal(0, config.NpcAi);
            Assert.Equal(55, config.NpcHealThreshold);
            Assert.Equal(10, config.NpcWanderLookAroundChance);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void GetNpcFlags_GlobalDefaultFromConfig()
    {
        var world = CreateWorld();
        var cfg = new SphereConfig { NpcAi = (int)(NpcAIFlags.Path | NpcAIFlags.Looting) };
        var ai = new NpcAI(world, cfg);

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var flags = ai.GetNpcFlags(npc);
        Assert.True(flags.HasFlag(NpcAIFlags.Path));
        Assert.True(flags.HasFlag(NpcAIFlags.Looting));
        Assert.False(flags.HasFlag(NpcAIFlags.Threat));
    }

    [Theory]
    [InlineData("0x0100", true)]   // hex → NPC_AI_LOOTING only
    [InlineData("256", true)]      // decimal 0x100
    public void GetNpcFlags_OverrideNpcAiTag_Wins(string tagValue, bool expectLootingOnly)
    {
        var world = CreateWorld();
        // Global default has no looting; the per-char override turns it on.
        var ai = new NpcAI(world, new SphereConfig { NpcAi = (int)NpcAIFlags.Path });

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        npc.SetTag("OVERRIDE.NPCAI", tagValue);

        var flags = ai.GetNpcFlags(npc);
        Assert.Equal(expectLootingOnly, flags.HasFlag(NpcAIFlags.Looting));
        Assert.False(flags.HasFlag(NpcAIFlags.Path)); // override replaces, not merges
    }

    [Fact]
    public void GetNpcFlags_GarbageOverride_FallsBackToGlobal()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig { NpcAi = (int)NpcAIFlags.Threat });

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        npc.SetTag("OVERRIDE.NPCAI", "not-a-number");

        Assert.True(ai.GetNpcFlags(npc).HasFlag(NpcAIFlags.Threat));
    }

    // ---- Phase C: throw special-attack gating ----

    [Theory]
    [InlineData(0x0001, true)]  // ogre
    [InlineData(0x0002, true)]  // ettin
    [InlineData(0x004C, true)]  // cyclops
    [InlineData(0x0190, false)] // human male
    public void IsRockThrowerBody_MatchesSourceX(int body, bool expected)
    {
        Assert.Equal(expected, NpcAI.IsRockThrowerBody((ushort)body));
    }

    [Fact]
    public void HasThrowableRock_DetectsRockInPack()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, npc);

        Assert.False(NpcAI.HasThrowableRock(npc));

        var rock = world.CreateItem();
        rock.ItemType = ItemType.ARock;
        pack.AddItem(rock);
        Assert.True(NpcAI.HasThrowableRock(npc));
    }

    // ---- Phase D: generic spellbook loader ----

    [Fact]
    public void EnsureNpcSpellsFromBook_ClassicMageryBook_ReadsBothMaskWords()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, npc);

        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.BaseId = 0x0EFA; // no registered def → classic offset 0, 64 spells
        // bit 0 of More1 → spell 1; bit 0 of More2 → spell 33.
        book.More1 = 0x1;
        book.More2 = 0x1;
        pack.AddItem(book);

        NpcAI.EnsureNpcSpellsFromBook(npc);

        Assert.Contains((SpellType)1, npc.NpcSpells);
        Assert.Contains((SpellType)33, npc.NpcSpells);
        Assert.DoesNotContain((SpellType)2, npc.NpcSpells);
    }

    // ---- Phase E: @NPCActCast RETURN 1 aborts the cast and reverts to melee ----

    [Fact]
    public void NpcActCast_Abort_NeverFiresNativeCast()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        bool abortAsked = false;
        bool nativeCastFired = false;
        // @NPCActCast hook always aborts (Source-X RETURN 1).
        ai.OnNpcActCast = (npc, target, spell) =>
        {
            abortAsked = true;
            return new NpcAI.NpcCastDecision(Abort: true, spell, target);
        };
        ai.OnNpcCastSpell = (_, _, _) => nativeCastFired = true;
        ai.OnNpcTickSpellCast = _ => false;

        var caster = world.CreateCharacter();
        caster.NpcBrain = NpcBrainType.Monster;
        caster.Hits = caster.MaxHits = 200;
        caster.Stam = caster.MaxStam = 100;
        caster.Mana = caster.MaxMana = 200;
        caster.Int = 50;
        caster.NpcSpellAdd(SpellType.EnergyBolt);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Hits = victim.MaxHits = 5000;
        world.PlaceCharacter(victim, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(victim);
        world.OnTick(); // activate the sector so the masterless caster acts

        caster.FightTarget = victim.Uid;
        for (int i = 0; i < 60; i++)
        {
            caster.NextNpcActionTime = 0;
            caster.NextAttackTime = 0;
            ai.OnTickAction(caster);
        }

        Assert.True(abortAsked, "the caster never attempted a spell, so the abort path was untested");
        Assert.False(nativeCastFired, "native cast fired despite @NPCActCast returning RETURN 1 (abort)");
    }

    [Fact]
    public void NpcActCast_SpellOverride_CastsScriptChosenSpell()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        SpellType castSpell = SpellType.None;
        ai.OnNpcActCast = (npc, target, spell) =>
            // Proceed, but force Flamestrike regardless of the AI's pick.
            new NpcAI.NpcCastDecision(Abort: false, SpellType.Flamestrike, target);
        ai.OnNpcCastSpell = (_, _, spell) => castSpell = spell;
        ai.OnNpcTickSpellCast = _ => false;

        var caster = world.CreateCharacter();
        caster.NpcBrain = NpcBrainType.Monster;
        caster.Hits = caster.MaxHits = 200;
        caster.Stam = caster.MaxStam = 100;
        caster.Mana = caster.MaxMana = 200;
        caster.Int = 50;
        caster.NpcSpellAdd(SpellType.EnergyBolt);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.IsOnline = true;
        victim.Hits = victim.MaxHits = 5000;
        world.PlaceCharacter(victim, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(victim);
        world.OnTick(); // activate the sector so the masterless caster acts

        caster.FightTarget = victim.Uid;
        for (int i = 0; i < 60 && castSpell == SpellType.None; i++)
        {
            caster.NextNpcActionTime = 0;
            caster.NextAttackTime = 0;
            ai.OnTickAction(caster);
        }

        Assert.Equal(SpellType.Flamestrike, castSpell);
    }

    [Fact]
    public void EnsureNpcSpellsFromBook_NoBookCasterBody_FallsBackToDefaults()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        npc.BodyId = 0x0018; // lich body — default caster spell list
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        AddPack(world, npc);

        NpcAI.EnsureNpcSpellsFromBook(npc);

        Assert.NotEmpty(npc.NpcSpells);
        Assert.Contains(SpellType.EnergyBolt, npc.NpcSpells);
    }
}
