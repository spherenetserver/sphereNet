using System;
using System.IO;
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
/// A copied character's pools survive a restart without growing (port plan İŞ-53 /
/// PLAN-102).
///
/// Review 13J found that duplicating an equipped character wrote the suit's
/// contribution into the copy's BASE pools, so a copy of a copy kept getting
/// stronger and taking the suit off never gave the original number back. That is
/// fixed and DuplicationParity13JTests covers three of the four things PLAN-102
/// asks for: two generations, unequipping, and the source-versus-copy comparison.
///
/// The fourth it could not cover, and the review says so in as many words: "the
/// inflated base value reaching the save is a risk visible from the code, the real
/// save experiment was not performed". This is that experiment.
///
/// It matters beyond the fixed bug. The saver writes BaseMaxHits and the loader
/// reads it back into a property whose getter returns the EFFECTIVE value, so a
/// round trip that read the wrong side - or applied the suit before the pools -
/// would re-introduce the same compounding through persistence instead of through
/// duplication, and only after a restart.
/// </summary>
public sealed class DuplicatedPoolPersistenceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public DuplicatedPoolPersistenceTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_pools_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeSource(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.Name = "Source";
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = 100; ch.MaxMana = 100; ch.MaxStam = 100;
        ch.Hits = 100; ch.Mana = 100; ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(150, 150, 0, 0));
        return ch;
    }

    private static Item MakeBonusSuit(GameWorld world)
    {
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.SetTag("BONUSHITSMAX", "20");
        shirt.SetTag("BONUSMANAMAX", "30");
        shirt.SetTag("BONUSSTAMMAX", "40");
        return shirt;
    }

    private GameWorld SaveAndReload(GameWorld world)
    {
        Assert.True(new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { }))
            .Save(world, _dir));

        var reloaded = NewWorld();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { }))
            .Load(reloaded, _dir);
        return reloaded;
    }

    private void Report(string label, Character ch) =>
        _out.WriteLine($"{label}: base {ch.BaseMaxHits}/{ch.BaseMaxMana}/{ch.BaseMaxStam}" +
                       $"  effective {ch.MaxHits}/{ch.MaxMana}/{ch.MaxStam}");

    [Fact]
    public void ACopiedCharactersBasePoolsComeBackUnchangedAfterARestart()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));

        var copy = source.CreateDupe(world);
        Assert.True(world.PlaceCharacter(copy, new Point3D(151, 150, 0, 0)));

        Report("copy before save", copy);
        Assert.Equal(100, copy.BaseMaxHits);
        Assert.Equal(120, copy.MaxHits);

        var back = SaveAndReload(world).FindChar(copy.Uid);
        Assert.NotNull(back);
        Report("copy after reload", back!);

        // The save writes the base and the suit is re-derived on read. If the round
        // trip stored the effective value - or equipped the suit before applying the
        // pools - the copy would come back at 120 base and 140 effective, and the
        // shard would only find out after a restart.
        Assert.Equal(100, back!.BaseMaxHits);
        Assert.Equal(100, back.BaseMaxMana);
        Assert.Equal(100, back.BaseMaxStam);
        Assert.Equal(120, back.MaxHits);
        Assert.Equal(130, back.MaxMana);
        Assert.Equal(140, back.MaxStam);
    }

    [Fact]
    public void TwoSaveCyclesDoNotCompoundWhatOneCycleLeftAlone()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));
        var copy = source.CreateDupe(world);
        Assert.True(world.PlaceCharacter(copy, new Point3D(151, 150, 0, 0)));

        var first = SaveAndReload(world);
        var second = SaveAndReload(first);

        var back = second.FindChar(copy.Uid);
        Assert.NotNull(back);
        Report("copy after two cycles", back!);

        // One clean cycle proves the read; two prove the write, because a cycle that
        // saved the effective value would only show it on the pass after the one that
        // read it back.
        Assert.Equal(100, back!.BaseMaxHits);
        Assert.Equal(120, back.MaxHits);
    }

    [Fact]
    public void TakingTheSuitOffAfterARestartGivesBackTheOriginalPool()
    {
        var world = NewWorld();
        var source = MakeSource(world);
        Assert.True(source.Equip(MakeBonusSuit(world), Layer.Shirt));
        var copy = source.CreateDupe(world);
        Assert.True(world.PlaceCharacter(copy, new Point3D(151, 150, 0, 0)));

        var reloaded = SaveAndReload(world);
        var back = reloaded.FindChar(copy.Uid);
        Assert.NotNull(back);

        var worn = back!.GetEquippedItem(Layer.Shirt);
        Assert.NotNull(worn);
        back.Unequip(Layer.Shirt);
        Report("copy stripped after reload", back);

        // The original symptom stated end to end: strip the copy and the number the
        // character started life with has to be what is left. 120 here would mean the
        // suit's contribution had become part of the character.
        Assert.Equal(100, back.BaseMaxHits);
        Assert.Equal(100, back.MaxHits);
    }

    [Fact]
    public void ACharacterWithNoBonusIsUnaffectedByTheSameRoundTrip()
    {
        var world = NewWorld();
        var plain = MakeSource(world);
        var copy = plain.CreateDupe(world);
        Assert.True(world.PlaceCharacter(copy, new Point3D(151, 150, 0, 0)));

        var back = SaveAndReload(world).FindChar(copy.Uid);
        Assert.NotNull(back);
        Report("plain copy after reload", back!);

        // The control. Without it, a round trip that silently reset every pool to a
        // default would pass the tests above for the wrong reason.
        Assert.Equal(100, back!.BaseMaxHits);
        Assert.Equal(100, back.MaxHits);
        Assert.Equal(100, back.MaxMana);
        Assert.Equal(100, back.MaxStam);
    }
}
