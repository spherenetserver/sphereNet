using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// [SKILLMENU] selection menus (reference Cmd_Skill_Menu): TEST-gated
/// entries displayed via 0x7C, selection runs the entry's script verbs
/// (POLY/SUMMON/...) against the character.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class SkillMenuTests
{
    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_sm_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private const string MenuScript = """
        [CHARDEF 0d0]
        DEFNAME=c_chicken
        NAME=chicken

        [ITEMDEF 020e9]
        DEFNAME=i_pet_chicken
        NAME=chicken

        [SKILLMENU sm_polymorph]
        Polymorph

        ON=i_pet_chicken Chicken
        TEST=MAGERY 40.0
        POLY c_chicken

        ON=i_pet_chicken Expert Form
        TEST=MAGERY 99.0
        POLY c_chicken
        """;

    private static (GameClient client, Character player) CreateClient()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(1000, 1000, 0, 0));

        var state = TestHarness.CreateActiveNetState(loggerFactory, 1);
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, player);
        return (client, player);
    }

    [Fact]
    public void SkillMenuVerb_OpensMenuAndGatesEntriesByTest()
    {
        LoadDefinitions(MenuScript);
        var (client, player) = CreateClient();
        player.SetSkill(SkillType.Magery, 500); // passes 40.0, fails 99.0

        Assert.True(client.TryExecuteScriptCommand(player, "SKILLMENU", "sm_polymorph", null));

        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x7C);
    }

    [Fact]
    public void SkillMenuChoice_RunsEntryVerbs_PolyChangesBody()
    {
        LoadDefinitions(MenuScript);
        var (client, player) = CreateClient();
        player.SetSkill(SkillType.Magery, 500);
        ushort originalBody = player.BodyId;

        Assert.True(client.TryExecuteScriptCommand(player, "SKILLMENU", "sm_polymorph", null));
        client.HandleMenuChoice(player.Uid.Value, 0, 1, 0); // pick first entry

        Assert.Equal(0x00D0, player.BodyId);
        Assert.True(player.IsStatFlag(StatFlag.Polymorph));
        Assert.NotEqual(originalBody, player.BodyId);
    }

    [Fact]
    public void SkillMenuVerb_UnknownMenu_ReturnsFalse()
    {
        LoadDefinitions(MenuScript);
        var (client, player) = CreateClient();

        Assert.False(client.TryExecuteScriptCommand(player, "SKILLMENU", "sm_does_not_exist", null));
    }

    [Fact]
    public void PolyVerb_ResolvesWeightedDefnameList()
    {
        LoadDefinitions(MenuScript);
        var (client, player) = CreateClient();

        Assert.True(player.TryExecuteCommand("POLY", "{ c_chicken 1 c_chicken 1 }", client));
        Assert.Equal(0x00D0, player.BodyId);
        Assert.True(player.IsStatFlag(StatFlag.Polymorph));
    }
}
