using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

// Town roles, perception and the look triggers measured against Source-X
// CCharNPCAct.cpp (NPC_LookAtChar* / NPC_LookAtItem / NPC_Act_Looting / NPC_Food),
// CCharNotoriety.cpp and CCharNPCAct_Vendor.cpp.
[Collection("DefinitionLoaderSerial")]
public sealed class NpcTownRolesParityTests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static GameWorld CreateWorld() => TestHarness.CreateWorld();

    private static Region AddGuarded(GameWorld world, int x0, int y0, int x1, int y1)
    {
        var region = new Region { Name = "town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect((short)x0, (short)y0, (short)x1, (short)y1);
        world.AddRegion(region);
        return region;
    }

    private static Character Npc(GameWorld world, NpcBrainType brain, int x, int y, short karma = 0)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = brain;
        npc.Karma = karma;
        npc.Str = 100;
        npc.Hits = npc.MaxHits = 100;
        npc.Int = 100;
        world.PlaceCharacter(npc, new Point3D((short)x, (short)y, 0, 0));
        return npc;
    }

    private static Character Player(GameWorld world, int x, int y)
    {
        var p = world.CreateCharacter();
        p.IsPlayer = true;
        p.Str = 10;
        p.Hits = p.MaxHits = 100;
        p.Karma = 1000;
        world.PlaceCharacter(p, new Point3D((short)x, (short)y, 0, 0));
        return p;
    }

    // ---- 1. Noto_IsEvil / evil brains acting as monsters ----

    [Fact]
    public void NotoIsEvil_MonsterNeedsNegativeKarma()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var monster = Npc(world, NpcBrainType.Monster, 100, 100, karma: 0);
        Assert.False(ai.NotoIsEvil(monster)); // CCharNotoriety.cpp:53: karma < 0
        monster.Karma = -1;
        Assert.True(ai.NotoIsEvil(monster));

        var wolf = Npc(world, NpcBrainType.Animal, 102, 100, karma: -799);
        Assert.False(ai.NotoIsEvil(wolf));
        wolf.Karma = -800;
        Assert.True(ai.NotoIsEvil(wolf)); // :57

        var brigand = Npc(world, NpcBrainType.Human, 104, 100, karma: -2999);
        Assert.False(ai.NotoIsEvil(brigand));
        brigand.Karma = -3000;
        Assert.True(ai.NotoIsEvil(brigand)); // :65
    }

    [Theory]
    [InlineData(NpcBrainType.Animal, -900)]
    [InlineData(NpcBrainType.Human, -5000)]
    public void EvilAnimalsAndHumans_HuntPlayersLikeMonsters(NpcBrainType brain, int karma)
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = Npc(world, brain, 100, 100, (short)karma);
        var player = Player(world, 102, 100);

        Invoke(ai, brain == NpcBrainType.Animal ? "ActAnimal" : "ActHuman", npc);

        Assert.Equal(player.Uid, npc.FightTarget);
    }

    [Fact]
    public void NonEvilAnimal_DoesNotHunt()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var deer = Npc(world, NpcBrainType.Animal, 100, 100, karma: 0);
        Player(world, 102, 100);

        for (int i = 0; i < 20; i++)
            Invoke(ai, "ActAnimal", deer);

        Assert.False(deer.FightTarget.IsValid);
    }

    [Fact]
    public void FedMonsterThatIsNotEvil_LooksLikeATownsman()
    {
        // NPC_LookAtCharMonster (CCharNPCAct.cpp:773): not criminal, food above 40%.
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var monster = Npc(world, NpcBrainType.Monster, 100, 100, karma: 0);
        monster.Food = monster.MaxFood;
        Player(world, 102, 100);

        Invoke(ai, "ActMonster", monster);

        Assert.False(monster.FightTarget.IsValid);
    }

    // ---- 2. Guards ----

    [Fact]
    public void Guard_StrikesACriminalOnGuardedGround()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var cfg = new SphereConfig { GuardsInstantKill = false };
        var ai = new NpcAI(world, cfg);
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        var guard = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        var crim = Player(world, 101, 100);
        crim.SetStatFlag(StatFlag.Criminal);

        Assert.True(ai.GuardLookAtChar(guard, crim, fromTrigger: false));

        Assert.Equal(crim.Uid, guard.FightTarget);
        Assert.True(guard.IsStatFlag(StatFlag.War));
        Assert.Single(said);
        Assert.StartsWith("npc_guard_strike", KeyOf(said[0], "npc_guard_strike_", 5));
        // No lightning, no forced death: the target is alive and whole.
        Assert.False(crim.IsDead);
        Assert.Equal(100, crim.Hits);
    }

    [Fact]
    public void Guard_AttacksAnEvilMonsterInTown()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig { GuardsInstantKill = false });
        var guard = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        var orc = Npc(world, NpcBrainType.Monster, 101, 100, karma: -500);

        Assert.True(ai.GuardLookAtChar(guard, orc, fromTrigger: false));
        Assert.Equal(orc.Uid, guard.FightTarget);
    }

    [Fact]
    public void Guard_IgnoresInnocents()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var guard = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        var player = Player(world, 101, 100);

        Assert.False(ai.GuardLookAtChar(guard, player, fromTrigger: false));
        Assert.False(guard.FightTarget.IsValid);
    }

    [Fact]
    public void Guard_OnlyJeersOutsideGuardedGround_OneTimeInTen()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 99, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        var guard = Npc(world, NpcBrainType.Guard, 99, 100, karma: 5000);
        var crim = Player(world, 105, 100);
        crim.SetStatFlag(StatFlag.Criminal);

        for (int i = 0; i < 400; i++)
            Assert.False(ai.GuardLookAtChar(guard, crim, fromTrigger: false));

        Assert.False(guard.FightTarget.IsValid);
        Assert.InRange(said.Count, 15, 80); // ~40 expected (1 in 10)
        var threats = Enumerable.Range(1, 5)
            .Select(i => ServerMessages.GetFormatted($"npc_guard_threat_{i}", crim.GetName()))
            .ToHashSet();
        Assert.All(said, s => Assert.Contains(s, threats));
    }

    [Fact]
    public void Guard_InstantKill_TeleportsAndLeavesTheTargetAtOneHit()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig { GuardsInstantKill = true });
        var guard = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        var crim = Player(world, 106, 100);
        crim.SetStatFlag(StatFlag.Criminal);

        ai.GuardLookAtChar(guard, crim, fromTrigger: false);

        Assert.Equal(crim.Position, guard.Position);
        Assert.Equal(1, crim.Hits);
        Assert.Equal(crim.Uid, guard.FightTarget);
    }

    [Fact]
    public void Guard_WithMagery_TeleportsEvenWithoutInstantKill()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig { GuardsInstantKill = false });
        var mage = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        mage.SetSkill(SkillType.Magery, 500);
        var plain = Npc(world, NpcBrainType.Guard, 100, 110, karma: 5000);
        var crim = Player(world, 106, 100);
        crim.SetStatFlag(StatFlag.Criminal);
        var crim2 = Player(world, 106, 110);
        crim2.SetStatFlag(StatFlag.Criminal);

        ai.GuardLookAtChar(mage, crim, fromTrigger: false);
        ai.GuardLookAtChar(plain, crim2, fromTrigger: false);

        Assert.Equal(crim.Position, mage.Position);
        Assert.Equal(new Point3D(100, 110, 0, 0), plain.Position);
        Assert.Equal(100, crim.Hits);
    }

    [Fact]
    public void Guard_PostOutsideGuardedGround_IsRemoved_NoPostStays()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());

        var postless = Npc(world, NpcBrainType.Guard, 400, 400, karma: 5000);
        Invoke(ai, "ActGuard", postless);
        Assert.False(postless.IsDeleted); // NPC_Act_Idle needs a valid home (:1979)

        var badPost = Npc(world, NpcBrainType.Guard, 400, 410, karma: 5000);
        badPost.Home = new Point3D(300, 300, 0, 0); // not guarded
        Invoke(ai, "ActGuard", badPost);
        Assert.True(badPost.IsDeleted); // NPC_Act_GoHome :1524-1535
    }

    [Fact]
    public void Guard_DetectHidden_IsAnExtra_AndKeepsNoTag()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var guard = Npc(world, NpcBrainType.Guard, 100, 100, karma: 5000);
        guard.SetSkill(SkillType.DetectingHidden, 1000);
        var hider = Player(world, 102, 100);
        hider.SetStatFlag(StatFlag.Hidden);

        Invoke(ai, "ActGuard", guard);
        Assert.True(hider.IsStatFlag(StatFlag.Hidden));

        ai.Extras = NpcAiExtraFlags.DetectHidden;
        for (int i = 0; i < 50 && hider.IsStatFlag(StatFlag.Hidden); i++)
        {
            typeof(NpcAI).GetField("_nextDetectHidden", BindingFlags.Instance | BindingFlags.NonPublic)!
                .FieldType.GetMethod("Clear")!
                .Invoke(typeof(NpcAI).GetField("_nextDetectHidden", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai), null);
            Invoke(ai, "ActGuard", guard);
        }
        Assert.False(hider.IsStatFlag(StatFlag.Hidden));
        Assert.False(guard.TryGetTag("NEXT_DETECT", out _));
    }

    // ---- 3. Healer ----

    [Fact]
    public void Healer_ResurrectsAGhost_WhateverItsWarMode()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        int resurrects = 0;
        ai.OnHealerAction = (_, ghost, res) => { if (res) { resurrects++; ghost.Resurrect(); } };
        var healer = Npc(world, NpcBrainType.Healer, 100, 100, karma: 0);
        var ghost = Player(world, 102, 100);
        ghost.Kill();
        Assert.True(ghost.IsDead);
        Assert.False(ghost.IsInWarMode);

        Assert.True((bool)Invoke(ai, "HealerLookAtChar", healer, ghost)!);

        Assert.Equal(1, resurrects);
        Assert.Single(said);
        Assert.Contains(said[0], Enumerable.Range(1, 5).Select(i => ServerMessages.Get($"npc_healer_res_{i}")));
    }

    [Fact]
    public void Healer_TooFar_CallsTheGhostCloser_OneTimeInFive()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        int resurrects = 0;
        ai.OnHealerAction = (_, _, _) => resurrects++;
        var healer = Npc(world, NpcBrainType.Healer, 100, 100);
        var ghost = Player(world, 105, 100);
        ghost.Kill();

        for (int i = 0; i < 200; i++)
            Invoke(ai, "HealerLookAtChar", healer, ghost);

        Assert.Equal(0, resurrects);
        Assert.InRange(said.Count, 15, 80);
        Assert.All(said, s => Assert.Equal(ServerMessages.Get(Msg.NpcHealerRange), s));
    }

    [Fact]
    public void GoodHealer_RefusesACriminalGhost()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        int resurrects = 0;
        ai.OnHealerAction = (_, _, _) => resurrects++;
        var healer = Npc(world, NpcBrainType.Healer, 100, 100, karma: 5000);
        var ghost = Player(world, 101, 100);
        ghost.SetStatFlag(StatFlag.Criminal);
        ghost.Kill();

        for (int i = 0; i < 100; i++)
            Invoke(ai, "HealerLookAtChar", healer, ghost);

        Assert.Equal(0, resurrects);
        var crim = Enumerable.Range(1, 3).Select(i => ServerMessages.Get($"npc_healer_ref_crim_{i}")).ToHashSet();
        Assert.NotEmpty(said);
        Assert.All(said, s => Assert.Contains(s, crim));
    }

    [Fact]
    public void Healer_IgnoresABondedPetGhost()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int resurrects = 0;
        ai.OnHealerAction = (_, _, _) => resurrects++;
        var healer = Npc(world, NpcBrainType.Healer, 100, 100);
        var pet = Npc(world, NpcBrainType.Animal, 101, 100);
        pet.IsBonded = true;
        pet.Kill();

        Assert.False((bool)Invoke(ai, "HealerLookAtChar", healer, pet)!);
        Assert.Equal(0, resurrects);
    }

    // ---- 4. Witness ----

    [Fact]
    public void SpeakingWitness_CallsGuardsYellsAndFlees()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        int calls = 0;
        ai.OnWitnessCrime = (_, _) => { calls++; return true; };
        var witness = Npc(world, NpcBrainType.Human, 100, 100, karma: 100);
        witness.DSpeech.Add(new ResourceId(
            ResType.Speech, 1));
        var crim = Player(world, 103, 100);
        crim.SetStatFlag(StatFlag.Criminal);

        for (int i = 0; i < 60 && calls == 0; i++)
            Invoke(ai, "LookAroundTown", witness, false);

        Assert.Equal(1, calls);
        Assert.Equal(ServerMessages.Get(Msg.NpcGenericSeecrim), Assert.Single(said));
        Assert.Equal(NpcAction.Flee, (NpcAction)witness.Action);
        Assert.Equal(crim.Uid, witness.Act);
        Assert.Equal(20, witness.FleeStepsMax);
    }

    [Fact]
    public void Witness_WhoseCallFound_NoGuard_StaysSilentButStillFlees()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var said = new List<string>();
        ai.OnNpcSay = (_, t) => said.Add(t);
        ai.OnWitnessCrime = (_, _) => false;
        var witness = Npc(world, NpcBrainType.Vendor, 100, 100, karma: 100);
        witness.DSpeech.Add(new ResourceId(
            ResType.Speech, 1));
        var crim = Player(world, 103, 100);
        crim.Kills = 100; // a murderer is evil (Noto_IsEvil) - GUARDSONMURDERERS on

        for (int i = 0; i < 60 && witness.Action == SkillType.None; i++)
            Invoke(ai, "LookAroundTown", witness, false);

        Assert.Empty(said);
        Assert.Equal(NpcAction.Flee, (NpcAction)witness.Action);
    }

    // ---- 5. Restock ----

    [Theory]
    [InlineData(NpcBrainType.Healer)]
    [InlineData(NpcBrainType.Banker)]
    [InlineData(NpcBrainType.Vendor)]
    [InlineData(NpcBrainType.Stable)]
    public void EveryVendorBrain_Restocks_AfterEmptyingItsContainers(NpcBrainType brain)
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        int leftAtRestock = -1;
        var npc = Npc(world, brain, 100, 100, karma: 100);
        var stock = world.CreateItem();
        stock.BaseId = 0x408D;
        npc.Equip(stock, Layer.VendorStock);
        var old = world.CreateItem();
        old.BaseId = 0x0F0E;
        stock.TryAddItem(old);
        ai.OnVendorRestock = v =>
        {
            restocks++;
            leftAtRestock = world.GetContainerContents(stock.Uid).Count();
        };

        Invoke(ai, "TryVendorRestock", npc);

        Assert.Equal(1, restocks);
        Assert.Equal(0, leftAtRestock); // CCharNPCAct_Vendor.cpp:83-93
        Assert.True(old.IsDeleted || !old.ContainedIn.IsValid);
    }

    [Fact]
    public void PetVendor_IsNeverRestocked()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        int restocks = 0;
        ai.OnVendorRestock = _ => restocks++;
        var owner = Player(world, 99, 100);
        var npc = Npc(world, NpcBrainType.Vendor, 100, 100);
        npc.NpcMaster = owner.Uid;

        Invoke(ai, "TryVendorRestock", npc);
        Assert.Equal(0, restocks);
    }

    // ---- 6. Item pickup / looting ----

    [Fact]
    public void VendorWantingGold_NeitherPicksItUpNorGrowsABackpack()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var vendor = Npc(world, NpcBrainType.Vendor, 100, 100, karma: 100);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = 50;
        world.PlaceItem(gold, new Point3D(100, 100, 0, 0));

        Assert.Equal(100, ai.GetWantScore(vendor, gold)); // NPC_WantThisItem: vendors want gold
        for (int i = 0; i < 5; i++)
        {
            typeof(NpcAI).GetField("_nextItemScan", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ai, new Dictionary<uint, long>());
            Invoke(ai, "LookAtNearbyItems", vendor);
        }

        Assert.Null(vendor.Backpack);
        Assert.True(gold.IsOnGround);
    }

    [Fact]
    public void LootingMonster_TakesAPieceOfACorpse_OthersDoNot()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var looted = new List<(Item item, bool corpse)>();
        ai.OnNpcLooted = (_, it, c) => looted.Add((it, c));

        var orc = Npc(world, NpcBrainType.Monster, 100, 100, karma: -500);
        orc.SetTag("OVERRIDE.NPCAI", "0x0100"); // NPC_AI_LOOTING
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        orc.Equip(pack, Layer.Pack);

        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));
        var coin = world.CreateItem();
        coin.BaseId = 0x0EED;
        coin.ItemType = ItemType.Gold;
        corpse.TryAddItem(coin);

        Invoke(ai, "ActLooting", orc, corpse);

        Assert.Equal(pack.Uid, coin.ContainedIn);
        Assert.Single(looted);
        Assert.True(looted[0].corpse);

        // A looting HUMAN brain never loots (NPC_Act_Looting :1606).
        var human = Npc(world, NpcBrainType.Human, 102, 101, karma: 100);
        human.SetTag("OVERRIDE.NPCAI", "0x0100");
        var coin2 = world.CreateItem();
        coin2.BaseId = 0x0EED;
        coin2.ItemType = ItemType.Gold;
        corpse.TryAddItem(coin2);
        Invoke(ai, "ActLooting", human, corpse);
        Assert.Equal(corpse.Uid, coin2.ContainedIn);
    }

    [Fact]
    public void LootingMonster_InGuardedGround_DoesNothing()
    {
        var world = CreateWorld();
        AddGuarded(world, 50, 50, 150, 150);
        var ai = new NpcAI(world, new SphereConfig());
        var orc = Npc(world, NpcBrainType.Monster, 100, 100, karma: -500);
        orc.SetTag("OVERRIDE.NPCAI", "0x0100");
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        orc.Equip(pack, Layer.Pack);
        var loot = world.CreateItem();
        loot.BaseId = 0x0EED;
        world.PlaceItem(loot, new Point3D(101, 100, 0, 0));

        Invoke(ai, "ActLooting", orc, loot);
        Assert.True(loot.IsOnGround);
    }

    // ---- 7. Food ----

    [Fact]
    public void IntFood_HungryNpcEatsAdjacentFood_FedNpcDoesNot()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var sheep = Npc(world, NpcBrainType.Animal, 100, 100, karma: 0);
        sheep.SetTag("OVERRIDE.NPCAI", "0x0010"); // NPC_AI_INTFOOD
        var bread = world.CreateItem();
        bread.BaseId = 0x103B;
        bread.ItemType = ItemType.Food;
        bread.Amount = 3;
        world.PlaceItem(bread, new Point3D(101, 100, 0, 0));

        sheep.Food = 30; // 50% of 60: not hungry enough (:1770)
        Assert.False(ai.RunFoodAI(sheep));
        Assert.Equal(3, bread.Amount);

        sheep.Food = 5; // under 10 and at most 40%
        Assert.True(ai.RunFoodAI(sheep));
        Assert.Equal(2, bread.Amount);
        Assert.True(sheep.Food > 5);
    }

    [Fact]
    public void IntFoodTag_IsNotAFoodFlag()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig { NpcAi = 0 });
        var sheep = Npc(world, NpcBrainType.Animal, 100, 100);
        sheep.SetTag("INTFOOD", "1");
        sheep.Food = 0;
        var bread = world.CreateItem();
        bread.ItemType = ItemType.Food;
        bread.Amount = 3;
        world.PlaceItem(bread, new Point3D(101, 100, 0, 0));

        Assert.False(ai.RunFoodAI(sheep));
        Assert.Equal(3, bread.Amount);
    }

    [Fact]
    public void PlainFood_HungryNpcWalksToDistantFood()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var cow = Npc(world, NpcBrainType.Animal, 100, 100);
        cow.SetTag("OVERRIDE.NPCAI", "0x0002"); // NPC_AI_FOOD
        cow.Food = 0;
        var apple = world.CreateItem();
        apple.ItemType = ItemType.Food;
        world.PlaceItem(apple, new Point3D(104, 100, 0, 0));

        Assert.False(ai.RunFoodAI(cow)); // plain FOOD never takes the tick
        Assert.Equal(NpcAction.GoTo, (NpcAction)cow.Action);
        Assert.Equal(apple.Position, cow.ActP);
    }

    // ---- 8. Animal backoff is an extra ----

    [Fact]
    public void AnimalBackoff_OnlyWithTheExtra()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var deer = Npc(world, NpcBrainType.Animal, 100, 100);
        deer.SetTag("OVERRIDE.NPCAIEXT", "0x0100");
        var warrior = Player(world, 102, 100);
        warrior.SetStatFlag(StatFlag.War);

        Invoke(ai, "ActAnimal", deer);
        Assert.True(deer.Position.GetDistanceTo(warrior.Position) > 2);
    }

    // ---- 9. FIGHTMODE ----

    [Fact]
    public void FightModeNone_NeverPicksATarget()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var orc = Npc(world, NpcBrainType.Monster, 100, 100, karma: -5000);
        orc.SetTag("FIGHTMODE", "None");
        Player(world, 102, 100);

        Invoke(ai, "ActMonster", orc);
        Assert.False(orc.FightTarget.IsValid);
    }

    [Fact]
    public void FightModeAggressor_OnlyPicksWhoeverHarmedIt()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var orc = Npc(world, NpcBrainType.Monster, 100, 100, karma: -5000);
        orc.SetTag("FIGHTMODE", "aggressor");
        var near = Player(world, 101, 100);
        var harmer = Player(world, 104, 100);
        orc.Memory_AddObjTypes(harmer.Uid, MemoryType.HarmedBy);

        var (target, _) = ((Character?, int))Invoke(ai, "FindBestTarget", orc, 18)!;
        Assert.Equal(harmer, target);
        Assert.NotEqual(near, target);
    }

    // ---- 10. Look triggers ----

    [Fact]
    public void LookAtChar_Return1_EndsTheLook_Return0_SkipsTheCharacter()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var orc = Npc(world, NpcBrainType.Monster, 100, 100, karma: -5000);
        var a = Player(world, 101, 100);
        var b = Player(world, 103, 100);

        int calls = 0;
        ai.OnNpcLookAtChar = (_, _) => { calls++; return TriggerResult.True; };
        var (t1, _) = ((Character?, int))Invoke(ai, "FindBestTarget", orc, 18)!;
        Assert.Null(t1);
        Assert.Equal(1, calls); // the look-around ended at the first answer

        ai.OnNpcLookAtChar = (_, ch) => ch == a ? TriggerResult.False : TriggerResult.Default;
        var (t2, _) = ((Character?, int))Invoke(ai, "FindBestTarget", orc, 18)!;
        Assert.Equal(b, t2);
    }

    [Fact]
    public void SeeNewPlayer_FiresOnlyForAnIdleNpc()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = Npc(world, NpcBrainType.Human, 100, 100, karma: 100);
        var player = Player(world, 102, 100);
        int fires = 0;
        Character.OnNpcSeeNewPlayer = (_, src) => { if (src == player) fires++; return false; };

        npc.SetStatFlag(StatFlag.War);
        for (int i = 0; i < 40; i++)
            Invoke(ai, "LookForNewPlayers", npc);
        Assert.Equal(0, fires);

        npc.ClearStatFlag(StatFlag.War);
        for (int i = 0; i < 80 && fires == 0; i++)
            Invoke(ai, "LookForNewPlayers", npc);
        Assert.Equal(1, fires);
        Assert.NotNull(npc.Memory_FindObjTypes(player.Uid, MemoryType.Speak));
    }

    /// <summary>Which of the numbered DEFMSG keys a spoken line came from.</summary>
    private static string KeyOf(string line, string prefix, int count)
    {
        for (int i = 1; i <= count; i++)
            if (ServerMessages.Get(prefix + i) == line)
                return prefix + i;
        return line;
    }
}
