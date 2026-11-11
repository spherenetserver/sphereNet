using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Death;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The glue around a character's life, death and return, pinned to what Source-X does:
/// the status window's stat cap, what a ghost may touch and how it sounds, reach for an
/// item and for a mount, the lines said (and not said) on death and resurrection, the
/// paperdoll, the skill button's wait rules, war mode across death, meditation under
/// damage, a figurine's creature, and regeneration of a statue.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharacterLifecycleGlueTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Me);

    private static Bench Build(int port, PrivLevel priv = PrivLevel.Player)
    {
        var world = TestHarness.CreateWorld();
        var lf = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.BodyId = 0x0190;
        me.PrivLevel = priv;
        me.Str = me.Dex = me.Int = 50;
        me.MaxHits = 50; me.Hits = 50;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        client.NetState.ClientVersionNumber = 60_000_000;
        TestHarness.ClearQueuedPackets(client.NetState);
        return new Bench(world, client, me);
    }

    private static Item GroundChest(Bench b, int dx)
    {
        var chest = b.World.CreateItem();
        chest.BaseId = 0x0E42;
        chest.ItemType = ItemType.Container;
        b.World.PlaceItem(chest, new Point3D((short)(100 + dx), 100, 0, 0));
        return chest;
    }

    /// <summary>Every line of text the client was sent (0xAE, text from byte 48).</summary>
    private static List<string> Texts(GameClient client) =>
        TestHarness.GetQueuedPackets(client.NetState)
            .Where(p => p.Length > 48 && p.Span[0] == 0xAE)
            .Select(p => System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..]).TrimEnd('\0'))
            .ToList();

    private static bool Sent(GameClient client, byte id) =>
        TestHarness.GetQueuedPackets(client.NetState).Any(p => p.Length > 0 && p.Span[0] == id);

    // ---- 1. status window stat cap (send.cpp:285-288, CCharStat.cpp:476-491) ----

    [Fact]
    public void TheStatusWindowCarriesTheStatSumLimitTheGainCodeEnforces()
    {
        var b = Build(9301);
        Assert.Equal(300, SkillEngine.StatSumLimit(b.Me));   // no class STATSUM: 300, not 225

        b.Me.SetTag("OVERRIDE.STATSUM", "270");
        b.Client.NetState.ClientVersionNumber = 0;           // AOS shape: cap right after weight
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.SendCharacterStatus(b.Me);

        var status = TestHarness.GetQueuedPackets(b.Client.NetState).First(p => p.Span[0] == 0x11);
        Assert.Equal(3, status.Span[42]);
        Assert.Equal(270, BinaryPrimitives.ReadInt16BigEndian(status.Span[66..]));
    }

    // ---- 2. what a ghost may touch, and item reach (CCharStatus.cpp:1293-1430) ----

    [Fact]
    public void AGhostIsRefusedBeforeTheDClickTriggerAndToldItCannotReach()
    {
        var b = Build(9302);
        var chest = GroundChest(b, 1);
        int fired = 0;
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DClick", (_, _) => { fired++; return TriggerResult.Default; });
        b.Client.SetEngines(triggerDispatcher: dispatcher);

        b.Client.HandleDoubleClick(chest.Uid.Value);
        Assert.Equal(1, fired);
        Assert.True(Sent(b.Client, 0x24), "calibration: alive, the chest opens");

        b.Me.SetStatFlag(StatFlag.Dead);
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.HandleDoubleClick(chest.Uid.Value);

        Assert.Equal(1, fired);
        Assert.False(Sent(b.Client, 0x24));
        Assert.Equal([ServerMessages.Get(Msg.ReachGhost)], Texts(b.Client));
    }

    [Fact]
    public void AGhostInGmModeMayUseAnythingAndAPlainGhostStillReachesAShrine()
    {
        var gm = Build(9303, PrivLevel.GM);
        var chest = GroundChest(gm, 1);
        gm.Me.SetStatFlag(StatFlag.Dead);
        gm.Client.HandleDoubleClick(chest.Uid.Value);
        Assert.True(Sent(gm.Client, 0x24));

        var b = Build(9304);
        var shrine = b.World.CreateItem();
        shrine.BaseId = 0x14E0;
        shrine.ItemType = ItemType.Shrine;
        b.World.PlaceItem(shrine, new Point3D(101, 100, 0, 0));
        b.Me.SetStatFlag(StatFlag.Dead);
        b.Client.HandleDoubleClick(shrine.Uid.Value);
        Assert.False(b.Me.IsDead);
    }

    [Fact]
    public void AnItemIsTouchedFromTwoTilesAndNotFromThree()
    {
        var b = Build(9305);
        b.Client.HandleDoubleClick(GroundChest(b, 2).Uid.Value);
        Assert.True(Sent(b.Client, 0x24));

        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.HandleDoubleClick(GroundChest(b, 3).Uid.Value);
        Assert.False(Sent(b.Client, 0x24));
        Assert.Equal([ServerMessages.Get(Msg.ReachFail)], Texts(b.Client));
    }

    // ---- 3. ghost speech (CWorldComm.cpp:63-76, CCharStatus.cpp:82-87) ----

    private static (GameClient Listener, Character Hearer) AddListener(Bench b, int port)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var listener = TestHarness.CreateClient(lf, b.World, new AccountManager(lf), port);
        var hearer = b.World.CreateCharacter();
        hearer.IsPlayer = true;
        b.World.PlaceCharacter(hearer, new Point3D(101, 100, 0, 0));
        TestHarness.AttachCharacter(listener, hearer);
        b.Client.ForEachClientInRange = (_, _, exclude, action) =>
        {
            if (hearer.Uid.Value != exclude) action(hearer, listener);
        };
        b.Client.BroadcastNearby = (_, _, packet, exclude) =>
        {
            if (hearer.Uid.Value != exclude) listener.Send(packet);
        };
        return (listener, hearer);
    }

    [Fact]
    public void TheLivingHearAGhostMoanInTheHueItSpokeIn()
    {
        var b = Build(9306);
        var (listener, _) = AddListener(b, 9307);
        b.Me.SetStatFlag(StatFlag.Dead);

        b.Client.HandleSpeech(0, 0x0035, 3, "help me please");

        var heard = TestHarness.GetQueuedPackets(listener.NetState).ToList();
        var line = heard.Single(p => p.Span[0] == 0xAE);
        string text = System.Text.Encoding.BigEndianUnicode.GetString(line.Span[48..]).TrimEnd('\0');
        Assert.Equal("help me please".Length, text.Length);
        Assert.All(text, c => Assert.Contains(c, "oO "));
        Assert.Equal(0x0035, BinaryPrimitives.ReadUInt16BigEndian(line.Span[10..]));
        var moan = heard.Single(p => p.Span[0] == 0x54);
        Assert.InRange(BinaryPrimitives.ReadUInt16BigEndian(moan.Span[2..]), (ushort)382, (ushort)386);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AGhostUnderSpiritSpeakOrInGmModeIsUnderstoodByAll(bool spiritSpeak, bool gmMode)
    {
        var b = Build(9308, gmMode ? PrivLevel.GM : PrivLevel.Player);
        var (listener, _) = AddListener(b, 9309);
        b.Me.SetStatFlag(StatFlag.Dead);
        if (spiritSpeak) b.Me.SetStatFlag(StatFlag.SpiritSpeak);
        Assert.False(GhostSpeech.SpeaksAsGhost(b.Me));

        b.Client.HandleSpeech(0, 0x0035, 3, "help me please");

        var heard = TestHarness.GetQueuedPackets(listener.NetState).ToList();
        var line = heard.Single(p => p.Span[0] == 0xAE);
        Assert.Equal("help me please",
            System.Text.Encoding.BigEndianUnicode.GetString(line.Span[48..]).TrimEnd('\0'));
        Assert.DoesNotContain(heard, p => p.Span[0] == 0x54);
    }

    // ---- 7. speaking reveals only under REVEALF_SPEAK (CCharAct.cpp:3553-3564) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpeakingRevealsOnlyWhenTheShardSaysSo(bool revealOnSpeak)
    {
        var saved = Character.ActiveRevealFlags;
        try
        {
            Character.ActiveRevealFlags = revealOnSpeak
                ? saved | RevealFlags.Speak
                : saved & ~RevealFlags.Speak;
            var b = Build(9310);
            b.Client.SetEngines(speech: new SpeechEngine(b.World));
            b.Me.SetStatFlag(StatFlag.Hidden);

            b.Client.HandleSpeech(0, 0x0035, 3, "is anyone there");

            Assert.Equal(!revealOnSpeak, b.Me.IsStatFlag(StatFlag.Hidden));
        }
        finally { Character.ActiveRevealFlags = saved; }
    }

    // ---- 4. mounting: touch reach and the swap (CCharAct.cpp:3957, 4011) ----

    private static Character Horse(Bench b, int dx)
    {
        var horse = b.World.CreateCharacter();
        horse.BodyId = 0x00C8;
        horse.Name = "horse" + dx;
        b.World.PlaceCharacter(horse, new Point3D((short)(100 + dx), 100, 0, 0));
        horse.TryAssignOwnership(b.Me, b.Me);
        return horse;
    }

    [Fact]
    public void AMountIsReachedFromTwoTilesAndRefusedFromThree()
    {
        var b = Build(9311);
        var engine = new MountEngine(b.World);
        var told = new List<string>();
        Character.SendOwnerMessage = (ch, text) => { if (ch == b.Me) told.Add(text); };

        Assert.False(engine.TryMount(b.Me, Horse(b, 3)));
        Assert.Equal([ServerMessages.Get(Msg.MsgMountDist)], told);

        Assert.True(engine.TryMount(b.Me, Horse(b, 2)));
        Assert.True(b.Me.IsMounted);
    }

    [Fact]
    public void MountingAnotherOwnedMountWhileRidingSwaps()
    {
        var b = Build(9312);
        var engine = new MountEngine(b.World);
        var first = Horse(b, 1);
        var second = Horse(b, 2);
        Assert.True(engine.TryMount(b.Me, first));
        Assert.True(first.IsStatFlag(StatFlag.Ridden));

        Assert.True(engine.TryMount(b.Me, second));

        Assert.True(b.Me.IsMounted);
        Assert.True(second.IsStatFlag(StatFlag.Ridden));
        Assert.False(first.IsStatFlag(StatFlag.Ridden));
        Assert.Equal(b.Me.Position, first.Position);
        Assert.Equal(second.Uid.Value, b.Me.GetEquippedItem(Layer.Horse)!.More2);
    }

    // ---- 5. death and resurrection lines (CCharAct.cpp:4326-4544, CCharSpell.cpp:493-525) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResurrectionSaysOnlyThatTheSpiritRejoinsARaisedCorpse(bool withCorpse)
    {
        var savedDef = Character.ResolveSpellDef;
        try
        {
            Character.ResolveSpellDef = null;
            var b = Build(9313);
            b.Client.SetEngines(deathEngine: new DeathEngine(b.World));
            var shown = new List<PacketWriter>();
            b.Client.BroadcastNearby = (_, _, packet, _) => shown.Add(packet);
            b.Me.SetStatFlag(StatFlag.Dead);
            if (withCorpse)
            {
                var corpse = b.World.CreateItem();
                corpse.BaseId = 0x2006;
                corpse.ItemType = ItemType.Corpse;
                corpse.SetTag("OWNER_UID", b.Me.Uid.Value.ToString());
                b.World.PlaceItem(corpse, b.Me.Position);
            }
            TestHarness.ClearQueuedPackets(b.Client.NetState);

            b.Client.OnResurrect();

            Assert.False(b.Me.IsDead);
            Assert.Equal(withCorpse ? [ServerMessages.Get(Msg.SpellResRejoin)] : new List<string>(), Texts(b.Client));
            // No effect and no sound of the client's own: the Resurrection SPELLDEF's
            // are the only ones (none is defined here).
            Assert.DoesNotContain(shown, p => p.Build().Span[0] is 0x70 or 0x54);
        }
        finally { Character.ResolveSpellDef = savedDef; }
    }

    // ---- 9. dying in war mode (CCharAct.cpp:4490-4491, 4514) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGhostKeepsTheWarModeItDiedInAndIsInsubstantialOnlyInPeace(bool war)
    {
        var b = Build(9314);
        if (war) b.Me.SetStatFlag(StatFlag.War);
        b.Me.Kill();
        TestHarness.ClearQueuedPackets(b.Client.NetState);

        b.Client.OnCharacterDeath();

        Assert.Equal(war, b.Me.IsInWarMode);
        Assert.Equal(!war, b.Me.IsStatFlag(StatFlag.Insubstantial));
        var echo = TestHarness.GetQueuedPackets(b.Client.NetState).Single(p => p.Span[0] == 0x72);
        Assert.Equal(war ? 1 : 0, echo.Span[1]);
        Assert.Empty(Texts(b.Client));   // and no "you have died" line
    }

    // ---- 6. paperdoll (CClientMsg.cpp:2685-2697, send.cpp:2646-2650, CCharStatus.cpp:921-931) ----

    [Fact]
    public void APaperdollIsSentOnEveryRequest()
    {
        var b = Build(9315);
        b.Client.SendPaperdoll(b.Me);
        b.Client.SendPaperdoll(b.Me);
        Assert.Equal(2, TestHarness.GetQueuedPackets(b.Client.NetState).Count(p => p.Span[0] == 0x88));
    }

    [Fact]
    public void ThePaperdollMayBeDressedByWhoeverCanDressTheCharacter()
    {
        bool savedUndress = Character.CanUndressPets;
        try
        {
            var b = Build(9316);
            var stranger = b.World.CreateCharacter();
            stranger.IsPlayer = true;
            b.World.PlaceCharacter(stranger, new Point3D(101, 100, 0, 0));
            var pet = Horse(b, 1);

            byte Mode(Character whose)
            {
                TestHarness.ClearQueuedPackets(b.Client.NetState);
                b.Client.SendPaperdoll(whose);
                return TestHarness.GetQueuedPackets(b.Client.NetState).First(p => p.Span[0] == 0x88).Span[65];
            }

            Assert.Equal(0x02, Mode(b.Me) & 0x02);
            Assert.Equal(0x00, Mode(stranger) & 0x02);
            Character.CanUndressPets = true;
            Assert.Equal(0x02, Mode(pet) & 0x02);
            Character.CanUndressPets = false;
            Assert.Equal(0x00, Mode(pet) & 0x02);

            b.Me.PrivLevel = PrivLevel.GM;       // GM mode over a lower privilege level
            Assert.Equal(0x02, Mode(stranger) & 0x02);
            stranger.PrivLevel = PrivLevel.GM;   // not over an equal
            Assert.Equal(0x00, Mode(stranger) & 0x02);
        }
        finally { Character.CanUndressPets = savedUndress; }
    }

    // ---- 8. the skill button's wait rules (CCharSkill.cpp:3977-4050) ----

    private static Bench SkillBench(int port)
    {
        var b = Build(port);
        b.Client.SetEngines(skillHandlers: new SkillHandlers(b.World), triggerDispatcher: new TriggerDispatcher());
        return b;
    }

    [Fact]
    public void AGhostPressingASkillButtonIsToldItCanDoLittle()
    {
        var b = SkillBench(9317);
        b.Me.SetStatFlag(StatFlag.Dead);
        b.Client.HandleUseSkill((int)SkillType.Hiding);
        Assert.Equal([ServerMessages.Get(Msg.Skillwait1)], Texts(b.Client));
    }

    [Fact]
    public void InWarModeABusyCharacterMustWaitEvenOutOfMeditation()
    {
        var b = SkillBench(9318);
        b.Me.BeginSkillPending((int)SkillType.Meditation, long.MaxValue / 2, long.MaxValue / 2, Serial.Invalid, null);
        b.Me.SetStatFlag(StatFlag.Meditation);
        b.Me.SetStatFlag(StatFlag.War);

        b.Client.HandleUseSkill((int)SkillType.Hiding);

        Assert.Equal([ServerMessages.Get(Msg.Skillwait2)], Texts(b.Client));
        Assert.True(b.Me.IsStatFlag(StatFlag.Meditation));
        Assert.Equal((int)SkillType.Meditation, b.Me.SkillPendingId);
    }

    [Fact]
    public void InPeaceABusyCharacterWaitsUnlessWhatItDoesIsPassive()
    {
        var b = SkillBench(9319);
        b.Me.BeginSkillPending((int)SkillType.Tracking, long.MaxValue / 2, long.MaxValue / 2, Serial.Invalid, null);
        b.Client.HandleUseSkill((int)SkillType.Hiding);
        Assert.Equal([ServerMessages.Get(Msg.Skillwait3)], Texts(b.Client));
        Assert.Equal((int)SkillType.Tracking, b.Me.SkillPendingId);

        var m = SkillBench(9320);
        m.Me.BeginSkillPending((int)SkillType.Meditation, long.MaxValue / 2, long.MaxValue / 2, Serial.Invalid, null);
        m.Me.SetStatFlag(StatFlag.Meditation);
        m.Client.HandleUseSkill((int)SkillType.Tracking);
        Assert.False(m.Me.IsStatFlag(StatFlag.Meditation));
        Assert.DoesNotContain(ServerMessages.Get(Msg.Skillwait3), Texts(m.Client));
    }

    [Fact]
    public void AnIdleHiddenCharacterIsRevealedByASkillButtonButNotByStealth()
    {
        var b = SkillBench(9321);
        b.Me.SetStatFlag(StatFlag.Hidden);
        b.Client.HandleUseSkill((int)SkillType.Stealth);
        Assert.True(b.Me.IsStatFlag(StatFlag.Hidden));

        b.Client.HandleUseSkill((int)SkillType.Tracking);
        Assert.False(b.Me.IsStatFlag(StatFlag.Hidden));
    }

    // ---- 10. meditation under damage (CCharFight.cpp:881-915, 291-316) ----

    [Fact]
    public void LosingHitPointsLeavesAMeditationRunningAndAFightEndsIt()
    {
        var b = Build(9322);
        b.Me.SetStatFlag(StatFlag.Meditation);

        b.Me.Hits -= 5;
        Assert.True(b.Me.IsStatFlag(StatFlag.Meditation));

        b.Me.FightTarget = new Serial(0x0000_1234);
        Assert.False(b.Me.IsStatFlag(StatFlag.Meditation));
    }

    // ---- 11. a figurine's creature (CCharUse.cpp:1127-1221) ----

    [Fact]
    public void AFigurinesCreatureAppearsWhereTheFigurineLiesFacingAsItsUserAndSilently()
    {
        var b = Build(9323);
        var horse = b.World.CreateCharacter();
        horse.BodyId = 0x00C8;
        b.World.PlaceCharacter(horse, new Point3D(300, 300, 0, 0));
        var figurine = b.World.CreateItem();
        figurine.BaseId = 0x20DD;
        figurine.ItemType = ItemType.Figurine;
        figurine.More2 = horse.Uid.Value;
        var lies = new Point3D(102, 100, 0, 0);
        b.World.PlaceItem(figurine, lies);
        b.Me.Direction = Direction.West;
        horse.Direction = Direction.South;

        b.Client.HandleDoubleClick(figurine.Uid.Value);

        Assert.True(figurine.IsDeleted);
        Assert.Equal(lies, horse.Position);
        // The user turned to the figurine to use it (Event_DoubleClick's UpdateDir),
        // and the creature takes that facing.
        Assert.Equal(Direction.East, b.Me.Direction);
        Assert.Equal(Direction.East, horse.Direction);
        Assert.True(horse.HasOwner(b.Me.Uid));
        Assert.Empty(Texts(b.Client));
    }

    // ---- 12. hunger spares GM mode, not the GM privilege level (CCharAct.cpp:5780-5781) ----

    [Fact]
    public void AGmWithGmModeSwitchedOffGetsHungry()
    {
        var b = Build(9325, PrivLevel.GM);
        var offDuty = b.World.CreateCharacter();
        offDuty.IsPlayer = true;
        offDuty.PrivLevel = PrivLevel.GM;
        b.World.PlaceCharacter(offDuty, new Point3D(110, 100, 0, 0));
        Assert.True(offDuty.TrySetProperty("GM", "0"));
        Assert.False(offDuty.IsGmMode);
        b.Me.Food = 30;
        offDuty.Food = 30;

        b.Me.OnTick();
        offDuty.OnTick();

        Assert.Equal(30, b.Me.Food);
        Assert.Equal(29, offDuty.Food);
    }

    // ---- 12. a statue does not regenerate (CCharAct.cpp:6122-6125) ----

    [Fact]
    public void ACharacterTurnedToStoneDoesNotRegenerate()
    {
        var b = Build(9324);
        var flesh = b.World.CreateCharacter();
        flesh.IsPlayer = true;
        flesh.MaxHits = 50; flesh.Hits = 10;
        b.World.PlaceCharacter(flesh, new Point3D(110, 100, 0, 0));
        b.Me.Hits = 10;
        b.Me.SetStatFlag(StatFlag.Stone);

        flesh.OnTick();
        b.Me.OnTick();

        Assert.True(flesh.Hits > 10, "calibration: a living character's first tick regenerates");
        Assert.Equal(10, b.Me.Hits);
    }
}
