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
        Assert.Equal(40, BinaryPrimitives.ReadInt16BigEndian(p[37..]));
        Assert.Equal(50, BinaryPrimitives.ReadInt16BigEndian(p[39..]));
        Assert.Equal(1, p[41]); // the owner may rename their pet
    }
}
