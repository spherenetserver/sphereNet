using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// SKILLMENU and MOVETO, pinned because the pack-member sweep listed both as
/// unanswered and neither was: MOVETO is the P setter under its other name
/// (OV_MOVETO shares OV_P's case, CObjBase.cpp:2489) and SKILLMENU opens a
/// [SKILLMENU] section. Both simply refused the sweep's probe ARGUMENT, which is
/// a different thing from not knowing the name - and the difference is what these
/// tests hold in place, so neither gets implemented a second time.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MoveToAndSkillMenuVerbTests
{
    private sealed class Console : SphereNet.Core.Interfaces.ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public SphereNet.Core.Interfaces.IScriptObj? GetSourceChar() => null;
    }

    [Fact]
    public void MoveToPutsTheObjectWhereItSays()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(ch.TryExecuteCommand("MOVETO", "120,130,5,0", new Console()));
        Assert.Equal(120, ch.X);
        Assert.Equal(130, ch.Y);
        Assert.Equal(5, ch.Z);
    }

    [Fact]
    public void AnUnparseableDestinationIsRefusedButTheNameIsStillKnown()
    {
        // The exact shape that made the sweep call this a gap: the verb answers
        // false for a destination it cannot read, while still owning the name.
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.False(ch.TryExecuteCommand("MOVETO", "not-a-point", new Console(), out bool owned));
        Assert.True(owned);
        Assert.Equal(100, ch.X);
    }

    [Fact]
    public void SkillMenuOpensTheSectionItNames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_sm_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [SKILLMENU sm_probe]
            Make an item
            ON=0 A dagger
            MAKEITEM=0

            """);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(path) ?? "" };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 4901);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        Assert.True(client.TryExecuteScriptCommand(me, "SKILLMENU", "sm_probe", null));

        // 0x7C - the menu the client is shown.
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span.Length > 0 && p.Span[0] == 0x7C);

        try { File.Delete(path); } catch (IOException) { }
    }

    [Fact]
    public void AnUnknownMenuNameOpensNothing()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 4902);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        Assert.False(client.TryExecuteScriptCommand(me, "SKILLMENU", "sm_no_such_menu", null));
    }
}
