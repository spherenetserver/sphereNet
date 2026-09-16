using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// A guild or town stone answers for its own record: ABBREV, ALIGN, MASTERUID and
/// WEBPAGE all live on the stone upstream (CItemStone_props.tbl).
///
/// The write side of these already existed - a classic save carries them as stone
/// lines and the importer had to accept them - but nothing read them back. Every
/// stone dialog that compares MASTERUID to SRC.UID was therefore comparing a uid
/// against an empty answer, which refuses the master along with everyone else, and
/// the guild list showed no abbreviations at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GuildStonePropertyTests
{
    private static (GameWorld World, Item Stone, GuildDef Guild) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        var guild = new GuildDef(stone.Uid);
        Item.ResolveGuild = uid => uid == stone.Uid ? guild : null;
        return (world, stone, guild);
    }

    [Fact]
    public void TheStoneAnswersItsAbbreviationAndWebPage()
    {
        var (_, stone, guild) = Setup();
        guild.Abbreviation = "AMP";
        guild.WebUrl = "http://example.com/guild";

        Assert.True(stone.TryGetProperty("ABBREV", out string abbrev));
        Assert.Equal("AMP", abbrev);
        Assert.True(stone.TryGetProperty("WEBPAGE", out string web));
        Assert.Equal("http://example.com/guild", web);
    }

    [Fact]
    public void AlignmentIsReadAsItsNumber()
    {
        var (_, stone, guild) = Setup();
        guild.Align = GuildAlign.Chaos;

        Assert.True(stone.TryGetProperty("ALIGN", out string align));
        Assert.Equal(((int)GuildAlign.Chaos).ToString(), align);
    }

    [Fact]
    public void MasterUidIsTheMastersUid()
    {
        var (world, stone, guild) = Setup();
        var master = world.CreateCharacter();
        world.PlaceCharacter(master, new Point3D(101, 100, 0, 0));
        guild.JoinAsMember(master.Uid);
        guild.SetMaster(master.Uid);

        Assert.True(stone.TryGetProperty("MASTERUID", out string uid));
        Assert.Equal($"0{master.Uid.Value:X}", uid);
    }

    [Fact]
    public void AStoneWithNoMasterAnswersZero()
    {
        // Not an empty string: a dialog comparing MASTERUID to SRC.UID would read
        // empty as a match against every other failed lookup.
        var (_, stone, _) = Setup();
        Assert.True(stone.TryGetProperty("MASTERUID", out string uid));
        Assert.Equal("0", uid);
    }

    [Fact]
    public void AnOrdinaryItemDoesNotAnswerStoneKeys()
    {
        var (world, _, _) = Setup();
        var crate = world.CreateItem();
        world.PlaceItem(crate, new Point3D(105, 100, 0, 0));

        Assert.False(crate.TryGetProperty("ABBREV", out _));
        Assert.False(crate.TryGetProperty("MASTERUID", out _));
    }
}
