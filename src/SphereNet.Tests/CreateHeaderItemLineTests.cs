using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
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
/// What the arguments of an ITEM= line mean.
///
/// Upstream states the form in the function that reads it: "ITEM=#id,#amount,R#chance"
/// (CItem::CreateHeader, CItem.cpp:461-488). Every argument after the defname is either
/// an amount expression, which goes through the expression engine, or R#, a one-in-#
/// chance for the line to produce anything at all; an amount of 0 creates nothing.
///
/// Three places here parsed those arguments themselves and got both halves wrong. The
/// amount was a plain int.TryParse, so ITEM=i_gold,{750 900} - the shape the live pack
/// writes 1,066 times - parsed as nothing and the monster dropped one coin. The third
/// argument was read as a "dice roll", a form upstream does not have, so R8 was taken
/// for an amount, failed to parse, and the line created its item every time instead of
/// one time in eight.
///
/// The correct reading already existed for loot templates; these tests hold all the
/// paths to it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CreateHeaderItemLineTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_hdr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Spawn an NPC from a CHARDEF built out of the given lines, firing
    /// @Create, and hand back its backpack contents.</summary>
    private List<Item> SpawnAndCollect(params string[] charDefLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 0eed]", "DEFNAME=i_hdr_gold", "NAME=gold", "TYPE=t_gold",
            "[CHARDEF 012]", "DEFNAME=c_hdr_probe", "NAME=probe",
        };
        lines.AddRange(charDefLines);
        File.WriteAllLines(file, lines);

        var lf = LoggerFactory.Create(_ => { });
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
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            dispatcher.FireCharTrigger(npc, CharTrigger.Create,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

        var gem = world.CreateItem();
        world.PlaceItem(gem, new Point3D(100, 100, 0, 0));
        var spawn = new SphereNet.Game.Components.SpawnComponent(gem, world) { MaxCount = 1 };
        var npc = spawn.SpawnSpecific(0x12);
        Assert.NotNull(npc);
        return npc!.Backpack?.Contents.ToList() ?? [];
    }

    /// <summary>The 1,066-line case: an amount written as a range is a range.</summary>
    [Fact]
    public void AnAmountRangeIsRolled()
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 12; i++)
        {
            var loot = SpawnAndCollect("ON=@Create", "ITEM=i_hdr_gold,{40 60}");
            var gold = Assert.Single(loot);
            Assert.InRange(gold.Amount, 40, 60);
            seen.Add(gold.Amount);
            Dispose();
        }
        Assert.True(seen.Count > 1, "the amount never moved off a single value");
    }

    /// <summary>R# is a chance, not an amount: the line sometimes produces nothing.
    /// One in eight over 40 tries is all but certain to show both outcomes - and
    /// whatever it does produce is one item, not eight.</summary>
    [Fact]
    public void AnRArgumentIsAChanceToCreateAtAll()
    {
        int made = 0;
        for (int i = 0; i < 40; i++)
        {
            var loot = SpawnAndCollect("ON=@Create", "ITEM=i_hdr_gold,R8");
            if (loot.Count > 0)
            {
                made++;
                Assert.Equal(1, loot[0].Amount);
            }
            Dispose();
        }
        Assert.InRange(made, 1, 39);   // neither never nor always
    }

    /// <summary>Both together, which is how the packs write a chance drop of a
    /// stack: the amount still applies when the chance comes up.</summary>
    [Fact]
    public void AnAmountAndAChanceBothApply()
    {
        for (int i = 0; i < 25; i++)
        {
            var loot = SpawnAndCollect("ON=@Create", "ITEM=i_hdr_gold,{10 20},R2");
            if (loot.Count > 0)
                Assert.InRange(loot[0].Amount, 10, 20);
            Dispose();
        }
    }

    /// <summary>An amount of zero creates nothing (CItem.cpp:487).</summary>
    [Fact]
    public void AZeroAmountCreatesNothing()
    {
        Assert.Empty(SpawnAndCollect("ON=@Create", "ITEM=i_hdr_gold,0"));
    }

    /// <summary>The CHARDEF body form reads the same way - it is the same line,
    /// written outside a trigger. Its items are the NPC's deferred loot, so this
    /// checks what the definition recorded and what those arguments roll to, rather
    /// than waiting for the moment the loot is handed over.</summary>
    [Fact]
    public void TheDefinitionBodyFormReadsTheSameWay()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        File.WriteAllLines(file, new[]
        {
            "[ITEMDEF 0eed]", "DEFNAME=i_hdr_gold", "NAME=gold", "TYPE=t_gold",
            "[CHARDEF 012]", "DEFNAME=c_hdr_probe", "NAME=probe",
            "ITEM=i_hdr_gold,{750 900},R3",
        });

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var def = DefinitionLoader.GetCharDef(0x12);
        Assert.NotNull(def);
        var entry = Assert.Single(def!.NewbieItems);
        Assert.Equal(new[] { "{750 900}", "R3" }, entry.RawArgs);

        // Both arguments are live: the amount is a range, and the chance can decline.
        var amounts = new HashSet<int>();
        int declined = 0;
        for (int i = 0; i < 60; i++)
        {
            if (TemplateEngine.TryRollCreateHeaderArgs(entry.RawArgs, out int amount))
                amounts.Add(amount);
            else
                declined++;
        }
        Assert.All(amounts, a => Assert.InRange(a, 750, 900));
        Assert.True(amounts.Count > 1, "the amount never moved");
        Assert.InRange(declined, 1, 59);
    }

    /// <summary>And the arguments go through the expression engine, so the hex
    /// convention holds here as everywhere else.</summary>
    [Fact]
    public void TheAmountIsASphereNumber()
    {
        Assert.True(TemplateEngine.TryRollCreateHeaderArgs(new[] { "{010 010}" }, out int hex));
        Assert.Equal(16, hex);

        Assert.True(TemplateEngine.TryRollCreateHeaderArgs(new[] { "5" }, out int plain));
        Assert.Equal(5, plain);

        Assert.True(TemplateEngine.TryRollCreateHeaderArgs([], out int none));
        Assert.Equal(1, none);   // no args at all = one item
    }
}
