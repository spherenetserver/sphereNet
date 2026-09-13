using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The same duplication contract, through each door it has (port plan İŞ-59 /
/// PLAN-201).
///
/// PLAN-201 asks for the 13A-13I fixes to be re-verified PER ENTRY POINT - direct
/// native call, interpreter, SERV path, character path, host wiring - because a
/// contract honoured at one door and forgotten at the next is the failure that
/// survives a green suite. İŞ-52 found exactly that shape once already.
///
/// The contract here is what a duplication leaves behind for the script that asked
/// for it. Upstream:
///
///   an ITEM dupe goes through CreateDupeItem, which sets both the caller's ACT and
///   the world's NEW to the copy (CItem.cpp:395-399);
///
///   a CHARACTER dupe ends DupeFrom by setting NEW to the new character, and the
///   comment there says why - the equipment dupes ran in between and left NEW
///   pointing at the last duplicated ITEM (CChar.cpp:1274-1275);
///
///   NEWDUPE runs the object's own DUPE verb and then copies NEW into the caller's
///   ACT (CScriptObj.cpp:1321-1338), so both end up on the copy rather than the
///   source it was told to duplicate.
///
/// Four doors, one answer. This measures each.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DupeEntryPointParityTests
{
    private readonly ITestOutputHelper _out;
    public DupeEntryPointParityTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Caller(GameWorld world, short x = 100)
    {
        var ch = world.CreateCharacter();
        ch.BaseId = 0x0190;
        ch.BodyId = 0x0190;
        ch.Name = "Caller";
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    private static Item Thing(GameWorld world, short x = 101)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        it.Name = "a thing";
        world.PlaceItem(it, new Point3D(x, 100, 0, 0));
        return it;
    }

    // ---- door 1: the item DUPE verb --------------------------------------

    [Fact]
    public void TheItemDupeVerbLeavesNewAndActOnTheCopy()
    {
        var world = NewWorld();
        var caller = Caller(world);
        var src = Thing(world);

        Assert.True(src.TryExecuteCommand("DUPE", "1", new CharConsole(caller)));

        var copy = world.GetAllObjects().OfType<Item>()
            .Single(i => !i.IsDeleted && i.Uid != src.Uid && i.Name == "a thing");
        _out.WriteLine($"item verb: src={src.Uid} copy={copy.Uid} " +
                       $"new={world.LastNewObject} act={caller.Act}");

        Assert.Equal(copy.Uid, world.LastNewObject);
        Assert.Equal(copy.Uid, caller.Act);
    }

    // ---- door 2: the character DUPE verb ---------------------------------

    [Fact]
    public void TheCharacterDupeVerbLeavesNewOnTheCopyButNotAct()
    {
        var world = NewWorld();
        var caller = Caller(world);
        var src = Caller(world, 102);
        src.Name = "Original";

        Assert.True(src.TryExecuteCommand("DUPE", "", new CharConsole(caller)));

        var copy = world.GetAllObjects().OfType<Character>()
            .Single(c => !c.IsDeleted && c.Uid != src.Uid && c.Name == "Original");
        _out.WriteLine($"char verb: src={src.Uid} copy={copy.Uid} " +
                       $"new={world.LastNewObject} act={caller.Act}");

        // DupeFrom's last line is g_World.m_uidNew.SetObjUID(GetUID()) and its comment
        // explains the ordering: the equipment copies ran first and each of them moved
        // NEW to an item, so the character has to claim it back.
        Assert.Equal(copy.Uid, world.LastNewObject);

        // ACT, on the other hand, is NOT set here, and that asymmetry is upstream's.
        // The item verb sets it because CreateDupeItem takes the source character and
        // assigns pSrc->m_Act_UID (CItem.cpp:395); CHV_DUPE calls CreateNPC and
        // DupeFrom, neither of which touches ACT (CChar.cpp:4541-4547). Only NEWDUPE
        // copies NEW into ACT, for both kinds. Asserted so that "making the two verbs
        // consistent" cannot be done by accident.
        Assert.NotEqual(copy.Uid, caller.Act);
    }

    // ---- door 3: the native call ----------------------------------------

    [Fact]
    public void TheNativeCallLeavesActAloneAndIsRepairedForNew()
    {
        var world = NewWorld();
        var caller = Caller(world);
        caller.Act = caller.Uid;
        var src = Thing(world);

        var copy = src.CreateDupe(world);
        _out.WriteLine($"native: new={world.LastNewObject} act={caller.Act}");

        // ACT belongs to the caller of a VERB; a native call has no caller and must
        // not invent one.
        Assert.Equal(caller.Uid, caller.Act);

        // NEW is a recorded deviation. Upstream makes it a parameter - CreateDupeItem
        // takes fSetNew, defaulting to FALSE (CItem.h:620), so an internal copy leaves
        // a script's NEW alone and only the verb passes true. This engine moves NEW on
        // every object creation (GameWorld.CreateItem), and each public duplication
        // entry point puts it back on the object the script asked for - the item verb
        // after its loop, Character.CreateDupe at its end. The observable contract is
        // the same; where the value sits mid-flight is not.
        Assert.Equal(copy.Uid, world.LastNewObject);
    }

    // ---- the shape the contract exists to protect ------------------------

    [Fact]
    public void DuplicatingADressedCharacterLeavesNewOnTheCharacterNotItsShirt()
    {
        var world = NewWorld();
        var caller = Caller(world);
        var src = Caller(world, 102);
        src.Name = "Dressed";

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.Name = "a shirt";
        Assert.True(src.Equip(shirt, Layer.Shirt));

        Assert.True(src.TryExecuteCommand("DUPE", "", new CharConsole(caller)));

        var copy = world.GetAllObjects().OfType<Character>()
            .Single(c => !c.IsDeleted && c.Uid != src.Uid && c.Name == "Dressed");
        var landed = world.FindObject(world.LastNewObject);
        _out.WriteLine($"dressed: new points at {(landed is Character ? "the character" : landed?.GetType().Name)}");

        // The exact case CChar.cpp:1274 comments on. The equipment copy runs between
        // the character being made and the verb returning, so a NEW set early is not
        // the NEW the script asked for.
        Assert.Equal(copy.Uid, world.LastNewObject);
        Assert.IsType<Character>(landed);
    }

    /// <summary>A console that speaks for a character - which is what a verb issued
    /// from a script has, and what decides whose ACT the contract writes.</summary>
    private sealed class CharConsole(Character ch) : SphereNet.Core.Interfaces.ITextConsole
    {
        public void SysMessage(string message) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => ch.Name;
        public SphereNet.Core.Interfaces.IScriptObj? GetSourceChar() => ch;
    }
}
