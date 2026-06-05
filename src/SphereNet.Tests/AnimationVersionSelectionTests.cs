using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;

namespace SphereNet.Tests;

/// <summary>
/// Verifies that <see cref="GameClient.BroadcastAnimation(Character, ushort, NewAnimationGesture, int, Action{Point3D, int, PacketWriter, uint}, Action{Point3D, int, uint, Action{Character, GameClient}}, byte)"/>
/// picks the 0xE2 (KR/Enhanced) or 0x6E (Classic/ClassicUO) animation packet per recipient,
/// and falls back to a single 0x6E broadcast when per-recipient dispatch is
/// unavailable.
/// </summary>
public class AnimationVersionSelectionTests
{
    private static (GameWorld world, Character actor, AccountManager accounts, ILoggerFactory lf) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        var actor = world.CreateCharacter();
        world.PlaceCharacter(actor, new Point3D(1000, 1000, 0, 0));
        return (world, actor, new AccountManager(lf), lf);
    }

    private static GameClient MakeClient(GameWorld world, AccountManager accounts, ILoggerFactory lf,
        int id, uint clientVersion, uint clientType = 0)
    {
        var state = TestHarness.CreateActiveNetState(lf, id);
        state.ClientVersionNumber = clientVersion;
        state.ClientTypeFlag = clientType;
        return new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
    }

    [Fact]
    public void BroadcastAnimation_UsesE2OnlyForKrEnhanced_And6EForClassic()
    {
        var (world, actor, accounts, lf) = Setup();
        var classicHsClient = MakeClient(world, accounts, lf, 1, 70_009_000);
        var enhancedClient = MakeClient(world, accounts, lf, 2, 70_009_000, 3);

        Assert.True(classicHsClient.NetState.SupportsHighSeas);
        Assert.False(classicHsClient.NetState.IsEnhancedClient);
        Assert.True(enhancedClient.NetState.IsEnhancedClient);

        ushort legacyAction = (ushort)AnimationType.AttackWeapon; // 0x09
        GameClient.BroadcastAnimation(
            actor, legacyAction, NewAnimationGesture.Impact, 18,
            broadcastNearby: null,
            forEachClientInRange: (_, _, _, action) =>
            {
                action(actor, classicHsClient);
                action(actor, enhancedClient);
            });

        uint serial = actor.Uid.Value;

        var classicPackets = TestHarness.GetQueuedPackets(classicHsClient.NetState).ToList();
        var a6e = classicPackets.Single(p => p.Span.Length == 14 && p.Span[0] == 0x6E);
        Assert.Equal(serial, ReadU32(a6e, 1));
        Assert.Equal(legacyAction, ReadU16(a6e, 5)); // action field

        var enhancedPackets = TestHarness.GetQueuedPackets(enhancedClient.NetState).ToList();
        var e2 = enhancedPackets.Single(p => p.Span.Length == 10 && p.Span[0] == 0xE2);
        Assert.Equal(serial, ReadU32(e2, 1));
        Assert.Equal((ushort)NewAnimationGesture.Impact, ReadU16(e2, 5)); // gesture field
    }

    [Fact]
    public void BroadcastAnimation_FallsBackToLegacy6E_WhenNoPerRecipientDispatch()
    {
        var (world, actor, _, _) = Setup();

        PacketWriter? captured = null;
        int range = -1;
        GameClient.BroadcastAnimation(
            actor, (ushort)AnimationType.Bow, NewAnimationGesture.Emote, 18,
            broadcastNearby: (_, r, pkt, _) => { captured = pkt; range = r; },
            forEachClientInRange: null);

        Assert.NotNull(captured);
        Assert.Equal(18, range);
        var built = captured!.Build();
        Assert.Equal(0x6E, built.Data[0]); // legacy packet for everyone
        Assert.Equal((ushort)AnimationType.Bow, (ushort)((built.Data[5] << 8) | built.Data[6]));
    }

    private static uint ReadU32(PacketBuffer p, int offset)
        => (uint)((p.Span[offset] << 24) | (p.Span[offset + 1] << 16) | (p.Span[offset + 2] << 8) | p.Span[offset + 3]);

    private static ushort ReadU16(PacketBuffer p, int offset)
        => (ushort)((p.Span[offset] << 8) | p.Span[offset + 1]);
}

