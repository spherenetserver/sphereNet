using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A crafting tool opens the pack's own menu, as Source-X CClient::Skill_Menu does
/// (CClientUse.cpp:642): @SkillMenu runs with LOCAL.SkillMenu / Skill / ItemUsed and
/// may answer 1 or name another menu, then the [SKILLMENU] of that name is shown. The
/// trigger used to receive only the skill number - a pack comparing LOCAL.SKILLMENU
/// never matched - and the engine's own recipe list was opened instead of the pack's
/// menu. Menu entries are built as CMenuItem::ParseLine builds them: the item's display
/// id, and text parsed against the item (&lt;name&gt;, &lt;resmake&gt;).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftingSkillMenuTests
{
    private const string Script = """
        [ITEMDEF 01bef]
        DEFNAME=i_ingot_iron
        NAME=iron ingot%s

        [ITEMDEF 01053]
        DEFNAME=i_gears
        NAME=gears
        RESOURCES=1 i_ingot_iron
        SKILLMAKE=Tinkering 14.7

        [ITEMDEF 0f39]
        DEFNAME=i_shovel
        NAME=shovel
        RESOURCES=4 i_ingot_iron

        [SKILLMENU sm_tinker]
        Tinkering

        ON=i_gears <name> (<resmake>)
        MAKEITEM=i_gears

        [SKILLMENU sm_other]
        Other

        ON=i_shovel <name>
        MAKEITEM=i_shovel
        """;

    private static void LoadDefinitions()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_craftmenu_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Script);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static (GameClient Client, Character Player, TriggerDispatcher Triggers, GameWorld World) Setup()
    {
        LoadDefinitions();
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1701);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var triggers = new TriggerDispatcher();
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: triggers);
        return (client, player, triggers, world);
    }

    private static (string Title, List<(ushort Model, string Text)> Entries)? LastMenu(GameClient client)
    {
        var p = TestHarness.GetQueuedPackets(client.NetState).LastOrDefault(x => x.Span.Length > 0 && x.Span[0] == 0x7C);
        if (p == null) return null;
        var b = p.Span.ToArray();
        int o = 9;
        int titleLen = b[o++];
        string title = Encoding.ASCII.GetString(b, o, titleLen);
        o += titleLen;
        int count = b[o++];
        var entries = new List<(ushort, string)>();
        for (int i = 0; i < count; i++)
        {
            ushort model = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
            int len = b[o + 4];
            entries.Add((model, Encoding.ASCII.GetString(b, o + 5, len)));
            o += 5 + len;
        }
        return (title, entries);
    }

    [Fact]
    public void TinkerToolsOpenThePacksSkillMenuWithItemModelsAndParsedText()
    {
        var (client, _, _, _) = Setup();

        client.OpenCraftingGump(SkillType.Tinkering);

        var menu = LastMenu(client);
        Assert.NotNull(menu);
        Assert.Equal("Tinkering", menu!.Value.Title);
        var entry = Assert.Single(menu.Value.Entries);
        // A numeric ITEMDEF without DISPID shows its own graphic, not 0.
        Assert.Equal(0x1053, entry.Model);
        Assert.Equal("gears (1 iron ingot)", entry.Text);
    }

    [Fact]
    public void SkillMenuTriggerSeesTheMenuAndCanNameAnother()
    {
        var (client, _, triggers, _) = Setup();
        string? seenMenu = null;
        long seenSkill = -1;
        triggers.RegisterCharEvent("EVENTSPLAYER", "SkillMenu", (_, a) =>
        {
            seenMenu = a.Locals?.Get("SkillMenu");
            seenSkill = a.Locals?.GetInt("Skill") ?? -1;
            a.Locals?.Set("SkillMenu", "sm_other");
            return TriggerResult.Default;
        });

        client.OpenCraftingGump(SkillType.Tinkering);

        Assert.Equal("sm_tinker", seenMenu);
        Assert.Equal((long)SkillType.Tinkering, seenSkill);
        Assert.Equal("Other", LastMenu(client)?.Title);
    }

    [Fact]
    public void SkillMenuTriggerReturningOneOpensNothing()
    {
        var (client, _, triggers, _) = Setup();
        triggers.RegisterCharEvent("EVENTSPLAYER", "SkillMenu", (_, _) => TriggerResult.True);

        client.OpenCraftingGump(SkillType.Tinkering);

        Assert.Null(LastMenu(client));
    }

    /// <summary>IBC_RESMAKE (CItemBase.cpp:1336): the RESOURCES list by name, plural
    /// above one (CResourceQtyArray::WriteNames).</summary>
    [Fact]
    public void ResMakeReadsTheResourcesByName()
    {
        var (_, _, _, world) = Setup();
        var gears = world.CreateItem();
        gears.BaseId = 0x1053;
        var shovel = world.CreateItem();
        shovel.BaseId = 0x0F39;

        Assert.True(gears.TryGetProperty("RESMAKE", out string one));
        Assert.Equal("1 iron ingot", one);
        Assert.True(shovel.TryGetProperty("RESMAKE", out string four));
        Assert.Equal("4 iron ingots", four);
    }
}
