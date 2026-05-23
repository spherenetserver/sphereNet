using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Execution;

namespace SphereNet.Tests;

[Collection("GlobalConfigSerial")]
public class DeferredParityTests
{
    [Fact]
    public void PacketNewMovementRequest_RoutesSixBytePayloadToMoveHandler()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        byte capturedDir = 0;
        byte capturedSeq = 0;
        uint capturedKey = 0;
        state.MoveRequestHandler = (_, dir, seq, key) =>
        {
            capturedDir = dir;
            capturedSeq = seq;
            capturedKey = key;
        };

        var handler = new PacketNewMovementRequest();
        var buf = new PacketBuffer(new byte[] { 0x04, 5, 0, 0, 0x30, 0x39 });

        handler.OnReceive(buf, state);

        Assert.Equal(0x04, capturedDir);
        Assert.Equal((byte)5, capturedSeq);
        Assert.Equal(12345u, capturedKey);
    }

    [Fact]
    public void PacketNewMovementRequest_IgnoresExtensionSubcommands()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var state = TestHarness.CreateActiveNetState(loggerFactory, 2);
        bool called = false;
        state.MoveRequestHandler = (_, _, _, _) => called = true;

        var handler = new PacketNewMovementRequest();
        var buf = new PacketBuffer(new byte[] { 0xFF });

        handler.OnReceive(buf, state);
        Assert.False(called);
    }

    [Fact]
    public void PacketBoatSmoothMove_BuildsExpectedPayload()
    {
        var packet = new PacketBoatSmoothMove(0x40000001, 4, 2, 2, 1500, 1600, 5);
        var buf = packet.Build();

        Assert.Equal(0xF6, buf.Data[0]);
        Assert.Equal(0x40, buf.Data[3]);
        Assert.Equal(0x00, buf.Data[4]);
        Assert.Equal(0x00, buf.Data[5]);
        Assert.Equal(0x01, buf.Data[6]);
        Assert.Equal(4, buf.Data[7]);
    }

    [Fact]
    public void TriggerDispatcher_FiresGenericGlobalItemFunction()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_global_item_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onitem_dclick]\nTAG.GLOBAL_DCLICK=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        var item = new SphereNet.Game.Objects.Items.Item();

        var result = dispatcher.FireItemTrigger(item, ItemTrigger.DClick, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(item.TryGetProperty("TAG.GLOBAL_DCLICK", out var val));
        Assert.Equal("1", val);
    }

    [Fact]
    public void HousingEngine_PlaceHouse_EnforcesAccountHouseLimit()
    {
        var world = TestHarness.CreateWorld();
        var registry = new MultiRegistry();
        var multi = new MultiDef { Id = 0x0064, Name = "small test house" };
        multi.Components.Add(new MultiComponent
        {
            TileId = 0x0001,
            DeltaX = 0,
            DeltaY = 0,
            DeltaZ = 0,
            Visible = true
        });
        multi.RecalcBounds();
        registry.Register(multi);

        var engine = new HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = 10,
            MaxHousesPerAccount = 1
        };

        var account = new Account { Name = "testacct" };
        var ownerA = world.CreateCharacter();
        var ownerB = world.CreateCharacter();
        ownerA.SetTag("ACCOUNT", account.Name);
        ownerB.SetTag("ACCOUNT", account.Name);
        account.SetCharSlot(0, ownerA.Uid);
        account.SetCharSlot(1, ownerB.Uid);

        Character.ResolveAccountForChar = uid =>
        {
            if (uid == ownerA.Uid || uid == ownerB.Uid)
                return account;
            return null;
        };

        world.PlaceCharacter(ownerA, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(ownerB, new Point3D(200, 200, 0, 0));

        var first = engine.PlaceHouse(ownerA, 0x0064, new Point3D(120, 120, 0, 0));
        var second = engine.PlaceHouse(ownerB, 0x0064, new Point3D(140, 140, 0, 0));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, engine.GetHouseCountForAccount(ownerA));
    }

    [Fact]
    public void GameClient_HandleExtendedCommand_UpdatesScreenSize()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accounts, 3);
        var player = world.CreateCharacter();
        TestHarness.AttachCharacter(client, player);

        client.HandleExtendedCommand(0x0005, [0, 0, 0, 0, 0x04, 0x00, 0x03, 0x20]);
        Assert.True(player.TryGetProperty("SCREENSIZE.X", out var width));
        Assert.True(player.TryGetProperty("SCREENSIZE.Y", out var height));
        Assert.Equal("1024", width);
        Assert.Equal("800", height);

        client.HandleExtendedCommand(0x001C, [0x05, 0x00, 0x02, 0xD4]);
        Assert.True(player.TryGetProperty("SCREENSIZE.X", out width));
        Assert.True(player.TryGetProperty("SCREENSIZE.Y", out height));
        Assert.Equal("1280", width);
        Assert.Equal("724", height);
    }
}
