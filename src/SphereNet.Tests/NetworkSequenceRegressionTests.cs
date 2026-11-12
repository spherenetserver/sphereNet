using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class NetworkSequenceRegressionTests
{
    private static GameClient Client()
    {
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(NullLoggerFactory.Instance, world,
            new AccountManager(NullLoggerFactory.Instance), 1);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.BodyId = 0x190; me.Str = 100; me.Hits = me.MaxHits = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        var pack = world.CreateItem(); pack.BaseId = 0xE75; pack.ItemType = ItemType.Container;
        me.Backpack = pack; me.Equip(pack, Layer.Pack);
        return client;
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(true, 75)]
    public void ContainerOpenPrecedesContentsEvenWhenQueuesAreFull(bool priority, int max)
    {
        PacketPriorityClassifier.Configure(priority, max, null);
        var client = Client();
        client.NetState.Send(new PacketMoveAck(1, 0));
        client.SendOpenContainer(client.Character!.Backpack!);
        var ops = TestHarness.GetQueuedPackets(client.NetState).Select(p => p.Span[0]).ToList();
        Assert.True(ops.IndexOf(0x2E) < ops.IndexOf(0x24));
        Assert.True(ops.IndexOf(0x24) < ops.IndexOf(0x3C));
    }

    [Fact]
    public void NestedTransactionKeepsOuterPriorityAndRestoresNormalSending()
    {
        PacketPriorityClassifier.Configure(true, 1, null);
        var state = TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 1);
        using (state.BeginPacketTransaction())
        {
            state.Send(new PacketOpenContainer(1, 0x3C));
            using (state.BeginPacketTransaction(PacketPriority.Highest))
                state.SendPriority(new PacketMoveAck(1, 0));
            state.Send(new PacketOpenContainer(2, 0x3C));
        }
        state.SendPriority(new PacketMoveAck(2, 0));
        Assert.Equal(new byte[] { 0x22, 0x24, 0x22, 0x24 },
            TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InactiveOrClosingStateDoesNotBuildAnyWriter(bool closing)
    {
        var state = closing ? TestHarness.CreateActiveNetState(NullLoggerFactory.Instance, 1)
            : new NetState(NullLogger.Instance);
        if (closing) state.MarkClosing();
        state.Send(new NeverBuild());
        state.Send(new NeverBuild(), PacketPriority.High);
        state.SendPriority(new NeverBuild());
    }
    private sealed class NeverBuild() : PacketWriter(0x22)
    {
        public override PacketBuffer Build() => throw new InvalidOperationException("No recipient");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ItemUseOnlyTurnsThePlayerWhenStationary(bool walking)
    {
        var client = Client();
        var ch = client.Character!;
        ch.Direction = Direction.North;
        client.NetState.WalkSequence = 17;
        if (walking) client.QueueMoveRequest((byte)Direction.North, 17, 0);
        var bag = client.World.CreateItem(); bag.BaseId = 0xE76; bag.ItemType = ItemType.Container;
        client.World.PlaceItem(bag, new Point3D(101, 100, 0, 0));
        client.HandleDoubleClick(bag.Uid.Value);
        Assert.Equal(walking ? 17 : 0, client.NetState.WalkSequence);
        Assert.Equal(walking ? Direction.North : Direction.East, ch.Direction);
        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(packets, p => p.Span[0] == 0x24); // interaction still happens
        if (walking) Assert.DoesNotContain(packets, p => p.Span[0] is 0x20 or 0x78);
        else Assert.Contains(packets, p => p.Span[0] == 0x20);
    }

    [Fact]
    public void ContainerPresyncUsesDisplayOverride()
    {
        var client = Client();
        var pack = client.Character!.Backpack!;
        typeof(Item).GetField("_dispId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pack, (ushort)0xE76);
        client.SendOpenContainer(pack);
        var worn = Assert.Single(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x2E);
        Assert.Equal(0xE76, BinaryPrimitives.ReadUInt16BigEndian(worn.Span[5..]));
    }

    [Theory]
    [InlineData(50000000u, true)] [InlineData(70090000u, true)] [InlineData(70090000u, false)]
    public void LateCorpseObserverGetsContentsBeforeClothing(uint version, bool humanoid)
    {
        PacketPriorityClassifier.Configure(true, 1, null);
        var client = Client(); client.NetState.ClientVersionNumber = version;
        var corpse = client.World.CreateItem(); corpse.BaseId = 0x2006; corpse.ItemType = ItemType.Corpse;
        corpse.Amount = humanoid ? (ushort)0x190 : (ushort)0xC8;
        client.World.PlaceItem(corpse, new Point3D(100, 101, 0, 0));
        foreach (var layer in new[] { Layer.Shirt, Layer.Shirt, Layer.Pack, Layer.Face })
        {
            var item = client.World.CreateItem(); item.BaseId = 0x1517;
            corpse.AddItem(item); item.EquipLayer = layer;
        }
        client.UpdateClientView();
        var packets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        Assert.Contains(packets, p => p.Span[0] == 0x1A);
        if (!humanoid) { Assert.DoesNotContain(packets, p => p.Span[0] is 0x89 or 0x3C); return; }
        var contents = Assert.Single(packets, p => p.Span[0] == 0x3C);
        var equipment = Assert.Single(packets, p => p.Span[0] == 0x89);
        Assert.True(Array.IndexOf(packets, contents) < Array.IndexOf(packets, equipment));
        Assert.Equal(13, equipment.Length); // header + one layer/serial + terminator
        Assert.Equal((byte)Layer.Shirt, equipment.Span[7]);
        Assert.Equal(5 + 2 * (client.NetState.IsClientPost6017 ? 20 : 19), contents.Length);
    }

    [Fact]
    public void LoginSendsOneSelfAppearanceAndOneStatusSnapshot()
    {
        var client = Client(); client.Character!.IsOnline = false;
        typeof(GameClient).GetMethod("EnterWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, null);
        var packets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        foreach (byte opcode in new byte[] { 0x78, 0x2E, 0x11, 0x4F, 0x4E, 0xBC, 0x55 })
            Assert.Single(packets, p => p.Span[0] == opcode);
        Assert.False(client.ViewNeedsRefresh);
        Assert.True(Array.FindIndex(packets, p => p.Span[0] == 0x78) < Array.FindIndex(packets, p => p.Span[0] == 0x55));
    }
}
