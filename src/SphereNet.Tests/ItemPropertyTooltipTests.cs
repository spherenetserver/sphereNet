using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// The built-in AOS tooltip carries an item's component properties - luck, stat
/// bonuses, resists, hit effects - the way upstream appends them after the name and
/// the per-type lines (CEntityProps::AddPropsTooltipData, CClientMsg_AOSTooltip.cpp:118;
/// the tables in CCPropsItemEquippable.cpp:250, CCPropsItemWeapon.cpp:264,
/// CCPropsItem.cpp:177, CCPropsItemChar.cpp:213). Only the name line used to go out,
/// so a weapon with LUCK=100 showed nothing but its name.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemPropertyTooltipTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_ipt_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Build the tooltip of a placed item of the given type, after
    /// <paramref name="setup"/> has written its properties. <paramref name="defLines"/>
    /// are extra ITEMDEF lines (definition-side values or a trigger).</summary>
    private (uint Cliloc, string Args)[] Build(Action<Item> setup,
        ItemType type = ItemType.WeaponSword, params string[] defLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "t.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 013b9]",
            "DEFNAME=i_probe_blade",
            "TYPE=t_weapon_sword",
            "NAME=probe blade",
        };
        lines.AddRange(defLines);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5210);
        client.SetEngines(triggerDispatcher: dispatcher);
        client.NetState.ClientVersionNumber = 70_020_000;   // AOS tooltips on

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        var item = world.CreateItem();
        item.BaseId = 0x13B9;
        item.ItemType = type;
        setup(item);
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));

        client.SkillUse.SendAosTooltip(item, requested: true);
        return item.TooltipCache?.Properties ?? [];
    }

    private static void Set(Item item, string key, string value) =>
        Assert.True(item.TrySetProperty(key, value), $"{key} was refused");

    /// <summary>The audit case: LUCK=100 on a weapon shows "luck 100" after the name.</summary>
    [Fact]
    public void LuckOnAWeaponShowsTheLuckLine()
    {
        var props = Build(i => Set(i, "LUCK", "100"));

        Assert.Equal(1050045u, props[0].Cliloc);
        Assert.Contains((1060436u, "100"), props);
    }

    /// <summary>A zero value emits nothing (upstream skips iVal == 0).</summary>
    [Fact]
    public void AZeroPropertyEmitsNothing()
    {
        var props = Build(i => { Set(i, "LUCK", "0"); Set(i, "HITLEECHLIFE", "0"); });

        Assert.DoesNotContain(props, p => p.Cliloc == 1060436);
        Assert.DoesNotContain(props, p => p.Cliloc == 1060422);
    }

    /// <summary>Lines come out in upstream order - item-char, item, equippable (table
    /// order), weapon - whatever order the script wrote them in; flag-style properties
    /// carry no argument.</summary>
    [Fact]
    public void LinesFollowTheComponentAndTableOrder()
    {
        var props = Build(i =>
        {
            Set(i, "USEBESTWEAPONSKILL", "1");
            Set(i, "LUCK", "40");
            Set(i, "HITLEECHLIFE", "25");
            Set(i, "BONUSSTR", "5");
            Set(i, "SPELLCHANNELING", "1");
            Set(i, "WEIGHTREDUCTION", "50");
        });

        var tail = props.Where(p => p.Cliloc != 1050045).ToArray();
        Assert.Equal(new (uint, string)[]
        {
            (1072210, "50"),   // weight reduction (item-char component)
            (1060485, "5"),    // strength bonus
            (1060422, "25"),   // hit life leech
            (1060436, "40"),   // luck
            (1060482, ""),     // spell channeling
            (1060400, ""),     // use best weapon skill (weapon component)
        }, tail);
    }

    /// <summary>A sphere hex token reads as the number it is.</summary>
    [Fact]
    public void AHexValueIsShownInDecimal()
    {
        var props = Build(i => Set(i, "LUCK", "064"));
        Assert.Contains((1060436u, "100"), props);
    }

    /// <summary>The elemental resists follow upstream's gates: fire/cold/poison/energy
    /// with the elemental engine OR DisplayElementalResistance, physical only with the
    /// elemental engine.</summary>
    [Fact]
    public void ResistLinesFollowTheElementalGates()
    {
        void Setup(Item i) { Set(i, "RESFIRE", "10"); Set(i, "RESPHYSICAL", "7"); }

        var off = Build(Setup);
        Assert.DoesNotContain(off, p => p.Cliloc == 1060447);
        Assert.DoesNotContain(off, p => p.Cliloc == 1060448);

        GameClient.DisplayElementalResistance = true;
        var display = Build(Setup);
        Assert.Contains((1060447u, "10"), display);
        Assert.DoesNotContain(display, p => p.Cliloc == 1060448);

        GameClient.DisplayElementalResistance = false;
        Character.CombatFlags = (int)SphereNet.Game.Combat.CombatFlags.ElementalEngine;
        var elemental = Build(Setup);
        Assert.Contains((1060447u, "10"), elemental);
        Assert.Contains((1060448u, "7"), elemental);
    }

    /// <summary>The equippable table belongs to equippable types only; a plain item
    /// carrying the tag shows nothing (upstream never gives it the component).</summary>
    [Fact]
    public void ANonEquippableItemShowsNoEquipmentLines()
    {
        var props = Build(i => Set(i, "LUCK", "100"), ItemType.Container);
        Assert.DoesNotContain(props, p => p.Cliloc == 1060436);
    }

    /// <summary>Weapon-only lines stay off armour.</summary>
    [Fact]
    public void WeaponLinesStayOffArmour()
    {
        var props = Build(i => { Set(i, "USEBESTWEAPONSKILL", "1"); Set(i, "LUCK", "3"); },
            ItemType.Armor);
        Assert.DoesNotContain(props, p => p.Cliloc == 1060400);
        Assert.Contains((1060436u, "3"), props);
    }

    /// <summary>Only the instance's own values count, as upstream walks the object's
    /// components and not its definition's.</summary>
    [Fact]
    public void ADefinitionOnlyValueIsNotShown()
    {
        var props = Build(_ => { }, ItemType.WeaponSword, "LUCK=100");
        Assert.DoesNotContain(props, p => p.Cliloc == 1060436);
    }

    /// <summary>@ClientTooltip RETURN 1 still suppresses every built-in line, the
    /// property lines included, and keeps the script's own.</summary>
    [Fact]
    public void ScriptReturnOneStillWins()
    {
        var props = Build(i => Set(i, "LUCK", "100"), ItemType.WeaponSword,
            "ON=@ClientTooltip", "ADDCLILOC 1060658,Access,Owner Only", "RETURN 1");

        Assert.Single(props);
        Assert.Equal(1060658u, props[0].Cliloc);
    }

    /// <summary>Changing a property flags the object so the tooltip is rebuilt for
    /// nearby clients (upstream UpdatePropertyFlag); rewriting the same value does
    /// not.</summary>
    [Fact]
    public void ChangingAPropertyFlagsTheTooltipForRebuild()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var item = world.CreateItem();
        item.ItemType = ItemType.WeaponSword;
        item.ConsumeDirty();

        Assert.True(item.TrySetProperty("LUCK", "100"));
        Assert.True((item.ConsumeDirty() & DirtyFlag.Properties) != 0);

        Assert.True(item.TrySetProperty("LUCK", "100"));
        Assert.Equal(DirtyFlag.None, item.ConsumeDirty() & DirtyFlag.Properties);

        Assert.True(item.TrySetProperty("RESFIRE", "5"));
        Assert.True((item.ConsumeDirty() & DirtyFlag.Properties) != 0);
    }
}
