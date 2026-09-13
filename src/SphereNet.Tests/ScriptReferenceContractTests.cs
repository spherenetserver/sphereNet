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
/// What a script reference says when the thing it names is gone (port plan İŞ-62 /
/// PLAN-204).
///
/// PLAN-204 asks for NEW / ACT / SRC / ARGO / LOCAL / REF to be verified on the
/// success path, the failure path, through nested calls, and - the case that gets
/// skipped - with a DELETED target.
///
/// Upstream answers that last one in the reader rather than at every write site.
/// Reading NEW validates it first: "if (!g_World.m_uidNew.ObjFind())
/// g_World.m_uidNew.ClearUID()" (CScriptObj.cpp:623-626), and OBJ does the same
/// (:617-621). A reference to something that has been deleted reads as 0 and the
/// stale value is cleared, so it cannot be handed to a later line as though it
/// still named something.
///
/// The alternative - hunting down every place an object can die and clearing the
/// references that might point at it - is the version that leaks, because the list
/// of such places is open-ended.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptReferenceContractTests
{
    private readonly ITestOutputHelper _out;
    public ScriptReferenceContractTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Thing(GameWorld world, short x = 100)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0EED;
        it.Name = "a thing";
        world.PlaceItem(it, new Point3D(x, 100, 0, 0));
        return it;
    }

    // ---- NEW -------------------------------------------------------------

    [Fact]
    public void NewNamesTheObjectThatWasJustMade()
    {
        var world = NewWorld();
        var made = Thing(world);

        _out.WriteLine($"made={made.Uid} new={world.LastNewObject}");

        // The success path, and the premise of the failure path below.
        Assert.Equal(made.Uid, world.LastNewObject);
    }

    [Fact]
    public void NewStopsNamingAnObjectThatHasBeenDeleted()
    {
        var world = NewWorld();
        var made = Thing(world);
        Assert.Equal(made.Uid, world.LastNewObject);

        world.DeleteObject(made);
        made.Delete();

        var stillThere = world.FindObject(world.LastNewObject);
        _out.WriteLine($"after delete: new={world.LastNewObject} resolves to " +
                       $"{(stillThere == null ? "nothing" : stillThere.GetType().Name)} " +
                       $"deleted={stillThere?.IsDeleted}");

        // Upstream reads NEW through ObjFind and clears it when the object is gone
        // (CScriptObj.cpp:623-626). What a script must never get is a uid that still
        // looks usable: NEW.NAME on a dead object, or worse, on whatever takes its
        // uid later.
        Assert.True(stillThere == null || stillThere.IsDeleted,
            "NEW still resolves to a live object after it was deleted");
    }

    [Fact]
    public void NewIsEmptyBeforeAnythingHasBeenMade()
    {
        var world = NewWorld();
        _out.WriteLine($"fresh world: new={world.LastNewObject} valid={world.LastNewObject.IsValid}");

        // Serial.Invalid, not zero - a "!= 0" test passes for "nothing created yet"
        // and that mistake is already recorded in Program.Scripting's NEW resolver.
        Assert.False(world.LastNewObject.IsValid);
    }

    // ---- ACT -------------------------------------------------------------

    [Fact]
    public void ActStopsNamingAnObjectThatHasBeenDeleted()
    {
        var world = NewWorld();
        var caller = world.CreateCharacter();
        caller.BaseId = 0x0190;
        caller.BodyId = 0x0190;
        world.PlaceCharacter(caller, new Point3D(100, 100, 0, 0));

        var target = Thing(world, 101);
        caller.Act = target.Uid;
        Assert.Equal(target.Uid, caller.Act);

        world.DeleteObject(target);
        target.Delete();

        var resolved = world.FindObject(caller.Act);
        _out.WriteLine($"after delete: act={caller.Act} resolves to " +
                       $"{(resolved == null ? "nothing" : resolved.GetType().Name)} " +
                       $"deleted={resolved?.IsDeleted}");

        // ACT is the same kind of reference as NEW and has to answer the same way: a
        // script that stored a target and comes back to it after it died must not act
        // on a live object that happens to sit at that uid now.
        Assert.True(resolved == null || resolved.IsDeleted,
            "ACT still resolves to a live object after it was deleted");
    }

    // ---- the reason this matters ----------------------------------------

    [Fact]
    public void AStaleReferenceDoesNotFollowTheUidOntoSomethingElse()
    {
        var world = NewWorld();
        var first = Thing(world);
        var staleUid = world.LastNewObject;
        Assert.Equal(first.Uid, staleUid);

        world.DeleteObject(first);
        first.Delete();

        // Whatever the allocator does next, the reference must not quietly start
        // naming it. This is the concrete harm behind the contract: a script holding
        // NEW from before a deletion, then writing NEW.NAME, editing a stranger.
        var second = Thing(world, 102);
        second.Name = "somebody else";

        var resolved = world.FindObject(staleUid);
        _out.WriteLine($"stale={staleUid} second={second.Uid} " +
                       $"stale resolves to '{(resolved as Item)?.Name ?? "nothing"}'");

        Assert.NotEqual(second.Uid, staleUid);
        Assert.True(resolved == null || resolved.IsDeleted);
    }

    [Fact]
    public void AFreedUidIsStillRecycledOnceTheSweepReleasesIt()
    {
        var world = NewWorld();
        var first = Thing(world);
        var freed = first.Uid;

        world.DeleteObject(first);
        first.Delete();

        // Deferring is not the same as never recycling. Upstream rebuilds its free
        // list during garbage collection and reuses the slots then; a shard that
        // never reclaimed a uid would walk its index upward forever. Here that point
        // is a completed maintenance sweep, driven the same way the server drives it.
        const long farFuture = 10_000_000;
        world.TickSleepingMaintenance(farFuture);
        while (world.MaintenanceSweepActive)
            world.TickSleepingMaintenance(farFuture);
        var next = Thing(world, 103);
        _out.WriteLine($"freed={freed} reallocated={next.Uid} same={next.Uid == freed}");

        Assert.Equal(freed, next.Uid);
    }

    // ---- REF ------------------------------------------------------------
    //
    // REF1..REFn are not stored on the object: they live in the script LOCAL pool
    // (ClientDialogHandler keys them as "REFn" in the same locals dictionary), so
    // their contract is the interpreter's rather than the world's and is exercised
    // by the dialog and interpreter tests. What IS the world's business is that a
    // uid taken out of any such pool cannot resolve to a live stranger, which is
    // what the stale-reference case above measures.
}
