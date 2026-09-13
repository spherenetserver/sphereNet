using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every callback in the spawn sequence: what it may rewrite, and what it may
/// destroy (port plan İŞ-61 / PLAN-203).
///
/// The order upstream runs is fixed (CCSpawn::GenerateChar, CCSpawn.cpp:380-440):
///
///   1. the spawner must be top level, or nothing happens;
///   2. @PreSpawn, with N1 carrying the resource index - RETURN 1 creates nothing
///      at all, and on return the resource id is REBUILT from whatever N1 now
///      holds, so the script may retarget the spawn;
///   3. the object is created and NPC_LoadScript runs;
///   4. @Spawn, with O1 carrying the new object - RETURN 1 DELETES it, which is a
///      different outcome from @PreSpawn's veto: there, nothing was ever made;
///   5. placement, but ONLY if @Spawn did not already give the object a valid
///      point ("Try to place it only if the @Spawn trigger didn't set it a valid
///      P", :428);
///   6. membership, then @AddObj.
///
/// PLAN-203 asks for a matrix over that sequence rather than a single happy path,
/// because each step's veto and each step's write-back fail differently: a veto
/// that forgets to delete leaks an object into the world, and a write-back that
/// arrives too late is ignored in silence.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnCallbackMatrixTests
{
    private readonly ITestOutputHelper _out;
    public SpawnCallbackMatrixTests(ITestOutputHelper output) => _out = output;

    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_cb
        TYPE=t_spawn_char

        [CHARDEF c_target_cb]
        DEFNAME=c_target_cb
        ID=0x27
        NAME=matrix target
        """;

    /// <summary>Definitions have to load INSIDE the test body: ResetEngineStatics
    /// clears the tables in its Before hook, which xUnit runs after the class is
    /// constructed, so a constructor-time load is wiped before the first
    /// assertion.</summary>
    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_cb_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new SphereNet.Game.Definitions.DefinitionLoader(
            resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A spawner that actually produces: it needs a target definition, or
    /// every callback below measures a production that never starts.</summary>
    private static Item Spawner(GameWorld world, ResourceHolder res, int amount = 1)
    {
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_target_cb");
        stone.Amount = (ushort)amount;
        stone.InitializeSpawnComponent(world, res);
        return stone;
    }

    /// <summary>Run one production. ForceSpawn only clears the countdown - the work
    /// happens on the next tick, so a test that calls ForceSpawn alone measures a
    /// spawn that never started.</summary>
    private static void Produce(Item spawner)
    {
        spawner.SpawnChar!.ForceSpawn();
        spawner.SpawnChar!.OnTick(Environment.TickCount64);
    }

    private static int LiveCharacters(GameWorld world) =>
        world.GetAllObjects().OfType<Character>().Count(c => !c.IsDeleted);

    /// <summary>Install a trigger handler for the duration of one test and record the
    /// order the callbacks actually ran in.</summary>
    private static List<ItemTrigger> Watch(
        Func<Item, ItemTrigger, SpawnTriggerArgs, TriggerResult>? handler = null)
    {
        var seen = new List<ItemTrigger>();
        SpawnComponent.OnSpawnTrigger = (item, trig, args) =>
        {
            seen.Add(trig);
            return handler?.Invoke(item, trig, args) ?? TriggerResult.Default;
        };
        return seen;
    }

    // ---- the order itself ------------------------------------------------

    [Fact]
    public void TheCallbacksRunInTheOrderTheReferenceRunsThem()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        var seen = Watch();

        Produce(spawner);
        _out.WriteLine(string.Join(" -> ", seen));

        // PreSpawn decides WHAT, Spawn inspects WHAT WAS MADE, AddObj announces that
        // it belongs to the spawner. Any other order makes at least one of them read
        // something that does not exist yet.
        Assert.Equal([ItemTrigger.PreSpawn, ItemTrigger.Spawn, ItemTrigger.AddObj], seen);
    }

    // ---- the two vetoes, which are not the same veto ---------------------

    [Fact]
    public void APreSpawnVetoCreatesNothingAtAll()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        int before = LiveCharacters(world);

        var seen = Watch(
            (_, trig, _) => trig == ItemTrigger.PreSpawn ? TriggerResult.True : TriggerResult.Default);

        Produce(spawner);
        _out.WriteLine($"pre-spawn veto: callbacks={string.Join(",", seen)} chars {before}->{LiveCharacters(world)}");

        // Upstream returns before CreateBasic (CCSpawn.cpp:391), so no object is made
        // and the later callbacks never run.
        Assert.Equal([ItemTrigger.PreSpawn], seen);
        Assert.Equal(before, LiveCharacters(world));
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void ASpawnVetoDeletesTheObjectItWasShown()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        int before = LiveCharacters(world);

        Character? shown = null;
        var seen = Watch((_, trig, args) =>
        {
            if (trig != ItemTrigger.Spawn) return TriggerResult.Default;
            shown = args.SpawnedChar;
            return TriggerResult.True;
        });

        Produce(spawner);
        _out.WriteLine($"spawn veto: callbacks={string.Join(",", seen)} " +
                       $"shown={(shown == null ? "nothing" : shown.Uid.ToString())} " +
                       $"deleted={shown?.IsDeleted} chars {before}->{LiveCharacters(world)}");

        // The difference that matters: by this point the object EXISTS, so a veto has
        // to destroy it (CCSpawn.cpp:422). A veto that only returns leaves a creature
        // standing in the world that no spawner owns and no script asked for.
        Assert.NotNull(shown);
        Assert.True(shown!.IsDeleted);
        Assert.Equal(before, LiveCharacters(world));
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
        Assert.DoesNotContain(ItemTrigger.AddObj, seen);
    }

    // ---- write-back ------------------------------------------------------

    [Fact]
    public void SpawnIsHandedTheObjectAndAddObjTheSameOne()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);

        Character? atSpawn = null, atAddObj = null;
        Watch((_, trig, args) =>
        {
            if (trig == ItemTrigger.Spawn) atSpawn = args.SpawnedChar;
            if (trig == ItemTrigger.AddObj) atAddObj = args.SpawnedChar;
            return TriggerResult.Default;
        });

        Produce(spawner);
        _out.WriteLine($"spawn={atSpawn?.Uid} addobj={atAddObj?.Uid}");

        // O1 is the object in both callbacks (:419, :650). A script that stores it in
        // @Spawn and acts on it in @AddObj must be looking at the same creature.
        Assert.NotNull(atSpawn);
        Assert.NotNull(atAddObj);
        Assert.Equal(atSpawn!.Uid, atAddObj!.Uid);
        Assert.Equal(atSpawn.Uid, Assert.Single(spawner.SpawnChar!.SpawnedUids));
    }

    [Fact]
    public void APointChosenInSpawnIsKeptRatherThanOverwrittenByPlacement()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        var chosen = new Point3D(140, 160, 0, 0);

        Watch((_, trig, args) =>
        {
            if (trig == ItemTrigger.Spawn && args.SpawnedChar != null)
                args.SpawnedChar.Position = chosen;
            return TriggerResult.Default;
        });

        Produce(spawner);
        var made = world.FindChar(spawner.SpawnChar!.SpawnedUids.Single());
        Assert.NotNull(made);
        _out.WriteLine($"script chose {chosen.X},{chosen.Y}; creature stands at {made!.X},{made.Y}");

        // "Try to place it only if the @Spawn trigger didn't set it a valid P"
        // (CCSpawn.cpp:428). Scattering it anyway would silently discard the one
        // decision the script made.
        Assert.Equal(chosen.X, made.X);
        Assert.Equal(chosen.Y, made.Y);
    }

    [Fact]
    public void WithoutAChoiceThePlacementStillHappensNearTheSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        Watch();

        Produce(spawner);
        var made = world.FindChar(spawner.SpawnChar!.SpawnedUids.Single());
        Assert.NotNull(made);
        _out.WriteLine($"unplaced creature landed at {made!.X},{made.Y} (spawner at 100,100)");

        // The control for the test above: if placement never ran at all, a creature
        // would sit at 0,0 and that test would pass for the wrong reason.
        Assert.NotEqual(0, made.X);
        Assert.NotEqual(0, made.Y);
    }

    // ---- the precondition ------------------------------------------------

    [Fact]
    public void ASpawnerThatIsNotTopLevelRunsNoCallbacksAtAll()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = Spawner(world, res);
        var seen = Watch();

        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(120, 120, 0, 0));
        bag.AddItem(spawner);

        Produce(spawner);
        _out.WriteLine($"contained spawner: callbacks={(seen.Count == 0 ? "none" : string.Join(",", seen))}");

        // GenerateChar returns before the first trigger when the spawner is not top
        // level (:383). A spawner in a bag firing @PreSpawn would let a script see a
        // production that can never complete.
        Assert.Empty(seen);
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
    }
}
