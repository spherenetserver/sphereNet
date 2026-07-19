using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SourceXClientVerbWave219Tests
{
    private static (GameClient Client, Character Character, CommandHandler Commands) CreateClient()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Name = "Wave219";
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Admin;
        world.PlaceCharacter(ch, new Point3D(500, 500, 0, 0));

        var commands = new CommandHandler
        {
            Resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        };
        commands.RegisterDefaults(world);
        var client = TestHarness.CreateClient(
            loggerFactory, world, new AccountManager(loggerFactory), id: 219);
        TestHarness.AttachCharacter(client, ch);
        client.SetEngines(commands: commands, skillHandlers: new SkillHandlers(world));
        return (client, ch, commands);
    }

    [Fact]
    public void ClosePaperdoll_SendsCloseUiWindowTypeOne()
    {
        var (client, ch, _) = CreateClient();

        Assert.True(client.TryExecuteScriptCommand(ch, "CLOSEPAPERDOLL", "", null));

        var packet = TestHarness.GetQueuedPackets(client.NetState)
            .Single(p => p.Span.Length == 13 && p.Span[0] == 0xBF);
        Assert.Equal((ushort)0x0016, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[3..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(packet.Span[5..]));
        Assert.Equal(ch.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(packet.Span[9..]));
    }

    [Theory]
    [InlineData("ADD")]
    [InlineData("ADDCHAR")]
    [InlineData("ADDITEM")]
    public void AddVerbs_ReuseTheExistingAddTargetCursor(string verb)
    {
        var (client, ch, _) = CreateClient();

        Assert.True(client.TryExecuteScriptCommand(ch, verb, "c_orc,5", null));

        Assert.True(client.Targets.CursorActive);
        Assert.Equal("c_orc", client.Targets.AddToken);
        Assert.Equal((ushort)5, client.Targets.AddAmount);
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0x6C);
    }

    [Fact]
    public void AddItem_CarriesStackAmountThroughTargetResponse()
    {
        var (client, ch, _) = CreateClient();
        Assert.True(client.TryExecuteScriptCommand(ch, "ADDITEM", "0x0EED,5", null));

        client.Targeting.HandleTargetResponse(0, client.ActiveTargetCursorId, 0, 650, 651, 2, 0);

        var created = client.World.FindItem(client.World.LastNewItem);
        Assert.NotNull(created);
        Assert.Equal((ushort)0x0EED, created!.BaseId);
        Assert.Equal((ushort)5, created.Amount);
        Assert.Equal(new Point3D(650, 651, 2, 0), created.Position);
    }

    [Fact]
    public void CTagList_EmitsSessionTagsToClient()
    {
        var (client, ch, _) = CreateClient();
        ch.CTags.Set("Dialog.Page", "4");

        Assert.True(client.TryExecuteScriptCommand(ch, "CTAGLIST", "", null));

        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), p => p.Span[0] == 0xAE);
    }

    [Fact]
    public void SaveAndGmPage_ReuseCommandHandlerEvents()
    {
        var (client, ch, commands) = CreateClient();
        bool saveRequested = false;
        string? pageText = null;
        commands.OnSaveCommand += () => saveRequested = true;
        commands.OnPageReceived += (_, text) => pageText = text;

        Assert.True(client.TryExecuteScriptCommand(ch, "SAVE", "", null));
        Assert.True(client.TryExecuteScriptCommand(ch, "GMPAGE", "ADD stuck under bridge", null));

        Assert.True(saveRequested);
        Assert.Equal("stuck under bridge", pageText);
    }

    [Fact]
    public void Self_FeedsOwnCharacterIntoActiveTargetCallback()
    {
        var (client, ch, _) = CreateClient();
        uint selected = 0;
        client.SetPendingTarget((serial, _, _, _, _) => selected = serial);

        Assert.True(client.TryExecuteScriptCommand(ch, "SELF", "", null));

        Assert.Equal(ch.Uid.Value, selected);
        Assert.False(client.Targets.CursorActive);
    }

    [Fact]
    public void SkillSelect_UsesNormalClientSkillPipeline()
    {
        var (client, ch, _) = CreateClient();

        Assert.True(client.TryExecuteScriptCommand(ch, "SKILLSELECT", "Hiding", null));

        Assert.True(ch.HasActiveSkillPending() || ch.IsStatFlag(StatFlag.Hidden));
        if (ch.HasActiveSkillPending())
            Assert.Equal((int)SkillType.Hiding, ch.SkillPendingId);
    }

    [Fact]
    public void InformationVersionAndResend_AreLiveClientRoutes()
    {
        var (client, ch, _) = CreateClient();

        Assert.True(client.TryExecuteScriptCommand(ch, "INFORMATION", "", null));
        Assert.True(client.TryExecuteScriptCommand(ch, "VERSION", "", null));
        Assert.True(client.TryExecuteScriptCommand(ch, "RESEND", "", null));

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        Assert.True(packets.Count(p => p.Span[0] == 0xAE) >= 3);
        Assert.Contains(packets, p => p.Span[0] == 0x20);
    }
}
