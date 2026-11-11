using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC fight flow, guards, pets, hirelings and region crossings against the
/// Source-X reference: every NPC engagement is Fight_Attack (CCharFight.cpp:1399),
/// the fight target is re-read off the attacker list when a due swing cannot be
/// taken (:1614) or its row leaves the list (CCharAttacker.cpp:339), a guarded
/// area that is also SAFE is not guarded (CRegion.cpp:713), and the region
/// banners / login step follow CChar::MoveToRegion (CCharAct.cpp:5139-5221).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcRegionParityGapTests
{
    private static object? Invoke(NpcAI ai, string method, params object?[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static Character Npc(GameWorld world, short x, short y, NpcBrainType brain = NpcBrainType.Monster)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = brain;
        npc.Str = 50;
        npc.Hits = npc.MaxHits = 100;
        npc.Stam = npc.MaxStam = 100;
        world.PlaceCharacter(npc, new Point3D(x, y, 0, 0));
        return npc;
    }

    private static Character Player(GameWorld world, short x, short y)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = true; // a logged-out player is never attackable
        ch.Str = 50;
        ch.Hits = ch.MaxHits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static (GameWorld World, NpcAI Ai) Bench()
    {
        var world = TestHarness.CreateWorld();
        return (world, new NpcAI(world, new SphereConfig()));
    }

    private static Region Area(GameWorld world, string name, RegionFlag flags, short x1, short y1, short x2, short y2)
    {
        var region = new Region { Name = name, Flags = flags, MapIndex = 0 };
        region.AddRect(x1, y1, x2, y2);
        world.AddRegion(region);
        return region;
    }

    // ---- 1: an AI fight is Fight_Attack ----

    [Fact]
    public void AnAiFight_FiresAttack_ListsTheTarget_AndGoesToWar()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var foe = Player(world, 103, 100);
        int attacks = 0, starts = 0;
        Character.OnAttackTrigger = (_, _, _) => { attacks++; return true; };
        Character.OnNpcCombatStart = (_, _) => { starts++; return true; };

        Assert.Same(foe, ai.NpcFightAttack(npc, foe));

        Assert.Equal(1, attacks);
        Assert.Equal(1, starts); // the first row on the list is @CombatStart
        Assert.Equal(foe.Uid, npc.FightTarget);
        Assert.True(npc.IsStatFlag(StatFlag.War));
        Assert.Contains(npc.Attackers, a => a.Uid == foe.Uid);
        Assert.NotNull(npc.Memory_FindObjTypes(foe.Uid, MemoryType.Fight));
        Assert.NotNull(foe.Memory_FindObjTypes(npc.Uid, MemoryType.IrritatedBy));

        // Re-confirming the same target is an acknowledgement, not a new attack.
        Assert.Same(foe, ai.NpcFightAttack(npc, foe));
        Assert.Equal(1, attacks);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void AttackReturn1_CancelsTheFight_FromTheLookAroundAndFromSelfDefence()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var foe = Player(world, 101, 100);
        Character.OnAttackTrigger = (_, _, _) => false; // RETURN 1
        Character.NpcFightAttackHook = (n, t) => ai.NpcFightAttack(n, t) != null;

        Assert.Null(ai.NpcFightAttack(npc, foe));
        npc.OnHarmedBy(foe);

        Assert.False(npc.FightTarget.IsValid);
        Assert.False(npc.IsStatFlag(StatFlag.War));
        Assert.DoesNotContain(npc.Attackers, a => a.Uid == foe.Uid);
    }

    [Fact]
    public void SelfDefence_IsAnOrdinaryEngagement()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100, NpcBrainType.Animal);
        var foe = Player(world, 101, 100);
        Character.NpcFightAttackHook = (n, t) => ai.NpcFightAttack(n, t) != null;

        npc.OnHarmedBy(foe);

        Assert.Equal(foe.Uid, npc.FightTarget);
        Assert.True(npc.IsStatFlag(StatFlag.War));
        Assert.Contains(npc.Attackers, a => a.Uid == foe.Uid);
    }

    [Fact]
    public void ADeadOrInvulnerableTargetIsNotAttacked()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var foe = Player(world, 101, 100);
        foe.SetStatFlag(StatFlag.Invul);

        Assert.Null(ai.NpcFightAttack(npc, foe));
        Assert.False(npc.FightTarget.IsValid);
        Assert.Empty(npc.Attackers);
    }

    [Fact]
    public void WarModeEndsWithTheFight_ButAFlagAScriptSetIsLeftAlone()
    {
        var (world, ai) = Bench();
        var fighter = Npc(world, 100, 100);
        var foe = Player(world, 101, 100);
        Assert.NotNull(ai.NpcFightAttack(fighter, foe));

        ai.NpcFightEnd(fighter);
        Assert.False(fighter.FightTarget.IsValid);
        Assert.False(fighter.IsStatFlag(StatFlag.War));

        // The target taken away from outside the brain: the next tick drops war.
        Assert.NotNull(ai.NpcFightAttack(fighter, foe));
        fighter.FightTarget = Serial.Invalid;
        Invoke(ai, "DropWarAfterFight", fighter);
        Assert.False(fighter.IsStatFlag(StatFlag.War));

        var poser = Npc(world, 110, 100);
        poser.SetStatFlag(StatFlag.War); // FLAGS=statf_war in a script
        ai.NpcFightEnd(poser);
        Invoke(ai, "DropWarAfterFight", poser);
        Assert.True(poser.IsStatFlag(StatFlag.War));
    }

    // ---- 2: the target is re-read when a due swing cannot be taken ----

    [Fact]
    public void ADueSwingThatCannotLand_TurnsTheNpcOnTheNearerAttacker()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var far = Player(world, 106, 100);
        var near = Player(world, 101, 100);
        Assert.Same(far, ai.NpcFightAttack(npc, far));
        Assert.True(npc.CombatState.AddAttacker(near.Uid));
        npc.NextAttackTime = 0;

        Invoke(ai, "ActFight", npc, far, 50);

        Assert.Equal(near.Uid, npc.FightTarget);
    }

    [Fact]
    public void ATargetInReachIsKept_WhoeverElseIsOnTheList()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var other = Player(world, 100, 101);
        var current = Player(world, 101, 100);
        // `other` is the first row, which a re-pick would fall back on.
        Assert.True(npc.CombatState.AddAttacker(other.Uid));
        Assert.NotNull(ai.NpcFightAttack(npc, current, toldByMaster: true));
        npc.NextAttackTime = 0;

        Invoke(ai, "ActFight", npc, current, 50);

        Assert.Equal(current.Uid, npc.FightTarget);
    }

    [Fact]
    public void FightAttack_EndsOnTheBestOfTheList_UnlessTheTargetWasOrdered()
    {
        var (world, ai) = Bench();
        var npc = Npc(world, 100, 100);
        var near = Player(world, 101, 100);
        var far = Player(world, 108, 100);
        Assert.True(npc.CombatState.AddAttacker(near.Uid));

        // Not ordered: the newcomer goes on the list, the nearest is fought
        // (CCharFight.cpp:1479-1484).
        Assert.Same(near, ai.NpcFightAttack(npc, far));
        Assert.Contains(npc.Attackers, a => a.Uid == far.Uid);

        // Ordered: that one, no re-pick.
        Assert.Same(far, ai.NpcFightAttack(npc, far, toldByMaster: true));
        Assert.Equal(far.Uid, npc.FightTarget);
    }

    // ---- 6: a row leaving the attacker list redirects or ends the fight ----

    [Fact]
    public void RemovingTheFoughtRow_TurnsTheNpcOnTheNextOne_AndTheLastEndsTheFight()
    {
        var (world, ai) = Bench();
        Character.OnNpcFightRowRemoved = ai.OnFightTargetRowRemoved;
        var npc = Npc(world, 100, 100);
        var first = Player(world, 101, 100);
        var second = Player(world, 100, 101);
        Assert.Same(first, ai.NpcFightAttack(npc, first));
        Assert.True(npc.CombatState.AddAttacker(second.Uid));

        npc.Attacker_Delete(first.Uid);
        Assert.Equal(second.Uid, npc.FightTarget);
        Assert.True(npc.IsStatFlag(StatFlag.War));

        npc.Attacker_Delete(second.Uid);
        Assert.False(npc.FightTarget.IsValid);
        Assert.False(npc.IsStatFlag(StatFlag.War));
    }

    [Fact]
    public void AnAttackerRowThatTimesOut_EndsTheNpcFight()
    {
        var (world, ai) = Bench();
        Character.OnNpcFightRowRemoved = ai.OnFightTargetRowRemoved;
        int saved = Character.AttackerTimeoutSeconds;
        try
        {
            Character.AttackerTimeoutSeconds = 30;
            var npc = Npc(world, 100, 100);
            var foe = Player(world, 101, 100);
            Assert.NotNull(ai.NpcFightAttack(npc, foe));

            // Still inside the timeout: nothing happens.
            npc.CombatState.CheckAttackerTimeout(Environment.TickCount64 + 5_000);
            Assert.Equal(foe.Uid, npc.FightTarget);

            // The foe has done no damage for longer than ATTACKERTIMEOUT.
            Assert.True(npc.CombatState.SetAttackerElapsed(0, 31));
            npc.CombatState.CheckAttackerTimeout(Environment.TickCount64 + 10_000);

            Assert.Empty(npc.Attackers);
            Assert.False(npc.FightTarget.IsValid);
            Assert.False(npc.IsStatFlag(StatFlag.War));
        }
        finally { Character.AttackerTimeoutSeconds = saved; }
    }

    // ---- 7: an engaged guard teleports back onto a criminal that got away ----

    [Fact]
    public void AnEngagedGuardWhoseSwingCannotLand_TeleportsOntoTheCriminalAgain()
    {
        var (world, ai) = Bench();
        Area(world, "town", RegionFlag.Guarded, 0, 0, 1000, 1000);
        var guard = Npc(world, 100, 100, NpcBrainType.Guard);
        guard.Karma = 5000;
        var criminal = Player(world, 101, 100);
        criminal.SetStatFlag(StatFlag.Criminal);
        Assert.True(ai.GuardLookAtChar(guard, criminal, fromTrigger: false));
        Assert.Equal(criminal.Uid, guard.FightTarget);

        // The criminal runs; the guard's swing is ready and cannot be taken.
        world.MoveCharacter(criminal, new Point3D(110, 100, 0, 0));
        var teleports = new List<Point3D>();
        ai.OnNpcTeleport = (_, from) => teleports.Add(from);
        for (int i = 0; i < 200 && teleports.Count == 0; i++)
        {
            world.MoveCharacter(guard, new Point3D(100, 100, 0, 0));
            guard.ClearPendingHit();
            guard.NextAttackTime = 0;
            Invoke(ai, "GuardEngage", guard, criminal);
        }

        Assert.Single(teleports);
        Assert.Equal(new Point3D(100, 100, 0, 0), teleports[0]); // where it left
        Assert.Equal(criminal.Position, guard.Position);
        Assert.Equal(1, criminal.Hits); // GUARDSINSTANTKILL (CCharNPCAct.cpp:745-749)
    }

    [Fact]
    public void AGuardStartsItsFightThroughAttack_AndLeavesWarWhenTheTargetIsGone()
    {
        var (world, ai) = Bench();
        Area(world, "town", RegionFlag.Guarded, 0, 0, 1000, 1000);
        var guard = Npc(world, 100, 100, NpcBrainType.Guard);
        var criminal = Player(world, 101, 100);
        criminal.SetStatFlag(StatFlag.Criminal);
        Character.OnAttackTrigger = (_, _, _) => false;
        Assert.True(ai.GuardLookAtChar(guard, criminal, fromTrigger: false));
        Assert.False(guard.FightTarget.IsValid); // @Attack refused it

        Character.OnAttackTrigger = null;
        Assert.True(ai.GuardLookAtChar(guard, criminal, fromTrigger: false));
        Assert.True(guard.IsStatFlag(StatFlag.War));

        criminal.IsOnline = false; // gone: no longer attackable
        Invoke(ai, "ActGuard", guard);
        Assert.False(guard.FightTarget.IsValid);
        Assert.False(guard.IsStatFlag(StatFlag.War));
    }

    // ---- 13: a chase does not open doors ----

    [Fact]
    public void AnNpcInAFightDoesNotOpenTheDoorInItsWay()
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"npc_chase_door_{Guid.NewGuid():N}.scp");
        try
        {
            // CAN_C_USEHANDS: a creature that could open the door if it tried.
            File.WriteAllText(path, "[CHARDEF 0190]\nCAN=0304\n");
            runtime.Resources.LoadResourceFile(path);
            ScriptTestBootstrap.LoadDefinitions(runtime.Resources);
            var (world, ai) = Bench();
            ai.Flags = NpcAIFlags.None;
            var npc = Npc(world, 100, 100);
            npc.CharDefIndex = 0x190;
            npc.Dex = 100;
            var start = npc.Position;
            var foe = Player(world, 108, 100);
            for (short dx = -1; dx <= 1; dx++)
            for (short dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var door = world.CreateItem();
                door.BaseId = 0x06A5;
                door.ItemType = ItemType.Door;
                world.PlaceItem(door, new Point3D((short)(100 + dx), (short)(100 + dy), 0, 0));
            }
            int attempts = 0;
            ai.OnNpcOpenDoor = (_, _) => { attempts++; return false; };

            // Walking about on its own it tries the door (a 50% roll each time).
            for (int i = 0; i < 128; i++)
            {
                world.MoveCharacter(npc, start);
                Invoke(ai, "MoveToward", npc, foe.Position, false);
            }
            Assert.True(attempts > 0);

            // Chasing somebody it does not: NPC_WalkToPoint has no door handling.
            attempts = 0;
            Assert.NotNull(ai.NpcFightAttack(npc, foe));
            for (int i = 0; i < 128; i++)
            {
                world.MoveCharacter(npc, start);
                Invoke(ai, "MoveToward", npc, foe.Position, true);
            }
            Assert.Equal(0, attempts);
        }
        finally { File.Delete(path); }
    }

    // ---- 20: a ranged NPC fires from exactly its minimum distance ----

    [Fact]
    public void ARangedSwingIsHeldInsideTheMinimumDistance_NotAtIt()
    {
        var (world, ai) = Bench();
        var archer = Npc(world, 100, 100);
        var bow = world.CreateItem();
        bow.BaseId = 0x13B2;
        bow.ItemType = ItemType.WeaponBow;
        archer.Equip(bow, Layer.TwoHanded);
        int min = Math.Max(1, Character.ArcheryMinDist);
        var foe = Player(world, (short)(100 + min), 100);

        Assert.True((bool)Invoke(ai, "CanSwingAtNow", archer, foe, min, true)!);
        Assert.False((bool)Invoke(ai, "CanSwingAtNow", archer, foe, min - 1, true)!);
        Assert.False((bool)Invoke(ai, "CanSwingAtNow", archer, foe, min, false)!);
    }

    // ---- 21: a wand does not excuse the NPC's own skill / mana gate ----

    [Fact]
    public void AWandSpellStillNeedsTheCastersOwnSkillAndMana()
    {
        var (world, ai) = Bench();
        var caster = Npc(world, 100, 100);
        var foe = Player(world, 103, 100);
        ai.ResolveNpcSpellFlags = _ => SpellFlag.Harm;
        Character.OnCanCastCheck = (_, _) => false;

        object?[] wandArgs = [caster, foe, SpellType.MagicArrow, 50, false, true];
        Assert.False((bool)Invoke(ai, "NpcFightCastAccepts", wandArgs)!);

        Character.OnCanCastCheck = (_, _) => true;
        Assert.True((bool)Invoke(ai, "NpcFightCastAccepts", wandArgs)!);
    }

    // ---- 3: guarded and SAFE is not guarded ----

    [Fact]
    public void AGuardedAreaThatIsAlsoSafe_IsNotGuarded()
    {
        var town = new Region { Name = "town", Flags = RegionFlag.Guarded };
        var arena = new Region { Name = "arena", Flags = RegionFlag.Guarded | RegionFlag.Safe };

        Assert.True(town.IsGuarded);
        Assert.False(arena.IsGuarded);
        Assert.True(arena.IsFlag(RegionFlag.Guarded)); // the raw flag is still there
        arena.SetTag("RED", "1");
        Assert.False(arena.IsGuardedRed);
    }

    // ---- 8: the region-crossing banners ----

    [Fact]
    public void CrossingBanners_AreSaidOnlyForWhatChanged()
    {
        var wild = new Region { Name = "wild" };
        var town = new Region { Name = "town", Flags = RegionFlag.Guarded };
        town.SetTag("GUARDOWNER", "Britain");
        var market = new Region { Name = "market", Flags = RegionFlag.Guarded };
        var arena = new Region { Name = "arena", Flags = RegionFlag.NoPvP | RegionFlag.Safe };
        var den = new Region { Name = "den", Flags = RegionFlag.Guarded };
        den.SetTag("RED", "1");

        // In: the new area's owner. Out: the OLD area's owner.
        Assert.Equal([ServerMessages.GetFormatted(Msg.MsgRegionGuards1, "Britain")],
            Region.CrossingBanners(wild, town, moverIsDead: false));
        Assert.Equal([ServerMessages.GetFormatted(Msg.MsgRegionGuards2, "Britain")],
            Region.CrossingBanners(town, wild, moverIsDead: false));

        // Guarded to guarded: nothing.
        Assert.Empty(Region.CrossingBanners(town, market, moverIsDead: false));

        // PvP-safe and safe are announced both ways, and only on a change.
        Assert.Equal([ServerMessages.Get(Msg.MsgRegionPvpsafe), ServerMessages.Get(Msg.MsgRegionSafetyget)],
            Region.CrossingBanners(wild, arena, moverIsDead: false));
        Assert.Equal([ServerMessages.Get(Msg.MsgRegionPvpnot), ServerMessages.Get(Msg.MsgRegionSafetylose)],
            Region.CrossingBanners(arena, wild, moverIsDead: false));
        Assert.Empty(Region.CrossingBanners(arena, arena, moverIsDead: false));

        // Red entered / left.
        Assert.Contains(ServerMessages.GetFormatted(Msg.MsgRegionReddef, ServerMessages.Get(Msg.MsgRegionRedenter)),
            Region.CrossingBanners(town, den, moverIsDead: false));
        Assert.Contains(ServerMessages.GetFormatted(Msg.MsgRegionReddef, ServerMessages.Get(Msg.MsgRegionRedleft)),
            Region.CrossingBanners(den, town, moverIsDead: false));

        // No area left behind, or a ghost: nothing.
        Assert.Empty(Region.CrossingBanners(null, town, moverIsDead: false));
        Assert.Empty(Region.CrossingBanners(wild, town, moverIsDead: true));
    }

    [Fact]
    public void AnAnnouncingArea_SaysItsNameAndNothingElse()
    {
        var wild = new Region { Name = "wild" };
        var keep = new Region { Name = "the keep", Flags = RegionFlag.Announce | RegionFlag.Guarded | RegionFlag.NoPvP };

        Assert.Equal([ServerMessages.GetFormatted(Msg.MsgRegionEnter, "the keep")],
            Region.CrossingBanners(wild, keep, moverIsDead: false));
        keep.SetTag("ANNOUNCEMENT", "Lord British's keep");
        Assert.Equal([ServerMessages.GetFormatted(Msg.MsgRegionEnter, "Lord British's keep")],
            Region.CrossingBanners(null, keep, moverIsDead: true));
    }

    // ---- 9: login / logout take the region step, and the login banner ----

    [Fact]
    public void LoginBanner_NamesTheGuardsOfAGuardedArea()
    {
        var town = new Region { Name = "town", Flags = RegionFlag.Guarded };
        Assert.Equal(
            [ServerMessages.GetFormatted(Msg.MsgRegionGuardsp, ServerMessages.Get(Msg.MsgRegionGuardspt))],
            Region.LoginBanners(town));

        town.SetTag("GUARDOWNER", "Britain");
        town.SetTag("RED", "1");
        Assert.Equal(
            [ServerMessages.GetFormatted(Msg.MsgRegionGuardsp, "Britain"),
             ServerMessages.GetFormatted(Msg.MsgRegionReddef, ServerMessages.Get(Msg.MsgRegionRedenter))],
            Region.LoginBanners(town));

        Assert.Empty(Region.LoginBanners(null));
        Assert.Empty(Region.LoginBanners(new Region { Name = "wild" }));
        Assert.Empty(Region.LoginBanners(new Region { Name = "arena", Flags = RegionFlag.Guarded | RegionFlag.Safe }));
        Assert.Empty(Region.LoginBanners(new Region { Name = "keep", Flags = RegionFlag.Guarded | RegionFlag.Announce }));
    }

    [Fact]
    public void ComingOnlineEntersTheArea_GoingOfflineLeavesIt_Once()
    {
        var world = TestHarness.CreateWorld();
        var town = Area(world, "town", RegionFlag.Guarded, 50, 50, 200, 200);
        var steps = new List<(Region? Old, Region? New, bool AllowReject)>();
        world.OnRegionTransition = (_, oldArea, newArea, allowReject) =>
        {
            steps.Add((oldArea, newArea, allowReject));
            return true;
        };
        var player = Player(world, 100, 100);

        world.AddOnlinePlayer(player);
        world.AddOnlinePlayer(player); // already in the world: no second @Enter
        Assert.Equal([(null, town, false)], steps);

        steps.Clear();
        // A reconnect inside the client linger re-seats the character only.
        world.RemoveOnlinePlayer(player, regionStep: false);
        world.AddOnlinePlayer(player, regionStep: false);
        Assert.Empty(steps);

        world.RemoveOnlinePlayer(player);
        world.RemoveOnlinePlayer(player);
        Assert.Equal([(town, null, false)], steps);

        // Standing in no area: nothing to enter.
        steps.Clear();
        var hermit = Player(world, 900, 900);
        world.AddOnlinePlayer(hermit);
        world.RemoveOnlinePlayer(hermit);
        Assert.Empty(steps);
    }

    // ---- 14 / 19: the guard call gate and the @RegPeriodic sector key ----

    [Fact]
    public void APlayerMayCallGuardsFromAnywhere_UnlessDeadOrStone()
    {
        var world = TestHarness.CreateWorld();
        var caller = Player(world, 100, 100); // no region at all under it
        Assert.True(SphereNet.Server.Program.PlayerMayCallGuards(caller));

        caller.SetStatFlag(StatFlag.Stone);
        Assert.False(SphereNet.Server.Program.PlayerMayCallGuards(caller));
    }

    [Fact]
    public void RegPeriodicIsCountedPerSector_NotPerRegion()
    {
        const int size = SphereNet.Game.World.Sectors.Sector.SectorSize;
        uint here = SphereNet.Server.Program.RegPeriodicSectorKey(new Point3D(10, 10, 0, 0));

        // Same sector: one tick between them, whichever area each stands in.
        Assert.Equal(here, SphereNet.Server.Program.RegPeriodicSectorKey(new Point3D((short)(size - 1), 20, 0, 0)));
        // The next sector over, the next row, another map: each its own tick.
        Assert.NotEqual(here, SphereNet.Server.Program.RegPeriodicSectorKey(new Point3D(size, 10, 0, 0)));
        Assert.NotEqual(here, SphereNet.Server.Program.RegPeriodicSectorKey(new Point3D(10, size, 0, 0)));
        Assert.NotEqual(here, SphereNet.Server.Program.RegPeriodicSectorKey(new Point3D(10, 10, 0, 1)));
    }

    // ---- 5: a pet with no order guards its owner ----

    [Fact]
    public void ANewPetHasNoOrder_SoItGuardsItsOwner()
    {
        var world = TestHarness.CreateWorld();
        var owner = Player(world, 100, 100);
        var pet = Npc(world, 101, 100, NpcBrainType.Animal);
        pet.SetTag("FOLLOW_TARGET", "0123");
        pet.SetTag("GUARD_TARGET", "0456");
        Assert.Equal(PetAIMode.Follow, pet.PetAIMode); // the unowned default

        Assert.True(pet.TryAssignOwnership(owner, owner));

        Assert.Equal(PetAIMode.Guard, pet.PetAIMode);
        Assert.False(pet.TryGetTag("FOLLOW_TARGET", out _));
        Assert.False(pet.TryGetTag("GUARD_TARGET", out _)); // nobody named: the owner

        // Re-assigning the same owner (dismount, stable, figurine) resets nothing.
        pet.PetAIMode = PetAIMode.Stay;
        Assert.True(pet.TryAssignOwnership(owner, owner));
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Fact]
    public void AGuardingPetJoinsItsOwnersFightThroughAttack()
    {
        var (world, ai) = Bench();
        var owner = Player(world, 100, 100);
        var pet = Npc(world, 101, 100, NpcBrainType.Animal);
        var foe = Npc(world, 102, 100);
        Assert.True(pet.TryAssignOwnership(owner, owner));
        owner.FightTarget = foe.Uid;

        int attacks = 0;
        Character.OnAttackTrigger = (self, _, _) => { if (self == pet) attacks++; return true; };
        Invoke(ai, "ActPet", pet);

        Assert.Equal(1, attacks);
        Assert.Equal(foe.Uid, pet.FightTarget);
        Assert.True(pet.IsStatFlag(StatFlag.War));
    }

    // ---- 10: the hireling's wages are the bank-box purse ----

    [Fact]
    public void ASavedHireBalanceIsFoldedIntoThePurse_Once()
    {
        var world = TestHarness.CreateWorld();
        var hireling = Npc(world, 100, 100, NpcBrainType.Human);
        VendorEngine.SetVendorGold(hireling, 10);
        hireling.SetTag(VendorEngine.LegacyHireBalanceTag, "50");

        Assert.Equal(60, VendorEngine.GetVendorGold(hireling));
        Assert.False(hireling.TryGetTag(VendorEngine.LegacyHireBalanceTag, out _));
        Assert.Equal(60, VendorEngine.GetVendorGold(hireling)); // not counted twice
        Assert.Equal(60u, hireling.GetEquippedItem(Layer.BankBox)!.More1);
    }

    [Fact]
    public void AHirelingWhoseTimeIsUp_RemembersThatTheNextGoldIsHireMoney()
    {
        var world = TestHarness.CreateWorld();
        var owner = Player(world, 100, 100);
        var hireling = Npc(world, 101, 100, NpcBrainType.Human);
        Assert.True(hireling.TryAssignOwnership(owner, owner));
        hireling.SetTag("HIRE_WAGE", "100");
        hireling.SetTag("MAXFOOD", "60"); // without a food ceiling an NPC never hungers
        hireling.Food = 30;
        VendorEngine.SetVendorGold(hireling, 1); // does not cover one period
        Character.OnPetDesert = (_, _) => true;  // keep it, so the memory can be read

        hireling.SetNextFoodTick(1);
        hireling.TickPetOwnershipTimers(1_000_000);
        hireling.SetNextFoodTick(1);
        hireling.TickPetOwnershipTimers(2_000_000);

        var memory = hireling.Memory_FindObjTypes(owner.Uid, MemoryType.Speak);
        Assert.NotNull(memory);
        Assert.Equal(Character.NpcMemActSpeakHire, memory!.More1 & 0xFFFF);
    }

    // ---- 4: offering an item to an NPC costs it nothing ----

    [Fact]
    public void AnItemAnNpcRefuses_KeepsItsNewbieAndOwnedAttributes()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8961);
        var me = Player(world, 100, 100);
        me.Dex = me.Int = 100;
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        var keepsake = world.CreateItem();
        keepsake.BaseId = 0x1F03;
        keepsake.SetAttr(ObjAttributes.Newbie);
        keepsake.SetAttr(ObjAttributes.Owned);
        Assert.True(pack.TryAddItem(keepsake));
        var shopkeeper = Npc(world, 101, 100, NpcBrainType.Human);

        client.Inventory.HandleItemPickup(keepsake.Uid.Value, 1);
        client.Inventory.HandleItemDrop(keepsake.Uid.Value, -1, -1, 0, shopkeeper.Uid.Value);

        Assert.False(keepsake.IsDeleted);
        Assert.Same(me, keepsake.ResolveTopObject()); // it came back
        Assert.True(keepsake.IsAttr(ObjAttributes.Newbie));
        Assert.True(keepsake.IsAttr(ObjAttributes.Owned));
    }

    // ---- 11: a pet command needs no "all" and no name in front ----

    [Fact]
    public void ABarePetCommand_IsAnOrderToTheNpcThatHeardIt_WhenItIsMyPet()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8962);
        var me = Player(world, 100, 100);
        TestHarness.AttachCharacter(client, me);
        var pet = Npc(world, 101, 100, NpcBrainType.Animal);
        Assert.True(pet.TryAssignOwnership(me, me));
        var stranger = Npc(world, 102, 100, NpcBrainType.Animal);

        Assert.True(client.HearBarePetCommand(pet, "stay"));
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);

        // Somebody else's creature understands the word and does nothing about it;
        // the line is spent all the same (NPC_OnHearPetCmd returns true).
        Assert.True(client.HearBarePetCommand(stranger, "stay"));
        Assert.Equal(PetAIMode.Follow, stranger.PetAIMode);

        // Not a pet command: ordinary speech, left for the NPC's own ears.
        Assert.False(client.HearBarePetCommand(pet, "hello there"));
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    // ---- 18: a pet hears its orders as far as any NPC hears speech ----

    [Fact]
    public void NpcsHearAtNpcDistanceHear_FourteenTilesByDefault()
    {
        var world = TestHarness.CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = Player(world, 100, 100);
        var close = Npc(world, 114, 100, NpcBrainType.Animal);
        var far = Npc(world, 115, 100, NpcBrainType.Animal);

        Assert.Equal(14, speech.NpcHearRadius);
        Assert.True(speech.NpcHearsSpeaker(speaker, close));
        Assert.False(speech.NpcHearsSpeaker(speaker, far));

        speech.NpcDistanceHear = 5;
        Assert.Equal(5, speech.NpcHearRadius);
        Assert.False(speech.NpcHearsSpeaker(speaker, close));

        speech.NpcDistanceHear = -20; // this far, and never mind the line of sight
        Assert.Equal(20, speech.NpcHearRadius);
        Assert.True(speech.NpcHearsSpeaker(speaker, far));
    }
}
