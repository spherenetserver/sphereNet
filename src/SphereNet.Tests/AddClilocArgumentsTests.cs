using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// A cliloc's arguments are TAB separated, and ADDCLILOC writes them with commas.
///
/// Upstream splits every comma and joins the rest with a tab, putting a single space
/// where an argument is empty or the literal word NULL (OV_ADDCLILOC,
/// CObjBase.cpp:2160). This engine split into TWO parts and passed the remainder
/// through untouched, so a multi-argument cliloc got its whole tail as argument one:
///
///     ADDCLILOC 1060658,Access,Owner Only
///
/// is "~1_val~: ~2_val~", and it rendered as "Access,Owner Only" in the first slot
/// with the second empty rather than "Access: Owner Only". The packs write 27 such
/// lines, on the house sign and on equipment; the engine's own tooltip code has always
/// used tabs for the same clilocs, which is what made the mismatch invisible from
/// inside.
/// </summary>
public sealed class AddClilocArgumentsTests
{
    /// <summary>Run one ADDCLILOC line through the script console and hand back what
    /// landed in the tooltip list.</summary>
    private static (uint Cliloc, string Args) Add(string arguments)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5100);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        var list = new List<(uint ClilocId, string Args)>();
        ((SphereNet.Game.Clients.IClientContext)client).ScriptTooltipProperties = list;

        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Assert.True(client.TryExecuteScriptCommand(item, "ADDCLILOC", arguments, null));

        Assert.Single(list);
        return list[0];
    }

    /// <summary>The house sign line: two arguments, tab separated.</summary>
    [Fact]
    public void TwoArgumentsAreTabSeparated()
    {
        var (cliloc, args) = Add("1060658,Access,Owner Only");
        Assert.Equal(1060658u, cliloc);
        Assert.Equal("Access\tOwner Only", args);
    }

    /// <summary>Three, from the equipment tooltip.</summary>
    [Fact]
    public void ThreeArgumentsAreTabSeparated()
    {
        var (_, args) = Add("1041522,red,#1038000,white");
        Assert.Equal("red\t#1038000\twhite", args);
    }

    /// <summary>One argument comes through as itself, which is the common case.</summary>
    [Fact]
    public void ASingleArgumentIsUnchanged()
    {
        var (cliloc, args) = Add("1070722, Demolition Scheduled");
        Assert.Equal(1070722u, cliloc);
        Assert.Equal("Demolition Scheduled", args);
    }

    /// <summary>And no argument at all is an empty string, not a stray tab.</summary>
    [Fact]
    public void NoArgumentIsEmpty()
    {
        var (cliloc, args) = Add("1018322");
        Assert.Equal(1018322u, cliloc);
        Assert.Equal("", args);
    }

    /// <summary>An empty or NULL argument becomes a single space, so the arguments
    /// after it keep their slots instead of shifting up one.</summary>
    [Fact]
    public void AnEmptyArgumentHoldsItsSlot()
    {
        Assert.Equal(" \tsecond", Add("1060658,,second").Args);
        Assert.Equal(" \tsecond", Add("1060658,NULL,second").Args);
        Assert.Equal("first\t ", Add("1060658,first,").Args);
    }
}
