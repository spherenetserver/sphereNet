using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// One recipe, every door that builds it (port plan İŞ-60 / PLAN-202).
///
/// PLAN-202 asks for the TEMPLATE execution path to be compared across NEWITEM,
/// spawn and the loot/vendor productions, closing the first line, sub-recipes,
/// amount, FUNC, the special containers and the immovable-object limit.
///
/// The reason it asks is visible in the code: the recipe grammar is ORDER, and it
/// is expressed twice. TemplateEngine walks TemplateDef.Rows, which carries every
/// line in the order it was written - ITEM, CONTAINER, FUNC and any other line as
/// a property assignment applied to whatever the recipe made last (ReadTemplate,
/// CItem.cpp:686). The loot expansion walks TemplateDef.ItemEntries instead, which
/// is the ITEM/CONTAINER lines only.
///
/// Two readings of one grammar is the shape that drifts, so the same recipe is
/// built through both and the results compared field by field.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TemplateEntryPointParityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Defs = """
        [ITEMDEF 01000]
        DEFNAME=i_reward_tp
        NAME=Reward
        TYPE=t_normal

        [ITEMDEF 0e75]
        DEFNAME=i_bag_tp
        NAME=Bag
        TYPE=t_container

        [TEMPLATE 020001]
        DEFNAME=t_named_reward
        ITEM=i_reward_tp,3
        NAME=Gilded Reward
        COLOR=0489

        [CHARDEF 0190]
        DEFNAME=c_man_tp
        NAME=Man
        """;

    public TemplateEntryPointParityTests(ITestOutputHelper output)
    {
        _out = output;
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_tp_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose() => File.Delete(_scriptPath);

    /// <summary>Load the fixture and make a world. The load has to happen INSIDE the
    /// test body: ResetEngineStatics clears the definition tables in its Before hook,
    /// which xUnit runs after the class is constructed - so a constructor-time load is
    /// wiped before the first assertion, and every recipe resolves to nothing.</summary>
    private GameWorld NewWorld()
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private int TemplateIndex()
    {
        var rid = _resources.ResolveDefName("t_named_reward");
        Assert.True(rid.IsValid && rid.Type == ResType.Template, "the recipe did not load");
        return rid.Index;
    }

    [Fact]
    public void TheEngineDoorAppliesThePropertyLinesTheRecipeCarries()
    {
        var world = NewWorld();
        var built = TemplateEngine.BuildTemplate(world, TemplateIndex());

        Assert.NotNull(built);
        _out.WriteLine($"engine door: name='{built!.Name}' amount={built.Amount} hue={built.Hue}");

        // The reference applies every line that is not ITEM/CONTAINER/FUNC to the item
        // the recipe created last (CItem.cpp:686). This is the behaviour the loot door
        // is compared against.
        Assert.Equal(3, built.Amount);
        Assert.Equal("Gilded Reward", built.Name);
    }

    [Fact]
    public void TheLootDoorBuildsTheSameRecipeTheSameWay()
    {
        var world = NewWorld();

        var owner = world.CreateCharacter();
        owner.BaseId = 0x0190;
        owner.BodyId = 0x0190;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        Assert.True(owner.Equip(pack, Layer.Pack));
        owner.Backpack = pack;

        // The real door: the @Create body verb a chardef uses to give an NPC its
        // loot (ITEM=<recipe>), which is what reaches the loot expansion.
        Assert.True(owner.TryExecuteCommand("ITEM", "t_named_reward", new CharConsole(owner)));

        var made = pack.Contents.SingleOrDefault();
        Assert.NotNull(made);
        _out.WriteLine($"loot door:   name='{made!.Name}' amount={made.Amount} hue={made.Hue}");

        // The same recipe, the same answer. A door that reads only the ITEM lines
        // produces an unnamed, uncoloured reward - the recipe's NAME and COLOR are
        // ordinary lines, and dropping them is not a smaller version of the recipe,
        // it is a different one.
        Assert.Equal(3, made.Amount);
        Assert.Equal("Gilded Reward", made.Name);
    }

    [Fact]
    public void BothDoorsAgreeFieldByField()
    {
        var world = NewWorld();

        var viaEngine = TemplateEngine.BuildTemplate(world, TemplateIndex());
        Assert.NotNull(viaEngine);

        var owner = world.CreateCharacter();
        owner.BaseId = 0x0190;
        owner.BodyId = 0x0190;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        Assert.True(owner.Equip(pack, Layer.Pack));

        // The real door: the @Create body verb a chardef uses to give an NPC its
        // loot (ITEM=<recipe>), which is what reaches the loot expansion.
        Assert.True(owner.TryExecuteCommand("ITEM", "t_named_reward", new CharConsole(owner)));
        var viaLoot = pack.Contents.Single();

        _out.WriteLine($"engine: {viaEngine!.BaseId:X4} '{viaEngine.Name}' x{viaEngine.Amount} hue={viaEngine.Hue}");
        _out.WriteLine($"loot:   {viaLoot.BaseId:X4} '{viaLoot.Name}' x{viaLoot.Amount} hue={viaLoot.Hue}");

        // The comparison PLAN-202 actually asks for: not "does each door work" but
        // "do the doors agree", because a script author writes one recipe and expects
        // one result whichever production runs it.
        Assert.Equal(viaEngine.BaseId, viaLoot.BaseId);
        Assert.Equal(viaEngine.Name, viaLoot.Name);
        Assert.Equal(viaEngine.Amount, viaLoot.Amount);
        Assert.Equal(viaEngine.Hue, viaLoot.Hue);
    }

    /// <summary>A console speaking for a character, which is what a verb issued from
    /// a @Create body has.</summary>
    private sealed class CharConsole(Character ch) : SphereNet.Core.Interfaces.ITextConsole
    {
        public void SysMessage(string message) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => ch.Name;
        public SphereNet.Core.Interfaces.IScriptObj? GetSourceChar() => ch;
    }
}
