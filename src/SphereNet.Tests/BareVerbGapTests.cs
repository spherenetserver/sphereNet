using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The verbs the packs write as bare STATEMENTS that nothing owned.
///
/// The member sweep measures OBJ.NAME and cannot see a verb on a line of its own -
/// which is most verbs. Sweeping those separately found two the engine never had.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BareVerbGapTests
{
    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>CLEARTAGS with no argument clears every tag
    /// (CObjBase.cpp:2122 -> CVarDefMap::ClearKeys with an empty mask).</summary>
    [Fact]
    public void ClearTagsWithNoArgumentClearsThemAll()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        item.SetTag("ONE", "1");
        item.SetTag("TWO", "2");
        Assert.True(item.TryGetProperty("TAGCOUNT", out string before));
        Assert.Equal("2", before);

        Assert.True(item.TryExecuteCommand("CLEARTAGS", "", new Console(), out bool owned));
        Assert.True(owned);
        Assert.True(item.TryGetProperty("TAGCOUNT", out string after));
        Assert.Equal("0", after);
    }

    /// <summary>With an argument it deletes the tags whose key CONTAINS it. Upstream
    /// matches with strstr on both lowercased (CVarDefMap.cpp:643), so it is a
    /// substring test and not a prefix - a chest clearing "Player" that way also
    /// clears OLDPLAYER, and getting that backwards would leave tags behind.</summary>
    [Fact]
    public void ClearTagsWithAMaskDeletesEveryKeyContainingIt()
    {
        var world = NewWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));

        ch.SetTag("PLAYER", "a");
        ch.SetTag("PLAYER_LAST", "b");
        ch.SetTag("OLDPLAYER", "c");      // contains it, but does not start with it
        ch.SetTag("SOMETHINGELSE", "d");

        Assert.True(ch.TryExecuteCommand("CLEARTAGS", "Player", new Console(), out _));

        Assert.False(ch.TryGetTag("PLAYER", out _));
        Assert.False(ch.TryGetTag("PLAYER_LAST", out _));
        Assert.False(ch.TryGetTag("OLDPLAYER", out _));
        Assert.True(ch.TryGetTag("SOMETHINGELSE", out _));
    }

    /// <summary>The mask is case-insensitive, as upstream's own lowercasing of both
    /// sides makes it.</summary>
    [Fact]
    public void ClearTagsIgnoresCase()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        item.SetTag("DelayBola", "1");
        Assert.True(item.TryExecuteCommand("CLEARTAGS", "delay", new Console(), out _));
        Assert.False(item.TryGetTag("DelayBola", out _));
    }

    /// <summary>A mask matching nothing leaves the tags alone - it is not a
    /// "clear everything" fallback.</summary>
    [Fact]
    public void ClearTagsWithAMaskThatMatchesNothingKeepsEveryTag()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));

        item.SetTag("KEEP", "1");
        Assert.True(item.TryExecuteCommand("CLEARTAGS", "nosuchthing", new Console(), out _));
        Assert.True(item.TryGetTag("KEEP", out _));
    }

    /// <summary>FLUSH pushes this client's queued packets out now (CV_FLUSH,
    /// CClient.cpp:1509). The live pack writes it before work that will take
    /// time, so the player sees what was queued before it rather than after.</summary>
    [Fact]
    public void FlushIsOwnedByTheClientAndSendsWhatIsQueued()
    {
        var world = NewWorld();
        using var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4977);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(10, 10, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        Assert.True(client.TryExecuteScriptCommand(ch, "FLUSH", "", null));
    }
}
