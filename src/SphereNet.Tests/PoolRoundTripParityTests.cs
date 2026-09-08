using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A character logs back in with the pool it logged out with (port plan PLAN-102).
///
/// Three separate rules have to hold together for that, and none of them did:
///
/// 1. Writing HITS is an ASSIGNMENT, not a heal. Upstream sends it to Stat_SetVal,
///    which stores what it is given and refuses only a negative (CChar.cpp:3901,
///    CCharStat.cpp:157); the clamp to the maximum belongs to UpdateStatVal, the
///    gameplay path (CCharAct.cpp:753). SphereNet had one clamping setter doing both.
/// 2. The MAXIMUM is written before the value it bounds. Upstream is emphatic about the
///    order - "this is VERY important" (CChar.cpp:4250) - because setting a maximum
///    trims the current value to it.
/// 3. The load must not take a piece of equipment off. It did: the item's own record
///    already put it on its layer, and the character's EQUIP link then re-equipped it,
///    unequipping first - which drops the suit's BONUSHITSMAX for that instant, and the
///    pool is trimmed to the lowered maximum.
///
/// The visible bug was small and permanent: a player in a +20 hits suit logged out at
/// 120 of 120 and came back at 100 of 120, every single time.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PoolRoundTripParityTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _dirs = [];

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_pool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static Item BonusSuit(GameWorld world)
    {
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.SetTag("BONUSHITSMAX", "20");
        shirt.SetTag("BONUSMANAMAX", "30");
        shirt.SetTag("BONUSSTAMMAX", "40");
        return shirt;
    }

    private static Character DressedCharacter(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.Name = "Roundtrip";
        ch.IsPlayer = true;
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = 100; ch.MaxMana = 100; ch.MaxStam = 100;
        world.PlaceCharacter(ch, new Point3D(150, 150, 0, 0));
        Assert.True(ch.Equip(BonusSuit(world), Layer.Shirt));
        return ch;
    }

    private (GameWorld World, Character Char) SaveAndLoadWorld(GameWorld world, string name)
    {
        string dir = NewDir();
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

        var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, dir);

        var back = reloaded.GetAllCharactersSnapshot().FirstOrDefault(c => c.Name == name);
        Assert.NotNull(back);
        return (reloaded, back!);
    }

    private Character SaveAndLoad(GameWorld world, string name) =>
        SaveAndLoadWorld(world, name).Char;

    [Fact]
    public void AFullPoolInABonusSuitSurvivesTheSave()
    {
        var world = TestHarness.CreateWorld();
        var ch = DressedCharacter(world);
        ch.Hits = 120; ch.Mana = 130; ch.Stam = 140;
        Assert.Equal(120, ch.Hits);

        var back = SaveAndLoad(world, "Roundtrip");

        Assert.Equal(120, back.Hits);
        Assert.Equal(130, back.Mana);
        Assert.Equal(140, back.Stam);
        // ...and the maximum is still derived, not baked into the base.
        Assert.Equal(100, back.BaseMaxHits);
        Assert.Equal(120, back.MaxHits);
        Assert.NotNull(back.GetEquippedItem(Layer.Shirt));
    }

    [Fact]
    public void TheLossDoesNotAccumulateOverSeveralCycles()
    {
        var world = TestHarness.CreateWorld();
        var ch = DressedCharacter(world);
        ch.Hits = 120;

        var current = (World: world, Char: ch);
        for (int cycle = 0; cycle < 3; cycle++)
            current = SaveAndLoadWorld(current.World, "Roundtrip");

        Assert.Equal(120, current.Char.Hits);
        Assert.Equal(120, current.Char.MaxHits);
    }

    [Fact]
    public void AWoundedCharacterIsStillWounded()
    {
        var world = TestHarness.CreateWorld();
        var ch = DressedCharacter(world);
        ch.Hits = 43;

        var back = SaveAndLoad(world, "Roundtrip");

        Assert.Equal(43, back.Hits);   // the fix restores what was saved, not full health
    }

    [Fact]
    public void WritingThePoolStoresWhatItIsGiven()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;

        // The assignment path: upstream's Stat_SetVal has no upper bound, so a script
        // saying HITS=150 gets 150 - it is the gameplay path that clamps.
        Assert.True(ch.TrySetProperty("HITS", "150"));
        Assert.Equal(150, ch.Hits);

        // A negative is refused, exactly as std::max(GetArgVal(), 0) does.
        Assert.True(ch.TrySetProperty("HITS", "-20"));
        Assert.Equal(0, ch.Hits);
    }

    [Fact]
    public void AGameplayChangeStillStopsAtTheMaximum()
    {
        var world = TestHarness.CreateWorld();
        var ch = DressedCharacter(world);

        ch.Hits = 500;                 // the clamping property: healing, regeneration
        Assert.Equal(120, ch.Hits);    // stops at the effective maximum, suit included

        ch.Unequip(Layer.Shirt);
        Assert.Equal(100, ch.Hits);    // and comes down with the suit
    }

    [Fact]
    public void TheRecordStatesTheMaximumBeforeTheValue()
    {
        var world = TestHarness.CreateWorld();
        var ch = DressedCharacter(world);
        ch.Hits = 120;

        string dir = NewDir();
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

        string record = Directory.EnumerateFiles(dir, "*.scp")
            .Select(File.ReadAllText).First(t => t.Contains("Roundtrip"));
        int max = record.IndexOf("MAXHITS=", StringComparison.Ordinal);
        int val = record.IndexOf("\nHITS=", StringComparison.Ordinal);
        Assert.True(max >= 0 && val >= 0, "the record states both the maximum and the value");
        Assert.True(max < val, "the maximum must be stated first - setting it trims the value");
    }
}
