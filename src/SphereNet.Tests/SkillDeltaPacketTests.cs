using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SkillDeltaPacketTests
{
    [Theory]
    [InlineData(501, 0)]
    [InlineData(499, 1)]
    public void SingleUpdatePreservesClientDeltaAndSkillFields(ushort newBase, byte skillLock)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 8991);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        TestHarness.AttachCharacter(client, player);
        const SkillType skill = SkillType.Magery;
        player.SetSkill(skill, 500);
        player.SetTag($"SkillMod{(int)skill}", "25");
        player.SetTag($"OVERRIDE.SKILLCAP_{(int)skill}", "1200");
        client.SendSkillList();
        var initial = Assert.Single(TestHarness.GetQueuedPackets(client.NetState));
        Assert.Equal(0x02, initial.Span[3]);
        TestHarness.ClearQueuedPackets(client.NetState);

        player.SetSkill(skill, newBase);
        player.SetSkillLock(skill, skillLock);
        client.SendSkillUpdate(skill);

        var packet = Assert.Single(TestHarness.GetQueuedPackets(client.NetState));
        Assert.Equal(0x3A, packet.Span[0]);
        // ClassicUO only prints the blue gain/loss message for a single update.
        Assert.Equal(0xDF, packet.Span[3]);
        Assert.Equal(13, packet.Length);
        Assert.Equal((ushort)skill, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[4..]));
        Assert.Equal(newBase + 25, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[6..]));
        Assert.Equal(newBase, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[8..]));
        Assert.Equal(skillLock, packet.Span[10]);
        Assert.Equal(1200, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[11..]));
    }
}
