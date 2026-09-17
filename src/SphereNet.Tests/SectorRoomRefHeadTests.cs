using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

/// <summary>
/// SECTOR is a reference head on every object.
///
/// Upstream lists it on CObjBase beside TOPOBJ and ROOM (sm_szRefKeys,
/// CObjBase.cpp:899), so &lt;SRC.SECTOR.ISNIGHTTIME&gt; walks to the sector the
/// character stands in and asks it. Sector already answered for its own keys here -
/// ISNIGHTTIME, ISDARK, LOCALTIME, CLIENTS and the rest - and nothing could reach it
/// from an object, so the shipped pack's
///
///     IF (&lt;SRC.SECTOR.ISNIGHTTIME&gt;)
///
/// read as nothing and the night branch behind it never ran.
///
/// ROOM, the other head on that table, was already answered: the interpreter resolves
/// ROOM.&lt;key&gt; through the host ahead of the object read. It is a reference head
/// here too, for the CALL path, but deliberately NOT a second read path.
/// </summary>
public sealed class SectorRoomRefHeadTests
{
    private static (SphereNet.Game.World.GameWorld World,
                    SphereNet.Game.Objects.Characters.Character Ch) Build()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var room = new Room { Name = "probe room", MapIndex = 0 };
        room.AddRect(90, 90, 120, 120);
        world.AddRoom(room);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    /// <summary>The read the pack makes, through the interpreter. Whichever hour it
    /// is the sector answers 0 or 1 - the point is that something answers, where
    /// before the whole expression collapsed to the unresolved "0".
    ///
    /// The two reads are joined with a comma rather than an operator on purpose. A
    /// value made only of numbers and math separators is a number upstream
    /// (IsSimpleNumberString counts "+-\*~|&amp;!%^()" and '/'), so "0|1" would be
    /// stored as the 1 it works out to; a comma is not one of them, so the pair
    /// survives as text and each half can still be read back.</summary>
    [Fact]
    public void ACharacterReadsItsSectorThroughTheInterpreter()
    {
        var (_, ch) = Build();
        var stack = ScriptTestBootstrap.CreateRuntimeStack();

        stack.Interpreter.Execute(
            [new ScriptKey("TAG.OUT", "<SECTOR.ISNIGHTTIME>,<SECTOR.ISDARK>")],
            ch, null, new TriggerArgs(), new ScriptScope());

        Assert.True(ch.TryGetProperty("TAG.OUT", out string v));
        string[] parts = v.Split(',');
        Assert.Equal(2, parts.Length);
        Assert.All(parts, p => Assert.True(p is "0" or "1", $"expected the sector to answer, got '{p}'"));
    }

    /// <summary>Straight off the object, which is where the read lands.</summary>
    [Fact]
    public void AnObjectAnswersForItsSectorKeys()
    {
        var (world, ch) = Build();
        Assert.True(ch.TryGetProperty("SECTOR.ISNIGHTTIME", out string night));
        Assert.True(night is "0" or "1");

        // An item on the ground reaches it the same way: the head belongs to every
        // object, not just to characters.
        var it = world.CreateItem();
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        Assert.True(it.TryGetProperty("SECTOR.ISDARK", out string dark));
        Assert.True(dark is "0" or "1");
    }

    /// <summary>A contained item asks about where the TOP of its stack stands, the way
    /// upstream reads it (GetTopLevelObj before the lookup) - a sword in a backpack is
    /// in whatever sector its owner is.</summary>
    [Fact]
    public void AContainedItemAsksAboutItsHoldersPosition()
    {
        var (world, ch) = Build();
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);

        var inside = world.CreateItem();
        Assert.True(pack.TryAddItem(inside));

        Assert.True(inside.TryGetProperty("SECTOR.ISNIGHTTIME", out string v));
        Assert.True(v is "0" or "1");
        ch.TryGetProperty("SECTOR.ISNIGHTTIME", out string owners);
        Assert.Equal(owners, v);
    }

    /// <summary>A key the sector does not answer reads back empty rather than
    /// throwing or falling into the object's own namespace.</summary>
    [Fact]
    public void AnUnknownSectorKeyIsEmpty()
    {
        var (_, ch) = Build();
        Assert.True(ch.TryGetProperty("SECTOR.NOTHING_ANSWERS_THIS", out string v));
        Assert.Equal("", v);
    }

    /// <summary>Both heads resolve as references, which is what the CALL path walks -
    /// upstream puts them on the same table.</summary>
    [Fact]
    public void BothHeadsResolveAsReferences()
    {
        var (world, ch) = Build();
        Assert.NotNull(ch.ResolveScriptRefHead("SECTOR"));
        Assert.NotNull(ch.ResolveScriptRefHead("ROOM"));

        var it = world.CreateItem();
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        Assert.NotNull(it.ResolveScriptRefHead("SECTOR"));
        Assert.NotNull(it.ResolveScriptRefHead("ROOM"));

        // Most of the map has no ROOMDEF over it, and that is an absent reference
        // rather than an error.
        var far = world.CreateItem();
        world.PlaceItem(far, new Point3D(200, 200, 0, 0));
        Assert.Null(far.ResolveScriptRefHead("ROOM"));
    }
}
