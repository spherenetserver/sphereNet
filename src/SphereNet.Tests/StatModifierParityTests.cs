using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The stat a character actually uses, and the base underneath it (port plan İŞ-11 /
/// PLAN-303).
///
/// Upstream keeps one base and one modifier per stat: Stat_GetAdjusted is base + mod
/// (CCharStat.cpp:143), the modifier holds both what a script set through MODSTR and
/// what equipment added, STR reads the adjusted value and OSTR the bare base
/// (CChar.cpp:3139/3146), and writing either name writes the base (:3641).
///
/// SphereNet had two defects against that. MODSTR was stored and read by nothing - the
/// live script pack sets it 83 times and none of them did anything. And OSTR was a
/// SEPARATE field rather than another name for the base, which quietly undid training:
/// a record states STR before OSTR, so the stale shadow overwrote the real stat on
/// every load.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StatModifierParityTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static Character Trainee(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.Name = "Trainee";
        ch.IsPlayer = true;
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = 100; ch.MaxMana = 100; ch.MaxStam = 100;
        world.PlaceCharacter(ch, new Point3D(150, 150, 0, 0));
        return ch;
    }

    private Character SaveAndLoad(GameWorld world, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_mod_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, dir);

        var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, dir);

        var back = reloaded.GetAllCharactersSnapshot().FirstOrDefault(c => c.Name == name);
        Assert.NotNull(back);
        return back!;
    }

    // ------------------------------------------------------------ OSTR is the base

    [Fact]
    public void TrainingSurvivesTheSave()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);
        // Imported from a classic record, which states only the O-names...
        Assert.True(ch.TrySetProperty("OSTR", "100"));
        // ...and then trained.
        ch.Str = 110; ch.Dex = 111; ch.Int = 112;

        var back = SaveAndLoad(world, "Trainee");

        Assert.Equal(110, back.Str);
        Assert.Equal(111, back.Dex);
        Assert.Equal(112, back.Int);
    }

    [Fact]
    public void TheONameIsTheBaseUnderAnotherName()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);

        Assert.True(ch.TrySetProperty("OSTR", "77"));
        Assert.Equal(77, ch.Str);                    // writing OSTR writes the base

        ch.Str = 88;
        Assert.True(ch.TryGetProperty("OSTR", out string? o));
        Assert.Equal("88", o);                       // and reading it reports the base
    }

    // ------------------------------------------------------------ the modifier is real

    [Fact]
    public void AScriptSetModifierChangesTheStatTheCharacterUses()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);

        Assert.True(ch.TrySetProperty("MODSTR", "20"));

        Assert.Equal(100, ch.Str);                          // the base is untouched
        Assert.Equal(120, CombatEngine.EffectiveStr(ch));    // the stat in use is not
        Assert.True(ch.TryGetProperty("STR", out string? str));
        Assert.Equal("120", str);                            // <STR> is the adjusted one
        Assert.True(ch.TryGetProperty("OSTR", out string? bare));
        Assert.Equal("100", bare);
    }

    [Fact]
    public void TheModifierIsFeltWhereTheStatIsFelt()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);
        int carriedBefore = ch.MaxWeight;

        Assert.True(ch.TrySetProperty("MODSTR", "40"));

        // Carry weight is derived from the effective strength, so the modifier moves it.
        Assert.Equal(carriedBefore + 40 * 7 / 2, ch.MaxWeight);
    }

    [Fact]
    public void ANegativeModifierCannotPushTheStatBelowZero()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);

        Assert.True(ch.TrySetProperty("MODSTR", "-500"));

        Assert.Equal(0, CombatEngine.EffectiveStr(ch));
        Assert.Equal(100, ch.Str);        // still only a modifier, the base stands
    }

    [Fact]
    public void TheModifierSurvivesTheSave()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);
        Assert.True(ch.TrySetProperty("MODSTR", "15"));
        Assert.True(ch.TrySetProperty("MODDEX", "-5"));

        var back = SaveAndLoad(world, "Trainee");

        Assert.Equal(15, back.ModStr);
        Assert.Equal(-5, back.ModDex);
        Assert.Equal(100, back.Str);                        // the base came back as itself
        Assert.Equal(115, CombatEngine.EffectiveStr(back));
        Assert.Equal(95, CombatEngine.EffectiveDex(back));
    }

    [Fact]
    public void TheModifierAndTheSuitBothCount()
    {
        var world = TestHarness.CreateWorld();
        var ch = Trainee(world);
        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.SetTag("BONUSSTR", "10");
        Assert.True(ch.Equip(shirt, Layer.Shirt));

        Assert.True(ch.TrySetProperty("MODSTR", "20"));

        // Upstream folds both into the one modifier; SphereNet derives the suit share on
        // read and adds the script's own on top of it. Same total either way.
        Assert.Equal(130, CombatEngine.EffectiveStr(ch));
        Assert.Equal(100, ch.Str);
    }
}
