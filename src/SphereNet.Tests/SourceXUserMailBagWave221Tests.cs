using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXUserMailBagWave221Tests
{
    [Fact]
    public void PacketMailMessage_IsRegisteredAndRoutesBothSerials()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var network = new NetworkManager(1, loggerFactory);
        uint receivedTarget = 0;
        uint receivedAttachment = 0;
        network.SetHandlers(mailMessage: (_, target, attachment) =>
        {
            receivedTarget = target;
            receivedAttachment = attachment;
        });

        var handler = Assert.IsType<PacketMailMessage>(network.Packets.GetHandler(0xBB));
        Assert.Equal(9, handler.ExpectedLength);
        handler.OnReceive(new PacketBuffer([
            0x01, 0x02, 0x03, 0x04,
            0xA1, 0xA2, 0xA3, 0xA4,
        ]), network.GetState(0)!);

        Assert.Equal(0x01020304u, receivedTarget);
        Assert.Equal(0xA1A2A3A4u, receivedAttachment);
    }

    [Fact]
    public void HandleMailMessage_FiresRecipientTriggerWithSenderAndNotifiesRecipient()
    {
        var (client, sender, target, triggers) = CreateStack();
        Character? firedOn = null;
        Character? source = null;
        triggers.RegisterCharEvent("EVENTSPLAYER", "UserMailBag", (obj, args) =>
        {
            firedOn = Assert.IsType<Character>(obj);
            source = args.CharSrc;
            Assert.Same(client, args.ScriptConsole);
            return TriggerResult.Default;
        });
        Serial recipient = Serial.Zero;
        PacketWriter? sent = null;
        client.SendToChar = (uid, packet) => (recipient, sent) = (uid, packet);

        client.HandleMailMessage(target.Uid.Value, 0x40000001);

        Assert.Same(target, firedOn);
        Assert.Same(sender, source);
        Assert.Equal(target.Uid, recipient);
        var speech = Assert.IsType<PacketSpeechUnicodeOut>(sent);
        var buffer = speech.Build();
        buffer.Position = 48;
        Assert.Equal("'Courier' has dropped mail on you.", buffer.ReadUnicodeNullBE());
    }

    [Fact]
    public void HandleMailMessage_ReturnTrueSuppressesRecipientNotification()
    {
        var (client, _, target, triggers) = CreateStack();
        triggers.RegisterCharEvent("EVENTSPLAYER", "UserMailBag", (_, _) => TriggerResult.True);
        bool sent = false;
        client.SendToChar = (_, _) => sent = true;

        client.HandleMailMessage(target.Uid.Value, 0);

        Assert.False(sent);
    }

    [Fact]
    public void HandleMailMessage_InvalidTargetWarnsSenderAndSelfDropIsSilent()
    {
        var (client, sender, _, _) = CreateStack();
        int directSends = 0;
        client.SendToChar = (_, _) => directSends++;

        client.HandleMailMessage(0x00ABCDEF, 0);
        int queuedAfterInvalid = TestHarness.GetQueuedPackets(client.NetState).Count;
        client.HandleMailMessage(sender.Uid.Value, 0);

        Assert.Equal(1, queuedAfterInvalid);
        Assert.Equal(queuedAfterInvalid, TestHarness.GetQueuedPackets(client.NetState).Count);
        Assert.Equal(0, directSends);
    }

    private static (GameClient Client, Character Sender, Character Target, TriggerDispatcher Triggers) CreateStack()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var sender = world.CreateCharacter();
        sender.Name = "Courier";
        sender.IsPlayer = true;
        world.PlaceCharacter(sender, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Name = "Recipient";
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var client = TestHarness.CreateClient(
            loggerFactory, world, new AccountManager(loggerFactory), id: 221);
        TestHarness.AttachCharacter(client, sender);
        var triggers = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: triggers);
        return (client, sender, target, triggers);
    }
}
