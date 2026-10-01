using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Kill credit against the Source-X reference: CChar::Death's attacker loop
/// (CCharAct.cpp:4357-4378) and CChar::Noto_Kill / Noto_Murder
/// (CCharNotoriety.cpp:379-387, :555-647). Expected values come from the
/// reference code.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class KillCreditSourceXTests
{
    private const int DecaySeconds = 28800; // MURDERDECAYTIME default (8h)

    private static GameWorld CreateWorld()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.MurderDecayTimeSeconds = DecaySeconds;
        Character.MurderMinCount = 5;
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

    // ---- C1: Noto_Murder renews the murder decay with the full MURDERDECAYTIME ----

    [Fact]
    public void FirstMurder_ArmsTheFullDecayImmediately()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101); // innocent human NPC

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(1, killer.Kills);
        Assert.InRange(killer.MurderDecayRemainingSeconds, DecaySeconds - 5, DecaySeconds);
    }

    [Fact]
    public void NewMurder_RenewsAShortRemainingDecay()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        killer.Kills = 2;
        killer.MurderDecayRemainingSeconds = 60;
        var victim = MakeNpc(world, 101);

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(3, killer.Kills);
        Assert.InRange(killer.MurderDecayRemainingSeconds, DecaySeconds - 5, DecaySeconds);
    }

    [Fact]
    public void MurderWithCriminalFlagSuppressed_StillRenewsTheDecay()
    {
        // ARGN2=0 only skips Noto_Criminal (:600-601); Noto_Murder (:603) still runs.
        var world = CreateWorld();
        Character.OnMurderMark = (_, _, proposed) => new Character.MurderMarkDecision(proposed, false);
        var killer = MakePlayer(world, 100);
        killer.Kills = 2;
        killer.MurderDecayRemainingSeconds = 60;
        var victim = MakeNpc(world, 101);

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(3, killer.Kills);
        Assert.False(killer.IsStatFlag(StatFlag.Criminal));
        Assert.InRange(killer.MurderDecayRemainingSeconds, DecaySeconds - 5, DecaySeconds);
    }

    [Fact]
    public void VetoedMurderMark_LeavesCountAndDecayAlone()
    {
        var world = CreateWorld();
        Character.OnMurderMark = (_, _, _) => new Character.MurderMarkDecision(null, false);
        var killer = MakePlayer(world, 100);
        killer.Kills = 2;
        killer.MurderDecayRemainingSeconds = 60;
        var victim = MakeNpc(world, 101);

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(2, killer.Kills);
        Assert.InRange(killer.MurderDecayRemainingSeconds, 0, 60);
    }

    [Fact]
    public void MurderMarkSettingZeroKills_CreatesNoDecay()
    {
        // Noto_Murder only creates the memory while m_wMurders is non-zero (:385).
        var world = CreateWorld();
        Character.OnMurderMark = (_, _, _) => new Character.MurderMarkDecision(0, true);
        var killer = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101);

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Kills);
        Assert.Equal(0, killer.MurderDecayRemainingSeconds);
    }

    [Fact]
    public void NonMurderKill_DoesNotRenewTheDecay()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        killer.Kills = 2;
        killer.MurderDecayRemainingSeconds = 60;
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, karma: -5000); // red to everyone

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(2, killer.Kills);
        Assert.InRange(killer.MurderDecayRemainingSeconds, 0, 60);
    }

    [Fact]
    public void MurderPastTheThreshold_TellsTheKillerHeIsAMurderer()
    {
        // Noto_Murder: "if (Noto_IsMurderer()) SysMessageDefault(DEFMSG_MSG_MURDERER)".
        var world = CreateWorld();
        var messages = new List<(Character, string)>();
        Character.SendOwnerMessage = (ch, msg) => messages.Add((ch, msg));
        var killer = MakePlayer(world, 100);
        killer.Kills = 5;
        var victim = MakeNpc(world, 101);

        victim.RecordAttack(killer.Uid, 10); // the blow that credits the kill
        new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.Equal(6, killer.Kills);
        Assert.Contains(messages, m => m.Item1 == killer &&
            m.Item2 == SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MsgMurderer));
    }

    // ---- C3: a guard killing an NPC conjures it (Noto_Kill :568-574) ----

    [Fact]
    public void GuardKillingAnNpc_LeavesNoCorpseAndEarnsNoReward()
    {
        var world = CreateWorld();
        var guard = MakeNpc(world, 100, NpcBrainType.Guard);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(guard.Uid, 20);

        var corpse = new DeathEngine(world).ProcessDeath(victim, guard);

        Assert.Null(corpse);
        Assert.True(victim.IsStatFlag(StatFlag.Conjured));
        Assert.Equal(0, guard.Fame);
    }

    [Fact]
    public void GuardKillingAPlayer_StillLeavesACorpse()
    {
        var world = CreateWorld();
        var guard = MakeNpc(world, 100, NpcBrainType.Guard);
        var victim = MakePlayer(world, 101);
        victim.RecordAttack(guard.Uid, 20);

        var corpse = new DeathEngine(world).ProcessDeath(victim, guard);

        Assert.NotNull(corpse);
        Assert.False(victim.IsStatFlag(StatFlag.Conjured));
    }

    [Fact]
    public void GuardKillingAnNpcWithHasCorpseFlag_KeepsTheCorpse()
    {
        // MakeCorpse: CONJURED leaves no corpse unless DEATH_NOCONJUREDEFFECT|DEATH_HASCORPSE.
        var world = CreateWorld();
        var guard = MakeNpc(world, 100, NpcBrainType.Guard);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.SetTag("DEATHFLAGS", "0x10");
        victim.RecordAttack(guard.Uid, 20);

        var corpse = new DeathEngine(world).ProcessDeath(victim, guard);

        Assert.NotNull(corpse);
        Assert.True(victim.IsStatFlag(StatFlag.Conjured));
        Assert.Equal(0, guard.Fame);
    }

    [Fact]
    public void NonGuardNpcKillingAnNpc_LeavesACorpseAndEarnsTheReward()
    {
        var world = CreateWorld();
        var killer = MakeNpc(world, 100, NpcBrainType.Monster);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(killer.Uid, 20);

        var corpse = new DeathEngine(world).ProcessDeath(victim, killer);

        Assert.NotNull(corpse);
        Assert.Equal(30, killer.Fame); // 6000/200
    }

    // ---- C7: the attacker on the list is credited, not its owner ----

    [Fact]
    public void PetKill_RunsKillOnThePetAndPaysThePet()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 99);
        var pet = MakeNpc(world, 100, NpcBrainType.Animal);
        pet.NpcMaster = owner.Uid;
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(pet.Uid, 20);

        var killTargets = new List<(Character Ch, Character? Src)>();
        var dispatcher = new TriggerDispatcher();
        TriggerDispatcher.TriggerHandler record = (obj, args) =>
        {
            killTargets.Add(((Character)obj, args.CharSrc));
            return TriggerResult.Default;
        };
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", record);
        dispatcher.RegisterCharEvent("EVENTSPET", "Kill", record);

        new DeathEngine(world) { TriggerDispatcher = dispatcher }.ProcessDeath(victim, pet);

        Assert.Contains(killTargets, k => k.Ch == pet && k.Src == pet);
        Assert.DoesNotContain(killTargets, k => k.Ch == owner);
        Assert.Equal(30, pet.Fame);
        Assert.Equal(0, owner.Fame);
    }

    [Fact]
    public void PetKillingAnInnocent_IsNoMurderForPetOrOwner()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 99);
        var pet = MakeNpc(world, 100, NpcBrainType.Animal);
        pet.NpcMaster = owner.Uid;
        var victim = MakeNpc(world, 101); // innocent human
        victim.RecordAttack(pet.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim, pet);

        Assert.Equal(0, owner.Kills);
        Assert.Equal(0, pet.Kills);
        Assert.Equal(0, owner.MurderDecayRemainingSeconds);
    }

    [Fact]
    public void OwnerAndPetBothStriking_AreCreditedSeparately()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 99);
        var pet = MakeNpc(world, 100, NpcBrainType.Animal);
        pet.NpcMaster = owner.Uid;
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(owner.Uid, 10);
        victim.RecordAttack(pet.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim, pet);

        Assert.Equal(15, owner.Fame); // 6000/200/2
        Assert.Equal(15, pet.Fame);
    }

    // ---- C9: the reward is divided by the full attacker-list size ----

    [Fact]
    public void Reward_IsSplitByTheFullAttackerList()
    {
        var world = CreateWorld();
        var a = MakePlayer(world, 99);
        var b = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(a.Uid, 20);
        Assert.True(victim.CombatState.AddAttacker(b.Uid)); // engaged, never hit
        Assert.Equal(2, victim.Attackers.Count);

        new DeathEngine(world).ProcessDeath(victim, a);

        Assert.Equal(15, a.Fame); // 6000/200 = 30, divided by GetAttackersCount() = 2
        Assert.Equal(0, b.Fame);  // amountDone == 0: no credit at all
    }

    [Fact]
    public void Reward_SoleAttackerTakesTheWhole()
    {
        var world = CreateWorld();
        var a = MakePlayer(world, 99);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(a.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim, a);

        Assert.Equal(30, a.Fame);
    }

    // ---- The attacker loop itself (CCharAct.cpp:4352-4378) ----

    [Fact]
    public void IgnoredAttackerEntry_IsStillCredited()
    {
        // The reference's death loop does not look at LastAttackers::ignore.
        var world = CreateWorld();
        var a = MakePlayer(world, 99);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(a.Uid, 20);
        Assert.True(victim.CombatState.SetAttackerIgnored(a.Uid, true));

        new DeathEngine(world).ProcessDeath(victim, a);

        Assert.Equal(30, a.Fame);
    }

    [Fact]
    public void FinalBlowArgumentNotOnTheAttackerList_EarnsNothing()
    {
        // Credit is read from m_lastAttackers only; there is no killer-first entry.
        var world = CreateWorld();
        var a = MakePlayer(world, 99);
        var outsider = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(a.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim, outsider);

        Assert.Equal(30, a.Fame);       // the only entry, divided by a list of 1
        Assert.Equal(0, outsider.Fame);
    }

    [Fact]
    public void KillAndNotoKill_RunPerEntry_InListOrder()
    {
        // @Kill of the second entry runs after the first entry's Noto_Kill has paid it.
        var world = CreateWorld();
        var a = MakePlayer(world, 99);
        var b = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101, NpcBrainType.Monster, fame: 6000, karma: -1000);
        victim.RecordAttack(a.Uid, 20);
        victim.RecordAttack(b.Uid, 5);

        var seen = new List<(Character Who, int AFameAtThatPoint)>();
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", (obj, _) =>
        {
            seen.Add(((Character)obj, a.Fame));
            return TriggerResult.Default;
        });

        new DeathEngine(world) { TriggerDispatcher = dispatcher }.ProcessDeath(victim, b);

        Assert.Equal(2, seen.Count);
        Assert.Same(a, seen[0].Who);
        Assert.Equal(0, seen[0].AFameAtThatPoint);
        Assert.Same(b, seen[1].Who);
        Assert.Equal(15, seen[1].AFameAtThatPoint);
    }

    // ---- The notoriety clocks are worn memories (Spell_Effect_Create) ----

    [Fact]
    public void Murder_WearsTheMurderDecayMemory()
    {
        var world = CreateWorld();
        var killer = MakePlayer(world, 100);
        var victim = MakeNpc(world, 101);
        victim.RecordAttack(killer.Uid, 10);

        new DeathEngine(world).ProcessDeath(victim, killer);

        var mem = killer.GetEquippedItem(Layer.FlagMurders);
        Assert.NotNull(mem);
        Assert.Equal("Murder Decay", mem!.Name);
        Assert.Equal(ItemType.Spell, mem.ItemType);
        Assert.Equal(Layer.FlagMurders, mem.EquipLayer);
        Assert.InRange(mem.Timeout - Environment.TickCount64, (DecaySeconds - 5) * 1000L, DecaySeconds * 1000L);
    }

    [Fact]
    public void CriminalFlag_IsTheWornCriminalTimerMemory()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);

        ch.MakeCriminal();

        var mem = ch.GetEquippedItem(Layer.FlagCriminal);
        Assert.NotNull(mem);
        Assert.Equal("Criminal Timer", mem!.Name);
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));
        Assert.True(ch.IsCriminal);

        world.DeleteObject(mem); // OnRemoveObj, LAYER_FLAG_Criminal
        Assert.False(ch.IsStatFlag(StatFlag.Criminal));
        Assert.False(ch.IsCriminal);
    }

    [Fact]
    public void CriminalMemory_EndsOnItsOwnTimer()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.MakeCriminal();
        var mem = ch.GetEquippedItem(Layer.FlagCriminal)!;
        mem.SetTimeout(Environment.TickCount64 - 1);

        mem.OnTick();

        Assert.True(mem.IsDeleted);
        Assert.False(ch.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void MurderMemory_AgesOneMurderOffOnItsOwnTimer()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.Kills = 2;
        ch.CombatState.NotoMurder();
        var mem = ch.GetEquippedItem(Layer.FlagMurders)!;
        mem.SetTimeout(Environment.TickCount64 - 1);

        mem.OnTick();
        Assert.Equal(1, ch.Kills);
        Assert.False(mem.IsDeleted);
        Assert.InRange(mem.Timeout - Environment.TickCount64, (DecaySeconds - 5) * 1000L, DecaySeconds * 1000L);

        mem.SetTimeout(Environment.TickCount64 - 1);
        mem.OnTick();
        Assert.Equal(0, ch.Kills);
        Assert.True(mem.IsDeleted); // the last murder takes the memory with it
    }

    [Fact]
    public void MurderClock_StopsWhileOffline_AndResumesFromTheBalance()
    {
        // CClient::Announce (CClient.cpp:388-401).
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.Kills = 1;
        ch.CombatState.NotoMurder();
        var mem = ch.GetEquippedItem(Layer.FlagMurders)!;
        mem.SetTimeout(Environment.TickCount64 + 600_000);

        ch.CombatState.OnClientAnnounce(arrive: false);
        Assert.Equal(-1, mem.Timeout);
        Assert.InRange((int)mem.More1, 595, 600);
        Assert.InRange(ch.MurderDecayRemainingSeconds, 595, 600);

        ch.CombatState.OnClientAnnounce(arrive: true);
        Assert.InRange(mem.Timeout - Environment.TickCount64, 590_000L, 600_000L);
    }

    [Fact]
    public void EnteringTheWorldWithMurdersButNoMemory_RunsNotoMurder()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.Kills = 3;

        ch.CombatState.OnClientAnnounce(arrive: true);

        Assert.NotNull(ch.GetEquippedItem(Layer.FlagMurders));
        Assert.InRange(ch.MurderDecayRemainingSeconds, DecaySeconds - 5, DecaySeconds);
    }

    // ---- Death: UnEquipAllItems' flag layers (CCharAct.cpp:613-621) ----

    private static Item WearFlagItem(GameWorld world, Character ch, Layer layer)
    {
        var mem = world.CreateItem();
        mem.BaseId = 0x2053;
        mem.ItemType = ItemType.EqMemoryObj;
        mem.SetAttr(ObjAttributes.Newbie | ObjAttributes.Move_Never);
        Assert.True(ch.Equip(mem, layer));
        return mem;
    }

    [Fact]
    public void Death_DeletesTheTransientFlagLayers_ButKeepsCriminalAndMurders()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.Kills = 2;
        ch.CombatState.NotoMurder();
        ch.MakeCriminal();
        var potionUsed = WearFlagItem(world, ch, Layer.FlagPotionUsed);
        var drunk = ch.MemoryState.CreateSpellEffect((int)SpellType.Ale, 0x2053, 10, Serial.Invalid,
            "Drunk Effect", SphereNet.Game.Magic.SpellLayers.FlagDrunk, world);
        var murders = ch.GetEquippedItem(Layer.FlagMurders)!;
        var criminal = ch.GetEquippedItem(Layer.FlagCriminal)!;

        var corpse = new DeathEngine(world).ProcessDeath(ch);

        Assert.NotNull(corpse);
        Assert.True(potionUsed.IsDeleted);
        Assert.True(drunk.IsDeleted);
        Assert.False(murders.IsDeleted);   // not a visible layer: it stays on the ghost
        Assert.False(criminal.IsDeleted);
        Assert.Same(murders, ch.GetEquippedItem(Layer.FlagMurders));
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void Death_WithNoLootDrop_LeavesTheFlagLayers()
    {
        // UnEquipAllItems is reached through MakeCorpse's DropAll, which
        // DEATH_NOLOOTDROP skips (CItemCorpse.cpp:218-219).
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.SetTag("DEATHFLAGS", "0x04");
        var potionUsed = WearFlagItem(world, ch, Layer.FlagPotionUsed);

        new DeathEngine(world).ProcessDeath(ch);

        Assert.False(potionUsed.IsDeleted);
    }

    // ---- Persistence: SphereNet round trip, a Source-X record, an older SphereNet save ----

    private readonly List<string> _temp = [];

    private string TempPath(string name)
    {
        string p = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        _temp.Add(p);
        return p;
    }

    private static GameWorld LoadText(string text, string file)
    {
        File.WriteAllText(file, text);
        var w = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        // A Source-X player record is a player through its ACCOUNT= line.
        var accounts = new SphereNet.Game.Accounts.AccountManager(lf);
        accounts.CreateAccount("redacct", "pw");
        new SphereNet.Persistence.Load.WorldLoader(lf).LoadFile(w, file, accounts);
        return w;
    }

    [Fact]
    public void NotorietyMemories_RoundTripThroughWorldSaverAndLoader()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);
        ch.Kills = 3;
        ch.CombatState.NotoMurder();
        ch.MakeCriminal();
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));

        string dir = TempPath("noto-memories");
        Directory.CreateDirectory(dir);
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var saver = new SphereNet.Persistence.Save.WorldSaver(lf)
            { Format = SphereNet.Core.Configuration.SaveFormat.Text, ShardCount = 0 };
        try
        {
            Assert.True(saver.Save(world, dir));
            string all = string.Join("\n", Directory.GetFiles(dir, "*.scp", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert.Contains("LAYER=52", all);
            Assert.Contains("LAYER=43", all);
            Assert.DoesNotContain("MURDERDECAY=", all);
            Assert.DoesNotContain("CRIMINALTIMER=", all);

            var next = TestHarness.CreateWorld();
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => next;
            Item.ResolveWorld = () => next;
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(next, dir);
            var back = next.FindChar(ch.Uid)!;

            Assert.Equal(3, back.Kills);
            var murders = back.GetEquippedItem(Layer.FlagMurders);
            Assert.NotNull(murders);
            Assert.Equal(ItemType.Spell, murders!.ItemType);
            Assert.False(murders.IsSpellMemory);
            Assert.InRange(back.MurderDecayRemainingSeconds, DecaySeconds - 30, DecaySeconds);
            Assert.NotNull(back.GetEquippedItem(Layer.FlagCriminal));
            Assert.True(back.IsStatFlag(StatFlag.Criminal));
            Assert.InRange(back.CriminalTimerRemainingSeconds, Character.CriminalTimerSeconds - 30,
                Character.CriminalTimerSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SourceXRecord_LoadsIntoWorkingNotorietyClocks()
    {
        // As Source-X writes them: the murder memory of a player who was offline at
        // save time keeps its balance in MORE1 with no timer; the criminal memory
        // carries its timer.
        string file = TempPath("sourcex-noto") + ".scp";
        var w = LoadText(
            "[WORLDCHAR c_man]\nSERIAL=01\nNAME=Red\nBODY=0190\nP=100,100,0,0\nACCOUNT=redacct\nKILLS=7\n" +
            "[WORLDITEM i_rhand_point_nw]\nSERIAL=040000010\nID=02053\nNAME=Murder Decay\nTYPE=t_spell\n" +
            "ATTR=04\nMORE1=0258\nMORE2=1\nCONT=01\nLAYER=52\n" +
            "[WORLDITEM i_rhand_point_nw]\nSERIAL=040000011\nID=02053\nNAME=Criminal Timer\nTYPE=t_spell\n" +
            "ATTR=04\nMORE2=1\nCONT=01\nLAYER=43\nTIMERMS=90000\n", file);
        var ch = w.FindChar(new Serial(1))!;
        Assert.True(ch.IsPlayer);

        Assert.Equal(7, ch.Kills);
        var murders = ch.GetEquippedItem(Layer.FlagMurders);
        Assert.NotNull(murders);
        Assert.Equal(600, ch.MurderDecayRemainingSeconds); // the stopped clock's balance
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));
        Assert.True(ch.IsCriminal);
        Assert.InRange(ch.CriminalTimerRemainingSeconds, 80, 90);

        ch.CombatState.OnClientAnnounce(arrive: true);
        Assert.InRange(murders!.Timeout - Environment.TickCount64, 590_000L, 600_000L);
        murders.SetTimeout(Environment.TickCount64 - 1);
        murders.OnTick();
        Assert.Equal(6, ch.Kills);
    }

    [Fact]
    public void SourceXRecord_MurderMemoryWithoutMurders_AndCriminalFlagWithoutMemory_AreRepaired()
    {
        string file = TempPath("sourcex-noto-fix") + ".scp";
        var w = LoadText(
            $"[WORLDCHAR c_man]\nSERIAL=01\nNAME=Clean\nBODY=0190\nP=100,100,0,0\nFLAGS=0{(uint)StatFlag.Criminal:x}\n" +
            "[WORLDITEM i_rhand_point_nw]\nSERIAL=040000010\nID=02053\nNAME=Murder Decay\nTYPE=t_spell\n" +
            "MORE1=0258\nMORE2=1\nCONT=01\nLAYER=52\n", file);
        var ch = w.FindChar(new Serial(1))!;

        Assert.Null(ch.GetEquippedItem(Layer.FlagMurders));   // CItem::FixWeirdness 0x2235
        Assert.False(ch.IsStatFlag(StatFlag.Criminal));        // CChar::FixWeirdness
    }

    [Fact]
    public void OlderSphereNetSave_TimerFields_StillLoad()
    {
        string file = TempPath("spherenet-legacy-noto") + ".scp";
        var w = LoadText(
            "[WORLDCHAR 0190]\nSERIAL=01\nNAME=Old\nP=100,100,0,0\nISPLAYER=1\nKILLS=2\nCRIMINALTIMER=120\nMURDERDECAY=300\n",
            file);
        var ch = w.FindChar(new Serial(1))!;
        Assert.True(ch.IsPlayer);

        Assert.Equal(2, ch.Kills);
        Assert.NotNull(ch.GetEquippedItem(Layer.FlagCriminal));
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));
        Assert.InRange(ch.CriminalTimerRemainingSeconds, 110, 120);
        Assert.NotNull(ch.GetEquippedItem(Layer.FlagMurders));
        Assert.Equal(300, ch.MurderDecayRemainingSeconds); // offline: balance kept, clock stopped
    }
}
