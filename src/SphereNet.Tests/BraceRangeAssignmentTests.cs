using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A "{lo hi}" written at an item rolls, the way it does everywhere else.
///
/// Upstream evaluates braces inside the expression engine (CExpression.cpp:790), so the
/// shape works against any object and any key that is read as a number. Here the
/// interpreter substitutes &lt;...&gt; into an assignment but does not evaluate it, and
/// the value reached Item.TrySetProperty as the literal text "{100 200}" - which parsed
/// as 0. The character side never showed it because Character.TrySetProperty normalises
/// its own values first.
///
/// The live pack writes that shape about 1,070 times inside ITEMDEF trigger blocks -
/// HITPOINTS (866 of them), MOREY, MORE2, COLOR, DISPID - so crafted and spawned items
/// came out with zero durability, no hue and no MORE values.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class BraceRangeAssignmentTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_brace_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Item LooseItem()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var it = world.CreateItem();
        world.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return it;
    }

    /// <summary>The 866-line case: durability rolls, and it varies.</summary>
    [Fact]
    public void ATwoValueRangeRollsInsideIt()
    {
        var it = LooseItem();
        var seen = new HashSet<int>();
        for (int i = 0; i < 40; i++)
        {
            Assert.True(it.TrySetProperty("HITPOINTS", "{100 200}"));
            Assert.True(it.TryGetProperty("HITPOINTS", out string got));
            int v = int.Parse(got);
            Assert.InRange(v, 100, 200);
            seen.Add(v);
        }
        Assert.True(seen.Count > 1, "the range never moved off a single value");
    }

    /// <summary>Leading zero is hex, the Sphere convention the hue lines are written
    /// in: COLOR={0481 0489} is 1153..1161, not four hundred and something.</summary>
    [Fact]
    public void TokensAreReadAsSphereNumbers()
    {
        var it = LooseItem();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(it.TrySetProperty("COLOR", "{0481 0489}"));
            Assert.True(it.TryGetProperty("COLOR", out string got));
            Assert.InRange(int.Parse(got), 0x481, 0x489);
        }
    }

    /// <summary>Four or more tokens are (value, weight) pairs - a pick from a list, not
    /// a range. The packs write hue lists that way.</summary>
    [Fact]
    public void FourTokensArePickedNotRanged()
    {
        var it = LooseItem();
        var seen = new HashSet<int>();
        for (int i = 0; i < 60; i++)
        {
            Assert.True(it.TrySetProperty("MOREY", "{10 1 900 1}"));
            Assert.True(it.TryGetProperty("MOREY", out string got));
            int v = int.Parse(got);
            Assert.True(v is 10 or 900, $"picked {v}, which is not in the list");
            seen.Add(v);
        }
        Assert.Equal(2, seen.Count);
    }

    /// <summary>An odd count above two is a script error upstream and yields 0
    /// (GetRangeNumber). Rolling it as if it were a list would invent a value.</summary>
    [Fact]
    public void AnOddCountIsRefused()
    {
        var it = LooseItem();
        Assert.True(it.TrySetProperty("MOREY", "{10 1 900}"));
        Assert.True(it.TryGetProperty("MOREY", out string got));
        Assert.Equal("0", got);
    }

    /// <summary>A list of resource names is NOT a number, and must be left exactly as
    /// it was - FRUIT={i_fruit_pumpkin ...} is read by the code that owns it.</summary>
    [Fact]
    public void ANonNumericListIsLeftAlone()
    {
        Assert.False(BraceRange.TryRollNumeric("{i_fruit_pumpkin i_fruit_watermelon}", out _));
        Assert.False(BraceRange.TryRollNumeric("{}", out _));
        Assert.False(BraceRange.TryRollNumeric("100", out _));
        Assert.False(BraceRange.TryRollNumeric("{100 200} 5", out _));
    }

    /// <summary>The decimal skill form stays the character's business: TACTICS={29.0
    /// 44.0} means 290..440, and 4,596 pack lines are written that way. Rolling it here
    /// would hand the character 29..44 with the x10 scale already lost.</summary>
    [Fact]
    public void TheDecimalSkillFormIsNotRolledHere()
    {
        Assert.False(BraceRange.TryRollNumeric("{29.0 44.0}", out _));

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(101, 100, 0, 0));

        for (int i = 0; i < 20; i++)
        {
            Assert.True(ch.TrySetProperty("TACTICS", "{29.0 44.0}"));
            Assert.True(ch.TryGetProperty("TACTICS", out string got));
            Assert.InRange(int.Parse(got), 290, 440);
        }
    }

    /// <summary>End to end, which is how the packs write it: an @Create block rolling
    /// its own durability and hue.</summary>
    [Fact]
    public void ACreateBlockRollsItsOwnValues()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 013bb]", "DEFNAME=i_probe_brace", "TYPE=t_armor",
            "ON=@Create", "HITPOINTS={100 200}", "COLOR={0481 0489}",
        });

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.CreateTriggerHook = it => dispatcher.FireItemTrigger(it,
            SphereNet.Core.Enums.ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = it });

        var hits = new HashSet<int>();
        for (int i = 0; i < 25; i++)
        {
            var it = world.CreateItem();
            ItemDefHelper.ApplyInstanceMetadata(it, 0x13BB);

            Assert.True(it.TryGetProperty("HITPOINTS", out string h));
            Assert.InRange(int.Parse(h), 100, 200);
            hits.Add(int.Parse(h));

            Assert.True(it.TryGetProperty("COLOR", out string c));
            Assert.InRange(int.Parse(c), 0x481, 0x489);
        }
        Assert.True(hits.Count > 1);
    }
}
