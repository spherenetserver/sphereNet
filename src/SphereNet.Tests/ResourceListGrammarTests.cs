using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The grammar every resource list is written in (port plan İŞ-68 / PLAN-304).
///
/// Upstream states it in one line - <c>"Can be either order.: Name Qty or Qty Name"</c>
/// (CResourceQty.cpp:57) - and a bare name means one (:88). This engine had four
/// readers of that grammar and each accepted one half of it, which is the quiet kind
/// of wrong: the recipe still loads, the item is still craftable, and a requirement
/// has vanished.
///
/// Both halves are measured against the shipped pack, because that is where the cost
/// is: <c>SKILLMAKE=Inscription 10.0,1 i_pen_and_ink,Magery 10.0</c> lost the pen, and
/// 257 <c>RESOURCES=i_spellbook</c>-shaped lines lost their material outright.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResourceListGrammarTests
{
    private readonly ITestOutputHelper _out;
    public ResourceListGrammarTests(ITestOutputHelper output) => _out = output;

    // ---- the grammar itself ---------------------------------------------

    [Fact]
    public void AQuantityMayLeadOrTrailTheName()
    {
        var lead = ResourceQtyList.ParseEntry("10 i_ingot_iron");
        var trail = ResourceQtyList.ParseEntry("i_ingot_iron 10");

        _out.WriteLine($"leading: {lead.Name} x{lead.Quantity}; trailing: {trail.Name} x{trail.Quantity}");

        // One grammar, two spellings of the same requirement. A reader that knows only
        // one of them does not fail loudly - it reads the other as something else.
        Assert.Equal("i_ingot_iron", lead.Name);
        Assert.Equal(10, lead.Quantity);
        Assert.Equal("i_ingot_iron", trail.Name);
        Assert.Equal(10, trail.Quantity);
    }

    [Fact]
    public void ABareNameMeansOne()
    {
        var entry = ResourceQtyList.ParseEntry("i_spellbook");

        _out.WriteLine($"{entry.Name} x{entry.Quantity} (written: {entry.HasExplicitQuantity})");

        // CResourceQty.cpp:88 - no trailing quantity is a quantity of one, not a
        // quantity of none. Treating it as unreadable made the material free.
        Assert.Equal("i_spellbook", entry.Name);
        Assert.Equal(1, entry.Quantity);
        Assert.False(entry.HasExplicitQuantity);
    }

    [Fact]
    public void ASkillValueKeepsItsTenths()
    {
        var half = ResourceQtyList.ParseEntry("Blacksmithing 50.0");
        var bare = ResourceQtyList.ParseEntry("BLACKSMITHING 76");

        _out.WriteLine($"50.0 -> {half.Quantity}; 76 -> {bare.Quantity}");

        // The reference's decimal path IGNORES the dot (CExpression.cpp:745), so "50.0"
        // is five hundred - the same scale a skill is stored on. The corollary is the
        // one that bites: a bare "76" is 7.6, not 76.0. Reading it as 76.0 raised a
        // colored-armor recipe's requirement tenfold.
        Assert.Equal(500, half.Quantity);
        Assert.Equal(76, bare.Quantity);
    }

    [Fact]
    public void ALeadingZeroIsStillABaseMarker()
    {
        // Sphere's own numeric rule, which the resource list inherits: 010 is sixteen.
        Assert.Equal(16, ResourceQtyList.ParseEntry("010 i_gold").Quantity);
        Assert.Equal(16, ResourceQtyList.ParseEntry("0x10 i_gold").Quantity);

        // ...but only when no dot follows, or 0.15 would read as 0x15.
        Assert.Equal(15, ResourceQtyList.ParseEntry("0.15 mr_ore").Quantity);
    }

    [Fact]
    public void TheListIsSplitOnCommasAndEmptyEntriesAreDropped()
    {
        var list = ResourceQtyList.Parse("Inscription 10.0,1 i_pen_and_ink,,Magery 10.0");

        _out.WriteLine(string.Join(" | ", list.ConvertAll(e => $"{e.Name}x{e.Quantity}")));

        Assert.Equal(3, list.Count);
        Assert.Equal("Inscription", list[0].Name);
        Assert.Equal("i_pen_and_ink", list[1].Name);
        Assert.Equal("Magery", list[2].Name);
    }

    // ---- what the misreading cost in the shipped pack --------------------

    private const string ScribeScript = """
        [TYPEDEFS]
        t_spellbook=41

        [ITEMDEF i_pen_and_ink]
        ID=0FBF
        NAME=pen and ink

        [ITEMDEF i_blank_scroll]
        ID=0E34
        NAME=blank scroll

        [ITEMDEF i_scroll_create_food]
        ID=01F2F
        NAME=create food scroll
        RESOURCES=1 i_blank_scroll
        SKILLMAKE=Inscription 10.0,1 i_pen_and_ink,Magery 10.0

        [ITEMDEF i_spellbook]
        ID=0EFA
        NAME=spellbook
        TYPE=t_spellbook

        [ITEMDEF i_spellbook_full]
        ID=0EFB
        NAME=full spellbook
        RESOURCES=i_spellbook
        SKILLMAKE=Inscription 10.0
        """;

    [Fact]
    public void ARecipeStillRequiresThePenTheListNames()
    {
        var (engine, scribe, pack) = Scribe();
        var recipe = engine.TryGetRecipe(ResolveDef("i_scroll_create_food"));
        Assert.NotNull(recipe);

        _out.WriteLine($"required items: {string.Join(", ", recipe!.RequiredItemIds)}");
        _out.WriteLine($"skills: {string.Join(", ", recipe.SkillRequirements)}");

        // "1 i_pen_and_ink" is the quantity-first spelling. Read as "name then value" it
        // became skill number 1 at level zero - a requirement that is always satisfied -
        // and the pen disappeared from the recipe entirely.
        Assert.Contains(recipe.RequiredItemIds, r => r.ItemId == 0x0FBF);
        Assert.False(engine.CanCraft(scribe, recipe, checkWorkSite: false),
            "a scribe with no pen should not be able to make the scroll");

        var pen = scribe.Uid.IsValid ? NewItem(pack, 0x0FBF) : null;
        Assert.NotNull(pen);
        Assert.True(engine.CanCraft(scribe, recipe, checkWorkSite: false),
            "with the pen in the pack the recipe should pass");
    }

    [Fact]
    public void ABareNamedMaterialIsStillConsumed()
    {
        var (engine, scribe, pack) = Scribe();
        var recipe = engine.TryGetRecipe(ResolveDef("i_spellbook_full"));
        Assert.NotNull(recipe);

        var resource = Assert.Single(recipe!.Resources);
        _out.WriteLine($"resource: id=0x{resource.ItemId:X4} amount={resource.Amount}");

        // RESOURCES=i_spellbook - no quantity written, so one. Demanding a written
        // quantity dropped the entry and handed out a free spellbook; 257 lines in the
        // shipped pack are written this way.
        Assert.Equal((ushort)0x0EFA, resource.ItemId);
        Assert.Equal(1, resource.Amount);
        Assert.False(engine.CanCraft(scribe, recipe, checkWorkSite: false));

        NewItem(pack, 0x0EFA);
        Assert.True(engine.CanCraft(scribe, recipe, checkWorkSite: false));
    }

    [Fact]
    public void ThePropertyReadAgreesWithTheParser()
    {
        var (_, _, pack) = Scribe();
        var scroll = NewItem(pack, 0x01F2F);
        scroll.SetTag("ITEMDEF", "i_scroll_create_food");

        Assert.True(scroll.TryGetProperty("SKILLMAKE.2.KEY", out string key));
        Assert.True(scroll.TryGetProperty("SKILLMAKE.2.VAL", out string val));
        _out.WriteLine($"SKILLMAKE.2 -> key={key} val={val}");

        // <ITEM.SKILLMAKE.n.KEY> reads the same list, so it has to read it the same
        // way: a script that asked for entry 2 used to get "1 i_pen_and_ink" back as
        // the key, quantity and all.
        Assert.Equal("i_pen_and_ink", key);
        Assert.Equal("1", val);
    }

    // ---- fixture ---------------------------------------------------------

    private static int ResolveDef(string defname) =>
        DefinitionLoader.StaticResources!.ResolveDefName(defname).Index;

    private static Item NewItem(Item pack, ushort id)
    {
        var world = Item.ResolveWorld!.Invoke();
        var it = world.CreateItem();
        it.BaseId = id;
        pack.AddItem(it);
        return it;
    }

    private (CraftingEngine Engine, Character Scribe, Item Pack) Scribe()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_reslist_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, ScribeScript);
        try
        {
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(path) ?? ""
            };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 1024, 1024);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            var engine = new CraftingEngine(world);
            engine.LoadRecipesFromDefs(resources);

            var scribe = world.CreateCharacter();
            scribe.IsPlayer = true;
            scribe.SetSkill(SkillType.Inscription, 1000);
            scribe.SetSkill(SkillType.Magery, 1000);
            world.PlaceCharacter(scribe, new Point3D(100, 100, 0, 0));

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            scribe.Equip(pack, Layer.Pack);

            // Everything the scroll needs EXCEPT the entry under test.
            NewItem(pack, 0x0E34);

            return (engine, scribe, pack);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
