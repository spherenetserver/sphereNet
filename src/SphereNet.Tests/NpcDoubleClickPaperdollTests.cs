using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Double-clicking a character goes by its brain group, not its body
/// (Event_DoubleClick, CClientEvent.cpp:2377-2396; GetNPCBrainGroup, CCharStatus.cpp:541).
/// An NPC outside the human group - animal, monster, dragon, berserk - is mounted,
/// opens its pack or gives nothing; everyone else shows the paperdoll, vendors included.
/// </summary>
public sealed class NpcDoubleClickPaperdollTests
{
    private static (GameClient Client, GameWorld World) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7201);
        var me = world.CreateCharacter();
        me.BodyId = 0x0190;
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, world);
    }

    private static bool OpensPaperdoll(NpcBrainType brain, ushort body)
    {
        var (client, world) = Setup();
        var npc = world.CreateCharacter();
        npc.BodyId = body;
        npc.NpcBrain = brain;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleDoubleClick(npc.Uid.Value);
        return TestHarness.GetQueuedPackets(client.NetState).Any(p => p.Span[0] == 0x88);
    }

    [Theory]
    [InlineData(NpcBrainType.Human)]
    [InlineData(NpcBrainType.Vendor)]
    [InlineData(NpcBrainType.Guard)]
    [InlineData(NpcBrainType.Banker)]
    public void ATownspersonShowsThePaperdoll(NpcBrainType brain) =>
        Assert.True(OpensPaperdoll(brain, 0x0190));

    [Theory]
    [InlineData(NpcBrainType.Monster, 0x0190)] // a human-bodied brigand or evil mage
    [InlineData(NpcBrainType.Animal, 0x00D9)]
    [InlineData(NpcBrainType.Dragon, 0x003B)]
    public void ACreatureOutsideTheHumanGroupDoesNot(NpcBrainType brain, ushort body) =>
        Assert.False(OpensPaperdoll(brain, body));

    [Fact]
    public void NoBrainIsGuessedFromTheBody()
    {
        Assert.True(OpensPaperdoll(NpcBrainType.None, 0x0190));
        Assert.False(OpensPaperdoll(NpcBrainType.None, 0x0001));
    }
}
