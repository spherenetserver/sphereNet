using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.World;
using SphereNet.Network.State;
using SphereNet.Network.Packets;

namespace SphereNet.Tests;

/// <summary>
/// Source-X CClient::addItem (CClientMsg.cpp:380-386): a customizable multi is
/// followed by its design revision every time the multi itself is sent to a
/// client. The client only asks for the 0xD8 design stream when it is handed a
/// revision it does not already hold (ClassicUO PacketHandlers.cs:4548-4566), so
/// without this a house that was customized before the viewer arrived keeps
/// drawing as the bare foundation.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CustomHouseRevisionOnViewTests
{
    private static (GameWorld World, GameClient Client) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        return (world, client);
    }

    private static SphereNet.Game.Objects.Items.Item PlaceMulti(GameWorld world, ItemType type)
    {
        var multi = world.CreateItem();
        multi.ItemType = type;
        world.PlaceItem(multi, new Point3D(110, 110, 0, 0));
        return multi;
    }

    private static (uint Serial, uint Revision)? FindVersionPacket(NetState state)
    {
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var s = p.Span;
            if (s.Length != 13 || s[0] != 0xBF) continue;
            if (s[3] != 0x00 || s[4] != 0x1D) continue;
            uint serial = (uint)((s[5] << 24) | (s[6] << 16) | (s[7] << 8) | s[8]);
            uint rev = (uint)((s[9] << 24) | (s[10] << 16) | (s[11] << 8) | s[12]);
            return (serial, rev);
        }
        return null;
    }

    [Fact]
    public void SendWorldItem_CommittedCustomMulti_IsFollowedByItsRevision()
    {
        var (world, client) = Setup();
        var multi = PlaceMulti(world, ItemType.MultiCustom);
        multi.Tags.Set(HouseDesign.RevisionTag, "7");

        client.SendWorldItem(multi);

        var version = FindVersionPacket(client.NetState);
        Assert.NotNull(version);
        Assert.Equal(multi.Uid.Value, version!.Value.Serial);
        Assert.Equal(7u, version.Value.Revision);
    }

    [Fact]
    public void SendWorldItem_UncustomizedFoundation_SendsNoRevision()
    {
        // No committed design means the only 0xD8 we could answer the request
        // with is an empty component list, which tells the client the house is
        // empty rather than leaving it drawing the multi it already has.
        var (world, client) = Setup();
        var multi = PlaceMulti(world, ItemType.MultiCustom);

        client.SendWorldItem(multi);

        Assert.Null(FindVersionPacket(client.NetState));
    }

    [Fact]
    public void SendWorldItem_OrdinaryMulti_SendsNoRevision()
    {
        var (world, client) = Setup();
        var multi = PlaceMulti(world, ItemType.Multi);
        multi.Tags.Set(HouseDesign.RevisionTag, "7");

        client.SendWorldItem(multi);

        Assert.Null(FindVersionPacket(client.NetState));
    }
}
