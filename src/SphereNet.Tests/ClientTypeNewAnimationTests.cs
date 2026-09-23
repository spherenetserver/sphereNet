using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The 0xE1 client-type parse and the 0xE2 new-animation content.
///
/// 0xE1 is [E1][u16 length][u16 count][u32 type]; Source-X skips the count
/// (PacketClientType::onReceive, receive.cpp:4305). Reading the type straight after the
/// length put the count in its high half, and the old clamp to 3 made every sender an
/// Enhanced Client - which then got 0xE2 packets whose action was whatever gesture the
/// call site passed: a mining stroke went out as NANIM_EMOTE and played a bow.
/// Source-X derives the 0xE2 fields from the legacy action (CChar::UpdateAnimate,
/// CCharAct.cpp:2257-2427).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ClientTypeNewAnimationTests
{
    // ---- 0xE1 -----------------------------------------------------------------

    /// <summary>The handler sees the payload after the length word, exactly as
    /// NetworkManager slices it (payloadOffset 3 for a variable packet).</summary>
    private static byte[] PayloadOf(byte[] wire)
    {
        Assert.Equal(0xE1, wire[0]);
        Assert.Equal(wire.Length, (wire[1] << 8) | wire[2]);
        return wire[3..];
    }

    [Theory]
    [InlineData(new byte[] { 0xE1, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 }, (byte)0)] // classic 2D
    [InlineData(new byte[] { 0xE1, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01 }, (byte)1)] // 3D
    [InlineData(new byte[] { 0xE1, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02 }, (byte)2)] // KR
    [InlineData(new byte[] { 0xE1, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03 }, (byte)3)] // EC
    public void ClientTypeIsReadAfterTheCountWord(byte[] wire, byte expected)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);

        new PacketClientType().OnReceive(new PacketBuffer(PayloadOf(wire)), state);

        Assert.Equal(expected, state.ClientTypeFlag);
        Assert.Equal(expected, state.ParsedClientType);
        Assert.Equal(expected == 2, state.IsKingdomRebornClient);
        Assert.Equal(expected == 3, state.IsEnhancedClient);
    }

    [Fact]
    public void AClassicAnnouncementIsNotAnEnhancedClient()
    {
        // The field packet: the old parse read 0x00010000 here and clamped it to 3.
        using var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);
        byte[] wire = { 0xE1, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };

        new PacketClientType().OnReceive(new PacketBuffer(PayloadOf(wire)), state);

        Assert.False(state.IsEnhancedClient);
        Assert.False(state.IsKingdomRebornClient);
    }

    [Fact]
    public void ATruncatedPacketIsIgnored()
    {
        using var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);

        // Length 7: count + only two bytes of the type (Source-X rejects < 9).
        new PacketClientType().OnReceive(new PacketBuffer(new byte[] { 0x00, 0x01, 0x00, 0x00 }), state);

        Assert.Equal(0u, state.ClientTypeFlag);
        Assert.False(state.IsEnhancedClient);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(0x00010000u)]
    [InlineData(0xFFFFFFFFu)]
    public void AnUnknownTypeIsNoneOfTheKnownClients(uint type)
    {
        // Source-X keeps the value as sent and tests it for equality.
        using var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);
        state.ClientTypeFlag = type;

        Assert.False(state.IsEnhancedClient);
        Assert.False(state.IsKingdomRebornClient);
        Assert.NotEqual((byte)3, state.ParsedClientType);
    }

    // ---- 0xE2 -----------------------------------------------------------------

    private static Character Actor(ushort body = 0x0190)
    {
        var ch = new Character { BodyId = body };
        ch.SetUid(new Serial(0x00000200));
        return ch;
    }

    private static Item Weapon(ItemType type, Layer layer = Layer.OneHanded)
        => new Item { ItemType = type, EquipLayer = layer };

    private static (GameClient classic, GameClient enhanced, GameClient hsClassic) Clients()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var accounts = new SphereNet.Game.Accounts.AccountManager(lf);
        var classic = TestHarness.CreateClient(lf, world, accounts, 18101);
        var enhanced = TestHarness.CreateClient(lf, world, accounts, 18102);
        var hsClassic = TestHarness.CreateClient(lf, world, accounts, 18103);
        enhanced.NetState.ClientTypeFlag = 3;
        hsClassic.NetState.ClientVersionNumber = 70_009_000;
        return (classic, enhanced, hsClassic);
    }

    private static void Play(Character actor, ushort action, params GameClient[] viewers)
        => GameClient.PlayAnimation(actor, action, 18, broadcastNearby: null,
            forEachClientInRange: (_, _, _, act) =>
            {
                foreach (var v in viewers) act(actor, v);
            });

    private static List<PacketBuffer> Sent(GameClient c)
        => TestHarness.GetQueuedPackets(c.NetState).ToList();

    private static ushort U16(PacketBuffer p, int o) => (ushort)((p.Span[o] << 8) | p.Span[o + 1]);

    [Fact]
    public void AMiningStrokeIsTheLegacyPacketForAClassicClient()
    {
        var (classic, _, _) = Clients();
        var miner = Actor();
        miner.Equip(Weapon(ItemType.WeaponMacePick), Layer.OneHanded);
        ushort stroke = SkillEngine.GetSkillAnim(SkillType.Mining)!.Value;

        Play(miner, stroke, classic);

        var ops = Sent(classic).Select(p => p.Span[0]).ToArray();
        Assert.Contains((byte)0x6E, ops);
        Assert.DoesNotContain((byte)0xE2, ops);
        var anim = Sent(classic).Single(p => p.Span[0] == 0x6E);
        Assert.Equal((ushort)AnimationType.Attack1HBash, U16(anim, 5));
    }

    [Fact]
    public void AMiningStrokeIsAnAttackForAnEnhancedClientNotAnEmote()
    {
        var (_, enhanced, _) = Clients();
        var miner = Actor();
        miner.Equip(Weapon(ItemType.WeaponMacePick), Layer.OneHanded);
        ushort stroke = SkillEngine.GetSkillAnim(SkillType.Mining)!.Value;
        Assert.Equal((ushort)AnimationType.Attack1HBash, stroke);

        Play(miner, stroke, enhanced);

        var e2 = Sent(enhanced).Single(p => p.Span[0] == 0xE2);
        Assert.Equal(10, e2.Span.Length);
        Assert.Equal(miner.Uid.Value, (uint)((e2.Span[1] << 24) | (e2.Span[2] << 16) | (e2.Span[3] << 8) | e2.Span[4]));
        // ANIM_ATTACK_1H_BASH -> NANIM_ATTACK + NANIM_ATTACK_1H_PIERCE (CCharAct.cpp:2327).
        Assert.Equal((ushort)NewAnimationGesture.Attack, U16(e2, 5));
        Assert.Equal((ushort)NewAnimationAttack.OneHandPierce, U16(e2, 7));
        Assert.Equal(0, e2.Span[9]);
        Assert.DoesNotContain(Sent(enhanced), p => p.Span[0] == 0x6E);
    }

    [Theory]
    [InlineData(ItemType.WeaponSword, false, NewAnimationAttack.OneHandPierce)]
    [InlineData(ItemType.WeaponAxe, false, NewAnimationAttack.OneHandPierce)]
    [InlineData(ItemType.WeaponMacePick, false, NewAnimationAttack.OneHandBash)]
    [InlineData(ItemType.WeaponThrowing, false, NewAnimationAttack.Throwing)]
    [InlineData(ItemType.WeaponBow, true, NewAnimationAttack.Bow)]
    [InlineData(ItemType.WeaponXBow, true, NewAnimationAttack.Crossbow)]
    public void AWeaponSwingTakesItsSubActionFromTheWeaponType(ItemType type, bool twoHand, NewAnimationAttack expected)
    {
        var fighter = Actor();
        var weapon = Weapon(type, twoHand ? Layer.TwoHanded : Layer.OneHanded);
        fighter.Equip(weapon, twoHand ? Layer.TwoHanded : Layer.OneHanded);

        ushort swing = BodyAnimTranslator.Generate(fighter, (ushort)AnimationType.AttackWeapon);
        var n = BodyAnimTranslator.ToNewAnimation(fighter.BodyId, swing, BodyAnimTranslator.WeaponInHand(fighter));

        Assert.Equal((ushort)NewAnimationGesture.Attack, n.Action);
        Assert.Equal((ushort)expected, n.SubAction);
        Assert.Equal(1, n.Variation);   // humans and elves: variation 1 in weapon attacks
    }

    [Fact]
    public void TableMappingsMatchUpdateAnimate()
    {
        const ushort human = 0x0190;
        var cases = new (AnimationType legacy, NewAnimationGesture action, ushort sub, byte variation)[]
        {
            (AnimationType.AttackWeapon, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.OneHandSlash, 0),
            (AnimationType.Attack1HPierce, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.OneHandSlash, 0),
            (AnimationType.Attack1HBash, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.OneHandPierce, 0),
            (AnimationType.Attack2HSlash, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.TwoHandBash, 0),
            (AnimationType.Attack2HBash, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.TwoHandSlash, 0),
            (AnimationType.Attack2HPierce, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.TwoHandSlash, 0),
            (AnimationType.HorseAttack, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.TwoHandBash, 0),
            (AnimationType.CastDirected, NewAnimationGesture.Spell, (ushort)NewAnimationSpell.Normal, 0),
            (AnimationType.CastArea, NewAnimationGesture.Spell, (ushort)NewAnimationSpell.Summon, 0),
            (AnimationType.GetHit, NewAnimationGesture.GetHit, BodyAnimTranslator.NoSubAction, 0),
            (AnimationType.Block, NewAnimationGesture.Block, BodyAnimTranslator.NoSubAction, 1),
            (AnimationType.AttackWrestle, NewAnimationGesture.Attack, (ushort)NewAnimationAttack.Wrestling, 0),
            (AnimationType.Eat, NewAnimationGesture.Eat, BodyAnimTranslator.NoSubAction, 0),
            (AnimationType.DieBackward, NewAnimationGesture.Death, BodyAnimTranslator.NoSubAction, 1),
            (AnimationType.DieForward, NewAnimationGesture.Death, BodyAnimTranslator.NoSubAction, 0),
        };
        foreach (var (legacy, action, sub, variation) in cases)
        {
            var n = BodyAnimTranslator.ToNewAnimation(human, (ushort)legacy, weapon: null);
            Assert.True(n == new BodyAnimTranslator.NewAnimation((ushort)action, sub, variation),
                $"{legacy}: got ({n.Action},{n.SubAction},{n.Variation})");
        }

        // Bow and salute are deliberately not mapped upstream: the legacy number passes.
        var bow = BodyAnimTranslator.ToNewAnimation(human, (ushort)AnimationType.Bow, null);
        Assert.Equal((ushort)AnimationType.Bow, bow.Action);
        Assert.Equal(BodyAnimTranslator.NoSubAction, bow.SubAction);
    }

    [Fact]
    public void ANonPlayableBodyPassesItsTranslatedActionThrough()
    {
        // A dragon's attack is a monster group; only dying is remapped for any body.
        var n = BodyAnimTranslator.ToNewAnimation(0x000C, 0x04, weapon: null);
        Assert.Equal((ushort)0x04, n.Action);
        Assert.Equal(BodyAnimTranslator.NoSubAction, n.SubAction);
        Assert.Equal(0, n.Variation);
    }

    [Fact]
    public void AGargoyleWeaponSwingHasNoVariation()
    {
        var garg = Actor(0x029A);
        var n = BodyAnimTranslator.ToNewAnimation(garg.BodyId, (ushort)AnimationType.AttackWeapon,
            Weapon(ItemType.WeaponSword));
        Assert.Equal((ushort)NewAnimationAttack.OneHandPierce, n.SubAction);
        Assert.Equal(0, n.Variation);
    }

    [Fact]
    public void AGargoyleActorSendsTheNewPacketToA7000ClassicClient()
    {
        var (classic, _, hsClassic) = Clients();   // classic: version unknown; hsClassic: 7.0.9
        var garg = Actor(0x029A);

        Play(garg, (ushort)AnimationType.Eat, classic, hsClassic);

        Assert.Contains(Sent(classic), p => p.Span[0] == 0x6E);
        Assert.DoesNotContain(Sent(classic), p => p.Span[0] == 0xE2);
        var e2 = Sent(hsClassic).Single(p => p.Span[0] == 0xE2);
        Assert.Equal((ushort)NewAnimationGesture.Eat, U16(e2, 5));
        Assert.DoesNotContain(Sent(hsClassic), p => p.Span[0] == 0x6E);
    }

    [Fact]
    public void AHumanActorSendsTheLegacyPacketToA7000ClassicClient()
    {
        var (_, _, hsClassic) = Clients();
        Play(Actor(), (ushort)AnimationType.Eat, hsClassic);

        Assert.Contains(Sent(hsClassic), p => p.Span[0] == 0x6E);
        Assert.DoesNotContain(Sent(hsClassic), p => p.Span[0] == 0xE2);
    }
}
