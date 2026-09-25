using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Swing timing and combat sounds against Source-X.
///
/// Timing: Fight_SetDefaultSwingDelays / Fight_Hit (CCharFight.cpp:1655-1679,
/// :1923-1999) - the swing animation goes out, the blow lands a second later (the
/// swing animation delay), then the recoil; COMBAT_PREHIT lands it with the
/// animation. The engine used to resolve every swing the moment it started.
///
/// Sounds: CChar::SoundChar (CCharAct.cpp:2612-2808), CItem::Weapon_GetSoundHit/Miss
/// (CItem.cpp:4979-5011) and Fight_Hit's miss set (CCharFight.cpp:2057-2072).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SwingTimingAndSoundParityTests : IDisposable
{
    private readonly List<string> _scripts = new();
    private readonly Func<ushort, (int Min, int Max)?>? _savedLookup = CombatEngine.WeaponDefLookup;

    public void Dispose()
    {
        CombatEngine.WeaponDefLookup = _savedLookup;
        foreach (var path in _scripts)
            try { File.Delete(path); } catch { }
    }

    private string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_swing_snd_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        _scripts.Add(path);
        return path;
    }

    private static (GameClient Client, Character Attacker, Character Target, Item Sword, List<byte[]> Packets)
        MakeFight(int port, GameWorld world)
    {
        // Deterministic blow: fixed weapon damage, a GM attacker (a guaranteed hit)
        // and a target with no parry.
        CombatEngine.WeaponDefLookup = _ => (10, 10);
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.GM;
        attacker.Str = attacker.Dex = 100;
        attacker.Stam = attacker.MaxStam = 100;
        attacker.SetSkill(SkillType.Swordsmanship, 1000);
        attacker.SetSkill(SkillType.Tactics, 1000);
        attacker.SetStatFlag(StatFlag.War);
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, attacker);
        var packets = new List<byte[]>();
        client.BroadcastNearby = (_, _, packet, _) =>
        {
            var built = packet.Build();
            packets.Add(built.Span.ToArray());
        };

        var target = world.CreateCharacter();
        target.Str = 100;
        target.Hits = target.MaxHits = 100;
        target.SetSkill(SkillType.Parrying, 0);
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.BaseId = 0x0F5E;
        attacker.Equip(sword, Layer.OneHanded);

        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = 0;
        return (client, attacker, target, sword, packets);
    }

    private static bool IsSwingAnim(byte[] p, uint serial) =>
        p.Length == 14 && p[0] == 0x6E &&
        (((uint)p[1] << 24) | ((uint)p[2] << 16) | ((uint)p[3] << 8) | p[4]) == serial;

    private static IEnumerable<ushort> Sounds(IEnumerable<byte[]> packets) =>
        packets.Where(p => p.Length >= 12 && p[0] == 0x54).Select(p => (ushort)((p[2] << 8) | p[3]));

    // ---- timing ------------------------------------------------------------

    [Theory]
    [InlineData(0, 3000, 20, 10)]                                   // 1 s animation, rest recoil
    [InlineData((int)CombatFlags.PreHit, 3000, 30, 0)]              // animation folded into recoil
    [InlineData((int)CombatFlags.AnimHitSmooth, 3000, 0, 30)]       // no recoil, animation spans it
    [InlineData(0, 500, 0, 10)]                                     // floored at the 1 s animation
    public void DefaultSwingDelays_FollowFightSetDefaultSwingDelays(int flags, int speedMs, int recoil, int anim)
    {
        Character.CombatFlags = flags;
        var delays = CombatHelper.GetDefaultSwingDelays(speedMs);
        Assert.Equal(recoil, delays.RecoilTenths);
        Assert.Equal(anim, delays.AnimTenths);
    }

    [Fact]
    public void SwingCycle_ByDefault_HitsASecondAfterTheAnimation_AndKeepsTheAttackSpeed()
    {
        Character.CombatFlags = 0;
        var delays = CombatHelper.GetDefaultSwingDelays(3000);
        Assert.Equal(1000, CombatHelper.GetSwingHitDelayMs(delays));
        Assert.Equal(3000, CombatHelper.GetSwingCycleMs(delays));
        Assert.Equal(1, CombatHelper.GetSwingAnimDelay(delays));
        // The first swing waits only the recoil, so the first blow still lands a
        // full attack speed after the fight began.
        Assert.Equal(2000, CombatHelper.GetInitialSwingWaitMs(3000));

        Character.CombatFlags = (int)CombatFlags.AnimHitSmooth;
        var smooth = CombatHelper.GetDefaultSwingDelays(3500);
        Assert.Equal(3, CombatHelper.GetSwingAnimDelay(smooth));
        Assert.Equal(3000, CombatHelper.GetSwingHitDelayMs(smooth)); // whole seconds sent
        Assert.Equal(0, CombatHelper.GetInitialSwingWaitMs(3500));
    }

    [Fact]
    public void DefaultSwing_SendsTheAnimationFirst_AndLandsDamageOnlyAfterTheWindup()
    {
        Character.CombatFlags = 0;
        var world = TestHarness.CreateWorld();
        var (client, attacker, target, _, packets) = MakeFight(1901, world);

        long before = Environment.TickCount64;
        client.TickCombat();

        // The swing is seen now...
        Assert.Contains(packets, p => IsSwingAnim(p, attacker.Uid.Value));
        // ...but the blow has not landed: no damage, no strike sound yet.
        Assert.True(attacker.HasPendingHit);
        Assert.Equal(100, target.Hits);
        Assert.Empty(Sounds(packets));
        Assert.InRange(attacker.SwingHitTime - before, 900, 1100 + (Environment.TickCount64 - before));

        // A tick before the windup elapses changes nothing.
        client.TickCombat();
        Assert.True(attacker.HasPendingHit);
        Assert.Equal(100, target.Hits);

        // Once the animation delay has passed the blow lands.
        attacker.SwingHitTime = Environment.TickCount64 - 1;
        client.TickCombat();
        Assert.False(attacker.HasPendingHit);
        Assert.True(target.Hits < 100);
        Assert.NotEmpty(Sounds(packets));
    }

    [Fact]
    public void PreHit_LandsTheBlowWithTheAnimation()
    {
        Character.CombatFlags = (int)CombatFlags.PreHit;
        var world = TestHarness.CreateWorld();
        var (client, attacker, target, _, packets) = MakeFight(1902, world);

        client.TickCombat();

        Assert.Contains(packets, p => IsSwingAnim(p, attacker.Uid.Value));
        Assert.False(attacker.HasPendingHit);
        Assert.True(target.Hits < 100);
    }

    [Fact]
    public void DefaultSwing_TargetLeavesReachDuringWindup_BlowIsHeldUntilItComesBack()
    {
        Character.CombatFlags = 0;
        var world = TestHarness.CreateWorld();
        var (client, attacker, target, _, packets) = MakeFight(1903, world);

        client.TickCombat();
        Assert.True(attacker.HasPendingHit);

        // Out of reach when the blow should land: Source-X holds the loaded swing
        // (swingTypeHold = WAR_SWING_READY) - no damage, no miss.
        world.MoveCharacter(target, new Point3D(106, 100, 0, 0));
        attacker.SwingHitTime = Environment.TickCount64 - 1;
        client.TickCombat();
        Assert.True(attacker.HasPendingHit);
        Assert.Equal(100, target.Hits);
        Assert.Empty(Sounds(packets));

        // Back in reach: the held blow lands.
        world.MoveCharacter(target, new Point3D(101, 100, 0, 0));
        client.TickCombat();
        Assert.False(attacker.HasPendingHit);
        Assert.True(target.Hits < 100);
    }

    [Fact]
    public void StayInRange_TargetLeavesReachDuringWindup_SwingIsSpentSilently()
    {
        Character.CombatFlags = (int)CombatFlags.StayInRange;
        var world = TestHarness.CreateWorld();
        var (client, attacker, target, _, packets) = MakeFight(1904, world);

        client.TickCombat();
        Assert.True(attacker.HasPendingHit);

        world.MoveCharacter(target, new Point3D(106, 100, 0, 0));
        attacker.SwingHitTime = Environment.TickCount64 - 1;
        client.TickCombat();

        // WAR_SWING_EQUIPPING straight out of Fight_Hit: no damage and no miss
        // whoosh either (CCharFight.cpp:1896).
        Assert.False(attacker.HasPendingHit);
        Assert.Equal(100, target.Hits);
        Assert.Empty(Sounds(packets));
    }

    [Fact]
    public void DefaultSwing_TargetDiesDuringWindup_BlowIsDropped()
    {
        Character.CombatFlags = 0;
        var world = TestHarness.CreateWorld();
        var (client, attacker, target, _, _) = MakeFight(1905, world);

        client.TickCombat();
        Assert.True(attacker.HasPendingHit);

        target.SetStatFlag(StatFlag.Invul); // an invalid target (Fight_CanHit -> INVALID)
        attacker.SwingHitTime = Environment.TickCount64 - 1;
        client.TickCombat();

        Assert.False(attacker.HasPendingHit);
        Assert.Equal(100, target.Hits);
    }

    [Fact]
    public void HitTry_ArgN1IsTheRecoil_AndAnimDelayPacesTheBlow()
    {
        Character.CombatFlags = 0;
        string path = WriteScript("""
            [EVENTS e_hittry_delay_probe]
            ON=@HitTry
            TAG.SEENN1=<ARGN1>
            TAG.SEENAD=<LOCAL.AnimDelay>
            ARGN1=7
            LOCAL.AnimDelay=5
            """);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);
        stack.Dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        var (client, attacker, _, sword, _) = MakeFight(1906, world);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        attacker.Events.Add(stack.Resources.ResolveDefName("e_hittry_delay_probe"));

        int speed = CombatEngine.GetSwingDelayMs(attacker, sword);
        var expected = CombatHelper.GetDefaultSwingDelays(speed);

        long before = Environment.TickCount64;
        client.TickCombat();
        long after = Environment.TickCount64;

        // Source-X hands the script the recoil and the animation delay, in tenths.
        Assert.True(attacker.TryGetTag("SEENN1", out var n1));
        Assert.Equal(expected.RecoilTenths.ToString(), n1);
        Assert.True(attacker.TryGetTag("SEENAD", out var ad));
        Assert.Equal("10", ad);

        // ...and swings on what it hands back: the blow 0.5 s after the animation,
        // the next swing 0.7 s after the blow.
        Assert.True(attacker.HasPendingHit);
        Assert.InRange(attacker.SwingHitTime, before + 500, after + 500);
        Assert.InRange(attacker.NextAttackTime, before + 1200, after + 1200);
    }

    [Fact]
    public void NpcSwing_AnimatesAtStart_AndResolvesOnlyAfterTheWindup()
    {
        Character.CombatFlags = 0;
        CombatEngine.WeaponDefLookup = _ => (10, 10);
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Karma = -1; // evil: a monster needs karma below zero (Noto_IsEvil, CCharNotoriety.cpp:53)
        npc.Str = npc.Dex = 100;
        npc.Stam = npc.MaxStam = 100;
        npc.Hits = npc.MaxHits = 100;
        npc.SetSkill(SkillType.Wrestling, 1000);
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        var order = new List<string>();
        ai.OnNpcSwingStart = (_, _, _, _, animDelay) => order.Add($"anim:{animDelay}");
        ai.OnNpcAttack = (_, _, _, _, _) => order.Add("attack");

        npc.FightTarget = target.Uid;
        npc.NextNpcActionTime = 0;
        npc.NextAttackTime = 0;
        ai.OnTickAction(npc);

        Assert.Equal(new[] { "anim:1" }, order);
        Assert.True(npc.HasPendingHit);

        npc.SwingHitTime = Environment.TickCount64 - 1;
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);

        Assert.Equal(new[] { "anim:1", "attack" }, order);
        Assert.False(npc.HasPendingHit);
    }

    // ---- sounds ------------------------------------------------------------

    private (Character Npc, int DefIndex) LoadCreature(GameWorld world, string defname, string body)
    {
        var npc = world.CreateCharacter();
        int index = LoadedDefs.ResolveDefName(defname).Index;
        npc.CharDefIndex = index;
        npc.BodyId = Convert.ToUInt16(body, 16);
        return (npc, index);
    }

    private SphereNet.Scripting.Resources.ResourceHolder LoadedDefs { get; set; } = null!;

    private void LoadSoundDefs()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [CHARDEF c_snd_probe_orc]
            ID=011
            NAME=Probe Orc
            SOUND=045a

            [CHARDEF c_snd_probe_crane]
            ID=0fe
            SOUND=04d6

            [CHARDEF c_snd_probe_infernal]
            ID=0fd
            SOUND=05d4

            [CHARDEF c_snd_probe_override]
            ID=012
            SOUND=045a
            SOUNDHIT=0111
            SOUNDDIE=-1

            [CHARDEF c_snd_probe_mute]
            ID=013
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        LoadedDefs = stack.Resources;
    }

    [Fact]
    public void CreatureDefinedBySoundOnly_HasEveryActionSound_FromTheBaseOffsets()
    {
        LoadSoundDefs();
        var world = TestHarness.CreateWorld();
        var (orc, _) = LoadCreature(world, "c_snd_probe_orc", "011");

        // Below the crane: base + CRESND_* (idle 0, notice 1, hit 2, gethit 3, die 4).
        Assert.Equal((ushort)0x45A, CharacterSounds.Resolve(orc, CreatureSoundType.Idle));
        Assert.Equal((ushort)0x45B, CharacterSounds.Resolve(orc, CreatureSoundType.Notice));
        Assert.Equal((ushort)0x45C, CharacterSounds.Resolve(orc, CreatureSoundType.Hit));
        Assert.Equal((ushort)0x45D, CharacterSounds.Resolve(orc, CreatureSoundType.GetHit));
        Assert.Equal((ushort)0x45E, CharacterSounds.Resolve(orc, CreatureSoundType.Die));

        // Through the combat entry points: unarmed strike and the pain cry.
        Assert.Equal((ushort)0x45C, GameClient.GetAttackerHitSoundPublic(orc, null));
        Assert.Equal((ushort)0x45D, GameClient.GetDefenderHitSoundPublic(orc));

        // Crane .. abyssal infernal: die +0, hit +1, idle +2, notice +3, gethit +4.
        var (crane, _) = LoadCreature(world, "c_snd_probe_crane", "0fe");
        Assert.Equal((ushort)0x4D6, CharacterSounds.Resolve(crane, CreatureSoundType.Die));
        Assert.Equal((ushort)0x4D7, CharacterSounds.Resolve(crane, CreatureSoundType.Hit));
        Assert.Equal((ushort)0x4DA, CharacterSounds.Resolve(crane, CreatureSoundType.GetHit));

        // From the infernal on: hit +0, die +1, gethit +2, idle/notice +3.
        var (infernal, _) = LoadCreature(world, "c_snd_probe_infernal", "0fd");
        Assert.Equal((ushort)0x5D4, CharacterSounds.Resolve(infernal, CreatureSoundType.Hit));
        Assert.Equal((ushort)0x5D5, CharacterSounds.Resolve(infernal, CreatureSoundType.Die));
        Assert.Equal((ushort)0x5D6, CharacterSounds.Resolve(infernal, CreatureSoundType.GetHit));
        Assert.Equal((ushort)0x5D7, CharacterSounds.Resolve(infernal, CreatureSoundType.Idle));
    }

    [Fact]
    public void SoundOverrides_WinOverTheBase_AndMinusOneSilencesTheAction()
    {
        LoadSoundDefs();
        var world = TestHarness.CreateWorld();
        var (npc, index) = LoadCreature(world, "c_snd_probe_override", "012");

        Assert.Equal((ushort)0x111, CharacterSounds.Resolve(npc, CreatureSoundType.Hit));
        Assert.Equal((ushort)0x45D, CharacterSounds.Resolve(npc, CreatureSoundType.GetHit));
        Assert.Equal((ushort)0, CharacterSounds.Resolve(npc, CreatureSoundType.Die));

        // SOUND reads back the base, not the idle slot.
        var def = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(index)!;
        Assert.Equal((ushort)0x45A, def.SoundBase);
        Assert.Equal((ushort)0, def.SoundIdle);

        // No SOUND at all: silent, like upstream.
        var (mute, _) = LoadCreature(world, "c_snd_probe_mute", "013");
        Assert.Equal((ushort)0, CharacterSounds.Resolve(mute, CreatureSoundType.GetHit));
    }

    [Fact]
    public void GenericSoundsOff_SilencesSoundChar()
    {
        LoadSoundDefs();
        var world = TestHarness.CreateWorld();
        var (orc, _) = LoadCreature(world, "c_snd_probe_orc", "011");
        CharacterSounds.GenericSoundsEnabled = false;
        Assert.Equal((ushort)0, CharacterSounds.Resolve(orc, CreatureSoundType.Die));
    }

    [Fact]
    public void HumanSoundSets_AreTheSourceXOnes()
    {
        var rng = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            Assert.Contains(CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.Hit, false, rng),
                new ushort[] { 0x135, 0x137, 0x13B });
            Assert.InRange(CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.Die, false, rng), 0x15A, 0x15D);
            Assert.InRange(CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.Die, true, rng), 0x150, 0x153);
            Assert.InRange(CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.GetHit, false, rng), 0x154, 0x159);
            Assert.InRange(CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.GetHit, true, rng), 0x14B, 0x14F);
        }
        // A human has no idle / notice sound.
        Assert.Equal((ushort)0, CharacterSounds.FromBase(CharacterSounds.SpecialHuman, CreatureSoundType.Idle, false, rng));
    }

    [Fact]
    public void WeaponSoundProps_OverrideTheClassSounds()
    {
        var world = TestHarness.CreateWorld();
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        Assert.Contains(CharacterSounds.GetWeaponHitSound(sword), new ushort[] { 0x23B, 0x23C });

        Assert.True(sword.TrySetProperty("WEAPONSOUNDHIT", "0333"));
        Assert.True(sword.TrySetProperty("WEAPONSOUNDMISS", "0334"));
        Assert.Equal((ushort)0x333, CharacterSounds.GetWeaponHitSound(sword));
        Assert.Equal((ushort)0x334, CharacterSounds.GetMissSound(sword));

        var attacker = world.CreateCharacter();
        attacker.BodyId = 0x190;
        Assert.Equal((ushort)0x333, GameClient.GetAttackerHitSoundPublic(attacker, sword));
    }

    [Fact]
    public void AmmoSoundProps_WinOnBowsAndCrossbows_ButNotOnThrowingWeapons()
    {
        var world = TestHarness.CreateWorld();
        var bow = world.CreateItem();
        bow.ItemType = ItemType.WeaponBow;
        Assert.True(bow.TrySetProperty("WEAPONSOUNDHIT", "0333"));
        Assert.True(bow.TrySetProperty("AMMOSOUNDHIT", "0444"));
        Assert.True(bow.TrySetProperty("AMMOSOUNDMISS", "0445"));
        Assert.Equal((ushort)0x444, CharacterSounds.GetWeaponHitSound(bow));
        Assert.Equal((ushort)0x445, CharacterSounds.GetMissSound(bow));

        var thrown = world.CreateItem();
        thrown.ItemType = ItemType.WeaponThrowing;
        Assert.True(thrown.TrySetProperty("AMMOSOUNDHIT", "0444"));
        Assert.Equal((ushort)0x5D2, CharacterSounds.GetWeaponHitSound(thrown)); // throwH
    }

    [Fact]
    public void ThrowingWeaponsMissWithTheRangedSet()
    {
        var world = TestHarness.CreateWorld();
        var thrown = world.CreateItem();
        thrown.ItemType = ItemType.WeaponThrowing;
        var rng = new Random(5);
        for (int i = 0; i < 40; i++)
            Assert.Contains(CharacterSounds.GetMissSound(thrown, rng), new ushort[] { 0x233, 0x238 });
        for (int i = 0; i < 40; i++)
            Assert.Contains(CharacterSounds.GetMissSound(null, rng), new ushort[] { 0x238, 0x239, 0x23A });
    }

    [Fact]
    public void ArmedStrike_WithWeaponSoundProp_IsBroadcastWhenTheBlowLands()
    {
        Character.CombatFlags = (int)CombatFlags.PreHit;
        var world = TestHarness.CreateWorld();
        var (client, _, target, sword, packets) = MakeFight(1907, world);
        Assert.True(sword.TrySetProperty("WEAPONSOUNDHIT", "0333"));

        client.TickCombat();

        Assert.True(target.Hits < 100);
        Assert.Contains((ushort)0x333, Sounds(packets));
    }

    // ---- names in combat messages ------------------------------------------

    [Fact]
    public void AttackEmote_NamesAChardefNpcByItsDefinitionName()
    {
        LoadSoundDefs();
        var world = TestHarness.CreateWorld();
        var (npc, _) = LoadCreature(world, "c_snd_probe_orc", "011");
        npc.Name = "";
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.Name = "Victim";

        var emote = CombatHelper.FormatAttackEmotes(npc, victim);

        Assert.Equal("Probe Orc", emote.AttackerName);
        Assert.Equal("*Probe Orc is attacking you!*", emote.VictimText);
        Assert.Equal("*Probe Orc is attacking Victim!*", emote.OthersText);
    }
}
