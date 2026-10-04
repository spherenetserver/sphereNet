using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Somebody else's status carries name, hit points, whether the viewer may
/// rename it and the version byte only (Source-X PacketObjectStatus, send.cpp:180);
/// strength, gold and weight are the viewer's own business. The full block went to
/// anyone, and computing it walked a vendor's whole stock.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class OtherStatusIsShortTests
{
    [Fact]
    public void AnotherCharactersStatusStopsAfterTheVersionByte()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7993);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pet = world.CreateCharacter();
        pet.Name = "rex"; pet.MaxHits = 50; pet.Hits = 40;
        pet.NpcMaster = me.Uid;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.SendCharacterStatus(pet, includeExtendedStats: false);

        var p = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), x => x.Span[0] == 0x11).Span;
        Assert.Equal(3 + 4 + 30 + 2 + 2 + 1 + 1, p.Length);
        // Hit points as a percentage over 100, not the real 40/50 (send.cpp:183-184, 205-206).
        Assert.Equal(80, BinaryPrimitives.ReadInt16BigEndian(p[37..]));
        Assert.Equal(100, BinaryPrimitives.ReadInt16BigEndian(p[39..]));
        Assert.Equal(1, p[41]); // the owner may rename their pet
    }

    [Fact]
    public void AStatusRequestForAnotherMobileGetsPercentHitPoints()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7994);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var ogre = world.CreateCharacter();
        ogre.Name = "ogre"; ogre.MaxHits = 300; ogre.Hits = 199;
        world.PlaceCharacter(ogre, new Point3D(102, 100, 0, 0));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleStatusRequest(4, ogre.Uid.Value); // 0x34 health bar request

        var p = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), x => x.Span[0] == 0x11).Span;
        Assert.Equal(66, BinaryPrimitives.ReadInt16BigEndian(p[37..])); // 199*100/300, truncated
        Assert.Equal(100, BinaryPrimitives.ReadInt16BigEndian(p[39..]));
        Assert.Equal(0, p[41]);
    }

    [Fact]
    public void TheViewersOwnStatusKeepsTheRealHitPoints()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7995);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.MaxHits = 120; me.Hits = 90;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        TestHarness.ClearQueuedPackets(client.NetState);
        client.SendCharacterStatus(me);

        var p = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), x => x.Span[0] == 0x11).Span;
        Assert.Equal(90, BinaryPrimitives.ReadInt16BigEndian(p[37..]));
        Assert.Equal(120, BinaryPrimitives.ReadInt16BigEndian(p[39..]));
    }

    [Theory]
    [InlineData(40, 50, 80)]
    [InlineData(0, 0, 0)]      // max clamped to 1 (maximum(tmpMaxHits, 1))
    [InlineData(-5, 50, 0)]
    [InlineData(1, 3, 33)]
    public void PercentHitsMatchesTheUpstreamFormula(short cur, short max, short expected)
    {
        var (pct, pctMax) = SphereNet.Game.Clients.GameClient.PercentStatusHits(cur, max);
        Assert.Equal(expected, pct);
        Assert.Equal(100, pctMax);
    }
}
