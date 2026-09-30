using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Death;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Murder, kill rewards, attacker threat and the notoriety options against the
/// Source-X reference (CCharNotoriety.cpp Noto_Kill / Noto_CalcFlag / Noto_GetFlag,
/// CCharFight.cpp OnTakeDamage). Each case is a counter-example the previous engine
/// got wrong; the expected values come from the reference code, not from this engine.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatNotorietySourceXTests
{
    private static GameWorld CreateWorld()
    {
        var world = TestHarness.CreateWorld();
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

    private static Character MakeNpc(GameWorld world, int x, NpcBrainType brain = NpcBrainType.Human,
        short fame = 0, short karma = 0)
    {
        var ch = world.CreateCharacter();
        ch.BodyId = 0x0190;
        ch.NpcBrain = brain;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.Fame = fame; ch.Karma = karma;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static GuildManager UseGuilds()
    {
        var guilds = new GuildManager();
        Character.ResolveGuildManager = _ => guilds;
        return guilds;
    }

    private static GuildDef MakeGuild(GameWorld world, GuildManager guilds, Character master,
        GuildAlign align = GuildAlign.Standard, bool town = false)
    {
        var stone = world.CreateItem();
        var guild = guilds.CreateGuild(stone.Uid, town ? "town" : "guild", master.Uid, town);
        guild.Align = align;
        return guild;
    }

    // ---- B6: the murder decision is Noto_Kill's view of the victim ----
    // CCharNotoriety.cpp:560 NotoThem = pKill->Noto_GetFlag(this, false);
    // :575 "else if (NotoThem < NOTO_GUILD_SAME)" for a non-NPC killer, :578 "if (!IsPriv(PRIV_GM))".

    [Fact]
    public void Murder_KarmaNeutralGreyPlayer_IsNotAMurder()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        // Below PLAYERNEUTRAL, above PLAYEREVIL: grey (NOTO_NEUTRAL, 3) to everyone.
        var victim = MakePlayer(world, 101, karma: (short)(Character.PlayerKarmaNeutral - 1000));
        Assert.Equal(3, GameClient.ComputeNotoriety(world, killer, victim));

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
    }

    [Fact]
    public void Murder_SameGuildMember_IsNotAMurder_AndEarnsNoReward()
    {
        var world = CreateWorld();
        var guilds = UseGuilds();
        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101, fame: 6000);
        var guild = MakeGuild(world, guilds, killer);
        guild.JoinAsMember(victim.Uid);
        Assert.Equal(2, GameClient.ComputeNotoriety(world, killer, victim)); // NOTO_GUILD_SAME

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
        // B7: "if (NotoThem == NOTO_GUILD_SAME || ...CONJURED) return;" (:611-613) -
        // not the 6000/10 = 600 the reward block would give.
        Assert.Equal(0, killer.Fame);
    }

    [Fact]
    public void Murder_InnocentNpc_IsAMurder()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101); // human brain, karma 0: NOTO_GOOD
        Assert.Equal(1, GameClient.ComputeNotoriety(world, killer, victim));

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
    }

    [Fact]
    public void Murder_InnocentSummonedNpc_IsAMurder_ButEarnsNoFame()
    {
        // The murder branch runs BEFORE the CONJURED reward exit (:575 then :611):
        // a summon that is innocent to its killer is still a murder.
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101, fame: 6000);
        victim.SetStatFlag(StatFlag.Conjured);
        victim.SetTag("SUMMON_DURATION", "600");
        Assert.Equal(1, GameClient.ComputeNotoriety(world, killer, victim));

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
        Assert.Equal(0, killer.Fame); // not 6000/200 = 30 (B7)
    }

    [Fact]
    public void Murder_InnocentKilledByAGameMaster_IsNotAMurder()
    {
        var world = CreateWorld();
        var gm = MakePlayer(world, 100);
        gm.PrivLevel = PrivLevel.GM;
        Assert.True(gm.IsGmMode);
        var victim = MakePlayer(world, 101);
        Assert.Equal(1, GameClient.ComputeNotoriety(world, gm, victim));

        new DeathEngine(world).ProcessDeath(victim, gm);

        Assert.Equal(0, gm.Kills);
    }

    [Fact]
    public void Murder_AnInnocentPlayerIsStillAMurder()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
    }

    [Fact]
    public void Reward_SameGuildKill_GrantsNoExperience()
    {
        // The same :611 exit covers the experience block (:619-646) too.
        var world = CreateWorld();
        var guilds = UseGuilds();
        Character.ExperienceSystem = true;
        Character.ExperienceMode = Character.ExpModeRaiseCombat;
        var killer = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);
        victim.Exp = 10_000;
        MakeGuild(world, guilds, killer).JoinAsMember(victim.Uid);
        int before = killer.Exp;

        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(before, killer.Exp);
    }

    // ---- B9: threat grows with every blow (CCharFight.cpp:917-938) ----

    [Fact]
    public void Threat_AccumulatesWithDamageOnAnNpc()
    {
        var world = CreateWorld();
        var npc = MakeNpc(world, 100, NpcBrainType.Monster);
        var attacker = MakePlayer(world, 101);

        npc.RecordAttack(attacker.Uid, 10);
        npc.RecordAttack(attacker.Uid, 20);

        int i = npc.CombatState.IndexOfAttacker(attacker.Uid);
        Assert.Equal(30, npc.CombatState.Attackers[i].TotalDamage);
        Assert.Equal(30, npc.CombatState.GetAttackerThreat(i));
    }

    [Fact]
    public void Threat_ScriptWeightKeepsGrowingWithLaterDamage()
    {
        var world = CreateWorld();
        var npc = MakeNpc(world, 100, NpcBrainType.Monster);
        var attacker = MakePlayer(world, 101);
        npc.RecordAttack(attacker.Uid, 10);
        int i = npc.CombatState.IndexOfAttacker(attacker.Uid);
        Assert.True(npc.CombatState.SetAttackerThreat(i, 100)); // ATTACKER.n.THREAT=100

        npc.RecordAttack(attacker.Uid, 5);

        Assert.Equal(105, npc.CombatState.GetAttackerThreat(i));
    }

    [Fact]
    public void Threat_CombatAddWeightIsTheStartingPoint()
    {
        // Attacker_Add stores the @CombatAdd ARGN1 threat (CCharAttacker.cpp:38-53);
        // the blow that put the attacker there then adds its damage.
        var world = CreateWorld();
        var npc = MakeNpc(world, 100, NpcBrainType.Monster);
        var attacker = MakePlayer(world, 101);
        Character.OnCombatAdd = (_, _, ctx) => { ctx.Threat = 50; return true; };

        npc.RecordAttack(attacker.Uid, 10);

        Assert.Equal(60, npc.CombatState.GetAttackerThreat(npc.CombatState.IndexOfAttacker(attacker.Uid)));
    }

    [Fact]
    public void Threat_CombatAddVetoStillKeepsTheAttackerOff()
    {
        var world = CreateWorld();
        var npc = MakeNpc(world, 100, NpcBrainType.Monster);
        var attacker = MakePlayer(world, 101);
        Character.OnCombatAdd = (_, _, _) => false; // RETURN 1

        npc.RecordAttack(attacker.Uid, 10);

        Assert.Equal(-1, npc.CombatState.IndexOfAttacker(attacker.Uid));
    }

    // ---- B10: notoriety options and guild/town relations (CCharNotoriety.cpp:171-276) ----

    private static (GameWorld World, Character Owner, Character Pet) OwnedEvilAnimal()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 100);
        var pet = MakeNpc(world, 101, NpcBrainType.Animal, karma: -1000); // evil: karma <= -800
        pet.SetStatFlag(StatFlag.Pet);
        pet.NpcMaster = owner.Uid;
        return (world, owner, pet);
    }

    [Fact]
    public void PetBehaviorOwnerNeutral_Off_OwnPetIsNeutral()
    {
        var (world, owner, pet) = OwnedEvilAnimal();
        Assert.Equal(3, GameClient.ComputeNotoriety(world, owner, pet));
    }

    [Fact]
    public void PetBehaviorOwnerNeutral_On_OwnerSeesTheTrueNotoriety()
    {
        // :177 "if (!IsSetOF(OF_PetBehaviorOwnerNeutral) && NPC_IsOwnedBy(pCharViewer, false))"
        var (world, owner, pet) = OwnedEvilAnimal();
        GameClient.ServerOptionFlags |= OptionFlags.PetBehaviorOwnerNeutral;
        Assert.Equal(6, GameClient.ComputeNotoriety(world, owner, pet));
    }

    [Fact]
    public void GuildAlign_OrderAgainstChaos_IsEnemy()
    {
        // :235-236 two aligned guilds of different alignment -> NOTO_GUILD_WAR,
        // with or without OF_EnableGuildAlignNotoriety.
        var world = CreateWorld();
        var guilds = UseGuilds();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        MakeGuild(world, guilds, viewer, GuildAlign.Order);
        MakeGuild(world, guilds, subject, GuildAlign.Chaos);

        Assert.Equal(5, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Fact]
    public void GuildAlign_TwoOrderGuilds_AreAlliesOnlyWithTheOption()
    {
        // :231-232 same alignment -> NOTO_GUILD_SAME only with OF_EnableGuildAlignNotoriety.
        var world = CreateWorld();
        var guilds = UseGuilds();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        MakeGuild(world, guilds, viewer, GuildAlign.Order);
        MakeGuild(world, guilds, subject, GuildAlign.Order);

        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));
        GameClient.ServerOptionFlags |= OptionFlags.EnableGuildAlignNotoriety;
        Assert.Equal(2, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Fact]
    public void TownWar_GuildAtWarWithTheViewersTown_IsEnemy()
    {
        // :241-242 my guild at war with the viewer's town -> NOTO_GUILD_WAR.
        var world = CreateWorld();
        var guilds = UseGuilds();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        var town = MakeGuild(world, guilds, viewer, town: true);
        var guild = MakeGuild(world, guilds, subject);
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));

        guilds.DeclareWar(guild.StoneUid, town.StoneUid);
        guilds.DeclareWar(town.StoneUid, guild.StoneUid);

        Assert.Equal(5, GameClient.ComputeNotoriety(world, viewer, subject));
        // And the other way round: my town at war with the viewer's guild (:245-246).
        Assert.Equal(5, GameClient.ComputeNotoriety(world, subject, viewer));
    }

    [Fact]
    public void PermaGrey_IsReadAsASphereNumber()
    {
        // :276 "m_TagDefs.GetKeyNum("NOTO.PERMAGREY")": any non-zero number, 02 included.
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        subject.SetTag("NOTO.PERMAGREY", "02");
        Assert.Equal(3, GameClient.ComputeNotoriety(world, viewer, subject));
        subject.SetTag("NOTO.PERMAGREY", "0");
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Fact]
    public void PetsInheritNotoriety_UsesThePrimaryOwner()
    {
        // :183 NPC_PetGetOwnerRecursive: a pet of a pet inherits from the player at
        // the top of the chain. The middle NPC is owned by the viewer, so its own
        // notoriety to the viewer would be neutral - the primary owner's is red.
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var master = MakePlayer(world, 101);
        master.Kills = (short)(Character.MurderMinCount + 1); // red
        var middle = MakeNpc(world, 102);
        middle.SetStatFlag(StatFlag.Pet);
        middle.NpcMaster = master.Uid;
        var pet = MakeNpc(world, 103);
        pet.SetStatFlag(StatFlag.Pet);
        pet.NpcMaster = middle.Uid;
        Character.PetsInheritNotoriety = 1 << (6 - 1); // NOTO_EVIL bit

        Assert.Equal(6, GameClient.ComputeNotoriety(world, viewer, pet));
    }

    // ---- B11: logical notoriety and display colour are separate ----

    [Fact]
    public void NotoSend_ColourAndNotorietyAreKeptApart()
    {
        // CCharNotoriety.cpp:122-131 - ARGN1 is the notoriety, ARGN2 the colour; the
        // packets send the colour (send.cpp:2404), behaviour reads the notoriety.
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);
        Character.OnNotoSend = (_, _) => new Character.NotorietyResult(1, 6);

        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));
        Assert.Equal(6, GameClient.ComputeNotorietyColor(world, viewer, subject));
    }

    [Fact]
    public void NotoSend_ZeroValuesFallBack()
    {
        // :126-129 NOTO_INVALID notoriety -> Noto_CalcFlag; NOTO_INVALID colour -> the notoriety.
        var world = CreateWorld();
        var viewer = MakePlayer(world, 100);
        var subject = MakePlayer(world, 101);

        Character.OnNotoSend = (_, _) => new Character.NotorietyResult(0, 6);
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, subject));
        Assert.Equal(6, GameClient.ComputeNotorietyColor(world, viewer, subject));

        Character.OnNotoSend = (_, _) => new Character.NotorietyResult(5, 0);
        Assert.Equal(5, GameClient.ComputeNotoriety(world, viewer, subject));
        Assert.Equal(5, GameClient.ComputeNotorietyColor(world, viewer, subject));
    }

    // ---- B12: Noto_Criminal outcomes (CCharNotoriety.cpp:389-431) ----

    private static (Character Criminal, Character Viewer) CrimeSeen()
    {
        var world = CreateWorld();
        var criminal = MakePlayer(world, 100);
        var viewer = MakePlayer(world, 101);
        viewer.Memory_AddObjTypes(criminal.Uid, MemoryType.SawCrime);
        return (criminal, viewer);
    }

    private static bool HasSawCrime(Character viewer, Character criminal) =>
        viewer.Memory_FindObjTypes(criminal.Uid, MemoryType.SawCrime) != null;

    [Fact]
    public void Criminal_Return1_NoFlag_KeepsTheSawCrimeMemory()
    {
        var (criminal, viewer) = CrimeSeen();
        Character.OnCriminalCheck = (_, _, _) =>
            Character.CriminalDecisionFromTrigger(TriggerResult.True, 3);

        criminal.MakeCriminal(viewer, fromSawCrime: true);

        Assert.False(criminal.IsStatFlag(StatFlag.Criminal));
        Assert.True(HasSawCrime(viewer, criminal));
    }

    [Fact]
    public void Criminal_Return0_NoFlag_ButSpendsTheSawCrimeMemory()
    {
        var (criminal, viewer) = CrimeSeen();
        Character.OnCriminalCheck = (_, _, _) =>
            Character.CriminalDecisionFromTrigger(TriggerResult.False, 3);

        criminal.MakeCriminal(viewer, fromSawCrime: true);

        Assert.False(criminal.IsStatFlag(StatFlag.Criminal));
        Assert.False(HasSawCrime(viewer, criminal));
    }

    [Fact]
    public void Criminal_ZeroMinutes_NoFlag_ButSpendsTheSawCrimeMemory()
    {
        var (criminal, viewer) = CrimeSeen();
        Character.OnCriminalCheck = (_, _, _) =>
            Character.CriminalDecisionFromTrigger(TriggerResult.Default, 0);

        criminal.MakeCriminal(viewer, fromSawCrime: true);

        Assert.False(criminal.IsStatFlag(StatFlag.Criminal));
        Assert.False(HasSawCrime(viewer, criminal));
    }

    [Fact]
    public void Criminal_DefaultReturn_FlagsForArgn1Minutes_AndSpendsTheMemory()
    {
        var (criminal, viewer) = CrimeSeen();
        Character.OnCriminalCheck = (_, _, _) =>
            Character.CriminalDecisionFromTrigger(TriggerResult.Default, 2);

        criminal.MakeCriminal(viewer, fromSawCrime: true);

        Assert.True(criminal.IsStatFlag(StatFlag.Criminal));
        Assert.InRange(criminal.CriminalTimerRemainingSeconds, 100, 120);
        Assert.False(HasSawCrime(viewer, criminal));
    }

    [Fact]
    public void Criminal_HookSeesTheViewerAndTheSawCrimeSource()
    {
        var (criminal, viewer) = CrimeSeen();
        Character? seenViewer = null;
        bool seenFromSawCrime = false;
        Character.OnCriminalCheck = (_, v, fromSawCrime) =>
        {
            seenViewer = v;
            seenFromSawCrime = fromSawCrime;
            return Character.CriminalDecisionFromTrigger(TriggerResult.Default, 3);
        };

        criminal.MakeCriminal(viewer, fromSawCrime: true);

        Assert.Same(viewer, seenViewer);
        Assert.True(seenFromSawCrime);
    }
}
