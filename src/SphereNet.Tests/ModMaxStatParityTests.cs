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
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// MODMAXHITS / MODMAXMANA / MODMAXSTAM (port plan İŞ-66 / PLAN-303).
///
/// A stat ceiling has three terms upstream, not two:
///
///   Stat_GetMaxAdjusted(i) = Stat_GetMax(i) + Stat_GetMaxMod(i)   (CCharStat.cpp:301)
///
/// Stat_GetMax is the base the definition and the save carry; Stat_GetMaxMod is a
/// SIGNED modifier a script owns (MODMAXHITS reads and writes it directly,
/// CChar.cpp:3204/3662, through GetArgSVal - it may be negative). The equipped
/// suit's contribution is a third, separate term this engine already derives on
/// read.
///
/// PLAN-303 asks for the family to be modelled as base + modifier + equipment and
/// for the getter, setter, save, copy and regeneration interactions to be tested,
/// because the modifier is the term most easily folded into the wrong one: written
/// into the base it compounds across save cycles and duplication, exactly like the
/// equipped-bonus bug review 13J found.
/// </summary>
public sealed class ModMaxStatParityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public ModMaxStatParityTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_mmx_{Guid.NewGuid():N}");
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

    private static Character Fighter(GameWorld world, short x = 100)
    {
        var ch = world.CreateCharacter();
        ch.BaseId = 0x0190;
        ch.BodyId = 0x0190;
        ch.Name = "Fighter";
        ch.MaxHits = 100; ch.MaxMana = 100; ch.MaxStam = 100;
        ch.Hits = 100; ch.Mana = 100; ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
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

    // ---- the three terms -------------------------------------------------

    [Fact]
    public void TheModifierRaisesTheCeilingWithoutTouchingTheBase()
    {
        var world = NewWorld();
        var ch = Fighter(world);

        ch.ModMaxHits = 25;
        _out.WriteLine($"base={ch.BaseMaxHits} mod={ch.ModMaxHits} effective={ch.MaxHits}");

        // Stat_GetMaxAdjusted is max + mod (CCharStat.cpp:301). Writing the modifier
        // into the base instead would look identical here and compound on the next
        // save cycle, which is why the base is asserted too.
        Assert.Equal(100, ch.BaseMaxHits);
        Assert.Equal(25, ch.ModMaxHits);
        Assert.Equal(125, ch.MaxHits);
    }

    [Fact]
    public void TheModifierMayBeNegative()
    {
        var world = NewWorld();
        var ch = Fighter(world);

        ch.ModMaxMana = -40;
        _out.WriteLine($"mana base={ch.BaseMaxMana} mod={ch.ModMaxMana} effective={ch.MaxMana}");

        // Upstream reads the argument with GetArgSVal - a SIGNED short - so a curse
        // that lowers a ceiling is the same mechanism as a blessing that raises one.
        Assert.Equal(-40, ch.ModMaxMana);
        Assert.Equal(60, ch.MaxMana);
    }

    [Fact]
    public void TheModifierStacksWithTheSuitRatherThanReplacingIt()
    {
        var world = NewWorld();
        var ch = Fighter(world);

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        shirt.SetTag("BONUSSTAMMAX", "30");
        Assert.True(ch.Equip(shirt, Layer.Shirt));

        ch.ModMaxStam = 10;
        _out.WriteLine($"stam base={ch.BaseMaxStam} mod={ch.ModMaxStam} effective={ch.MaxStam}");

        // Three separate terms: the base, the script's modifier, the suit. Folding
        // any pair together loses a distinction the reference keeps.
        Assert.Equal(100, ch.BaseMaxStam);
        Assert.Equal(140, ch.MaxStam);
    }

    // ---- persistence -----------------------------------------------------

    [Fact]
    public void TheModifierSurvivesARestartWithoutMovingIntoTheBase()
    {
        var world = NewWorld();
        var ch = Fighter(world);
        ch.ModMaxHits = 25;

        var back = SaveAndReload(world).FindChar(ch.Uid);
        Assert.NotNull(back);
        _out.WriteLine($"after reload: base={back!.BaseMaxHits} mod={back.ModMaxHits} effective={back.MaxHits}");

        // Upstream writes it as its own key (CChar.cpp:4262). A modifier saved into
        // the base would come back as 125/0/125 - the same ceiling today and a
        // compounding one at the next cycle.
        Assert.Equal(100, back.BaseMaxHits);
        Assert.Equal(25, back.ModMaxHits);
        Assert.Equal(125, back.MaxHits);
    }

    [Fact]
    public void TwoSaveCyclesDoNotCompoundTheModifier()
    {
        var world = NewWorld();
        var ch = Fighter(world);
        ch.ModMaxHits = 25;

        var back = SaveAndReload(SaveAndReload(world)).FindChar(ch.Uid);
        Assert.NotNull(back);
        _out.WriteLine($"after two cycles: base={back!.BaseMaxHits} mod={back.ModMaxHits} effective={back.MaxHits}");

        // One cycle proves the read; two prove the write.
        Assert.Equal(100, back.BaseMaxHits);
        Assert.Equal(125, back.MaxHits);
    }

    // ---- duplication -----------------------------------------------------

    [Fact]
    public void ACopyInheritsTheModifierAsAModifier()
    {
        var world = NewWorld();
        var ch = Fighter(world);
        ch.ModMaxHits = 25;

        var copy = ch.CreateDupe(world);
        _out.WriteLine($"copy: base={copy.BaseMaxHits} mod={copy.ModMaxHits} effective={copy.MaxHits}");

        // The shape review 13J found, in the term added here: a copy that folded the
        // modifier into its base would get stronger every time it was duplicated.
        Assert.Equal(100, copy.BaseMaxHits);
        Assert.Equal(25, copy.ModMaxHits);
        Assert.Equal(125, copy.MaxHits);
    }

    // ---- the current value ----------------------------------------------

    [Fact]
    public void LoweringTheCeilingBelowTheCurrentValueTrimsIt()
    {
        var world = NewWorld();
        var ch = Fighter(world);
        Assert.Equal(100, ch.Hits);

        ch.ModMaxHits = -30;
        _out.WriteLine($"hits={ch.Hits} of {ch.MaxHits}");

        // Stat_SetMaxMod is followed by the same clamp every max change gets
        // (CCharStat.cpp:66/115/142 re-read Stat_GetMaxAdjusted "to make sure the
        // current value is not higher than new max value"). A character left standing
        // above its own ceiling reads as full health while regenerating downward.
        Assert.Equal(70, ch.MaxHits);
        Assert.True(ch.Hits <= ch.MaxHits, $"hits {ch.Hits} exceeds the new ceiling {ch.MaxHits}");
    }

    [Fact]
    public void RaisingTheCeilingDoesNotHealTheCharacter()
    {
        var world = NewWorld();
        var ch = Fighter(world);
        ch.Hits = 40;

        ch.ModMaxHits = 50;
        _out.WriteLine($"hits={ch.Hits} of {ch.MaxHits}");

        // The other direction, and the control: a ceiling is not a heal. Upstream
        // only clamps downward.
        Assert.Equal(150, ch.MaxHits);
        Assert.Equal(40, ch.Hits);
    }
}
